namespace Sonic.Core.Models;

/// <summary>Tag values read from or written to an audio file.</summary>
public sealed class TrackMetadata
{
    public string? Title { get; set; }
    public string? Artist { get; set; }
    public string? Album { get; set; }
    public string? AlbumArtist { get; set; }
    public uint? TrackNumber { get; set; }
    public uint? Year { get; set; }
    public string? Genre { get; set; }
    public string? Comment { get; set; }

    public byte[]? Artwork { get; set; }
    public string? ArtworkMimeType { get; set; }

    // Read-only audio properties.
    public double DurationSeconds { get; set; }
    public int Bitrate { get; set; }
    public int SampleRate { get; set; }

    public TrackMetadata Clone() => (TrackMetadata)MemberwiseClone();
}
