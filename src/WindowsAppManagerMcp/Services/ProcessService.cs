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

    /// <param name="targetPolicy">
    /// The server's target policy, which kill_process enforces (roadmap M1.12). The server always passes
    /// it; without it no process is refused by policy.
    /// </param>
    /// <param name="hostGuard">
    /// The agent's host as recorded at server start (roadmap M1.1/M1.7): kill_process refuses its
    /// processes, and a forced kill whose process tree contains one. The server always passes it; without
    /// it only this server and its live ancestors count as the host.
    /// </param>
    public ProcessService(
        IWindowService windowService,
        IInputValidationService validationService,
        IBrowserDetectionService browserDetection,
        IExecutableResolver? executableResolver = null,
        INativeProcessWrapper? processWrapper = null,
        ITargetPolicy? targetPolicy = null,
        IHostGuard? hostGuard = null)
    {
        _windowService = windowService;
        _validationService = validationService;
        _browserDetection = browserDetection;
        _executableResolver = executableResolver ?? new ExecutableResolver();
        _processWrapper = processWrapper ?? new NativeProcessWrapper();
        _targetPolicy = targetPolicy;
        _hostGuard = hostGuard;
    }

    /// <summary>How often the wait for a launched application's window looks for it.</summary>
    private const int WindowPollIntervalMs = 100;

    public async Task<LaunchResult> LaunchApplicationAsync(
        string executable,
        string[]? arguments = null,
        string? workingDirectory = null,
        bool waitForWindow = false,
        int waitTimeoutMs = 5000,
        CancellationToken cancellationToken = default)
    {
        var plan = PlanLaunch(executable, arguments, workingDirectory);
        if (!plan.IsValid)
        {
            return new LaunchResult(false, ErrorMessage: plan.ErrorMessage, ErrorCode: plan.ErrorCode, Warning: plan.Warning);
        }

        var warning = plan.Warning;

        // A call cancelled before the start starts nothing
        cancellationToken.ThrowIfCancellationRequested();

        try
        {
            using var process = _processWrapper.StartProcess(plan.StartInfo!);
            if (process == null)
            {
                return new LaunchResult(false, ErrorMessage: "Failed to start process", ErrorCode: nameof(LaunchErrorCode.ProcessStartFailed), Warning: warning);
            }

            nint? windowHandle = null;

            if (waitForWindow)
            {
                windowHandle = await WaitForMainWindowAsync(process, waitTimeoutMs, cancellationToken).ConfigureAwait(false);
            }

            return new LaunchResult(
                Success: true,
                ProcessId: process.Id,
                WindowHandle: windowHandle,
                Warning: warning
            );
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Cancelled while waiting for the window: the program was started and keeps running
            throw;
        }
        catch (Exception ex)
        {
            return new LaunchResult(false, ErrorMessage: ex.Message, ErrorCode: nameof(LaunchErrorCode.LaunchException), Warning: warning);
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

        var effectiveWorkingDirectory = workingDirValidation.SanitizedPath ?? workingDirectory ?? Environment.CurrentDirectory;
        var target = _executableResolver.Resolve(executableValidation.SanitizedExecutable ?? executable, effectiveWorkingDirectory);

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
            WorkingDirectory = effectiveWorkingDirectory
        };

        if (arguments?.Length > 0)
        {
            startInfo.Arguments = CommandLineBuilder.JoinArguments(arguments, quoteCmdDelimiters: !isNativeExecutable);
        }

        return new LaunchPlan(startInfo, target.Kind, warning);
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
        IReadOnlyList<(string Executable, string[]? Arguments, string? WorkingDirectory)> applications,
        bool waitForWindows = false,
        int waitTimeoutMs = 5000,
        IProgress<OperationProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var results = new List<LaunchResult>(applications.Count);

        for (var i = 0; i < applications.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var app = applications[i];
            var result = await LaunchApplicationAsync(
                app.Executable,
                app.Arguments,
                app.WorkingDirectory,
                waitForWindows,
                waitTimeoutMs,
                cancellationToken).ConfigureAwait(false);

            results.Add(result);
            progress?.Report(new OperationProgress(i + 1, applications.Count,
                $"{i + 1} of {applications.Count}: {app.Executable} {DescribeLaunch(result, waitForWindows)}"));
        }

        return results;
    }

    private static string DescribeLaunch(LaunchResult result, bool waitedForWindow) =>
        !result.Success ? $"was not started ({result.ErrorCode})"
        : !waitedForWindow ? "started"
        : result.WindowHandle != null ? "started, its window appeared"
        : "started, no window appeared in time";

    public async Task<KillResult> KillProcessAsync(
        int processId,
        bool confirm,
        bool forceKill = false,
        int gracefulTimeoutMs = DefaultGracefulTimeoutMs,
        string? expectedProcessName = null,
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

            if (TargetRefusal(processId, processName, forceKill, expectedProcessName, out _) is { } refused)
            {
                return refused;
            }

            // A call cancelled before anything is sent does nothing
            cancellationToken.ThrowIfCancellationRequested();

            return forceKill
                ? await TerminateForcefullyAsync(process, processId, processName, cancellationToken).ConfigureAwait(false)
                : await CloseGracefullyAsync(process, processId, processName, gracefulTimeoutMs, cancellationToken).ConfigureAwait(false);
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

            return TargetRefusal(processId, processName, forceKill, expectedProcessName, out var tree) is { } refused
                ? KillTargetCheck.Refused(refused)
                : new KillTargetCheck(null, processName, tree ?? []);
        }
    }

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
        IProcessHandle process, int processId, string processName, int timeoutMs, CancellationToken cancellationToken)
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

            return new KillResult(
                Success: false,
                ProcessId: processId,
                ProcessName: processName,
                ErrorMessage: $"'{processName}' (PID {processId}) has no main window that can receive a close request " +
                              "(it may be windowless, running only in the notification area, or its window is blocked by a " +
                              "modal dialog). It is still running and nothing was terminated. To terminate it anyway, call " +
                              $"kill_process again with forceKill=true: {ForceKillConsequence}",
                ErrorCode: nameof(ProcessErrorCode.NoClosableWindow));
        }

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
        IProcessHandle process, int processId, string processName, CancellationToken cancellationToken)
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

    /// <summary>
    /// Looks for the launched process's window every <see cref="WindowPollIntervalMs"/> until
    /// <paramref name="timeoutMs"/> has passed. Null when none appeared, or when the process can no longer be read.
    /// </summary>
    private async Task<nint?> WaitForMainWindowAsync(IProcessHandle process, int timeoutMs, CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();

        while (stopwatch.ElapsedMilliseconds < timeoutMs)
        {
            try
            {
                var mainWindowHandle = process.RefreshMainWindowHandle();
                if (mainWindowHandle != nint.Zero)
                {
                    return mainWindowHandle;
                }

                // Also try to find windows belonging to this process
                var windows = _windowService.FindWindows(processId: process.Id);
                if (windows.Count > 0)
                {
                    return windows[0].Handle;
                }
            }
            catch
            {
                break;
            }

            await Task.Delay(WindowPollIntervalMs, cancellationToken).ConfigureAwait(false);
        }

        return null;
    }

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
