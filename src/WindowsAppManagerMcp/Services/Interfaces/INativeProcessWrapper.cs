namespace WindowsAppManagerMcp.Services.Interfaces;

/// <summary>
/// Abstraction over the operating-system process calls used by kill_process.
/// This interface enables unit testing without closing or killing real processes.
/// </summary>
public interface INativeProcessWrapper
{
    /// <summary>
    /// Opens a running process by ID. Returns null when no such process exists.
    /// </summary>
    IProcessHandle? OpenProcess(int processId);

    /// <summary>
    /// Returns true when the process is an ancestor (parent, grandparent, ...) of the current
    /// process, such as the terminal or editor that hosts the agent which started this server.
    /// </summary>
    bool IsAncestorOfCurrentProcess(int processId);
}

/// <summary>
/// A process opened by ID.
/// </summary>
public interface IProcessHandle : IDisposable
{
    int Id { get; }

    /// <summary>The process name without the .exe extension. Throws InvalidOperationException if the process has exited.</summary>
    string ProcessName { get; }

    bool HasExited { get; }

    /// <summary>
    /// Posts WM_CLOSE to the main window. Returns false when there is no main window or the main
    /// window is disabled (for example by a modal dialog).
    /// </summary>
    bool CloseMainWindow();

    /// <summary>Waits for the process to exit. Returns true if it exited within the timeout.</summary>
    bool WaitForExit(int milliseconds);

    /// <summary>Terminates the process immediately, optionally with all of its descendants.</summary>
    void Kill(bool entireProcessTree);
}
