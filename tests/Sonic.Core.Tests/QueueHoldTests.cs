using Microsoft.Extensions.DependencyInjection;
using Sonic.Core.Abstractions;
using Sonic.Core.Data;
using Sonic.Core.Models;
using Sonic.Core.Services;

namespace Sonic.Core.Tests;

public sealed class QueueHoldTests : IAsyncLifetime
{
    /// <summary>A pipeline that "downloads" until it's cancelled, counting how often it started.</summary>
    private sealed class BlockingPipeline : IDownloadPipeline
    {
        public int Starts;
        public TaskCompletionSource Started = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task ExecuteAsync(DownloadItem item, Action<DownloadItem> onProgress, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref Starts);
            Started.TrySetResult();
            await Task.Delay(Timeout.Infinite, cancellationToken);
        }

        public void DeleteWorkingData(Guid id) { }
    }

    private readonly string _root = Directory.CreateTempSubdirectory("sonic-hold").FullName;
    private readonly BlockingPipeline _pipeline = new();
    private ServiceProvider _sp = null!;

    public async Task InitializeAsync()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSonicCore("Sonic-Tests/1.0");
        services.AddSingleton<IAppPaths>(new AppPaths(_root));
        services.AddSingleton<IDownloadPipeline>(_pipeline);
        _sp = services.BuildServiceProvider();
        await _sp.GetRequiredService<ISettingsService>().LoadAsync();
        await _sp.GetRequiredService<IDatabaseInitializer>().InitializeAsync();
    }

    public async Task DisposeAsync()
    {
        await _sp.GetRequiredService<IDownloadQueueService>().ShutdownAsync();
        await _sp.DisposeAsync();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_root, true); } catch (IOException) { }
    }

    [Fact]
    public async Task Hold_requeues_running_downloads_and_release_resumes_them()
    {
        var queue = _sp.GetRequiredService<IDownloadQueueService>();
        await queue.InitializeAsync();
        var item = await queue.EnqueueAsync(new DownloadRequest { Url = "https://example.org/a" });
        await _pipeline.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(DownloadStatus.Downloading, queue.Items.Single().Status);

        await queue.HoldAsync("Playing a game");
        Assert.Equal("Playing a game", queue.HoldReason);
        var held = queue.Items.Single(i => i.Id == item.Id);
        Assert.Equal(DownloadStatus.Queued, held.Status);
        Assert.Equal("On hold", held.StageText);

        // New downloads wait too.
        await queue.EnqueueAsync(new DownloadRequest { Url = "https://example.org/b" });
        await Task.Delay(300);
        Assert.Equal(1, _pipeline.Starts);
        Assert.All(queue.Items, i => Assert.Equal(DownloadStatus.Queued, i.Status));

        _pipeline.Started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        queue.ReleaseHold();
        await _pipeline.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Null(queue.HoldReason);
        Assert.Contains(queue.Items, i => i.Status == DownloadStatus.Downloading);
    }
}
