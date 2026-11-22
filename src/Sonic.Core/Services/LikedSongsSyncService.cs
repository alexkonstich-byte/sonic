using Microsoft.Extensions.Logging;
using Sonic.Core.Abstractions;
using Sonic.Core.Models;

namespace Sonic.Core.Services;

/// <param name="Failed">Songs whose YouTube search failed (network, YouTube blocked); they're tried again next sync.</param>
public sealed record LikedSyncResult(int NewLikes, int Queued, int Linked, int NotFound, int PlaylistId, bool Unchanged, int Failed = 0);

public interface ILikedSongsSyncService
{
    bool IsRunning { get; }

    /// <summary>
    /// Mirrors the user's Spotify Liked Songs into the "Liked Songs" playlist, links songs that already exist on
    /// disk and queues the missing ones (only songs liked after <see cref="AppSettings.SpotifyLikedSyncFrom"/>).
    /// </summary>
    Task<LikedSyncResult> SyncAsync(bool force = false, CancellationToken cancellationToken = default);
}

public sealed class LikedSongsSyncService(
    ISpotifyService spotify,
    IPlaylistService playlists,
    IPlaylistDownloadService downloads,
    ISettingsService settings,
    ILogger<LikedSongsSyncService> logger) : ILikedSongsSyncService
{
    public const string CollectionId = "liked";
    public const string CollectionUrl = "https://open.spotify.com/collection/tracks";

    private readonly SemaphoreSlim _gate = new(1, 1);
    private (int Total, string? FirstId) _lastSeen;
    private int _playlistId;

    public bool IsRunning => _gate.CurrentCount == 0;

    public async Task<LikedSyncResult> SyncAsync(bool force = false, CancellationToken cancellationToken = default)
    {
        if (!await _gate.WaitAsync(0, cancellationToken).ConfigureAwait(false))
            throw new InvalidOperationException("Liked songs are already being synced.");
        try
        {
            var liked = await spotify.GetLikedSongsAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
            var seen = (liked.Total, liked.Tracks.FirstOrDefault()?.Id);
            // Nothing liked or unliked since the last sync in this session: skip the folder scan.
            if (!force && _playlistId > 0 && seen == _lastSeen)
                return new LikedSyncResult(0, 0, 0, 0, _playlistId, Unchanged: true);

            var s = settings.Current;
            var playlist = await playlists.ImportSpotifyAsync(new SpotifyCollection
            {
                Id = CollectionId,
                Name = "Liked Songs",
                Description = "Your Spotify Liked Songs, kept in sync by Sonic.",
                Owner = spotify.SignedInUser,
                ExternalUrl = CollectionUrl,
                Tracks = liked.Tracks,
            }, cancellationToken).ConfigureAwait(false);

            var folder = string.IsNullOrWhiteSpace(s.SpotifyLikedSyncFolder) ? s.DownloadDirectory : s.SpotifyLikedSyncFolder;
            Directory.CreateDirectory(folder);
            var plan = await downloads.AnalyzeAsync(playlist.Id, folder, cancellationToken: cancellationToken).ConfigureAwait(false);

            // Only songs liked after the chosen start, and not ones YouTube had no match for.
            var addedAt = liked.Tracks.Where(t => t.AddedAt is not null).ToDictionary(t => t.Id, t => t.AddedAt!.Value);
            var notFoundBefore = s.SpotifyLikedNotFound.ToHashSet();
            var from = s.SpotifyLikedSyncFrom;
            plan.ToDownload.RemoveAll(e =>
                e.ExternalId is null || notFoundBefore.Contains(e.ExternalId) ||
                (from is not null && (!addedAt.TryGetValue(e.ExternalId, out var at) || at < from.Value)));

            var linked = await downloads.LinkFoundAsync(plan, cancellationToken).ConfigureAwait(false);
            var options = new PlaylistDownloadOptions(folder, s.DefaultFormat, s.DefaultQuality, s.FilenameTemplate);
            var queued = await downloads.QueueMissingAsync(plan, options, cancellationToken: cancellationToken).ConfigureAwait(false);

            // Only real misses are remembered. Failed searches (network, YouTube blocked) are retried next time.
            var notFoundIds = queued.NotFound.Select(e => e.ExternalId).OfType<string>().ToList();
            await settings.UpdateAsync(x =>
            {
                x.SpotifyLikedLastSync = DateTime.UtcNow;
                foreach (var id in notFoundIds.Where(id => !x.SpotifyLikedNotFound.Contains(id))) x.SpotifyLikedNotFound.Add(id);
            }).ConfigureAwait(false);

            _playlistId = playlist.Id;
            logger.LogInformation("Liked songs: {Total} on Spotify, {Queued} queued, {Linked} linked, {NotFound} not found, {Failed} search failures",
                liked.Total, queued.Queued, linked, notFoundIds.Count, queued.Failed.Count);
            // A failed search must be retried even when the likes don't change.
            _lastSeen = queued.Failed.Count > 0 ? default : seen;
            return new LikedSyncResult(plan.ToDownload.Count, queued.Queued, linked, notFoundIds.Count, playlist.Id, Unchanged: false, queued.Failed.Count);
        }
        finally
        {
            _gate.Release();
        }
    }
}
