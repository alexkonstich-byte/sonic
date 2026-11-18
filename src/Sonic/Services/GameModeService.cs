using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;
using Sonic.Core.Abstractions;

namespace Sonic.Services;

/// <summary>
/// Pauses downloads while a Steam game is running so they don't eat bandwidth (or disk) during play.
/// Steam publishes the running game's id in HKCU\Software\Valve\Steam\RunningAppID; Sonic only reads it.
/// "Download anyway" overrides the pause until that game exits.
/// </summary>
public sealed partial class GameModeService : ObservableObject, IDisposable
{
    private const string SteamKey = @"Software\Valve\Steam";
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(5);

    private readonly IDownloadQueueService _queue;
    private readonly ISettingsService _settings;
    private readonly IUiDispatcher _dispatcher;
    private readonly ILogger<GameModeService> _logger;
    private readonly Timer _timer;
    private int _overriddenAppId;

    public GameModeService(IDownloadQueueService queue, ISettingsService settings, IUiDispatcher dispatcher, ILogger<GameModeService> logger)
    {
        _queue = queue;
        _settings = settings;
        _dispatcher = dispatcher;
        _logger = logger;
        _timer = new Timer(_ => Poll(), null, Timeout.Infinite, Timeout.Infinite);
        settings.Changed += (_, _) => Poll();
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsHolding), nameof(BannerText))]
    private string? _gameName;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsHolding), nameof(BannerText))]
    private bool _isOverridden;

    /// <summary>True while downloads are paused because of a game.</summary>
    public bool IsHolding => GameName is not null && !IsOverridden && _settings.Current.PauseWhileGaming;

    public string BannerText => $"Downloads are paused while you play {GameName}. They continue automatically when you quit the game.";

    public void Start() => _timer.Change(TimeSpan.FromSeconds(2), PollInterval);

    /// <summary>"Download anyway": ignore the current game until it exits.</summary>
    public void Override()
    {
        _overriddenAppId = ReadRunningAppId();
        _dispatcher.Post(() => IsOverridden = true);
        _queue.ReleaseHold();
        _logger.LogInformation("Game mode overridden for app {AppId}", _overriddenAppId);
    }

    private void Poll()
    {
        try
        {
            var appId = ReadRunningAppId();
            var name = appId > 0 ? ReadGameName(appId) : null;
            if (appId == 0) _overriddenAppId = 0;
            var overridden = appId > 0 && appId == _overriddenAppId;
            var shouldHold = appId > 0 && !overridden && _settings.Current.PauseWhileGaming;

            if (shouldHold && _queue.HoldReason is null) _ = _queue.HoldAsync($"Playing {name}");
            else if (!shouldHold && _queue.HoldReason is not null) _queue.ReleaseHold();

            _dispatcher.Post(() =>
            {
                GameName = name;
                IsOverridden = overridden;
                OnPropertyChanged(nameof(IsHolding));
            });
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Game detection failed");
        }
    }

    /// <summary>
    /// Steam leaves RunningAppID (and Apps\id\Running) set when it is killed or crashes, so the value is only
    /// trusted while the Steam client is actually running.
    /// </summary>
    private static int ReadRunningAppId()
    {
        using var key = Registry.CurrentUser.OpenSubKey(SteamKey);
        if (key?.GetValue("RunningAppID") is not int id || id <= 0) return 0;
        return IsSteamRunning() ? id : 0;
    }

    private static bool IsSteamRunning()
    {
        var processes = System.Diagnostics.Process.GetProcessesByName("steam");
        try
        {
            return processes.Length > 0;
        }
        finally
        {
            foreach (var p in processes) p.Dispose();
        }
    }

    private static string ReadGameName(int appId)
    {
        using var key = Registry.CurrentUser.OpenSubKey($@"{SteamKey}\Apps\{appId}");
        return key?.GetValue("Name") as string is { Length: > 0 } name ? name : "a Steam game";
    }

    public void Dispose() => _timer.Dispose();
}
