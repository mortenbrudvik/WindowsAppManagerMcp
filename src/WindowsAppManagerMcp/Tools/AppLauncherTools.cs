using System.ComponentModel;
using System.Globalization;
using Microsoft.Extensions.Logging;
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
        "frame, as set_window_bounds), monitor, state (normal, maximized or minimized) and focus (as focus_window). Checked " +
        "before anything is launched: unknown values fail with InvalidSnapPosition or InvalidState, contradictions (snap with " +
        "bounds, bounds with monitor, maximized with snap or bounds, focus with minimized, or nothing asked) with " +
        "InvalidArguments, a monitor that does not exist with InvalidMonitorIndex, MonitorNotFound or AmbiguousMonitor. The " +
        "window is then watched for up to 3 s and placed again each time the app moves it back, up to 3 times (reapplied: true)";

    private const string ReuseDescription =
        "Use an open window of the app instead of starting another copy (default false): a window on the current desktop, not " +
        "the agent's own, of a process with the program's path or name (calc: CalculatorApp, wt: WindowsTerminal; " +
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

    /// <summary>
    /// The most processes whose command lines list_processes reads by a name filter (roadmap M6.6): includeCommandLine needs
    /// processId, or nameFilter with a limit up to this.
    /// </summary>
    public const int MaxCommandLineProcesses = 20;

    private readonly IProcessService _processService;
    private readonly IWindowService? _windowService;
    private readonly IInputSession? _session;
    private readonly TimeSpan _answerTimeout;
    private readonly TimeSpan? _gateWaitTimeout;
    private readonly ILaunchPlacementService? _placementService;
    private readonly IMonitorService? _monitorService;
    private readonly IInstalledAppsService? _installedApps;
    private readonly ILogger<AppLauncherTools>? _logger;

    /// <param name="processService">Launches, lists and ends processes.</param>
    /// <param name="windowService">Names the windows of a process in kill_process's question to the user.</param>
    /// <param name="session">The input session, whose pause kill_process respects (it takes the input gate itself).</param>
    /// <param name="answerTimeout">How long kill_process waits for the user's answer; default <see cref="UserConfirmation.DefaultAnswerTimeout"/>.</param>
    /// <param name="gateWaitTimeout">How long kill_process waits for the input gate; default <see cref="InputGateToolFilter.GateWaitTimeout"/>.</param>
    /// <param name="placementService">Places the launches' windows (placement, roadmap M3.13). The server always passes it.</param>
    /// <param name="monitorService">Checks a placement's monitorIndex before anything is launched; without it the placement reports it.</param>
    /// <param name="installedApps">Lists the installed apps (list_installed_apps, roadmap M6.5). The server always passes it.</param>
    /// <param name="logger">Logs a placement that throws, with its exception (review F5); the server passes it.</param>
    public AppLauncherTools(
        IProcessService processService,
        IWindowService? windowService = null,
        IInputSession? session = null,
        TimeSpan? answerTimeout = null,
        TimeSpan? gateWaitTimeout = null,
        ILaunchPlacementService? placementService = null,
        IMonitorService? monitorService = null,
        IInstalledAppsService? installedApps = null,
        ILogger<AppLauncherTools>? logger = null)
    {
        _processService = processService;
        _windowService = windowService;
        _session = session;
        _answerTimeout = answerTimeout ?? UserConfirmation.DefaultAnswerTimeout;
        _gateWaitTimeout = gateWaitTimeout;
        _placementService = placementService;
        _monitorService = monitorService;
        _installedApps = installedApps;
        _logger = logger;
    }

    private const string RunAsAdminDescription =
        "Start the .exe as administrator (default false). Windows asks the user (UAC); where it would not, the user is asked " +
        "here. Not with placement, reuseExisting or a target that is no .exe (InvalidArguments). Input to the elevated window " +
        "is refused (TargetElevated)";

    /// <summary>The most apps list_installed_apps returns in one call.</summary>
    public const int MaxInstalledApps = 200;

    [McpServerTool(Name = "launch_application", Title = "Launch Application", ReadOnly = false, Destructive = true, Idempotent = false, OpenWorld = true)]
    [Description("Launch an application by executable path, name, installed app (appId or exact name from list_installed_apps; a packaged " +
                 "app returns its own processId), or protocol URI (http/https, mailto, tel, ms-settings: Settings pages, and the schemes " +
                 "the user allows). Returns launchStatus and the process ID. " +
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
        [Description("Path to executable, application name, appId, or protocol URI (e.g., 'notepad', 'C:\\Program Files\\App\\app.exe', 'Microsoft.WindowsCalculator_8wekyb3d8bbwe!App', 'https://example.com', 'ms-settings:display'). A bare name is looked up in the Windows folder, System32, PATH and App Paths, never in the current folder, then as an installed app's exact name (AmbiguousApp when several have it); else ExecutableNotFound")]
        string executable,
        [Description("Command line arguments, one array element per argument (quoting is handled for you; do not add quotes). No commands or scripts are run: an interpreter (cmd, powershell, wscript, python, ...) with arguments fails with InterpreterWithArguments, a script (.bat, .cmd, .ps1; for code use code.exe) with ScriptFile. For a target that is not an .exe, arguments with & | < > ^ % ! or \" fail with UnsafeBatchArguments. Chromium/Electron switches that run commands or allow remote control (--gpu-launcher, --*-cmd-prefix, --remote-debugging-*, --inspect, ...) fail with UnsafeChromiumSwitch.")]
        string[]? arguments = null,
        [Description("Working directory for the application (default: the program's own folder)")]
        string? workingDirectory = null,
        [Description("Whether to wait for the application window to appear before returning (default: false)")]
        bool waitForWindow = false,
        [Description("Maximum time in milliseconds to wait for the window (0-30000, default: 5000)")]
        int waitTimeoutMs = 5000,
        [Description("With waitForWindow or placement: the window to expect when the server cannot tell it from the started process, such as a URI or a document opened by whatever app is registered for it; a new window that matches every field set counts as the launch's window. With reuseExisting: the open window must match it (a web address needs titleContains)")]
        LaunchWindowHint? expectWindow = null,
        [Description(PlacementDescription)]
        LaunchPlacement? placement = null,
        [Description(ReuseDescription)]
        bool reuseExisting = false,
        [Description(ForceAccessibilityDescription)]
        bool forceAccessibility = false,
        [Description(RunAsAdminDescription)]
        bool runAsAdmin = false,
        McpServer? server = null,
        CancellationToken cancellationToken = default)
    {
        // Nothing while the user has paused input, not even a question
        InputGateToolFilter.ThrowIfPaused(_session);

        // A placement is checked before anything is launched; a bad one launches nothing
        LaunchPlacementPlan? plan = null;
        if (placement != null)
        {
            if (runAsAdmin)
            {
                throw new ToolErrorException(ToolErrorReporting.InvalidArgumentsErrorCode,
                    "runAsAdmin cannot be combined with placement: the server cannot reliably move or focus a window that runs above " +
                    "it. Launch without placement, and ask the user to arrange the window. Nothing was launched.");
            }

            if (!LaunchPlacing.TryPlan(placement, _monitorService, out plan, out var errorCode, out var error))
            {
                throw new ToolErrorException(errorCode!, $"{error} Nothing was launched.");
            }
        }

        var request = new LaunchRequest(executable, arguments, workingDirectory, expectWindow, reuseExisting, forceAccessibility)
        {
            RunAsAdmin = runAsAdmin
        };

        // Owner decision D11: where Windows would elevate without a UAC prompt, the user approves instead, through the client,
        // before the input gate is taken (a user who takes minutes to answer must not hold up every input and window call).
        // The launch is planned first, so the user sees the program it resolves to and its arguments, and only that plan runs
        // (review M6.5 #2)
        ElevationApproval? approved = null;
        if (runAsAdmin && _processService.PreviewElevation(request) is { } preview)
        {
            if (preview.ErrorCode is { } refusedCode)
            {
                return new LaunchResultDto(false, Error: preview.Error, ErrorCode: refusedCode);
            }

            if (preview is { NeedsConfirmation: true, Launch: { } launch } && UserConfirmation.CanAsk(server))
            {
                var answer = await UserConfirmation.AskAsync(
                    server!, ElevationQuestion(launch, preview.Reason), "Start as administrator", _answerTimeout, cancellationToken)
                    .ConfigureAwait(false);
                if (!answer.Confirmed)
                {
                    return new LaunchResultDto(false,
                        Error: $"{answer.Reason} Nothing was started. Ask the user how to continue.",
                        ErrorCode: nameof(ProcessErrorCode.ConfirmationDeclined));
                }

                approved = launch;
            }
        }

        // The launch and the placement run inside the input gate, as every window-changing tool does. An approval is not kept
        // for a retry after InputBusy: the retry asks the user again (review M6.5 #9)
        IDisposable gate;
        try
        {
            gate = await InputGateToolFilter.EnterAsync(_session, cancellationToken, _gateWaitTimeout).ConfigureAwait(false);
        }
        catch (ToolErrorException busy) when (approved != null)
        {
            throw ElevationApprovalLost(busy);
        }

        using var _ = gate;
        var result = await _processService.LaunchApplicationAsync(
            request with { ApprovedElevation = approved },
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
            ForceAccessibilityApplied: result.ForceAccessibilityApplied,
            Elevation: ElevationOf(runAsAdmin, result.Success, approved != null)
        );
    }

    /// <summary>
    /// How an elevated launch was approved, for the result and the audit line (review M6.5 #8): "confirmed" when the user said
    /// yes through the client, "uacPrompt" when Windows' own UAC prompt asks them; null when nothing was elevated.
    /// </summary>
    private static string? ElevationOf(bool runAsAdmin, bool success, bool confirmed) =>
        !runAsAdmin || !success ? null : confirmed ? "confirmed" : "uacPrompt";

    /// <summary>
    /// The InputBusy (or pause) a launch the user approved got while it waited for the input gate, saying that the approval
    /// was not kept: a retry asks the user again (review M6.5 #9).
    /// </summary>
    public static ToolErrorException ElevationApprovalLost(ToolErrorException refusal)
    {
        ArgumentNullException.ThrowIfNull(refusal);
        return new ToolErrorException(refusal.ErrorCode,
            $"{refusal.Message} The user's approval to start the program as administrator was not kept: they are asked again on a retry.");
    }

    /// <summary>The longest command line an elevation question shows, after secret-looking values are masked.</summary>
    private const int MaxQuestionArgumentsLength = 200;

    /// <summary>
    /// The question runAsAdmin asks the user where Windows would elevate without asking (roadmap M6.5, review M6.5 #2): the
    /// program the launch resolved to (its file name whole, the middle of its folder shortened when needed), its command line
    /// with secret-looking values masked (CommandLineRedactor) and cut to 200 characters, and why Windows will not ask.
    /// </summary>
    private static string ElevationQuestion(ElevationApproval launch, string? reason)
    {
        var argumentText = launch.Arguments.Length == 0
            ? "with no arguments"
            : $"with the arguments {UserConfirmation.Quote(AppText.Cut(CommandLineRedactor.Redact(launch.Arguments).Text, MaxQuestionArgumentsLength), MaxQuestionArgumentsLength + 2)}";
        return $"The AI agent asks to start {UserConfirmation.QuotePath(launch.FileName)} {argumentText} as administrator. " +
               $"Windows will not ask you first: {reason ?? "it cannot be told whether it would"}. The program gets full control " +
               "of the computer. Allow it?";
    }

    [McpServerTool(Name = "list_installed_apps", Title = "List Installed Apps", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("List the installed apps of the Start menu, packaged apps first, each with name (localized), appId, packaged, and path " +
                 "for a desktop app. Launch one with launch_application: executable = appId for a packaged app, path for a desktop app, " +
                 "or the exact name. total and truncated tell when limit cut the list.")]
    public InstalledAppsResultDto ListInstalledApps(
        [Description("Only apps whose name or appId contains this text (case-insensitive)")]
        string? nameFilter = null,
        [Description("Return at most this many apps (1-200, default 25)")]
        int limit = 25)
    {
        if (limit is < 1 or > MaxInstalledApps)
        {
            throw new ToolErrorException(ToolErrorReporting.InvalidArgumentsErrorCode,
                $"limit must be between 1 and {MaxInstalledApps}, but it is {limit}. Use nameFilter to narrow the list.");
        }

        var listing = _installedApps?.List(nameFilter, limit) ?? new InstalledAppsListing([], 0, false, Unreadable: true);
        return new InstalledAppsResultDto(
            listing.Apps.Select(a => new InstalledAppDto(a.Name, a.AppId, a.Packaged, a.Path)).ToList(),
            listing.Total,
            listing.Truncated,
            listing.Unreadable
                ? "The Start menu's apps could not be read in time; try again, or launch the program by its path."
                : listing.SkippedApps > 0
                    ? $"{listing.SkippedApps} of the Start menu's apps could not be read and are missing; launch_application does " +
                      "not resolve an app's name or appId until the next read (30 seconds) reads them all."
                    : null);
    }

    /// <summary>
    /// Places the windows of the launches that have a plan (roadmap M3.13) through <see cref="LaunchPlacing.PlaceAsync"/>,
    /// and returns each launch's placement result, in order.
    /// </summary>
    private Task<IReadOnlyList<LaunchPlacementResultDto?>> PlaceAsync(
        IReadOnlyList<(LaunchResult Result, LaunchPlacementPlan? Plan)> launches, CancellationToken cancellationToken) =>
        LaunchPlacing.PlaceAsync(
            _placementService,
            launches.Select(l => new LaunchToPlace(l.Result.Success, l.Result.Window?.Handle, l.Result.Status, l.Plan)).ToList(),
            cancellationToken,
            _logger);

    [McpServerTool(Name = "list_processes", Title = "List Processes", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("List running processes, by name unless sortBy says otherwise. Each has processId, name, executablePath, memoryUsageMB, " +
                 "parentProcessId (left out when unknown: the parent exited, or a start time cannot be read), startTime (UTC), integrityLevel and elevated, aboveServer: true when it " +
                 "runs above this server (input and UI Automation to its windows are refused: TargetElevated), windowCount and windows " +
                 "(handle and title of up to 10, front to back). Only processes with a visible, titled window are listed, including windows " +
                 "on other virtual desktops and tool windows (a UWP app's window also counts for ApplicationFrameHost); includeWindowless=true " +
                 "lists every process, so processId with includeWindowless=true confirms that a process has exited.")]
    public IReadOnlyList<ProcessInfoDto> ListProcesses(
        [Description("Filter processes by name (partial match, case-insensitive)")]
        string? nameFilter = null,
        [Description("Also list processes without a visible, titled window (default: false)")]
        bool includeWindowless = false,
        [Description("Only the process with this ID")]
        int? processId = null,
        [Description("Only processes whose executable path contains this text (case-insensitive); a process whose path cannot be read is left out")]
        string? pathContains = null,
        [Description("Order: 'name' (default), 'memory' (largest first) or 'startTime' (newest first)")]
        ProcessSort sortBy = ProcessSort.Name,
        [Description("Return at most this many processes, the first in sortBy order (at least 1; default: all)")]
        int? limit = null,
        [Description("Also return each process's commandLine, cut to 500 characters (default: false); only with processId, or with " +
                     "nameFilter and a limit of at most 20. " +
                     "Values that look secret (password=, --token, Bearer, a URL's password) become *** with commandLineRedacted: true, " +
                     "but this is a heuristic. Left out when it cannot be read or the user's policy denies the process")]
        bool includeCommandLine = false)
    {
        if (limit is < 1)
        {
            throw new ToolErrorException(ToolErrorReporting.InvalidArgumentsErrorCode,
                $"limit must be at least 1, but it is {limit}. Leave it out for every process.");
        }

        // A name filter as short as "e" matches most processes, so it bounds nothing without a limit
        if (includeCommandLine && processId == null && (string.IsNullOrEmpty(nameFilter) || limit is not <= MaxCommandLineProcesses))
        {
            throw new ToolErrorException(ToolErrorReporting.InvalidArgumentsErrorCode,
                $"includeCommandLine needs processId, or nameFilter with a limit of at most {MaxCommandLineProcesses}, so that only " +
                "the command lines of the processes you are after are read. Nothing was listed.");
        }

        var processes = _processService.GetRunningProcesses(new ProcessQuery
        {
            NameFilter = nameFilter,
            IncludeWindowless = includeWindowless,
            ProcessId = processId,
            PathContains = pathContains,
            Sort = sortBy,
            Limit = limit,
            IncludeCommandLine = includeCommandLine
        });

        return processes.Select(p => new ProcessInfoDto(
            ProcessId: p.ProcessId,
            Name: p.Name,
            ExecutablePath: p.ExecutablePath,
            WindowCount: p.WindowCount,
            MemoryUsageMB: p.MemoryUsageMB.HasValue ? Math.Round(p.MemoryUsageMB.Value, 1) : null,
            ParentProcessId: p.ParentProcessId,
            StartTime: p.StartTime?.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture),
            IntegrityLevel: p.Integrity?.Level,
            Elevated: p.Integrity?.IsElevated,
            AboveServer: p.AboveServer ? true : null,
            Windows: p.Windows is { Count: > 0 } windows
                ? windows.Select(w => new ProcessWindowDto(w.Handle.ToInt64(), AppText.Cut(w.Title))).ToList()
                : null,
            CommandLine: p.CommandLine,
            CommandLineRedacted: p.CommandLineRedacted ? true : null
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
                 "The result reports closeRequested, forced and exited. Refuses protected system processes (ApplicationFrameHost too: close a Store app by its own processId), " +
                 "the agent's own host (the terminal or editor hosting this server; errorCode CannotTerminateHost; ProcessTreeUnreadable if that cannot be checked) " +
                 "and apps the user's policy denies (TargetDeniedByPolicy); with forceKill=true also when a child process it would end is protected, belongs to that host or is denied by the policy, " +
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
                 "were found, in order, and watched together until they stay put.")]
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
        for (var i = 0; i < applications.Length; i++)
        {
            var application = applications[i];
            if (application == null)
            {
                refusals[i] = new LaunchResultItem(string.Empty, false,
                    Error: $"Item {i} of applications is null; each item needs an executable. Nothing was launched for it.",
                    ErrorCode: ToolErrorReporting.InvalidArgumentsErrorCode);
            }
            else if (application.Placement != null && application.RunAsAdmin)
            {
                refusals[i] = new LaunchResultItem(application.Executable, false,
                    Error: $"Item {i}: runAsAdmin cannot be combined with placement: the server cannot reliably move or focus a window " +
                           "that runs above it. Nothing was launched for it.",
                    ErrorCode: ToolErrorReporting.InvalidArgumentsErrorCode);
            }
            else if (application.Placement != null &&
                     !LaunchPlacing.TryPlan(application.Placement, _monitorService, out plans[i], out var errorCode, out var error))
            {
                refusals[i] = new LaunchResultItem(application.Executable, false,
                    Error: $"Item {i}: {error} Nothing was launched for it.", ErrorCode: errorCode);
            }
        }

        var requested = Enumerable.Range(0, applications.Length).Where(i => refusals[i] == null).ToList();
        var serviceApplications = requested
            .Select(i => applications[i])
            .Select(a => new LaunchRequest(a.Executable, a.Arguments, a.WorkingDirectory, a.ExpectWindow, a.ReuseExisting, a.ForceAccessibility)
            {
                RunAsAdmin = a.RunAsAdmin
            })
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
            var elevated = applications[i].RunAsAdmin;
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
                ForceAccessibilityApplied: r.ForceAccessibilityApplied,
                // A batch never asks the user, so a successful elevation is one Windows asked about (review M6.5 #8)
                Elevation: ElevationOf(elevated, r.Success, confirmed: false)));
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
/// elevation says how a runAsAdmin start was approved (roadmap M6.5): "confirmed" by the user through the client, or
/// "uacPrompt" when Windows asks them with its UAC prompt.
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
    bool? ForceAccessibilityApplied = null,
    string? Elevation = null
);

/// <summary>
/// What a launch's placement did (roadmap M3.13): the window as it is afterwards (finalBounds its visible frame, none when
/// minimized; finalState normal, minimized or maximized; monitorIndex), whether it is in front (focused), and whether it
/// was placed again because the app moved it back (reapplied). errorCode and error say why the placement failed;
/// the launch itself still succeeded, so the window is arranged with the window tools, never by launching again.
/// warning says that the app kept moving its window back, so it was left where the app put it (roadmap M3.F27).
/// </summary>
public record LaunchPlacementResultDto(
    BoundsDto? FinalBounds,
    string? FinalState,
    int? MonitorIndex,
    bool Focused,
    bool Reapplied,
    string? ErrorCode = null,
    string? Error = null,
    string? Warning = null
);

/// <summary>
/// A process as list_processes returns it. What is unknown or empty is left out: executablePath, memoryUsageMB,
/// parentProcessId and startTime when they cannot be read, integrityLevel and elevated without a security probe (elevated
/// also when the token cannot be read), windows when it has none, the command line unless asked for. aboveServer and
/// commandLineRedacted are only ever true. startTime is UTC, to the second.
/// </summary>
public record ProcessInfoDto(
    int ProcessId,
    string Name,
    string? ExecutablePath,
    int WindowCount,
    double? MemoryUsageMB,
    int? ParentProcessId = null,
    string? StartTime = null,
    IntegrityLevel? IntegrityLevel = null,
    bool? Elevated = null,
    bool? AboveServer = null,
    IReadOnlyList<ProcessWindowDto>? Windows = null,
    string? CommandLine = null,
    bool? CommandLineRedacted = null
);

/// <summary>One of a process's windows in list_processes (roadmap M6.6): its handle and its title, cut as the window tools cut it.</summary>
public record ProcessWindowDto(long Handle, string Title);

/// <summary>
/// list_installed_apps' result (roadmap M6.5): the apps returned, how many matched in all, whether limit cut the list, and a
/// warning when the Start menu's apps could not be read.
/// </summary>
public record InstalledAppsResultDto(IReadOnlyList<InstalledAppDto> Apps, int Total, bool Truncated, string? Warning = null);

/// <summary>An installed app: its name (localized), its AppUserModelID, whether it is packaged, and a desktop app's program or file.</summary>
public record InstalledAppDto(string Name, string AppId, bool Packaged, string? Path = null);
