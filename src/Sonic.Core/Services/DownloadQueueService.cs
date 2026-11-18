using Microsoft.Extensions.Logging;
using Sonic.Core.Abstractions;
using Sonic.Core.Helpers;
using Sonic.Core.Models;
using Sonic.Core.Repositories;

namespace Sonic.Core.Services;

/// <summary>
/// Persistent download queue with a configurable concurrency limit, pause/resume, automatic retries for
/// transient failures and recovery of downloads interrupted by a shutdown or crash.
/// </summary>
public sealed class DownloadQueueService : IDownloadQueueService
{
    private static readonly TimeSpan ProgressThrottle = TimeSpan.FromMilliseconds(150);

    private readonly IDownloadPipeline _pipeline;
    private readonly IDownloadRepository _repository;
    private readonly ISettingsService _settings;
    private readonly ILogger<DownloadQueueService> _logger;
    private readonly Lock _lock = new();
    private readonly List<DownloadItem> _items = [];
    private readonly Dictionary<Guid, RunningDownload> _running = [];
    private readonly Dictionary<Guid, DateTime> _retryAfter = [];
    private readonly Dictionary<Guid, (DateTime At, DownloadStatus Status, string? Stage)> _lastProgress = [];
    private bool _initialized;
    private bool _shuttingDown;

    private enum StopReason { None, Cancel, Pause, Remove, Shutdown, Hold }

    private string? _holdReason;

    public string? HoldReason => _holdReason;

    public event EventHandler? HoldChanged;

    public async Task HoldAsync(string reason)
    {
        List<RunningDownload> stopping;
        lock (_lock)
        {
            if (_holdReason == reason) return;
            _holdReason = reason;
            stopping = _running.Values.ToList();
            foreach (var run in stopping)
            {
                run.Reason = StopReason.Hold;
                run.Cts.Cancel();
            }
        }

        _logger.LogInformation("Downloads on hold: {Reason}", reason);
        HoldChanged?.Invoke(this, EventArgs.Empty);
        await Task.WhenAll(stopping.Select(r => r.Task)).ConfigureAwait(false);
    }

    public void ReleaseHold()
    {
        lock (_lock)
        {
            if (_holdReason is null) return;
            _holdReason = null;
        }

        _logger.LogInformation("Downloads resumed after hold");
        HoldChanged?.Invoke(this, EventArgs.Empty);
        Pump();
    }

    public async Task<DownloadItem?> FindPreviousDownloadAsync(string url, CancellationToken cancellationToken = default)
    {
        var key = SourceDetector.CanonicalKey(url);
        var history = await _repository.GetHistoryAsync(cancellationToken).ConfigureAwait(false);
        return history.FirstOrDefault(d => d.Status == DownloadStatus.Completed && SourceDetector.CanonicalKey(d.Url) == key);
    }

    public async Task<int> RetryFailedAsync(Func<DownloadItem, bool> filter)
    {
        List<DownloadItem> failed;
        lock (_lock) failed = _items.Where(i => i.Status == DownloadStatus.Failed && filter(i)).ToList();
        foreach (var item in failed) await RetryAsync(item.Id).ConfigureAwait(false);
        return failed.Count;
    }

    private sealed class RunningDownload(CancellationTokenSource cts)
    {
        public CancellationTokenSource Cts { get; } = cts;
        public StopReason Reason { get; set; }
        public Task Task { get; set; } = Task.CompletedTask;
    }

    public DownloadQueueService(
        IDownloadPipeline pipeline,
        IDownloadRepository repository,
        ISettingsService settings,
        ILogger<DownloadQueueService> logger)
    {
        _pipeline = pipeline;
        _repository = repository;
        _settings = settings;
        _logger = logger;
        _settings.Changed += (_, _) => Pump();
    }

    public event EventHandler<DownloadChangedEventArgs>? Changed;
    public event EventHandler<DownloadItem>? DownloadFinished;

    public IReadOnlyList<DownloadItem> Items
    {
        get { lock (_lock) return _items.ToList(); }
    }

    public int ActiveCount
    {
        get { lock (_lock) return _items.Count(i => i.Status == DownloadStatus.Queued || i.Status.IsActive()); }
    }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        var stored = await _repository.GetQueueAsync(cancellationToken).ConfigureAwait(false);
        var resume = _settings.Current.ResumeDownloadsOnStartup;
        var recovered = new List<DownloadItem>();
        foreach (var item in stored)
        {
            if (item.Status.IsActive() || item.Status == DownloadStatus.Queued)
            {
                // Interrupted by shutdown/crash: partial data in the temp folder lets yt-dlp continue.
                item.Status = resume ? DownloadStatus.Queued : DownloadStatus.Paused;
                item.Progress = 0;
                recovered.Add(item);
            }
        }

        lock (_lock)
        {
            _items.Clear();
            _items.AddRange(stored);
            _initialized = true;
        }

        foreach (var item in recovered) await PersistAsync(item).ConfigureAwait(false);
        if (recovered.Count > 0) _logger.LogInformation("Recovered {Count} interrupted downloads", recovered.Count);
        foreach (var item in stored) Raise(item, DownloadChangeKind.Added);
        Pump();
    }

    public async Task<DownloadItem> EnqueueAsync(DownloadRequest request, CancellationToken cancellationToken = default)
    {
        if (!SourceDetector.TryNormalizeUrl(request.Url, out var uri))
            throw new ArgumentException("Enter a valid web address.");
        if (SourceDetector.Detect(uri.AbsoluteUri) == SourcePlatform.Spotify || SourceDetector.IsProtectedStreamingService(uri.AbsoluteUri))
            throw new InvalidOperationException("Audio from DRM-protected streaming services can't be downloaded. Use the service's official offline mode instead.");

        var s = _settings.Current;
        var isVideo = request.Kind == MediaKind.Video;
        var outputDirectory = request.OutputDirectory ?? (isVideo ? s.VideoDirectory : s.DownloadDirectory);
        if (FileNameHelper.ValidateDirectory(outputDirectory) is { } error) throw new ArgumentException(error);
        var template = request.FilenameTemplate ?? (isVideo ? s.VideoFilenameTemplate : s.FilenameTemplate);
        if (FileNameHelper.ValidateTemplate(template, request.Kind) is { } templateError) throw new ArgumentException("File name: " + templateError);

        var item = new DownloadItem
        {
            Url = uri.AbsoluteUri,
            Kind = request.Kind,
            Video = isVideo ? (request.Video ?? s.CreateDefaultVideoOptions()).Clone() : null,
            Source = SourceDetector.Detect(uri.AbsoluteUri),
            Title = request.Title,
            Artist = request.Artist,
            ThumbnailUrl = request.ThumbnailUrl,
            DurationSeconds = request.DurationSeconds,
            FormatId = request.FormatId,
            Format = request.Format ?? s.DefaultFormat,
            Quality = request.Quality ?? s.DefaultQuality,
            OutputDirectory = outputDirectory,
            FilenameTemplate = template.Trim(),
            Hints = request.Hints,
            PlaylistEntryId = request.PlaylistEntryId,
            Status = DownloadStatus.Queued,
            StageText = "Queued",
        };

        await _repository.UpsertAsync(item, cancellationToken).ConfigureAwait(false);
        lock (_lock) _items.Add(item);
        await RememberFolderAsync(outputDirectory, isVideo ? s.VideoDirectory : s.DownloadDirectory).ConfigureAwait(false);
        if (isVideo) _logger.LogInformation("Queued video {Url} as {Container} ≤{Height}p", item.Url, item.Video!.Container, item.Video.MaxHeight);
        else _logger.LogInformation("Queued {Url} as {Format}/{Quality}", item.Url, item.Format, item.Quality);
        Raise(item, DownloadChangeKind.Added);
        Pump();
        return item;
    }

    /// <summary>Custom "Save to" folders are remembered for quick reuse.</summary>
    private async Task RememberFolderAsync(string folder, string defaultFolder)
    {
        if (string.Equals(Path.TrimEndingDirectorySeparator(folder), Path.TrimEndingDirectorySeparator(defaultFolder), StringComparison.OrdinalIgnoreCase)) return;
        var recent = _settings.Current.RecentFolders;
        if (recent.Count > 0 && string.Equals(recent[0], folder, StringComparison.OrdinalIgnoreCase)) return;
        try
        {
            await _settings.UpdateAsync(s =>
            {
                s.RecentFolders.RemoveAll(f => string.Equals(f, folder, StringComparison.OrdinalIgnoreCase));
                s.RecentFolders.Insert(0, folder);
            }).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not remember folder {Folder}", folder);
        }
    }

    private void Pump()
    {
        lock (_lock)
        {
            if (!_initialized || _shuttingDown || _holdReason is not null) return;
            var max = _settings.Current.MaxConcurrentDownloads;
            var now = DateTime.UtcNow;
            DateTime? nextRetry = null;
            while (_running.Count < max)
            {
                DownloadItem? next = null;
                foreach (var candidate in _items)
                {
                    if (candidate.Status != DownloadStatus.Queued || _running.ContainsKey(candidate.Id)) continue;
                    if (_retryAfter.TryGetValue(candidate.Id, out var after) && after > now)
                    {
                        nextRetry = nextRetry is null || after < nextRetry ? after : nextRetry;
                        continue;
                    }

                    next = candidate;
                    break;
                }

                if (next is null) break;
                Start(next);
            }

            if (nextRetry is { } when)
            {
                var delay = when - now + TimeSpan.FromMilliseconds(100);
                _ = Task.Delay(delay).ContinueWith(_ => Pump(), TaskScheduler.Default);
            }
        }
    }

    // Called with _lock held.
    private void Start(DownloadItem item)
    {
        _retryAfter.Remove(item.Id);
        var run = new RunningDownload(new CancellationTokenSource());
        _running[item.Id] = run;
        item.Status = DownloadStatus.Downloading;
        item.StageText = "Starting";
        item.StartedAt = DateTime.UtcNow;
        item.CompletedAt = null;
        item.Attempts++;
        item.Progress = 0;
        item.ErrorMessage = null;
        item.ErrorDetails = null;
        run.Task = Task.Run(() => RunAsync(item, run));
    }

    private async Task RunAsync(DownloadItem item, RunningDownload run)
    {
        var finished = false;
        try
        {
            await PersistAsync(item).ConfigureAwait(false);
            Raise(item, DownloadChangeKind.Updated);
            await _pipeline.ExecuteAsync(item, OnProgress, run.Cts.Token).ConfigureAwait(false);
            if (item.Status != DownloadStatus.Skipped) item.Status = DownloadStatus.Completed;
            item.Progress = 100;
            item.StageText = null;
            item.CompletedAt = DateTime.UtcNow;
            finished = true;
        }
        catch (OperationCanceledException) when (run.Cts.IsCancellationRequested)
        {
            switch (run.Reason)
            {
                case StopReason.Pause:
                    item.Status = DownloadStatus.Paused;
                    item.StageText = "Paused";
                    break;
                case StopReason.Shutdown:
                    item.Status = DownloadStatus.Queued; // Resumes on next launch.
                    item.StageText = "Interrupted";
                    break;
                case StopReason.Hold:
                    item.Status = DownloadStatus.Queued; // Continues from the partial data when the hold ends.
                    item.StageText = "On hold";
                    item.Attempts = Math.Max(0, item.Attempts - 1);
                    break;
                default:
                    item.Status = DownloadStatus.Canceled;
                    item.StageText = null;
                    item.CompletedAt = DateTime.UtcNow;
                    _pipeline.DeleteWorkingData(item.Id);
                    break;
            }
        }
        catch (Exception ex)
        {
            var (message, details, retryable) = Describe(ex);
            var maxRetries = _settings.Current.MaxRetries;
            if (retryable && item.Attempts <= maxRetries && !_shuttingDown)
            {
                var delay = TimeSpan.FromSeconds(5 * item.Attempts);
                _logger.LogWarning(ex, "Download failed (attempt {Attempt}), retrying in {Delay}s: {Url}", item.Attempts, delay.TotalSeconds, item.Url);
                item.Status = DownloadStatus.Queued;
                item.StageText = $"Retrying in {delay.TotalSeconds:0}s";
                item.ErrorMessage = message;
                lock (_lock) _retryAfter[item.Id] = DateTime.UtcNow + delay;
            }
            else
            {
                _logger.LogError(ex, "Download failed: {Url}", item.Url);
                item.Status = DownloadStatus.Failed;
                item.StageText = null;
                item.ErrorMessage = message;
                item.ErrorDetails = details;
                item.CompletedAt = DateTime.UtcNow;
                finished = true;
            }
        }
        finally
        {
            item.SpeedBytesPerSecond = null;
            item.EtaSeconds = null;
            bool removed;
            lock (_lock)
            {
                _running.Remove(item.Id);
                _lastProgress.Remove(item.Id);
                removed = run.Reason == StopReason.Remove;
            }

            run.Cts.Dispose();
            if (!removed)
            {
                await PersistAsync(item).ConfigureAwait(false);
                Raise(item, DownloadChangeKind.Updated);
                if (finished) DownloadFinished?.Invoke(this, item);
            }

            Pump();
        }
    }

    private static (string Message, string? Details, bool Retryable) Describe(Exception ex) => ex switch
    {
        YtDlpException y => (y.Message, y.Details, y.IsRetryable),
        FfmpegException f => (f.Message, f.Details, false),
        DependencyMissingException d => (d.Message, null, false),
        HttpRequestException h => ("A network error occurred: " + h.Message, h.ToString(), true),
        UnauthorizedAccessException u => ("Sonic doesn't have permission to write the file. Check the download folder.", u.ToString(), false),
        IOException io when io.HResult == unchecked((int)0x80070070) => ("The disk is full.", io.ToString(), false),
        IOException io => ("A file error occurred: " + io.Message, io.ToString(), true),
        InvalidOperationException op => (op.Message, op.ToString(), false),
        _ => ("An unexpected error occurred: " + ex.Message, ex.ToString(), false),
    };

    private void OnProgress(DownloadItem item)
    {
        var now = DateTime.UtcNow;
        lock (_lock)
        {
            // Throttle byte-level progress, but never drop a stage change (it may not be followed by another update).
            if (_lastProgress.TryGetValue(item.Id, out var last) && now - last.At < ProgressThrottle &&
                last.Status == item.Status && last.Stage == item.StageText)
                return;
            _lastProgress[item.Id] = (now, item.Status, item.StageText);
        }

        Raise(item, DownloadChangeKind.Progress);
    }

    public Task CancelAsync(Guid id) => StopAsync(id, StopReason.Cancel);

    public Task PauseAsync(Guid id) => StopAsync(id, StopReason.Pause);

    private async Task StopAsync(Guid id, StopReason reason)
    {
        DownloadItem? item;
        RunningDownload? run;
        lock (_lock)
        {
            item = _items.FirstOrDefault(i => i.Id == id);
            if (item is null) return;
            if (_running.TryGetValue(id, out run))
            {
                run.Reason = reason;
                run.Cts.Cancel();
            }
        }

        if (run is not null)
        {
            await run.Task.ConfigureAwait(false);
            return;
        }

        if (reason == StopReason.Pause && item.Status == DownloadStatus.Queued)
        {
            item.Status = DownloadStatus.Paused;
            item.StageText = "Paused";
        }
        else if (reason == StopReason.Cancel && item.Status is DownloadStatus.Queued or DownloadStatus.Paused)
        {
            item.Status = DownloadStatus.Canceled;
            item.StageText = null;
            item.CompletedAt = DateTime.UtcNow;
            _pipeline.DeleteWorkingData(item.Id);
        }
        else
        {
            return;
        }

        lock (_lock) _retryAfter.Remove(id);
        await PersistAsync(item).ConfigureAwait(false);
        Raise(item, DownloadChangeKind.Updated);
    }

    public async Task ResumeAsync(Guid id)
    {
        var item = Find(id);
        if (item is not { Status: DownloadStatus.Paused }) return;
        item.Status = DownloadStatus.Queued;
        item.StageText = "Queued";
        await PersistAsync(item).ConfigureAwait(false);
        Raise(item, DownloadChangeKind.Updated);
        Pump();
    }

    public async Task RetryAsync(Guid id)
    {
        var item = Find(id);
        if (item is not { Status: DownloadStatus.Failed or DownloadStatus.Canceled }) return;
        item.Status = DownloadStatus.Queued;
        item.StageText = "Queued";
        item.Attempts = 0;
        item.Progress = 0;
        item.ErrorMessage = null;
        item.ErrorDetails = null;
        item.CompletedAt = null;
        lock (_lock) _retryAfter.Remove(id);
        await PersistAsync(item).ConfigureAwait(false);
        Raise(item, DownloadChangeKind.Updated);
        Pump();
    }

    public async Task RemoveAsync(Guid id)
    {
        RunningDownload? run;
        DownloadItem? item;
        lock (_lock)
        {
            item = _items.FirstOrDefault(i => i.Id == id);
            if (item is null) return;
            if (_running.TryGetValue(id, out run))
            {
                run.Reason = StopReason.Remove;
                run.Cts.Cancel();
            }
        }

        if (run is not null) await run.Task.ConfigureAwait(false);

        if (!item.Status.IsTerminal())
        {
            item.Status = DownloadStatus.Canceled;
            item.CompletedAt = DateTime.UtcNow;
            _pipeline.DeleteWorkingData(id);
        }

        item.IsDismissed = true;
        lock (_lock)
        {
            _items.Remove(item);
            _retryAfter.Remove(id);
        }

        await PersistAsync(item).ConfigureAwait(false);
        Raise(item, DownloadChangeKind.Removed);
    }

    public async Task ClearFinishedAsync()
    {
        List<DownloadItem> finished;
        lock (_lock) finished = _items.Where(i => i.Status.IsTerminal()).ToList();
        foreach (var item in finished) await RemoveAsync(item.Id).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<DownloadItem>> GetHistoryAsync(CancellationToken cancellationToken = default) =>
        await _repository.GetHistoryAsync(cancellationToken).ConfigureAwait(false);

    public async Task DeleteHistoryAsync(IReadOnlyCollection<Guid> ids)
    {
        await _repository.DeleteAsync(ids).ConfigureAwait(false);
        List<DownloadItem> removed;
        lock (_lock)
        {
            removed = _items.Where(i => ids.Contains(i.Id) && i.Status.IsTerminal()).ToList();
            foreach (var r in removed) _items.Remove(r);
        }

        foreach (var r in removed) Raise(r, DownloadChangeKind.Removed);
    }

    public async Task ClearHistoryAsync()
    {
        await _repository.DeleteFinishedHistoryAsync().ConfigureAwait(false);
        List<DownloadItem> removed;
        lock (_lock)
        {
            removed = _items.Where(i => i.Status.IsTerminal()).ToList();
            foreach (var r in removed) _items.Remove(r);
        }

        foreach (var r in removed) Raise(r, DownloadChangeKind.Removed);
    }

    public async Task ShutdownAsync()
    {
        List<Task> tasks;
        lock (_lock)
        {
            if (_shuttingDown) return;
            _shuttingDown = true;
            foreach (var run in _running.Values)
            {
                run.Reason = StopReason.Shutdown;
                run.Cts.Cancel();
            }

            tasks = _running.Values.Select(r => r.Task).ToList();
        }

        if (tasks.Count == 0) return;
        _logger.LogInformation("Stopping {Count} running downloads for shutdown", tasks.Count);
        await Task.WhenAny(Task.WhenAll(tasks), Task.Delay(TimeSpan.FromSeconds(8))).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync() => await ShutdownAsync().ConfigureAwait(false);

    private DownloadItem? Find(Guid id)
    {
        lock (_lock) return _items.FirstOrDefault(i => i.Id == id);
    }

    private async Task PersistAsync(DownloadItem item)
    {
        try
        {
            await _repository.UpsertAsync(item).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Could not persist download {Id}", item.Id);
        }
    }

    private void Raise(DownloadItem item, DownloadChangeKind kind)
    {
        try
        {
            Changed?.Invoke(this, new DownloadChangedEventArgs(item, kind));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Download change handler failed");
        }
    }
}
