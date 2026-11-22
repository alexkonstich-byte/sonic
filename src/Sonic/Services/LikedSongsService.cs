using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Sonic.Core.Abstractions;
using Sonic.Core.Services;
using Sonic.Helpers;

namespace Sonic.Services;

/// <summary>
/// Spotify sign-in and the background Liked Songs sync: every 30 minutes (and shortly after start) newly liked
/// songs are found on YouTube and downloaded; songs already on disk are linked instead.
/// </summary>
public sealed partial class LikedSongsService : ObservableObject
{
    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(30);

    private readonly ISpotifyService _spotify;
    private readonly ILikedSongsSyncService _sync;
    private readonly ISettingsService _settings;
    private readonly IShellLauncher _launcher;
    private readonly IUiDispatcher _dispatcher;
    private readonly IInAppNotificationService _notifications;
    private readonly IToastService _toasts;
    private readonly ILogger<LikedSongsService> _logger;
    private Timer? _timer;
    private CancellationTokenSource? _signInCts;

    public LikedSongsService(ISpotifyService spotify, ILikedSongsSyncService sync, ISettingsService settings, IShellLauncher launcher,
        IUiDispatcher dispatcher, IInAppNotificationService notifications, IToastService toasts, ILogger<LikedSongsService> logger)
    {
        _spotify = spotify;
        _sync = sync;
        _settings = settings;
        _launcher = launcher;
        _dispatcher = dispatcher;
        _notifications = notifications;
        _toasts = toasts;
        _logger = logger;
        settings.Changed += (_, _) => dispatcher.Post(RaiseAccount);
        UpdateStatus();
    }

    public string RedirectUri => SpotifyService.RedirectUri;
    public bool IsSignedIn => _spotify.IsSignedIn;
    public bool IsSignedOut => !IsSignedIn;
    public string AccountText => IsSignedIn ? $"Signed in as {_spotify.SignedInUser}." : "Not signed in.";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SyncNowCommand), nameof(SignInCommand))]
    private bool _isBusy;

    [ObservableProperty] private string _status = string.Empty;

    private void RaiseAccount()
    {
        OnPropertyChanged(nameof(IsSignedIn));
        OnPropertyChanged(nameof(IsSignedOut));
        OnPropertyChanged(nameof(AccountText));
        SyncNowCommand.NotifyCanExecuteChanged();
        if (!IsBusy) UpdateStatus();
    }

    private void UpdateStatus()
    {
        var s = _settings.Current;
        var last = s.SpotifyLikedLastSync is { } t ? $" Last checked {Format.RelativeTime(t)}." : string.Empty;
        Status = !IsSignedIn ? "Sign in to Spotify to download your liked songs."
            : !s.SpotifyLikedSync ? "Off." + last
            : (s.SpotifyLikedSyncFrom is { } from ? $"Downloading songs you like from {from.ToLocalTime():d MMM yyyy, HH:mm} on." : "Downloading all your liked songs.")
              + " Checked every 30 minutes." + last;
    }

    public void Start()
    {
        _timer = new Timer(_ => _dispatcher.Post(() =>
        {
            if (_settings.Current.SpotifyLikedSync && IsSignedIn) _ = SyncAsync(userInitiated: false);
        }), null, TimeSpan.FromMinutes(1), Interval);
    }

    private bool CanSignIn() => !IsBusy;

    [RelayCommand(CanExecute = nameof(CanSignIn))]
    private async Task SignInAsync()
    {
        _signInCts?.Cancel();
        _signInCts = new CancellationTokenSource();
        IsBusy = true;
        Status = "Finish signing in in your browser…";
        try
        {
            await Task.Run(() => _spotify.SignInAsync(uri =>
            {
                _dispatcher.Post(() => _launcher.OpenUri(uri.AbsoluteUri));
                return Task.CompletedTask;
            }, _signInCts.Token));
            _notifications.ShowSuccess("Connected to Spotify", AccountText);
        }
        catch (SpotifyException ex)
        {
            _notifications.ShowWarning("Spotify sign-in", ex.Message);
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            IsBusy = false;
            RaiseAccount();
        }
    }

    [RelayCommand]
    private void CancelSignIn() => _signInCts?.Cancel();

    [RelayCommand]
    private async Task SignOutAsync()
    {
        await _spotify.SignOutAsync();
        await _settings.UpdateAsync(s => s.SpotifyLikedSync = false);
        RaiseAccount();
    }

    private bool CanSync() => IsSignedIn && !IsBusy;

    [RelayCommand(CanExecute = nameof(CanSync))]
    private Task SyncNow() => SyncAsync(userInitiated: true);

    public async Task SyncAsync(bool userInitiated)
    {
        if (IsBusy || _sync.IsRunning) return;
        IsBusy = true;
        Status = "Checking your liked songs…";
        try
        {
            var result = await Task.Run(() => _sync.SyncAsync(force: userInitiated));
            if (result.Queued > 0)
            {
                var text = $"{Format.Count(result.Queued, "liked song")} added to downloads.";
                _notifications.ShowSuccess("Liked Songs", text);
                if (!userInitiated) _toasts.Show("Liked Songs", text);
            }
            if (result.Failed > 0 && userInitiated)
            {
                _notifications.ShowWarning("Liked Songs",
                    $"{result.Failed} song(s) couldn't be searched on YouTube (check your connection or zapret). Sonic tries them again on the next check.");
            }
            else if (result.Queued == 0 && userInitiated)
            {
                _notifications.ShowInfo("Liked Songs", result.NotFound > 0
                    ? $"Nothing new to download. {result.NotFound} song(s) weren't found on YouTube; use Find source in the Liked Songs playlist."
                    : "Everything you liked is already downloaded.");
            }
        }
        catch (SpotifyException ex)
        {
            _logger.LogWarning(ex, "Liked songs sync failed");
            if (userInitiated) _notifications.ShowWarning("Liked Songs", ex.Message);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Liked songs sync failed");
            if (userInitiated) _notifications.ShowError("Liked Songs", ex.Message);
        }
        finally
        {
            IsBusy = false;
            UpdateStatus();
        }
    }
}
