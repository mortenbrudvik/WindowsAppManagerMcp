using System.ComponentModel;
using ModelContextProtocol;
using ModelContextProtocol.Server;
using WindowsAppManagerMcp.Infrastructure;
using WindowsAppManagerMcp.Models;
using WindowsAppManagerMcp.Models.Results;
using WindowsAppManagerMcp.Services;
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

    /// <summary>What the launch tools say about placement (roadmap M3.13).</summary>
    private const string PlacementDescription =
        "Where to put the window once it is found (implies waiting for it): snap (a snap_window position) or bounds (the visible " +
        "frame, as set_window_bounds), monitorIndex, state (normal, maximized or minimized) and focus (as focus_window). Checked " +
        "before anything is launched: unknown values fail with InvalidSnapPosition or InvalidState, contradictions (snap with " +
        "bounds, bounds with monitorIndex, maximized with snap or bounds, focus with minimized, or nothing asked) with " +
        "InvalidArguments, a monitor that does not exist with InvalidMonitorIndex. The window is checked again about 500 ms after it " +
        "is placed and placed once more if the app moved it back (reapplied: true)";

    private const string ReuseDescription =
        "Use an open window of the app instead of starting another copy (default false): a window on the current desktop, not " +
        "the agent's own, of a process with the program's path or name (calc: CalculatorApp, code: Code, wt: WindowsTerminal; " +
        "never a terminal for cmd or powershell) and of expectWindow.processName when set (needed for a document, a Store app or " +
        "a web address). Its title must contain expectWindow.titleContains when set, else show each file or folder the launch " +
        "opens as a whole name, never after another path; a web address (a page sets its own title) or a URI page such as " +
        "ms-settings:display needs titleContains. The front-most match is used (one showing the full path first), nothing is " +
        "started, and launchStatus is alreadyOpen; warning says when only the name matched or the title is the page's: check " +
        "the window before acting";

    private const string ForceAccessibilityDescription =
        "Start a Chromium browser, an Electron app (VS Code, Slack, Discord) or a CEF app with --force-renderer-accessibility, " +
        "so get_ui_elements sees its web content from the first read (default false). Added only when the executable is such a " +
        "program's .exe (for VS Code Code.exe, not code); otherwise nothing is added and warning says why. forceAccessibilityApplied " +
        "reports it. An app that is already running keeps its mode, and warning says so: end every process of it first (a browser " +
        "may keep one without a window)";

    private readonly IProcessService _processService;
    private readonly IWindowService? _windowService;
    private readonly IInputSession? _session;
    private readonly TimeSpan _answerTimeout;
    private readonly TimeSpan? _gateWaitTimeout;
    private readonly ILaunchPlacementService? _placementService;
    private readonly IMonitorService? _monitorService;

    /// <param name="processService">Launches, lists and ends processes.</param>
    /// <param name="windowService">Names the windows of a process in kill_process's question to the user.</param>
    /// <param name="session">The input session, whose pause kill_process respects (it takes the input gate itself).</param>
    /// <param name="answerTimeout">How long kill_process waits for the user's answer; default <see cref="UserConfirmation.DefaultAnswerTimeout"/>.</param>
    /// <param name="gateWaitTimeout">How long kill_process waits for the input gate; default <see cref="InputGateToolFilter.GateWaitTimeout"/>.</param>
    /// <param name="placementService">Places the launches' windows (placement, roadmap M3.13). The server always passes it.</param>
    /// <param name="monitorService">Checks a placement's monitorIndex before anything is launched; without it the placement reports it.</param>
    public AppLauncherTools(
        IProcessService processService,
        IWindowService? windowService = null,
        IInputSession? session = null,
        TimeSpan? answerTimeout = null,
        TimeSpan? gateWaitTimeout = null,
        ILaunchPlacementService? placementService = null,
        IMonitorService? monitorService = null)
    {
        _processService = processService;
        _windowService = windowService;
        _session = session;
        _answerTimeout = answerTimeout ?? UserConfirmation.DefaultAnswerTimeout;
        _gateWaitTimeout = gateWaitTimeout;
        _placementService = placementService;
        _monitorService = monitorService;
    }

    [McpServerTool(Name = "launch_application", Title = "Launch Application", ReadOnly = false, Destructive = true, Idempotent = false, OpenWorld = true)]
    [Description("Launch an application by executable path, name, or protocol URI (http/https, mailto, tel, ms-settings: Settings pages, " +
                 "shell:AppsFolder\\<AppUserModelID> for Store apps, and the schemes the user allows). Returns launchStatus and the process ID. " +
                 "With waitForWindow=true it also returns the window the launch opened, whichever process owns it (launchers such as calc, " +
                 "code or a browser hand off to another process): launchStatus windowObserved, reusedExistingWindow or openedInTab " +
                 "(single-instance and tabbed apps such as Notepad, Terminal, VS Code or Explorer), or, without a window, possibleHandoff, " +
                 "exitedWithoutWindow or timedOut. awaitingUserConsent means Windows shows the user a prompt (UAC, SmartScreen) that only the user may answer. " +
                 "Never launch again just because no window was found: look for it with find_windows. " +
                 "placement arranges the window in the same call (snap or bounds, monitor, state, focus) and returns placement {finalBounds, " +
                 "finalState, monitorIndex, focused, reapplied}; a placement that fails has placement.errorCode and error while success " +
                 "stays true, because the app was launched: fix the window with the window tools, never launch again. reuseExisting uses " +
                 "an open window of the app instead of starting another (launchStatus alreadyOpen).")]
    public async Task<LaunchResultDto> LaunchApplication(
        [Description("Path to executable, application name, or protocol URI (e.g., 'notepad', 'C:\\Program Files\\App\\app.exe', 'https://example.com', 'ms-settings:display'). A bare name is looked up in the Windows folder, System32, PATH and App Paths, never in the current folder; one that is not found there fails with ExecutableNotFound")]
        string executable,
        [Description("Command line arguments, one array element per argument (quoting is handled for you; do not add quotes). If the target is a batch file such as 'code' (code.cmd) or 'npm' (npm.cmd), arguments containing & | < > ^ % ! or \" are refused (errorCode UnsafeBatchArguments); launch the program's .exe by full path instead.")]
        string[]? arguments = null,
        [Description("Working directory for the application (default: the program's own folder)")]
        string? workingDirectory = null,
        [Description("Whether to wait for the application window to appear before returning (default: false)")]
        bool waitForWindow = false,
        [Description("Maximum time in milliseconds to wait for the window (0-30000, default: 5000)")]
        int waitTimeoutMs = 5000,
        [Description("With waitForWindow or placement: the window to expect when the server cannot tell it from the started process, such as a Store app, a URI or a document opened by whatever app is registered for it; a new window that matches every field set counts as the launch's window. With reuseExisting: the open window must match it (a web address needs titleContains)")]
        LaunchWindowHint? expectWindow = null,
        [Description(PlacementDescription)]
        LaunchPlacement? placement = null,
        [Description(ReuseDescription)]
        bool reuseExisting = false,
        [Description(ForceAccessibilityDescription)]
        bool forceAccessibility = false,
        CancellationToken cancellationToken = default)
    {
        // A placement is checked before anything is launched; a bad one launches nothing
        LaunchPlacementPlan? plan = null;
        if (placement != null)
        {
            var monitorCount = MonitorCount();
            if (!TryPlan(placement, monitorCount, out plan, out var errorCode, out var error))
            {
                throw new ToolErrorException(errorCode!, $"{error} Nothing was launched.");
            }
        }

        var result = await _processService.LaunchApplicationAsync(
            new LaunchRequest(executable, arguments, workingDirectory, expectWindow, reuseExisting, forceAccessibility),
            waitForWindow || plan != null,
            BoundedWait(waitTimeoutMs),
            cancellationToken).ConfigureAwait(false);

        var placed = await PlaceAsync([(result, plan)], cancellationToken).ConfigureAwait(false);

        return new LaunchResultDto(
            Success: result.Success,
            ProcessId: result.ProcessId,
            WindowHandle: result.WindowHandle?.ToInt64(),
            Error: result.ErrorMessage,
            ErrorCode: result.ErrorCode,
            Warning: result.Warning,
            LaunchStatus: result.Status?.ToWireName(),
            Window: result.Window is { } window ? WindowFinderTools.ToDto(window) : null,
            LauncherExited: result.LauncherExited,
            Placement: placed[0],
            ForceAccessibilityApplied: result.ForceAccessibilityApplied
        );
    }

    /// <summary>The number of monitors, to check a placement's monitorIndex before the launch; null without a monitor service.</summary>
    private int? MonitorCount() => _monitorService?.GetAllMonitors().Count;

    /// <summary>
    /// Checks a placement (<see cref="LaunchPlacementPlan.TryCreate"/>), and its monitorIndex against the monitors there are
    /// when <paramref name="monitorCount"/> is known.
    /// </summary>
    private static bool TryPlan(LaunchPlacement placement, int? monitorCount, out LaunchPlacementPlan? plan, out string? errorCode, out string? error)
    {
        if (!LaunchPlacementPlan.TryCreate(placement, out plan, out errorCode, out error))
        {
            return false;
        }

        if (plan!.MonitorIndex is { } index && monitorCount is { } count && index >= count)
        {
            plan = null;
            errorCode = nameof(WindowErrorCode.InvalidMonitorIndex);
            error = $"placement.monitorIndex: {WindowService.MonitorIndexMessage(index, count)}";
            return false;
        }

        return true;
    }

    /// <summary>
    /// Places the windows of the launches that have a plan (roadmap M3.13), all in one go (<see cref="ILaunchPlacementService"/>),
    /// and returns each launch's placement result, in order: null without a plan or for a launch that failed, WindowNotFound
    /// for a launch that found no window.
    /// </summary>
    private async Task<IReadOnlyList<LaunchPlacementResultDto?>> PlaceAsync(
        IReadOnlyList<(LaunchResult Result, LaunchPlacementPlan? Plan)> launches, CancellationToken cancellationToken)
    {
        var placed = new LaunchPlacementResultDto?[launches.Count];
        var placements = new List<(nint Handle, LaunchPlacementPlan Plan)>();
        var positions = new List<int>();
        for (var i = 0; i < launches.Count; i++)
        {
            var (result, plan) = launches[i];
            if (plan == null || !result.Success)
                continue;

            if (result.Window is { } window)
            {
                placements.Add((window.Handle, plan));
                positions.Add(i);
            }
            else
            {
                placed[i] = new LaunchPlacementResultDto(null, null, null, Focused: false, Reapplied: false,
                    ErrorCode: nameof(WindowErrorCode.WindowNotFound),
                    Error: $"The launch succeeded (launchStatus {result.Status?.ToWireName()}) but found no window, so nothing was placed. " +
                           "Look for its window with find_windows and arrange it with snap_window, set_window_bounds or focus_window; " +
                           "do not launch it again.");
            }
        }

        if (placements.Count > 0)
        {
            try
            {
                var service = _placementService ?? throw new InvalidOperationException("No launch placement service is registered.");
                var outcomes = await service.PlaceAsync(placements, cancellationToken).ConfigureAwait(false);
                for (var k = 0; k < outcomes.Count; k++)
                {
                    placed[positions[k]] = ToDto(outcomes[k]);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // A placement failure never hides a successful launch: the launch result stays, the placement reports it
                foreach (var position in positions)
                {
                    placed[position] = new LaunchPlacementResultDto(null, null, null, Focused: false, Reapplied: false,
                        ErrorCode: nameof(WindowErrorCode.MoveFailed),
                        Error: $"The launch succeeded, but placing its window failed: {ex.Message} Arrange it with snap_window, " +
                               "set_window_bounds or focus_window; do not launch it again.");
                }
            }
        }

        return placed;
    }

    private static LaunchPlacementResultDto ToDto(LaunchPlacementOutcome outcome)
    {
        var window = outcome.Window;
        var visible = window is null || window.State == WindowState.Minimized ? null : window.VisibleBounds ?? window.Bounds;
        return new LaunchPlacementResultDto(
            FinalBounds: visible is null ? null : new BoundsDto(visible.X, visible.Y, visible.Width, visible.Height),
            FinalState: window?.State.ToString().ToLowerInvariant(),
            MonitorIndex: window?.MonitorIndex,
            Focused: outcome.Focused,
            Reapplied: outcome.Reapplied,
            ErrorCode: outcome.Success ? null : outcome.ErrorCode.ToString(),
            Error: outcome.Error);
    }

    [McpServerTool(Name = "list_processes", Title = "List Processes", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false)]
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

    [McpServerTool(Name = "kill_process", Title = "Close or Terminate Process", ReadOnly = false, Destructive = true, Idempotent = false, OpenWorld = false)]
    [Description("Close or terminate a process by its ID. When the MCP client can ask the user (elicitation), the user is asked to confirm, " +
                 "with the process's name, PID, windows and (forceKill) child processes, and nothing is done unless they confirm " +
                 "(errorCode ConfirmationDeclined); a process that got the PID while the user read the question is not touched, even one with " +
                 "the same name (ProcessNameMismatch), nor a forceKill tree that gained a process the user was not shown (ProcessTreeChanged). " +
                 "Otherwise confirm=true is required (ConfirmationRequired). " +
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
        [Description("Must be set to true to confirm termination - this is a safety requirement. When the client can ask the user, the user's answer decides instead.")]
        bool confirm,
        [Description("If true, immediately terminates the process and all of its child processes (unsaved work is lost). If false (default), only requests a graceful close and never escalates.")]
        bool forceKill = false,
        [Description("Graceful mode only: milliseconds to wait for the process to exit after the close request (0-60000, default 5000)")]
        int gracefulTimeoutMs = 5000,
        [Description("Optional guard against PID reuse: the expected process name as shown by list_processes (e.g. 'notepad' or 'notepad.exe'). If the PID now belongs to a different process, nothing is done and errorCode is ProcessNameMismatch.")]
        string? expectedProcessName = null,
        McpServer? server = null,
        CancellationToken cancellationToken = default)
    {
        // Nothing while the user has paused input, not even a question
        InputGateToolFilter.ThrowIfPaused(_session);

        KillTargetCheck? confirmed = null;
        if (UserConfirmation.CanAsk(server))
        {
            // Ask the user, not the model, and before the input gate: a waiting user must not hold up every input call.
            // A kill that would be refused anyway is refused without asking.
            var target = _processService.CheckKillTarget(processId, forceKill, gracefulTimeoutMs, expectedProcessName);
            if (target.Refusal is { } refusal)
            {
                return ToDto(refusal);
            }

            var answer = await UserConfirmation.AskAsync(
                server!, KillQuestion(processId, target, forceKill), forceKill ? "Terminate the process" : "Close the process",
                _answerTimeout, cancellationToken).ConfigureAwait(false);
            if (!answer.Confirmed)
            {
                return new KillResultDto(false, processId, target.ProcessName,
                    Error: $"{answer.Reason} Nothing was done: '{target.ProcessName}' (PID {processId}) is still running. Ask the user how to continue.",
                    ErrorCode: nameof(ProcessErrorCode.ConfirmationDeclined));
            }

            // The user agreed to this process: if its PID now belongs to another one, even one with the same name (its start
            // time differs), or a forced kill's tree gained a process the user was not shown, nothing is done
            confirm = true;
            expectedProcessName = target.ProcessName;
            confirmed = target;
        }
        else if (!confirm)
        {
            // ConfirmationRequired, without waiting for the gate
            return ToDto(await _processService.KillProcessAsync(
                processId, confirm, forceKill, gracefulTimeoutMs, expectedProcessName, cancellationToken: cancellationToken).ConfigureAwait(false));
        }

        // Only the close request or the termination is sent inside the input gate: the gate is released before the wait for
        // the process to exit (up to gracefulTimeoutMs), as the close tools do, so a 'Save changes?' prompt does not hold up
        // every input and window call. The wait ends when the client cancels the call.
        var gate = await InputGateToolFilter.EnterAsync(_session, cancellationToken, _gateWaitTimeout).ConfigureAwait(false);
        try
        {
            var result = await _processService.KillProcessAsync(
                processId, confirm, forceKill, gracefulTimeoutMs, expectedProcessName, new KillSafeguards(confirmed, gate.Dispose),
                cancellationToken).ConfigureAwait(false);
            return ToDto(result);
        }
        finally
        {
            gate.Dispose();
        }
    }

    private static KillResultDto ToDto(KillResult result) => new(
        Success: result.Success,
        ProcessId: result.ProcessId,
        ProcessName: result.ProcessName,
        Error: result.ErrorMessage,
        ErrorCode: result.ErrorCode,
        CloseRequested: result.CloseRequested,
        Forced: result.Forced,
        Exited: result.Exited,
        Warning: result.Warning);

    /// <summary>The question kill_process asks the user: what is closed or terminated, and what that costs.</summary>
    private string KillQuestion(int processId, KillTargetCheck target, bool forceKill)
    {
        var windows = _windowService?.FindWindows(new WindowQuery { ProcessId = processId })
            .Select(w => w.Title).Where(t => !string.IsNullOrWhiteSpace(t)).Distinct().Take(5).ToList() ?? [];
        var windowText = windows.Count == 0 ? string.Empty : $" Its windows: {string.Join(", ", windows.Select(t => UserConfirmation.Quote(t)))}.";

        if (!forceKill)
        {
            return $"The AI agent asks to close '{target.ProcessName}' (PID {processId}): its main window is asked to close, and the app " +
                   $"may ask to save your work.{windowText} Allow it?";
        }

        var children = target.ChildProcesses;
        var childText = children.Count == 0
            ? string.Empty
            : $" It also ends its {children.Count} child process{(children.Count == 1 ? string.Empty : "es")}: " +
              string.Join(", ", children.Take(10).Select(c => $"{c.ImageName} (PID {c.ProcessId})")) +
              (children.Count > 10 ? $" and {children.Count - 10} more" : string.Empty) + ".";
        return $"The AI agent asks to terminate '{target.ProcessName}' (PID {processId}) at once. Unsaved work in it is lost.{childText}" +
               $"{windowText} Allow it?";
    }

    [McpServerTool(Name = "launch_applications_batch", Title = "Launch Applications (Batch)", ReadOnly = false, Destructive = true, Idempotent = false, OpenWorld = true)]
    [Description("Launch multiple applications in a single batch operation. " +
                 "Each item is launched as by launch_application, in order; with waitForWindows the windows are then waited for together, " +
                 "and each result has its own launchStatus, window and errorCode. " +
                 "An item that is null fails alone with InvalidArguments; the others are still launched. " +
                 "Each item can have a placement, reuseExisting and forceAccessibility, as launch_application takes them: an item " +
                 "whose placement is invalid fails alone and is not launched, and at most one item may ask for focus (InvalidArguments, " +
                 "nothing launched). With a placement on any item every window is waited for; the windows are placed after all of them " +
                 "were found, in order, and checked again together once.")]
    public async Task<BatchLaunchResult> LaunchApplicationsBatch(
        [Description("The applications to launch: executable, and optionally arguments, workingDirectory, expectWindow, placement, " +
                     "reuseExisting and forceAccessibility")]
        ApplicationLaunchRequest[] applications,
        [Description("Whether to wait for each application window to appear (default: false)")]
        bool waitForWindows = false,
        [Description("Maximum time in milliseconds to wait for the windows, all together (0-30000, default: 5000)")]
        int waitTimeoutMs = 5000,
        IProgress<ProgressNotificationValue>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (applications == null)
        {
            throw new ToolErrorException(ToolErrorReporting.InvalidArgumentsErrorCode,
                "applications must be an array of items, not null. Nothing was launched.");
        }

        // Only one window can end in front: two items asking for focus contradict each other, so nothing is launched
        var focusing = applications.Select((a, i) => (Item: a, Index: i)).Where(t => t.Item?.Placement?.Focus == true).Select(t => t.Index).ToList();
        if (focusing.Count > 1)
        {
            throw new ToolErrorException(ToolErrorReporting.InvalidArgumentsErrorCode,
                $"Items {string.Join(", ", focusing)} all ask for placement.focus, but only one window can end in front. Nothing was launched.");
        }

        // A null item, or one whose placement is invalid, fails alone (roadmap M2.1) and is not launched; the others go to the
        // service, in request order
        var refusals = new LaunchResultItem?[applications.Length];
        var plans = new LaunchPlacementPlan?[applications.Length];
        var monitorCount = applications.Any(a => a?.Placement?.MonitorIndex != null) ? MonitorCount() : null;
        for (var i = 0; i < applications.Length; i++)
        {
            var application = applications[i];
            if (application == null)
            {
                refusals[i] = new LaunchResultItem(string.Empty, false,
                    Error: $"Item {i} of applications is null; each item needs an executable. Nothing was launched for it.",
                    ErrorCode: ToolErrorReporting.InvalidArgumentsErrorCode);
            }
            else if (application.Placement != null &&
                     !TryPlan(application.Placement, monitorCount, out plans[i], out var errorCode, out var error))
            {
                refusals[i] = new LaunchResultItem(application.Executable, false,
                    Error: $"Item {i}: {error} Nothing was launched for it.", ErrorCode: errorCode);
            }
        }

        var requested = Enumerable.Range(0, applications.Length).Where(i => refusals[i] == null).ToList();
        var serviceApplications = requested
            .Select(i => applications[i])
            .Select(a => new LaunchRequest(a.Executable, a.Arguments, a.WorkingDirectory, a.ExpectWindow, a.ReuseExisting, a.ForceAccessibility))
            .ToList();

        // Started one after another, then the windows are waited for together; the client gets "i of n" as each one is
        // done, and a cancelled call starts no more. A placement needs the window, so any placement waits for every window
        var results = await _processService.LaunchApplicationsBatchAsync(
            serviceApplications, waitForWindows || requested.Any(i => plans[i] != null), BoundedWait(waitTimeoutMs),
            ToolProgress.ForServices(progress), cancellationToken)
            .ConfigureAwait(false);

        // Then the windows are placed, after every launch has its window, so a placement does not race another item's wait
        var placed = await PlaceAsync(results.Select((r, k) => (r, plans[requested[k]])).ToList(), cancellationToken).ConfigureAwait(false);

        var items = new List<LaunchResultItem>(applications.Length);
        var next = 0;
        for (var i = 0; i < applications.Length; i++)
        {
            if (refusals[i] is { } refusal)
            {
                items.Add(refusal);
                continue;
            }

            if (next >= results.Count)
            {
                continue;
            }

            var r = results[next];
            items.Add(new LaunchResultItem(
                Executable: applications[i].Executable,
                Success: r.Success,
                ProcessId: r.ProcessId,
                WindowHandle: r.WindowHandle?.ToInt64(),
                Error: r.ErrorMessage,
                Warning: r.Warning,
                ErrorCode: r.ErrorCode,
                LaunchStatus: r.Status?.ToWireName(),
                Window: r.Window is { } window ? WindowFinderTools.ToDto(window) : null,
                LauncherExited: r.LauncherExited,
                Placement: placed[next],
                ForceAccessibilityApplied: r.ForceAccessibilityApplied));
            next++;
        }

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

/// <summary>
/// launch_application's result. launchStatus says what the launch led to (see <see cref="LaunchStatus"/>); window is the
/// window it found (with its real owner process, which can differ from processId), and windowHandle its handle;
/// launcherExited says whether the started process had exited by the end of the window wait. placement is what the
/// placement did, when one was asked for and the launch succeeded (roadmap M3.13). forceAccessibilityApplied says whether
/// the program was started with --force-renderer-accessibility, when forceAccessibility was asked for (roadmap M3.3).
/// </summary>
public record LaunchResultDto(
    bool Success,
    int? ProcessId = null,
    long? WindowHandle = null,
    string? Error = null,
    string? ErrorCode = null,
    string? Warning = null,
    string? LaunchStatus = null,
    WindowInfoDto? Window = null,
    bool? LauncherExited = null,
    LaunchPlacementResultDto? Placement = null,
    bool? ForceAccessibilityApplied = null
);

/// <summary>
/// What a launch's placement did (roadmap M3.13): the window as it is afterwards (finalBounds its visible frame, none when
/// minimized; finalState normal, minimized or maximized; monitorIndex), whether it is in front (focused), and whether it
/// was placed a second time because the app moved it back (reapplied). errorCode and error say why the placement failed;
/// the launch itself still succeeded, so the window is arranged with the window tools, never by launching again.
/// </summary>
public record LaunchPlacementResultDto(
    BoundsDto? FinalBounds,
    string? FinalState,
    int? MonitorIndex,
    bool Focused,
    bool Reapplied,
    string? ErrorCode = null,
    string? Error = null
);

public record ProcessInfoDto(
    int ProcessId,
    string Name,
    string? ExecutablePath,
    int WindowCount,
    double? MemoryUsageMB
);
