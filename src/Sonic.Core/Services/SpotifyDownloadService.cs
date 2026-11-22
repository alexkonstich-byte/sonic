using Microsoft.Extensions.Logging;
using Sonic.Core.Abstractions;
using Sonic.Core.Models;

namespace Sonic.Core.Services;

/// <summary>What a one-step Spotify download did.</summary>
public sealed record SpotifyDownloadResult(
    Playlist Playlist,
    int TotalSongs,
    int InLibrary,
    int Linked,
    int AlreadyQueued,
    int Queued,
    IReadOnlyList<PlaylistEntry> NotFound)
{
    /// <summary>Songs whose YouTube search failed (network, YouTube blocked); downloading again retries them.</summary>
    public IReadOnlyList<PlaylistEntry> Failed { get; init; } = [];
}

public interface ISpotifyDownloadService
{
    /// <summary>
    /// Loads a Spotify playlist or album, imports (or refreshes) it as a playlist, links songs that already exist in
    /// <see cref="PlaylistDownloadOptions.Folder"/> or the library, and queues the rest from YouTube.
    /// </summary>
    Task<SpotifyDownloadResult> DownloadAsync(string url, PlaylistDownloadOptions options, IProgress<string>? progress = null, CancellationToken cancellationToken = default);
}

/// <summary>Paste a Spotify link, get the songs: import + "Download all" in one step.</summary>
public sealed class SpotifyDownloadService(
    ISpotifyService spotify,
    IPlaylistService playlists,
    IPlaylistDownloadService playlistDownloads,
    ILogger<SpotifyDownloadService> logger) : ISpotifyDownloadService
{
    public async Task<SpotifyDownloadResult> DownloadAsync(string url, PlaylistDownloadOptions options, IProgress<string>? progress = null, CancellationToken cancellationToken = default)
    {
        if (!spotify.IsConfigured)
            throw new SpotifyException("Spotify isn't set up yet. Add your Client ID and Secret in Settings › Integrations to download Spotify playlists.");

        progress?.Report("Loading the playlist from Spotify…");
        var collection = await spotify.GetCollectionAsync(url, cancellationToken).ConfigureAwait(false);
        if (collection.Tracks.Count == 0)
            throw new SpotifyException($"\"{collection.Name}\" has no songs that can be downloaded.");

        cancellationToken.ThrowIfCancellationRequested();
        var playlist = await playlists.ImportSpotifyAsync(collection, cancellationToken).ConfigureAwait(false);

        var plan = await playlistDownloads.AnalyzeAsync(playlist.Id, options.Folder, progress, cancellationToken).ConfigureAwait(false);

        var linked = 0;
        if (plan.FoundInFolder.Count > 0)
        {
            progress?.Report("Linking songs you already have…");
            linked = await playlistDownloads.LinkFoundAsync(plan, cancellationToken).ConfigureAwait(false);
        }

        var queued = 0;
        IReadOnlyList<PlaylistEntry> notFound = [], failed = [];
        if (plan.ToDownload.Count > 0)
        {
            var total = plan.ToDownload.Count;
            progress?.Report($"Finding songs on YouTube… 0 of {total}");
            var searched = new Progress<int>(n => progress?.Report($"Finding songs on YouTube… {n} of {total}"));
            var result = await playlistDownloads.QueueMissingAsync(plan, options, searched, cancellationToken).ConfigureAwait(false);
            queued = result.Queued;
            notFound = result.NotFound;
            failed = result.Failed;
        }

        logger.LogInformation("Spotify download \"{Name}\": {Total} songs; {Lib} in library, {Linked} linked, {AlreadyQueued} already queued, {Queued} queued, {NotFound} not found",
            collection.Name, collection.Tracks.Count, plan.InLibrary.Count, linked, plan.AlreadyQueued.Count, queued, notFound.Count);

        return new SpotifyDownloadResult(playlist, collection.Tracks.Count, plan.InLibrary.Count, linked, plan.AlreadyQueued.Count, queued, notFound) { Failed = failed };
    }
}
