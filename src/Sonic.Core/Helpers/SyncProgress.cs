namespace Sonic.Core.Helpers;

/// <summary>
/// An <see cref="IProgress{T}"/> that invokes the handler synchronously on the reporting thread.
/// Unlike <see cref="Progress{T}"/> it preserves ordering when no synchronization context exists.
/// </summary>
public sealed class SyncProgress<T>(Action<T> handler) : IProgress<T>
{
    public void Report(T value) => handler(value);
}
