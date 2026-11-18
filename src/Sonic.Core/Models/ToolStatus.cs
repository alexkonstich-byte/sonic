namespace Sonic.Core.Models;

public enum ToolKind
{
    YtDlp,
    Ffmpeg,
    Deno,
}

public enum ToolState
{
    Unknown,
    Checking,
    Ready,
    UpdateAvailable,
    Missing,
    Error,
    Installing,
}

public sealed record ToolStatus(
    ToolKind Kind,
    ToolState State,
    string? Path = null,
    string? Version = null,
    string? LatestVersion = null,
    bool IsManaged = false,
    string? Message = null)
{
    public bool IsUsable => State is ToolState.Ready or ToolState.UpdateAvailable;

    public string DisplayName => Kind switch
    {
        ToolKind.YtDlp => "yt-dlp",
        ToolKind.Ffmpeg => "FFmpeg",
        ToolKind.Deno => "JavaScript runtime",
        _ => Kind.ToString(),
    };
}

public sealed record InstallProgress(string Stage, double? Percent);
