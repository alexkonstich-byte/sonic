namespace Sonic.Core.Models;

public sealed class SpotifyCollection
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public string? Description { get; init; }
    public string? Owner { get; init; }
    public string? ImageUrl { get; init; }
    public string? ExternalUrl { get; init; }
    public bool IsAlbum { get; init; }
    public IReadOnlyList<SpotifyTrack> Tracks { get; init; } = [];
    /// <summary>Items Spotify returned that could not be imported (local files, episodes, removed tracks).</summary>
    public int SkippedItems { get; init; }
}

public sealed class SpotifyTrack
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public IReadOnlyList<string> Artists { get; init; } = [];
    public string? Album { get; init; }
    public string? AlbumArtist { get; init; }
    public uint? TrackNumber { get; init; }
    public uint? Year { get; init; }
    public double DurationSeconds { get; init; }
    public string? ImageUrl { get; init; }
    public string? Isrc { get; init; }

    public string ArtistText => string.Join(", ", Artists);
}
