namespace WindowsAppManagerMcp.Services.Interfaces;

/// <summary>
/// Runs a window call that can block until another thread's window answers, with a deadline. SetWindowPos and
/// ShowWindow on a window of another thread send that thread messages and wait for it, and a hung application
/// never answers. The window tools run inside the process-wide input gate (see
/// <see cref="Infrastructure.InputGateToolFilter"/>), so such a call would stop every input and window tool until
/// the application recovered; through this runner it fails with <see cref="WindowNotRespondingException"/> instead,
/// and the tool returns NotResponding and releases the gate.
/// </summary>
public interface IWindowCallRunner
{
    /// <summary>
    /// Runs <paramref name="call"/> on a thread of its own and waits for it until the deadline.
    /// </summary>
    /// <param name="window">The window the call acts on: its thread is the one that has to answer.</param>
    /// <param name="call">The call, such as SetWindowPos or ShowWindow.</param>
    /// <returns>What <paramref name="call"/> returned.</returns>
    /// <exception cref="WindowNotRespondingException">
    /// The call was not started, because Windows reports the window hung (IsHungAppWindow) or calls to its
    /// application that missed their deadline are still blocked; or it did not return by the deadline. Its thread
    /// is then abandoned and finishes on its own, so the change it asked for can still happen once the window
    /// answers.
    /// </exception>
    /// <remarks>An exception thrown by <paramref name="call"/> is rethrown unchanged.</remarks>
    T Run<T>(nint window, Func<T> call);
}

/// <summary>
/// A window call was not made, or did not return in time, because the window's thread does not answer: the
/// application is hung or busy. Reported as NotResponding.
/// </summary>
public sealed class WindowNotRespondingException : TimeoutException
{
    public WindowNotRespondingException(nint window, string message)
        : base(message)
    {
        Window = window;
    }

    /// <summary>The window the call was for.</summary>
    public nint Window { get; }
}
