using System.Collections.Concurrent;
using System.Security.Cryptography;
using Microsoft.Extensions.Logging;
using Sonic.Core.Abstractions;
using Sonic.Core.Helpers;
using Sonic.Core.Models;
using Sonic.Core.Repositories;

namespace Sonic.Core.Services;

public sealed class LibraryService : ILibraryService
{
    private const int BatchSize = 100;
    private static readonly TimeSpan WatchDebounce = TimeSpan.FromSeconds(3);

    private readonly ITrackRepository _tracks;
    private readonly IMetadataService _metadata;
    private readonly ISettingsService _settings;
    private readonly IAppPaths _paths;
    private readonly ILogger<LibraryService> _logger;
    private readonly SemaphoreSlim _scanGate = new(1, 1);
    private readonly List<FileSystemWatcher> _watchers = [];
    private readonly ConcurrentDictionary<string, byte> _pendingPaths = new(StringComparer.OrdinalIgnoreCase);
    private readonly Timer _watchTimer;
    private bool _isScanning;
    private bool _disposed;

    public LibraryService(
        ITrackRepository tracks,
        IMetadataService metadata,
        ISettingsService settings,
        IAppPaths paths,
        ILogger<LibraryService> logger)
    {
        _tracks = tracks;
        _metadata = metadata;
        _settings = settings;
        _paths = paths;
        _logger = logger;
        _watchTimer = new Timer(_ => _ = ProcessPendingAsync(), null, Timeout.Infinite, Timeout.Infinite);
    }

    public event EventHandler<LibraryChangedEventArgs>? LibraryChanged;
    public event EventHandler<bool>? ScanningChanged;

    public bool IsScanning => _isScanning;

    public async Task<IReadOnlyList<Track>> GetTracksAsync(CancellationToken cancellationToken = default) =>
        await _tracks.GetAllAsync(cancellationToken).ConfigureAwait(false);

    public Task<Track?> GetTrackAsync(int id, CancellationToken cancellationToken = default) => _tracks.GetAsync(id, cancellationToken);

    public async Task<IReadOnlyList<Track>> GetRecentlyAddedAsync(int count, CancellationToken cancellationToken = default) =>
        await _tracks.GetRecentAsync(count, cancellationToken).ConfigureAwait(false);

    private IEnumerable<string> GetRoots()
    {
        var s = _settings.Current;
        var roots = s.MusicFolders.Append(s.DownloadDirectory)
            .Where(Directory.Exists)
            .Select(p => Path.TrimEndingDirectorySeparator(Path.GetFullPath(p)))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        // Drop roots nested inside other roots to avoid enumerating files twice.
        return roots.Where(r => !roots.Any(o => o != r && r.StartsWith(o + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)));
    }

    public async Task<ScanResult> ScanAsync(IProgress<ScanProgress>? progress, CancellationToken cancellationToken = default)
    {
        await _scanGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        SetScanning(true);
        int added = 0, updated = 0, removed = 0, failed = 0;
        try
        {
            var roots = GetRoots().ToList();
            _logger.LogInformation("Scanning library roots: {Roots}", string.Join("; ", roots));

            var files = await Task.Run(() => roots.SelectMany(EnumerateAudioFiles).Distinct(StringComparer.OrdinalIgnoreCase).ToList(), cancellationToken)
                .ConfigureAwait(false);
            var index = await _tracks.GetFileIndexAsync(cancellationToken).ConfigureAwait(false);
            var batch = new List<Track>(BatchSize);
            var processed = 0;

            foreach (var file in files)
            {
                cancellationToken.ThrowIfCancellationRequested();
                processed++;
                if (processed % 25 == 0 || processed == files.Count) progress?.Report(new ScanProgress(processed, files.Count, file));

                try
                {
                    var info = new FileInfo(file);
                    if (index.TryGetValue(file, out var existing) &&
                        existing.Size == info.Length &&
                        Math.Abs((existing.Modified - info.LastWriteTimeUtc).TotalSeconds) < 2)
                    {
                        continue;
                    }

                    var track = await Task.Run(() => BuildTrack(file, null), cancellationToken).ConfigureAwait(false);
                    batch.Add(track);
                    if (index.ContainsKey(file)) updated++;
                    else added++;
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    failed++;
                    _logger.LogWarning(ex, "Could not read {File}", file);
                }

                if (batch.Count >= BatchSize)
                {
                    await _tracks.UpsertManyAsync(batch, cancellationToken).ConfigureAwait(false);
                    batch.Clear();
                }
            }

            await _tracks.UpsertManyAsync(batch, cancellationToken).ConfigureAwait(false);

            // Drop entries whose files have disappeared, but not ones on a drive, share or music folder that is
            // offline right now (unplugged USB disk, sleeping NAS): they come back, with favorites and playlist links.
            var unavailable = UnavailableLocations();
            var rootAvailable = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
            var missing = new List<int>();
            var offline = 0;
            foreach (var (path, entry) in index)
            {
                // Checked first: File.Exists on an unreachable share can take seconds per file.
                if (!IsLocationAvailable(path, unavailable, rootAvailable)) offline++;
                else if (!File.Exists(path)) missing.Add(entry.Id);
            }

            if (offline > 0) _logger.LogInformation("Skipped {Count} tracks on drives or folders that are offline", offline);
            await _tracks.DeleteAsync(missing, cancellationToken).ConfigureAwait(false);
            removed = missing.Count;

            _logger.LogInformation("Scan finished: {Added} added, {Updated} updated, {Removed} removed, {Failed} failed", added, updated, removed, failed);
            return new ScanResult(added, updated, removed, failed);
        }
        finally
        {
            SetScanning(false);
            _scanGate.Release();
            if (added + updated + removed > 0) LibraryChanged?.Invoke(this, new LibraryChangedEventArgs());
        }
    }

    /// <summary>Configured music folders (and the download folder) that don't exist right now.</summary>
    private List<string> UnavailableLocations()
    {
        var s = _settings.Current;
        return s.MusicFolders.Append(s.DownloadDirectory)
            .Where(f => !string.IsNullOrWhiteSpace(f) && !Directory.Exists(f))
            .Select(f => Path.TrimEndingDirectorySeparator(Path.GetFullPath(f)) + Path.DirectorySeparatorChar)
            .ToList();
    }

    /// <summary>
    /// False when the file's drive or share root is unreachable, or it lies in a configured folder that's missing:
    /// then a missing file means "offline", not "deleted".
    /// </summary>
    internal static bool IsLocationAvailable(string filePath, IReadOnlyList<string> unavailableFolders, Dictionary<string, bool>? rootCache = null)
    {
        if (unavailableFolders.Any(f => filePath.StartsWith(f, StringComparison.OrdinalIgnoreCase))) return false;
        string? root;
        try
        {
            root = Path.GetPathRoot(filePath);
        }
        catch (ArgumentException)
        {
            return false;
        }

        if (string.IsNullOrEmpty(root)) return false;
        if (rootCache is not null && rootCache.TryGetValue(root, out var known)) return known;
        var available = Directory.Exists(root);
        if (rootCache is not null) rootCache[root] = available;
        return available;
    }

    private IEnumerable<string> EnumerateAudioFiles(string root)
    {
        var options = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = true,
            AttributesToSkip = FileAttributes.System | FileAttributes.Hidden,
        };
        try
        {
            return Directory.EnumerateFiles(root, "*", options).Where(FileNameHelper.IsAudioFile).ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Could not enumerate {Root}", root);
            return [];
        }
    }

    public async Task<Track?> ImportFileAsync(string path, string? sourceUrl = null, CancellationToken cancellationToken = default)
    {
        if (!File.Exists(path) || !FileNameHelper.IsAudioFile(path)) return null;
        var track = await Task.Run(() => BuildTrack(Path.GetFullPath(path), sourceUrl), cancellationToken).ConfigureAwait(false);
        await _tracks.UpsertAsync(track, cancellationToken).ConfigureAwait(false);
        LibraryChanged?.Invoke(this, new LibraryChangedEventArgs([track.Id]));
        return track;
    }

    public async Task<Track?> FindByKeyAsync(string? artist, string? title, CancellationToken cancellationToken = default)
    {
        var key = TitleParser.NormalizeKey(artist, title);
        if (key.StartsWith('|') || key.EndsWith('|')) return null;
        var matches = await _tracks.GetByKeyAsync(key, cancellationToken).ConfigureAwait(false);
        return matches.FirstOrDefault(t => File.Exists(t.FilePath));
    }

    public async Task<IReadOnlyList<DuplicateGroup>> FindDuplicatesAsync(CancellationToken cancellationToken = default)
    {
        var all = await _tracks.GetAllAsync(cancellationToken).ConfigureAwait(false);
        var groups = new List<DuplicateGroup>();
        foreach (var g in all.Where(t => !t.NormalizedKey.StartsWith('|') && !t.NormalizedKey.EndsWith('|')).GroupBy(t => t.NormalizedKey))
        {
            if (g.Count() < 2) continue;
            // Same artist/title but very different lengths (e.g. remix vs. radio edit) are not duplicates.
            var cluster = new List<Track>();
            foreach (var t in g.OrderBy(t => t.DurationSeconds))
            {
                if (cluster.Count > 0 && t.DurationSeconds - cluster[0].DurationSeconds > 6)
                {
                    if (cluster.Count > 1) groups.Add(new DuplicateGroup(g.Key, cluster));
                    cluster = [];
                }

                cluster.Add(t);
            }

            if (cluster.Count > 1) groups.Add(new DuplicateGroup(g.Key, cluster));
        }

        return groups;
    }

    public async Task SetFavoriteAsync(int trackId, bool isFavorite, CancellationToken cancellationToken = default)
    {
        await _tracks.SetFavoriteAsync(trackId, isFavorite, cancellationToken).ConfigureAwait(false);
        LibraryChanged?.Invoke(this, new LibraryChangedEventArgs([trackId]));
    }

    public async Task<Track> UpdateMetadataAsync(int trackId, TrackMetadata metadata, bool removeArtwork, CancellationToken cancellationToken = default)
    {
        var track = await _tracks.GetAsync(trackId, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("The track is no longer in the library.");
        if (!File.Exists(track.FilePath)) throw new FileNotFoundException("The audio file no longer exists.", track.FilePath);

        await Task.Run(() => _metadata.Write(track.FilePath, metadata, overwriteAll: true, removeArtwork), cancellationToken).ConfigureAwait(false);
        var refreshed = await Task.Run(() => BuildTrack(track.FilePath, track.SourceUrl), cancellationToken).ConfigureAwait(false);
        await _tracks.UpsertAsync(refreshed, cancellationToken).ConfigureAwait(false);
        LibraryChanged?.Invoke(this, new LibraryChangedEventArgs([refreshed.Id]));
        return refreshed;
    }

    public async Task<Track> ApplyArtworkAsync(int trackId, byte[] artwork, string? album = null, uint? year = null, CancellationToken cancellationToken = default)
    {
        var track = await _tracks.GetAsync(trackId, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("The track is no longer in the library.");
        if (!File.Exists(track.FilePath)) throw new FileNotFoundException("The audio file no longer exists.", track.FilePath);

        var update = new TrackMetadata
        {
            Artwork = artwork,
            Album = string.IsNullOrWhiteSpace(track.Album) ? album : null,
            Year = track.Year is null or 0 ? year : null,
        };
        await Task.Run(() => _metadata.Write(track.FilePath, update, overwriteAll: false), cancellationToken).ConfigureAwait(false);
        var refreshed = await Task.Run(() => BuildTrack(track.FilePath, track.SourceUrl), cancellationToken).ConfigureAwait(false);
        await _tracks.UpsertAsync(refreshed, cancellationToken).ConfigureAwait(false);
        LibraryChanged?.Invoke(this, new LibraryChangedEventArgs([refreshed.Id]));
        return refreshed;
    }

    public async Task RemoveAsync(IReadOnlyCollection<int> trackIds, bool deleteFiles, CancellationToken cancellationToken = default)
    {
        var removedIds = new List<int>();
        var errors = new List<Exception>();
        foreach (var id in trackIds)
        {
            if (deleteFiles)
            {
                var track = await _tracks.GetAsync(id, cancellationToken).ConfigureAwait(false);
                if (track is not null && File.Exists(track.FilePath))
                {
                    try
                    {
                        await Task.Run(() => RecycleBin.SendFile(track.FilePath), cancellationToken).ConfigureAwait(false);
                        _logger.LogInformation("Moved {File} to the Recycle Bin", track.FilePath);
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or OperationCanceledException)
                    {
                        _logger.LogWarning(ex, "Could not delete {File}", track.FilePath);
                        errors.Add(new IOException($"Could not delete \"{Path.GetFileName(track.FilePath)}\": {ex.Message}", ex));
                        continue;
                    }
                }
            }

            removedIds.Add(id);
        }

        await _tracks.DeleteAsync(removedIds, cancellationToken).ConfigureAwait(false);
        if (removedIds.Count > 0) LibraryChanged?.Invoke(this, new LibraryChangedEventArgs());
        if (errors.Count > 0) throw new AggregateException("Some files could not be deleted.", errors);
    }

    public async Task RemoveFolderAsync(string folder, CancellationToken cancellationToken = default)
    {
        var prefix = Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder)) + Path.DirectorySeparatorChar;
        var stillCovered = GetRoots().Where(r => !string.Equals(r + Path.DirectorySeparatorChar, prefix, StringComparison.OrdinalIgnoreCase)).ToList();
        var all = await _tracks.GetAllAsync(cancellationToken).ConfigureAwait(false);
        var ids = all.Where(t => t.FilePath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
                                 && !stillCovered.Any(r => t.FilePath.StartsWith(r + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)))
            .Select(t => t.Id).ToList();
        await _tracks.DeleteAsync(ids, cancellationToken).ConfigureAwait(false);
        if (ids.Count > 0) LibraryChanged?.Invoke(this, new LibraryChangedEventArgs());
    }

    /// <summary>Reads a file into a <see cref="Track"/>, caching its artwork. Runs on a background thread.</summary>
    private Track BuildTrack(string path, string? sourceUrl)
    {
        var info = new FileInfo(path);
        var meta = _metadata.Read(path, includeArtwork: true);
        var title = string.IsNullOrWhiteSpace(meta.Title) ? Path.GetFileNameWithoutExtension(path) : meta.Title!;
        var artist = meta.Artist;

        // Untagged files named "Artist - Title.ext" still get sensible values.
        if (string.IsNullOrWhiteSpace(meta.Title) && string.IsNullOrWhiteSpace(artist) &&
            TitleParser.TrySplitArtistTitle(title, out var a, out var t))
        {
            artist = a;
            title = t;
        }

        return new Track
        {
            FilePath = path,
            Title = title,
            Artist = artist,
            Album = meta.Album,
            AlbumArtist = meta.AlbumArtist,
            Genre = meta.Genre,
            Comment = meta.Comment,
            TrackNumber = meta.TrackNumber,
            Year = meta.Year,
            DurationSeconds = meta.DurationSeconds,
            Bitrate = meta.Bitrate,
            SampleRate = meta.SampleRate,
            FileSize = info.Length,
            Extension = info.Extension.TrimStart('.').ToUpperInvariant(),
            FileModified = info.LastWriteTimeUtc,
            ArtworkPath = CacheArtwork(meta.Artwork, meta.ArtworkMimeType),
            SourceUrl = sourceUrl,
            NormalizedKey = TitleParser.NormalizeKey(artist, title),
            DateAdded = DateTime.UtcNow,
        };
    }

    private string? CacheArtwork(byte[]? data, string? mime)
    {
        if (data is not { Length: > 0 }) return null;
        try
        {
            var hash = Convert.ToHexString(SHA1.HashData(data)).ToLowerInvariant();
            var ext = mime?.Contains("png", StringComparison.OrdinalIgnoreCase) == true ? ".png" : ".jpg";
            var path = Path.Combine(_paths.ArtworkCacheDirectory, hash + ext);
            if (!File.Exists(path))
            {
                var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
                File.WriteAllBytes(temp, data);
                try { File.Move(temp, path, overwrite: false); }
                catch (IOException) { File.Delete(temp); } // Another thread cached the same image.
            }

            return path;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Could not cache artwork");
            return null;
        }
    }

    public void ConfigureWatchers()
    {
        lock (_watchers)
        {
            foreach (var w in _watchers) w.Dispose();
            _watchers.Clear();
            if (_disposed || !_settings.Current.WatchFolders) return;

            foreach (var root in GetRoots())
            {
                try
                {
                    var watcher = new FileSystemWatcher(root)
                    {
                        IncludeSubdirectories = true,
                        NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite | NotifyFilters.Size,
                        InternalBufferSize = 64 * 1024,
                    };
                    watcher.Created += OnFileEvent;
                    watcher.Changed += OnFileEvent;
                    watcher.Deleted += OnFileEvent;
                    watcher.Renamed += (_, e) =>
                    {
                        Enqueue(e.OldFullPath);
                        Enqueue(e.FullPath);
                    };
                    watcher.Error += (_, e) => _logger.LogWarning(e.GetException(), "Folder watcher error for {Root}", root);
                    watcher.EnableRaisingEvents = true;
                    _watchers.Add(watcher);
                }
                catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException)
                {
                    _logger.LogWarning(ex, "Could not watch {Root}", root);
                }
            }
        }
    }

    private void OnFileEvent(object sender, FileSystemEventArgs e) => Enqueue(e.FullPath);

    private void Enqueue(string path)
    {
        if (path.Contains(Path.DirectorySeparatorChar + "." , StringComparison.Ordinal)) return;
        _pendingPaths[path] = 0;
        _watchTimer.Change(WatchDebounce, Timeout.InfiniteTimeSpan);
    }

    private async Task ProcessPendingAsync()
    {
        if (_disposed) return;
        var paths = _pendingPaths.Keys.ToList();
        foreach (var p in paths) _pendingPaths.TryRemove(p, out _);
        if (paths.Count == 0) return;

        // Don't compete with a full scan; it will pick up the changes anyway.
        if (!await _scanGate.WaitAsync(0).ConfigureAwait(false))
        {
            foreach (var p in paths) _pendingPaths[p] = 0;
            _watchTimer.Change(WatchDebounce, Timeout.InfiniteTimeSpan);
            return;
        }

        var changed = false;
        try
        {
            var upserts = new List<Track>();
            var index = await _tracks.GetFileIndexAsync().ConfigureAwait(false);
            var toDelete = new List<int>();
            foreach (var path in paths)
            {
                if (File.Exists(path))
                {
                    if (!FileNameHelper.IsAudioFile(path)) continue;
                    try
                    {
                        upserts.Add(BuildTrack(path, null));
                    }
                    catch (Exception ex)
                    {
                        // The file may still be being written; a later change event will retry.
                        _logger.LogDebug(ex, "Watcher could not read {Path} yet", path);
                    }
                }
                else
                {
                    var prefix = path + Path.DirectorySeparatorChar;
                    toDelete.AddRange(index.Where(kv => string.Equals(kv.Key, path, StringComparison.OrdinalIgnoreCase)
                                                        || kv.Key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                        .Select(kv => kv.Value.Id));
                }
            }

            await _tracks.UpsertManyAsync(upserts).ConfigureAwait(false);
            await _tracks.DeleteAsync(toDelete).ConfigureAwait(false);
            changed = upserts.Count + toDelete.Count > 0;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Processing folder changes failed");
        }
        finally
        {
            _scanGate.Release();
        }

        if (changed) LibraryChanged?.Invoke(this, new LibraryChangedEventArgs());
    }

    private void SetScanning(bool value)
    {
        _isScanning = value;
        ScanningChanged?.Invoke(this, value);
    }

    public void Dispose()
    {
        _disposed = true;
        lock (_watchers)
        {
            foreach (var w in _watchers) w.Dispose();
            _watchers.Clear();
        }

        _watchTimer.Dispose();
    }
}
