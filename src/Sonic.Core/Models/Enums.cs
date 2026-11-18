namespace Sonic.Core.Models;

public enum DownloadStatus
{
    Queued = 0,
    Downloading = 1,
    Converting = 2,
    Tagging = 3,
    Completed = 4,
    Failed = 5,
    Canceled = 6,
    Paused = 7,
    Skipped = 8,
}

public enum AudioFormat
{
    /// <summary>Keep the source codec and only remux into a suitable container.</summary>
    Original = 0,
    Mp3 = 1,
    Flac = 2,
    Wav = 3,
    M4a = 4,
    Opus = 5,
    Ogg = 6,
}

public enum MediaKind
{
    Audio = 0,
    Video = 1,
}

public enum VideoContainer
{
    Mp4 = 0,
    Mkv = 1,
    Webm = 2,
}

public enum VideoCodecPreference
{
    /// <summary>H.264 + AAC where available: plays on every device and editor.</summary>
    Compatible = 0,
    /// <summary>Highest quality stream regardless of codec (VP9/AV1 on YouTube).</summary>
    BestQuality = 1,
}

public enum SubtitleMode
{
    None = 0,
    Embed = 1,
    Separate = 2,
}

public enum SubtitleFormat
{
    Srt = 0,
    Vtt = 1,
    Ass = 2,
}

public enum SponsorBlockMode
{
    Off = 0,
    /// <summary>Add chapters for sponsor segments but keep them in the video.</summary>
    Mark = 1,
    /// <summary>Cut sponsor segments out of the video.</summary>
    Remove = 2,
}

public enum AudioQuality
{
    Best = 0,
    High = 1,
    Standard = 2,
    Low = 3,
}

public enum SourcePlatform
{
    Unknown = 0,
    YouTube,
    YouTubeMusic,
    VK,
    Rutube,
    SoundCloud,
    Bandcamp,
    Vimeo,
    Spotify,
    Other,
}

public enum AppTheme
{
    System = 0,
    Light = 1,
    Dark = 2,
}

public enum DuplicateHandling
{
    /// <summary>Download anyway and keep both files.</summary>
    KeepBoth = 0,
    /// <summary>Skip downloads whose artist/title already exist in the library.</summary>
    Skip = 1,
}

public enum StartupPage
{
    Home = 0,
    Downloads = 1,
    Library = 2,
}

public enum PlaylistSource
{
    Local = 0,
    Spotify = 1,
}

public static class DownloadStatusExtensions
{
    public static bool IsActive(this DownloadStatus s) =>
        s is DownloadStatus.Downloading or DownloadStatus.Converting or DownloadStatus.Tagging;

    public static bool IsTerminal(this DownloadStatus s) =>
        s is DownloadStatus.Completed or DownloadStatus.Failed or DownloadStatus.Canceled or DownloadStatus.Skipped;
}
