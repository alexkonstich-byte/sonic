using System.Globalization;
using Sonic.Core.Models;

namespace Sonic.Helpers;

/// <summary>Display formatting used by views (also callable from x:Bind function bindings).</summary>
public static class Format
{
    public static string Duration(double seconds)
    {
        if (seconds <= 0 || double.IsNaN(seconds)) return "–";
        var t = TimeSpan.FromSeconds(Math.Round(seconds));
        return t.TotalHours >= 1 ? t.ToString(@"h\:mm\:ss", CultureInfo.InvariantCulture) : t.ToString(@"m\:ss", CultureInfo.InvariantCulture);
    }

    public static string Duration(double? seconds) => Duration(seconds ?? 0);

    public static string LongDuration(double seconds)
    {
        if (seconds <= 0) return "0 min";
        var t = TimeSpan.FromSeconds(seconds);
        if (t.TotalHours >= 1) return $"{(int)t.TotalHours} hr {t.Minutes} min";
        return $"{Math.Max(1, (int)Math.Round(t.TotalMinutes))} min";
    }

    public static string Bytes(long? bytes)
    {
        if (bytes is not > 0) return "–";
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        double value = bytes.Value;
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        return unit == 0 ? $"{value:0} {units[unit]}" : $"{value:0.0} {units[unit]}";
    }

    public static string Speed(double? bytesPerSecond) => bytesPerSecond is > 0 ? Bytes((long)bytesPerSecond.Value) + "/s" : string.Empty;

    public static string Eta(double? seconds)
    {
        if (seconds is not >= 0) return string.Empty;
        var t = TimeSpan.FromSeconds(seconds.Value);
        if (t.TotalHours >= 1) return $"{(int)t.TotalHours}h {t.Minutes}m left";
        if (t.TotalMinutes >= 1) return $"{(int)t.TotalMinutes}m {t.Seconds}s left";
        return $"{t.Seconds}s left";
    }

    public static string RelativeTime(DateTime utc)
    {
        var local = utc.Kind == DateTimeKind.Utc ? utc.ToLocalTime() : utc;
        var delta = DateTime.Now - local;
        if (delta.TotalMinutes < 1) return "Just now";
        if (delta.TotalHours < 1) return $"{(int)delta.TotalMinutes} min ago";
        if (delta.TotalDays < 1) return $"{(int)delta.TotalHours} hr ago";
        if (delta.TotalDays < 2) return "Yesterday";
        if (delta.TotalDays < 7) return local.ToString("dddd", CultureInfo.CurrentCulture);
        return local.ToString("d", CultureInfo.CurrentCulture);
    }

    public static string DateTimeShort(DateTime? utc) =>
        utc is null ? string.Empty : (utc.Value.Kind == DateTimeKind.Utc ? utc.Value.ToLocalTime() : utc.Value).ToString("g", CultureInfo.CurrentCulture);

    public static string AudioFormatName(AudioFormat format) => format switch
    {
        AudioFormat.Original => "Original",
        AudioFormat.Mp3 => "MP3",
        AudioFormat.Flac => "FLAC",
        AudioFormat.Wav => "WAV",
        AudioFormat.M4a => "M4A",
        AudioFormat.Opus => "Opus",
        AudioFormat.Ogg => "OGG",
        _ => format.ToString(),
    };

    public static string QualityName(AudioQuality quality, AudioFormat format)
    {
        if (format is AudioFormat.Flac or AudioFormat.Wav) return "Lossless";
        if (format == AudioFormat.Original) return "Source quality";
        return (quality, format) switch
        {
            (AudioQuality.Best, AudioFormat.Mp3) => "Best (VBR V0)",
            (AudioQuality.Best, AudioFormat.M4a) => "Best (320 kbps)",
            (AudioQuality.Best, AudioFormat.Opus) => "Best (192 kbps)",
            (AudioQuality.Best, AudioFormat.Ogg) => "Best (q8)",
            (AudioQuality.High, AudioFormat.Mp3) => "High (320 kbps)",
            (AudioQuality.High, AudioFormat.M4a) => "High (256 kbps)",
            (AudioQuality.High, AudioFormat.Opus) => "High (160 kbps)",
            (AudioQuality.High, AudioFormat.Ogg) => "High (q6)",
            (AudioQuality.Standard, AudioFormat.Opus) => "Standard (128 kbps)",
            (AudioQuality.Standard, AudioFormat.Ogg) => "Standard (q5)",
            (AudioQuality.Standard, _) => "Standard (192 kbps)",
            (AudioQuality.Low, AudioFormat.Opus) => "Low (96 kbps)",
            (AudioQuality.Low, AudioFormat.Ogg) => "Low (q3)",
            (AudioQuality.Low, _) => "Low (128 kbps)",
            _ => quality.ToString(),
        };
    }

    /// <summary>"MP4 · 1080p · subtitles" style summary of a video download.</summary>
    public static string VideoSummary(VideoOptions? v)
    {
        if (v is null) return "Video";
        var parts = new List<string> { v.Container.ToString().ToUpperInvariant(), v.MaxHeight > 0 ? $"≤{v.MaxHeight}p" : "best quality" };
        if (v.SubtitleMode != SubtitleMode.None && !string.IsNullOrWhiteSpace(v.SubtitleLanguages)) parts.Add("subtitles");
        return string.Join(" · ", parts);
    }

    public static string DownloadFormat(DownloadItem item) =>
        item.Kind == MediaKind.Video ? VideoSummary(item.Video)
        : item.Format == AudioFormat.Original ? "Original format" : AudioFormatName(item.Format);

    public static string StatusText(DownloadStatus status) => status switch
    {
        DownloadStatus.Queued => "Queued",
        DownloadStatus.Downloading => "Downloading",
        DownloadStatus.Converting => "Converting",
        DownloadStatus.Tagging => "Writing tags",
        DownloadStatus.Completed => "Completed",
        DownloadStatus.Failed => "Failed",
        DownloadStatus.Canceled => "Canceled",
        DownloadStatus.Paused => "Paused",
        DownloadStatus.Skipped => "Skipped",
        _ => status.ToString(),
    };

    public static string Count(int n, string singular, string? plural = null) =>
        $"{n:N0} {(n == 1 ? singular : plural ?? singular + "s")}";
}
