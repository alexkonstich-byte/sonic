using System.Text;
using Microsoft.Extensions.Logging;
using Sonic.Core.Abstractions;
using Sonic.Core.Helpers;
using Sonic.Core.Models;
using Sonic.Core.Repositories;

namespace Sonic.Core.Services;

public sealed class PlaylistService(
    IPlaylistRepository playlists,
    ITrackRepository tracks,
    ILogger<PlaylistService> logger) : IPlaylistService
{
    public event EventHandler? PlaylistsChanged;

    public async Task<IReadOnlyList<PlaylistSummary>> GetPlaylistsAsync(CancellationToken cancellationToken = default) =>
        await playlists.GetSummariesAsync(cancellationToken).ConfigureAwait(false);

    public Task<Playlist?> GetPlaylistAsync(int id, CancellationToken cancellationToken = default) =>
        playlists.GetWithEntriesAsync(id, cancellationToken);

    public async Task<Playlist> CreateAsync(string name, string? description = null, CancellationToken cancellationToken = default)
    {
        var trimmed = ValidateName(name);
        var p = await playlists.AddAsync(new Playlist { Name = trimmed, Description = description, SourceType = PlaylistSource.Local }, cancellationToken)
            .ConfigureAwait(false);
        Raise();
        return p;
    }

    public async Task RenameAsync(int playlistId, string name, CancellationToken cancellationToken = default)
    {
        var trimmed = ValidateName(name);
        await playlists.UpdateAsync(playlistId, p => p.Name = trimmed, cancellationToken).ConfigureAwait(false);
        Raise();
    }

    public async Task DeleteAsync(int playlistId, CancellationToken cancellationToken = default)
    {
        await playlists.DeleteAsync(playlistId, cancellationToken).ConfigureAwait(false);
        Raise();
    }

    public async Task AddTracksAsync(int playlistId, IReadOnlyCollection<int> trackIds, CancellationToken cancellationToken = default)
    {
        var entries = new List<PlaylistEntry>();
        foreach (var id in trackIds)
        {
            var t = await tracks.GetAsync(id, cancellationToken).ConfigureAwait(false);
            if (t is null) continue;
            entries.Add(new PlaylistEntry
            {
                TrackId = t.Id,
                Title = t.Title,
                Artist = t.Artist,
                Album = t.Album,
                AlbumArtist = t.AlbumArtist,
                TrackNumber = t.TrackNumber,
                Year = t.Year,
                DurationSeconds = t.DurationSeconds,
            });
        }

        await playlists.AddEntriesAsync(playlistId, entries, cancellationToken).ConfigureAwait(false);
        Raise();
    }

    public async Task<IReadOnlyList<PlaylistEntry>> AddExternalEntriesAsync(int playlistId, IReadOnlyList<PlaylistEntry> entries, CancellationToken cancellationToken = default)
    {
        foreach (var e in entries) e.TrackId = null;
        // EF assigns the generated ids to these instances on save.
        await playlists.AddEntriesAsync(playlistId, entries, cancellationToken).ConfigureAwait(false);
        Raise();
        return entries;
    }

    public async Task RemoveEntriesAsync(int playlistId, IReadOnlyCollection<int> entryIds, CancellationToken cancellationToken = default)
    {
        await playlists.RemoveEntriesAsync(playlistId, entryIds, cancellationToken).ConfigureAwait(false);
        Raise();
    }

    public async Task ReorderAsync(int playlistId, IReadOnlyList<int> orderedEntryIds, CancellationToken cancellationToken = default)
    {
        await playlists.ReorderAsync(playlistId, orderedEntryIds, cancellationToken).ConfigureAwait(false);
        Raise();
    }

    public async Task<Playlist> ImportSpotifyAsync(SpotifyCollection collection, CancellationToken cancellationToken = default)
    {
        var externalId = (collection.IsAlbum ? "album:" : "playlist:") + collection.Id;
        var existing = await playlists.FindExternalAsync(PlaylistSource.Spotify, externalId, cancellationToken).ConfigureAwait(false);

        // Keep links to local tracks when refreshing an existing import.
        var previousLinks = new Dictionary<string, int>();
        if (existing is not null)
        {
            var old = await playlists.GetWithEntriesAsync(existing.Id, cancellationToken).ConfigureAwait(false);
            foreach (var e in old?.Entries ?? [])
            {
                if (e.ExternalId is not null && e.TrackId is { } tid) previousLinks[e.ExternalId] = tid;
            }
        }

        var entries = collection.Tracks.Select(t => new PlaylistEntry
        {
            Title = t.Name,
            Artist = t.ArtistText,
            Album = t.Album,
            AlbumArtist = t.AlbumArtist,
            TrackNumber = t.TrackNumber,
            Year = t.Year,
            DurationSeconds = t.DurationSeconds,
            ArtworkUrl = t.ImageUrl,
            ExternalId = t.Id,
            Isrc = t.Isrc,
            TrackId = previousLinks.TryGetValue(t.Id, out var tid) ? tid : null,
        }).ToList();

        int playlistId;
        if (existing is null)
        {
            var created = await playlists.AddAsync(new Playlist
            {
                Name = collection.Name,
                Description = collection.Description,
                SourceType = PlaylistSource.Spotify,
                ExternalId = externalId,
                ExternalUrl = collection.ExternalUrl,
                Owner = collection.Owner,
                ArtworkUrl = collection.ImageUrl,
            }, cancellationToken).ConfigureAwait(false);
            playlistId = created.Id;
        }
        else
        {
            playlistId = existing.Id;
            await playlists.UpdateAsync(playlistId, p =>
            {
                p.Name = collection.Name;
                p.Description = collection.Description;
                p.Owner = collection.Owner;
                p.ArtworkUrl = collection.ImageUrl;
                p.ExternalUrl = collection.ExternalUrl;
            }, cancellationToken).ConfigureAwait(false);
        }

        await playlists.ReplaceEntriesAsync(playlistId, entries, cancellationToken).ConfigureAwait(false);
        var matched = await MatchCoreAsync(playlistId, cancellationToken).ConfigureAwait(false);
        logger.LogInformation("Imported Spotify {Kind} '{Name}' with {Count} tracks ({Matched} matched locally)",
            collection.IsAlbum ? "album" : "playlist", collection.Name, entries.Count, matched);
        Raise();
        return (await playlists.GetWithEntriesAsync(playlistId, cancellationToken).ConfigureAwait(false))!;
    }

    public async Task<int> MatchWithLibraryAsync(int playlistId, CancellationToken cancellationToken = default)
    {
        var count = await MatchCoreAsync(playlistId, cancellationToken).ConfigureAwait(false);
        if (count > 0) Raise();
        return count;
    }

    private async Task<int> MatchCoreAsync(int playlistId, CancellationToken cancellationToken)
    {
        var playlist = await playlists.GetWithEntriesAsync(playlistId, cancellationToken).ConfigureAwait(false);
        if (playlist is null) return 0;
        var unmatched = playlist.Entries.Where(e => e.TrackId is null).ToList();
        if (unmatched.Count == 0) return 0;

        var library = await tracks.GetAllAsync(cancellationToken).ConfigureAwait(false);
        var byKey = library.GroupBy(t => t.NormalizedKey).ToDictionary(g => g.Key, g => g.ToList());
        var matched = 0;
        foreach (var entry in unmatched)
        {
            var key = TitleParser.NormalizeKey(entry.Artist, entry.Title);
            if (!byKey.TryGetValue(key, out var candidates)) continue;
            // Prefer the candidate with the closest duration (within 10s when the duration is known).
            var best = candidates
                .OrderBy(t => entry.DurationSeconds > 0 ? Math.Abs(t.DurationSeconds - entry.DurationSeconds) : 0)
                .FirstOrDefault(t => entry.DurationSeconds <= 0 || Math.Abs(t.DurationSeconds - entry.DurationSeconds) <= 10);
            if (best is null) continue;
            await playlists.SetEntryTrackAsync(entry.Id, best.Id, cancellationToken).ConfigureAwait(false);
            matched++;
        }

        return matched;
    }

    public async Task LinkEntryAsync(int entryId, int trackId, CancellationToken cancellationToken = default)
    {
        await playlists.SetEntryTrackAsync(entryId, trackId, cancellationToken).ConfigureAwait(false);
        Raise();
    }

    public async Task ExportM3uAsync(int playlistId, string destinationPath, CancellationToken cancellationToken = default)
    {
        var playlist = await playlists.GetWithEntriesAsync(playlistId, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("The playlist no longer exists.");
        var sb = new StringBuilder();
        sb.AppendLine("#EXTM3U");
        sb.AppendLine($"#PLAYLIST:{playlist.Name}");
        foreach (var e in playlist.Entries)
        {
            if (e.Track is null || !File.Exists(e.Track.FilePath)) continue;
            var artist = e.Track.Artist ?? e.Artist ?? string.Empty;
            sb.AppendLine($"#EXTINF:{(int)Math.Round(e.Track.DurationSeconds)},{artist} - {e.Track.Title}");
            sb.AppendLine(e.Track.FilePath);
        }

        await File.WriteAllTextAsync(destinationPath, sb.ToString(), new UTF8Encoding(false), cancellationToken).ConfigureAwait(false);
    }

    private static string ValidateName(string name)
    {
        var trimmed = name?.Trim() ?? string.Empty;
        if (trimmed.Length == 0) throw new ArgumentException("Enter a playlist name.");
        if (trimmed.Length > 200) throw new ArgumentException("The playlist name is too long.");
        return trimmed;
    }

    private void Raise() => PlaylistsChanged?.Invoke(this, EventArgs.Empty);
}
