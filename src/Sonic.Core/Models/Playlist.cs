namespace Sonic.Core.Models;

public class Playlist
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    public PlaylistSource SourceType { get; set; }
    public string? ExternalId { get; set; }
    public string? ExternalUrl { get; set; }
    public string? Owner { get; set; }
    public string? ArtworkUrl { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    public List<PlaylistEntry> Entries { get; set; } = [];
}

/// <summary>
/// A playlist row. It can point at a local <see cref="Track"/> and/or carry external metadata
/// (for example from Spotify) for tracks that are not in the local library yet.
/// </summary>
public class PlaylistEntry
{
    public int Id { get; set; }
    public int PlaylistId { get; set; }
    public Playlist? Playlist { get; set; }
    public int Position { get; set; }
    public int? TrackId { get; set; }
    public Track? Track { get; set; }

    public string Title { get; set; } = string.Empty;
    public string? Artist { get; set; }
    public string? Album { get; set; }
    public string? AlbumArtist { get; set; }
    public uint? TrackNumber { get; set; }
    public uint? Year { get; set; }
    public double DurationSeconds { get; set; }
    public string? ArtworkUrl { get; set; }
    public string? ExternalId { get; set; }
    public string? Isrc { get; set; }
    public DateTime AddedAt { get; set; } = DateTime.UtcNow;
}
