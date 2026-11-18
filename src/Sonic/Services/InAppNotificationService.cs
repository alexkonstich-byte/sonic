using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.UI.Xaml.Controls;

namespace Sonic.Services;

public sealed class InAppNotification
{
    public required string Title { get; init; }
    public required string Message { get; init; }
    public InfoBarSeverity Severity { get; init; }
    public string? ActionText { get; init; }
    public IRelayCommand? ActionCommand { get; init; }
    public bool HasAction => ActionCommand is not null;
}

/// <summary>Non-blocking messages shown as InfoBars at the top of the window.</summary>
public interface IInAppNotificationService
{
    ObservableCollection<InAppNotification> Items { get; }
    void Show(InAppNotification notification, TimeSpan? autoDismiss = null);
    void ShowInfo(string title, string message);
    void ShowSuccess(string title, string message);
    void ShowWarning(string title, string message);
    void ShowError(string title, string message);
    void Dismiss(InAppNotification notification);
}

public sealed class InAppNotificationService(IUiDispatcher dispatcher) : IInAppNotificationService
{
    private const int MaxVisible = 3;

    public ObservableCollection<InAppNotification> Items { get; } = [];

    public void Show(InAppNotification notification, TimeSpan? autoDismiss = null)
    {
        dispatcher.Post(() =>
        {
            // Collapse identical consecutive messages.
            if (Items.Any(i => i.Title == notification.Title && i.Message == notification.Message)) return;
            Items.Insert(0, notification);
            while (Items.Count > MaxVisible) Items.RemoveAt(Items.Count - 1);
        });

        var delay = autoDismiss ?? (notification.Severity == InfoBarSeverity.Error ? TimeSpan.FromSeconds(15) : TimeSpan.FromSeconds(6));
        _ = Task.Delay(delay).ContinueWith(_ => Dismiss(notification), TaskScheduler.Default);
    }

    public void ShowInfo(string title, string message) => Show(new InAppNotification { Title = title, Message = message, Severity = InfoBarSeverity.Informational });

    public void ShowSuccess(string title, string message) => Show(new InAppNotification { Title = title, Message = message, Severity = InfoBarSeverity.Success });

    public void ShowWarning(string title, string message) => Show(new InAppNotification { Title = title, Message = message, Severity = InfoBarSeverity.Warning });

    public void ShowError(string title, string message) => Show(new InAppNotification { Title = title, Message = message, Severity = InfoBarSeverity.Error });

    public void Dismiss(InAppNotification notification) => dispatcher.Post(() => Items.Remove(notification));
}
