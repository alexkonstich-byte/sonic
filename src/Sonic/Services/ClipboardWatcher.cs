using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml.Controls;
using Sonic.Core.Abstractions;
using Sonic.Core.Helpers;
using Sonic.Core.Models;
using Windows.ApplicationModel.DataTransfer;

namespace Sonic.Services;

/// <summary>
/// Offers to download links the user copies anywhere: a toast with Audio/Video buttons while Sonic is in the
/// background, or a notification with an Analyze action while it's in front.
/// </summary>
public interface IClipboardWatcher
{
    void Start();

    /// <summary>Tell the watcher Sonic itself is about to copy this text (e.g. "Copy link"), so it isn't offered back.</summary>
    void IgnoreNext(string text);
}

public sealed class ClipboardWatcher(
    ISettingsService settings,
    IWindowService windows,
    IToastService toasts,
    IInAppNotificationService notifications,
    INavigationService navigation,
    IUiDispatcher dispatcher,
    ILogger<ClipboardWatcher> logger) : IClipboardWatcher
{
    /// <summary>Only sites where a copied link almost certainly means "media" are offered, to avoid noise.</summary>
    private static readonly HashSet<SourcePlatform> Offered =
    [
        SourcePlatform.YouTube, SourcePlatform.YouTubeMusic, SourcePlatform.VK, SourcePlatform.Rutube,
        SourcePlatform.SoundCloud, SourcePlatform.Bandcamp, SourcePlatform.Vimeo,
    ];

    private string? _lastOffered;
    private string? _ignore;
    private DateTime _lastChange;

    public void Start()
    {
        try
        {
            Clipboard.ContentChanged += OnChanged;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Clipboard monitoring is unavailable");
        }
    }

    public void IgnoreNext(string text) => _ignore = text.Trim();

    private void OnChanged(object? sender, object e)
    {
        // Some apps fire several change events for one copy.
        var now = DateTime.UtcNow;
        if (now - _lastChange < TimeSpan.FromMilliseconds(250)) return;
        _lastChange = now;
        dispatcher.Post(() => _ = CheckAsync());
    }

    private async Task CheckAsync()
    {
        if (!settings.Current.WatchClipboard) return;
        var text = await ClipboardText.TryGetAsync();
        if (string.IsNullOrEmpty(text) || text.Length > 2048 || text.Contains('\n') || text.Contains(' ')) return;
        if (text == _ignore)
        {
            _ignore = null;
            return;
        }

        if (!SourceDetector.TryNormalizeUrl(text, out var uri) || !text.StartsWith("http", StringComparison.OrdinalIgnoreCase)) return;
        var url = uri.AbsoluteUri;
        if (url == _lastOffered || !Offered.Contains(SourceDetector.Detect(url))) return;
        _lastOffered = url;
        var site = SourceDetector.GetDisplayName(SourceDetector.Detect(url));
        logger.LogInformation("Offering copied {Site} link ({Mode})", site, windows.IsForeground ? "in-app" : "toast");

        if (windows.IsForeground)
        {
            notifications.Show(new InAppNotification
            {
                Title = $"{site} link copied",
                Message = url,
                Severity = InfoBarSeverity.Informational,
                ActionText = "Analyze",
                ActionCommand = new RelayCommand(() => navigation.NavigateTo(PageKey.Home, url)),
            }, TimeSpan.FromSeconds(10));
        }
        else
        {
            toasts.ShowWithButtons($"Download from {site}?", url,
            [
                new ToastButton("Audio", new SonicCommand("download", url, MediaKind.Audio)),
                new ToastButton("Video", new SonicCommand("download", url, MediaKind.Video)),
                new ToastButton("Options…", new SonicCommand("open", url, MediaKind.Audio)),
            ]);
        }
    }
}
