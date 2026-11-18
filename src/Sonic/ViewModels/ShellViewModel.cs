using System.Collections.ObjectModel;
using System.Reflection;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Sonic.Services;

namespace Sonic.ViewModels;

public sealed partial class ShellViewModel(
    ToolsStore tools,
    DownloadsStore downloads,
    INavigationService navigation,
    IInAppNotificationService notifications) : ObservableObject
{
    public ToolsStore Tools { get; } = tools;
    public DownloadsStore Downloads { get; } = downloads;
    public ObservableCollection<InAppNotification> Notifications => notifications.Items;

    public GameModeService GameMode { get; } = App.GetService<GameModeService>();

    public UpdateService Updates { get; } = App.GetService<UpdateService>();

    public static string AppVersion { get; } =
        "Version " + (Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0]
                      ?? Assembly.GetExecutingAssembly().GetName().Version?.ToString(3));

    public static string ActiveDownloadsText(int count) => count switch
    {
        0 => "No active downloads",
        1 => "1 active download",
        _ => $"{count} active downloads",
    };

    [RelayCommand]
    private void OpenIntegrations() => navigation.NavigateTo(PageKey.Settings, "integrations");

    [RelayCommand]
    private void OpenDownloads() => navigation.NavigateTo(PageKey.Downloads);

    [RelayCommand]
    private void DismissNotification(InAppNotification notification) => notifications.Dismiss(notification);
}
