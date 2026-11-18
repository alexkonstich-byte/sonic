using System.Globalization;
using Sonic.Core.Abstractions;

namespace Sonic.Core.Helpers;

/// <summary>Parses the structured lines Sonic asks yt-dlp to print.</summary>
public static class YtDlpOutputParser
{
    public const string ProgressPrefix = "[sonic-progress]";
    public const string FilePrefix = "[sonic-file]";

    /// <summary>Template passed to <c>--progress-template</c>. Fields are separated by '|'.</summary>
    public const string ProgressTemplate =
        "download:" + ProgressPrefix +
        "%(progress.status)s|%(progress.downloaded_bytes)s|%(progress.total_bytes)s|%(progress.total_bytes_estimate)s|%(progress.speed)s|%(progress.eta)s" +
        "|%(info.vcodec)s|%(info.acodec)s|%(info.ext)s";

    public const string FileTemplate = "after_move:" + FilePrefix + "%(filepath)s";

    public const string PostprocessPrefix = "[sonic-pp]";

    /// <summary>Template passed to <c>--progress-template</c> for post-processing steps.</summary>
    public const string PostprocessTemplate =
        "postprocess:" + PostprocessPrefix + "%(progress.status)s|%(progress.postprocessor)s";

    public static readonly string[] SubtitleExtensions = [".srt", ".vtt", ".ass", ".ssa", ".lrc", ".ttml", ".srv3", ".json3"];

    public static bool TryParsePostprocess(string line, out string status, out string postprocessor)
    {
        status = postprocessor = string.Empty;
        var start = line.IndexOf(PostprocessPrefix, StringComparison.Ordinal);
        if (start < 0) return false;
        var parts = line[(start + PostprocessPrefix.Length)..].Split('|');
        if (parts.Length < 2) return false;
        status = parts[0].Trim();
        postprocessor = parts[1].Trim();
        return true;
    }

    /// <summary>User-facing name of a yt-dlp post-processor.</summary>
    public static string DescribePostprocessor(string? name) => name switch
    {
        "Merger" => "Merging video and audio",
        "FFmpegMerger" => "Merging video and audio",
        "EmbedSubtitle" or "FFmpegEmbedSubtitle" => "Embedding subtitles",
        "SubtitlesConvertor" or "FFmpegSubtitlesConvertor" => "Converting subtitles",
        "EmbedThumbnail" => "Embedding thumbnail",
        "ThumbnailsConvertor" or "FFmpegThumbnailsConvertor" => "Preparing thumbnail",
        "Metadata" or "FFmpegMetadata" => "Writing metadata and chapters",
        "VideoRemuxer" or "FFmpegVideoRemuxer" => "Remuxing",
        "FixupM3u8" or "FixupM4a" or "FixupStretched" or "FixupDuplicateMoov" or "FixupTimestamp" or "FixupDuration" => "Fixing container",
        "MoveFiles" => "Finishing",
        "SponsorBlock" => "Checking SponsorBlock",
        "ModifyChapters" => "Removing skipped segments",
        _ => "Processing",
    };

    public static bool TryParseProgress(string line, out YtDlpProgress progress)
    {
        progress = null!;
        var start = line.IndexOf(ProgressPrefix, StringComparison.Ordinal);
        if (start < 0) return false;
        var parts = line[(start + ProgressPrefix.Length)..].Split('|');
        if (parts.Length < 6) return false;

        var downloaded = ParseLong(parts[1]);
        var total = ParseLong(parts[2]) ?? ParseLong(parts[3]);
        var stream = parts.Length >= 9 ? ClassifyStream(parts[6].Trim(), parts[7].Trim(), parts[8].Trim()) : null;
        progress = new YtDlpProgress(parts[0].Trim(), downloaded, total, ParseDouble(parts[4]), ParseDouble(parts[5]), Stream: stream);
        return true;
    }

    /// <summary>Returns "video", "audio" or "subtitles" for the stream being downloaded.</summary>
    internal static string? ClassifyStream(string vcodec, string acodec, string ext)
    {
        static bool Has(string c) => c.Length > 0 && c != "none" && c != "NA";
        if (SubtitleExtensions.Contains("." + ext, StringComparer.OrdinalIgnoreCase)) return "subtitles";
        if (Has(vcodec)) return "video";
        if (Has(acodec)) return "audio";
        // Some sites don't report codecs; fall back to the file extension.
        return ext switch
        {
            "m4a" or "mp3" or "aac" or "opus" or "ogg" or "oga" or "flac" or "wav" => "audio",
            "mp4" or "mkv" or "webm" or "flv" or "mov" or "ts" => "video",
            _ => null,
        };
    }

    public static bool TryParseFile(string line, out string path)
    {
        path = string.Empty;
        var start = line.IndexOf(FilePrefix, StringComparison.Ordinal);
        if (start < 0) return false;
        path = line[(start + FilePrefix.Length)..].Trim();
        return path.Length > 0;
    }

    private static double? ParseDouble(string s)
    {
        s = s.Trim();
        if (s.Length == 0 || s == "NA" || s == "None") return null;
        return double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) && double.IsFinite(v) ? v : null;
    }

    private static long? ParseLong(string s)
    {
        var d = ParseDouble(s);
        return d is null ? null : (long)Math.Round(d.Value);
    }

    /// <summary>Converts yt-dlp error output into a short, user-facing message.</summary>
    public static (string Message, bool Retryable) Describe(string stderr)
    {
        var lines = stderr.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var error = lines.LastOrDefault(l => l.StartsWith("ERROR:", StringComparison.Ordinal)) ?? lines.LastOrDefault() ?? string.Empty;
        var e = error.ToLowerInvariant();

        if (e.Contains("drm")) return ("This content is DRM-protected and cannot be downloaded.", false);
        if (e.Contains("unsupported url")) return ("This website or link type isn't supported by yt-dlp.", false);
        if (e.Contains("private video") || e.Contains("this video is private")) return ("This video is private.", false);
        if (e.Contains("sign in to confirm your age") || e.Contains("age-restricted")) return ("This content is age-restricted and requires signing in on the platform.", false);
        if (e.Contains("sign in to confirm") || e.Contains("not a bot"))
            return ("YouTube asked to confirm you're not a bot (often after many downloads). Import your browser cookies in Settings › Integrations › Cookies, or wait a while and update yt-dlp.", true);
        if (e.Contains("could not copy") && e.Contains("cookie")) return ("The browser's cookie database is locked. Close that browser, or import cookies once in Settings › Integrations › Cookies.", false);
        if (e.Contains("failed to decrypt") && e.Contains("cookie")) return ("This browser's cookies can't be read by yt-dlp (Chrome/Edge encrypt them). Use Firefox or import a cookies.txt file instead.", false);
        if (e.Contains("cookies") && (e.Contains("invalid") || e.Contains("does not look like a netscape"))) return ("The imported cookies are invalid. Import them again in Settings › Integrations › Cookies.", false);
        if (e.Contains("members-only") || e.Contains("join this channel")) return ("This content is only available to channel members.", false);
        if (e.Contains("video unavailable") || e.Contains("is not available") || e.Contains("has been removed")) return ("This content is unavailable or has been removed.", false);
        if (e.Contains("not available in your country") || e.Contains("geo")) return ("This content isn't available in your region.", false);
        if (e.Contains("live event will begin") || e.Contains("premieres in")) return ("This is an upcoming live stream or premiere and can't be downloaded yet.", false);
        if (e.Contains("http error 404")) return ("The page was not found (HTTP 404).", false);
        if (e.Contains("http error 403")) return ("Access was denied by the server (HTTP 403). Updating yt-dlp often fixes this.", true);
        if (e.Contains("http error 429") || e.Contains("too many requests")) return ("The platform is rate limiting requests. Please wait and retry.", true);
        if (e.Contains("getaddrinfo") || e.Contains("name resolution") || e.Contains("network is unreachable") || e.Contains("connection") || e.Contains("timed out"))
            return ("A network error occurred. Check your internet connection and retry.", true);
        if (e.Contains("requested format is not available")) return ("No audio format is available for this item.", false);
        if (e.Contains("ffmpeg") && e.Contains("not found")) return ("FFmpeg was not found. Install it from Settings › Integrations.", false);
        if (e.Contains("no space left")) return ("The disk is full.", false);

        var message = error.StartsWith("ERROR:", StringComparison.Ordinal) ? error[6..].Trim() : error;
        // Strip the "[extractor] id:" prefix yt-dlp adds.
        if (message.StartsWith('['))
        {
            var close = message.IndexOf("] ", StringComparison.Ordinal);
            if (close > 0)
            {
                message = message[(close + 2)..];
                var colon = message.IndexOf(": ", StringComparison.Ordinal);
                if (colon is > 0 and < 40) message = message[(colon + 2)..];
            }
        }

        return (string.IsNullOrWhiteSpace(message) ? "yt-dlp failed for an unknown reason." : message, true);
    }
}
