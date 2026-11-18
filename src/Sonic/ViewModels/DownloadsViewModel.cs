using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Sonic.Core.Abstractions;
using Sonic.Core.Models;
using Sonic.Services;

namespace Sonic.ViewModels;

public sealed partial class DownloadsViewModel(
    DownloadsStore store,
    IDownloadQueueService queue,
    ISettingsService settings,
    IShellLauncher launcher,
    IDialogService dialogs,
    INavigationService navigation,
    IInAppNotificationService notifications) : ObservableObject
{
    public DownloadsStore Store { get; } = store;

    public GameModeService GameMode { get; } = App.GetService<GameModeService>();

    [RelayCommand]
    private void DownloadAnyway() => GameMode.Override();

    [RelayCommand]
    private Task NotAGame() => GameMode.IgnoreCurrentAppAsync();

    public string ConcurrencyText => $"Up to {settings.Current.MaxConcurrentDownloads} at a time";

    public void Refresh() => OnPropertyChanged(nameof(ConcurrencyText));

    [RelayCommand]
    private async Task PauseAllAsync()
    {
        foreach (var item in Store.Items.Where(i => i.CanPause).ToList()) await queue.PauseAsync(item.Id);
    }

    [RelayCommand]
    private async Task ResumeAllAsync()
    {
        foreach (var item in Store.Items.Where(i => i.CanResume).ToList()) await queue.ResumeAsync(item.Id);
    }

    [RelayCommand]
    private async Task RetryFailedAsync()
    {
        foreach (var item in Store.Items.Where(i => i.Status == DownloadStatus.Failed).ToList()) await queue.RetryAsync(item.Id);
    }

    [RelayCommand]
    private async Task CancelAllAsync()
    {
        var active = Store.Items.Where(i => i.CanCancel).ToList();
        if (active.Count == 0) return;
        if (!await dialogs.ConfirmAsync("Cancel all downloads?", $"{active.Count} unfinished downloads will be canceled and their partial data deleted.", "Cancel downloads", destructive: true, closeText: "Keep downloading"))
            return;
        foreach (var item in active) await queue.CancelAsync(item.Id);
    }

    [RelayCommand]
    private async Task ClearFinishedAsync()
    {
        await queue.ClearFinishedAsync();
        notifications.ShowInfo("Cleared", "Finished downloads were removed from the list. They remain in History.");
    }

    [RelayCommand]
    private void OpenDownloadFolder()
    {
        Directory.CreateDirectory(settings.Current.DownloadDirectory);
        launcher.OpenFolder(settings.Current.DownloadDirectory);
    }

    [RelayCommand]
    private void GoHome() => navigation.NavigateTo(PageKey.Home);

    [RelayCommand]
    private void OpenSettings() => navigation.NavigateTo(PageKey.Settings, "downloads");
}
