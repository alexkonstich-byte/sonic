using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Windows.AppNotifications;
using Microsoft.Windows.AppNotifications.Builder;
using Sonic.Core.Abstractions;
using Sonic.Core.Models;

namespace Sonic.Services;

public sealed record ToastButton(string Text, SonicCommand Command);

/// <summary>Windows toast notifications. Buttons carry a <see cref="SonicCommand"/> that runs when clicked.</summary>
public interface IToastService
{
    void Initialize();

    /// <summary>Shows a toast if notifications are enabled in Settings.</summary>
    void Show(string title, string message);

    /// <summary>Shows a toast for something the user just triggered (hotkey, tray), regardless of the setting.</summary>
    void ShowAlways(string title, string message);

    void ShowWithButtons(string title, string message, IReadOnlyList<ToastButton> buttons);

    void Shutdown();
}

public sealed class ToastService(ISettingsService settings, IServiceProvider services, ILogger<ToastService> logger) : IToastService
{
    private bool _registered;

    public void Initialize()
    {
        try
        {
            AppNotificationManager.Default.NotificationInvoked += OnInvoked;
            AppNotificationManager.Default.Register();
            _registered = true;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Toast notifications are unavailable");
        }
    }

    private void OnInvoked(AppNotificationManager sender, AppNotificationActivatedEventArgs args)
    {
        var activation = services.GetRequiredService<IActivationService>();
        var a = args.Arguments;
        if (a.TryGetValue("action", out var action))
        {
            a.TryGetValue("url", out var url);
            var kind = a.TryGetValue("kind", out var k) && k == "video" ? MediaKind.Video : MediaKind.Audio;
            _ = activation.HandleAsync(new SonicCommand(action, url, kind));
        }
        else
        {
            _ = activation.HandleAsync(new SonicCommand("show", null, MediaKind.Audio));
        }
    }

    public void Show(string title, string message)
    {
        if (settings.Current.ShowNotifications) ShowAlways(title, message);
    }

    public void ShowAlways(string title, string message) => ShowCore(new AppNotificationBuilder().AddText(title).AddText(message));

    public void ShowWithButtons(string title, string message, IReadOnlyList<ToastButton> buttons)
    {
        var builder = new AppNotificationBuilder().AddText(title).AddText(message);
        foreach (var b in buttons)
        {
            var button = new AppNotificationButton(b.Text)
                .AddArgument("action", b.Command.Action)
                .AddArgument("kind", b.Command.Kind == MediaKind.Video ? "video" : "audio");
            if (b.Command.Url is { } url) button.AddArgument("url", url);
            builder.AddButton(button);
        }

        ShowCore(builder);
    }

    private void ShowCore(AppNotificationBuilder builder)
    {
        if (!_registered) return;
        try
        {
            AppNotificationManager.Default.Show(builder.BuildNotification());
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not show toast");
        }
    }

    public void Shutdown()
    {
        if (!_registered) return;
        try
        {
            AppNotificationManager.Default.Unregister();
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Toast unregister failed");
        }
    }
}
