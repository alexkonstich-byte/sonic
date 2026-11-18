using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Sonic.Core.Abstractions;
using Sonic.Core.Helpers;
using Sonic.Core.Models;
using Sonic.Helpers;
using Sonic.Services;

namespace Sonic.ViewModels;

/// <summary>Bindable projection of a <see cref="DownloadItem"/>. Refreshed on the UI thread by <see cref="DownloadsStore"/>.</summary>
public sealed partial class DownloadItemViewModel : ObservableObject
{
    private readonly IDownloadQueueService _queue;
    private readonly IShellLauncher _launcher;
    private readonly IDialogService _dialogs;
    private readonly IInAppNotificationService _notifications;

    public DownloadItemViewModel(DownloadItem model, IDownloadQueueService queue, IShellLauncher launcher, IDialogService dialogs, IInAppNotificationService notifications)
    {
        Model = model;
        _queue = queue;
        _launcher = launcher;
        _dialogs = dialogs;
        _notifications = notifications;
        Refresh();
    }

    public DownloadItem Model { get; }
    public Guid Id => Model.Id;

    [ObservableProperty] private string _title = string.Empty;
    [ObservableProperty] private string _subtitle = string.Empty;
    [ObservableProperty] private string? _thumbnailUrl;
    [ObservableProperty] private DownloadStatus _status;
    [ObservableProperty] private double _progress;
    [ObservableProperty] private bool _isIndeterminate;
    [ObservableProperty] private string _statusText = string.Empty;
    [ObservableProperty] private string _detailText = string.Empty;
    [ObservableProperty] private string _sizeText = string.Empty;
    [ObservableProperty] private string? _errorMessage;
    [ObservableProperty] private bool _isActive;
    [ObservableProperty] private bool _isRunning;
    [ObservableProperty] private bool _canPause;
    [ObservableProperty] private bool _canResume;
    [ObservableProperty] private bool _canRetry;
    [ObservableProperty] private bool _canCancel;
    [ObservableProperty] private bool _hasFile;
    [ObservableProperty] private bool _isFailed;
    [ObservableProperty] private bool _isCompleted;
    [ObservableProperty] private bool _showProgress;

    public string SourceName => SourceDetector.GetDisplayName(Model.Source);
    public string SourceGlyph => Model.Kind == MediaKind.Video ? Glyphs.Video : Glyphs.ForSource(Model.Source);
    public string FormatText => Format.DownloadFormat(Model);
    public string StatusGlyph => Glyphs.ForStatus(Status);

    public void Refresh()
    {
        var m = Model;
        Title = m.Title ?? m.Url;
        Subtitle = string.Join(" · ", new[] { m.Artist, SourceName }.Where(s => !string.IsNullOrWhiteSpace(s)));
        ThumbnailUrl = m.ThumbnailUrl;
        Status = m.Status;
        Progress = m.Progress;
        ErrorMessage = m.ErrorMessage;

        IsRunning = m.Status.IsActive();
        IsActive = IsRunning || m.Status == DownloadStatus.Queued;
        IsFailed = m.Status == DownloadStatus.Failed;
        IsCompleted = m.Status is DownloadStatus.Completed or DownloadStatus.Skipped;
        CanPause = m.Status is DownloadStatus.Queued or DownloadStatus.Downloading;
        CanResume = m.Status == DownloadStatus.Paused;
        CanRetry = m.Status is DownloadStatus.Failed or DownloadStatus.Canceled;
        CanCancel = IsActive || m.Status == DownloadStatus.Paused;
        HasFile = IsCompleted && !string.IsNullOrEmpty(m.FilePath);
        ShowProgress = IsRunning || m.Status == DownloadStatus.Paused;
        // Indeterminate until yt-dlp reports a size (or while FFmpeg doesn't know the duration).
        IsIndeterminate = IsRunning && m.Progress <= 0;

        StatusText = m.Status switch
        {
            DownloadStatus.Downloading or DownloadStatus.Converting or DownloadStatus.Tagging when m.Progress > 0 =>
                $"{m.StageText ?? Format.StatusText(m.Status)} · {m.Progress:0}%",
            DownloadStatus.Downloading or DownloadStatus.Converting or DownloadStatus.Tagging => m.StageText ?? Format.StatusText(m.Status),
            DownloadStatus.Queued when m.StageText is { } stage && stage.StartsWith("Retrying", StringComparison.Ordinal) => stage,
            DownloadStatus.Queued when m.StageText == "Interrupted" => "Waiting to resume",
            DownloadStatus.Skipped => "Already in library",
            _ => Format.StatusText(m.Status),
        };

        DetailText = m.Status == DownloadStatus.Downloading
            ? string.Join("  ·  ", new[]
            {
                m.TotalBytes is > 0 ? $"{Format.Bytes(m.DownloadedBytes)} of {Format.Bytes(m.TotalBytes)}" : null,
                Format.Speed(m.SpeedBytesPerSecond),
                Format.Eta(m.EtaSeconds),
            }.Where(s => !string.IsNullOrEmpty(s)))
            : m.Status switch
            {
                DownloadStatus.Completed => $"{FormatText} · {Format.Bytes(m.TotalBytes)} · {Format.RelativeTime(m.CompletedAt ?? m.CreatedAt)}",
                DownloadStatus.Failed => m.ErrorMessage ?? "The download failed.",
                DownloadStatus.Queued when m.ErrorMessage is not null => m.ErrorMessage,
                _ => FormatText,
            };

        SizeText = Format.Bytes(m.TotalBytes);
        OnPropertyChanged(nameof(StatusGlyph));
    }

    [RelayCommand]
    private Task Cancel() => Guard(() => _queue.CancelAsync(Id));

    [RelayCommand]
    private Task Pause() => Guard(() => _queue.PauseAsync(Id));

    [RelayCommand]
    private Task Resume() => Guard(() => _queue.ResumeAsync(Id));

    [RelayCommand]
    private Task Retry() => Guard(() => _queue.RetryAsync(Id));

    [RelayCommand]
    private Task Remove() => Guard(() => _queue.RemoveAsync(Id));

    [RelayCommand]
    private void OpenFile()
    {
        if (Model.FilePath is { } path) _launcher.OpenFile(path);
    }

    [RelayCommand]
    private void OpenFolder()
    {
        if (Model.FilePath is { } path) _launcher.RevealInExplorer(path);
        else _launcher.OpenFolder(Model.OutputDirectory);
    }

    [RelayCommand]
    private void OpenSource() => _launcher.OpenUri(Model.Url);

    [RelayCommand]
    private Task ShowError() =>
        _dialogs.ShowErrorAsync("Download failed", Model.ErrorMessage ?? "The download failed.", Model.ErrorDetails);

    private async Task Guard(Func<Task> action)
    {
        try
        {
            await action();
        }
        catch (Exception ex)
        {
            _notifications.ShowError("Couldn't update the download", ex.Message);
        }
    }
}
