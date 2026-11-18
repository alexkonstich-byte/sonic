using Microsoft.UI.Dispatching;

namespace Sonic.Services;

/// <summary>Marshals work onto the UI thread. Core services raise events on background threads.</summary>
public interface IUiDispatcher
{
    bool HasThreadAccess { get; }

    void Post(Action action);

    Task RunAsync(Func<Task> action);
}

public sealed class UiDispatcher(DispatcherQueue queue) : IUiDispatcher
{
    public bool HasThreadAccess => queue.HasThreadAccess;

    public void Post(Action action)
    {
        if (queue.HasThreadAccess) action();
        else queue.TryEnqueue(() => action());
    }

    public Task RunAsync(Func<Task> action)
    {
        if (queue.HasThreadAccess) return action();
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var queued = queue.TryEnqueue(async () =>
        {
            try
            {
                await action();
                tcs.TrySetResult();
            }
            catch (Exception ex)
            {
                tcs.TrySetException(ex);
            }
        });
        if (!queued) tcs.TrySetCanceled();
        return tcs.Task;
    }
}
