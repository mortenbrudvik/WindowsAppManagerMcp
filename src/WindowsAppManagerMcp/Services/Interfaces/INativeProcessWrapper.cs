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
    /// prints, or null when it finds nothing, cannot be run or does not finish within a few seconds.
    /// The wait is bounded even if where.exe prints nothing; on timeout where.exe is killed.
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

    /// <summary>
    /// Returns the current process's ancestors, parent first, with their start times (UTC; null when
    /// unreadable). Uses the same PID-reuse-aware walk as <see cref="IsAncestorOfCurrentProcess"/>.
    /// Returns an empty list when the process list cannot be read.
    /// </summary>
    IReadOnlyList<(int ProcessId, DateTime? StartTime)> GetAncestorProcesses();

    /// <summary>
    /// Returns the ancestors of any running process, parent first, with the same PID-reuse-aware walk as
    /// <see cref="GetAncestorProcesses"/>: the walk stops at a parent that started after its child. The
    /// input guard uses it to accept popups of a target's child processes (WebView2, for example).
    /// Returns an empty list when the process list cannot be read or the process is not running.
    /// </summary>
    IReadOnlyList<int> GetAncestorsOf(int processId);

    /// <summary>
    /// Returns the descendants of a running process (children, grandchildren, ...), breadth-first, with
    /// their image names (such as "KeePass.exe"): the processes that Kill(entireProcessTree: true) also
    /// ends. A child that started before its parent is skipped, because its PID was reused. Returns null
    /// when the process list cannot be read (the snapshot cannot be taken, or not even its first entry read)
    /// or does not list the process (it is not running, or exited meanwhile): the children are then unknown,
    /// not absent, so a caller that checks them must fail closed. An empty list means the process is in the
    /// list and has no children. Only reads.
    /// </summary>
    IReadOnlyList<(int ProcessId, string ImageName)>? GetDescendantsOf(int processId);

    /// <summary>
    /// Returns the start time (UTC) of a running process, or null when it is not running or its
    /// start time cannot be read (for example access is denied).
    /// </summary>
    DateTime? GetProcessStartTime(int processId);

    /// <summary>
    /// Returns the window of the console the current process is attached to (GetConsoleWindow), or
    /// zero when it has none, for example when it was started with CREATE_NO_WINDOW.
    /// </summary>
    nint GetConsoleWindow();

    // Launch-to-window resolution (roadmap M2.6). All of them only read or wait; none changes a process or window.

    /// <summary>
    /// Every running process with its parent's ID and image name (such as "chrome.exe"), from one process snapshot, or
    /// null when the list cannot be read. A parent ID can name a process that has exited, or a later process that
    /// reused its PID: check start times before trusting it.
    /// </summary>
    IReadOnlyDictionary<int, (int ParentProcessId, string ImageName)>? GetProcessList();

    /// <summary>
    /// The full path of a process's executable (QueryFullProcessImageNameW with PROCESS_QUERY_LIMITED_INFORMATION), or
    /// null when it is not running or cannot be read.
    /// </summary>
    string? GetProcessImagePath(int processId);

    /// <summary>
    /// Waits up to <paramref name="milliseconds"/> for the process to finish starting and wait for user input
    /// (WaitForInputIdle). True when it did, false on timeout, null when it has no message loop (a console program),
    /// is not running or cannot be opened. Blocks the calling thread for at most the given time.
    /// </summary>
    bool? WaitForInputIdle(int processId, int milliseconds);
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
    /// The exit code of the process once it has exited; null while it runs, or when the code cannot be read. Never
    /// throws. A batch launch reads it to tell a launcher that handed its request to a running instance (0) from one that
    /// failed.
    /// </summary>
    int? ExitCode { get; }

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

    /// <summary>
    /// Waits, asynchronously, for the process to exit. True if it exited within <paramref name="milliseconds"/>.
    /// </summary>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled first.</exception>
    Task<bool> WaitForExitAsync(int milliseconds, CancellationToken cancellationToken);

    /// <summary>Terminates the process immediately, optionally with all of its descendants.</summary>
    void Kill(bool entireProcessTree);
}
