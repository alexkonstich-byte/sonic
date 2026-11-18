using Sonic.Core.Models;

namespace Sonic.Core.Abstractions;

/// <summary>
/// Progress reported by yt-dlp. <see cref="Status"/> is "downloading"/"finished", or "postprocess" with
/// <see cref="Postprocessor"/> naming the step (Merger, EmbedSubtitle, EmbedThumbnail…).
/// </summary>
public sealed record YtDlpProgress(
    string Status,
    long? DownloadedBytes,
    long? TotalBytes,
    double? SpeedBytesPerSecond,
    double? EtaSeconds,
    string? Postprocessor = null,
    string? Stream = null);

public sealed class YtDlpDownloadRequest
{
    public required string Url { get; init; }
    public required string WorkingDirectory { get; init; }
    public required string FormatSelector { get; init; }
    public bool WriteThumbnail { get; init; } = true;
    /// <summary>Additional yt-dlp options (merge format, subtitles, embedding…).</summary>
    public IReadOnlyList<string> ExtraArguments { get; init; } = [];
}

public sealed record FeedPage(IReadOnlyList<MediaInfo> Items, bool HasMore);

public sealed record YtDlpDownloadResult(string MediaFilePath, string? InfoJsonPath, string? ThumbnailPath, IReadOnlyList<string> SubtitlePaths);

public interface IYtDlpService
{
    Task<MediaInfo> AnalyzeAsync(string url, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<MediaInfo>> SearchAsync(string query, int maxResults, CancellationToken cancellationToken = default);

    Task<YtDlpDownloadResult> DownloadAsync(
        YtDlpDownloadRequest request,
        IProgress<YtDlpProgress> progress,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Reads a page of a (signed-in) feed such as ":ythistory", ":ytfav", ":ytwatchlater" or ":ytsubs".
    /// <paramref name="start"/> is 1-based.
    /// </summary>
    Task<FeedPage> GetFeedAsync(string feed, int start, int count, CancellationToken cancellationToken = default);

    /// <summary>Direct media URL for streaming a preview without downloading (audio-only when available).</summary>
    Task<string> GetStreamUrlAsync(string url, CancellationToken cancellationToken = default);

    /// <summary>Parses metadata from an info JSON file written during download.</summary>
    Task<MediaInfo?> ReadInfoJsonAsync(string path, CancellationToken cancellationToken = default);
}

/// <summary>A yt-dlp failure with a user-friendly message and the raw diagnostic output.</summary>
public sealed class YtDlpException(string message, string? details, bool isRetryable) : Exception(message)
{
    public string? Details { get; } = details;
    public bool IsRetryable { get; } = isRetryable;
}
