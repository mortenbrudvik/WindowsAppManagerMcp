using System.Diagnostics;

namespace WindowsAppManagerMcp.Services.Interfaces;

/// <summary>
/// Abstraction over the operating-system process calls used by launch_application,
/// launch_browser_with_debug and kill_process. It is the only place that starts, closes or
/// terminates processes, so unit tests can mock it and never launch, close or kill a real process.
/// </summary>
public interface INativeProcessWrapper
{
    /// <summary>
    /// Starts a process as described by <paramref name="startInfo"/>. Returns null when no new
    /// process was started, for example when ShellExecute handed the request to a running instance.
    /// </summary>
    IProcessHandle? StartProcess(ProcessStartInfo startInfo);

    /// <summary>
    /// Runs where.exe for <paramref name="fileName"/> without a window and returns the first line it
    /// prints, or null when it finds nothing, cannot be run or does not finish in time.
    /// </summary>
    string? FindWithWhereExe(string fileName);

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
/// A process opened by ID or started by this server.
/// </summary>
public interface IProcessHandle : IDisposable
{
    int Id { get; }

    /// <summary>The process name without the .exe extension. Throws InvalidOperationException if the process has exited.</summary>
    string ProcessName { get; }

    bool HasExited { get; }

    /// <summary>
    /// Re-reads the process information and returns the handle of its main window, or zero when it
    /// has no main window yet.
    /// </summary>
    nint RefreshMainWindowHandle();

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
