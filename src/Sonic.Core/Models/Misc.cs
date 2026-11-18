namespace Sonic.Core.Models;

public sealed record ScanProgress(int Processed, int Total, string? CurrentFile);

public sealed record ScanResult(int Added, int Updated, int Removed, int Failed);

public sealed record DuplicateGroup(string Key, IReadOnlyList<Track> Tracks);

/// <summary>Request to add a download to the queue.</summary>
public sealed class DownloadRequest
{
    public required string Url { get; init; }
    public MediaKind Kind { get; init; } = MediaKind.Audio;
    public VideoOptions? Video { get; init; }
    /// <summary>File name template for this download; null uses the default for the media kind.</summary>
    public string? FilenameTemplate { get; init; }
    public string? Title { get; init; }
    public string? Artist { get; init; }
    public string? ThumbnailUrl { get; init; }
    public double? DurationSeconds { get; init; }
    public string? FormatId { get; init; }
    public AudioFormat? Format { get; init; }
    public AudioQuality? Quality { get; init; }
    public string? OutputDirectory { get; init; }
    public MetadataHints? Hints { get; init; }
    public int? PlaylistEntryId { get; init; }
}

public enum DownloadChangeKind
{
    Added,
    Updated,
    Progress,
    Removed,
}

public sealed class DownloadChangedEventArgs(DownloadItem item, DownloadChangeKind kind) : EventArgs
{
    public DownloadItem Item { get; } = item;
    public DownloadChangeKind Kind { get; } = kind;
}

public sealed class LibraryChangedEventArgs(IReadOnlyCollection<int>? changedTrackIds = null) : EventArgs
{
    /// <summary>Ids of changed tracks, or null when the change is broad (scan, bulk removal).</summary>
    public IReadOnlyCollection<int>? ChangedTrackIds { get; } = changedTrackIds;
}
