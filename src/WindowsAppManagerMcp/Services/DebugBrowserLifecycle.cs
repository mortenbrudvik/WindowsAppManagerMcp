using System.Collections.Concurrent;
using System.ComponentModel;
using System.Globalization;
using WindowsAppManagerMcp.Infrastructure;
using WindowsAppManagerMcp.Models;
using WindowsAppManagerMcp.Services.Interfaces;

namespace WindowsAppManagerMcp.Services;

/// <summary>
/// The debug browsers' lifecycle (roadmap M5.4): named profiles and reuseIfRunning for launch_browser_with_debug, the
/// browsers this server started, and close_debug_browser.
/// </summary>
/// <remarks>
/// A browser is identified by its main process's own command line (ChromiumCommandLine: --user-data-dir and
/// --remote-debugging-port, no --type) and the listener of its port (DebugPortLocator), never by a window title, which a
/// page sets. close_debug_browser closes only a debug browser that this server started (same PID and start time, and its
/// command line still names the profile it was started with) or whose command line shows a debug port and a profile in the
/// server's own folders (<see cref="DebugBrowserProfiles"/>): the user's own browser has neither. It asks the browser to
/// close over its own DevTools endpoint, and only then ends the browser process and what is left of its own child
/// processes (the same .exe, with a --type and the browser's own --user-data-dir, which Chromium passes to its children);
/// a program the browser opened (a download, for example, or the user's own browser started by a link) is never ended.
/// </remarks>
public sealed class DebugBrowserLifecycle : IDebugBrowserLifecycle
{
    private const string PortSwitch = "remote-debugging-port";
    private const string ProfileSwitch = "user-data-dir";
    private const string TypeSwitch = "type";

    /// <summary>The image names of the browsers launch_browser_with_debug starts.</summary>
    private static readonly HashSet<string> BrowserImages = new(StringComparer.OrdinalIgnoreCase) { "chrome.exe", "msedge.exe", "brave.exe" };

    /// <summary>How long a reuseIfRunning check waits for the running browser's /json/version.</summary>
    private static readonly TimeSpan ReuseVersionTimeout = TimeSpan.FromSeconds(2);

    /// <summary>How long Browser.close may take to be sent and answered.</summary>
    private static readonly TimeSpan CloseCommandTimeout = TimeSpan.FromSeconds(3);

    /// <summary>How long the browser, or a child process of it, may take to exit once it has been ended.</summary>
    private static readonly TimeSpan EndedExitTimeout = TimeSpan.FromSeconds(5);

    /// <summary>How long the profile's delete is tried again while the browser's processes release its files.</summary>
    private static readonly TimeSpan ProfileDeleteBudget = TimeSpan.FromSeconds(10);

    /// <summary>How long <see cref="AbandonAsync"/> waits for the browser it ends to exit, and tries its profile's delete.</summary>
    private static readonly TimeSpan AbandonTimeout = TimeSpan.FromSeconds(5);

    private readonly IProcessService _processes;
    private readonly INativeProcessWrapper _native;
    private readonly IDebugPortLocator _ports;
    private readonly ITcpPortOwnerResolver _portOwners;
    private readonly ICdpService _cdp;
    private readonly IWindowService _windows;
    private readonly DebugBrowserProfiles _profiles;
    private readonly ITargetPolicy? _targetPolicy;
    private readonly IHostGuard? _hostGuard;

    /// <summary>The browsers this server started, by PID, with the start time and profile they were started with.</summary>
    private readonly ConcurrentDictionary<int, StartedBrowser> _started = new();

    private sealed record StartedBrowser(int ProcessId, DateTime StartTime, string UserDataDir);

    /// <param name="processes">Starts the browser (launch_browser_with_debug's launch).</param>
    /// <param name="native">Reads process lists, command lines and start times; opens and ends processes.</param>
    /// <param name="ports">Finds a browser's own DevTools port from its command line.</param>
    /// <param name="portOwners">Tells which process listens on a port.</param>
    /// <param name="cdp">Reads /json/version and sends Browser.close.</param>
    /// <param name="windows">Finds a reused browser's window.</param>
    /// <param name="profiles">The server's profile folders.</param>
    /// <param name="targetPolicy">The user's target policy, which close_debug_browser respects; the server always passes it.</param>
    /// <param name="hostGuard">The agent's host, which is never closed; the server always passes it.</param>
    public DebugBrowserLifecycle(
        IProcessService processes,
        INativeProcessWrapper native,
        IDebugPortLocator ports,
        ITcpPortOwnerResolver portOwners,
        ICdpService cdp,
        IWindowService windows,
        DebugBrowserProfiles profiles,
        ITargetPolicy? targetPolicy = null,
        IHostGuard? hostGuard = null)
    {
        _processes = processes;
        _native = native;
        _ports = ports;
        _portOwners = portOwners;
        _cdp = cdp;
        _windows = windows;
        _profiles = profiles;
        _targetPolicy = targetPolicy;
        _hostGuard = hostGuard;
    }

    /// <inheritdoc />
    public async Task<DebugBrowserLaunchResult> LaunchAsync(DebugBrowserLaunchRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (request.ProfileName != null)
        {
            if (request.UserDataDir != null)
                return Refused("Pass profile or userDataDir, not both. Nothing was launched.");

            if (DebugBrowserProfiles.NameRefusal(request.ProfileName) is { } badName)
                return Refused($"{badName} Nothing was launched.");

            var folder = _profiles.NamedPath(request.ProfileName);
            if (PrepareNamedProfile(folder) is { } notReady)
                return notReady;

            request = request with { UserDataDir = folder };
        }

        if (request.ReuseIfRunning)
        {
            if (request.UserDataDir == null)
            {
                return Refused("reuseIfRunning needs profile or userDataDir: a new temporary profile has no running browser. " +
                               "Nothing was launched.");
            }

            if (await FindRunningAsync(request, request.UserDataDir, cancellationToken).ConfigureAwait(false) is { } running)
                return running;
        }
        else if (request.UserDataDir == null)
        {
            // What closed, ended or crashed browsers left; a profile a browser still holds, or one a running browser of this
            // server uses, is kept. A browser of the server's that exited without close_debug_browser (closed by hand,
            // crashed, kill_process) no longer keeps its folder
            ForgetExited();
            _profiles.SweepStaleTemporary(folder => _started.Values.Any(b => DebugBrowserProfiles.SamePath(b.UserDataDir, folder)));
        }

        var result = await _processes.LaunchBrowserWithDebugAsync(request, cancellationToken).ConfigureAwait(false);
        if (result is { ProcessId: int processId, StartTime: DateTime startTime, UserDataDir: { } profile })
            _started[processId] = new StartedBrowser(processId, startTime, DebugBrowserProfiles.Normalize(profile));

        return result;
    }

    /// <inheritdoc />
    public async Task AbandonAsync(DebugBrowserLaunchResult launch)
    {
        ArgumentNullException.ThrowIfNull(launch);
        if (launch is not { Success: true, ProcessId: int pid } || launch.LaunchStatus == LaunchStatus.AlreadyRunning)
            return;

        try
        {
            if (_started.TryGetValue(pid, out var known) && known.StartTime == launch.StartTime)
                _started.TryRemove(pid, out _);

            using (var process = _native.OpenProcess(pid))
            {
                if (process != null && !HasExited(process) && IsLaunchedBrowser(pid, launch))
                {
                    process.Kill(entireProcessTree: true);
                    await process.WaitForExitAsync((int)AbandonTimeout.TotalMilliseconds, CancellationToken.None).ConfigureAwait(false);
                }
            }

            if (launch.UserDataDir is { } profile && _profiles.KindOf(profile) == DebugProfileKind.Temporary)
                await _profiles.DeleteAsync(profile, AbandonTimeout).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception or NotSupportedException or AggregateException
                                       or IOException or UnauthorizedAccessException)
        {
            // It exited meanwhile, or cannot be ended; a temporary profile left is swept by a later launch
        }
    }

    /// <summary>
    /// Whether PID <paramref name="processId"/> is still the browser <paramref name="launch"/> started: the start time read at
    /// its start, or when that was not read, a command line that names its profile.
    /// </summary>
    private bool IsLaunchedBrowser(int processId, DebugBrowserLaunchResult launch)
    {
        if (launch.StartTime != null)
            return _native.GetProcessStartTime(processId) == launch.StartTime;

        return launch.UserDataDir != null &&
               DebugBrowserProfiles.SamePath(ProfileOf(ChromiumCommandLine.Parse(_native.GetProcessCommandLine(processId))), launch.UserDataDir);
    }

    /// <summary>Forgets the browsers this server started whose PID no longer has the start time they were started with.</summary>
    private void ForgetExited()
    {
        foreach (var browser in _started.Values)
        {
            if (_native.GetProcessStartTime(browser.ProcessId) != browser.StartTime)
                _started.TryRemove(new KeyValuePair<int, StartedBrowser>(browser.ProcessId, browser));
        }
    }

    /// <summary>Whether this server started PID <paramref name="processId"/>, with that start time and profile.</summary>
    private bool IsStartedByServer(int processId, DateTime? startTime, string userDataDir) =>
        startTime != null && _started.TryGetValue(processId, out var known) && known.StartTime == startTime &&
        DebugBrowserProfiles.SamePath(known.UserDataDir, userDataDir);

    /// <summary>Creates a named profile's folder; a failure, or a folder that is a link to another folder, is refused.</summary>
    private static DebugBrowserLaunchResult? PrepareNamedProfile(string folder)
    {
        try
        {
            var info = Directory.CreateDirectory(folder);
            if (info.Attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                return Failed($"The profile folder '{folder}' is a link to another folder, so it is not used: the debug port would " +
                              "expose whatever profile it leads to. Nothing was launched.");
            }

            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Failed($"The profile folder '{folder}' could not be created ({ex.Message}). Nothing was launched.");
        }
    }

    /// <summary>
    /// reuseIfRunning: the debug browser running with <paramref name="userDataDir"/>, as an alreadyRunning result; a failure
    /// when one runs but cannot be used; null when none runs (the launch goes on, and refuses a profile a browser without a
    /// debug port holds).
    /// </summary>
    private async Task<DebugBrowserLaunchResult?> FindRunningAsync(
        DebugBrowserLaunchRequest request, string userDataDir, CancellationToken cancellationToken)
    {
        // Chromium holds the lockfile while it runs: no lock, no browser to reuse (and no process list to read)
        if (ChromiumProfileLock.HeldReason(userDataDir) == null)
            return null;

        if (FindMainProcess(userDataDir) is not { } running)
            return null;

        var (processId, imageName, switches) = running;
        var pid = processId.ToString(CultureInfo.InvariantCulture);
        var expected = DebugBrowserLocator.ExecutableName(request.Browser);
        if (!string.Equals(imageName, expected, StringComparison.OrdinalIgnoreCase))
        {
            return Failed($"reuseIfRunning: a {imageName} (processId {pid}) runs with this profile, not {expected}. Nothing was " +
                          $"launched. Pass browser for {imageName} to reuse it.");
        }

        // A port the server finds by itself is the target policy's to refuse (roadmap M5.3)
        var processName = Path.GetFileNameWithoutExtension(imageName);
        if (_targetPolicy?.Evaluate([processName]) is { Allowed: false } verdict)
        {
            return Failed($"reuseIfRunning: the '{processName}' running with this profile is refused by the server's target policy: " +
                          $"{verdict.Reason}. Nothing was launched.") with { ErrorCode = nameof(ProcessErrorCode.TargetDeniedByPolicy) };
        }

        if (!switches.ContainsKey(PortSwitch))
        {
            // Not a debug browser: it may be the user's, so it is not named for closing
            return Failed("reuseIfRunning: a browser runs with this profile without a DevTools port, so there is nothing to reuse " +
                          "and a second start would only hand the request to it. Nothing was launched. It was not started by " +
                          "launch_browser_with_debug; ask the user to close it, or use another profile.");
        }

        // close_debug_browser closes it only when this server started it or its profile is one of the server's folders
        var startTime = _native.GetProcessStartTime(processId);
        var closable = IsStartedByServer(processId, startTime, userDataDir) || _profiles.KindOf(userDataDir) != DebugProfileKind.Custom;
        var advice = closable
            ? " Close it with close_debug_browser and launch again."
            : " This server did not start it, so close_debug_browser does not close it: ask the user to close it, or use another profile.";

        var lookup = _ports.Find(processId);
        if (lookup.Port is not int port)
        {
            return Failed($"reuseIfRunning: the debug browser{(closable ? $" (processId {pid})" : string.Empty)} runs with this " +
                          $"profile, but its DevTools port cannot be used: {lookup.Detail} Nothing was launched.{advice}") with
            {
                ProcessId = closable ? processId : null,
                UserDataDir = userDataDir
            };
        }

        var version = await _cdp.GetBrowserVersionAsync(port, ReuseVersionTimeout, cancellationToken).ConfigureAwait(false);
        if (!version.Success)
        {
            return Failed($"reuseIfRunning: the debug browser{(closable ? $" (processId {pid})" : string.Empty)} runs with this " +
                          $"profile on port {port}, but its DevTools endpoint did not answer ({version.DescribeError()}). Nothing was " +
                          $"launched.{advice}") with
            {
                ProcessId = closable ? processId : null,
                UserDataDir = userDataDir
            };
        }

        string? warning = closable
            ? null
            : "It was not started by this server (or by an earlier run of it), so it may be the user's own: close_debug_browser does not close it.";
        if (!string.IsNullOrWhiteSpace(request.Url))
            warning = Combine(warning, "The browser was already running, so the url was not opened; its windows show what they showed.");

        if (request.Port != DebugBrowserLaunchRequest.EphemeralPort && request.Port != port)
            warning = Combine(warning, $"It listens on port {port}, not on the port asked for ({request.Port}).");

        var window = _windows.FindWindows(new WindowQuery { ProcessId = processId }).FirstOrDefault();
        if (window == null)
            warning = Combine(warning, "No window of it was found on this desktop; find_windows with its processId and includeCloaked=true finds one on another virtual desktop.");

        var info = version.Value!;
        return new DebugBrowserLaunchResult(true, processId, window?.Handle, port, userDataDir, null, null,
            warning, LaunchStatus.AlreadyRunning, CdpReady: true,
            BrowserVersion: info.Browser.Length > 0 ? info.Browser : null,
            CdpEndpoint: info.HttpEndpoint,
            BrowserWsEndpoint: info.WebSocketDebuggerUrl.ToString())
        {
            StartTime = startTime
        };
    }

    /// <summary>
    /// The main process (no --type) of chrome, msedge or brave whose command line names <paramref name="userDataDir"/>,
    /// with its switches; null when none does or the process list cannot be read.
    /// </summary>
    private (int ProcessId, string ImageName, IReadOnlyDictionary<string, string> Switches)? FindMainProcess(string userDataDir)
    {
        foreach (var (processId, entry) in _native.GetProcessList() ?? new Dictionary<int, (int, string)>())
        {
            if (!BrowserImages.Contains(entry.ImageName))
                continue;

            var switches = ChromiumCommandLine.Parse(_native.GetProcessCommandLine(processId));
            if (switches.ContainsKey(TypeSwitch) || ProfileOf(switches) is not { } profile)
                continue;

            if (DebugBrowserProfiles.SamePath(profile, userDataDir))
                return (processId, entry.ImageName, switches);
        }

        return null;
    }

    /// <summary>The --user-data-dir of a command line as a full path, or null when it has none or a relative one.</summary>
    private static string? ProfileOf(IReadOnlyDictionary<string, string> switches)
    {
        if (!switches.TryGetValue(ProfileSwitch, out var profile) || string.IsNullOrWhiteSpace(profile))
            return null;

        var trimmed = profile.Trim();
        if (!Path.IsPathFullyQualified(trimmed))
            return null;

        try
        {
            return DebugBrowserProfiles.Normalize(trimmed);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }

    /// <inheritdoc />
    public DebugBrowserCloseCheck CheckClose(int? processId, int? cdpPort, bool deleteProfile)
    {
        if (processId == null && cdpPort == null)
            return Refuse(null, ToolErrorReporting.InvalidArgumentsErrorCode, "Pass processId or cdpPort. Nothing was closed.");

        int pid;
        if (cdpPort is int port)
        {
            if (!_portOwners.TryGetListeningProcessId(port, out var owner))
            {
                return Refuse(processId, nameof(BrowserErrorCode.NotADebugBrowser),
                    $"Which process listens on {CdpEndpointValidator.Host}:{port} could not be read, so it cannot be shown to be a " +
                    "debug browser. Nothing was closed. Pass the processId launch_browser_with_debug returned.");
            }

            if (owner is not int listener)
            {
                return Refuse(processId, nameof(ProcessErrorCode.ProcessNotFound),
                    $"Nothing listens on {CdpEndpointValidator.Host}:{port}: the browser may have exited already. Nothing was closed.");
            }

            if (processId is int given && given != listener)
            {
                return Refuse(given, nameof(BrowserErrorCode.NotADebugBrowser),
                    $"Port {port} belongs to process {listener}, not to processId {given}. Nothing was closed.");
            }

            pid = listener;
        }
        else
        {
            pid = processId!.Value;
        }

        var shown = pid.ToString(CultureInfo.InvariantCulture);
        var processes = _native.GetProcessList();
        if (processes == null)
        {
            return Refuse(pid, nameof(ProcessErrorCode.ProcessTreeUnreadable),
                "The process list could not be read, so the browser cannot be checked. Nothing was closed. Try again.");
        }

        if (!processes.TryGetValue(pid, out var entry))
            return Refuse(pid, nameof(ProcessErrorCode.ProcessNotFound), $"Process {shown} is not running. Nothing was closed.");

        var imageName = entry.ImageName;
        if (!BrowserImages.Contains(imageName))
        {
            return Refuse(pid, nameof(BrowserErrorCode.NotADebugBrowser),
                $"Process {shown} is {imageName}, not a Chrome, Edge or Brave browser. Nothing was closed.");
        }

        var startTime = _native.GetProcessStartTime(pid);
        var commandLine = _native.GetProcessCommandLine(pid);
        if (commandLine == null)
        {
            return Refuse(pid, nameof(BrowserErrorCode.NotADebugBrowser),
                $"The command line of {imageName} (processId {shown}) could not be read, so it cannot be shown to be a debug " +
                "browser. Nothing was closed.");
        }

        var switches = ChromiumCommandLine.Parse(commandLine);
        if (switches.ContainsKey(TypeSwitch))
        {
            return Refuse(pid, nameof(BrowserErrorCode.NotADebugBrowser),
                $"Process {shown} is a helper process of a browser ({switches[TypeSwitch]}), not the browser. Nothing was closed. " +
                "Pass the processId launch_browser_with_debug returned.");
        }

        if (!switches.ContainsKey(PortSwitch) || ProfileOf(switches) is not { } profile)
        {
            return Refuse(pid, nameof(BrowserErrorCode.NotADebugBrowser),
                $"{imageName} (processId {shown}) was not started with a DevTools port and a profile folder, so it is not a " +
                "debug browser of this server: it may be the user's own browser. Nothing was closed; never close it.");
        }

        var started = _started.TryGetValue(pid, out var known) && known.StartTime == startTime ? known : null;
        if (known != null && started == null)
            _started.TryRemove(pid, out _); // The PID now belongs to a later process

        if (started != null && !DebugBrowserProfiles.SamePath(started.UserDataDir, profile))
        {
            return Refuse(pid, nameof(BrowserErrorCode.NotADebugBrowser),
                $"{imageName} (processId {shown}) no longer runs with the profile this server started it with " +
                $"('{started.UserDataDir}'). Nothing was closed.");
        }

        var kind = _profiles.KindOf(profile);
        if (started == null && kind == DebugProfileKind.Custom)
        {
            return Refuse(pid, nameof(BrowserErrorCode.NotADebugBrowser),
                $"{imageName} (processId {shown}) has a DevTools port, but this server did not start it and its profile " +
                $"'{profile}' is not one of the server's own folders, so it may be the user's browser. Nothing was closed; ask " +
                "the user to close it.");
        }

        var isHost = _hostGuard?.IsHostProcess(pid) == true ? true : IsAncestorOfServer(pid);
        if (isHost == true)
        {
            return Refuse(pid, nameof(ProcessErrorCode.CannotTerminateHost),
                $"{imageName} (processId {shown}) belongs to the agent's host; closing it would end the agent's own session. Nothing was closed.");
        }

        if (isHost == null)
        {
            return Refuse(pid, nameof(ProcessErrorCode.ProcessTreeUnreadable),
                $"The process list could not be read, so the server cannot tell whether {imageName} (processId {shown}) is an " +
                "ancestor of this server (the agent's host); closing it could end the agent's own session. Nothing was closed. Try again.");
        }

        var processName = Path.GetFileNameWithoutExtension(imageName);
        if (_targetPolicy?.Evaluate([processName]) is { Allowed: false } verdict)
        {
            return Refuse(pid, nameof(ProcessErrorCode.TargetDeniedByPolicy),
                $"'{processName}' (processId {shown}) is refused by the server's target policy: {verdict.Reason}. Nothing was closed. " +
                "Ask the user to close it.");
        }

        // The browser's own child processes, to end what is left of them once it exits: the same .exe with a --type and the
        // browser's own --user-data-dir. Another program it opened is not one, nor is the user's own browser that a link
        // started (the same .exe, a descendant, but without that profile), nor one whose command line cannot be read
        var helpers = (_native.GetDescendantsOf(pid) ?? [])
            .Where(d => string.Equals(d.ImageName, imageName, StringComparison.OrdinalIgnoreCase) && _hostGuard?.IsHostProcess(d.ProcessId) != true &&
                        IsChildOf(d.ProcessId, profile))
            .Select(d => (d.ProcessId, _native.GetProcessStartTime(d.ProcessId)))
            .ToList();

        var deletes = kind == DebugProfileKind.Temporary || (kind == DebugProfileKind.Named && deleteProfile);
        var warning = deleteProfile && kind == DebugProfileKind.Custom
            ? $"deleteProfile does not apply to a userDataDir that was passed: '{profile}' is kept."
            : null;
        return new DebugBrowserCloseCheck(
            new DebugBrowserCloseTarget(pid, processName, startTime, profile, kind, started != null, deletes, helpers, warning), null);
    }

    /// <summary>Whether a descendant is a Chromium child process of the browser on <paramref name="profile"/>.</summary>
    private bool IsChildOf(int processId, string profile)
    {
        var switches = ChromiumCommandLine.Parse(_native.GetProcessCommandLine(processId));
        return switches.ContainsKey(TypeSwitch) && ProfileOf(switches) is { } own && DebugBrowserProfiles.SamePath(own, profile);
    }

    /// <inheritdoc />
    public async Task<DebugBrowserCloseResult> CloseAsync(
        DebugBrowserCloseTarget target,
        TimeSpan timeout,
        Func<CancellationToken, Task<IDisposable>> enterGate,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(enterGate);
        var pid = target.ProcessId;
        var shown = pid.ToString(CultureInfo.InvariantCulture);
        var warning = target.Warning;
        bool? graceful = null;
        var forced = false;
        bool exited;

        using (var process = _native.OpenProcess(pid))
        {
            var now = _native.GetProcessStartTime(pid);
            exited = process == null || HasExited(process) || now == null;
            if (!exited && target.StartTime != null && now != target.StartTime)
            {
                // The browser exited while the user was asked, and its PID went to another process: that one is not touched
                exited = true;
            }

            if (!exited && !DebugBrowserProfiles.SamePath(ProfileOf(ChromiumCommandLine.Parse(_native.GetProcessCommandLine(pid))), target.UserDataDir))
            {
                return Result(target, false, nameof(BrowserErrorCode.NotADebugBrowser),
                    $"Process {shown} no longer runs with the profile '{target.UserDataDir}'. Nothing was closed.");
            }

            if (exited)
            {
                warning = Combine(warning, "The browser had already exited.");
            }
            else
            {
                (bool Sent, string? Problem) close;
                using (await enterGate(cancellationToken).ConfigureAwait(false))
                {
                    close = await SendCloseAsync(pid, cancellationToken).ConfigureAwait(false);
                }

                // Browser.close sent, even without an answer (a busy browser may be shutting down): the browser gets the
                // whole wait to exit before it is ended
                exited = close.Sent &&
                         await process!.WaitForExitAsync((int)timeout.TotalMilliseconds, cancellationToken).ConfigureAwait(false);
                graceful = close.Sent && (close.Problem == null || exited);
                if (!exited)
                {
                    IDisposable? gate = null;
                    try
                    {
                        gate = await enterGate(cancellationToken).ConfigureAwait(false);
                    }
                    catch (ToolErrorException ex) when (close.Sent)
                    {
                        // Browser.close went out, so "nothing was done" is not true: report what was done
                        if (!HasExited(process!))
                        {
                            return Result(target, false, ex.ErrorCode,
                                $"Browser.close was sent to {target.ProcessName} (processId {shown}), but it had not exited when the wait " +
                                $"ended, and it was not ended: {GateRefusal(ex.ErrorCode)} Its profile was kept.",
                                graceful, forced: false, exited: false, warning: warning);
                        }
                    }

                    if (gate == null)
                    {
                        // It exited meanwhile, after Browser.close
                        exited = true;
                        graceful = true;
                    }
                    else
                    {
                        using (gate)
                        {
                            // Still the browser the user agreed to close
                            if (_native.GetProcessStartTime(pid) is { } stillRunning && (target.StartTime == null || stillRunning == target.StartTime))
                            {
                                forced = End(process!);
                            }
                        }

                        exited = HasExited(process!) ||
                                 await process!.WaitForExitAsync((int)EndedExitTimeout.TotalMilliseconds, cancellationToken).ConfigureAwait(false);
                        warning = Combine(warning, !close.Sent
                            ? $"It was ended without Browser.close: {close.Problem}"
                            : $"{(close.Problem != null ? close.Problem + " " : string.Empty)}It did not exit within " +
                              $"{timeout.TotalMilliseconds.ToString(CultureInfo.InvariantCulture)} ms of Browser.close, so it was ended.");
                    }
                }
            }
        }

        if (!exited)
        {
            return Result(target, false, nameof(ProcessErrorCode.TerminationException),
                $"{target.ProcessName} (processId {shown}) is still running: it did not close and could not be ended. Its profile was kept.",
                graceful, forced, exited: false, warning: warning);
        }

        _started.TryRemove(pid, out _);
        var helpersEnded = await EndHelpersAsync(target.Helpers, cancellationToken).ConfigureAwait(false);
        if (helpersEnded > 0)
            warning = Combine(warning, $"{helpersEnded} of its own processes still ran after it exited and were ended.");

        bool? deleted = null;
        if (target.DeletesProfile)
        {
            var notDeleted = await _profiles.DeleteAsync(target.UserDataDir, ProfileDeleteBudget, cancellationToken).ConfigureAwait(false);
            deleted = notDeleted == null;
            if (notDeleted != null)
            {
                warning = Combine(warning, $"Its profile could not be deleted ({notDeleted})" +
                    (target.ProfileKind == DebugProfileKind.Temporary ? "; a later launch deletes it." : "."));
            }
        }

        return Result(target, true, null, null, graceful, forced, exited: true, deleted, warning);
    }

    /// <summary>
    /// Sends Browser.close over the browser's own DevTools port: whether it was sent, and why it could not be sent or was
    /// not answered (null when the browser answered).
    /// </summary>
    private async Task<(bool Sent, string? Problem)> SendCloseAsync(int processId, CancellationToken cancellationToken)
    {
        var lookup = _ports.Find(processId);
        if (lookup.Port is not int port)
            return (false, lookup.Detail);

        var closed = await _cdp.CloseBrowserAsync(port, processId, CloseCommandTimeout, cancellationToken).ConfigureAwait(false);
        if (!closed.Success)
            return (false, closed.Error);

        return closed.Value
            ? (true, null)
            : (true, $"Browser.close was sent but not answered within {CloseCommandTimeout.TotalMilliseconds.ToString(CultureInfo.InvariantCulture)} ms.");
    }

    /// <summary>Why the input gate was not taken to end the browser, and what to do, for the PausedByUser and InputBusy codes.</summary>
    private static string GateRefusal(string errorCode) => errorCode == nameof(WindowErrorCode.PausedByUser)
        ? "input is paused (the user pressed the pause hotkey). Stop and ask the user how to continue."
        : "another input or window call held the input gate. Call close_debug_browser again when your other calls have finished.";

    /// <summary>Ends what is left of the browser's own child processes (only those that still have their start time).</summary>
    private async Task<int> EndHelpersAsync(IReadOnlyList<(int ProcessId, DateTime? StartTime)> helpers, CancellationToken cancellationToken)
    {
        var ended = new List<IProcessHandle>();
        try
        {
            foreach (var (processId, startTime) in helpers)
            {
                if (startTime == null || _native.GetProcessStartTime(processId) != startTime)
                    continue;

                var helper = _native.OpenProcess(processId);
                if (helper == null)
                    continue;

                if (HasExited(helper) || !End(helper))
                {
                    helper.Dispose();
                    continue;
                }

                ended.Add(helper);
            }

            await Task.WhenAll(ended.Select(h => h.WaitForExitAsync((int)EndedExitTimeout.TotalMilliseconds, cancellationToken))).ConfigureAwait(false);
            return ended.Count;
        }
        finally
        {
            foreach (var helper in ended)
                helper.Dispose();
        }
    }

    /// <summary>Ends one process (not its tree); false when it could not be ended (it exited meanwhile, or access is denied).</summary>
    private static bool End(IProcessHandle process)
    {
        try
        {
            process.Kill(entireProcessTree: false);
            return true;
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception or NotSupportedException)
        {
            return false;
        }
    }

    private static bool HasExited(IProcessHandle process)
    {
        try
        {
            return process.HasExited;
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception)
        {
            return false;
        }
    }

    /// <summary>
    /// Whether the process is an ancestor of this server, by a live walk of the process tree; null when the process list
    /// cannot be read (review F3): unknown, so the close is refused rather than assumed safe.
    /// </summary>
    private bool? IsAncestorOfServer(int processId)
    {
        try
        {
            return _native.IsAncestorOfCurrentProcess(processId);
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception)
        {
            return null;
        }
    }

    private static DebugBrowserCloseResult Result(
        DebugBrowserCloseTarget target, bool success, string? errorCode, string? error,
        bool? graceful = null, bool forced = false, bool? exited = null, bool? deleted = null, string? warning = null) =>
        new(success, target.ProcessId, error, errorCode, target.UserDataDir, target.ProfileKind.ToWireName(),
            graceful, forced, exited, deleted, warning);

    private static DebugBrowserCloseCheck Refuse(int? processId, string errorCode, string error) =>
        DebugBrowserCloseCheck.Refused(new DebugBrowserCloseResult(false, processId, error, errorCode));

    private static DebugBrowserLaunchResult Refused(string error) =>
        new(false, null, null, null, null, error, ToolErrorReporting.InvalidArgumentsErrorCode);

    private static DebugBrowserLaunchResult Failed(string error) =>
        new(false, null, null, null, null, error, nameof(BrowserErrorCode.DebugLaunchFailed));

    private static string? Combine(string? first, string? second) =>
        first == null ? second : second == null ? first : $"{first} {second}";
}
