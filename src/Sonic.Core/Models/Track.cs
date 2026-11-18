namespace Sonic.Core.Models;

/// <summary>An audio file in the local music library.</summary>
public class Track
{
    public int Id { get; set; }
    public string FilePath { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string? Artist { get; set; }
    public string? Album { get; set; }
    public string? AlbumArtist { get; set; }
    public string? Genre { get; set; }
    public string? Comment { get; set; }
    public uint? TrackNumber { get; set; }
    public uint? Year { get; set; }
    public double DurationSeconds { get; set; }
    public int Bitrate { get; set; }
    public int SampleRate { get; set; }
    public long FileSize { get; set; }
    public string Extension { get; set; } = string.Empty;
    public DateTime DateAdded { get; set; } = DateTime.UtcNow;
    public DateTime FileModified { get; set; }
    public bool IsFavorite { get; set; }
    /// <summary>Path of the cached artwork image extracted from the file, if any.</summary>
    public string? ArtworkPath { get; set; }
    public string? SourceUrl { get; set; }
    /// <summary>Normalized artist|title key used for duplicate detection.</summary>
    public string NormalizedKey { get; set; } = string.Empty;

    public string DisplayArtist => string.IsNullOrWhiteSpace(Artist) ? "Unknown artist" : Artist!;
    public string DisplayAlbum => string.IsNullOrWhiteSpace(Album) ? "Unknown album" : Album!;
    public string GroupingArtist => !string.IsNullOrWhiteSpace(AlbumArtist) ? AlbumArtist! : DisplayArtist;
}
