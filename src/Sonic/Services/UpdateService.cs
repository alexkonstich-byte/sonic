using System.Reflection;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Sonic.Core.Abstractions;
using Velopack;
using Velopack.Sources;

namespace Sonic.Services;

public enum UpdateState
{
    NotInstalled,
    NoFeed,
    Idle,
    Checking,
    UpToDate,
    Downloading,
    Ready,
    Failed,
}

/// <summary>
/// Updates the installed app (Velopack): checks the update feed, downloads in the background and installs on
/// restart — or silently when Sonic is closed. The feed is a folder, a web address or a GitHub repository;
/// release builds carry the feed they were published to.
/// </summary>
public sealed partial class UpdateService : ObservableObject
{
    private static readonly TimeSpan CheckInterval = TimeSpan.FromHours(6);

    private readonly ISettingsService _settings;
    private readonly IUiDispatcher _dispatcher;
    private readonly IInAppNotificationService _notifications;
    private readonly ILogger<UpdateService> _logger;
    private UpdateManager? _manager;
    private string? _managerFeed;
    private UpdateInfo? _pending;
    private Timer? _timer;

    public UpdateService(ISettingsService settings, IUiDispatcher dispatcher, IInAppNotificationService notifications, ILogger<UpdateService> logger)
    {
        _settings = settings;
        _dispatcher = dispatcher;
        _notifications = notifications;
        _logger = logger;
        _state = IsInstalled ? (Feed is null ? UpdateState.NoFeed : UpdateState.Idle) : UpdateState.NotInstalled;
    }

    /// <summary>The feed baked into the build by the release script (-p:SonicUpdateFeed=…).</summary>
    public static string? BuiltInFeed { get; } = Assembly.GetExecutingAssembly().GetCustomAttributes<AssemblyMetadataAttribute>()
        .FirstOrDefault(a => a.Key == "SonicUpdateFeed")?.Value is { Length: > 0 } feed ? feed : null;

    public string? Feed => string.IsNullOrWhiteSpace(_settings.Current.UpdateFeed) ? BuiltInFeed : _settings.Current.UpdateFeed.Trim();

    public bool IsInstalled { get; } = DetectInstalled();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusText), nameof(IsReady), nameof(CanCheck))]
    [NotifyCanExecuteChangedFor(nameof(CheckNowCommand), nameof(RestartToUpdateCommand))]
    private UpdateState _state;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusText))]
    private string? _detail;

    public bool IsReady => State == UpdateState.Ready;
    public bool CanCheck => IsInstalled && Feed is not null && State is not (UpdateState.Checking or UpdateState.Downloading);

    public string StatusText => State switch
    {
        UpdateState.NotInstalled => "Updates work in the installed version (Setup.exe). This copy runs from a folder.",
        UpdateState.NoFeed => "No update source is set.",
        UpdateState.Checking => "Checking for updates…",
        UpdateState.UpToDate => "Sonic is up to date.",
        UpdateState.Downloading => $"Downloading version {Detail}…",
        UpdateState.Ready => $"Version {Detail} is ready. It installs when you restart or close Sonic.",
        UpdateState.Failed => "Couldn't check for updates: " + Detail,
        _ => "Updates are checked automatically.",
    };

    private static bool DetectInstalled()
    {
        try
        {
            // Throws or reports false when the app wasn't installed by Velopack (e.g. a dev build).
            return new UpdateManager(new SimpleFileSource(new DirectoryInfo(Path.GetTempPath()))).IsInstalled;
        }
        catch (Exception)
        {
            return false;
        }
    }

    public void Start()
    {
        _settings.Changed += (_, _) => _dispatcher.Post(() =>
        {
            if (State is UpdateState.NoFeed or UpdateState.Idle && IsInstalled) State = Feed is null ? UpdateState.NoFeed : UpdateState.Idle;
        });
        if (!IsInstalled) return;
        _timer = new Timer(_ => _dispatcher.Post(() =>
        {
            if (_settings.Current.AutoCheckUpdates) _ = CheckAsync(userInitiated: false);
        }), null, TimeSpan.FromSeconds(30), CheckInterval);
    }

    private UpdateManager? GetManager()
    {
        var feed = Feed;
        if (feed is null) return null;
        if (_manager is not null && _managerFeed == feed) return _manager;
        IUpdateSource source = feed.StartsWith("https://github.com/", StringComparison.OrdinalIgnoreCase)
            ? new GithubSource(feed, null, false)
            : feed.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || feed.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
                ? new SimpleWebSource(feed)
                : new SimpleFileSource(new DirectoryInfo(feed));
        _manager = new UpdateManager(source);
        _managerFeed = feed;
        return _manager;
    }

    private bool CanCheckNow() => CanCheck;

    [RelayCommand(CanExecute = nameof(CanCheckNow))]
    private Task CheckNow() => CheckAsync(userInitiated: true);

    public async Task CheckAsync(bool userInitiated)
    {
        if (!CanCheck || IsReady) return;
        var manager = GetManager();
        if (manager is null)
        {
            State = UpdateState.NoFeed;
            return;
        }

        State = UpdateState.Checking;
        try
        {
            var update = await Task.Run(() => manager.CheckForUpdatesAsync());
            if (update is null)
            {
                State = UpdateState.UpToDate;
                return;
            }

            var version = update.TargetFullRelease.Version.ToString();
            Detail = version;
            State = UpdateState.Downloading;
            await Task.Run(() => manager.DownloadUpdatesAsync(update));
            _pending = update;
            State = UpdateState.Ready;
            _logger.LogInformation("Update {Version} downloaded", version);
            _notifications.ShowSuccess("Update ready", $"Sonic {version} installs when you restart or close Sonic. Use Restart to update in the status bar.");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Update check failed");
            Detail = ex.Message;
            State = UpdateState.Failed;
            if (userInitiated) _notifications.ShowWarning("Update check failed", ex.Message);
        }
    }

    [RelayCommand(CanExecute = nameof(IsReady))]
    private void RestartToUpdate()
    {
        if (_pending is null || _manager is null) return;
        // Velopack's updater waits for this process to exit, installs, and starts the new version.
        _manager.WaitExitThenApplyUpdates(_pending.TargetFullRelease, silent: false, restart: true);
        _pending = null;
        App.GetService<IWindowService>().RequestExit();
    }

    /// <summary>Called on exit: a downloaded update is installed quietly after Sonic closes.</summary>
    public void ApplyOnExit()
    {
        if (_pending is null || _manager is null) return;
        try
        {
            _manager.WaitExitThenApplyUpdates(_pending.TargetFullRelease, silent: true, restart: false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not schedule the update");
        }
    }
}
