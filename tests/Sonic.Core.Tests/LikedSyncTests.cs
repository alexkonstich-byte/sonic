using Microsoft.Extensions.DependencyInjection;
using Sonic.Core.Abstractions;
using Sonic.Core.Data;
using Sonic.Core.Models;
using Sonic.Core.Services;
using Xunit;

namespace Sonic.Core.Tests;

public sealed class LikedSyncTests : IAsyncLifetime
{
    private readonly string _root = Directory.CreateTempSubdirectory("sonic-liked").FullName;
    private readonly FakeSpotify _spotify = new();
    private readonly FakeDownloads _downloads = new();
    private ServiceProvider _sp = null!;

    public async Task InitializeAsync()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSonicCore("Sonic-Tests/1.0");
        services.AddSingleton<IAppPaths>(new AppPaths(Path.Combine(_root, "data")));
        services.AddSingleton<ISpotifyService>(_spotify);
        services.AddSingleton<IPlaylistDownloadService>(sp => _downloads.With(sp.GetRequiredService<IPlaylistService>()));
        _sp = services.BuildServiceProvider();
        await _sp.GetRequiredService<ISettingsService>().LoadAsync();
        await _sp.GetRequiredService<IDatabaseInitializer>().InitializeAsync();
        await _sp.GetRequiredService<ISettingsService>().UpdateAsync(s => s.DownloadDirectory = Path.Combine(_root, "music"));
    }

    public async Task DisposeAsync()
    {
        await _sp.DisposeAsync();
        try { Directory.Delete(_root, true); } catch (IOException) { }
    }

    private static SpotifyTrack Track(string id, DateTime added) =>
        new SpotifyTrack { Id = id, Name = "Song " + id, Artists = ["Artist"], DurationSeconds = 200 }.WithAddedAt(added);

    [Fact]
    public async Task Queues_only_likes_after_the_start_and_skips_known_misses()
    {
        var settings = _sp.GetRequiredService<ISettingsService>();
        var start = new DateTime(2026, 11, 18, 12, 0, 0, DateTimeKind.Utc);
        await settings.UpdateAsync(s =>
        {
            s.SpotifyLikedSyncFrom = start;
            s.SpotifyLikedNotFound.Add("missing-before");
        });
        _spotify.Liked = new SpotifyLikedSongs(4,
        [
            Track("new1", start.AddHours(2)),
            Track("missing-before", start.AddHours(1)),
            Track("new2", start.AddMinutes(5)),
            Track("old", start.AddDays(-30)),
        ]);
        _downloads.NotFoundIds.Add("new2");

        var sync = _sp.GetRequiredService<ILikedSongsSyncService>();
        var result = await sync.SyncAsync();

        Assert.Equal(["new1", "new2"], _downloads.Queued.Select(e => e.ExternalId).Order());
        Assert.Equal(1, result.Queued);
        Assert.Equal(1, result.NotFound);
        Assert.Contains("new2", settings.Current.SpotifyLikedNotFound);

        // The playlist mirrors all four likes.
        var playlist = await _sp.GetRequiredService<IPlaylistService>().GetPlaylistAsync(result.PlaylistId);
        Assert.Equal("Liked Songs", playlist!.Name);
        Assert.Equal(4, playlist.Entries.Count);

        // Same likes again: nothing to do.
        _downloads.Queued.Clear();
        var again = await sync.SyncAsync();
        Assert.True(again.Unchanged);
        Assert.Empty(_downloads.Queued);
    }

    [Fact]
    public async Task All_mode_queues_every_liked_song()
    {
        _spotify.Liked = new SpotifyLikedSongs(2, [Track("x", DateTime.UtcNow), Track("y", DateTime.UtcNow.AddYears(-3))]);
        var result = await _sp.GetRequiredService<ILikedSongsSyncService>().SyncAsync();
        Assert.Equal(2, result.Queued);
    }

    [Fact]
    public async Task Failed_searches_are_not_remembered_and_are_retried()
    {
        var settings = _sp.GetRequiredService<ISettingsService>();
        _spotify.Liked = new SpotifyLikedSongs(1, [Track("flaky", DateTime.UtcNow)]);
        _downloads.FailedIds.Add("flaky");
        var sync = _sp.GetRequiredService<ILikedSongsSyncService>();

        var result = await sync.SyncAsync();
        Assert.Equal(1, result.Failed);
        Assert.Equal(0, result.NotFound);
        Assert.DoesNotContain("flaky", settings.Current.SpotifyLikedNotFound);

        // The connection is back; the likes didn't change, but the song is searched again.
        _downloads.FailedIds.Clear();
        _downloads.Queued.Clear();
        var again = await sync.SyncAsync();
        Assert.False(again.Unchanged);
        Assert.Equal(1, again.Queued);
    }

    private sealed class FakeSpotify : ISpotifyService
    {
        public SpotifyLikedSongs Liked { get; set; } = new(0, []);
        public bool IsConfigured => true;
        public bool IsSignedIn => true;
        public string? SignedInUser => "Tester";
        public Task VerifyCredentialsAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<SpotifyCollection> GetCollectionAsync(string urlOrUri, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<SpotifyTrack>> SearchTracksAsync(string query, int limit, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task SignInAsync(Func<Uri, Task> openBrowser, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task SignOutAsync() => Task.CompletedTask;
        public Task<SpotifyLikedSongs> GetLikedSongsAsync(int maxItems = 10000, CancellationToken cancellationToken = default) => Task.FromResult(Liked);
    }

    /// <summary>Treats every playlist entry as missing; records what gets queued.</summary>
    private sealed class FakeDownloads : IPlaylistDownloadService
    {
        private IPlaylistService _playlists = null!;
        public List<PlaylistEntry> Queued { get; } = [];
        public HashSet<string> NotFoundIds { get; } = [];
        public HashSet<string> FailedIds { get; } = [];

        public FakeDownloads With(IPlaylistService playlists)
        {
            _playlists = playlists;
            return this;
        }

        public async Task<PlaylistDownloadPlan> AnalyzeAsync(int playlistId, string folder, IProgress<string>? progress = null, CancellationToken cancellationToken = default)
        {
            var playlist = await _playlists.GetPlaylistAsync(playlistId, cancellationToken);
            var plan = new PlaylistDownloadPlan { PlaylistId = playlistId, Folder = folder };
            plan.ToDownload.AddRange(playlist!.Entries);
            return plan;
        }

        public Task<int> LinkFoundAsync(PlaylistDownloadPlan plan, CancellationToken cancellationToken = default) => Task.FromResult(0);

        public Task<PlaylistQueueResult> QueueMissingAsync(PlaylistDownloadPlan plan, PlaylistDownloadOptions options, IProgress<int>? searched = null, CancellationToken cancellationToken = default)
        {
            Queued.AddRange(plan.ToDownload);
            var notFound = plan.ToDownload.Where(e => NotFoundIds.Contains(e.ExternalId!)).ToList();
            var failed = plan.ToDownload.Where(e => FailedIds.Contains(e.ExternalId!)).ToList();
            return Task.FromResult(new PlaylistQueueResult(plan.ToDownload.Count - notFound.Count - failed.Count, notFound, failed));
        }
    }
}
