using Sonic.Core.Models;

namespace Sonic.Helpers;

/// <summary>Segoe Fluent Icons code points used across the app.</summary>
public static class Glyphs
{
    public const string Home = "";
    public const string Download = "";
    public const string Library = "";
    public const string Playlist = "";
    public const string History = "";
    public const string Settings = "";
    public const string Music = "";
    public const string Album = "";
    public const string Contact = "";
    public const string Globe = "";
    public const string Video = "";
    public const string Link = "";
    public const string Paste = "";
    public const string Search = "";
    public const string Cancel = "";
    public const string Retry = "";
    public const string Pause = "";
    public const string Play = "";
    public const string Delete = "";
    public const string Folder = "";
    public const string OpenFile = "";
    public const string Edit = "";
    public const string Favorite = "";
    public const string FavoriteFilled = "";
    public const string Add = "";
    public const string Warning = "";
    public const string Error = "";
    public const string Completed = "";
    public const string Clock = "";
    public const string Cloud = "";
    public const string Sync = "";
    public const string Import = "";
    public const string Skip = "";

    public static string ForSource(SourcePlatform source) => source switch
    {
        SourcePlatform.YouTube or SourcePlatform.Vimeo or SourcePlatform.Rutube or SourcePlatform.VK => Video,
        SourcePlatform.YouTubeMusic or SourcePlatform.SoundCloud or SourcePlatform.Bandcamp => Music,
        SourcePlatform.Spotify => Playlist,
        SourcePlatform.Other => Globe,
        _ => Link,
    };

    public static string ForStatus(DownloadStatus status) => status switch
    {
        DownloadStatus.Completed => Completed,
        DownloadStatus.Failed => Error,
        DownloadStatus.Canceled => Cancel,
        DownloadStatus.Paused => Pause,
        DownloadStatus.Skipped => Skip,
        DownloadStatus.Queued => Clock,
        _ => Download,
    };
}
