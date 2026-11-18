namespace Sonic.Core.Models;

/// <summary>A saved set of download options applied with one click.</summary>
public sealed class DownloadPreset
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = string.Empty;
    public MediaKind Kind { get; set; }
    public AudioFormat Format { get; set; } = AudioFormat.Mp3;
    public AudioQuality Quality { get; set; } = AudioQuality.Best;
    public VideoOptions? Video { get; set; }
    /// <summary>Folder, or null for the default folder of the kind.</summary>
    public string? OutputDirectory { get; set; }
    /// <summary>File name template, or null for the default template of the kind.</summary>
    public string? FilenameTemplate { get; set; }

    public static List<DownloadPreset> Defaults() =>
    [
        new() { Id = "mp3-320", Name = "MP3 320 kbps", Kind = MediaKind.Audio, Format = AudioFormat.Mp3, Quality = AudioQuality.High },
        new() { Id = "flac", Name = "FLAC (lossless container)", Kind = MediaKind.Audio, Format = AudioFormat.Flac },
        new() { Id = "original", Name = "Original audio (no re-encoding)", Kind = MediaKind.Audio, Format = AudioFormat.Original },
        new()
        {
            Id = "video-1080", Name = "Video 1080p MP4", Kind = MediaKind.Video,
            Video = new VideoOptions { Container = VideoContainer.Mp4, MaxHeight = 1080 },
        },
        new()
        {
            Id = "video-720-subs", Name = "Video 720p + RU/EN subtitles", Kind = MediaKind.Video,
            Video = new VideoOptions
            {
                Container = VideoContainer.Mkv, MaxHeight = 720, SubtitleMode = SubtitleMode.Embed, SubtitleLanguages = "ru,en",
                IncludeAutoSubtitles = true,
            },
        },
    ];
}
