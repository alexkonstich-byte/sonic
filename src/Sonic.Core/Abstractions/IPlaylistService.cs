using Sonic.Core.Models;

namespace Sonic.Core.Abstractions;

public interface IPlaylistService
{
    event EventHandler? PlaylistsChanged;

    Task<IReadOnlyList<PlaylistSummary>> GetPlaylistsAsync(CancellationToken cancellationToken = default);

    Task<Playlist?> GetPlaylistAsync(int id, CancellationToken cancellationToken = default);

    Task<Playlist> CreateAsync(string name, string? description = null, CancellationToken cancellationToken = default);

    Task RenameAsync(int playlistId, string name, CancellationToken cancellationToken = default);

    Task DeleteAsync(int playlistId, CancellationToken cancellationToken = default);

    Task AddTracksAsync(int playlistId, IReadOnlyCollection<int> trackIds, CancellationToken cancellationToken = default);

    /// <summary>Adds rows that aren't in the library yet (e.g. from a batch list). Returns them with their ids, in order.</summary>
    Task<IReadOnlyList<PlaylistEntry>> AddExternalEntriesAsync(int playlistId, IReadOnlyList<PlaylistEntry> entries, CancellationToken cancellationToken = default);

    Task RemoveEntriesAsync(int playlistId, IReadOnlyCollection<int> entryIds, CancellationToken cancellationToken = default);

    Task ReorderAsync(int playlistId, IReadOnlyList<int> orderedEntryIds, CancellationToken cancellationToken = default);

    /// <summary>Imports (or refreshes, if already imported) a Spotify playlist or album.</summary>
    Task<Playlist> ImportSpotifyAsync(SpotifyCollection collection, CancellationToken cancellationToken = default);

    /// <summary>Links unmatched entries to library tracks with the same artist/title. Returns the number of new matches.</summary>
    Task<int> MatchWithLibraryAsync(int playlistId, CancellationToken cancellationToken = default);

    Task LinkEntryAsync(int entryId, int trackId, CancellationToken cancellationToken = default);

    Task ExportM3uAsync(int playlistId, string destinationPath, CancellationToken cancellationToken = default);
}

public sealed record PlaylistSummary(
    int Id,
    string Name,
    string? Description,
    PlaylistSource SourceType,
    string? ArtworkUrl,
    string? FirstTrackArtworkPath,
    int EntryCount,
    int AvailableCount,
    double TotalSeconds,
    DateTime UpdatedAt,
    DateTime CreatedAt);
