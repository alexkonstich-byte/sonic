using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Sonic.Core.Abstractions;
using Sonic.Core.Helpers;
using Sonic.Core.Models;

namespace Sonic.Core.Services;

/// <summary>An audio file found on disk, described by its tags (or its file name when untagged).</summary>
public sealed record LocalAudioFile(string Path, string? Artist, string? Title, double DurationSeconds);

/// <summary>A playlist entry that already exists as a file.</summary>
public sealed record LocalMatch(PlaylistEntry Entry, string FilePath);

/// <summary>What "Download all" will do for a playlist and a target folder.</summary>
public sealed class PlaylistDownloadPlan
{
    public required int PlaylistId { get; init; }
    public required string Folder { get; init; }
    public int ScannedFiles { get; init; }
    /// <summary>Already linked to a library file that exists.</summary>
    public List<PlaylistEntry> InLibrary { get; } = [];
    /// <summary>Found in the folder (or elsewhere in the library); will be linked instead of downloaded.</summary>
    public List<LocalMatch> FoundInFolder { get; } = [];
    /// <summary>Already waiting or downloading in the queue.</summary>
    public List<PlaylistEntry> AlreadyQueued { get; } = [];
    public List<PlaylistEntry> ToDownload { get; } = [];
}

public sealed record PlaylistDownloadOptions(string Folder, AudioFormat Format, AudioQuality Quality, string FilenameTemplate);

public sealed record PlaylistQueueResult(int Queued, IReadOnlyList<PlaylistEntry> NotFound);

/// <summary>
/// Matches playlist entries against audio files: by normalized "artist|title", then ignoring version suffixes
/// ("- Remastered 2011", "(Radio Edit)", "[Live]"), and by length when both sides know it.
/// </summary>
public sealed class LocalTrackIndex
{
    private const double ExactTolerance = 10;
    private const double LooseTolerance = 15;
    private readonly Dictionary<string, List<LocalAudioFile>> _exact = [];
    private readonly Dictionary<string, List<LocalAudioFile>> _loose = [];

    public LocalTrackIndex(IEnumerable<LocalAudioFile> files)
    {
        foreach (var f in files)
        {
            if (string.IsNullOrWhiteSpace(f.Title)) continue;
            Add(_exact, TitleParser.NormalizeKey(f.Artist, f.Title), f);
            Add(_loose, TitleParser.NormalizeKey(f.Artist, StripVersion(f.Title)), f);
        }
    }

    private static void Add(Dictionary<string, List<LocalAudioFile>> map, string key, LocalAudioFile f)
    {
        if (key.StartsWith('|') || key.EndsWith('|')) return; // needs both artist and title
        if (!map.TryGetValue(key, out var list)) map[key] = list = [];
        list.Add(f);
    }

    public LocalAudioFile? Find(string? artist, string title, double durationSeconds) =>
        Best(_exact, TitleParser.NormalizeKey(artist, title), durationSeconds, ExactTolerance)
        ?? Best(_loose, TitleParser.NormalizeKey(artist, StripVersion(title)), durationSeconds, LooseTolerance);

    private static LocalAudioFile? Best(Dictionary<string, List<LocalAudioFile>> map, string key, double duration, double tolerance)
    {
        if (!map.TryGetValue(key, out var candidates)) return null;
        return candidates
            .Where(c => duration <= 0 || c.DurationSeconds <= 0 || Math.Abs(c.DurationSeconds - duration) <= tolerance)
            .OrderBy(c => duration > 0 && c.DurationSeconds > 0 ? Math.Abs(c.DurationSeconds - duration) : 0)
            .FirstOrDefault();
    }

    /// <summary>"Song - Remastered 2011" / "Song (Radio Edit)" / "Song [Live]" → "Song".</summary>
    public static string StripVersion(string? title)
    {
        if (string.IsNullOrWhiteSpace(title)) return string.Empty;
        var t = Regex.Replace(title, @"\s*[\(\[][^\)\]]*[\)\]]", string.Empty);
        var dash = t.IndexOf(" - ", StringComparison.Ordinal);
        if (dash > 0) t = t[..dash];
        return t.Trim();
    }
}

public interface IPlaylistDownloadService
{
    /// <summary>Scans <paramref name="folder"/> (with subfolders) and sorts the playlist's songs into what exists and what to download.</summary>
    Task<PlaylistDownloadPlan> AnalyzeAsync(int playlistId, string folder, IProgress<string>? progress = null, CancellationToken cancellationToken = default);

    /// <summary>Adds songs found in the folder to the library and links them to their playlist entries.</summary>
    Task<int> LinkFoundAsync(PlaylistDownloadPlan plan, CancellationToken cancellationToken = default);

    /// <summary>Searches each missing song on YouTube (matching the length) and queues the best result.</summary>
    Task<PlaylistQueueResult> QueueMissingAsync(PlaylistDownloadPlan plan, PlaylistDownloadOptions options, IProgress<int>? searched = null, CancellationToken cancellationToken = default);
}

public sealed class PlaylistDownloadService(
    IPlaylistService playlists,
    ILibraryService library,
    IMetadataService metadata,
    IYtDlpService ytDlp,
    IDownloadQueueService queue,
    ILogger<PlaylistDownloadService> logger) : IPlaylistDownloadService
{
    private const int SearchParallelism = 3;
    private static readonly string[] UnwantedWords = ["karaoke", "cover", "instrumental", "8d", "slowed", "sped up", "nightcore", "reverb", "live"];

    public async Task<PlaylistDownloadPlan> AnalyzeAsync(int playlistId, string folder, IProgress<string>? progress = null, CancellationToken cancellationToken = default)
    {
        var playlist = await playlists.GetPlaylistAsync(playlistId, cancellationToken).ConfigureAwait(false)
                       ?? throw new InvalidOperationException("The playlist no longer exists.");

        progress?.Report("Scanning the folder…");
        var files = await Task.Run(() => ScanFolder(folder, progress, cancellationToken), cancellationToken).ConfigureAwait(false);
        var index = new LocalTrackIndex(files);
        // Songs already in the library (in any folder) count as downloaded too.
        var libraryTracks = await library.GetTracksAsync(cancellationToken).ConfigureAwait(false);
        var libraryIndex = new LocalTrackIndex(libraryTracks.Where(t => File.Exists(t.FilePath))
            .Select(t => new LocalAudioFile(t.FilePath, t.Artist, t.Title, t.DurationSeconds)));

        var queuedEntryIds = queue.Items
            .Where(i => i.PlaylistEntryId is not null && i.Status is DownloadStatus.Queued or DownloadStatus.Downloading or DownloadStatus.Converting or DownloadStatus.Tagging or DownloadStatus.Paused)
            .Select(i => i.PlaylistEntryId!.Value)
            .ToHashSet();

        var plan = new PlaylistDownloadPlan { PlaylistId = playlistId, Folder = folder, ScannedFiles = files.Count };
        foreach (var entry in playlist.Entries.OrderBy(e => e.Position))
        {
            if (entry.Track is { } track && File.Exists(track.FilePath)) plan.InLibrary.Add(entry);
            else if ((index.Find(entry.Artist, entry.Title, entry.DurationSeconds) ?? libraryIndex.Find(entry.Artist, entry.Title, entry.DurationSeconds)) is { } file)
                plan.FoundInFolder.Add(new LocalMatch(entry, file.Path));
            else if (queuedEntryIds.Contains(entry.Id)) plan.AlreadyQueued.Add(entry);
            else plan.ToDownload.Add(entry);
        }

        logger.LogInformation("Playlist {Id}: {Files} files scanned; {Lib} in library, {Found} found in folder, {Queued} queued, {Missing} to download",
            playlistId, files.Count, plan.InLibrary.Count, plan.FoundInFolder.Count, plan.AlreadyQueued.Count, plan.ToDownload.Count);
        return plan;
    }

    private List<LocalAudioFile> ScanFolder(string folder, IProgress<string>? progress, CancellationToken ct)
    {
        var result = new List<LocalAudioFile>();
        if (!Directory.Exists(folder)) return result;
        var options = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true };
        foreach (var path in Directory.EnumerateFiles(folder, "*", options).Where(FileNameHelper.IsAudioFile))
        {
            ct.ThrowIfCancellationRequested();
            string? artist = null, title = null;
            double duration = 0;
            try
            {
                var tags = metadata.Read(path, includeArtwork: false);
                artist = tags.Artist;
                title = tags.Title;
                duration = tags.DurationSeconds;
            }
            catch (Exception ex)
            {
                logger.LogDebug(ex, "Couldn't read tags of {Path}", path);
            }

            // Untagged files: "Artist - Title.mp3".
            if (string.IsNullOrWhiteSpace(artist) || string.IsNullOrWhiteSpace(title))
            {
                var name = Path.GetFileNameWithoutExtension(path);
                if (TitleParser.TrySplitArtistTitle(name, out var a, out var t))
                {
                    artist ??= a;
                    title = string.IsNullOrWhiteSpace(title) ? t : title;
                }
            }

            result.Add(new LocalAudioFile(path, artist, title, duration));
            if (result.Count % 50 == 0) progress?.Report($"Scanning the folder… {result.Count} files");
        }

        return result;
    }

    public async Task<int> LinkFoundAsync(PlaylistDownloadPlan plan, CancellationToken cancellationToken = default)
    {
        var linked = 0;
        foreach (var match in plan.FoundInFolder)
        {
            try
            {
                var track = await library.ImportFileAsync(match.FilePath, null, cancellationToken).ConfigureAwait(false);
                if (track is null) continue;
                await playlists.LinkEntryAsync(match.Entry.Id, track.Id, cancellationToken).ConfigureAwait(false);
                linked++;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Couldn't link {Path}", match.FilePath);
            }
        }

        return linked;
    }

    public async Task<PlaylistQueueResult> QueueMissingAsync(PlaylistDownloadPlan plan, PlaylistDownloadOptions options, IProgress<int>? searched = null, CancellationToken cancellationToken = default)
    {
        var notFound = new List<PlaylistEntry>();
        var queued = 0;
        var done = 0;
        using var gate = new SemaphoreSlim(SearchParallelism);
        await Task.WhenAll(plan.ToDownload.Select(async entry =>
        {
            await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                var query = string.IsNullOrWhiteSpace(entry.Artist) ? entry.Title : $"{entry.Artist} - {entry.Title}";
                var results = await ytDlp.SearchAsync(query + " audio", 5, cancellationToken).ConfigureAwait(false);
                if (PickBest(entry, results) is not { } best)
                {
                    lock (notFound) notFound.Add(entry);
                    return;
                }

                await queue.EnqueueAsync(new DownloadRequest
                {
                    Url = best.Url,
                    Kind = MediaKind.Audio,
                    Title = entry.Title,
                    Artist = entry.Artist,
                    ThumbnailUrl = entry.ArtworkUrl ?? best.ThumbnailUrl,
                    DurationSeconds = best.DurationSeconds,
                    PlaylistEntryId = entry.Id,
                    OutputDirectory = options.Folder,
                    FilenameTemplate = options.FilenameTemplate,
                    Format = options.Format,
                    Quality = options.Quality,
                    Hints = new MetadataHints
                    {
                        Title = entry.Title,
                        Artist = entry.Artist,
                        Album = entry.Album,
                        AlbumArtist = entry.AlbumArtist,
                        TrackNumber = entry.TrackNumber,
                        Year = entry.Year,
                        ArtworkUrl = entry.ArtworkUrl,
                    },
                }, cancellationToken).ConfigureAwait(false);
                Interlocked.Increment(ref queued);
            }
            catch (Exception ex) when (ex is YtDlpException or ArgumentException or InvalidOperationException)
            {
                logger.LogWarning(ex, "Couldn't queue {Artist} - {Title}", entry.Artist, entry.Title);
                lock (notFound) notFound.Add(entry);
            }
            finally
            {
                gate.Release();
                searched?.Report(Interlocked.Increment(ref done));
            }
        })).ConfigureAwait(false);

        return new PlaylistQueueResult(queued, notFound);
    }

    /// <summary>The closest-length result, skipping karaoke/cover/live versions the playlist didn't ask for.</summary>
    public static MediaInfo? PickBest(PlaylistEntry entry, IReadOnlyList<MediaInfo> results)
    {
        var wanted = (entry.Title + " " + entry.Album).ToLowerInvariant();
        var candidates = results
            .Where(r => !string.IsNullOrWhiteSpace(r.Url) && !r.IsLive)
            .Select(r => new
            {
                Info = r,
                Diff = entry.DurationSeconds > 0 && r.DurationSeconds is > 0 ? Math.Abs(r.DurationSeconds.Value - entry.DurationSeconds) : 0,
                Unwanted = UnwantedWords.Any(w => r.Title.Contains(w, StringComparison.OrdinalIgnoreCase) && !wanted.Contains(w, StringComparison.Ordinal)),
            })
            .Where(c => c.Diff <= 25)
            .OrderBy(c => c.Unwanted)
            .ThenBy(c => c.Diff)
            .ToList();
        return candidates.FirstOrDefault()?.Info;
    }
}
