using System.ComponentModel.DataAnnotations.Schema;

namespace Sonic.Core.Models;

/// <summary>A queued, running or finished download. Persisted so the queue survives restarts.</summary>
public class DownloadItem
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Url { get; set; } = string.Empty;
    public SourcePlatform Source { get; set; }
    public MediaKind Kind { get; set; }

    /// <summary>Video options (only for <see cref="MediaKind.Video"/> downloads).</summary>
    public VideoOptions? Video { get; set; }

    public string? Title { get; set; }
    public string? Artist { get; set; }
    public string? ThumbnailUrl { get; set; }
    public double? DurationSeconds { get; set; }

    /// <summary>Specific yt-dlp format id chosen by the user, or null for automatic selection.</summary>
    public string? FormatId { get; set; }
    public AudioFormat Format { get; set; }
    public AudioQuality Quality { get; set; }
    public string OutputDirectory { get; set; } = string.Empty;
    public string FilenameTemplate { get; set; } = string.Empty;

    /// <summary>Metadata supplied up front (for example from Spotify) that takes priority over source metadata.</summary>
    public MetadataHints? Hints { get; set; }
    public int? PlaylistEntryId { get; set; }

    public DownloadStatus Status { get; set; }
    public double Progress { get; set; }
    public long? TotalBytes { get; set; }
    public long DownloadedBytes { get; set; }
    public string? FilePath { get; set; }
    public string? ErrorMessage { get; set; }
    public string? ErrorDetails { get; set; }
    public int Attempts { get; set; }
    public bool IsDismissed { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? StartedAt { get; set; }
    public DateTime? CompletedAt { get; set; }

    // Live, non-persisted values.
    [NotMapped] public double? SpeedBytesPerSecond { get; set; }
    [NotMapped] public double? EtaSeconds { get; set; }
    [NotMapped] public string? StageText { get; set; }
}

/// <summary>How a video download is selected, merged and finished (stored as an owned type).</summary>
public class VideoOptions
{
    public VideoContainer Container { get; set; } = VideoContainer.Mp4;
    /// <summary>Maximum height in pixels (1080, 720…); 0 means the best available.</summary>
    public int MaxHeight { get; set; }
    public VideoCodecPreference Codec { get; set; } = VideoCodecPreference.Compatible;
    public SubtitleMode SubtitleMode { get; set; } = SubtitleMode.None;
    public SubtitleFormat SubtitleFormat { get; set; } = SubtitleFormat.Srt;
    /// <summary>Comma-separated yt-dlp subtitle language codes or patterns, e.g. "en,ru" or "en.*".</summary>
    public string? SubtitleLanguages { get; set; }
    public bool IncludeAutoSubtitles { get; set; }
    public bool EmbedThumbnail { get; set; } = true;
    public bool EmbedChapters { get; set; } = true;
    public bool EmbedMetadata { get; set; } = true;

    public VideoOptions Clone() => (VideoOptions)MemberwiseClone();
}

/// <summary>Metadata hints attached to a download (stored as an owned type).</summary>
public class MetadataHints
{
    public string? Title { get; set; }
    public string? Artist { get; set; }
    public string? Album { get; set; }
    public string? AlbumArtist { get; set; }
    public uint? TrackNumber { get; set; }
    public uint? Year { get; set; }
    public string? Genre { get; set; }
    public string? ArtworkUrl { get; set; }
}
