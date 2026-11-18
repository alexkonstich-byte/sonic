using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml.Controls;
using Sonic.Core.Abstractions;
using Sonic.Core.Models;

namespace Sonic.Services;

/// <summary>
/// First-run and startup setup: installs missing tools (yt-dlp, FFmpeg, a JavaScript runtime) from their official
/// sources and keeps Sonic's own yt-dlp current. zapret is handled separately and only when YouTube is blocked.
/// </summary>
public interface IToolSetupService
{
    Task RunAsync();
}

public sealed class ToolSetupService(
    IDependencyService dependencies,
    ISettingsService settings,
    IInAppNotificationService notifications,
    IZapretAdvisor zapret,
    ILogger<ToolSetupService> logger) : IToolSetupService
{
    public async Task RunAsync()
    {
        if (settings.Current.AutoInstallTools)
        {
            foreach (var kind in new[] { ToolKind.YtDlp, ToolKind.Ffmpeg, ToolKind.Deno })
            {
                var status = dependencies.GetStatus(kind);
                var missing = status.State is ToolState.Missing or ToolState.Error;
                // yt-dlp breaks when sites change, so Sonic keeps the copy it manages up to date.
                var update = kind == ToolKind.YtDlp && status is { State: ToolState.UpdateAvailable, IsManaged: true };
                if (!missing && !update) continue;
                await InstallAsync(kind, status, update);
            }
        }
        else
        {
            ReportMissing();
        }

        await zapret.EnsureIfBlockedAsync();
    }

    private async Task InstallAsync(ToolKind kind, ToolStatus status, bool update)
    {
        var verb = update ? "Updating" : "Installing";
        notifications.Show(new InAppNotification
        {
            Title = $"{verb} {status.DisplayName}",
            Message = kind == ToolKind.Ffmpeg
                ? "Downloading FFmpeg from its official build site (about 100 MB). Downloads start as soon as it's ready."
                : $"Downloading {status.DisplayName} from its official release page.",
            Severity = InfoBarSeverity.Informational,
        }, TimeSpan.FromSeconds(8));
        try
        {
            await Task.Run(() => dependencies.InstallAsync(kind, null));
            var installed = dependencies.GetStatus(kind);
            notifications.ShowSuccess($"{installed.DisplayName} is ready", $"Version {installed.Version}.");
            logger.LogInformation("{Verb} {Tool} finished automatically", verb, kind);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Automatic {Verb} of {Tool} failed", verb, kind);
            notifications.Show(new InAppNotification
            {
                Title = $"Couldn't install {status.DisplayName}",
                Message = $"{ex.Message} You can retry from Settings › Integrations.",
                Severity = kind == ToolKind.Deno ? InfoBarSeverity.Warning : InfoBarSeverity.Error,
            }, TimeSpan.FromSeconds(20));
        }
    }

    private void ReportMissing()
    {
        var missing = new[] { ToolKind.YtDlp, ToolKind.Ffmpeg }
            .Select(dependencies.GetStatus).Where(s => !s.IsUsable).Select(s => s.DisplayName).ToList();
        if (missing.Count > 0)
            notifications.ShowWarning("Setup needed", $"{string.Join(" and ", missing)} {(missing.Count == 1 ? "is" : "are")} missing. Install from Settings › Integrations.");
    }
}
