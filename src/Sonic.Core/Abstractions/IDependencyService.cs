using Sonic.Core.Models;

namespace Sonic.Core.Abstractions;

/// <summary>Locates, verifies, installs and updates the external tools Sonic relies on.</summary>
public interface IDependencyService
{
    event EventHandler<ToolStatus>? StatusChanged;

    ToolStatus GetStatus(ToolKind kind);

    /// <summary>Re-detects all tools. When <paramref name="checkForUpdates"/> is set, queries the official release feeds.</summary>
    Task RefreshAsync(bool checkForUpdates, CancellationToken cancellationToken = default);

    /// <summary>Installs or updates a Sonic-managed copy of the tool from its official distribution source.</summary>
    Task InstallAsync(ToolKind kind, IProgress<InstallProgress>? progress, CancellationToken cancellationToken = default);

    /// <summary>Path to yt-dlp. Throws <see cref="DependencyMissingException"/> when unavailable.</summary>
    string RequireYtDlp();

    /// <summary>Path to ffmpeg. Throws <see cref="DependencyMissingException"/> when unavailable.</summary>
    string RequireFfmpeg();

    /// <summary>Path to ffprobe next to ffmpeg, if present.</summary>
    string? FfprobePath { get; }

    /// <summary>Arguments that tell yt-dlp which JavaScript runtime to use (may be empty).</summary>
    IReadOnlyList<string> GetYtDlpRuntimeArguments();
}

public sealed class DependencyMissingException(ToolKind kind, string message) : Exception(message)
{
    public ToolKind Kind { get; } = kind;
}
