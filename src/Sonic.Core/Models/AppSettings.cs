namespace Sonic.Core.Models;

public sealed class AppSettings
{
    // General
    public AppTheme Theme { get; set; } = AppTheme.System;
    public bool LaunchAtSignIn { get; set; }
    public StartupPage StartupPage { get; set; } = StartupPage.Home;
    public bool ResumeDownloadsOnStartup { get; set; } = true;
    public bool ShowNotifications { get; set; } = true;
    public bool ConfirmExitWithActiveDownloads { get; set; } = true;

    // Downloads
    public static string DefaultAudioDirectory => Path.Combine(Helpers.KnownFolders.Downloads, "Sonic");
    public static string DefaultVideoDirectory => Path.Combine(Helpers.KnownFolders.Downloads, "Sonic", "Video");

    /// <summary>Bumped when defaults change in a way existing settings should follow.</summary>
    public int SettingsVersion { get; set; } = CurrentVersion;
    public const int CurrentVersion = 2;

    public string DownloadDirectory { get; set; } = DefaultAudioDirectory;

    /// <summary>Folders recently chosen with "Save to" (most recent first).</summary>
    public List<string> RecentFolders { get; set; } = [];

    public List<DownloadPreset> Presets { get; set; } = DownloadPreset.Defaults();

    /// <summary>"Download all" names files by playlist position ("01 - Artist - Title"). Remembered from the dialog.</summary>
    public bool NumberPlaylistDownloads { get; set; }

    /// <summary>Pause downloads while a Steam game is running.</summary>
    public bool PauseWhileGaming { get; set; } = true;

    /// <summary>
    /// Steam apps that aren't games and never pause downloads. Wallpaper Engine (431960) runs all day through Steam.
    /// </summary>
    public List<int> GameModeIgnoredApps { get; set; } = [431960];

    /// <summary>Retry downloads that failed because of the network when the connection comes back (or zapret starts).</summary>
    public bool AutoRetryOnReconnect { get; set; } = true;

    /// <summary>Warn before downloading something that was already downloaded.</summary>
    public bool WarnAlreadyDownloaded { get; set; } = true;
    public string FilenameTemplate { get; set; } = "{artist} - {title}";
    public int MaxConcurrentDownloads { get; set; } = 3;
    public AudioFormat DefaultFormat { get; set; } = AudioFormat.Mp3;
    public AudioQuality DefaultQuality { get; set; } = AudioQuality.Best;
    public bool EmbedArtwork { get; set; } = true;
    public bool CropArtworkToSquare { get; set; } = true;

    /// <summary>Look up the real album cover (iTunes/Deezer) instead of using the video thumbnail.</summary>
    public bool FindAlbumArt { get; set; } = true;

    /// <summary>Show the playing song as the Discord status (through the local Discord app).</summary>
    public bool DiscordPresence { get; set; }

    /// <summary>Check for app updates in the background (installed version only).</summary>
    public bool AutoCheckUpdates { get; set; } = true;

    /// <summary>Update feed override: a folder, a web address or a GitHub repository URL. Null uses the built-in feed.</summary>
    public string? UpdateFeed { get; set; }

    /// <summary>Application ID from the Discord Developer Portal; the app's name is what Discord shows.</summary>
    public string? DiscordClientId { get; set; }
    public bool CleanTitles { get; set; } = true;
    public int MaxRetries { get; set; } = 2;

    // Video
    public MediaKind LastMediaKind { get; set; } = MediaKind.Audio;
    public string VideoDirectory { get; set; } = DefaultVideoDirectory;
    public string VideoFilenameTemplate { get; set; } = "{uploader} - {title}";
    public VideoContainer DefaultVideoContainer { get; set; } = VideoContainer.Mp4;
    public int DefaultVideoMaxHeight { get; set; } = 1080;
    public VideoCodecPreference DefaultVideoCodec { get; set; } = VideoCodecPreference.Compatible;
    public SubtitleMode DefaultSubtitleMode { get; set; } = SubtitleMode.None;
    public SubtitleFormat DefaultSubtitleFormat { get; set; } = SubtitleFormat.Srt;
    public string DefaultSubtitleLanguages { get; set; } = "en,ru";
    public bool IncludeAutoSubtitles { get; set; }
    public bool EmbedVideoThumbnail { get; set; } = true;
    public bool EmbedVideoChapters { get; set; } = true;

    public VideoOptions CreateDefaultVideoOptions() => new()
    {
        Container = DefaultVideoContainer,
        MaxHeight = DefaultVideoMaxHeight,
        Codec = DefaultVideoCodec,
        SubtitleMode = DefaultSubtitleMode,
        SubtitleFormat = DefaultSubtitleFormat,
        SubtitleLanguages = DefaultSubtitleLanguages,
        IncludeAutoSubtitles = IncludeAutoSubtitles,
        EmbedThumbnail = EmbedVideoThumbnail,
        EmbedChapters = EmbedVideoChapters,
    };

    // Integrations
    public string? SpotifyClientId { get; set; }
    /// <summary>DPAPI-protected (current user) Spotify client secret, base64 encoded.</summary>
    public string? SpotifyClientSecretProtected { get; set; }
    /// <summary>DPAPI-protected refresh token of the signed-in Spotify account.</summary>
    public string? SpotifyRefreshTokenProtected { get; set; }
    public string? SpotifyUserName { get; set; }

    /// <summary>Download newly liked Spotify songs automatically.</summary>
    public bool SpotifyLikedSync { get; set; }
    /// <summary>Folder for liked songs; null uses the download folder.</summary>
    public string? SpotifyLikedSyncFolder { get; set; }
    /// <summary>Only songs liked after this time are downloaded; null downloads all liked songs.</summary>
    public DateTime? SpotifyLikedSyncFrom { get; set; }
    public DateTime? SpotifyLikedLastSync { get; set; }
    /// <summary>Spotify ids that couldn't be found on YouTube, so they aren't searched again every sync.</summary>
    public List<string> SpotifyLikedNotFound { get; set; } = [];
    public string? YtDlpPathOverride { get; set; }
    public string? FfmpegPathOverride { get; set; }
    public bool CheckForToolUpdates { get; set; } = true;
    /// <summary>Download missing tools (and update Sonic's own yt-dlp) automatically at startup.</summary>
    public bool AutoInstallTools { get; set; } = true;
    // Network
    /// <summary>Per-download speed limit in KB/s; 0 = unlimited.</summary>
    public int SpeedLimitKBps { get; set; }
    /// <summary>Proxy URL (http, https, socks4, socks5), or null for a direct connection.</summary>
    public string? ProxyUrl { get; set; }

    // SponsorBlock (YouTube only)
    /// <summary>Cut "non-music" parts (intros, talking) out of music videos when downloading audio.</summary>
    public bool SponsorBlockAudio { get; set; } = true;
    public SponsorBlockMode SponsorBlockVideo { get; set; } = SponsorBlockMode.Off;

    // Desktop integration
    public bool WatchClipboard { get; set; } = true;
    public bool MinimizeToTray { get; set; }
    public bool GlobalHotkey { get; set; } = true;
    public bool ProtocolHandler { get; set; } = true;

    // zapret (DPI bypass for blocked/slowed YouTube)
    public string? ZapretFolder { get; set; }
    public string ZapretStrategy { get; set; } = "general.bat";
    public bool ZapretAutoStart { get; set; }
    public bool ZapretSuggest { get; set; } = true;
    /// <summary>Download zapret automatically, but only when YouTube is unreachable while the internet works.</summary>
    public bool ZapretAutoInstall { get; set; } = true;

    // Cookies (sign-in) for yt-dlp
    public CookieMode CookieMode { get; set; } = CookieMode.None;
    /// <summary>yt-dlp browser spec for <see cref="CookieMode.Browser"/>, e.g. "firefox:C:\…\profile".</summary>
    public string? CookieBrowserSpec { get; set; }
    public string? CookieBrowserName { get; set; }
    /// <summary>Where the imported snapshot came from (browser profile or file name).</summary>
    public string? CookiesImportedFrom { get; set; }
    public DateTime? CookiesImportedAtUtc { get; set; }

    // Library
    public List<string> MusicFolders { get; set; } = [];
    public bool ScanOnStartup { get; set; } = true;
    public bool WatchFolders { get; set; } = true;
    public DuplicateHandling DuplicateHandling { get; set; } = DuplicateHandling.KeepBoth;

    // Advanced
    public bool VerboseLogging { get; set; }

    public AppSettings Clone()
    {
        var copy = (AppSettings)MemberwiseClone();
        copy.MusicFolders = [.. MusicFolders];
        copy.RecentFolders = [.. RecentFolders];
        copy.Presets = [.. Presets];
        copy.GameModeIgnoredApps = [.. GameModeIgnoredApps];
        copy.SpotifyLikedNotFound = [.. SpotifyLikedNotFound];
        return copy;
    }
}
