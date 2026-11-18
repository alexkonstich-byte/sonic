using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Sonic.Core.Abstractions;
using Sonic.Core.Models;
using Sonic.Services;

namespace Sonic.ViewModels;

/// <summary>
/// Single UI-thread mirror of the download queue shared by the shell, Home and Downloads pages.
/// </summary>
public sealed partial class DownloadsStore : ObservableObject
{
    private readonly IDownloadQueueService _queue;
    private readonly IShellLauncher _launcher;
    private readonly IDialogService _dialogs;
    private readonly IInAppNotificationService _notifications;
    private readonly Dictionary<Guid, DownloadItemViewModel> _byId = [];

    public DownloadsStore(
        IDownloadQueueService queue,
        IUiDispatcher dispatcher,
        IShellLauncher launcher,
        IDialogService dialogs,
        IInAppNotificationService notifications)
    {
        _queue = queue;
        _launcher = launcher;
        _dialogs = dialogs;
        _notifications = notifications;
        queue.Changed += (_, e) => dispatcher.Post(() => Apply(e));
    }

    /// <summary>All queue items, newest first.</summary>
    public ObservableCollection<DownloadItemViewModel> Items { get; } = [];

    /// <summary>Queued and running items only.</summary>
    public ObservableCollection<DownloadItemViewModel> ActiveItems { get; } = [];

    [ObservableProperty] private int _activeCount;
    [ObservableProperty] private int _runningCount;
    [ObservableProperty] private int _finishedCount;
    [ObservableProperty] private int _failedCount;

    private void Apply(DownloadChangedEventArgs e)
    {
        switch (e.Kind)
        {
            case DownloadChangeKind.Added:
                if (_byId.ContainsKey(e.Item.Id)) break;
                var vm = new DownloadItemViewModel(e.Item, _queue, _launcher, _dialogs, _notifications);
                _byId[e.Item.Id] = vm;
                // Items arrive oldest-first when the queue loads, so inserting at 0 keeps newest on top.
                Items.Insert(0, vm);
                break;

            case DownloadChangeKind.Updated:
            case DownloadChangeKind.Progress:
                if (_byId.TryGetValue(e.Item.Id, out var existing)) existing.Refresh();
                break;

            case DownloadChangeKind.Removed:
                if (_byId.Remove(e.Item.Id, out var removed))
                {
                    Items.Remove(removed);
                    ActiveItems.Remove(removed);
                }

                break;
        }

        if (e.Kind != DownloadChangeKind.Progress) SyncActive();
    }

    private void SyncActive()
    {
        // Keep ActiveItems in the same order as Items.
        var desired = Items.Where(i => i.IsActive).ToList();
        for (var i = ActiveItems.Count - 1; i >= 0; i--)
        {
            if (!desired.Contains(ActiveItems[i])) ActiveItems.RemoveAt(i);
        }

        for (var i = 0; i < desired.Count; i++)
        {
            var current = ActiveItems.IndexOf(desired[i]);
            if (current < 0) ActiveItems.Insert(i, desired[i]);
            else if (current != i) ActiveItems.Move(current, i);
        }

        ActiveCount = desired.Count;
        RunningCount = Items.Count(i => i.IsRunning);
        FinishedCount = Items.Count(i => i.Status is DownloadStatus.Completed or DownloadStatus.Skipped or DownloadStatus.Canceled or DownloadStatus.Failed);
        FailedCount = Items.Count(i => i.IsFailed);
    }
}
