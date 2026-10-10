using System.ComponentModel;
using ModelContextProtocol;
using ModelContextProtocol.Server;
using WindowsAppManagerMcp.Infrastructure;
using WindowsAppManagerMcp.Models;
using WindowsAppManagerMcp.Services.Interfaces;

namespace WindowsAppManagerMcp.Tools;

[McpServerToolType]
public class AppLauncherTools
{
    /// <summary>
    /// The longest wait for a launched application's window, whatever waitTimeoutMs asks for. The launch tools run
    /// inside the input gate, so an agent's value must not hold it, and with it every input and window tool, for long.
    /// </summary>
    public const int MaxWaitTimeoutMs = 30_000;

    private readonly IProcessService _processService;

    public AppLauncherTools(IProcessService processService)
    {
        _processService = processService;
    }

    [McpServerTool(Name = "launch_application")]
    [Description("Launch an application by executable path, name, or protocol URI. Returns process ID and optionally the window handle if waitForWindow is true.")]
    public async Task<LaunchResultDto> LaunchApplication(
        [Description("Path to executable, application name, or protocol URI (e.g., 'notepad', 'C:\\Program Files\\App\\app.exe', 'https://example.com')")]
        string executable,
        [Description("Command line arguments, one array element per argument (quoting is handled for you; do not add quotes). If the target is a batch file such as 'code' (code.cmd) or 'npm' (npm.cmd), arguments containing & | < > ^ % ! or \" are refused (errorCode UnsafeBatchArguments); launch the program's .exe by full path instead.")]
        string[]? arguments = null,
        [Description("Working directory for the application")]
        string? workingDirectory = null,
        [Description("Whether to wait for the application window to appear before returning (default: false)")]
        bool waitForWindow = false,
        [Description("Maximum time in milliseconds to wait for the window (0-30000, default: 5000)")]
        int waitTimeoutMs = 5000,
        CancellationToken cancellationToken = default)
    {
        var result = await _processService.LaunchApplicationAsync(
            executable,
            arguments,
            workingDirectory,
            waitForWindow,
            BoundedWait(waitTimeoutMs),
            cancellationToken).ConfigureAwait(false);

        return new LaunchResultDto(
            Success: result.Success,
            ProcessId: result.ProcessId,
            WindowHandle: result.WindowHandle?.ToInt64(),
            Error: result.ErrorMessage,
            ErrorCode: result.ErrorCode,
            Warning: result.Warning
        );
    }

    [McpServerTool(Name = "list_processes")]
    [Description("List currently running processes with optional filtering by name. Returns process info including ID, name, window count, and memory usage. " +
                 "By default only processes with a visible, titled window are listed, including windows on other virtual desktops and tool windows; " +
                 "pass includeWindowless=true to list every process, for example to confirm that a process has exited.")]
    public IReadOnlyList<ProcessInfoDto> ListProcesses(
        [Description("Filter processes by name (partial match, case-insensitive)")]
        string? nameFilter = null,
        [Description("Also list processes without a visible, titled window (default: false). windowCount is then 0 for every process.")]
        bool includeWindowless = false)
    {
        var processes = _processService.GetRunningProcesses(nameFilter, includeWindowless);

        return processes.Select(p => new ProcessInfoDto(
            ProcessId: p.ProcessId,
            Name: p.Name,
            ExecutablePath: p.ExecutablePath,
            WindowCount: p.WindowCount,
            MemoryUsageMB: p.MemoryUsageMB.HasValue ? Math.Round(p.MemoryUsageMB.Value, 1) : null
        )).ToList();
    }

    [McpServerTool(Name = "kill_process")]
    [Description("Close or terminate a process by its ID. Requires confirm=true. " +
                 "Default (forceKill=false) is a graceful close only: WM_CLOSE is sent to the main window and the call waits up to gracefulTimeoutMs. " +
                 "It never escalates: if the process has no closable window (errorCode NoClosableWindow) or is still running after the timeout, " +
                 "for example because it shows a 'Save changes?' prompt (errorCode GracefulCloseTimedOut), the process is left running and success is false. " +
                 "forceKill=true terminates the process and all of its child processes immediately; unsaved work is lost. " +
                 "The result reports closeRequested, forced and exited. Refuses protected system processes, the agent's own host (the terminal or editor hosting this server; errorCode CannotTerminateHost) " +
                 "and apps the user's policy denies (TargetDeniedByPolicy); with forceKill=true also when a child process it would end belongs to that host or is denied by the policy, " +
                 "or when the child processes cannot be read to check them (ProcessTreeUnreadable).")]
    public async Task<KillResultDto> KillProcess(
        [Description("The process ID to terminate (get from list_processes tool)")]
        int processId,
        [Description("Must be set to true to confirm termination - this is a safety requirement")]
        bool confirm,
        [Description("If true, immediately terminates the process and all of its child processes (unsaved work is lost). If false (default), only requests a graceful close and never escalates.")]
        bool forceKill = false,
        [Description("Graceful mode only: milliseconds to wait for the process to exit after the close request (0-60000, default 5000)")]
        int gracefulTimeoutMs = 5000,
        [Description("Optional guard against PID reuse: the expected process name as shown by list_processes (e.g. 'notepad' or 'notepad.exe'). If the PID now belongs to a different process, nothing is done and errorCode is ProcessNameMismatch.")]
        string? expectedProcessName = null,
        CancellationToken cancellationToken = default)
    {
        // The wait for the process to exit (up to gracefulTimeoutMs) ends when the client cancels the call
        var result = await _processService.KillProcessAsync(
            processId, confirm, forceKill, gracefulTimeoutMs, expectedProcessName, cancellationToken).ConfigureAwait(false);

        return new KillResultDto(
            Success: result.Success,
            ProcessId: result.ProcessId,
            ProcessName: result.ProcessName,
            Error: result.ErrorMessage,
            ErrorCode: result.ErrorCode,
            CloseRequested: result.CloseRequested,
            Forced: result.Forced,
            Exited: result.Exited,
            Warning: result.Warning
        );
    }

    [McpServerTool(Name = "launch_applications_batch")]
    [Description("Launch multiple applications in a single batch operation. More efficient than multiple individual calls.")]
    public async Task<BatchLaunchResult> LaunchApplicationsBatch(
        [Description("Array of applications to launch with executable, optional arguments, and optional workingDirectory")]
        ApplicationLaunchRequest[] applications,
        [Description("Whether to wait for each application window to appear (default: false)")]
        bool waitForWindows = false,
        [Description("Maximum time in milliseconds to wait for each window (0-30000, default: 5000)")]
        int waitTimeoutMs = 5000,
        IProgress<ProgressNotificationValue>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var serviceApplications = applications.Select(a =>
            (a.Executable, a.Arguments, a.WorkingDirectory)).ToList();

        // One application after another; the client gets "i of n" after each, and a cancelled call starts no more
        var results = await _processService.LaunchApplicationsBatchAsync(
            serviceApplications, waitForWindows, BoundedWait(waitTimeoutMs), ToolProgress.ForServices(progress), cancellationToken)
            .ConfigureAwait(false);

        var items = results.Select((r, i) => new LaunchResultItem(
            Executable: applications[i].Executable,
            Success: r.Success,
            ProcessId: r.ProcessId,
            WindowHandle: r.WindowHandle?.ToInt64(),
            Error: r.ErrorMessage,
            Warning: r.Warning,
            ErrorCode: r.ErrorCode
        )).ToList();

        return new BatchLaunchResult(
            TotalRequested: applications.Length,
            Succeeded: items.Count(i => i.Success),
            Failed: items.Count(i => !i.Success),
            Results: items
        );
    }

    /// <summary>waitTimeoutMs within 0 to <see cref="MaxWaitTimeoutMs"/>.</summary>
    private static int BoundedWait(int waitTimeoutMs) => Math.Clamp(waitTimeoutMs, 0, MaxWaitTimeoutMs);
}

public record KillResultDto(
    bool Success,
    int ProcessId,
    string? ProcessName = null,
    string? Error = null,
    string? ErrorCode = null,
    bool CloseRequested = false,
    bool Forced = false,
    bool Exited = false,
    string? Warning = null
);

public record LaunchResultDto(
    bool Success,
    int? ProcessId = null,
    long? WindowHandle = null,
    string? Error = null,
    string? ErrorCode = null,
    string? Warning = null
);

public record ProcessInfoDto(
    int ProcessId,
    string Name,
    string? ExecutablePath,
    int WindowCount,
    double? MemoryUsageMB
);
