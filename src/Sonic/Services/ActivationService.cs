using Microsoft.Extensions.Logging;
using Sonic.Core.Abstractions;
using Sonic.Core.Helpers;
using Sonic.Core.Models;

namespace Sonic.Services;

/// <summary>An external request: from a toast button, the tray menu, the global hotkey or a sonic:// link.</summary>
public sealed record SonicCommand(string Action, string? Url, MediaKind Kind)
{
    /// <summary>
    /// Parses sonic://download?url=…&amp;kind=video, sonic://open?url=…, or a bare sonic://https://… link.
    /// </summary>
    public static SonicCommand? TryParse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        text = text.Trim().Trim('"');
        if (!text.StartsWith("sonic:", StringComparison.OrdinalIgnoreCase)) return null;
        var rest = text["sonic:".Length..].TrimStart('/');

        // Bare form: sonic://https://www.youtube.com/watch?v=…
        if (rest.StartsWith("http", StringComparison.OrdinalIgnoreCase))
            return new SonicCommand("open", Uri.UnescapeDataString(rest), MediaKind.Audio);

        var q = rest.IndexOf('?');
        var action = (q < 0 ? rest : rest[..q]).TrimEnd('/').ToLowerInvariant();
        var query = q < 0 ? string.Empty : rest[(q + 1)..];
        string? url = null;
        var kind = MediaKind.Audio;
        foreach (var pair in query.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var eq = pair.IndexOf('=');
            if (eq <= 0) continue;
            var key = pair[..eq].ToLowerInvariant();
            var value = Uri.UnescapeDataString(pair[(eq + 1)..].Replace('+', ' '));
            if (key == "url") url = value;
            else if (key == "kind" && value.Equals("video", StringComparison.OrdinalIgnoreCase)) kind = MediaKind.Video;
        }

        return action is "download" or "open" or "show" ? new SonicCommand(action, url, kind) : null;
    }
}

public interface IActivationService
{
    Task HandleAsync(SonicCommand command);

    /// <summary>Handles a command line / protocol activation string, if it contains a sonic:// link.</summary>
    Task HandleArgumentsAsync(string? arguments);

    /// <summary>Downloads the link currently on the clipboard (hotkey / tray).</summary>
    Task DownloadClipboardAsync(MediaKind kind);
}

public sealed class ActivationService(
    IDownloadQueueService queue,
    IWindowService windows,
    INavigationService navigation,
    IInAppNotificationService notifications,
    IToastService toasts,
    IUiDispatcher dispatcher,
    ILogger<ActivationService> logger) : IActivationService
{
    public Task HandleArgumentsAsync(string? arguments)
    {
        if (string.IsNullOrWhiteSpace(arguments)) return Task.CompletedTask;
        var token = arguments.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Select(t => t.Trim('"'))
            .FirstOrDefault(t => t.StartsWith("sonic:", StringComparison.OrdinalIgnoreCase));
        return SonicCommand.TryParse(token) is { } command ? HandleAsync(command) : Task.CompletedTask;
    }

    public Task HandleAsync(SonicCommand command) => dispatcher.RunAsync(async () =>
    {
        logger.LogInformation("Activation: {Action} {Kind} {Url}", command.Action, command.Kind, command.Url);
        switch (command.Action)
        {
            case "download" when command.Url is { } url:
                await DownloadAsync(url, command.Kind, showWindow: false);
                break;
            case "open" when command.Url is { } url:
                windows.BringToFront();
                navigation.NavigateTo(PageKey.Home, url);
                break;
            default:
                windows.BringToFront();
                break;
        }
    });

    public async Task DownloadClipboardAsync(MediaKind kind)
    {
        var text = await ClipboardText.TryGetAsync();
        if (text is null || !SourceDetector.TryNormalizeUrl(text, out var uri) || text.Contains('\n'))
        {
            toasts.ShowAlways("Nothing to download", "Copy a link first, then press the shortcut again.");
            return;
        }

        await DownloadAsync(uri.AbsoluteUri, kind, showWindow: false);
    }

    private async Task DownloadAsync(string url, MediaKind kind, bool showWindow)
    {
        if (SourceDetector.Detect(url) == SourcePlatform.Spotify)
        {
            // Spotify audio can't be downloaded; open the playlist importer instead.
            windows.BringToFront();
            navigation.NavigateTo(PageKey.Home, url);
            return;
        }

        try
        {
            var item = await queue.EnqueueAsync(new DownloadRequest { Url = url, Kind = kind });
            var what = kind == MediaKind.Video ? "Video" : "Audio";
            if (windows.IsForeground) notifications.ShowSuccess("Added to downloads", $"{what}: {url}");
            else toasts.ShowAlways($"{what} download started", url);
            if (showWindow) windows.BringToFront();
            _ = item;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            if (windows.IsForeground) notifications.ShowError("Couldn't add the download", ex.Message);
            else toasts.ShowAlways("Couldn't add the download", ex.Message);
        }
    }
}

/// <summary>Clipboard text helper (must run on the UI thread).</summary>
public static class ClipboardText
{
    public static async Task<string?> TryGetAsync()
    {
        try
        {
            var content = Windows.ApplicationModel.DataTransfer.Clipboard.GetContent();
            if (content.Contains(Windows.ApplicationModel.DataTransfer.StandardDataFormats.Text))
                return (await content.GetTextAsync())?.Trim();
            if (content.Contains(Windows.ApplicationModel.DataTransfer.StandardDataFormats.WebLink))
                return (await content.GetWebLinkAsync())?.AbsoluteUri;
        }
        catch (Exception)
        {
            // Another app may hold the clipboard open; try again on the next change.
        }

        return null;
    }
}
