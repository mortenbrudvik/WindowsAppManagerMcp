using WindowsAppManagerMcp.Models;
using WindowsAppManagerMcp.Models.Results;

namespace WindowsAppManagerMcp.Services.Interfaces;

public interface IProcessService
{
    /// <summary>
    /// Launches an application, and with <paramref name="waitForWindow"/> waits up to
    /// <paramref name="waitTimeoutMs"/> for its window: <see cref="LaunchApplicationAsync(LaunchRequest, bool, int, CancellationToken)"/>
    /// without a window hint.
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
    /// Launches an application (roadmap M2.6/M2.7). Bare names are resolved to an absolute path, never from the working
    /// directory, and an unresolved one is refused (ExecutableNotFound); the working directory defaults to the program's
    /// folder. The start is waited for at most <see cref="ProcessService.StartTimeout"/>; a longer one is reported as
    /// <see cref="LaunchStatus.AwaitingUserConsent"/> (a UAC prompt). When Windows starts no process (the request went to
    /// a running app), the launch still succeeds, with no process ID.
    /// <para>
    /// With <paramref name="waitForWindow"/>, waits up to <paramref name="waitTimeoutMs"/> (at most 60000) for the window
    /// the launch opens, whichever process owns it (<see cref="IWindowWaitService"/>, <see cref="LaunchWindowOwner"/>),
    /// and reports it in <see cref="LaunchResult.Window"/> with a <see cref="LaunchResult.Status"/>.
    /// </para>
    /// <para>
    /// With <see cref="LaunchRequest.ReuseExisting"/> (roadmap M3.13), a valid request first looks for an open window of the
    /// same app that shows what the launch opens; when it finds one nothing is started and the result is
    /// <see cref="LaunchStatus.AlreadyOpen"/> with that window (no process ID). ProcessService.FindOpenWindow defines which
    /// window counts.
    /// </para>
    /// </summary>
    /// <param name="cancellationToken">As for the other overload.</param>
    Task<LaunchResult> LaunchApplicationAsync(
        LaunchRequest request,
        bool waitForWindow,
        int waitTimeoutMs,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Lists running processes (roadmap M6.6). The list comes from <see cref="INativeProcessWrapper.ListRunningProcesses"/>,
    /// filtered by name, process ID and (by default) a window, then by path, sorted (<see cref="ProcessQuery.Sort"/>: name,
    /// then process ID, by default) and cut to <see cref="ProcessQuery.Limit"/>. A process is opened only for what is asked:
    /// for its path and start time (<see cref="INativeProcessWrapper.ReadProcessDetails"/>) when the path filter or the
    /// start-time order needs them, else only once it is returned, and for its integrity and command line only once it is
    /// returned. Its parent is taken from one process snapshot with the PID-reuse rule of
    /// <see cref="Native.NativeProcessWrapper.FindParent"/> (named only when both start times are known). Never throws for a
    /// process that exits or cannot be opened: its unreadable fields are null.
    /// </summary>
    /// <remarks>
    /// Windows: only processes that have a visible, titled top-level window are listed unless
    /// <see cref="ProcessQuery.IncludeWindowless"/> is set, wherever the window is: windows on other virtual desktops, tool
    /// windows and the desktop shell's windows count, so a process is never hidden only because its window is not on the
    /// current desktop. WindowCount counts those windows in both modes (0 for a process without one), and Windows holds the
    /// first <see cref="ProcessInfo.MaxWindows"/> of them, front to back; a UWP app's window counts for the app's process and
    /// for its ApplicationFrameHost.
    /// </remarks>
    /// <exception cref="InvalidOperationException">
    /// The process list cannot be read: an empty list would look like "no such process is running".
    /// </exception>
    /// <exception cref="ArgumentOutOfRangeException"><see cref="ProcessQuery.Limit"/> is below 1.</exception>
    IReadOnlyList<ProcessInfo> GetRunningProcesses(ProcessQuery query);

    string? GetProcessName(int processId);

    string? GetProcessPath(int processId);

    /// <summary>
    /// The checks of <see cref="KillProcessAsync"/> without acting: the refusal it would return for this process
    /// (except a missing confirm), or the process's name and, with <paramref name="forceKill"/>, the child processes it
    /// would also end. Without <paramref name="forceKill"/>, a process without a main window is refused with
    /// NoClosableWindow, as the graceful close would refuse it. kill_process uses it to ask the user before it enters the
    /// input gate.
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
    /// <param name="safeguards">The process the user confirmed, and what to call before the wait for the exit (see <see cref="KillSafeguards"/>).</param>
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
        KillSafeguards? safeguards = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Launches multiple applications: each one is checked and started in order, as by
    /// <see cref="LaunchApplicationAsync(LaunchRequest, bool, int, CancellationToken)"/>; with <paramref name="waitForWindows"/>
    /// the windows are then waited for together, against one deadline (<paramref name="waitTimeoutMs"/> after the last
    /// start), and a new window is reported for one launch only. An open window that one item reuses
    /// (<see cref="LaunchRequest.ReuseExisting"/>) is not reused by a later item.
    /// </summary>
    /// <param name="applications">The applications to launch.</param>
    /// <param name="waitForWindows">Whether to wait for each application's window.</param>
    /// <param name="waitTimeoutMs">How long to wait for the windows, in milliseconds.</param>
    /// <param name="progress">Receives "i of n" each time an application is done: refused, started, or its window wait ended. Optional.</param>
    /// <param name="cancellationToken">
    /// Cancels the batch: no further application is started, the window waits end, and the call throws
    /// <see cref="OperationCanceledException"/>.
    /// </param>
    /// <returns>A result for each application, in request order.</returns>
    Task<IReadOnlyList<LaunchResult>> LaunchApplicationsBatchAsync(
        IReadOnlyList<LaunchRequest> applications,
        bool waitForWindows = false,
        int waitTimeoutMs = 5000,
        IProgress<OperationProgress>? progress = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Launches a browser with CDP (Chrome DevTools Protocol) debug port enabled, with a profile of its own and without
    /// first-run screens (roadmap M5.1). Success means the started browser is running and its port is known; a launch
    /// that handed off to a browser already running with the same profile fails with DebugLaunchFailed.
    /// </summary>
    /// <param name="request">The browser, url, port (0: the browser picks one), profile and .exe.</param>
    /// <param name="cancellationToken">Cancels the call, as for <see cref="LaunchApplicationAsync(LaunchRequest, bool, int, CancellationToken)"/>.</param>
    /// <returns>Result containing process ID, window handle, CDP port, user data directory and the launch status.</returns>
    Task<DebugBrowserLaunchResult> LaunchBrowserWithDebugAsync(
        DebugBrowserLaunchRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Plans a runAsAdmin request without starting anything (roadmap M6.5, owner decision D11): the program and command
    /// line it would start, and whether the user must approve them through the client because Windows would not ask (User
    /// Account Control off or not asking administrators, the built-in Administrator outside Admin Approval Mode, a server
    /// that already runs elevated, or a policy that cannot be read). launch_application shows the user that launch before
    /// the input gate and passes the approved one as <see cref="LaunchRequest.ApprovedElevation"/>; without it such a launch
    /// is ElevationNotConfirmed.
    /// </summary>
    ElevationPreview PreviewElevation(LaunchRequest request);
}

/// <summary>One running process, as <see cref="IProcessService.GetRunningProcesses"/> lists it.</summary>
/// <param name="ProcessId">The process ID.</param>
/// <param name="Name">The process name without the .exe extension.</param>
/// <param name="ExecutablePath">The full path of the executable; null when the process cannot be opened.</param>
/// <param name="WindowCount">The process's visible, titled top-level windows (see GetRunningProcesses).</param>
/// <param name="MemoryUsageMB">The working set in MB; null when it cannot be read.</param>
/// <param name="ParentProcessId">
/// The process that started it, when that process is still running and both start times can be read and show it
/// started no later than the child (so a later process that reused the parent's PID is not named); null otherwise:
/// when either process cannot be opened for its start time, or when the process snapshot cannot be read.
/// </param>
/// <param name="StartTime">When the process started (UTC); null when it cannot be read.</param>
/// <param name="Windows">
/// The first <see cref="MaxWindows"/> of the windows <paramref name="WindowCount"/> counts, front to back; empty when it
/// has none.
/// </param>
/// <param name="Integrity">Its integrity level and elevation (IProcessSecurityProbe); null when the service has no probe.</param>
/// <param name="AboveServer">
/// True when it is known or likely to run at a higher integrity level than the server
/// (<see cref="ProcessIntegrity.IsMorePrivilegedThan"/>, the input guard's TargetElevated test): Windows drops the server's
/// input and UI Automation to its windows.
/// </param>
/// <param name="CommandLine">
/// Its command line, redacted (<see cref="Infrastructure.CommandLineRedactor"/>) and cut to
/// <see cref="MaxCommandLineLength"/> characters; null unless asked for, and when it cannot be read or the user's target
/// policy denies the process.
/// </param>
/// <param name="CommandLineRedacted">Whether something in <paramref name="CommandLine"/> was masked.</param>
public record ProcessInfo(
    int ProcessId,
    string Name,
    string? ExecutablePath,
    int WindowCount,
    double? MemoryUsageMB,
    int? ParentProcessId = null,
    DateTime? StartTime = null,
    IReadOnlyList<WindowInfo>? Windows = null,
    ProcessIntegrity? Integrity = null,
    bool AboveServer = false,
    string? CommandLine = null,
    bool CommandLineRedacted = false
)
{
    /// <summary>The most windows <see cref="Windows"/> holds; find_windows with the process ID lists the rest.</summary>
    public const int MaxWindows = 10;

    /// <summary>The longest command line returned, the ellipsis of a cut one included.</summary>
    public const int MaxCommandLineLength = 500;
}
