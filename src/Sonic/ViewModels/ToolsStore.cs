using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Sonic.Core.Abstractions;
using Sonic.Core.Models;
using Sonic.Services;

namespace Sonic.ViewModels;

public sealed partial class ToolStatusViewModel(ToolKind kind, IDialogService dialogs, IShellLauncher launcher) : ObservableObject
{
    public ToolKind Kind { get; } = kind;

    [ObservableProperty] private ToolState _state = ToolState.Unknown;
    [ObservableProperty] private string? _version;
    [ObservableProperty] private string? _latestVersion;
    [ObservableProperty] private string? _path;
    [ObservableProperty] private string? _message;
    [ObservableProperty] private bool _isManaged;

    public string DisplayName => Kind switch
    {
        ToolKind.YtDlp => "yt-dlp",
        ToolKind.Ffmpeg => "FFmpeg",
        _ => "JavaScript runtime",
    };

    public string Description => Kind switch
    {
        ToolKind.YtDlp => "Downloads audio from YouTube and thousands of other sites.",
        ToolKind.Ffmpeg => "Converts audio between formats and prepares album art.",
        _ => "Optional. Lets yt-dlp solve YouTube's JavaScript challenges (Deno or Node.js).",
    };

    public bool IsReady => State is ToolState.Ready or ToolState.UpdateAvailable;
    public bool IsMissing => State is ToolState.Missing or ToolState.Error;
    public bool IsChecking => State is ToolState.Checking or ToolState.Unknown or ToolState.Installing;
    public bool HasUpdate => State == ToolState.UpdateAvailable;
    public bool IsRequired => Kind != ToolKind.Deno;

    /// <summary>Install/update is offered for missing tools, outdated tools and Sonic-managed copies.</summary>
    public bool CanInstall => !IsChecking && (IsMissing || HasUpdate || IsManaged);

    public string InstallText => IsMissing ? "Install" : HasUpdate ? "Update" : "Reinstall";

    public string ShortStatus => State switch
    {
        ToolState.Ready or ToolState.UpdateAvailable => Version ?? "Ready",
        ToolState.Missing => "Not installed",
        ToolState.Error => "Error",
        ToolState.Installing => "Installing…",
        _ => "Checking…",
    };

    public string StatusText => State switch
    {
        ToolState.Ready => $"Version {Version}" + (IsManaged ? " · Managed by Sonic" : string.Empty),
        ToolState.UpdateAvailable => $"Version {Version} · {LatestVersion} available",
        ToolState.Missing => IsRequired ? "Not installed" : "Not installed (optional)",
        ToolState.Error => Message ?? "Couldn't run",
        ToolState.Installing => "Installing from the official source…",
        _ => "Checking…",
    };

    public void Update(ToolStatus status)
    {
        State = status.State;
        Version = status.Version;
        LatestVersion = status.LatestVersion;
        Path = status.Path;
        Message = status.Message;
        IsManaged = status.IsManaged;
        foreach (var name in new[] { nameof(IsReady), nameof(IsMissing), nameof(IsChecking), nameof(HasUpdate), nameof(CanInstall), nameof(InstallText), nameof(ShortStatus), nameof(StatusText) })
            OnPropertyChanged(name);
    }

    [RelayCommand]
    private Task Install() => dialogs.InstallToolAsync(Kind);

    [RelayCommand]
    private void RevealPath()
    {
        if (Path is not null) launcher.RevealInExplorer(Path);
    }
}

/// <summary>Live status of the external tools for the status bar, Home and Settings.</summary>
public sealed class ToolsStore
{
    public ToolsStore(IDependencyService dependencies, IUiDispatcher dispatcher, IDialogService dialogs, IShellLauncher launcher)
    {
        YtDlp = new ToolStatusViewModel(ToolKind.YtDlp, dialogs, launcher);
        Ffmpeg = new ToolStatusViewModel(ToolKind.Ffmpeg, dialogs, launcher);
        JsRuntime = new ToolStatusViewModel(ToolKind.Deno, dialogs, launcher);
        foreach (var kind in Enum.GetValues<ToolKind>()) Get(kind).Update(dependencies.GetStatus(kind));
        dependencies.StatusChanged += (_, s) => dispatcher.Post(() => Get(s.Kind).Update(s));
    }

    public ToolStatusViewModel YtDlp { get; }
    public ToolStatusViewModel Ffmpeg { get; }
    public ToolStatusViewModel JsRuntime { get; }

    public IReadOnlyList<ToolStatusViewModel> All => [YtDlp, Ffmpeg, JsRuntime];

    public ToolStatusViewModel Get(ToolKind kind) => kind switch
    {
        ToolKind.YtDlp => YtDlp,
        ToolKind.Ffmpeg => Ffmpeg,
        _ => JsRuntime,
    };
}
