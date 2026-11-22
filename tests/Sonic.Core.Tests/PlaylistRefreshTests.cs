using Microsoft.Extensions.DependencyInjection;
using Sonic.Core.Abstractions;
using Sonic.Core.Data;
using Sonic.Core.Models;
using Sonic.Core.Repositories;
using Sonic.Core.Services;
using Xunit;

namespace Sonic.Core.Tests;

/// <summary>
/// Refreshing a Spotify playlist must keep the ids of entries that are still there (queued downloads point at them),
/// a failed YouTube search isn't "not found", and a library scan keeps tracks on offline drives.
/// </summary>
public sealed class PlaylistRefreshTests : IAsyncLifetime
{
    private readonly string _root = Directory.CreateTempSubdirectory("sonic-refresh").FullName;
    private readonly FailingYtDlp _ytDlp = new();
    private ServiceProvider _sp = null!;

    public async Task InitializeAsync()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSonicCore("Sonic-Tests/1.0");
        services.AddSingleton<IAppPaths>(new AppPaths(Path.Combine(_root, "data")));
        services.AddSingleton<IYtDlpService>(_ytDlp);
        _sp = services.BuildServiceProvider();
        var settings = _sp.GetRequiredService<ISettingsService>();
        await settings.LoadAsync();
        await _sp.GetRequiredService<IDatabaseInitializer>().InitializeAsync();
        var music = Path.Combine(_root, "music");
        Directory.CreateDirectory(music);
        await settings.UpdateAsync(s =>
        {
            s.DownloadDirectory = music;
            s.MusicFolders = [];
            s.WatchFolders = false;
        });
    }

    public async Task DisposeAsync()
    {
        await _sp.GetRequiredService<IDownloadQueueService>().ShutdownAsync();
        _sp.GetRequiredService<ILibraryService>().Dispose();
        await _sp.DisposeAsync();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_root, true); } catch (IOException) { }
    }

    private static SpotifyTrack T(string id) => new() { Id = id, Name = "Song " + id, Artists = ["Artist " + id], DurationSeconds = 200 };

    private static SpotifyCollection Liked(params string[] ids) => new()
    {
        Id = "liked",
        Name = "Liked Songs",
        ExternalUrl = LikedSongsSyncService.CollectionUrl,
        Tracks = ids.Select(T).ToList(),
    };

    [Fact]
    public async Task Refresh_keeps_entry_ids_so_queued_songs_are_not_queued_again()
    {
        var playlists = _sp.GetRequiredService<IPlaylistService>();
        var queue = _sp.GetRequiredService<IDownloadQueueService>();
        await queue.InitializeAsync();
        // Nothing actually runs: the queue is on hold, like during game mode.
        await queue.HoldAsync("test");

        var first = await playlists.ImportSpotifyAsync(Liked("a", "b", "c"));
        var b = first.Entries.Single(e => e.ExternalId == "b");
        await queue.EnqueueAsync(new DownloadRequest { Url = "https://www.youtube.com/watch?v=bbbbbbbbbbb", PlaylistEntryId = b.Id });

        // A new like on top, "c" unliked.
        var second = await playlists.ImportSpotifyAsync(Liked("new", "a", "b"));
        Assert.Equal(["new", "a", "b"], second.Entries.Select(e => e.ExternalId));
        Assert.Equal(b.Id, second.Entries.Single(e => e.ExternalId == "b").Id);
        Assert.Equal(first.Entries.Single(e => e.ExternalId == "a").Id, second.Entries.Single(e => e.ExternalId == "a").Id);
        Assert.Equal([0, 1, 2], second.Entries.Select(e => e.Position));

        var plan = await _sp.GetRequiredService<IPlaylistDownloadService>().AnalyzeAsync(second.Id, Path.Combine(_root, "music"));
        Assert.Equal(["b"], plan.AlreadyQueued.Select(e => e.ExternalId));
        Assert.Equal(["new", "a"], plan.ToDownload.Select(e => e.ExternalId));
    }

    [Fact]
    public async Task Refresh_keeps_library_links_and_both_copies_of_a_repeated_song()
    {
        var playlists = _sp.GetRequiredService<IPlaylistService>();
        var tracks = _sp.GetRequiredService<ITrackRepository>();
        var track = await tracks.UpsertAsync(new Track { FilePath = Path.Combine(_root, "music", "x.mp3"), Title = "Song x", NormalizedKey = "zzz|zzz" });

        var first = await playlists.ImportSpotifyAsync(Liked("x", "y", "x"));
        await playlists.LinkEntryAsync(first.Entries[0].Id, track.Id);

        var second = await playlists.ImportSpotifyAsync(Liked("x", "x", "y"));
        Assert.Equal(3, second.Entries.Count);
        Assert.Equal(first.Entries.Where(e => e.ExternalId == "x").Select(e => e.Id).Order(),
            second.Entries.Where(e => e.ExternalId == "x").Select(e => e.Id).Order());
        Assert.Equal(track.Id, second.Entries.Single(e => e.Id == first.Entries[0].Id).TrackId);
    }

    [Fact]
    public async Task Failed_search_is_reported_separately_from_not_found()
    {
        var playlists = _sp.GetRequiredService<IPlaylistService>();
        var service = _sp.GetRequiredService<IPlaylistDownloadService>();
        var playlist = await playlists.ImportSpotifyAsync(Liked("a", "b"));
        var plan = new PlaylistDownloadPlan { PlaylistId = playlist.Id, Folder = Path.Combine(_root, "music") };
        plan.ToDownload.AddRange(playlist.Entries);
        _ytDlp.FailFor.Add("Artist a - Song a audio");

        var result = await service.QueueMissingAsync(plan, new PlaylistDownloadOptions(plan.Folder, AudioFormat.Mp3, AudioQuality.Best, "{artist} - {title}"));

        Assert.Equal(0, result.Queued);
        Assert.Equal(["a"], result.Failed.Select(e => e.ExternalId));
        Assert.Equal(["b"], result.NotFound.Select(e => e.ExternalId));
    }

    [Fact]
    public async Task Numbered_download_queues_in_playlist_order_and_names_by_position()
    {
        var playlists = _sp.GetRequiredService<IPlaylistService>();
        var queue = _sp.GetRequiredService<IDownloadQueueService>();
        await queue.InitializeAsync();
        await queue.HoldAsync("test");
        var playlist = await playlists.ImportSpotifyAsync(Liked("a", "b", "c", "d"));

        // "b" is already on disk; the first song's search is the slowest, so a naive queue would put it last.
        _ytDlp.Found["Artist a - Song a audio"] = 300;
        _ytDlp.Found["Artist c - Song c audio"] = 100;
        _ytDlp.Found["Artist d - Song d audio"] = 0;
        var plan = new PlaylistDownloadPlan { PlaylistId = playlist.Id, Folder = Path.Combine(_root, "music") };
        foreach (var (e, i) in playlist.Entries.Select((e, i) => (e, i))) plan.Numbers[e.Id] = i + 1;
        plan.ToDownload.AddRange(playlist.Entries.Where(e => e.ExternalId != "b").Reverse());

        var result = await _sp.GetRequiredService<IPlaylistDownloadService>().QueueMissingAsync(plan,
            new PlaylistDownloadOptions(plan.Folder, AudioFormat.Mp3, AudioQuality.Best, "{artist} - {title}", NumberByPosition: true));

        Assert.Equal(3, result.Queued);
        Assert.Equal(["01 - {artist} - {title}", "03 - {artist} - {title}", "04 - {artist} - {title}"],
            queue.Items.Select(i => i.FilenameTemplate));
    }

    [Theory]
    [InlineData("{artist} - {title}", 7, 2, "07 - {artist} - {title}")]
    [InlineData("{album}/{title}", 12, 3, "{album}/012 - {title}")]
    [InlineData(@"{artist}\{album}\{title}", 1, 2, @"{artist}\{album}\01 - {title}")]
    public void Number_prefix_goes_on_the_file_name(string template, int number, int digits, string expected) =>
        Assert.Equal(expected, Sonic.Core.Helpers.FileNameHelper.WithNumberPrefix(template, number, digits));

    [Fact]
    public async Task Scan_keeps_tracks_on_offline_drives_and_missing_music_folders()
    {
        var settings = _sp.GetRequiredService<ISettingsService>();
        var tracks = _sp.GetRequiredService<ITrackRepository>();
        var offlineFolder = Path.Combine(_root, "nas-share");
        await settings.UpdateAsync(s => s.MusicFolders = [offlineFolder]);

        var letter = "ZYXWVUTSRQPONMLKJIHG".First(c => !Directory.Exists($"{c}:\\"));
        var deleted = Path.Combine(_root, "music", "deleted.mp3");
        var onUnpluggedDrive = $"{letter}:\\Music\\song.mp3";
        var inOfflineFolder = Path.Combine(offlineFolder, "song.mp3");
        foreach (var path in new[] { deleted, onUnpluggedDrive, inOfflineFolder })
            await tracks.UpsertAsync(new Track { FilePath = path, Title = Path.GetFileName(path), NormalizedKey = "|" + path });

        var result = await _sp.GetRequiredService<ILibraryService>().ScanAsync(null);

        Assert.Equal(1, result.Removed);
        var left = (await tracks.GetAllAsync()).Select(t => t.FilePath).ToList();
        Assert.DoesNotContain(deleted, left);
        Assert.Contains(onUnpluggedDrive, left);
        Assert.Contains(inOfflineFolder, left);
    }

    /// <summary>Search fails for the queries in <see cref="FailFor"/> and finds nothing for the rest.</summary>
    private sealed class FailingYtDlp : IYtDlpService
    {
        public HashSet<string> FailFor { get; } = [];
        /// <summary>Queries that find a video, with how long the search takes (ms).</summary>
        public Dictionary<string, int> Found { get; } = [];

        public async Task<IReadOnlyList<MediaInfo>> SearchAsync(string query, int maxResults, CancellationToken cancellationToken = default)
        {
            if (FailFor.Contains(query)) throw new YtDlpException("A network error occurred.", "ERROR: Unable to download webpage: timed out", true);
            if (!Found.TryGetValue(query, out var delay)) return [];
            await Task.Delay(delay, cancellationToken);
            var id = query.GetHashCode().ToString("x8");
            return [new MediaInfo { Id = id, Url = $"https://www.youtube.com/watch?v={id}", Title = query, DurationSeconds = 200 }];
        }

        public Task<MediaInfo> AnalyzeAsync(string url, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<YtDlpDownloadResult> DownloadAsync(YtDlpDownloadRequest request, IProgress<YtDlpProgress> progress, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<FeedPage> GetFeedAsync(string feed, int start, int count, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<string> GetStreamUrlAsync(string url, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<MediaInfo?> ReadInfoJsonAsync(string path, CancellationToken cancellationToken = default) => Task.FromResult<MediaInfo?>(null);
    }
}
