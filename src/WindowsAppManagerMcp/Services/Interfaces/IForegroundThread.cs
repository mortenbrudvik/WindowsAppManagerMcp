namespace WindowsAppManagerMcp.Services.Interfaces;

/// <summary>
/// The dedicated thread that does all window activation work for <see cref="IForegroundService"/>.
/// AttachThreadInput attaches the calling thread, and the detach has to come from the same thread;
/// MCP tool calls run on arbitrary thread-pool threads, so the work is handed to one long-lived thread
/// instead. That thread gets a message queue on its first USER call, which AttachThreadInput needs.
/// </summary>
public interface IForegroundThread
{
    /// <summary>
    /// Runs <paramref name="work"/> on the activation thread and waits for it at most
    /// <paramref name="timeout"/>. Calls run one at a time. The token passed to the work is cancelled
    /// when the caller stops waiting, so the work can skip its remaining steps.
    /// </summary>
    /// <exception cref="TimeoutException">
    /// The work did not finish in time, usually because a window did not answer a call. The work may
    /// still be running; the next call gets a new thread.
    /// </exception>
    T Run<T>(Func<CancellationToken, T> work, TimeSpan timeout);
}
