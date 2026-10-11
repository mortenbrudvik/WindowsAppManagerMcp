using System.Collections.Concurrent;

namespace WindowsAppManagerMcp.Tests.Fixtures;

/// <summary>
/// An <see cref="IProgress{T}"/> that records every report on the reporting thread, in order. Unlike
/// <see cref="Progress{T}"/>, it does not post reports to a synchronization context or the thread pool, so a test can
/// check them as soon as the operation returns.
/// </summary>
public sealed class RecordingProgress<T> : IProgress<T>
{
    private readonly ConcurrentQueue<T> _reports = new();

    public IReadOnlyList<T> Reports => _reports.ToList();

    public void Report(T value) => _reports.Enqueue(value);
}
