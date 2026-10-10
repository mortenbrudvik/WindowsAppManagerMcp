namespace WindowsAppManagerMcp.Services.Interfaces;

/// <summary>
/// Runs UI Automation reads with a hard deadline. A UI Automation call blocks until the target
/// process answers, and an application that hangs (or a provider stuck in FromPoint) can block it
/// far longer than any tool timeout. The worker runs the read on its own thread and stops waiting
/// when the deadline passes, so the tool call still returns.
/// </summary>
public interface IUiaWorker
{
    /// <summary>
    /// Runs <paramref name="read"/> on a worker thread and waits for it.
    /// </summary>
    /// <param name="read">The UI Automation read. It receives <paramref name="cancellationToken"/> and
    /// should stop early, returning what it has, once that is cancelled.</param>
    /// <param name="cancellationToken">Asks the read to stop. The deadline is when this is cancelled
    /// plus <paramref name="grace"/>.</param>
    /// <param name="grace">How long to keep waiting after <paramref name="cancellationToken"/> is
    /// cancelled, for a read that stops cooperatively and returns a partial result.</param>
    /// <returns>What <paramref name="read"/> returned.</returns>
    /// <exception cref="TimeoutException">The read did not return by the deadline (its thread is
    /// abandoned and finishes on its own), or too many earlier reads are still stuck, which means the
    /// target application is probably not responding.</exception>
    /// <remarks>An exception thrown by <paramref name="read"/> is rethrown unchanged.</remarks>
    T Run<T>(Func<CancellationToken, T> read, CancellationToken cancellationToken, TimeSpan grace);
}
