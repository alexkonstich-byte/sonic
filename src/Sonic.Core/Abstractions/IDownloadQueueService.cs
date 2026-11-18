using Sonic.Core.Models;

namespace Sonic.Core.Abstractions;

public interface IDownloadQueueService : IAsyncDisposable
{
    /// <summary>Raised on a background thread whenever an item changes.</summary>
    event EventHandler<DownloadChangedEventArgs>? Changed;

    /// <summary>Raised on a background thread when a download completes, fails or is skipped.</summary>
    event EventHandler<DownloadItem>? DownloadFinished;

    IReadOnlyList<DownloadItem> Items { get; }

    int ActiveCount { get; }

    /// <summary>Loads persisted items and recovers downloads interrupted by a previous shutdown.</summary>
    Task InitializeAsync(CancellationToken cancellationToken = default);

    Task<DownloadItem> EnqueueAsync(DownloadRequest request, CancellationToken cancellationToken = default);

    Task CancelAsync(Guid id);

    Task PauseAsync(Guid id);

    Task ResumeAsync(Guid id);

    Task RetryAsync(Guid id);

    /// <summary>Removes the item from the queue view (cancelling it first if needed). History is kept.</summary>
    Task RemoveAsync(Guid id);

    Task ClearFinishedAsync();

    Task<IReadOnlyList<DownloadItem>> GetHistoryAsync(CancellationToken cancellationToken = default);

    Task DeleteHistoryAsync(IReadOnlyCollection<Guid> ids);

    Task ClearHistoryAsync();

    /// <summary>Stops running downloads, keeping partial data so they resume on next launch.</summary>
    Task ShutdownAsync();

    /// <summary>Why downloads are on hold (e.g. a game is running), or null when they run normally.</summary>
    string? HoldReason { get; }

    event EventHandler? HoldChanged;

    /// <summary>
    /// Holds the queue: running downloads stop (keeping partial data) and go back to waiting; nothing starts until
    /// <see cref="ReleaseHold"/>. Their state is persisted as queued, so they also resume after a restart.
    /// </summary>
    Task HoldAsync(string reason);

    void ReleaseHold();

    /// <summary>The most recent completed download of the same media (URL variants are matched), if any.</summary>
    Task<DownloadItem?> FindPreviousDownloadAsync(string url, CancellationToken cancellationToken = default);

    /// <summary>Retries failed items in the queue that match <paramref name="filter"/>. Returns how many were retried.</summary>
    Task<int> RetryFailedAsync(Func<DownloadItem, bool> filter);
}

public interface IDownloadPipeline
{
    /// <summary>Executes a download end-to-end (download, convert, tag, move, import). Updates <paramref name="item"/> in place.</summary>
    Task ExecuteAsync(DownloadItem item, Action<DownloadItem> onProgress, CancellationToken cancellationToken);

    /// <summary>Deletes partial data kept for resuming the download.</summary>
    void DeleteWorkingData(Guid id);
}
