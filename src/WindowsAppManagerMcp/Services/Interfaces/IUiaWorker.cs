namespace WindowsAppManagerMcp.Services.Interfaces;

/// <summary>
/// Runs UI Automation reads with a hard deadline. A UI Automation call blocks until the target
/// process answers, and an application that hangs (or a provider stuck in FromPoint) can block it
/// far longer than any tool timeout. The worker runs the read on its own thread and stops waiting
/// when the deadline passes, so the tool call still returns. The caller waits asynchronously: no
/// thread of the caller (a thread-pool thread of the MCP server) is blocked while the read runs.
/// </summary>
public interface IUiaWorker
{
    /// <summary>
    /// Runs <paramref name="read"/> on a worker thread; the task completes when it returns or at the deadline.
    /// </summary>
    /// <param name="targetProcessId">The process whose UI the read reads (the owner of the window, of
    /// the window at the point, or of the foreground window; for a UWP frame window the app's own
    /// process, see <see cref="Services.UwpFrames.AppProcessId"/>, not the ApplicationFrameHost shared by
    /// every UWP app), or 0 when it is not known. Reads that miss their deadline are counted per target,
    /// so an application that hangs blocks further reads of itself, not reads of other applications.</param>
    /// <param name="read">The UI Automation read. It receives <paramref name="cancellationToken"/> and
    /// should stop early, returning what it has, once that is cancelled.</param>
    /// <param name="cancellationToken">Asks the read to stop. The deadline is when this is cancelled
    /// plus <paramref name="grace"/>.</param>
    /// <param name="grace">How long to keep waiting after <paramref name="cancellationToken"/> is
    /// cancelled, for a read that stops cooperatively and returns a partial result.</param>
    /// <returns>What <paramref name="read"/> returned.</returns>
    /// <exception cref="TimeoutException">The read did not return by the deadline (its thread is
    /// abandoned and finishes on its own).</exception>
    /// <exception cref="UiaDeadlinePassedException">The deadline had passed before the call (<paramref name="cancellationToken"/>
    /// already cancelled, with no <paramref name="grace"/>), so the read was not started and is not counted as stuck.</exception>
    /// <exception cref="UiaReadRefusedException">The read was not started, because earlier reads of the
    /// same target, or too many reads overall, are still stuck: the target application is probably
    /// not responding.</exception>
    /// <remarks>An exception thrown by <paramref name="read"/> is rethrown unchanged.</remarks>
    Task<T> RunAsync<T>(int targetProcessId, Func<CancellationToken, T> read, CancellationToken cancellationToken, TimeSpan grace);
}

/// <summary>
/// <see cref="IUiaWorker"/> did not start a read because earlier reads are still stuck in an
/// application that does not answer. It fails at once, without waiting for any deadline.
/// </summary>
public sealed class UiaReadRefusedException : TimeoutException
{
    public UiaReadRefusedException(string message)
        : base(message)
    {
    }
}

/// <summary>
/// <see cref="IUiaWorker"/> did not start a read because its deadline had passed before the read was asked for: the
/// caller's time ran out between two reads, so the application was asked nothing. A <see cref="TimeoutException"/>, as a
/// missed deadline, so every caller that handles one handles this too; a caller that describes the timeout tells it apart,
/// so its message never says the application did not answer, or may be hung, when it was never asked.
/// </summary>
public sealed class UiaDeadlinePassedException : TimeoutException
{
    /// <summary>The worker's sentence for it, which a caller may put in its own message.</summary>
    public const string DefaultMessage =
        "The time for this UI Automation read had run out before it started, so the application was not asked. Nothing was read.";

    public UiaDeadlinePassedException()
        : base(DefaultMessage)
    {
    }
}
