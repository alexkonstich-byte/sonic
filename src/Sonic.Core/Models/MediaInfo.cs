namespace Sonic.Core.Models;

/// <summary>Result of analyzing a URL with yt-dlp (single item or playlist).</summary>
public sealed class MediaInfo
{
    public string Id { get; init; } = string.Empty;
    public string Url { get; init; } = string.Empty;
    public string Title { get; init; } = string.Empty;
    public string? Uploader { get; init; }
    public string? Artist { get; init; }
    public string? Track { get; init; }
    public string? Album { get; init; }
    public string? Genre { get; init; }
    public uint? ReleaseYear { get; init; }
    public uint? TrackNumber { get; init; }
    public string? ThumbnailUrl { get; init; }
    public double? DurationSeconds { get; init; }
    public string? Extractor { get; init; }
    public SourcePlatform Source { get; init; }
    public bool IsPlaylist { get; init; }
    public bool IsLive { get; init; }
    public IReadOnlyList<MediaFormat> AudioFormats { get; init; } = [];
    public IReadOnlyList<MediaInfo> Entries { get; init; } = [];

    /// <summary>Upload date as yyyyMMdd, when known.</summary>
    public string? UploadDate { get; init; }
    /// <summary>Height of the selected/best video stream, when known.</summary>
    public int? Height { get; init; }
    /// <summary>Distinct video heights available, highest first. Empty for audio-only sources.</summary>
    public IReadOnlyList<int> VideoHeights { get; init; } = [];
    public IReadOnlyList<SubtitleTrack> Subtitles { get; init; } = [];
    public string? Language { get; init; }

    public bool HasVideo => VideoHeights.Count > 0;

    /// <summary>Best guess for the performing artist.</summary>
    public string? DisplayArtist => Artist ?? Uploader;
}

/// <summary>A subtitle track offered by the source. <see cref="IsAuto"/> marks automatic captions.</summary>
public sealed record SubtitleTrack(string Code, string Name, bool IsAuto);

public sealed class MediaFormat
{
    public string FormatId { get; init; } = string.Empty;
    public string Extension { get; init; } = string.Empty;
    public string? AudioCodec { get; init; }
    public double? AudioBitrateKbps { get; init; }
    public int? SampleRate { get; init; }
    public int? Channels { get; init; }
    public long? FileSize { get; init; }
    public bool HasVideo { get; init; }
    public string? Note { get; init; }
}
