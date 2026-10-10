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
    /// Runs <paramref name="work"/> on the activation thread. Calls run one at a time: a call first
    /// waits at most <paramref name="timeout"/> for an earlier call to finish, then at most
    /// <paramref name="timeout"/> for its own work, so the wait in line does not eat the work's time.
    /// The token passed to the work is cancelled when the caller stops waiting, so the work can skip
    /// its remaining steps.
    /// </summary>
    /// <exception cref="ForegroundThreadBusyException">
    /// An earlier call still held the activation thread after <paramref name="timeout"/>; the work did
    /// not start.
    /// </exception>
    /// <exception cref="TimeoutException">
    /// The work did not finish in time, usually because a window did not answer a call, or earlier
    /// work is still blocked that way. The work may still be running; the next call gets a new thread.
    /// </exception>
    T Run<T>(Func<CancellationToken, T> work, TimeSpan timeout);

    /// <summary>
    /// Called by the work, on the activation thread: that thread exits once the current work returns,
    /// and the next call starts on a new thread. For a thread whose input may still be attached to
    /// another thread's, for example after a detach that Windows refused.
    /// </summary>
    /// <exception cref="InvalidOperationException">Called from a thread that is not an activation thread.</exception>
    void RetireCurrentThread();
}

/// <summary>
/// An earlier window activation still held the activation thread, so this one did not start. Nothing
/// was changed; the caller can try again.
/// </summary>
public sealed class ForegroundThreadBusyException : TimeoutException
{
    public ForegroundThreadBusyException(string message)
        : base(message)
    {
    }
}
