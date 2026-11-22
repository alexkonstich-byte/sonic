using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Sonic.Core.Abstractions;
using Sonic.Core.Helpers;
using Sonic.Core.Models;
using Sonic.Core.Services;
using Sonic.Helpers;
using Sonic.Services;

namespace Sonic.ViewModels;

public sealed record PlaylistDownloadRow(string Title, string Artist, string Status);

/// <summary>
/// "Download all" for a playlist: scans the chosen folder for songs that already exist (by tags or file name,
/// with length checks), links those instead of downloading, and queues the rest.
/// </summary>
public sealed partial class PlaylistDownloadViewModel : ObservableObject
{
    private readonly IPlaylistDownloadService _service;
    private readonly ISettingsService _settings;
    private readonly IInAppNotificationService _notifications;
    private readonly ILogger<PlaylistDownloadViewModel> _logger;
    private CancellationTokenSource? _cts;
    private PlaylistDownloadPlan? _plan;
    private int _playlistId;

    public PlaylistDownloadViewModel(IPlaylistDownloadService service, ISettingsService settings, IInAppNotificationService notifications, ILogger<PlaylistDownloadViewModel> logger)
    {
        _service = service;
        _settings = settings;
        _notifications = notifications;
        _logger = logger;
        var s = settings.Current;
        _defaultFolder = s.DownloadDirectory;
        _saveToFolder = s.DownloadDirectory;
        _recentFolders = s.RecentFolders.ToList();
        _selectedFormat = Options.Find(Options.Formats, s.DefaultFormat);
        _selectedQuality = Options.Find(Options.Qualities, s.DefaultQuality);
        _numberByPosition = s.NumberPlaylistDownloads;
    }

    public IReadOnlyList<Option<AudioFormat>> FormatOptions => Options.Formats;
    public IReadOnlyList<Option<AudioQuality>> QualityOptions => Options.Qualities;
    public ObservableCollection<PlaylistDownloadRow> Rows { get; } = [];

    [ObservableProperty] private string _playlistName = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FolderError), nameof(HasFolderError), nameof(CanDownload))]
    private string _saveToFolder;

    [ObservableProperty] private string _defaultFolder;
    [ObservableProperty] private IReadOnlyList<string> _recentFolders;
    [ObservableProperty] private Option<AudioFormat> _selectedFormat;
    [ObservableProperty] private Option<AudioQuality> _selectedQuality;

    /// <summary>Name files "01 - Artist - Title" in playlist order.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(NumberingExample))]
    private bool _numberByPosition;

    public string NumberingExample
    {
        get
        {
            var template = _settings.Current.FilenameTemplate;
            return "For example: " + (NumberByPosition ? FileNameHelper.WithNumberPrefix(template, 1) : template)
                .Replace("{artist}", "Artist").Replace("{title}", "Title") + ".mp3";
        }
    }

    partial void OnNumberByPositionChanged(bool value) =>
        _ = _settings.UpdateAsync(s => s.NumberPlaylistDownloads = value);

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanDownload), nameof(IsIdle))]
    [NotifyCanExecuteChangedFor(nameof(ScanCommand))]
    private bool _isBusy;

    [ObservableProperty] private string _status = string.Empty;
    [ObservableProperty] private double _progress;
    [ObservableProperty] private bool _isProgressIndeterminate = true;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanDownload), nameof(DownloadButtonText))]
    private int _toDownloadCount;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanDownload), nameof(DownloadButtonText))]
    private int _toLinkCount;

    [ObservableProperty] private string _summary = string.Empty;

    public bool IsIdle => !IsBusy;
    public string? FolderError => FileNameHelper.ValidateDirectory(SaveToFolder);
    public bool HasFolderError => FolderError is not null;
    public bool CanDownload => !IsBusy && !HasFolderError && _plan is not null && ToDownloadCount + ToLinkCount > 0;

    public string DownloadButtonText => ToDownloadCount > 0
        ? $"Download {ToDownloadCount}"
        : ToLinkCount > 0 ? $"Link {ToLinkCount} files" : "Download";

    /// <summary>Number of songs queued when the dialog closes.</summary>
    public int QueuedCount { get; private set; }

    public void Initialize(int playlistId, string name)
    {
        _playlistId = playlistId;
        PlaylistName = name;
        _ = ScanAsync();
    }

    partial void OnSaveToFolderChanged(string value)
    {
        // A different folder needs a new scan.
        _plan = null;
        ToDownloadCount = 0;
        ToLinkCount = 0;
        Rows.Clear();
        Summary = HasFolderError ? string.Empty : "Select Scan folder to check which songs are already there.";
    }

    private bool CanScan() => !IsBusy;

    [RelayCommand(CanExecute = nameof(CanScan))]
    private async Task ScanAsync()
    {
        if (HasFolderError) return;
        _cts?.Cancel();
        _cts = new CancellationTokenSource();
        IsBusy = true;
        IsProgressIndeterminate = true;
        Status = "Scanning the folder…";
        try
        {
            var folder = SaveToFolder.Trim();
            var plan = await Task.Run(() => _service.AnalyzeAsync(_playlistId, folder, new Progress<string>(s => Status = s), _cts.Token));
            _plan = plan;
            ToDownloadCount = plan.ToDownload.Count;
            ToLinkCount = plan.FoundInFolder.Count;

            Rows.Clear();
            foreach (var e in plan.ToDownload) Rows.Add(new PlaylistDownloadRow(e.Title, e.Artist ?? string.Empty, "Will download"));
            foreach (var m in plan.FoundInFolder) Rows.Add(new PlaylistDownloadRow(m.Entry.Title, m.Entry.Artist ?? string.Empty, "Already downloaded: " + Path.GetFileName(m.FilePath)));
            foreach (var e in plan.AlreadyQueued) Rows.Add(new PlaylistDownloadRow(e.Title, e.Artist ?? string.Empty, "Already in the download queue"));
            foreach (var e in plan.InLibrary) Rows.Add(new PlaylistDownloadRow(e.Title, e.Artist ?? string.Empty, "In library"));

            var parts = new List<string> { $"{Format.Count(plan.ScannedFiles, "audio file")} scanned" };
            if (plan.InLibrary.Count > 0) parts.Add($"{plan.InLibrary.Count} already in the library");
            if (plan.FoundInFolder.Count > 0) parts.Add($"{plan.FoundInFolder.Count} already downloaded (linked, not downloaded again)");
            if (plan.AlreadyQueued.Count > 0) parts.Add($"{plan.AlreadyQueued.Count} already queued");
            parts.Add(plan.ToDownload.Count == 0 ? "nothing left to download" : $"{plan.ToDownload.Count} to download");
            Summary = string.Join(" · ", parts) + ".";
            Status = string.Empty;
        }
        catch (OperationCanceledException)
        {
            Status = string.Empty;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Playlist scan failed");
            Status = "Scan failed: " + ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>Links found files and queues the rest. Returns false to keep the dialog open.</summary>
    public async Task<bool> DownloadAsync()
    {
        if (_plan is not { } plan || !CanDownload) return false;
        _cts?.Cancel();
        _cts = new CancellationTokenSource();
        var token = _cts.Token;
        IsBusy = true;
        try
        {
            var linked = 0;
            if (plan.FoundInFolder.Count > 0)
            {
                Status = "Linking songs that are already in the folder…";
                linked = await Task.Run(() => _service.LinkFoundAsync(plan, token), token);
            }

            PlaylistQueueResult? result = null;
            if (plan.ToDownload.Count > 0)
            {
                IsProgressIndeterminate = false;
                Progress = 0;
                var total = plan.ToDownload.Count;
                Status = $"Finding songs on YouTube… 0 of {total}";
                var options = new PlaylistDownloadOptions(SaveToFolder.Trim(), SelectedFormat.Value, SelectedQuality.Value, _settings.Current.FilenameTemplate, NumberByPosition);
                var progress = new Progress<int>(n =>
                {
                    Progress = 100.0 * n / total;
                    Status = $"Finding songs on YouTube… {n} of {total}";
                });
                result = await Task.Run(() => _service.QueueMissingAsync(plan, options, progress, token), token);
                QueuedCount = result.Queued;
            }

            var message = new List<string>();
            if (result is not null) message.Add($"{Format.Count(result.Queued, "song")} queued");
            if (linked > 0) message.Add($"{linked} already downloaded (linked)");
            if (result is { NotFound.Count: > 0 } r)
            {
                var names = string.Join(", ", r.NotFound.Take(5).Select(e => $"{e.Artist} – {e.Title}"));
                message.Add($"{r.NotFound.Count} not found on YouTube ({names}{(r.NotFound.Count > 5 ? ", …" : string.Empty)}). Use Find source on those rows");
            }

            if (result is { Failed.Count: > 0 } f)
                message.Add($"{f.Failed.Count} couldn't be searched (check your connection or zapret) — run Download all again to retry them");

            _notifications.ShowSuccess($"\"{PlaylistName}\"", string.Join(" · ", message) + ".");
            return true;
        }
        catch (OperationCanceledException)
        {
            Status = "Stopped. Songs found so far are in the download queue.";
            return false;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Playlist download failed");
            Status = "Something went wrong: " + ex.Message;
            return false;
        }
        finally
        {
            IsBusy = false;
            IsProgressIndeterminate = true;
        }
    }

    public void Cancel() => _cts?.Cancel();
}
