using Microsoft.Extensions.Logging.Abstractions;
using Sonic.Core.Abstractions;
using Sonic.Core.Models;
using Sonic.Core.Services;

namespace Sonic.Core.Tests;

public class SpotifyDownloadTests
{
    private static readonly PlaylistDownloadOptions Options = new(@"C:\Music", AudioFormat.Mp3, AudioQuality.High, "{artist} - {title}");

    [Fact]
    public async Task Imports_links_existing_and_queues_the_rest()
    {
        var spotify = new FakeSpotify(configured: true);
        var playlists = new FakePlaylists();
        var downloads = new FakePlaylistDownloads();
        var service = new SpotifyDownloadService(spotify, playlists, downloads, NullLogger<SpotifyDownloadService>.Instance);
        var messages = new List<string>();

        var r = await service.DownloadAsync("https://open.spotify.com/playlist/abc", Options, new SyncProgress(messages));

        Assert.Equal("https://open.spotify.com/playlist/abc", spotify.RequestedUrl);
        Assert.Same(spotify.Collection, playlists.Imported);
        Assert.Equal((42, @"C:\Music"), downloads.Analyzed);
        Assert.True(downloads.LinkCalled);
        Assert.Same(Options, downloads.QueuedWith);
        Assert.Equal(3, r.TotalSongs);
        Assert.Equal(1, r.Linked);
        Assert.Equal(1, r.Queued);
        Assert.Single(r.NotFound);
        Assert.Contains("Loading the playlist from Spotify…", messages);
        Assert.Contains(messages, m => m.StartsWith("Finding songs on YouTube…", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Not_configured_throws_a_helpful_message()
    {
        var service = new SpotifyDownloadService(new FakeSpotify(configured: false), new FakePlaylists(), new FakePlaylistDownloads(), NullLogger<SpotifyDownloadService>.Instance);
        var ex = await Assert.ThrowsAsync<SpotifyException>(() => service.DownloadAsync("https://open.spotify.com/playlist/abc", Options));
        Assert.Contains("Settings", ex.Message);
    }

    private sealed class SyncProgress(List<string> messages) : IProgress<string>
    {
        public void Report(string value)
        {
            lock (messages) messages.Add(value);
        }
    }

    private sealed class FakeSpotify(bool configured) : ISpotifyService
    {
        public string? RequestedUrl;

        public SpotifyCollection Collection { get; } = new()
        {
            Id = "abc",
            Name = "Mix",
            Tracks =
            [
                new SpotifyTrack { Id = "1", Name = "A", Artists = ["X"], DurationSeconds = 200 },
                new SpotifyTrack { Id = "2", Name = "B", Artists = ["Y"], DurationSeconds = 210 },
                new SpotifyTrack { Id = "3", Name = "C", Artists = ["Z"], DurationSeconds = 220 },
            ],
        };

        public bool IsConfigured => configured;
        public bool IsSignedIn => false;
        public string? SignedInUser => null;
        public Task SignInAsync(Func<Uri, Task> openBrowser, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task SignOutAsync() => Task.CompletedTask;
        public Task<SpotifyLikedSongs> GetLikedSongsAsync(int maxItems = 10000, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task VerifyCredentialsAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task<SpotifyCollection> GetCollectionAsync(string urlOrUri, CancellationToken cancellationToken = default)
        {
            RequestedUrl = urlOrUri;
            return Task.FromResult(Collection);
        }

        public Task<IReadOnlyList<SpotifyTrack>> SearchTracksAsync(string query, int limit, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<SpotifyTrack>>([]);
    }

    private sealed class FakePlaylists : IPlaylistService
    {
        public SpotifyCollection? Imported;
        public event EventHandler? PlaylistsChanged { add { } remove { } }

        public Task<Playlist> ImportSpotifyAsync(SpotifyCollection collection, CancellationToken cancellationToken = default)
        {
            Imported = collection;
            return Task.FromResult(new Playlist { Id = 42, Name = collection.Name, SourceType = PlaylistSource.Spotify });
        }

        public Task<IReadOnlyList<PlaylistSummary>> GetPlaylistsAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<Playlist?> GetPlaylistAsync(int id, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<Playlist> CreateAsync(string name, string? description = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task RenameAsync(int playlistId, string name, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task DeleteAsync(int playlistId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task AddTracksAsync(int playlistId, IReadOnlyCollection<int> trackIds, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<PlaylistEntry>> AddExternalEntriesAsync(int playlistId, IReadOnlyList<PlaylistEntry> entries, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task RemoveEntriesAsync(int playlistId, IReadOnlyCollection<int> entryIds, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task ReorderAsync(int playlistId, IReadOnlyList<int> orderedEntryIds, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<int> MatchWithLibraryAsync(int playlistId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task LinkEntryAsync(int entryId, int trackId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task ExportM3uAsync(int playlistId, string destinationPath, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private sealed class FakePlaylistDownloads : IPlaylistDownloadService
    {
        public (int, string)? Analyzed;
        public bool LinkCalled;
        public PlaylistDownloadOptions? QueuedWith;

        public Task<PlaylistDownloadPlan> AnalyzeAsync(int playlistId, string folder, IProgress<string>? progress = null, CancellationToken cancellationToken = default)
        {
            Analyzed = (playlistId, folder);
            var plan = new PlaylistDownloadPlan { PlaylistId = playlistId, Folder = folder, ScannedFiles = 10 };
            plan.FoundInFolder.Add(new LocalMatch(new PlaylistEntry { Id = 1, Title = "A", Artist = "X" }, @"C:\Music\X - A.mp3"));
            plan.ToDownload.Add(new PlaylistEntry { Id = 2, Title = "B", Artist = "Y" });
            plan.ToDownload.Add(new PlaylistEntry { Id = 3, Title = "C", Artist = "Z" });
            return Task.FromResult(plan);
        }

        public Task<int> LinkFoundAsync(PlaylistDownloadPlan plan, CancellationToken cancellationToken = default)
        {
            LinkCalled = true;
            return Task.FromResult(plan.FoundInFolder.Count);
        }

        public Task<PlaylistQueueResult> QueueMissingAsync(PlaylistDownloadPlan plan, PlaylistDownloadOptions options, IProgress<int>? searched = null, CancellationToken cancellationToken = default)
        {
            QueuedWith = options;
            searched?.Report(2);
            return Task.FromResult(new PlaylistQueueResult(1, [plan.ToDownload[1]]));
        }
    }
}
