using System.Net.NetworkInformation;
using Microsoft.Extensions.Logging;
using Sonic.Core.Abstractions;
using Sonic.Core.Helpers;
using Sonic.Core.Models;
using Sonic.Core.Services;

namespace Sonic.Services;

/// <summary>
/// Retries downloads that failed because of the network once the connection is back, and YouTube downloads
/// right after zapret starts.
/// </summary>
public sealed class AutoRetryService(
    IDownloadQueueService queue,
    IZapretService zapret,
    ISettingsService settings,
    IInAppNotificationService notifications,
    ILogger<AutoRetryService> logger)
{
    private bool _zapretWasRunning;

    public void Start()
    {
        NetworkChange.NetworkAvailabilityChanged += (_, e) =>
        {
            if (e.IsAvailable) _ = RetryAsync("Connection restored", _ => true, TimeSpan.FromSeconds(5));
        };
        _zapretWasRunning = zapret.GetStatus().Running;
        zapret.StatusChanged += (_, _) =>
        {
            var running = zapret.GetStatus().Running;
            if (running && !_zapretWasRunning)
                _ = RetryAsync("zapret started", i => SourceDetector.Detect(i.Url) is SourcePlatform.YouTube or SourcePlatform.YouTubeMusic, TimeSpan.FromSeconds(2));
            _zapretWasRunning = running;
        };
    }

    private async Task RetryAsync(string reason, Func<DownloadItem, bool> scope, TimeSpan delay)
    {
        if (!settings.Current.AutoRetryOnReconnect) return;
        await Task.Delay(delay); // let the connection settle
        var cutoff = DateTime.UtcNow.AddHours(-6);
        var count = await queue.RetryFailedAsync(i =>
            (i.CompletedAt ?? i.CreatedAt) > cutoff && zapret.LooksLikeBlocking(i.ErrorMessage) && scope(i));
        if (count == 0) return;
        logger.LogInformation("{Reason}: retrying {Count} failed downloads", reason, count);
        notifications.ShowInfo(reason, $"Retrying {Format(count)}.");
    }

    private static string Format(int n) => n == 1 ? "1 failed download" : $"{n} failed downloads";
}
