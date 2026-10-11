using WindowsAppManagerMcp.Models;
using WindowsAppManagerMcp.Models.Results;

namespace WindowsAppManagerMcp.Services.Interfaces;

public interface IProcessService
{
    /// <summary>
    /// Launches an application, and with <paramref name="waitForWindow"/> waits up to
    /// <paramref name="waitTimeoutMs"/> for its window.
    /// </summary>
    /// <param name="cancellationToken">
    /// Cancels the call: before the start nothing is started; during the wait for the window the started program keeps
    /// running and the call throws <see cref="OperationCanceledException"/>.
    /// </param>
    Task<LaunchResult> LaunchApplicationAsync(
        string executable,
        string[]? arguments = null,
        string? workingDirectory = null,
        bool waitForWindow = false,
        int waitTimeoutMs = 5000,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Lists running processes, optionally filtered by name.
    /// </summary>
    /// <param name="nameFilter">Part of the process name, case-insensitive.</param>
    /// <param name="includeWindowless">
    /// False (default): only processes that have a visible, titled top-level window, wherever it is:
    /// windows on other virtual desktops, tool windows and the desktop shell's windows count, so a
    /// process is never hidden only because its window is not on the current desktop. WindowCount
    /// counts those windows; a UWP app's window counts for the app's process and for its
    /// ApplicationFrameHost. True: every process, with WindowCount 0 (see M6.6).
    /// </param>
    IReadOnlyList<ProcessInfo> GetRunningProcesses(string? nameFilter = null, bool includeWindowless = false);

    string? GetProcessName(int processId);

    string? GetProcessPath(int processId);

    /// <summary>
    /// The checks of <see cref="KillProcessAsync"/> without acting: the refusal it would return for this process
    /// (except a missing confirm), or the process's name and, with <paramref name="forceKill"/>, the child processes it
    /// would also end. kill_process uses it to ask the user before it enters the input gate.
    /// </summary>
    KillTargetCheck CheckKillTarget(int processId, bool forceKill, int gracefulTimeoutMs, string? expectedProcessName = null);

    /// <summary>
    /// Terminates a process by its ID.
    /// </summary>
    /// <param name="processId">The process ID to terminate.</param>
    /// <param name="confirm">Must be true to confirm termination (safety check).</param>
    /// <param name="forceKill">
    /// If true, terminates the process and all of its child processes immediately.
    /// If false, only requests a graceful close (WM_CLOSE to the main window) and never escalates:
    /// a process that cannot be closed, or is still running after the timeout, is left running and
    /// the result reports success=false.
    /// </param>
    /// <param name="gracefulTimeoutMs">Graceful mode: how long to wait for the process to exit (0-60000 ms).</param>
    /// <param name="expectedProcessName">Optional PID-reuse guard: the expected process name, with or without .exe.</param>
    /// <param name="cancellationToken">
    /// Cancels the wait for the process to exit; the close request or termination already sent stands, and the call
    /// throws <see cref="OperationCanceledException"/>. Cancelled before anything was sent, nothing is done.
    /// </param>
    /// <returns>Result stating whether a close was requested, whether termination was forced, and whether the process exited.</returns>
    Task<KillResult> KillProcessAsync(
        int processId,
        bool confirm,
        bool forceKill = false,
        int gracefulTimeoutMs = 5000,
        string? expectedProcessName = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Launches multiple applications in batch, one after another.
    /// </summary>
    /// <param name="applications">List of applications to launch with their arguments and working directories.</param>
    /// <param name="waitForWindows">Whether to wait for each application window to appear.</param>
    /// <param name="waitTimeoutMs">Maximum time in milliseconds to wait for each window.</param>
    /// <param name="progress">Receives "i of n" after each application, started or not. Optional.</param>
    /// <param name="cancellationToken">
    /// Cancels the batch: no further application is started, and the call throws <see cref="OperationCanceledException"/>.
    /// </param>
    /// <returns>Results for each launch attempt.</returns>
    Task<IReadOnlyList<LaunchResult>> LaunchApplicationsBatchAsync(
        IReadOnlyList<(string Executable, string[]? Arguments, string? WorkingDirectory)> applications,
        bool waitForWindows = false,
        int waitTimeoutMs = 5000,
        IProgress<OperationProgress>? progress = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Launches a browser with CDP (Chrome DevTools Protocol) debug port enabled.
    /// </summary>
    /// <param name="browser">Browser to launch: chrome, edge, or brave.</param>
    /// <param name="url">Optional URL to open.</param>
    /// <param name="port">CDP debug port (default: 9222).</param>
    /// <param name="userDataDir">Optional separate user data directory to avoid conflicts with existing sessions.</param>
    /// <param name="waitForWindow">Whether to wait for the browser window to appear.</param>
    /// <param name="waitTimeoutMs">Maximum time in milliseconds to wait for the window.</param>
    /// <param name="cancellationToken">Cancels the call, as for <see cref="LaunchApplicationAsync"/>.</param>
    /// <returns>Result containing process ID, window handle, CDP port, and user data directory.</returns>
    Task<DebugBrowserLaunchResult> LaunchBrowserWithDebugAsync(
        string browser = "chrome",
        string? url = null,
        int port = 9222,
        string? userDataDir = null,
        bool waitForWindow = true,
        int waitTimeoutMs = 10000,
        CancellationToken cancellationToken = default);
}

public record ProcessInfo(
    int ProcessId,
    string Name,
    string? ExecutablePath,
    int WindowCount,
    double? MemoryUsageMB
);
