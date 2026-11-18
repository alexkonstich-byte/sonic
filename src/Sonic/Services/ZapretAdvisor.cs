using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml.Controls;
using Sonic.Core.Abstractions;
using Sonic.Core.Helpers;
using Sonic.Core.Models;
using Sonic.Core.Services;

namespace Sonic.Services;

/// <summary>Suggests (or auto-starts) zapret when YouTube fails in a way that looks like provider blocking.</summary>
public interface IZapretAdvisor
{
    /// <summary>Shows a "Start zapret" suggestion if the error looks like blocking and zapret is installed but stopped.</summary>
    bool MaybeSuggest(string? error, string? url);

    Task AutoStartAsync();

    Task StartWithSavedStrategyAsync();

    /// <summary>
    /// Checks YouTube; only if it's blocked while the internet works, downloads zapret (when missing) and offers to
    /// start it — or starts it when "Start with Sonic" is on.
    /// </summary>
    Task EnsureIfBlockedAsync();
}

public sealed class ZapretAdvisor(
    IZapretService zapret,
    ISettingsService settings,
    IInAppNotificationService notifications,
    IToastService toasts,
    IWindowService windows,
    INavigationService navigation,
    ILogger<ZapretAdvisor> logger) : IZapretAdvisor
{
    private DateTime _lastSuggestion = DateTime.MinValue;
    private int _ensuring;

    public async Task EnsureIfBlockedAsync()
    {
        if (Interlocked.Exchange(ref _ensuring, 1) == 1) return;
        try
        {
            var status = zapret.GetStatus();
            if (status.Running) return;
            if (!status.Installed && !settings.Current.ZapretAutoInstall) return;
            if (status.Installed && !settings.Current.ZapretAutoStart && !settings.Current.ZapretSuggest) return;

            var reach = await Task.Run(() => zapret.CheckYouTubeAsync());
            logger.LogInformation("YouTube reachability: {Details}", reach.Details);
            if (!reach.LooksBlocked) return; // YouTube works (or the whole internet is down): nothing to do.

            if (!status.Installed)
            {
                notifications.Show(new InAppNotification
                {
                    Title = "YouTube seems to be blocked",
                    Message = "The internet works but YouTube doesn't. Downloading zapret from its official GitHub page…",
                    Severity = InfoBarSeverity.Warning,
                }, TimeSpan.FromSeconds(10));
                await Task.Run(() => zapret.InstallAsync(null));
            }

            if (settings.Current.ZapretAutoStart)
            {
                await StartWithSavedStrategyAsync();
                return;
            }

            _lastSuggestion = DateTime.UtcNow;
            notifications.Show(new InAppNotification
            {
                Title = "zapret is ready",
                Message = "YouTube looks blocked by your provider. Start zapret to get around it (Windows will ask for administrator approval).",
                Severity = InfoBarSeverity.Warning,
                ActionText = "Start zapret",
                ActionCommand = new AsyncRelayCommand(StartWithSavedStrategyAsync),
            }, TimeSpan.FromSeconds(30));
            if (!windows.IsForeground) toasts.ShowAlways("YouTube seems to be blocked", "Open Sonic to start zapret.");
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "zapret setup failed");
            notifications.ShowError("zapret", ex.Message);
        }
        finally
        {
            Interlocked.Exchange(ref _ensuring, 0);
        }
    }

    public bool MaybeSuggest(string? error, string? url)
    {
        if (!settings.Current.ZapretSuggest || !zapret.LooksLikeBlocking(error)) return false;
        var source = SourceDetector.Detect(url);
        if (url is not null && source is not (SourcePlatform.YouTube or SourcePlatform.YouTubeMusic)) return false;
        var status = zapret.GetStatus();
        if (status.Running) return false;
        if (!status.Installed)
        {
            // Not installed yet: check whether YouTube is really blocked and fetch zapret only then.
            if (settings.Current.ZapretAutoInstall) _ = EnsureIfBlockedAsync();
            return false;
        }
        if (DateTime.UtcNow - _lastSuggestion < TimeSpan.FromMinutes(2)) return true; // don't nag after every failed item
        _lastSuggestion = DateTime.UtcNow;

        if (windows.IsForeground)
        {
            notifications.Show(new InAppNotification
            {
                Title = "YouTube may be blocked",
                Message = "Downloads from YouTube are failing with network errors. zapret is installed but not running.",
                Severity = InfoBarSeverity.Warning,
                ActionText = "Start zapret",
                ActionCommand = new AsyncRelayCommand(StartWithSavedStrategyAsync),
            }, TimeSpan.FromSeconds(20));
        }
        else
        {
            toasts.ShowAlways("YouTube may be blocked", "Open Sonic › Settings › Integrations › zapret to start it.");
        }

        return true;
    }

    public async Task StartWithSavedStrategyAsync()
    {
        try
        {
            var status = zapret.GetStatus();
            var strategy = status.Strategies.FirstOrDefault(s => s.Equals(settings.Current.ZapretStrategy, StringComparison.OrdinalIgnoreCase))
                           ?? status.Strategies.FirstOrDefault();
            if (strategy is null)
            {
                navigation.NavigateTo(PageKey.Settings, "zapret");
                return;
            }

            await zapret.StartAsync(strategy);
            notifications.ShowSuccess("zapret started", $"Strategy {strategy}. Retry the failed downloads; if it still fails, try another strategy in Settings.");
        }
        catch (Exception ex) when (ex is OperationCanceledException or InvalidOperationException)
        {
            notifications.ShowError("zapret", ex.Message);
        }
    }

    public async Task AutoStartAsync()
    {
        if (!settings.Current.ZapretAutoStart) return;
        var status = zapret.GetStatus();
        if (!status.Installed || status.Running) return;
        logger.LogInformation("Auto-starting zapret");
        await StartWithSavedStrategyAsync();
    }
}
