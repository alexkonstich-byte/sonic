using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Sonic.Core.Abstractions;
using Sonic.Core.Helpers;
using Sonic.Core.Models;
using Sonic.Helpers;
using Sonic.Services;
using Windows.ApplicationModel.DataTransfer;

namespace Sonic.ViewModels;

public sealed class HistoryItemViewModel(DownloadItem item)
{
    public DownloadItem Model { get; } = item;
    public string Title => Model.Title ?? Model.Url;
    public string Subtitle => string.Join(" · ", new[] { Model.Artist, SourceDetector.GetDisplayName(Model.Source) }.Where(s => !string.IsNullOrWhiteSpace(s)));
    public string? ThumbnailUrl => Model.ThumbnailUrl;
    public string StatusText => Model.Status == DownloadStatus.Skipped ? "Skipped" : Format.StatusText(Model.Status);
    public string StatusGlyph => Glyphs.ForStatus(Model.Status);
    public bool IsCompleted => Model.Status == DownloadStatus.Completed;
    public bool IsFailed => Model.Status == DownloadStatus.Failed;
    public bool HasFile => !string.IsNullOrEmpty(Model.FilePath) && Model.Status is DownloadStatus.Completed or DownloadStatus.Skipped;
    public string DateText => Format.DateTimeShort(Model.CompletedAt ?? Model.CreatedAt);
    public string FormatText => Format.DownloadFormat(Model);
    public string SizeText => IsCompleted ? Format.Bytes(Model.TotalBytes) : string.Empty;
    public string? Error => Model.ErrorMessage;
    public string GroupKey
    {
        get
        {
            var date = (Model.CompletedAt ?? Model.CreatedAt).ToLocalTime().Date;
            if (date == DateTime.Today) return "Today";
            if (date == DateTime.Today.AddDays(-1)) return "Yesterday";
            if (date > DateTime.Today.AddDays(-7)) return "This week";
            return date.ToString("MMMM yyyy");
        }
    }
}

public sealed partial class HistoryGroup(string key, IEnumerable<HistoryItemViewModel> items) : List<HistoryItemViewModel>(items)
{
    public string Key { get; } = key;
}

public sealed partial class HistoryViewModel : ObservableObject, INavigationAware
{
    private readonly IDownloadQueueService _queue;
    private readonly IShellLauncher _launcher;
    private readonly IDialogService _dialogs;
    private readonly IInAppNotificationService _notifications;
    private readonly ILogger<HistoryViewModel> _logger;
    private List<HistoryItemViewModel> _all = [];
    private bool _isVisible;
    private bool _reloadPending;

    public HistoryViewModel(
        IDownloadQueueService queue,
        IShellLauncher launcher,
        IDialogService dialogs,
        IInAppNotificationService notifications,
        IUiDispatcher dispatcher,
        ILogger<HistoryViewModel> logger)
    {
        _queue = queue;
        _launcher = launcher;
        _dialogs = dialogs;
        _notifications = notifications;
        _logger = logger;
        queue.DownloadFinished += (_, _) => dispatcher.Post(() =>
        {
            if (_isVisible) _ = LoadAsync();
            else _reloadPending = true;
        });
    }

    public IReadOnlyList<string> Filters { get; } = ["All", "Completed", "Failed", "Canceled", "Skipped"];

    [ObservableProperty] private ObservableCollection<HistoryGroup> _groups = [];
    [ObservableProperty] private string _searchText = string.Empty;
    [ObservableProperty] private string _selectedFilter = "All";
    [ObservableProperty] private bool _isLoading;
    [ObservableProperty] private bool _isEmpty;
    [ObservableProperty] private string _summary = string.Empty;

    partial void OnSearchTextChanged(string value) => ApplyFilter();
    partial void OnSelectedFilterChanged(string value) => ApplyFilter();

    public async Task OnNavigatedToAsync(object? parameter)
    {
        _isVisible = true;
        if (_all.Count == 0 || _reloadPending) await LoadAsync();
    }

    public void OnNavigatedFrom() => _isVisible = false;

    [RelayCommand]
    private async Task LoadAsync()
    {
        _reloadPending = false;
        IsLoading = true;
        try
        {
            var items = await _queue.GetHistoryAsync();
            _all = items.Select(i => new HistoryItemViewModel(i)).ToList();
            ApplyFilter();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Loading history failed");
            _notifications.ShowError("Couldn't load history", ex.Message);
        }
        finally
        {
            IsLoading = false;
        }
    }

    private void ApplyFilter()
    {
        IEnumerable<HistoryItemViewModel> items = _all;
        items = SelectedFilter switch
        {
            "Completed" => items.Where(i => i.Model.Status == DownloadStatus.Completed),
            "Failed" => items.Where(i => i.Model.Status == DownloadStatus.Failed),
            "Canceled" => items.Where(i => i.Model.Status == DownloadStatus.Canceled),
            "Skipped" => items.Where(i => i.Model.Status == DownloadStatus.Skipped),
            _ => items,
        };
        if (!string.IsNullOrWhiteSpace(SearchText))
        {
            var q = SearchText.Trim();
            items = items.Where(i => i.Title.Contains(q, StringComparison.CurrentCultureIgnoreCase)
                                     || (i.Model.Artist?.Contains(q, StringComparison.CurrentCultureIgnoreCase) ?? false)
                                     || i.Model.Url.Contains(q, StringComparison.OrdinalIgnoreCase));
        }

        var list = items.ToList();
        Groups = new ObservableCollection<HistoryGroup>(list.GroupBy(i => i.GroupKey).Select(g => new HistoryGroup(g.Key, g)));
        IsEmpty = list.Count == 0;
        var completed = _all.Count(i => i.IsCompleted);
        var failed = _all.Count(i => i.IsFailed);
        Summary = $"{Format.Count(completed, "download")} completed" + (failed > 0 ? $" · {failed} failed" : string.Empty);
    }

    [RelayCommand]
    private void OpenFile(HistoryItemViewModel item)
    {
        if (item.Model.FilePath is { } p) _launcher.OpenFile(p);
    }

    [RelayCommand]
    private void OpenFolder(HistoryItemViewModel item)
    {
        if (item.Model.FilePath is { } p) _launcher.RevealInExplorer(p);
    }

    [RelayCommand]
    private void OpenSource(HistoryItemViewModel item) => _launcher.OpenUri(item.Model.Url);

    [RelayCommand]
    private void CopyLink(HistoryItemViewModel item)
    {
        var package = new DataPackage();
        package.SetText(item.Model.Url);
        Clipboard.SetContent(package);
        _notifications.ShowInfo("Link copied", item.Model.Url);
    }

    [RelayCommand]
    private async Task DownloadAgainAsync(HistoryItemViewModel item)
    {
        try
        {
            await _queue.EnqueueAsync(new DownloadRequest
            {
                Url = item.Model.Url,
                Kind = item.Model.Kind,
                Video = item.Model.Video,
                FilenameTemplate = item.Model.FilenameTemplate,
                OutputDirectory = item.Model.OutputDirectory,
                Title = item.Model.Title,
                Artist = item.Model.Artist,
                ThumbnailUrl = item.Model.ThumbnailUrl,
                DurationSeconds = item.Model.DurationSeconds,
                FormatId = item.Model.FormatId,
                Format = item.Model.Format,
                Quality = item.Model.Quality,
                Hints = item.Model.Hints,
                PlaylistEntryId = item.Model.PlaylistEntryId,
            });
            _notifications.ShowSuccess("Added to downloads", item.Title);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            await _dialogs.ShowErrorAsync("Couldn't add the download", ex.Message);
        }
    }

    [RelayCommand]
    private Task ShowError(HistoryItemViewModel item) =>
        _dialogs.ShowErrorAsync("Download failed", item.Model.ErrorMessage ?? "The download failed.", item.Model.ErrorDetails);

    [RelayCommand]
    private async Task RemoveAsync(HistoryItemViewModel item)
    {
        await _queue.DeleteHistoryAsync([item.Model.Id]);
        _all.Remove(item);
        ApplyFilter();
    }

    [RelayCommand]
    private async Task ClearAsync()
    {
        if (_all.Count == 0) return;
        if (!await _dialogs.ConfirmAsync("Clear download history?", "All finished, failed and canceled entries will be removed from History. Downloaded files are not affected.", "Clear history", destructive: true))
            return;
        await _queue.ClearHistoryAsync();
        await LoadAsync();
    }
}
