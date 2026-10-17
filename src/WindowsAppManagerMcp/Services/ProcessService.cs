using System.ComponentModel;
using System.Diagnostics;
using WindowsAppManagerMcp.Infrastructure;
using WindowsAppManagerMcp.Models;
using WindowsAppManagerMcp.Models.Results;
using WindowsAppManagerMcp.Native;
using WindowsAppManagerMcp.Services.Interfaces;

namespace WindowsAppManagerMcp.Services;

public class ProcessService : IProcessService
{
    // Characters cmd.exe interprets in a command line, even inside quotes for % and !.
    // Control characters (line breaks in particular) are rejected as well.
    private static readonly char[] CmdMetacharacters = ['&', '|', '<', '>', '^', '%', '!', '"'];

    private const int MaxArgumentLengthInMessages = 80;

    private readonly IWindowService _windowService;
    private readonly IInputValidationService _validationService;
    private readonly IBrowserDetectionService _browserDetection;
    private readonly IExecutableResolver _executableResolver;
    private readonly INativeProcessWrapper _processWrapper;
    private readonly ITargetPolicy? _targetPolicy;
    private readonly IHostGuard? _hostGuard;
    private readonly IWindowWaitService _windowWait;
    private readonly ChromiumPrograms _chromiumPrograms;
    private readonly InterpreterGuard _interpreterGuard;
    private readonly DebugBrowserLocator _debugBrowserLocator;
    private readonly IDebugBrowserReadiness? _debugBrowserReadiness;
    private readonly DebugBrowserProfiles _debugBrowserProfiles;
    private readonly IProcessSecurityProbe? _securityProbe;
    private readonly INativeAppsWrapper? _appsWrapper;
    private readonly IInstalledAppsService? _installedApps;

    /// <param name="targetPolicy">
    /// The server's target policy, which kill_process enforces (roadmap M1.12), and which keeps list_processes from
    /// reading a denied process's command line (roadmap M6.6). The server always passes it; without it no process is
    /// refused by policy.
    /// </param>
    /// <param name="hostGuard">
    /// The agent's host as recorded at server start (roadmap M1.1/M1.7): kill_process refuses its
    /// processes, and a forced kill whose process tree contains one. The server always passes it; without
    /// it only this server and its live ancestors count as the host.
    /// </param>
    /// <param name="windowWait">
    /// Finds the window a launch opens (roadmap M2.6). The server always passes it; without it a wait reads the real
    /// desktop through a <see cref="NativeWindowWrapper"/> created here (eagerly, so that the E2E reference walk sees it).
    /// </param>
    /// <param name="chromiumPrograms">Tells which programs forceAccessibility may add --force-renderer-accessibility to
    /// (roadmap M3.3); default the real file system.</param>
    /// <param name="interpreterGuard">Refuses an interpreter with arguments and a script (roadmap M3.F8); default one that
    /// expands 8.3 short names from the real file system.</param>
    /// <param name="debugBrowserLocator">Finds the browser for launch_browser_with_debug (roadmap M5.1); default the real
    /// registry and file system.</param>
    /// <param name="debugBrowserReadiness">Confirms that a debug browser's DevTools endpoint answers before the launch
    /// reports success (roadmap M5.1); without it the launch is reported once the browser's port is known.</param>
    /// <param name="debugBrowserProfiles">Where a debug launch's new temporary profile goes, and deletes it again (roadmap
    /// M5.4); default the server's folders under %TEMP% and %LOCALAPPDATA%.</param>
    /// <param name="securityProbe">Reads the listed processes' integrity (roadmap M6.6), the server's own (an elevated server
    /// cannot activate packaged apps) and whether Windows asks before it elevates (runAsAdmin, roadmap M6.5). The server always
    /// passes it; without it list_processes reports no integrity, and runAsAdmin needs the user's confirmation.</param>
    /// <param name="appsWrapper">Activates packaged apps and reads which package a process belongs to (roadmap M6.5). The
    /// server always passes it; without it a packaged app is started through the shell, as before, without a process ID.</param>
    /// <param name="installedApps">The installed apps (roadmap M6.5): a bare name that is no program on disk is looked up by
    /// its exact display name, and an app's program is read before arguments are passed to it. The server always passes it;
    /// without it no name is looked up and no argument is passed to an app whose program the server cannot tell.</param>
    public ProcessService(
        IWindowService windowService,
        IInputValidationService validationService,
        IBrowserDetectionService browserDetection,
        IExecutableResolver? executableResolver = null,
        INativeProcessWrapper? processWrapper = null,
        ITargetPolicy? targetPolicy = null,
        IHostGuard? hostGuard = null,
        IWindowWaitService? windowWait = null,
        ChromiumPrograms? chromiumPrograms = null,
        InterpreterGuard? interpreterGuard = null,
        DebugBrowserLocator? debugBrowserLocator = null,
        IDebugBrowserReadiness? debugBrowserReadiness = null,
        DebugBrowserProfiles? debugBrowserProfiles = null,
        IProcessSecurityProbe? securityProbe = null,
        INativeAppsWrapper? appsWrapper = null,
        IInstalledAppsService? installedApps = null)
    {
        _windowService = windowService;
        _validationService = validationService;
        _browserDetection = browserDetection;
        _executableResolver = executableResolver ?? new ExecutableResolver();
        _processWrapper = processWrapper ?? new NativeProcessWrapper();
        _targetPolicy = targetPolicy;
        _hostGuard = hostGuard;
        _windowWait = windowWait ?? new WindowWaitService(new NativeWindowWrapper(), windowService, _processWrapper);
        _chromiumPrograms = chromiumPrograms ?? new ChromiumPrograms();
        _interpreterGuard = interpreterGuard ?? new InterpreterGuard();
        _debugBrowserLocator = debugBrowserLocator ?? new DebugBrowserLocator();
        _debugBrowserReadiness = debugBrowserReadiness;
        _debugBrowserProfiles = debugBrowserProfiles ?? DebugBrowserProfiles.FromEnvironment();
        _securityProbe = securityProbe;
        _appsWrapper = appsWrapper;
        _installedApps = installedApps;
    }

    /// <summary>
    /// How long a start may take before the launch reports <see cref="LaunchStatus.AwaitingUserConsent"/>: ShellExecute does
    /// not return while Windows asks the user something (a UAC prompt for an elevated program, SmartScreen, a security
    /// warning, an Open with dialog), which can take any time.
    /// </summary>
    public static readonly TimeSpan DefaultStartTimeout = TimeSpan.FromSeconds(5);

    /// <summary>How long a start may take (default <see cref="DefaultStartTimeout"/>); tests shorten it.</summary>
    public TimeSpan StartTimeout { get; init; } = DefaultStartTimeout;

    /// <summary>ERROR_CANCELLED: the user declined or cancelled a prompt of the start (most likely UAC).</summary>
    private const int ErrorCancelled = 1223;

    /// <summary>
    /// How long a packaged app's activation may take (roadmap M6.5): IApplicationActivationManager returns once the app is
    /// running, and a cold start with a splash screen takes seconds. Within the launch tools' 30-second cap.
    /// </summary>
    public static readonly TimeSpan DefaultActivationTimeout = TimeSpan.FromSeconds(15);

    /// <summary>How long an activation may take (default <see cref="DefaultActivationTimeout"/>); tests shorten it.</summary>
    public TimeSpan ActivationTimeout { get; init; } = DefaultActivationTimeout;

    /// <summary>
    /// The HRESULTs with which Windows refuses to activate a packaged app from this server's process, though the shell
    /// can start it (roadmap M6.5): E_ELEVATED_ACTIVATION_NOT_SUPPORTED, E_UAC_DISABLED and E_FULL_ADMIN_NOT_SUPPORTED.
    /// </summary>
    private static readonly HashSet<int> ActivationRefusedForElevation =
        [unchecked((int)0x80270251), unchecked((int)0x80270252), unchecked((int)0x80270253)];

    /// <summary>E_APPLICATION_NOT_REGISTERED: no app with this AppUserModelID is installed for the user.</summary>
    private const int ApplicationNotRegistered = unchecked((int)0x80270254);

    public bool ElevationNeedsConfirmation() =>
        (_securityProbe?.GetElevationPrompt() ?? ElevationPrompt.Unknown) != ElevationPrompt.Prompts;

    /// <summary>Whether the server itself runs elevated; false when that cannot be read.</summary>
    private bool ServerIsElevated() =>
        _securityProbe?.ServerIntegrity is { } server && (server.IsElevated == true || server.IntegrityRid >= ProcessIntegrity.HighRid);

    public Task<LaunchResult> LaunchApplicationAsync(
        string executable,
        string[]? arguments = null,
        string? workingDirectory = null,
        bool waitForWindow = false,
        int waitTimeoutMs = 5000,
        CancellationToken cancellationToken = default) =>
        LaunchApplicationAsync(new LaunchRequest(executable, arguments, workingDirectory), waitForWindow, waitTimeoutMs, cancellationToken);

    public async Task<LaunchResult> LaunchApplicationAsync(
        LaunchRequest request,
        bool waitForWindow,
        int waitTimeoutMs,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var plan = PlanFor(request);
        if (!plan.IsValid)
        {
            return Rejected(plan);
        }

        // A call cancelled before the start starts nothing
        cancellationToken.ThrowIfCancellationRequested();

        if (request.ReuseExisting)
        {
            var reuse = FindOpenWindow(plan, request, taken: null, openBefore: null);
            if (reuse.Result is { } alreadyOpen)
            {
                return Finish(alreadyOpen, plan);
            }

            plan = plan with { Warning = CombineWarnings(plan.Warning, reuse.Note) };
        }

        plan = plan with { RunningInstance = RunningInstanceOf(plan) };
        var launch = await StartAsync(plan, waitForWindow, cancellationToken, request.OnStartAbandoned).ConfigureAwait(false);
        if (launch.Outcome is { } ended)
        {
            return Finish(ended, plan);
        }

        if (launch.Process != null)
        {
            request.OnStarted?.Invoke(launch.Process.Id);
        }

        using (launch.Process)
        {
            // Cancelled while waiting for the window: the program was started and keeps running, and the call throws
            // (launch_browser_with_debug closes its browser then, through LaunchRequest.OnStarted)
            if (!waitForWindow)
            {
                return Finish(StartedWithoutWait(launch), plan);
            }

            var owner = OwnerOf(launch, request);
            return Finish(
                await WaitForWindowAsync(launch, request, owner, owner.Matches, waitTimeoutMs, tryClaim: null, cancellationToken)
                    .ConfigureAwait(false),
                plan);
        }
    }

    /// <summary>
    /// Plans a request (<see cref="PlanLaunch"/>) with its runAsAdmin, which reuseExisting contradicts: an open window is
    /// not the new elevated copy.
    /// </summary>
    private LaunchPlan PlanFor(LaunchRequest request)
    {
        if (request.RunAsAdmin && request.ReuseExisting)
        {
            return LaunchPlan.Rejected(
                "runAsAdmin starts a new copy of the program as administrator, and reuseExisting would use an open window instead: " +
                "leave one of them out. Nothing was started.",
                ToolErrorReporting.InvalidArgumentsErrorCode);
        }

        return PlanLaunch(request.Executable, request.Arguments, request.WorkingDirectory, request.ForceAccessibility,
            request.RunAsAdmin, request.ElevationConfirmed);
    }

    /// <summary>What a launch reports besides its own outcome: whether forceAccessibility took effect, and an elevated start.</summary>
    private LaunchResult Finish(LaunchResult result, LaunchPlan plan) => WithElevationNote(WithForceAccessibility(result, plan), plan);

    /// <summary>
    /// runAsAdmin (roadmap M6.5): a program started as administrator runs above a server that is not elevated, so the input
    /// guard refuses input and element actions to its windows (TargetElevated), and Windows drops the server's close
    /// requests to them. Only when the start was handed to Windows.
    /// </summary>
    private LaunchResult WithElevationNote(LaunchResult result, LaunchPlan plan)
    {
        if (!result.Success || plan.StartInfo?.Verb != RunAsVerb || ServerIsElevated())
            return result;

        return result with
        {
            Warning = CombineWarnings(result.Warning,
                "runAsAdmin: once the user approves, the program runs as administrator, above this server, so input and element " +
                "actions to its windows are refused (TargetElevated) and close requests do not reach them. Ask the user to close it.")
        };
    }

    /// <summary>The ShellExecute verb that starts a program as administrator.</summary>
    private const string RunAsVerb = "runas";

    /// <summary>What to do so that --force-renderer-accessibility takes effect, when an instance of the app runs.</summary>
    private const string ForceAccessibilityAdvice =
        "To apply it, no process of the app may be left, not only no window: Edge's Startup boost and Chrome's background " +
        "apps keep one running without a window (list_processes with includeWindowless: true lists them; ask the user " +
        "before ending one). Then launch it again.";

    /// <summary>
    /// Says whether forceAccessibility took effect (roadmap M3.3), when it was asked for and the request was valid: false
    /// when nothing was started (reuseExisting found an open window) or the start failed. The flag only matters when the
    /// app's main process starts with it: when the launch went to an instance that was already running (a handoff, a
    /// reused window or a new tab, or a launcher that exited), that instance keeps the mode it started with, and the result
    /// warns about it. A launch that did not wait cannot tell yet, so it warns when a process of the program was running
    /// before the start (<see cref="LaunchPlan.RunningInstance"/>): the new process most likely hands off to it.
    /// </summary>
    private static LaunchResult WithForceAccessibility(LaunchResult result, LaunchPlan plan)
    {
        if (plan.ForceAccessibilityApplied is not { } applied)
        {
            return result;
        }

        if (!applied || !result.Success)
        {
            return result with { ForceAccessibilityApplied = false };
        }

        if (result.Status == LaunchStatus.AlreadyOpen)
        {
            return result with
            {
                ForceAccessibilityApplied = false,
                Warning = CombineWarnings(result.Warning,
                    "forceAccessibility was not applied: reuseExisting used an open window, so nothing was started.")
            };
        }

        var handedOff = result.Status is LaunchStatus.PossibleHandoff or LaunchStatus.ReusedExistingWindow or LaunchStatus.OpenedInTab ||
                        result.LauncherExited == true;
        var note = handedOff
            ? $"{ChromiumPrograms.ForceRendererAccessibility} was passed, but the launch went to an instance of the app that was " +
              $"already running, which keeps the accessibility mode it started with. {ForceAccessibilityAdvice}"
            : result.Status == LaunchStatus.Started && plan.RunningInstance is { } running
                ? $"{ChromiumPrograms.ForceRendererAccessibility} was passed, but the app was already running (process {running}, " +
                  "with or without a window), so the new process most likely hands its request to that instance and exits, and " +
                  $"the instance keeps the accessibility mode it started with. {ForceAccessibilityAdvice}"
                : null;
        return result with
        {
            ForceAccessibilityApplied = true,
            Warning = CombineWarnings(result.Warning, note)
        };
    }

    /// <summary>
    /// With forceAccessibility applied: a process of the program that is running before the start (its image path is the
    /// program's), from one process snapshot, or null when none is or the list cannot be read. Only reads.
    /// </summary>
    private int? RunningInstanceOf(LaunchPlan plan)
    {
        if (plan.ForceAccessibilityApplied != true || plan.Target is not { } target || !Path.IsPathFullyQualified(target.FileName))
            return null;

        var imageName = Path.GetFileName(target.FileName);
        foreach (var (processId, entry) in _processWrapper.GetProcessList() ?? new Dictionary<int, (int, string)>())
        {
            if (string.Equals(entry.ImageName, imageName, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(_processWrapper.GetProcessImagePath(processId), target.FileName, StringComparison.OrdinalIgnoreCase))
                return processId;
        }

        return null;
    }

    private static LaunchResult Rejected(LaunchPlan plan) =>
        new(false, ErrorMessage: plan.ErrorMessage, ErrorCode: plan.ErrorCode, Warning: plan.Warning);

    /// <summary>
    /// reuseExisting (roadmap M3.13): the open window to use instead of starting the program, as an
    /// <see cref="LaunchStatus.AlreadyOpen"/> result, or, when there is none, a note for the launch's warning (null when the
    /// server looked and no window of the app was open). A window counts when it is:
    /// <list type="bullet">
    /// <item>an application window the user can switch to (<see cref="IWindowService.GetApplicationWindows"/>: visible,
    /// not cloaked, so on the current virtual desktop, unowned, not a tool or shell window, not the server's own), minimized
    /// or not, that is not the agent's own host, not refused by the user's target policy, open before the batch started
    /// anything (<paramref name="openBefore"/>, null for a single launch) and not already used by an earlier item of the
    /// batch (<paramref name="taken"/>);</item>
    /// <item>of the same app: its process has the started program's path or name, or the name of the same app it hands
    /// off to (<see cref="LaunchWindowOwner.SameAppProcessNames"/>; never a terminal for a console program or a browser for
    /// a web address), and, when expectWindow names a process, that process (part of the name, also the frame host's). A
    /// launch whose app is not known (a document, a desktop app's shell:AppsFolder entry, a web address) needs
    /// expectWindow.processName; a packaged app's windows are those of its package's processes (roadmap M6.5);</item>
    /// <item>showing what the launch opens: its title contains expectWindow.titleContains when that is set; otherwise it
    /// shows every file and folder the launch opens (<see cref="ReuseTitleNames"/>) as <see cref="LaunchTitles.Shows"/>
    /// tells: as a whole name, so todo.txt is not shown by old-todo.txt or todo.txt.bak, and never after another path, so
    /// C:\notes\todo.txt is not shown by "C:\other\todo.txt - Notepad++". Starting an app (shell:AppsFolder\&lt;AUMID&gt;, a
    /// bare scheme such as ms-settings:) opens nothing; a launch that opens something whose name a title would not show (an
    /// ms-settings: page, a mailto: address) is never reused, nor is a web address: a page sets its own title, so a host
    /// name in it proves nothing.</item>
    /// </list>
    /// A window whose title shows the launched full paths goes before one that shows only their names; of several, the
    /// front-most in z-order (the one used last) is used, and the warning says how many matched. A window matched by the
    /// names alone, or by expectWindow.titleContains for a web address (a title the page sets), is used with a warning that
    /// says so and tells the agent to check it before acting.
    /// </summary>
    private (LaunchResult? Result, string? Note) FindOpenWindow(
        LaunchPlan plan, LaunchRequest request, IReadOnlySet<nint>? taken, IReadOnlySet<nint>? openBefore)
    {
        var target = plan.Target!;
        var hint = request.ExpectWindow is { IsEmpty: false } expected ? expected : null;
        var names = LaunchWindowOwner.SameAppProcessNames(target);
        var imagePath = target.Kind == LaunchTargetKind.NativeExecutable && Path.IsPathFullyQualified(target.FileName) ? target.FileName : null;
        var wantedProcess = string.IsNullOrWhiteSpace(hint?.ProcessName) ? null : StripExe(hint.ProcessName.Trim());
        var family = target.Kind == LaunchTargetKind.PackagedApp && _appsWrapper != null ? target.App?.PackageFamilyName : null;
        if (names.Count == 0 && imagePath == null && wantedProcess == null && family == null)
        {
            return (null, "reuseExisting: the server cannot tell which app opens this target, so it did not look for an open window " +
                          "and started it. Pass expectWindow.processName to reuse a window of that app.");
        }

        var titleContains = string.IsNullOrWhiteSpace(hint?.TitleContains) ? null : hint.TitleContains.Trim();
        var webAddress = WebAddressOf(target, request.Arguments);
        IReadOnlyList<DocumentHint> titleNames = [];
        if (titleContains == null)
        {
            if (webAddress != null)
            {
                return (null, "reuseExisting: the launch opens a web address, and a web page sets its own title, so a browser window " +
                              "is never reused because its title shows the host; it was started. Pass expectWindow.titleContains to " +
                              "name the window to reuse.");
            }

            if (ReuseTitleNames(target, request.Arguments, plan.StartInfo!.WorkingDirectory) is not { } shownNames)
            {
                return (null, "reuseExisting: the launch opens something whose name a window title would not show, so no open window " +
                              "was reused and it was started. Pass expectWindow.titleContains to name the window to reuse.");
            }

            titleNames = shownNames;
        }

        var nameSet = new HashSet<string>(names.Select(StripExe), StringComparer.OrdinalIgnoreCase);
        var appWindows = _windowService.GetApplicationWindows()
            .Where(w => !w.IsAgentHost && taken?.Contains(w.Handle) != true && openBefore?.Contains(w.Handle) != false)
            .Where(w => (names.Count == 0 && imagePath == null && family == null) || nameSet.Contains(StripExe(w.ProcessName)) ||
                        (imagePath != null && string.Equals(_processWrapper.GetProcessImagePath(w.ProcessId), imagePath, StringComparison.OrdinalIgnoreCase)) ||
                        (family != null && string.Equals(_appsWrapper!.GetPackageFamilyName(w.ProcessId), family, StringComparison.OrdinalIgnoreCase)))
            .Where(w => wantedProcess == null || w.ProcessName.Contains(wantedProcess, StringComparison.OrdinalIgnoreCase) ||
                        w.HostProcessName?.Contains(wantedProcess, StringComparison.OrdinalIgnoreCase) == true)
            .Where(w => _windowService.EvaluateTargetPolicy(w.Handle) is not { Allowed: false })
            .ToList();

        // Each window that shows what the launch opens, with the names its title shows without their paths
        var matches = new List<(WindowInfo Window, List<DocumentHint> NameOnly)>();
        foreach (var w in appWindows)
        {
            if (titleContains != null)
            {
                if (w.Title.Contains(titleContains, StringComparison.OrdinalIgnoreCase))
                    matches.Add((w, []));
                continue;
            }

            var shows = titleNames.Select(name => (Name: name, Shows: LaunchTitles.Shows(w.Title, name))).ToList();
            if (shows.All(s => s.Shows is TitleShows.FullPath or TitleShows.NameOnly))
                matches.Add((w, shows.Where(s => s.Shows == TitleShows.NameOnly).Select(s => s.Name).ToList()));
        }

        if (matches.Count == 0)
        {
            var wanted = titleContains != null ? $"'{titleContains}'" : string.Join(" and ", titleNames.Select(n => $"'{n.Name}'"));
            return (null, appWindows.Count == 0 ? null :
                $"reuseExisting: {appWindows.Count} open window(s) of the app were found, but no title shows {wanted}, so none was " +
                "reused and it was started.");
        }

        // A window that shows the launched paths is that file or folder; one that shows only the names may be another
        var best = matches.Any(m => m.NameOnly.Count == 0) ? matches.Where(m => m.NameOnly.Count == 0).ToList() : matches;
        var (window, nameOnly) = best[0];
        var check = nameOnly.Count > 0
            ? $"reuseExisting: only the name was matched: the title '{window.Title}' shows " +
              $"{string.Join(" and ", nameOnly.Select(n => $"'{n.Name}'"))} but not the path, so the window may show another file " +
              $"or folder of the same name. Check that it shows {string.Join(" and ", nameOnly.Select(n => n.FullPath ?? n.Name))} " +
              "before acting on it."
            : webAddress != null
                ? $"reuseExisting: the window '{window.Title}' was matched by expectWindow.titleContains against a title the page " +
                  $"controls: check that it shows {webAddress} before acting on it."
                : null;
        return (new LaunchResult(
            Success: true,
            WindowHandle: window.Handle,
            Warning: CombineWarnings(CombineWarnings(plan.Warning, check), best.Count == 1 ? null :
                $"reuseExisting: {best.Count} open windows matched; the front-most one was used. Nothing was started."),
            Status: LaunchStatus.AlreadyOpen,
            Window: window), null);
    }

    /// <summary>
    /// The files and folders an open window's title must show for reuseExisting to count it as showing what the launch
    /// opens: those (as <see cref="DocumentHints"/> takes them) of the arguments that are not options and of a document
    /// launched directly, with their full paths (a relative path taken in <paramref name="workingDirectory"/>). Empty when
    /// the launch opens nothing: no such argument, and a target that only starts an app (shell:AppsFolder\&lt;AUMID&gt;, or
    /// a scheme alone such as ms-settings:). Null when it opens something no title can be trusted to show: a URI
    /// (ms-settings:display, mailto:...), a web address (a page sets its own title), or arguments none of which names a
    /// file or folder.
    /// </summary>
    public static IReadOnlyList<DocumentHint>? ReuseTitleNames(
        ResolvedLaunchTarget target, IEnumerable<string>? arguments, string? workingDirectory = null)
    {
        ArgumentNullException.ThrowIfNull(target);

        var values = (arguments ?? []).Where(a => !string.IsNullOrWhiteSpace(a) && !a.StartsWith('-')).ToList();
        if (target.Kind == LaunchTargetKind.Other && !StartsAppOnly(target.FileName))
        {
            if (IsUri(target.FileName, out var scheme) && !IsWebScheme(scheme))
                return null;

            values.Add(target.FileName);
        }

        if (values.Count == 0)
            return [];

        if (values.Any(IsWebAddress))
            return null;

        var hints = FileHints(values, workingDirectory);
        return hints.Count == 0 ? null : hints;
    }

    /// <summary>The first web address (http or https) the launch opens, as an argument or launched directly; null when none.</summary>
    private static string? WebAddressOf(ResolvedLaunchTarget target, IEnumerable<string>? arguments) =>
        (arguments ?? []).Append(target.Kind == LaunchTargetKind.Other ? target.FileName : string.Empty).FirstOrDefault(IsWebAddress);

    private static bool IsWebAddress(string value) =>
        Uri.TryCreate(value, UriKind.Absolute, out var uri) && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);

    /// <summary>A target that starts an app and opens nothing in it: shell:AppsFolder\&lt;AUMID&gt;, or a scheme alone (ms-settings:).</summary>
    private static bool StartsAppOnly(string fileName) =>
        fileName.StartsWith(@"shell:AppsFolder\", StringComparison.OrdinalIgnoreCase) ||
        (IsUri(fileName, out var scheme) && !IsWebScheme(scheme) && fileName.AsSpan(scheme.Length + 1).TrimStart('/').IsWhiteSpace());

    /// <summary>A URI: a scheme of two or more characters (not a drive letter) before a colon, other than file:.</summary>
    private static bool IsUri(string value, out string scheme)
    {
        var colon = value.IndexOf(':');
        scheme = colon > 1 ? value[..colon] : string.Empty;
        return colon > 1 && !scheme.Equals("file", StringComparison.OrdinalIgnoreCase) &&
               scheme.All(c => char.IsAsciiLetterOrDigit(c) || c is '+' or '-' or '.');
    }

    private static bool IsWebScheme(string scheme) =>
        scheme.Equals(Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) || scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase);

    private static string StripExe(string name) =>
        name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? name[..^4] : name;

    /// <summary>
    /// A launch whose start was requested: the started process (null when Windows started none), the windows before the
    /// start (with a window wait) and when it was requested; or, when it ended there, its <see cref="Outcome"/>.
    /// </summary>
    private sealed record StartedLaunch(
        LaunchPlan Plan, IProcessHandle? Process, WindowBaseline? Baseline, DateTime LaunchedAtUtc, LaunchResult? Outcome = null);

    /// <summary>
    /// Starts a planned launch. The start runs on a thread of its own and is waited for at most <see cref="StartTimeout"/>:
    /// ShellExecute blocks while Windows asks the user to approve an elevated program, so a longer start is reported as
    /// <see cref="LaunchStatus.AwaitingUserConsent"/> (the program starts later if the user approves; its handle is then
    /// released). A declined prompt is <see cref="LaunchErrorCode.ElevationCancelled"/>, any other failure
    /// <see cref="LaunchErrorCode.LaunchException"/>. A call cancelled while the start still runs hands the start to
    /// <paramref name="onAbandonedStart"/> when there is one (which then owns the process handle it brings), and throws.
    /// A packaged app (roadmap M6.5) is activated instead (<see cref="Activate"/>), for at most <see cref="ActivationTimeout"/>,
    /// and an activation that takes longer is <see cref="LaunchStatus.TimedOut"/>; from an elevated server, which Windows does
    /// not let activate an app, it is started through the shell, without a process ID.
    /// </summary>
    private async Task<StartedLaunch> StartAsync(
        LaunchPlan plan, bool takeBaseline, CancellationToken cancellationToken, Action<Task<IProcessHandle?>>? onAbandonedStart = null)
    {
        var activate = plan.Target is { Kind: LaunchTargetKind.PackagedApp, App: not null } && _appsWrapper != null;
        string? startNote = null;
        if (activate && ServerIsElevated())
        {
            activate = false;
            startNote = "The server runs elevated, and Windows does not activate a packaged app from an elevated process, so the " +
                        "app was started through the shell: its processId is not known (the app runs at the user's normal level, " +
                        "and its window is found by its package).";
        }

        var baseline = takeBaseline ? _windowWait.TakeBaseline() : null;
        var launchedAt = DateTime.UtcNow;
        // On a thread of its own, not the thread pool: a start that waits for the user's answer to a UAC prompt can take
        // minutes, and a busy thread pool must not delay a start into looking like one
        Func<IProcessHandle?> startCall = activate
            ? () => Activate(plan, note => startNote = note)
            : () => _processWrapper.StartProcess(plan.StartInfo!);
        var start = Task.Factory.StartNew(startCall, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);

        var timeout = activate ? ActivationTimeout : StartTimeout;
        Task finished;
        using (var stopTimer = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
        {
            finished = await Task.WhenAny(start, Task.Delay(timeout, stopTimer.Token)).ConfigureAwait(false);
            stopTimer.Cancel();
        }

        if (finished != start)
        {
            if (cancellationToken.IsCancellationRequested && onAbandonedStart != null)
            {
                onAbandonedStart(start);
                cancellationToken.ThrowIfCancellationRequested();
            }

            ReleaseWhenStarted(start);
            cancellationToken.ThrowIfCancellationRequested();
            if (activate)
            {
                return new StartedLaunch(plan, null, null, launchedAt, new LaunchResult(
                    Success: true,
                    Warning: CombineWarnings(plan.Warning,
                        $"The activation of the app did not return within {timeout.TotalSeconds:0.#} seconds: it may still be " +
                        "starting (a cold start with a splash screen) or be stuck, and its process ID is not known. Look for its " +
                        "window with find_windows; do not launch it again."),
                    Status: LaunchStatus.TimedOut));
            }

            return new StartedLaunch(plan, null, null, launchedAt, new LaunchResult(
                Success: true,
                Warning: CombineWarnings(plan.Warning,
                    $"The start did not return within {StartTimeout.TotalSeconds:0.#} seconds: Windows is most likely showing a consent " +
                    "or security prompt (UAC, SmartScreen, a security warning for a downloaded or network file, or 'How do you want to " +
                    "open this file?'). Only the user may answer it: do not click or type into it. The program starts only if the user " +
                    "approves, its process ID is not known, and a later decline is not reported. Ask the user to answer the prompt, then " +
                    "look for its window with find_windows; do not launch it again."),
                Status: LaunchStatus.AwaitingUserConsent));
        }

        try
        {
            var process = await start.ConfigureAwait(false);
            return new StartedLaunch(plan with { Warning = CombineWarnings(plan.Warning, startNote) }, process, baseline, launchedAt);
        }
        catch (ActivationFailedException ex)
        {
            return new StartedLaunch(plan, null, null, launchedAt, new LaunchResult(false,
                ErrorMessage: ActivationFailureMessage(plan.Target!.App!, ex.Result),
                ErrorCode: nameof(LaunchErrorCode.ActivationFailed), Warning: plan.Warning));
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == ErrorCancelled)
        {
            return new StartedLaunch(plan, null, null, launchedAt, new LaunchResult(false,
                ErrorMessage: "The user declined or cancelled a prompt Windows showed for the start (most likely a UAC prompt for a " +
                              "program that needs administrator rights). Nothing was started. Ask the user before trying again.",
                ErrorCode: nameof(LaunchErrorCode.ElevationCancelled), Warning: plan.Warning));
        }
        catch (Exception ex)
        {
            return new StartedLaunch(plan, null, null, launchedAt, new LaunchResult(false,
                ErrorMessage: ex.Message, ErrorCode: nameof(LaunchErrorCode.LaunchException), Warning: plan.Warning));
        }
    }

    /// <summary>
    /// Activates a packaged app (roadmap M6.5) on the start's own thread and opens the process the activation reports: null
    /// when it exited already (it handed the request to an instance that was running, whose window the package then tells).
    /// When Windows refuses the activation for this server's elevation, the app is started through the shell instead,
    /// without a process ID, and <paramref name="note"/> says so; any other failure throws <see cref="ActivationFailedException"/>.
    /// </summary>
    private IProcessHandle? Activate(LaunchPlan plan, Action<string> note)
    {
        var app = plan.Target!.App!;
        var arguments = string.IsNullOrEmpty(plan.StartInfo!.Arguments) ? null : plan.StartInfo.Arguments;
        var activation = _appsWrapper!.ActivateApplication(app.AppUserModelId, arguments);
        if (activation.Succeeded)
        {
            if (activation.ProcessId is not { } processId)
                return null;

            try
            {
                return _processWrapper.OpenProcess(processId);
            }
            catch (Exception ex) when (ex is InvalidOperationException or Win32Exception or ArgumentException or UnauthorizedAccessException)
            {
                return null;
            }
        }

        if (!ActivationRefusedForElevation.Contains(activation.HResult))
            throw new ActivationFailedException(activation.HResult);

        note($"Windows does not activate a packaged app from this server (HRESULT 0x{activation.HResult:X8}: the server runs " +
             "elevated, or User Account Control is off), so the app was started through the shell: its processId is not known, " +
             "and its window is found by its package.");
        return _processWrapper.StartProcess(plan.StartInfo);
    }

    /// <summary>Why a packaged app's activation failed, with its HRESULT and what to do.</summary>
    private static string ActivationFailureMessage(PackagedAppId app, int hresult)
    {
        var reason = hresult == ApplicationNotRegistered
            ? "no app with this AppUserModelID is installed for the user: check the appId with list_installed_apps"
            : "the app may not be installed for this user (check the appId with list_installed_apps), its package may be " +
              "being updated, or it failed to start";
        return $"Windows could not activate the app '{app.AppUserModelId}' (HRESULT 0x{hresult:X8}): {reason}. Nothing was started.";
    }

    /// <summary>A packaged app's activation failed with the HRESULT in <see cref="Result"/> (roadmap M6.5).</summary>
    private sealed class ActivationFailedException(int result) : Exception($"The activation failed with HRESULT 0x{result:X8}.")
    {
        public int Result { get; } = result;
    }

    /// <summary>A start that was not waited for: its process handle is released when it comes, and a failure is observed.</summary>
    private static void ReleaseWhenStarted(Task<IProcessHandle?> start) =>
        start.ContinueWith(
            static t =>
            {
                if (t.Status == TaskStatus.RanToCompletion)
                    t.Result?.Dispose();
                else
                    _ = t.Exception;
            },
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);

    private static LaunchResult StartedWithoutWait(StartedLaunch launch) => launch.Process == null
        ? new LaunchResult(
            Success: true,
            Warning: CombineWarnings(launch.Plan.Warning,
                "Windows started no new process: the request went to an app that was already running, or a packaged app was " +
                "activated, so processId is null. Find its window with find_windows, or launch with waitForWindow=true."),
            Status: LaunchStatus.PossibleHandoff)
        : new LaunchResult(Success: true, ProcessId: launch.Process.Id, Warning: launch.Plan.Warning, Status: LaunchStatus.Started);

    /// <summary>
    /// Waits for the window of a started launch (<see cref="IWindowWaitService"/>) and turns the outcome into the launch
    /// result. When no window was found, a visible main window of the started process (which may be untitled) still counts.
    /// </summary>
    /// <param name="isOwner">Whether a window belongs to the launch: <paramref name="owner"/>'s match, narrowed in a batch.</param>
    private async Task<LaunchResult> WaitForWindowAsync(
        StartedLaunch launch,
        LaunchRequest request,
        LaunchWindowOwner owner,
        Func<WindowCandidate, bool> isOwner,
        int waitTimeoutMs,
        Func<nint, bool>? tryClaim,
        CancellationToken cancellationToken)
    {
        var target = launch.Plan.Target!;
        var options = new WindowWaitOptions
        {
            TimeoutMs = waitTimeoutMs,
            DocumentHints = DocumentHints(target, request.Arguments, launch.Plan.StartInfo!.WorkingDirectory),
            TitleContains = string.IsNullOrWhiteSpace(request.ExpectWindow?.TitleContains) ? null : request.ExpectWindow.TitleContains.Trim(),
            TryClaim = tryClaim,
            // A packaged app's frame may be in the baseline, cloaked: Windows shows a suspended app's frame again (roadmap M6.5)
            WasCloakedOwner = target.Kind == LaunchTargetKind.PackagedApp && launch.Process != null
                ? candidate => owner.StartedProcess(candidate.ProcessId)
                : null
        };

        var wait = await _windowWait.WaitForNewWindowAsync(launch.Baseline!, isOwner, options, cancellationToken).ConfigureAwait(false);
        var window = wait.Window;
        var status = wait.Outcome switch
        {
            WindowWaitOutcome.NewWindow => LaunchStatus.WindowObserved,
            WindowWaitOutcome.ReusedExistingWindow => LaunchStatus.ReusedExistingWindow,
            WindowWaitOutcome.OpenedInTab => LaunchStatus.OpenedInTab,
            _ => LaunchStatus.TimedOut
        };

        if (status == LaunchStatus.TimedOut && launch.Process != null && MainWindowOf(launch.Process) is { } mainWindow)
        {
            window = mainWindow;
            status = LaunchStatus.WindowObserved;
        }

        bool? launcherExited = launch.Process == null ? null : HasExitedSafe(launch.Process);
        if (status == LaunchStatus.TimedOut)
        {
            status = launch.Process == null ? LaunchStatus.PossibleHandoff
                : launcherExited == true ? LaunchStatus.ExitedWithoutWindow
                : LaunchStatus.TimedOut;
        }

        return new LaunchResult(
            Success: true,
            ProcessId: launch.Process?.Id,
            WindowHandle: window?.Handle,
            Warning: CombineWarnings(launch.Plan.Warning, NoWindowNote(status, waitTimeoutMs)),
            Status: status,
            Window: window,
            LauncherExited: launcherExited);
    }

    private LaunchWindowOwner OwnerOf(StartedLaunch launch, LaunchRequest request) =>
        LaunchWindowOwner.For(_processWrapper, launch.Process?.Id, launch.LaunchedAtUtc, launch.Plan.Target!, request.ExpectWindow,
            request.OnlyStartedProcess, _appsWrapper == null ? null : _appsWrapper.GetPackageFamilyName);

    /// <summary>
    /// A batch launch's owner check: <paramref name="owner"/>'s match, except for a window whose process another launch of
    /// the batch started (or a child of it) and this one did not. Two launches of the same program both match each
    /// other's windows by name and path; the started process decides between them. Such a window is this launch's after
    /// all once that other launch has its window and this launch's launcher really handed off (<paramref name="handoff"/>):
    /// a single-instance app whose launcher handed the request to the instance an earlier item started (two Code or
    /// Terminal launches) opens its window in that instance. The shared claim still gives each window to one launch.
    /// </summary>
    private static Func<WindowCandidate, bool> BatchOwner(
        LaunchWindowOwner owner, IReadOnlyList<(LaunchWindowOwner Owner, Func<bool> HasWindow)> others, LauncherHandoff handoff) =>
        window =>
        {
            if (!owner.Matches(window))
                return false;

            if (owner.StartedProcess(window.ProcessId))
                return true;

            // Read for every candidate, so the handoff sees which windows were there while the launcher still ran
            var handedOff = handoff.HandedOff(window.Handle);
            return !others.Any(other => other.Owner.StartedProcess(window.ProcessId) && !(other.HasWindow() && handedOff));
        };

    /// <summary>
    /// Whether a batch launch's launcher handed its request to a running instance, so that a window of that instance can
    /// be the launch's (M2 follow-up F3). A launch that started no process has no launcher to judge (Windows handed it
    /// over). Otherwise the launcher must have exited: with exit code 0, which a single-instance launcher returns once it
    /// handed off; a non-zero code means it failed, and nothing it did opens a window. When the code cannot be read, only
    /// a window that was not there while the launcher still ran counts.
    /// </summary>
    private sealed class LauncherHandoff(IProcessHandle? launcher)
    {
        private readonly HashSet<nint> _seenWhileRunning = [];

        /// <summary>True when the launcher handed off and <paramref name="window"/> can be what it handed off.</summary>
        public bool HandedOff(nint window)
        {
            if (launcher == null)
                return true;

            lock (_seenWhileRunning)
            {
                if (!HasExitedSafe(launcher))
                {
                    _seenWhileRunning.Add(window);
                    return false;
                }

                return launcher.ExitCode switch
                {
                    0 => true,
                    null => !_seenWhileRunning.Contains(window),
                    _ => false
                };
            }
        }
    }

    /// <summary>The visible main window of the started process, or null (it has none, or it exited).</summary>
    private WindowInfo? MainWindowOf(IProcessHandle process)
    {
        try
        {
            var handle = process.RefreshMainWindowHandle();
            return handle != nint.Zero && _windowService.GetWindowInfo(handle) is { IsVisible: true } window ? window : null;
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception or NotSupportedException)
        {
            return null;
        }
    }

    /// <summary>What to do when the wait found no window; null when it found one.</summary>
    private static string? NoWindowNote(LaunchStatus status, int waitTimeoutMs) => status switch
    {
        LaunchStatus.PossibleHandoff =>
            "Windows started no new process (the request went to an app that was already running, or a packaged app was activated), " +
            $"and no window of it was recognised within {waitTimeoutMs} ms. Look for it with find_windows, or launch with expectWindow " +
            "(processName or titleContains) so the wait can recognise it. Do not launch it again.",
        LaunchStatus.ExitedWithoutWindow =>
            "The started process exited without a window of its own, and no window of an app it may have handed off to was " +
            $"recognised within {waitTimeoutMs} ms. Look for it with find_windows, or launch with expectWindow (processName or " +
            "titleContains). Do not launch it again.",
        LaunchStatus.TimedOut =>
            $"The program is running, but no window of it appeared within {waitTimeoutMs} ms: it may still be starting, or run " +
            "without a window. Check with find_windows; do not launch it again.",
        _ => null
    };

    /// <summary>
    /// The files and folders that show a launched document in an existing window's title: those among the arguments (and
    /// a document launched directly), with their full paths (a relative path taken in <paramref name="workingDirectory"/>,
    /// where the app takes it). Options (-x, --x) are skipped, and so are web addresses: a web page sets its own title, so
    /// a host name in it is no sign of the launch.
    /// </summary>
    public static IReadOnlyList<DocumentHint> DocumentHints(
        ResolvedLaunchTarget target, IEnumerable<string>? arguments, string? workingDirectory = null)
    {
        var values = (arguments ?? []).Where(a => !string.IsNullOrWhiteSpace(a) && !a.StartsWith('-')).ToList();
        if (target.Kind == LaunchTargetKind.Other)
            values.Add(target.FileName);

        return FileHints(values, workingDirectory);
    }

    /// <summary>
    /// The file or folder of each path among <paramref name="values"/> that is not a web address: its name, of 3
    /// characters or more and listed once, and its full path when it can be told.
    /// </summary>
    private static List<DocumentHint> FileHints(IEnumerable<string> values, string? workingDirectory)
    {
        var hints = new List<DocumentHint>();
        foreach (var value in values)
        {
            if (IsWebAddress(value) || (value.IndexOfAny(['\\', '/']) < 0 && !Path.HasExtension(value)))
                continue;

            string name;
            try
            {
                name = Path.GetFileName(value.TrimEnd('\\', '/'));
            }
            catch (ArgumentException)
            {
                continue;
            }

            if (name.Length >= 3 && !hints.Any(h => h.Name.Equals(name, StringComparison.OrdinalIgnoreCase)))
                hints.Add(new DocumentHint(name, FullPathOf(value, workingDirectory)));
        }

        return hints;
    }

    /// <summary>
    /// The full path of a file or folder argument, without a trailing separator: a file: URI's local path, a fully
    /// qualified path, or a relative one in <paramref name="workingDirectory"/>; null when it cannot be told. Only the text
    /// is read; the file system is not.
    /// </summary>
    private static string? FullPathOf(string value, string? workingDirectory)
    {
        try
        {
            var path = value.StartsWith("file:", StringComparison.OrdinalIgnoreCase) && Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.IsFile
                ? uri.LocalPath
                : value;
            var full = Path.IsPathFullyQualified(path) ? Path.GetFullPath(path)
                : !string.IsNullOrEmpty(workingDirectory) && Path.IsPathFullyQualified(workingDirectory) ? Path.GetFullPath(path, workingDirectory)
                : null;
            return full == null ? null : Path.TrimEndingDirectorySeparator(full);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }

    /// <summary>
    /// Validates and resolves a launch request and builds the start info, without starting anything.
    /// </summary>
    /// <remarks>
    /// <list type="bullet">
    /// <item>Bare names are resolved to an absolute path by <see cref="IExecutableResolver"/>, never from
    /// the working directory, and the resolved path is what gets started.</item>
    /// <item>Arguments are quoted with the Windows command-line rules (<see cref="CommandLineBuilder"/>).</item>
    /// <item>The server does not run commands or scripts for the agent (roadmap M3.F8): a command interpreter or script
    /// host with arguments, and a script file (a batch file included), are refused by <see cref="InterpreterGuard"/>.</item>
    /// <item>When the target cannot be verified to be a native executable (a URI, document or unresolved name, which
    /// may be handed to cmd.exe), arguments containing cmd.exe metacharacters are rejected, because cmd.exe
    /// ignores \" escaping and expands %VAR% even inside quotes.</item>
    /// <item><paramref name="forceAccessibility"/> (roadmap M3.3) puts --force-renderer-accessibility in front of the
    /// arguments, only for a program <see cref="ChromiumPrograms"/> recognizes (a Chromium browser, an Electron or a CEF
    /// app, started by its .exe) and only once; for any other target nothing is added and the plan's warning says why. In
    /// front, because Chromium reads no switch after a "--" argument.</item>
    /// <item>Roadmap M6.5: a packaged app's AppUserModelID, bare or as shell:AppsFolder\&lt;AUMID&gt;, is planned as an
    /// activation (<see cref="LaunchTargetKind.PackagedApp"/>). Arguments go to an app or a shell:AppsFolder entry only when
    /// its program is known and is no interpreter (<see cref="InterpreterGuard.CheckApp"/>). A bare name that is no program
    /// on disk is looked up among the installed apps by its exact display name (<see cref="IInstalledAppsService.FindByName"/>):
    /// one packaged app is planned as it, one desktop app as its program (through every check here), several are
    /// AmbiguousApp.</item>
    /// <item><paramref name="runAsAdmin"/> (roadmap M6.5) starts a program's .exe with the runas verb; any other target is
    /// refused, and so is a computer where Windows would elevate without asking, unless
    /// <paramref name="elevationConfirmed"/> (the user said yes through the client).</item>
    /// </list>
    /// </remarks>
    public LaunchPlan PlanLaunch(
        string executable, string[]? arguments = null, string? workingDirectory = null, bool forceAccessibility = false,
        bool runAsAdmin = false, bool elevationConfirmed = false)
    {
        // Validate executable path
        var executableValidation = _validationService.ValidateExecutable(executable);
        if (!executableValidation.IsValid)
        {
            return LaunchPlan.Rejected(executableValidation.Error, executableValidation.ErrorCode);
        }

        // Validate working directory
        var workingDirValidation = _validationService.ValidateWorkingDirectory(workingDirectory);
        if (!workingDirValidation.IsValid)
        {
            return LaunchPlan.Rejected(workingDirValidation.Error, workingDirValidation.ErrorCode);
        }

        // Combine warnings
        var warning = CombineWarnings(executableValidation.Warning, workingDirValidation.Warning);

        if (arguments != null && arguments.Any(a => a is null || a.Contains('\0')))
        {
            return LaunchPlan.Rejected(
                "Arguments must not be null or contain NUL characters.",
                nameof(LaunchErrorCode.InvalidArgumentValue),
                warning);
        }

        // Auto-inject --new-window for browsers opening URLs
        if (_browserDetection.IsBrowser(executable) && _browserDetection.ContainsUrl(arguments))
        {
            var newWindowFlag = _browserDetection.GetNewWindowFlag(executable);
            if (newWindowFlag != null && (arguments?.All(a => a != newWindowFlag) ?? true))
            {
                arguments = new[] { newWindowFlag }.Concat(arguments ?? []).ToArray();
            }
        }

        // The resolver checks the server's current directory itself; the working directory is checked when one was given
        var requestedWorkingDirectory = workingDirValidation.SanitizedPath ?? (string.IsNullOrWhiteSpace(workingDirectory) ? null : workingDirectory);

        // A packaged app is activated by its AppUserModelID; the resolver has nothing to find (roadmap M6.5)
        if (PackagedAppId.TryParse(executableValidation.SanitizedExecutable ?? executable, out var packagedApp))
        {
            return PlanPackagedApp(packagedApp, executable, arguments, requestedWorkingDirectory, forceAccessibility, runAsAdmin, warning);
        }

        var target = _executableResolver.Resolve(executableValidation.SanitizedExecutable ?? executable, requestedWorkingDirectory);

        if (target.FoundOnlyInWorkingDirectory != null)
        {
            return LaunchPlan.Rejected(
                $"'{executable}' was not found in the Windows directory, System32, PATH or App Paths, but " +
                $"'{target.FoundOnlyInWorkingDirectory}' exists in the current or working directory. Bare names are " +
                "never resolved from the working directory, to avoid running a planted program. Pass the full path " +
                "if you intend to run that file.",
                nameof(LaunchErrorCode.ExecutableInWorkingDirectory),
                warning);
        }

        if (target.NotFound)
        {
            if (PlanInstalledApp(executable, arguments, workingDirectory, requestedWorkingDirectory, forceAccessibility, runAsAdmin,
                    elevationConfirmed, warning) is { } byName)
            {
                return byName;
            }

            return LaunchPlan.Rejected(
                $"'{executable}' was not found in the Windows directory, System32, the PATH entries or App Paths, and is no " +
                "installed app's exact name. A bare name is never passed to Windows unresolved, because Windows would also search " +
                "the server's current directory. Pass the program's full path, or an app's appId or path from list_installed_apps " +
                "(names are localized), or a URI such as ms-settings:.",
                nameof(LaunchErrorCode.ExecutableNotFound),
                warning);
        }

        if (_interpreterGuard.Check(executable, target.FileName, arguments) is { } refusal)
        {
            return LaunchPlan.Rejected(refusal.Message, refusal.ErrorCode, warning);
        }

        // A desktop app's shell:AppsFolder entry: the shell starts its program, which the guard above cannot see (roadmap M6.5)
        if (arguments?.Length > 0 && target.Kind == LaunchTargetKind.Other &&
            target.FileName.StartsWith(PackagedAppId.AppsFolderPrefix, StringComparison.OrdinalIgnoreCase))
        {
            var appId = target.FileName[PackagedAppId.AppsFolderPrefix.Length..];
            if (InterpreterGuard.CheckApp(appId, _installedApps?.FindByAppId(appId)?.Path, null, arguments) is { } appRefusal)
            {
                return LaunchPlan.Rejected(appRefusal.Message, appRefusal.ErrorCode, warning);
            }
        }

        var isNativeExecutable = target.Kind == LaunchTargetKind.NativeExecutable;
        if (!isNativeExecutable && arguments?.Length > 0)
        {
            var unsafeArgument = arguments.FirstOrDefault(ContainsCmdMetacharacters);
            if (unsafeArgument != null)
            {
                return LaunchPlan.Rejected(
                    BuildUnsafeBatchArgumentMessage(executable, unsafeArgument),
                    nameof(LaunchErrorCode.UnsafeBatchArguments),
                    warning);
            }
        }

        if (runAsAdmin)
        {
            if (!isNativeExecutable)
            {
                return LaunchPlan.Rejected(RunAsAdminOnlyExe(executable), ToolErrorReporting.InvalidArgumentsErrorCode, warning);
            }

            if (!elevationConfirmed && ElevationNeedsConfirmation())
            {
                return LaunchPlan.Rejected(ElevationNotConfirmedMessage(), nameof(LaunchErrorCode.ElevationNotConfirmed), warning);
            }
        }

        bool? forceAccessibilityApplied = null;
        if (forceAccessibility)
        {
            var whyNot = isNativeExecutable
                ? _chromiumPrograms.WhyNot(target.FileName)
                : "it is not a program's .exe (a URI, document, shortcut or Store app)";
            forceAccessibilityApplied = whyNot == null;
            if (whyNot != null)
            {
                warning = CombineWarnings(warning, ForceAccessibilityNotApplied(executable, whyNot));
            }
            else if (arguments?.Any(ChromiumPrograms.IsForceAccessibilityArgument) != true)
            {
                arguments = [ChromiumPrograms.ForceRendererAccessibility, .. arguments ?? []];
            }
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = target.FileName,
            UseShellExecute = true,
            WorkingDirectory = requestedWorkingDirectory ?? DefaultWorkingDirectory(target)
        };

        if (arguments?.Length > 0)
        {
            startInfo.Arguments = CommandLineBuilder.JoinArguments(arguments, quoteCmdDelimiters: !isNativeExecutable);
        }

        if (runAsAdmin)
        {
            startInfo.Verb = RunAsVerb;
        }

        return new LaunchPlan(startInfo, target.Kind, warning, Target: target, ForceAccessibilityApplied: forceAccessibilityApplied);
    }

    private static string ForceAccessibilityNotApplied(string executable, string whyNot) =>
        $"forceAccessibility was not applied to '{executable}': {whyNot}. {ChromiumPrograms.ForceRendererAccessibility} " +
        "is added only to a Chromium browser, an Electron or a CEF app started by its .exe (for VS Code, Code.exe, " +
        "not code.cmd).";

    private static string RunAsAdminOnlyExe(string executable) =>
        $"runAsAdmin starts only a program's .exe as administrator, and '{executable}' is not one (a URI, document, shortcut " +
        "or packaged app). Nothing was started.";

    private string ElevationNotConfirmedMessage() =>
        (_securityProbe?.GetElevationPrompt() == ElevationPrompt.Silent
            ? "Windows on this computer elevates programs without asking (User Account Control is off, or administrators are not asked for consent)"
            : "The User Account Control policy could not be read, so the server cannot tell whether Windows would ask the user") +
        ", so runAsAdmin needs the user's confirmation through the client: launch_application asks for it when the client can " +
        "ask (elicitation), and launch_applications_batch never asks. Nothing was started. Ask the user to start the program " +
        "as administrator.";

    /// <summary>
    /// Plans a packaged app's activation (roadmap M6.5): its shell:AppsFolder target (what the shell fallback starts), the
    /// arguments quoted for the app alone (activation runs no cmd.exe), and refused when the app is an interpreter or its
    /// program cannot be told (<see cref="InterpreterGuard.CheckApp"/>). Neither runAsAdmin nor forceAccessibility applies.
    /// </summary>
    private LaunchPlan PlanPackagedApp(
        PackagedAppId app, string executable, string[]? arguments, string? workingDirectory, bool forceAccessibility, bool runAsAdmin,
        string? warning)
    {
        if (runAsAdmin)
        {
            return LaunchPlan.Rejected(RunAsAdminOnlyExe(executable), ToolErrorReporting.InvalidArgumentsErrorCode, warning);
        }

        var program = arguments?.Length > 0 ? _installedApps?.ProgramOf(app) : null;
        if (InterpreterGuard.CheckApp(app.AppUserModelId, program, app.PackageFamilyName, arguments) is { } refusal)
        {
            return LaunchPlan.Rejected(refusal.Message, refusal.ErrorCode, warning);
        }

        bool? forceAccessibilityApplied = null;
        if (forceAccessibility)
        {
            forceAccessibilityApplied = false;
            warning = CombineWarnings(warning, ForceAccessibilityNotApplied(executable, "it is a packaged app, not a program's .exe"));
        }

        var target = new ResolvedLaunchTarget(app.AppsFolderTarget, LaunchTargetKind.PackagedApp) { App = app };
        var startInfo = new ProcessStartInfo
        {
            FileName = app.AppsFolderTarget,
            UseShellExecute = true,
            WorkingDirectory = workingDirectory ?? DefaultWorkingDirectory(target)
        };

        if (arguments?.Length > 0)
        {
            startInfo.Arguments = CommandLineBuilder.JoinArguments(arguments, quoteCmdDelimiters: false);
        }

        return new LaunchPlan(startInfo, LaunchTargetKind.PackagedApp, warning, Target: target, ForceAccessibilityApplied: forceAccessibilityApplied);
    }

    /// <summary>The most installed apps an AmbiguousApp error lists.</summary>
    private const int MaxAmbiguousApps = 5;

    /// <summary>
    /// A bare name that is no program on disk, looked up among the installed apps by its exact display name (roadmap M6.5,
    /// owner decision D11: never a part of a name, since any program can add a Start menu shortcut). Null when no installed
    /// app has the name (or the apps cannot be read), so the launch is ExecutableNotFound. One packaged app is planned as
    /// its activation; one desktop app as its program's path, through every check of <see cref="PlanLaunch"/> (a shortcut's
    /// own arguments are never used); several are AmbiguousApp.
    /// </summary>
    private LaunchPlan? PlanInstalledApp(
        string name, string[]? arguments, string? workingDirectory, string? requestedWorkingDirectory, bool forceAccessibility,
        bool runAsAdmin, bool elevationConfirmed, string? warning)
    {
        if (_installedApps == null || name.IndexOfAny(['\\', '/', ':']) >= 0)
            return null;

        var apps = _installedApps.FindByName(name)?
            .Where(a => a.Packaged ? PackagedAppId.TryParse(a.AppId, out _) : a.Path != null && Path.IsPathFullyQualified(a.Path))
            .ToList();
        if (apps is not { Count: > 0 })
            return null;

        if (apps.Count > 1)
        {
            var listed = string.Join("; ", apps.Take(MaxAmbiguousApps).Select(a => a.Packaged
                ? $"'{a.Name}' appId {a.AppId}"
                : $"'{a.Name}' appId {a.AppId}, path {a.Path}"));
            var more = apps.Count > MaxAmbiguousApps ? $" and {apps.Count - MaxAmbiguousApps} more" : string.Empty;
            return LaunchPlan.Rejected(
                $"'{name}' is no program on disk, and {apps.Count} installed apps have that name: {listed}{more}. Launch one by its " +
                "appId (a packaged app) or path. Nothing was started.",
                nameof(LaunchErrorCode.AmbiguousApp),
                warning);
        }

        var app = apps[0];
        var note = $"'{name}' is no program on disk; it was resolved to the installed app '{app.Name}' " +
                   (app.Packaged ? $"(appId {app.AppId})." : $"({app.Path}).");
        if (app.Packaged && PackagedAppId.TryParse(app.AppId, out var packaged))
        {
            return PlanPackagedApp(packaged, name, arguments, requestedWorkingDirectory, forceAccessibility, runAsAdmin, CombineWarnings(warning, note));
        }

        // Planned again from its path, so the program gets every check a path gets; that plan validates the working directory
        // again and repeats its warning, so only the name's note is added
        var plan = PlanLaunch(app.Path!, arguments, workingDirectory, forceAccessibility, runAsAdmin, elevationConfirmed);
        return plan with { Warning = CombineWarnings(note, plan.Warning) };
    }

    /// <summary>
    /// The working directory of a launch that names none: the folder of the program or document, never the server's
    /// current directory (a repository the agent works in, from which the program could load planted files). A URI gets
    /// the user's profile folder, as the Run dialog would.
    /// </summary>
    private static string DefaultWorkingDirectory(ResolvedLaunchTarget target)
    {
        try
        {
            if (Path.IsPathFullyQualified(target.FileName) && Path.GetDirectoryName(target.FileName) is { Length: > 0 } folder)
            {
                return folder;
            }
        }
        catch (ArgumentException)
        {
            // Not a path: a URI
        }

        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return string.IsNullOrEmpty(profile) ? Environment.SystemDirectory : profile;
    }

    private static bool ContainsCmdMetacharacters(string argument)
    {
        return argument.IndexOfAny(CmdMetacharacters) >= 0 || argument.Any(char.IsControl);
    }

    private static string BuildUnsafeBatchArgumentMessage(string executable, string argument)
    {
        var shownArgument = argument.Length > MaxArgumentLengthInMessages
            ? argument[..MaxArgumentLengthInMessages] + "..."
            : argument;

        // A batch file never gets here: InterpreterGuard refuses it as a script
        var reason = $"'{executable}' could not be verified to be a native .exe (it is a URI, document, shortcut or an " +
                     "unresolved name), so it may be run through cmd.exe";

        return $"{reason}, which would interpret characters in the argument '{shownArgument}' " +
               "(& | < > ^ % ! \" or control characters) as commands or variables. Remove those characters, " +
               "or launch the program's .exe directly by its full path.";
    }

    private static string? CombineWarnings(string? warning1, string? warning2)
    {
        if (string.IsNullOrEmpty(warning1)) return warning2;
        if (string.IsNullOrEmpty(warning2)) return warning1;
        return $"{warning1}; {warning2}";
    }

    /// <summary>
    /// Each process's visible, titled top-level windows, in the order FindWindows returns them. Unlike the user-facing window
    /// listings, this also takes cloaked windows (on another virtual desktop), tool windows and the desktop shell's
    /// windows: a process that has such a window is running with a window, and hiding it from list_processes would let
    /// "has it exited?" checks pass too early. A UWP window is listed for the app's process and for its
    /// ApplicationFrameHost frame host.
    /// </summary>
    private Dictionary<int, List<WindowInfo>> MapWindowsToProcesses()
    {
        var windowsByProcess = new Dictionary<int, List<WindowInfo>>();
        void Add(int processId, WindowInfo window)
        {
            if (!windowsByProcess.TryGetValue(processId, out var windows))
            {
                windowsByProcess[processId] = windows = [];
            }

            windows.Add(window);
        }

        foreach (var window in _windowService.FindWindows(new WindowQuery { IncludeShell = true }))
        {
            if (string.IsNullOrEmpty(window.Title))
                continue;

            Add(window.ProcessId, window);
            if (window.HostProcessId is { } hostProcessId && hostProcessId != window.ProcessId)
                Add(hostProcessId, window);
        }

        return windowsByProcess;
    }

    public IReadOnlyList<ProcessInfo> GetRunningProcesses(ProcessQuery query)
    {
        ArgumentNullException.ThrowIfNull(query);
        if (query.Limit is < 1)
            throw new ArgumentOutOfRangeException(nameof(query), query.Limit, "The limit must be at least 1.");

        // An empty list would read as "no such process is running" and let a "has it exited?" check pass when nothing
        // was read, so an unreadable list is an error, as it was before the process seam
        var processes = _processWrapper.ListRunningProcesses()
            ?? throw new InvalidOperationException("The process list could not be read.");

        // Built in both modes, so that windowCount is real with includeWindowless as well (D-PAL-20)
        var windowsByProcess = MapWindowsToProcesses();

        var listed = new List<(RunningProcess Process, List<WindowInfo>? Windows)>();
        foreach (var process in processes)
        {
            if (query.ProcessId is { } processId && process.ProcessId != processId)
                continue;
            if (!string.IsNullOrEmpty(query.NameFilter) && !process.Name.Contains(query.NameFilter, StringComparison.OrdinalIgnoreCase))
                continue;

            var windows = windowsByProcess.GetValueOrDefault(process.ProcessId);
            if (!query.IncludeWindowless && windows == null)
                continue;

            listed.Add((process, windows));
        }

        if (listed.Count == 0)
            return [];

        // Each process is opened once for its path and start time: here only when the path filter or the start-time
        // order needs them, else once it is returned (or is the parent of one that is)
        var details = new Dictionary<int, ProcessDetails?>();
        ProcessDetails? DetailsOf(int processId)
        {
            if (!details.TryGetValue(processId, out var read))
            {
                details[processId] = read = _processWrapper.ReadProcessDetails(processId);
            }

            return read;
        }

        IEnumerable<(RunningProcess Process, List<WindowInfo>? Windows)> rows = listed;
        if (!string.IsNullOrEmpty(query.PathContains))
        {
            rows = rows.Where(r => DetailsOf(r.Process.ProcessId)?.ImagePath?.Contains(query.PathContains, StringComparison.OrdinalIgnoreCase) == true);
        }

        DateTime? StartOf((RunningProcess Process, List<WindowInfo>? Windows) row) => DetailsOf(row.Process.ProcessId)?.StartTime;
        var ordered = query.Sort switch
        {
            // Largest or newest first; what cannot be read last
            ProcessSort.Memory => rows.OrderBy(r => r.Process.WorkingSetBytes == null).ThenByDescending(r => r.Process.WorkingSetBytes),
            ProcessSort.StartTime => rows.OrderBy(r => StartOf(r) == null).ThenByDescending(StartOf),
            _ => rows.OrderBy(r => r.Process.Name, StringComparer.OrdinalIgnoreCase)
        };
        rows = ordered.ThenBy(r => r.Process.Name, StringComparer.OrdinalIgnoreCase).ThenBy(r => r.Process.ProcessId);
        if (query.Limit is { } limit)
        {
            rows = rows.Take(limit);
        }

        var returned = rows.ToList();

        // The parents come from a second snapshot (Toolhelp), so a process can be in one list and not the other: a
        // parent that is not in it is not running, and is not reported
        var parentByProcessId = _processWrapper.GetProcessList()?.ToDictionary(p => p.Key, p => p.Value.ParentProcessId);
        var serverIntegrity = _securityProbe?.ServerIntegrity;

        var result = new List<ProcessInfo>(returned.Count);
        foreach (var (process, windows) in returned)
        {
            var processDetails = DetailsOf(process.ProcessId);
            var parent = parentByProcessId == null
                ? null
                : NativeProcessWrapper.FindParent(process.ProcessId, parentByProcessId, id => DetailsOf(id)?.StartTime);
            var integrity = _securityProbe?.GetIntegrity(process.ProcessId);
            var commandLine = query.IncludeCommandLine ? ReadCommandLine(process, processDetails) : null;

            result.Add(new ProcessInfo(
                ProcessId: process.ProcessId,
                Name: process.Name,
                ExecutablePath: processDetails?.ImagePath,
                WindowCount: windows?.Count ?? 0,
                MemoryUsageMB: process.WorkingSetBytes / (1024.0 * 1024.0),
                ParentProcessId: parent,
                StartTime: processDetails?.StartTime,
                Windows: windows?.Take(ProcessInfo.MaxWindows).ToList() ?? [],
                Integrity: integrity,
                AboveServer: integrity != null && serverIntegrity != null && integrity.IsMorePrivilegedThan(serverIntegrity),
                CommandLine: commandLine?.Text,
                CommandLineRedacted: commandLine?.Redacted == true));
        }

        return result;
    }

    /// <summary>
    /// A listed process's command line, redacted and cut to <see cref="ProcessInfo.MaxCommandLineLength"/>; null when the
    /// user's target policy denies the process (it is then never read) and when it cannot be read. The policy decides by the
    /// listed name, so the command line is read only when the process <paramref name="details"/> describes still bears it
    /// (its image file is that name) and is still the one at the PID once the command line is read (same start time): a
    /// PID reused at any point after the listing gives no command line, rather than one of a process the policy check did
    /// not cover. Without a start time or a path the PID cannot be pinned, so none is read either.
    /// </summary>
    private RedactedCommandLine? ReadCommandLine(RunningProcess process, ProcessDetails? details)
    {
        if (details is not { StartTime: { } startTime, ImagePath: { } imagePath })
            return null;

        var imageName = Path.GetFileNameWithoutExtension(imagePath);
        if (!string.Equals(imageName, process.Name, StringComparison.OrdinalIgnoreCase) ||
            _targetPolicy?.Evaluate([process.Name, imageName]) is { Allowed: false })
            return null;

        var commandLine = _processWrapper.GetProcessCommandLine(process.ProcessId);
        if (commandLine == null || _processWrapper.ReadProcessDetails(process.ProcessId)?.StartTime != startTime)
            return null;

        var redacted = CommandLineRedactor.Redact(commandLine);
        return redacted with { Text = AppText.Cut(redacted.Text, ProcessInfo.MaxCommandLineLength) };
    }

    public string? GetProcessName(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            return process.ProcessName;
        }
        catch
        {
            return null;
        }
    }

    public string? GetProcessPath(int processId) => _processWrapper.GetProcessImagePath(processId);

    // Protected system and shell processes that should not be terminated
    private static readonly HashSet<string> ProtectedProcesses = new(StringComparer.OrdinalIgnoreCase)
    {
        "System", "smss", "csrss", "wininit", "services", "lsass", "lsm",
        "svchost", "winlogon", "dwm", "explorer", "Registry", "Memory Compression",
        // User-session shell infrastructure: killing these breaks keyboard input, the Start menu or search
        "sihost", "ctfmon", "StartMenuExperienceHost", "ShellExperienceHost", "SearchHost", "TextInputHost", "fontdrvhost"
    };

    public const int DefaultGracefulTimeoutMs = 5000;
    public const int MaxGracefulTimeoutMs = 60000;
    private const int ForceKillExitWaitMs = 5000;
    private const int ErrorAccessDenied = 5;
    private const string ForceKillConsequence =
        "this terminates the process and all of its child processes immediately, and unsaved work is lost.";

    /// <summary>What counts as the agent's host process, for the CannotTerminateHost messages.</summary>
    private const string HostProcessKinds =
        "this server, an ancestor of it such as the terminal or editor hosting the agent, the process that shows " +
        "its console or the console server behind it (conhost or OpenConsole), or the app that was in front when the server started";

    public async Task<IReadOnlyList<LaunchResult>> LaunchApplicationsBatchAsync(
        IReadOnlyList<LaunchRequest> applications,
        bool waitForWindows = false,
        int waitTimeoutMs = 5000,
        IProgress<OperationProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(applications);

        var results = new LaunchResult?[applications.Count];
        var plans = new LaunchPlan?[applications.Count];
        var waiting = new List<(int Index, StartedLaunch Launch)>();
        var reused = new HashSet<nint>();
        // reuseExisting reuses only windows that were open before the batch started anything, not one an earlier item started
        var openBefore = applications.Any(a => a.ReuseExisting)
            ? _windowService.GetApplicationWindows().Select(w => w.Handle).ToHashSet()
            : null;
        var finished = 0;
        var reportLock = new object();

        // "i of n" when an application is done: refused or started (without a wait), or its window wait ended. The waits
        // end concurrently: counting and reporting under one lock keeps the reported values increasing
        void Report(int index)
        {
            lock (reportLock)
            {
                finished++;
                progress?.Report(new OperationProgress(finished, applications.Count,
                    $"{finished} of {applications.Count}: {applications[index].Executable} {DescribeLaunch(results[index]!)}"));
            }
        }

        try
        {
            // Start every application first, in order; a cancelled call starts no more
            for (var i = 0; i < applications.Count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var request = applications[i];
                var plan = PlanFor(request);
                plans[i] = plan;
                if (!plan.IsValid)
                {
                    results[i] = Rejected(plan);
                    Report(i);
                    continue;
                }

                // reuseExisting: an open window of the app, each used by one item only
                if (request.ReuseExisting)
                {
                    var reuse = FindOpenWindow(plan, request, reused, openBefore);
                    if (reuse.Result is { } alreadyOpen)
                    {
                        reused.Add(alreadyOpen.WindowHandle!.Value);
                        results[i] = alreadyOpen;
                        Report(i);
                        continue;
                    }

                    plan = plan with { Warning = CombineWarnings(plan.Warning, reuse.Note) };
                    plans[i] = plan;
                }

                // Read right before this item's start, so an earlier item's process counts as running
                plan = plan with { RunningInstance = RunningInstanceOf(plan) };
                plans[i] = plan;
                var launch = await StartAsync(plan, waitForWindows, cancellationToken).ConfigureAwait(false);
                if (launch.Outcome is { } ended)
                {
                    results[i] = ended;
                    Report(i);
                }
                else if (!waitForWindows)
                {
                    using (launch.Process)
                    {
                        results[i] = StartedWithoutWait(launch);
                    }

                    Report(i);
                }
                else
                {
                    waiting.Add((i, launch));
                }
            }

            // Then wait for all the windows at once, against one deadline; a new window goes to one launch only. A window
            // of a process that one launch started (or that process's child) is that launch's: the other launches' looser
            // matches (same program name or path, handoff names, hint) leave it alone, whichever wait sees it first
            var owners = waiting.Select(item => OwnerOf(item.Launch, applications[item.Index])).ToList();
            var claimed = new HashSet<nint>();
            var hasWindow = new bool[waiting.Count];
            bool Claim(int position, nint handle)
            {
                lock (claimed)
                {
                    if (!claimed.Add(handle))
                        return false;

                    hasWindow[position] = true;
                    return true;
                }
            }

            bool HasWindow(int position)
            {
                lock (claimed)
                {
                    return hasWindow[position];
                }
            }

            await Task.WhenAll(waiting.Select(async (item, position) =>
            {
                var owner = owners[position];
                var others = owners.Select((other, index) => (Owner: other, HasWindow: (Func<bool>)(() => HasWindow(index))))
                    .Where((_, index) => index != position).ToList();
                results[item.Index] = await WaitForWindowAsync(
                    item.Launch, applications[item.Index], owner, BatchOwner(owner, others, new LauncherHandoff(item.Launch.Process)), waitTimeoutMs,
                    handle => Claim(position, handle), cancellationToken).ConfigureAwait(false);
                Report(item.Index);
            })).ConfigureAwait(false);
        }
        finally
        {
            foreach (var (_, launch) in waiting)
            {
                launch.Process?.Dispose();
            }
        }

        return results.Select((result, i) => plans[i] is { } plan ? Finish(result!, plan) : result!).ToList();
    }

    private static string DescribeLaunch(LaunchResult result) => !result.Success
        ? $"was not started ({result.ErrorCode})"
        : result.Status switch
        {
            LaunchStatus.WindowObserved => "started, its window appeared",
            LaunchStatus.ReusedExistingWindow => "started, it reused a window that was already open",
            LaunchStatus.OpenedInTab => "started, it opened in a window that was already open",
            LaunchStatus.PossibleHandoff => "was handed to an app that was already running",
            LaunchStatus.ExitedWithoutWindow => "started, but it exited without a window",
            LaunchStatus.TimedOut => "started, no window appeared in time",
            LaunchStatus.AwaitingUserConsent => "is waiting for the user to answer a prompt (most likely UAC)",
            LaunchStatus.AlreadyOpen => "was already open, nothing was started",
            _ => "started"
        };

    public async Task<KillResult> KillProcessAsync(
        int processId,
        bool confirm,
        bool forceKill = false,
        int gracefulTimeoutMs = DefaultGracefulTimeoutMs,
        string? expectedProcessName = null,
        KillSafeguards? safeguards = null,
        CancellationToken cancellationToken = default)
    {
        // Safety check: require explicit confirmation
        if (!confirm)
        {
            return new KillResult(
                Success: false,
                ProcessId: processId,
                ErrorMessage: "Confirmation required. Set confirm=true to terminate the process.",
                ErrorCode: nameof(ProcessErrorCode.ConfirmationRequired));
        }

        if (ArgumentRefusal(processId, gracefulTimeoutMs) is { } invalid)
        {
            return invalid;
        }

        IProcessHandle? process;
        try
        {
            process = _processWrapper.OpenProcess(processId);
        }
        catch (Exception ex)
        {
            return KillFailureFromException(ex, processId, processName: null);
        }

        if (process == null)
        {
            return ProcessNotFound(processId);
        }

        using (process)
        {
            string processName;
            try
            {
                processName = process.ProcessName;
            }
            catch (InvalidOperationException)
            {
                // The process exited between opening it and reading its name
                return ProcessNotFound(processId);
            }

            if (TargetRefusal(processId, processName, forceKill, expectedProcessName, out var tree) is { } refused)
            {
                return refused;
            }

            if (safeguards?.Confirmed is { } confirmed && ConfirmedTargetRefusal(processId, processName, forceKill, confirmed, tree) is { } changed)
            {
                return changed;
            }

            // A call cancelled before anything is sent does nothing
            cancellationToken.ThrowIfCancellationRequested();

            var beforeWait = safeguards?.BeforeWait;
            return forceKill
                ? await TerminateForcefullyAsync(process, processId, processName, beforeWait, cancellationToken).ConfigureAwait(false)
                : await CloseGracefullyAsync(process, processId, processName, gracefulTimeoutMs, beforeWait, cancellationToken).ConfigureAwait(false);
        }
    }

    public KillTargetCheck CheckKillTarget(int processId, bool forceKill, int gracefulTimeoutMs, string? expectedProcessName = null)
    {
        if (ArgumentRefusal(processId, gracefulTimeoutMs) is { } invalid)
        {
            return KillTargetCheck.Refused(invalid);
        }

        IProcessHandle? process;
        try
        {
            process = _processWrapper.OpenProcess(processId);
        }
        catch (Exception ex)
        {
            return KillTargetCheck.Refused(KillFailureFromException(ex, processId, processName: null));
        }

        if (process == null)
        {
            return KillTargetCheck.Refused(ProcessNotFound(processId));
        }

        using (process)
        {
            string processName;
            try
            {
                processName = process.ProcessName;
            }
            catch (InvalidOperationException)
            {
                return KillTargetCheck.Refused(ProcessNotFound(processId));
            }

            if (TargetRefusal(processId, processName, forceKill, expectedProcessName, out var tree) is { } refused)
            {
                return KillTargetCheck.Refused(refused);
            }

            // A graceful close needs a main window to ask; without one the kill would refuse after the user's yes.
            // (A main window that a modal dialog disables is only found by the close request itself.)
            if (!forceKill && !HasMainWindow(process))
            {
                return KillTargetCheck.Refused(NoClosableWindow(processId, processName));
            }

            // Read while the process is open, so the PID cannot have been reused yet
            return new KillTargetCheck(null, processName, tree ?? [], _processWrapper.GetProcessStartTime(processId));
        }
    }

    /// <summary>
    /// Why the open process is not the one the user confirmed, or null: it started at another time than the confirmed one
    /// (the PID was reused while the question was open), or, with <paramref name="forceKill"/>, its tree has a process the
    /// user was not shown. The process is open, so its PID cannot be reused while this reads its start time.
    /// </summary>
    private KillResult? ConfirmedTargetRefusal(
        int processId, string processName, bool forceKill, KillTargetCheck confirmed, IReadOnlyList<(int ProcessId, string ImageName)>? tree)
    {
        if (confirmed.StartTime is { } confirmedStart && _processWrapper.GetProcessStartTime(processId) is var start && start != confirmedStart)
        {
            var started = start is { } now ? $"at {now:u}" : "at a time that cannot be read";
            return new KillResult(
                Success: false,
                ProcessId: processId,
                ProcessName: processName,
                ErrorMessage: $"PID {processId} now belongs to a '{processName}' process that started {started}, not the one the user " +
                              $"confirmed (started at {confirmedStart:u}): that one exited and its PID was reused while the question was " +
                              "open. Nothing was done; call list_processes to get the current PID.",
                ErrorCode: nameof(ProcessErrorCode.ProcessNameMismatch));
        }

        if (forceKill && tree != null)
        {
            var shown = confirmed.ChildProcesses.Select(c => c.ProcessId).ToHashSet();
            var added = tree.Where(c => !shown.Contains(c.ProcessId)).ToList();
            if (added.Count > 0)
            {
                var names = string.Join(", ", added.Take(10).Select(c => $"{c.ImageName} (PID {c.ProcessId})")) +
                            (added.Count > 10 ? $" and {added.Count - 10} more" : string.Empty);
                return new KillResult(
                    Success: false,
                    ProcessId: processId,
                    ProcessName: processName,
                    ErrorMessage: $"forceKill would also end {names}, which started under '{processName}' (PID {processId}) while the " +
                                  "user was asked, so the user never saw them. Nothing was terminated. Call kill_process again to ask " +
                                  "about the process tree as it is now.",
                    ErrorCode: nameof(ProcessErrorCode.ProcessTreeChanged));
            }
        }

        return null;
    }

    /// <summary>True when the process has a main window, or when that cannot be read (the close request then finds out).</summary>
    private static bool HasMainWindow(IProcessHandle process)
    {
        try
        {
            return process.RefreshMainWindowHandle() != nint.Zero;
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException)
        {
            return true;
        }
    }

    private static KillResult NoClosableWindow(int processId, string processName) => new(
        Success: false,
        ProcessId: processId,
        ProcessName: processName,
        ErrorMessage: $"'{processName}' (PID {processId}) has no main window that can receive a close request " +
                      "(it may be windowless, running only in the notification area, or its window is blocked by a " +
                      "modal dialog). It is still running and nothing was terminated. To terminate it anyway, call " +
                      $"kill_process again with forceKill=true: {ForceKillConsequence}",
        ErrorCode: nameof(ProcessErrorCode.NoClosableWindow));

    /// <summary>The refusal of a process ID or timeout that is not valid, or null.</summary>
    private static KillResult? ArgumentRefusal(int processId, int gracefulTimeoutMs)
    {
        // Validate process ID
        if (processId <= 0)
        {
            return new KillResult(
                Success: false,
                ProcessId: processId,
                ErrorMessage: "Invalid process ID. Must be a positive integer.",
                ErrorCode: nameof(ProcessErrorCode.InvalidProcessId));
        }

        // Cannot kill self
        if (processId == Environment.ProcessId)
        {
            return new KillResult(
                Success: false,
                ProcessId: processId,
                ErrorMessage: "Cannot terminate the current process.",
                ErrorCode: nameof(ProcessErrorCode.CannotTerminateSelf));
        }

        if (gracefulTimeoutMs < 0 || gracefulTimeoutMs > MaxGracefulTimeoutMs)
        {
            return new KillResult(
                Success: false,
                ProcessId: processId,
                ErrorMessage: $"gracefulTimeoutMs must be between 0 and {MaxGracefulTimeoutMs}.",
                ErrorCode: nameof(ProcessErrorCode.InvalidTimeout));
        }

        return null;
    }

    /// <summary>
    /// Why the open process must not be closed or terminated, or null: a protected system process, the target
    /// policy, a PID that now belongs to another process than <paramref name="expectedProcessName"/>, the agent's
    /// host, and with <paramref name="forceKill"/> a child process that is denied or belongs to the host (or a tree
    /// that cannot be read). <paramref name="tree"/> is the process tree a forced kill would end, when it was read.
    /// </summary>
    private KillResult? TargetRefusal(
        int processId, string processName, bool forceKill, string? expectedProcessName, out IReadOnlyList<(int ProcessId, string ImageName)>? tree)
    {
        tree = null;

        // Check if it's a protected system process
        if (ProtectedProcesses.Contains(processName))
        {
            return new KillResult(
                Success: false,
                ProcessId: processId,
                ProcessName: processName,
                ErrorMessage: $"Cannot terminate protected system process '{processName}'.",
                ErrorCode: nameof(ProcessErrorCode.ProtectedProcess));
        }

        // The user's policy (WAM_DENY_PROCESSES, WAM_ALLOW_PROCESSES); no argument overrides it
        if (_targetPolicy?.Evaluate([processName]) is { Allowed: false } verdict)
        {
            return new KillResult(
                Success: false,
                ProcessId: processId,
                ProcessName: processName,
                ErrorMessage: $"'{processName}' (PID {processId}) is refused by the server's target policy: {verdict.Reason}. " +
                              "Nothing was done. The policy is set by the user in the server's environment; ask the user to end this process.",
                ErrorCode: nameof(ProcessErrorCode.TargetDeniedByPolicy));
        }

        // PID reuse guard: the process the agent saw in list_processes may have exited
        if (!string.IsNullOrWhiteSpace(expectedProcessName) && !ProcessNameMatches(processName, expectedProcessName))
        {
            return new KillResult(
                Success: false,
                ProcessId: processId,
                ProcessName: processName,
                ErrorMessage: $"PID {processId} belongs to '{processName}', not '{expectedProcessName.Trim()}'. " +
                              "The process you meant may have exited and its PID been reused. Nothing was done; " +
                              "call list_processes to get the current PID.",
                ErrorCode: nameof(ProcessErrorCode.ProcessNameMismatch));
        }

        // Closing or killing the agent's host (the terminal or editor hosting the agent) ends its own session
        if (IsHostProcess(processId))
        {
            return new KillResult(
                Success: false,
                ProcessId: processId,
                ProcessName: processName,
                ErrorMessage: $"'{processName}' (PID {processId}) belongs to the agent's host: {HostProcessKinds}. " +
                              "Closing it would end or disrupt the agent's own session, so it is refused. Nothing was done.",
                ErrorCode: nameof(ProcessErrorCode.CannotTerminateHost));
        }

        // forceKill ends the whole process tree, so every process in it must pass the policy and the
        // host check too. A graceful close asks only the process's own main window to close.
        if (forceKill)
        {
            // Fails closed: the runtime's tree kill finds the children on its own, so an unread tree would
            // still be ended, unchecked
            if (_processWrapper.GetDescendantsOf(processId) is not { } descendants)
            {
                return new KillResult(
                    Success: false,
                    ProcessId: processId,
                    ProcessName: processName,
                    ErrorMessage: $"forceKill also ends the child processes of '{processName}' (PID {processId}), but the process list " +
                                  "could not be read, or did not list the process (it may just have exited), so they could not be " +
                                  "checked against the target policy and the agent's host. Nothing was terminated. Try again, or close " +
                                  "it without forceKill (only its main window is asked to close).",
                    ErrorCode: nameof(ProcessErrorCode.ProcessTreeUnreadable));
            }

            tree = descendants;
            if (DeniedDescendant(descendants) is { } denied)
            {
                return new KillResult(
                    Success: false,
                    ProcessId: processId,
                    ProcessName: processName,
                    ErrorMessage: $"forceKill also ends the child processes of '{processName}' (PID {processId}), and its child process " +
                                  $"'{denied.ImageName}' (PID {denied.ProcessId}) is refused by the server's target policy: {denied.Reason}. " +
                                  "Nothing was terminated. Close it without forceKill (only its main window is asked to close), or ask the user.",
                    ErrorCode: nameof(ProcessErrorCode.TargetDeniedByPolicy));
            }

            if (HostDescendant(descendants) is { } host)
            {
                return new KillResult(
                    Success: false,
                    ProcessId: processId,
                    ProcessName: processName,
                    ErrorMessage: $"forceKill also ends the child processes of '{processName}' (PID {processId}), and its child process " +
                                  $"'{host.ImageName}' (PID {host.ProcessId}) belongs to the agent's host: {HostProcessKinds}. " +
                                  "Ending it would end or disrupt the agent's own session, so nothing was terminated. Close it without " +
                                  "forceKill (only its main window is asked to close), or ask the user.",
                    ErrorCode: nameof(ProcessErrorCode.CannotTerminateHost));
            }
        }

        return null;
    }

    /// <summary>
    /// The first process of a process tree (children, grandchildren, ...) that the target policy refuses,
    /// or null. Without a policy, nothing is refused.
    /// </summary>
    private (int ProcessId, string ImageName, string? Reason)? DeniedDescendant(IReadOnlyList<(int ProcessId, string ImageName)> tree)
    {
        if (_targetPolicy == null)
        {
            return null;
        }

        foreach (var (childId, imageName) in tree)
        {
            if (_targetPolicy.Evaluate([imageName]) is { Allowed: false } verdict)
            {
                return (childId, imageName, verdict.Reason);
            }
        }

        return null;
    }

    /// <summary>
    /// The first process of a process tree that belongs to the agent's host (<see cref="IHostGuard.IsHostProcess"/>,
    /// or this server itself), or null. The live ancestor walk of <see cref="IsHostProcess"/> is not repeated
    /// for each child: a child that is an ancestor of the server makes the tree's root one too, and the
    /// root has been checked already.
    /// </summary>
    private (int ProcessId, string ImageName)? HostDescendant(IReadOnlyList<(int ProcessId, string ImageName)> tree)
    {
        foreach (var child in tree)
        {
            if (child.ProcessId == Environment.ProcessId || _hostGuard?.IsHostProcess(child.ProcessId) == true)
            {
                return child;
            }
        }

        return null;
    }

    /// <summary>
    /// Graceful mode: posts WM_CLOSE to the main window and waits. Never escalates to a forced kill;
    /// a process that cannot be closed or is still running is left alone and reported as such.
    /// </summary>
    private static async Task<KillResult> CloseGracefullyAsync(
        IProcessHandle process, int processId, string processName, int timeoutMs, Action? beforeWait, CancellationToken cancellationToken)
    {
        bool closeRequested;
        try
        {
            closeRequested = process.CloseMainWindow();
        }
        catch (Exception ex)
        {
            return KillFailureFromException(ex, processId, processName);
        }

        if (!closeRequested)
        {
            if (HasExitedSafe(process))
            {
                return new KillResult(Success: true, ProcessId: processId, ProcessName: processName, Exited: true);
            }

            return NoClosableWindow(processId, processName);
        }

        beforeWait?.Invoke();
        bool exited;
        try
        {
            exited = await process.WaitForExitAsync(timeoutMs, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // The close request was sent; the process may still exit, or show a prompt
            throw;
        }
        catch (Exception ex)
        {
            return KillFailureFromException(ex, processId, processName) with { CloseRequested = true };
        }

        if (exited)
        {
            return new KillResult(Success: true, ProcessId: processId, ProcessName: processName, CloseRequested: true, Exited: true);
        }

        return new KillResult(
            Success: false,
            ProcessId: processId,
            ProcessName: processName,
            ErrorMessage: $"A close request was sent to '{processName}' (PID {processId}), but it was still running after " +
                          $"{timeoutMs} ms. It may be showing a prompt such as 'Save changes?', still shutting down, or " +
                          "running elevated (Windows then drops the close request). Nothing was force-terminated. Handle " +
                          "the prompt, retry with a longer gracefulTimeoutMs, or call kill_process with forceKill=true: " +
                          ForceKillConsequence,
            ErrorCode: nameof(ProcessErrorCode.GracefulCloseTimedOut),
            CloseRequested: true);
    }

    /// <summary>
    /// Force mode: terminates the process and its child processes, then confirms that it exited.
    /// </summary>
    private static async Task<KillResult> TerminateForcefullyAsync(
        IProcessHandle process, int processId, string processName, Action? beforeWait, CancellationToken cancellationToken)
    {
        AggregateException? treeFailures = null;
        try
        {
            process.Kill(entireProcessTree: true);
        }
        catch (AggregateException ex)
        {
            // Kill(entireProcessTree: true) tries the root and every descendant, then reports all
            // failures together without saying which process failed. Whether the root is gone,
            // which decides the outcome, is checked below.
            treeFailures = ex;
        }
        catch (InvalidOperationException) when (HasExitedSafe(process))
        {
            return new KillResult(Success: true, ProcessId: processId, ProcessName: processName, Exited: true);
        }
        catch (Exception ex)
        {
            return KillFailureFromException(ex, processId, processName);
        }

        var accessDenied = treeFailures != null && IsAccessDenied(treeFailures);
        var warning = treeFailures == null
            ? null
            : "Some child processes could not be terminated and may still be running" +
              (accessDenied ? " (access denied: they probably run elevated or as another user)" : "") +
              ": " + string.Join("; ", treeFailures.InnerExceptions.Select(e => e.Message));

        beforeWait?.Invoke();
        bool exited;
        try
        {
            exited = await process.WaitForExitAsync(ForceKillExitWaitMs, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // The termination was requested; only the confirmation that the process exited is not waited for
            throw;
        }
        catch (Exception ex)
        {
            return KillFailureFromException(ex, processId, processName) with { Forced = true, Warning = warning };
        }

        if (exited)
        {
            return new KillResult(Success: true, ProcessId: processId, ProcessName: processName, Forced: true, Exited: true, Warning: warning);
        }

        if (accessDenied)
        {
            // The root is still running, so the denial most likely came from the root itself
            return KillFailureFromException(treeFailures!, processId, processName) with
            {
                Forced = true,
                Warning = "Termination was attempted for the whole process tree, so some of its child processes may " +
                          "have been terminated even though the process itself is still running."
            };
        }

        return new KillResult(
            Success: false,
            ProcessId: processId,
            ProcessName: processName,
            ErrorMessage: $"Termination of '{processName}' (PID {processId}) was requested, but the process had not exited " +
                          $"after {ForceKillExitWaitMs} ms. It may still be shutting down; check with list_processes.",
            ErrorCode: nameof(ProcessErrorCode.TerminationNotConfirmed),
            Forced: true,
            Warning: warning);
    }

    /// <summary>
    /// True for a process of the agent's host: what the host guard recorded at server start (this server,
    /// its ancestors, the console host's process and the startup foreground window's process), or an
    /// ancestor found by a live walk of the process tree. The live walk keeps the check working when the
    /// host guard could not read the ancestors at start (it then logs a warning and protects less).
    /// </summary>
    private bool IsHostProcess(int processId)
    {
        if (_hostGuard?.IsHostProcess(processId) == true)
        {
            return true;
        }

        try
        {
            return _processWrapper.IsAncestorOfCurrentProcess(processId);
        }
        catch (Exception)
        {
            // If the process tree cannot be read, fall back to the runtime's own check in Kill(entireProcessTree: true)
            return false;
        }
    }

    private static bool ProcessNameMatches(string actualName, string expectedName)
    {
        var expected = expectedName.Trim();
        if (expected.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
        {
            expected = expected[..^4];
        }

        return actualName.Equals(expected, StringComparison.OrdinalIgnoreCase);
    }

    private static bool HasExitedSafe(IProcessHandle process)
    {
        try
        {
            return process.HasExited;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static bool IsAccessDenied(Exception ex) => ex switch
    {
        Win32Exception win32 => win32.NativeErrorCode == ErrorAccessDenied,
        UnauthorizedAccessException => true,
        AggregateException aggregate => aggregate.InnerExceptions.Any(IsAccessDenied),
        _ => false
    };

    private static KillResult KillFailureFromException(Exception ex, int processId, string? processName)
    {
        if (IsAccessDenied(ex))
        {
            var target = processName != null ? $"'{processName}' (PID {processId})" : $"PID {processId}";
            return new KillResult(
                Success: false,
                ProcessId: processId,
                ProcessName: processName,
                ErrorMessage: $"Access denied for {target}. It is probably running elevated (as administrator), as another " +
                              "user, or as a protected process; terminating it requires the server to run with at least the " +
                              "same privileges.",
                ErrorCode: nameof(ProcessErrorCode.AccessDenied));
        }

        return new KillResult(
            Success: false,
            ProcessId: processId,
            ProcessName: processName,
            ErrorMessage: ex.Message,
            ErrorCode: nameof(ProcessErrorCode.TerminationException));
    }

    private static KillResult ProcessNotFound(int processId) => new(
        Success: false,
        ProcessId: processId,
        ErrorMessage: "Process not found. It may have already exited.",
        ErrorCode: nameof(ProcessErrorCode.ProcessNotFound));

    private static readonly HashSet<string> SupportedBrowsers = new(StringComparer.OrdinalIgnoreCase)
    {
        "chrome", "edge", "brave"
    };

    /// <summary>
    /// How long a debug browser started with port 0 may take, after its window wait, to write the DevToolsActivePort file
    /// that names its port (roadmap M5.1); tests shorten it.
    /// </summary>
    public TimeSpan DevToolsPortTimeout { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>How long the <see cref="IDebugBrowserReadiness"/> check may wait for the DevTools endpoint to answer.</summary>
    public TimeSpan DebugReadinessTimeout { get; init; } = TimeSpan.FromSeconds(10);

    private static readonly TimeSpan DevToolsPortPollInterval = TimeSpan.FromMilliseconds(100);

    /// <remarks>
    /// The steps (roadmap M5.1): find the .exe (<see cref="DebugBrowserLocator"/>); check an explicit port is free (port 0
    /// needs no check: the browser picks a free port); refuse a relative profile path, and a profile that a running browser
    /// holds (<see cref="ChromiumProfileLock"/>); delete a DevToolsActivePort file an earlier run of the profile left;
    /// start the browser with <see cref="BuildDebugBrowserArguments"/> and wait for a window of the started process only
    /// (<see cref="LaunchRequest.OnlyStartedProcess"/>); refuse a launch that handed off or did not start; for port 0, read
    /// the port from DevToolsActivePort; then let <see cref="IDebugBrowserReadiness"/>, when the server has one, confirm
    /// that the endpoint answers (<see cref="DebugBrowserReadinessProbe"/>), and report cdpReady, the browser version and
    /// the endpoints it found. A failure after the start keeps the process, window and profile in the result while the
    /// browser still runs, so that it can be closed.
    /// </remarks>
    public async Task<DebugBrowserLaunchResult> LaunchBrowserWithDebugAsync(
        DebugBrowserLaunchRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var browser = request.Browser;
        if (!SupportedBrowsers.Contains(browser))
        {
            return DebugLaunchFailure($"Unsupported browser '{browser}'. Supported browsers: chrome, edge, brave.", BrowserErrorCode.UnsupportedBrowser);
        }

        var location = _debugBrowserLocator.Locate(browser, request.ExecutablePath);
        if (location.Path == null)
        {
            return DebugLaunchFailure(BrowserNotFoundMessage(browser, request.ExecutablePath, location.Tried), BrowserErrorCode.BrowserNotFound);
        }

        var port = request.Port;
        if (port != DebugBrowserLaunchRequest.EphemeralPort && IsPortInUse(port))
        {
            return DebugLaunchFailure(
                $"Port {port} is already in use. Nothing was launched. Leave port out (0) to let the browser pick a free port, or choose another one.",
                BrowserErrorCode.PortInUse);
        }

        if (request.UserDataDir is { } given && !Path.IsPathFullyQualified(given))
        {
            // The server would read DevToolsActivePort relative to its own current directory, the browser would take the
            // folder relative to its install folder
            return DebugLaunchFailure(
                $"userDataDir must be a full path, but '{given}' is not. Nothing was launched.", ToolErrorReporting.InvalidArgumentsErrorCode);
        }

        var temporaryProfile = request.UserDataDir == null;
        var userDataDir = request.UserDataDir ?? _debugBrowserProfiles.NewTemporaryPath();

        // Before anything is deleted or started: a start would hand the url to the browser holding the profile, and the
        // port file deleted below would be that browser's
        if (ChromiumProfileLock.HeldReason(userDataDir) is { } held)
        {
            return DebugLaunchFailure(
                $"A browser is already running with this userDataDir: it holds the profile's {ChromiumProfileLock.FileName} ({held}). " +
                "Nothing was launched and nothing in the profile was changed; a start would only hand the url to that browser, " +
                $"which ignores the debug switches. {CloseOnlyYourOwnAdvice(userDataDir)} Otherwise leave userDataDir out to use a new temporary profile.",
                BrowserErrorCode.DebugLaunchFailed) with { UserDataDir = userDataDir };
        }

        if (DevToolsActivePortFile.DeleteStale(userDataDir) is { } notDeleted)
        {
            // Chromium writes the file and closes it, so a running browser does not stop the delete
            return DebugLaunchFailure(
                $"The {DevToolsActivePortFile.FileName} file an earlier run left in '{userDataDir}' could not be deleted ({notDeleted}), " +
                "so the port read from it could be that run's. Nothing was launched. Check the file's attributes and the folder's " +
                "permissions, or leave userDataDir out to use a new temporary profile.",
                BrowserErrorCode.DebugLaunchFailed);
        }

        if (temporaryProfile)
        {
            // The server's own folder (roadmap M5.4): its path proves to close_debug_browser that the browser is the server's
            try
            {
                Directory.CreateDirectory(userDataDir);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return DebugLaunchFailure(
                    $"The temporary profile folder '{userDataDir}' could not be created ({ex.Message}). Nothing was launched.",
                    BrowserErrorCode.DebugLaunchFailed);
            }
        }

        var arguments = BuildDebugBrowserArguments(port, userDataDir, request.Url);

        // The started browser, from the moment the start returns its PID (roadmap M5.4): a call cancelled after that closes it
        int? startedProcessId = null;
        DateTime? startedAt = null;

        // A start still running when the call was cancelled: its browser is ended when it comes
        Task<IProcessHandle?>? abandonedStart = null;

        try
        {
            var launch = await LaunchApplicationAsync(
                new LaunchRequest(location.Path, arguments.ToArray())
                {
                    OnlyStartedProcess = true,
                    OnStarted = pid =>
                    {
                        // Read at once, so that a failure or a cancellation below can tell the browser from a process
                        // that got its PID after it exited
                        startedAt = _processWrapper.GetProcessStartTime(pid);
                        startedProcessId = pid;
                    },
                    OnStartAbandoned = start => abandonedStart = start
                },
                waitForWindow: true,
                request.WaitTimeoutMs,
                cancellationToken).ConfigureAwait(false);

            if (!launch.Success)
            {
                return await WithoutLeftoverProfileAsync(
                    DebugLaunchFailure(launch.ErrorMessage ?? "Failed to launch browser", launch.ErrorCode ?? nameof(BrowserErrorCode.DebugLaunchFailed)) with
                    {
                        Warning = launch.Warning
                    },
                    userDataDir, temporaryProfile).ConfigureAwait(false);
            }

            if (NotStartedReason(browser, launch, temporaryProfile, CloseOnlyYourOwnAdvice(userDataDir)) is { } notStarted)
            {
                // Only the launch's own warning: launch_application's note for a launch without a window advises its
                // expectWindow, which this tool does not have. A consent prompt may still start the browser with the
                // profile, so the profile is kept then
                var notStartedResult = new DebugBrowserLaunchResult(false, null, null, null, userDataDir, notStarted,
                    nameof(BrowserErrorCode.DebugLaunchFailed), WithoutNoWindowNote(launch, request.WaitTimeoutMs), launch.Status);
                return launch.Status == LaunchStatus.AwaitingUserConsent
                    ? notStartedResult
                    : await WithoutLeftoverProfileAsync(notStartedResult, userDataDir, temporaryProfile).ConfigureAwait(false);
            }

            var processId = launch.ProcessId!.Value;
            var startTime = startedProcessId == processId ? startedAt : _processWrapper.GetProcessStartTime(processId);
            var started = new DebugBrowserLaunchResult(true, processId, launch.WindowHandle, port, userDataDir, null, null,
                launch.Warning, launch.Status) { StartTime = startTime };
            if (port == DebugBrowserLaunchRequest.EphemeralPort)
            {
                if (await WaitForDevToolsPortAsync(userDataDir, cancellationToken).ConfigureAwait(false) is not { } reportedPort)
                {
                    return await FailedAfterStartAsync(started with { CdpPort = null }, startTime, temporaryProfile,
                        $"The browser started (processId {processId}) but did not write its DevTools port to " +
                        $"'{DevToolsActivePortFile.PathIn(userDataDir)}' within {DevToolsPortTimeout.TotalSeconds:0.#} seconds, so " +
                        "the port is not known.").ConfigureAwait(false);
                }

                started = started with { CdpPort = reportedPort };
            }

            if (_debugBrowserReadiness != null)
            {
                DebugBrowserReadiness readiness;
                try
                {
                    readiness = await _debugBrowserReadiness.WaitUntilReadyAsync(
                        new DebugBrowserEndpoint(browser, processId, started.CdpPort!.Value, userDataDir, startTime),
                        DebugReadinessTimeout,
                        cancellationToken).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
                {
                    // The hook reports, it does not throw; one that does (its own timeout, a refused connection) still
                    // leaves a running browser that the result must name
                    readiness = new DebugBrowserReadiness(Ready: false, Error: $"The DevTools endpoint check failed: {ex.Message}");
                }

                if (!readiness.Ready)
                {
                    // A port another process holds, or one whose answer nothing ties to this browser, is not offered as
                    // its cdpPort: a CDP client pointed at it would control whatever listens there
                    var notTheBrowsers = readiness.Reason is DebugBrowserNotReadyReason.PortHeldByOtherProcess
                        or DebugBrowserNotReadyReason.PortOwnerUnknown;
                    return await FailedAfterStartAsync(notTheBrowsers ? started with { CdpPort = null } : started, startTime, temporaryProfile,
                        readiness.Error ?? "The DevTools endpoint did not answer.").ConfigureAwait(false);
                }

                started = started with
                {
                    CdpReady = true,
                    BrowserVersion = readiness.BrowserVersion,
                    CdpEndpoint = readiness.CdpEndpoint,
                    BrowserWsEndpoint = readiness.BrowserWebSocketUrl
                };
            }

            return started;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Nobody learns this browser's PID or port, and its debug port has no authentication: close it (roadmap M5.4)
            if (abandonedStart != null)
            {
                // The start still runs: its browser may still start with the profile, so neither is touched before it returns.
                // Not awaited: the call (and the input gate it holds) does not wait for a start that may take minutes
                _ = EndAbandonedStartAsync(abandonedStart, userDataDir, temporaryProfile);
            }
            else
            {
                await AbandonCancelledLaunchAsync(startedProcessId, startedAt, userDataDir, temporaryProfile).ConfigureAwait(false);
            }

            throw;
        }
        catch (Exception ex)
        {
            return await WithoutLeftoverProfileAsync(
                DebugLaunchFailure(ex.Message, BrowserErrorCode.DebugLaunchFailed), userDataDir, temporaryProfile).ConfigureAwait(false);
        }
    }

    /// <summary>How long a debug launch waits for a browser it ends to exit, and for its temporary profile to be deleted.</summary>
    private static readonly TimeSpan AbandonTimeout = TimeSpan.FromSeconds(5);

    /// <summary>
    /// A launch_browser_with_debug call cancelled after the start (roadmap M5.4): the browser it started is ended with its
    /// process tree, since no result names its PID or port and its debug port has no authentication, and its temporary
    /// profile is deleted. Only that browser: the PID must still have the start time read when it started (or, when that
    /// could not be read, the browser's own command line must name the launch's profile). Best effort; never throws.
    /// </summary>
    private async Task AbandonCancelledLaunchAsync(int? processId, DateTime? startTime, string userDataDir, bool temporaryProfile)
    {
        if (processId is int pid)
        {
            try
            {
                using var process = _processWrapper.OpenProcess(pid);
                if (process != null && !HasExitedSafe(process) && IsStartedDebugBrowser(pid, startTime, userDataDir))
                {
                    process.Kill(entireProcessTree: true);
                    await process.WaitForExitAsync((int)AbandonTimeout.TotalMilliseconds, CancellationToken.None).ConfigureAwait(false);
                }
            }
            catch (Exception ex) when (ex is InvalidOperationException or Win32Exception or NotSupportedException or AggregateException)
            {
                // It exited meanwhile, or cannot be ended; nothing else to do
            }
        }

        if (temporaryProfile)
        {
            await _debugBrowserProfiles.DeleteAsync(userDataDir, AbandonTimeout).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// A launch_browser_with_debug call cancelled while its start still ran (roadmap M5.4): once the start returns, the
    /// browser it brought is ended with its process tree (the handle is the started process's own, so it cannot be another
    /// process that got the PID), and only then is the temporary profile deleted. Best effort; never throws.
    /// </summary>
    private async Task EndAbandonedStartAsync(Task<IProcessHandle?> start, string userDataDir, bool temporaryProfile)
    {
        try
        {
            IProcessHandle? process;
            try
            {
                process = await start.ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                process = null; // Nothing was started
            }

            if (process != null)
            {
                using (process)
                {
                    if (!HasExitedSafe(process))
                    {
                        process.Kill(entireProcessTree: true);
                        await process.WaitForExitAsync((int)AbandonTimeout.TotalMilliseconds, CancellationToken.None).ConfigureAwait(false);
                    }
                }
            }

            if (temporaryProfile)
            {
                await _debugBrowserProfiles.DeleteAsync(userDataDir, AbandonTimeout).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception or NotSupportedException or AggregateException
                                       or IOException or UnauthorizedAccessException)
        {
            // It exited meanwhile, or cannot be ended; a temporary profile left is swept by a later launch
        }
    }

    /// <summary>
    /// Whether PID <paramref name="processId"/> is still the browser this launch started: the start time read at its start,
    /// or when that was not read, a command line that names <paramref name="userDataDir"/>.
    /// </summary>
    private bool IsStartedDebugBrowser(int processId, DateTime? startTime, string userDataDir)
    {
        if (startTime != null)
            return _processWrapper.GetProcessStartTime(processId) == startTime;

        var switches = ChromiumCommandLine.Parse(_processWrapper.GetProcessCommandLine(processId));
        return switches.TryGetValue("user-data-dir", out var profile) && DebugBrowserProfiles.SamePath(profile, userDataDir);
    }

    /// <summary>
    /// A failed debug launch whose browser is not running (no processId): its new temporary profile is deleted, since no
    /// browser will use it (roadmap M5.4), and the result names no profile folder. Any other result is returned as it is.
    /// </summary>
    private async Task<DebugBrowserLaunchResult> WithoutLeftoverProfileAsync(DebugBrowserLaunchResult result, string userDataDir, bool temporaryProfile)
    {
        if (!temporaryProfile || result.ProcessId != null)
            return result;

        return await _debugBrowserProfiles.DeleteAsync(userDataDir, AbandonTimeout).ConfigureAwait(false) == null
            ? result with { UserDataDir = null }
            : result;
    }

    private static DebugBrowserLaunchResult DebugLaunchFailure(string error, BrowserErrorCode errorCode) =>
        DebugLaunchFailure(error, errorCode.ToString());

    private static DebugBrowserLaunchResult DebugLaunchFailure(string error, string errorCode) =>
        new(Success: false, ProcessId: null, WindowHandle: null, CdpPort: null, UserDataDir: null, Error: error, ErrorCode: errorCode);

    private static string BrowserNotFoundMessage(string browser, string? executablePath, IReadOnlyList<string> tried) =>
        string.IsNullOrWhiteSpace(executablePath)
            ? $"Browser '{browser}' was not found. Tried: {string.Join("; ", tried)}. Nothing was launched. If it is installed " +
              $"elsewhere (another channel or a portable copy), pass the full path of its {DebugBrowserLocator.ExecutableName(browser)} as executablePath."
            : $"executablePath '{executablePath.Trim()}' does not exist. Nothing was launched.";

    /// <summary>
    /// What a failure says about closing a browser. A result that names no process gives no safe way to find "that
    /// browser": one found by name or title may be the user's own. close_debug_browser closes a browser on a userDataDir
    /// the agent passed only while this server run started it (a named profile is the server's own folder, so it always can).
    /// </summary>
    private string CloseOnlyYourOwnAdvice(string userDataDir) => _debugBrowserProfiles.KindOf(userDataDir) == DebugProfileKind.Custom
        ? "If it is a debug browser started with this userDataDir, launch again with reuseIfRunning=true to use it. " +
          "close_debug_browser closes it only if this server started it; otherwise ask the user to close it, and never close a " +
          "browser you did not start."
        : "If it is a debug browser started with this profile, launch again with reuseIfRunning=true to use it, or close it " +
          "with close_debug_browser; never close a browser you did not start.";

    /// <summary>
    /// Why a launch that Windows accepted did not start a debug browser of its own, or null when it did: the started
    /// process is running (with its window, or still without one when the wait ended). The window wait counted only the
    /// started process's windows, so a launch whose request went to a browser already running with the same profile ends
    /// with the launcher gone and no window (that browser ignores the debug switches of the request); so does a browser
    /// that could not start with the profile. The profile lock is checked before the start, so the first is a browser
    /// that took the profile meanwhile; with a new temporary profile it cannot be.
    /// </summary>
    private static string? NotStartedReason(string browser, LaunchResult launch, bool temporaryProfile, string closeAdvice) => launch.Status switch
    {
        LaunchStatus.ExitedWithoutWindow or LaunchStatus.PossibleHandoff when temporaryProfile =>
            $"The {browser} process that was started exited without a window of its own, so no debug port was opened. No " +
            "other browser can use its new temporary profile, so the browser could not start (for example a policy, or a " +
            "crash). Ask the user before trying again.",
        LaunchStatus.ExitedWithoutWindow or LaunchStatus.PossibleHandoff =>
            $"The {browser} process that was started exited without a window of its own, so no debug port was opened. Either " +
            $"a {browser} already running with this userDataDir took the request over (it ignores the debug switches, and has " +
            "most likely opened the url in a new window of its own), or the browser could not start with this profile (for " +
            $"example a folder it cannot write, a policy, or a crash). {closeAdvice} Otherwise leave userDataDir " +
            "out to use a new temporary profile.",
        LaunchStatus.AwaitingUserConsent =>
            "Windows is showing a consent or security prompt for the browser's start (see warning), so it is not known " +
            "whether it starts or which port it gets. Ask the user to answer the prompt; do not launch it again meanwhile.",
        _ when launch.ProcessId == null => "Windows started no browser process, so no debug port was opened.",
        _ => null
    };

    /// <summary><paramref name="launch"/>'s warning without the note <see cref="NoWindowNote"/> added to it.</summary>
    private static string? WithoutNoWindowNote(LaunchResult launch, int waitTimeoutMs)
    {
        if (launch.Status is not { } status || NoWindowNote(status, waitTimeoutMs) is not { } note || launch.Warning is not { } warning)
            return launch.Warning;

        if (warning == note)
            return null;

        return warning.EndsWith("; " + note, StringComparison.Ordinal) ? warning[..^(note.Length + 2)] : warning;
    }

    /// <summary>
    /// A failure after the browser started: it keeps the process, window and port (when <paramref name="started"/> has
    /// one) while that process still runs (its start time is the one read after the start), so that it can be closed;
    /// once it exited, its PID may be another process's, so the result names none. Either way the endpoint is not ready
    /// (cdpReady false).
    /// </summary>
    private async Task<DebugBrowserLaunchResult> FailedAfterStartAsync(
        DebugBrowserLaunchResult started, DateTime? startTime, bool temporaryProfile, string error)
    {
        var processId = started.ProcessId!.Value;
        var stillRunning = _processWrapper.GetProcessStartTime(processId) is { } now && (startTime == null || now == startTime);
        return stillRunning
            ? started with
            {
                Success = false,
                Error = $"{error} The browser (processId {processId}) is still running: close it with close_debug_browser and that processId.",
                ErrorCode = nameof(BrowserErrorCode.DebugLaunchFailed),
                CdpReady = false
            }
            : await WithoutLeftoverProfileAsync(started with
            {
                Success = false,
                ProcessId = null,
                WindowHandle = null,
                CdpPort = null,
                StartTime = null,
                Error = $"{error} The browser (processId {processId}) is no longer running, so there is nothing to close.",
                ErrorCode = nameof(BrowserErrorCode.DebugLaunchFailed),
                CdpReady = false
            }, started.UserDataDir!, temporaryProfile).ConfigureAwait(false);
    }

    /// <summary>
    /// The port a browser started with port 0 wrote to its profile's DevToolsActivePort, polled for at most
    /// <see cref="DevToolsPortTimeout"/>; null when it did not appear. The file of an earlier run was deleted before the
    /// start, so a file here is this launch's.
    /// </summary>
    private async Task<int?> WaitForDevToolsPortAsync(string userDataDir, CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow + DevToolsPortTimeout;
        while (true)
        {
            if (DevToolsActivePortFile.TryReadPort(userDataDir) is { } port)
            {
                return port;
            }

            var left = deadline - DateTime.UtcNow;
            if (left <= TimeSpan.Zero)
            {
                return null;
            }

            await Task.Delay(left < DevToolsPortPollInterval ? left : DevToolsPortPollInterval, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// The switches every debug launch passes first (roadmap M5.1), so that a new profile opens the url rather than a
    /// screen that asks the user something; such a screen would also be the first tab, which the window title and the CDP
    /// tab match would land on. Chrome, Edge and Brave are all Chromium and read the same switches.
    /// </summary>
    public static readonly IReadOnlyList<string> FirstRunSwitches =
    [
        // No first-run experience: Chrome's welcome and "make Chrome your own" pages, Edge's welcome, sign-in and
        // personalization pages, Brave's welcome tour, and the offer to import another browser's data
        "--no-first-run",
        // No bar or dialog that offers to make this browser the user's default browser
        "--no-default-browser-check",
        // No search engine choice dialog (Chrome 120 and later, in some regions) over the first window of a new profile
        "--disable-search-engine-choice-screen",
        // No sync: a browser sign-in (by the user, or Edge's own with the Windows account) would otherwise copy the account's
        // passwords, history and other data into a profile that the unauthenticated debug port exposes. It does not stop
        // the sign-in itself: Edge may still sign in to Microsoft sites with the Windows account (not checked on a desktop yet)
        "--disable-sync"
    ];

    /// <summary>
    /// Builds the argument list for a CDP debug browser launch: <see cref="FirstRunSwitches"/>, the port (0: the browser
    /// picks one and writes it to DevToolsActivePort), the profile and the url. The profile is never the browser's default
    /// one (launch_browser_with_debug refuses a 'User Data' folder): Chrome 136 and later ignore --remote-debugging-port on
    /// the default profile. Values are left unquoted: <see cref="CommandLineBuilder"/> quotes each argument once when the
    /// command line is built, so user data directories containing spaces or a trailing backslash stay a single argument.
    /// </summary>
    public static IReadOnlyList<string> BuildDebugBrowserArguments(int port, string userDataDir, string? url)
    {
        var arguments = new List<string>(FirstRunSwitches)
        {
            $"--remote-debugging-port={port}",
            $"--user-data-dir={userDataDir}"
        };

        if (!string.IsNullOrWhiteSpace(url))
        {
            arguments.Add(url);
        }

        return arguments;
    }

    private static bool IsPortInUse(int port)
    {
        try
        {
            var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, port);
            listener.Start();
            listener.Stop();
            return false;
        }
        catch (System.Net.Sockets.SocketException)
        {
            return true;
        }
    }
}
