using System.ComponentModel;
using System.Diagnostics;
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

    /// <param name="targetPolicy">
    /// The server's target policy, which kill_process enforces (roadmap M1.12). The server always passes
    /// it; without it no process is refused by policy.
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
    public ProcessService(
        IWindowService windowService,
        IInputValidationService validationService,
        IBrowserDetectionService browserDetection,
        IExecutableResolver? executableResolver = null,
        INativeProcessWrapper? processWrapper = null,
        ITargetPolicy? targetPolicy = null,
        IHostGuard? hostGuard = null,
        IWindowWaitService? windowWait = null)
    {
        _windowService = windowService;
        _validationService = validationService;
        _browserDetection = browserDetection;
        _executableResolver = executableResolver ?? new ExecutableResolver();
        _processWrapper = processWrapper ?? new NativeProcessWrapper();
        _targetPolicy = targetPolicy;
        _hostGuard = hostGuard;
        _windowWait = windowWait ?? new WindowWaitService(new NativeWindowWrapper(), windowService, _processWrapper);
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

        var plan = PlanLaunch(request.Executable, request.Arguments, request.WorkingDirectory);
        if (!plan.IsValid)
        {
            return Rejected(plan);
        }

        // A call cancelled before the start starts nothing
        cancellationToken.ThrowIfCancellationRequested();

        var launch = await StartAsync(plan, waitForWindow, cancellationToken).ConfigureAwait(false);
        if (launch.Outcome is { } ended)
        {
            return ended;
        }

        using (launch.Process)
        {
            // Cancelled while waiting for the window: the program was started and keeps running, and the call throws
            if (!waitForWindow)
            {
                return StartedWithoutWait(launch);
            }

            var owner = OwnerOf(launch, request);
            return await WaitForWindowAsync(launch, request, owner, owner.Matches, waitTimeoutMs, tryClaim: null, cancellationToken)
                .ConfigureAwait(false);
        }
    }

    private static LaunchResult Rejected(LaunchPlan plan) =>
        new(false, ErrorMessage: plan.ErrorMessage, ErrorCode: plan.ErrorCode, Warning: plan.Warning);

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
    /// <see cref="LaunchErrorCode.LaunchException"/>.
    /// </summary>
    private async Task<StartedLaunch> StartAsync(LaunchPlan plan, bool takeBaseline, CancellationToken cancellationToken)
    {
        var baseline = takeBaseline ? _windowWait.TakeBaseline() : null;
        var launchedAt = DateTime.UtcNow;
        // On a thread of its own, not the thread pool: a start that waits for the user's answer to a UAC prompt can take
        // minutes, and a busy thread pool must not delay a start into looking like one
        var start = Task.Factory.StartNew(
            () => _processWrapper.StartProcess(plan.StartInfo!),
            CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);

        Task finished;
        using (var stopTimer = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
        {
            finished = await Task.WhenAny(start, Task.Delay(StartTimeout, stopTimer.Token)).ConfigureAwait(false);
            stopTimer.Cancel();
        }

        if (finished != start)
        {
            ReleaseWhenStarted(start);
            cancellationToken.ThrowIfCancellationRequested();
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
            return new StartedLaunch(plan, await start.ConfigureAwait(false), baseline, launchedAt);
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
            DocumentHints = DocumentHints(target, request.Arguments),
            TryClaim = tryClaim
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
        LaunchWindowOwner.For(_processWrapper, launch.Process?.Id, launch.LaunchedAtUtc, launch.Plan.Target!, request.ExpectWindow);

    /// <summary>
    /// A batch launch's owner check: <paramref name="owner"/>'s match, except for a window whose process another launch of
    /// the batch started (or a child of it) and this one did not. Two launches of the same program both match each
    /// other's windows by name and path; the started process decides between them.
    /// </summary>
    private static Func<WindowCandidate, bool> BatchOwner(LaunchWindowOwner owner, IReadOnlyList<LaunchWindowOwner> others) =>
        window => owner.Matches(window) &&
                  (owner.StartedProcess(window.ProcessId) || !others.Any(other => other.StartedProcess(window.ProcessId)));

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
    /// The texts that show a launched document or URL in an existing window's title: the file or folder names among the
    /// arguments (and a document launched directly), and the host names of web addresses. Options (-x, --x) are skipped.
    /// </summary>
    public static IReadOnlyList<string> DocumentHints(ResolvedLaunchTarget target, IEnumerable<string>? arguments)
    {
        var values = (arguments ?? []).Where(a => !string.IsNullOrWhiteSpace(a) && !a.StartsWith('-')).ToList();
        if (target.Kind == LaunchTargetKind.Other)
            values.Add(target.FileName);

        var hints = new List<string>();
        foreach (var value in values)
        {
            string? hint = null;
            if (Uri.TryCreate(value, UriKind.Absolute, out var uri) && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps))
            {
                hint = uri.Host.StartsWith("www.", StringComparison.OrdinalIgnoreCase) ? uri.Host[4..] : uri.Host;
            }
            else if (value.IndexOfAny(['\\', '/']) >= 0 || Path.HasExtension(value))
            {
                try
                {
                    hint = Path.GetFileName(value.TrimEnd('\\', '/'));
                }
                catch (ArgumentException)
                {
                    hint = null;
                }
            }

            if (hint is { Length: >= 3 } && !hints.Contains(hint, StringComparer.OrdinalIgnoreCase))
                hints.Add(hint);
        }

        return hints;
    }

    /// <summary>
    /// Validates and resolves a launch request and builds the start info, without starting anything.
    /// </summary>
    /// <remarks>
    /// <list type="bullet">
    /// <item>Bare names are resolved to an absolute path by <see cref="IExecutableResolver"/>, never from
    /// the working directory, and the resolved path is what gets started.</item>
    /// <item>Arguments are quoted with the Windows command-line rules (<see cref="CommandLineBuilder"/>).</item>
    /// <item>When the target is a batch file (ShellExecute runs it through cmd.exe) or cannot be verified to
    /// be a native executable, arguments containing cmd.exe metacharacters are rejected, because cmd.exe
    /// ignores \" escaping and expands %VAR% even inside quotes.</item>
    /// </list>
    /// </remarks>
    public LaunchPlan PlanLaunch(string executable, string[]? arguments = null, string? workingDirectory = null)
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
            return LaunchPlan.Rejected(
                $"'{executable}' was not found in the Windows directory, System32, the PATH entries or App Paths. A bare name " +
                "is never passed to Windows unresolved, because Windows would also search the server's current directory. Pass " +
                @"the program's full path, or a URI such as ms-settings: or shell:AppsFolder\<AppUserModelID> for a Store app.",
                nameof(LaunchErrorCode.ExecutableNotFound),
                warning);
        }

        var isNativeExecutable = target.Kind == LaunchTargetKind.NativeExecutable;
        if (!isNativeExecutable && arguments?.Length > 0)
        {
            var unsafeArgument = arguments.FirstOrDefault(ContainsCmdMetacharacters);
            if (unsafeArgument != null)
            {
                return LaunchPlan.Rejected(
                    BuildUnsafeBatchArgumentMessage(executable, target, unsafeArgument),
                    nameof(LaunchErrorCode.UnsafeBatchArguments),
                    warning);
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

        return new LaunchPlan(startInfo, target.Kind, warning, Target: target);
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

    private static string BuildUnsafeBatchArgumentMessage(string executable, ResolvedLaunchTarget target, string argument)
    {
        var shownArgument = argument.Length > MaxArgumentLengthInMessages
            ? argument[..MaxArgumentLengthInMessages] + "..."
            : argument;

        var reason = target.Kind == LaunchTargetKind.BatchFile
            ? $"'{executable}' resolves to the batch file '{target.FileName}'. Windows runs batch files through cmd.exe"
            : $"'{executable}' could not be verified to be a native .exe (it is a URI, document, shortcut, script or an " +
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
    /// Counts each process's visible, titled top-level windows. Unlike the user-facing window listings,
    /// this also counts cloaked windows (on another virtual desktop), tool windows and the desktop
    /// shell's windows: a process that has such a window is running with a window, and hiding it from
    /// list_processes would let "has it exited?" checks pass too early. A UWP window counts for the
    /// app's process and for its ApplicationFrameHost frame host.
    /// </summary>
    private Dictionary<int, int> CountWindowsPerProcess()
    {
        var counts = new Dictionary<int, int>();
        void Count(int processId) => counts[processId] = counts.GetValueOrDefault(processId) + 1;

        foreach (var window in _windowService.FindWindows(new WindowQuery { IncludeShell = true }))
        {
            if (string.IsNullOrEmpty(window.Title))
                continue;

            Count(window.ProcessId);
            if (window.HostProcessId is { } hostProcessId && hostProcessId != window.ProcessId)
                Count(hostProcessId);
        }

        return counts;
    }

    public IReadOnlyList<ProcessInfo> GetRunningProcesses(string? nameFilter = null, bool includeWindowless = false)
    {
        var result = new List<ProcessInfo>();
        var processes = Process.GetProcesses();

        var windowCounts = includeWindowless ? new Dictionary<int, int>() : CountWindowsPerProcess();

        foreach (var process in processes)
        {
            try
            {
                // Apply name filter
                if (!string.IsNullOrEmpty(nameFilter))
                {
                    if (!process.ProcessName.Contains(nameFilter, StringComparison.OrdinalIgnoreCase))
                        continue;
                }

                // Check window count
                var windowCount = windowCounts.GetValueOrDefault(process.Id, 0);
                if (!includeWindowless && windowCount == 0)
                    continue;

                // Get executable path
                var execPath = GetProcessPath(process.Id);

                // Get memory usage
                double? memoryMB = null;
                try
                {
                    memoryMB = process.WorkingSet64 / (1024.0 * 1024.0);
                }
                catch
                {
                    // Access denied for some processes
                }

                result.Add(new ProcessInfo(
                    ProcessId: process.Id,
                    Name: process.ProcessName,
                    ExecutablePath: execPath,
                    WindowCount: windowCount,
                    MemoryUsageMB: memoryMB
                ));
            }
            catch
            {
                // Skip processes we can't access
            }
            finally
            {
                process.Dispose();
            }
        }

        return result;
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

    public string? GetProcessPath(int processId)
    {
        try
        {
            var hProcess = NativeMethods.Kernel32.OpenProcess(
                NativeEnums.PROCESS_QUERY_LIMITED_INFORMATION,
                false,
                (uint)processId);

            if (hProcess == nint.Zero)
                return null;

            try
            {
                var buffer = new char[1024];
                uint size = (uint)buffer.Length;

                if (NativeMethods.Kernel32.QueryFullProcessImageNameW(hProcess, 0, buffer, ref size))
                {
                    return new string(buffer, 0, (int)size);
                }
            }
            finally
            {
                NativeMethods.Kernel32.CloseHandle(hProcess);
            }
        }
        catch
        {
            // Access denied or process terminated
        }

        return null;
    }

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
        var waiting = new List<(int Index, StartedLaunch Launch)>();
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
                var plan = PlanLaunch(request.Executable, request.Arguments, request.WorkingDirectory);
                if (!plan.IsValid)
                {
                    results[i] = Rejected(plan);
                    Report(i);
                    continue;
                }

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
            bool Claim(nint handle)
            {
                lock (claimed)
                {
                    return claimed.Add(handle);
                }
            }

            await Task.WhenAll(waiting.Select(async (item, position) =>
            {
                var owner = owners[position];
                var others = owners.Where((_, other) => other != position).ToList();
                results[item.Index] = await WaitForWindowAsync(
                    item.Launch, applications[item.Index], owner, BatchOwner(owner, others), waitTimeoutMs, Claim,
                    cancellationToken).ConfigureAwait(false);
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

        return results!;
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

    // Browser executable paths for common browsers
    private static readonly Dictionary<string, string[]> BrowserPaths = new(StringComparer.OrdinalIgnoreCase)
    {
        ["chrome"] = new[]
        {
            @"C:\Program Files\Google\Chrome\Application\chrome.exe",
            @"C:\Program Files (x86)\Google\Chrome\Application\chrome.exe"
        },
        ["edge"] = new[]
        {
            @"C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe",
            @"C:\Program Files\Microsoft\Edge\Application\msedge.exe"
        },
        ["brave"] = new[]
        {
            @"C:\Program Files\BraveSoftware\Brave-Browser\Application\brave.exe",
            @"C:\Program Files (x86)\BraveSoftware\Brave-Browser\Application\brave.exe"
        }
    };

    private static readonly HashSet<string> SupportedBrowsers = new(StringComparer.OrdinalIgnoreCase)
    {
        "chrome", "edge", "brave"
    };

    public async Task<DebugBrowserLaunchResult> LaunchBrowserWithDebugAsync(
        string browser = "chrome",
        string? url = null,
        int port = 9222,
        string? userDataDir = null,
        bool waitForWindow = true,
        int waitTimeoutMs = 10000,
        CancellationToken cancellationToken = default)
    {
        // Validate browser type
        if (!SupportedBrowsers.Contains(browser))
        {
            return new DebugBrowserLaunchResult(
                Success: false,
                ProcessId: null,
                WindowHandle: null,
                CdpPort: null,
                UserDataDir: null,
                Error: $"Unsupported browser '{browser}'. Supported browsers: chrome, edge, brave.",
                ErrorCode: nameof(BrowserErrorCode.UnsupportedBrowser));
        }

        // Find browser executable
        var browserPath = ResolveBrowserPath(browser);
        if (browserPath == null)
        {
            return new DebugBrowserLaunchResult(
                Success: false,
                ProcessId: null,
                WindowHandle: null,
                CdpPort: null,
                UserDataDir: null,
                Error: $"Browser '{browser}' not found. Please ensure it is installed.",
                ErrorCode: nameof(BrowserErrorCode.BrowserNotFound));
        }

        // Check if port is already in use
        if (IsPortInUse(port))
        {
            return new DebugBrowserLaunchResult(
                Success: false,
                ProcessId: null,
                WindowHandle: null,
                CdpPort: null,
                UserDataDir: null,
                Error: $"Port {port} is already in use. Choose a different port or close the application using it.",
                ErrorCode: nameof(BrowserErrorCode.PortInUse));
        }

        // Generate user data directory if not specified
        var effectiveUserDataDir = userDataDir ?? Path.Combine(Path.GetTempPath(), $"browser-debug-{Guid.NewGuid()}");

        var arguments = BuildDebugBrowserArguments(port, effectiveUserDataDir, url);

        try
        {
            // Launch browser
            var launchResult = await LaunchApplicationAsync(
                browserPath,
                arguments.ToArray(),
                workingDirectory: null,
                waitForWindow: waitForWindow,
                waitTimeoutMs: waitTimeoutMs,
                cancellationToken: cancellationToken).ConfigureAwait(false);

            if (!launchResult.Success)
            {
                return new DebugBrowserLaunchResult(
                    Success: false,
                    ProcessId: null,
                    WindowHandle: null,
                    CdpPort: null,
                    UserDataDir: null,
                    Error: launchResult.ErrorMessage ?? "Failed to launch browser",
                    ErrorCode: launchResult.ErrorCode ?? nameof(BrowserErrorCode.DebugLaunchFailed));
            }

            return new DebugBrowserLaunchResult(
                Success: true,
                ProcessId: launchResult.ProcessId,
                WindowHandle: launchResult.WindowHandle,
                CdpPort: port,
                UserDataDir: effectiveUserDataDir,
                Error: null,
                ErrorCode: null);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new DebugBrowserLaunchResult(
                Success: false,
                ProcessId: null,
                WindowHandle: null,
                CdpPort: null,
                UserDataDir: null,
                Error: ex.Message,
                ErrorCode: nameof(BrowserErrorCode.DebugLaunchFailed));
        }
    }

    /// <summary>
    /// Builds the argument list for a CDP debug browser launch. Values are left unquoted:
    /// <see cref="CommandLineBuilder"/> quotes each argument once when the command line is built,
    /// so user data directories containing spaces or a trailing backslash stay a single argument.
    /// </summary>
    public static IReadOnlyList<string> BuildDebugBrowserArguments(int port, string userDataDir, string? url)
    {
        var arguments = new List<string>
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

    private string? ResolveBrowserPath(string browser)
    {
        // Check common paths
        if (BrowserPaths.TryGetValue(browser, out var paths))
        {
            foreach (var path in paths)
            {
                if (File.Exists(path))
                {
                    return path;
                }
            }
        }

        // Fall back to PATH lookup
        var executableName = browser switch
        {
            "chrome" => "chrome.exe",
            "edge" => "msedge.exe",
            "brave" => "brave.exe",
            _ => $"{browser}.exe"
        };

        var output = _processWrapper.FindWithWhereExe(executableName);
        return !string.IsNullOrWhiteSpace(output) && File.Exists(output) ? output : null;
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
