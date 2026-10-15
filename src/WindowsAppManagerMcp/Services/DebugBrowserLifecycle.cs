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
/// processes (the same .exe); a program the browser opened (a download, for example) is never ended.
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
            // What closed, ended or crashed browsers left; a profile a browser still holds, or one this server's browser
            // uses, is kept
            _profiles.SweepStaleTemporary(folder => _started.Values.Any(b => DebugBrowserProfiles.SamePath(b.UserDataDir, folder)));
        }

        var result = await _processes.LaunchBrowserWithDebugAsync(request, cancellationToken).ConfigureAwait(false);
        if (result is { ProcessId: int processId, StartTime: DateTime startTime, UserDataDir: { } profile })
            _started[processId] = new StartedBrowser(processId, startTime, DebugBrowserProfiles.Normalize(profile));

        return result;
    }

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

        if (!switches.ContainsKey(PortSwitch))
        {
            // Not a debug browser: it may be the user's, so it is not named for closing
            return Failed("reuseIfRunning: a browser runs with this profile without a DevTools port, so there is nothing to reuse " +
                          "and a second start would only hand the request to it. Nothing was launched. It was not started by " +
                          "launch_browser_with_debug; ask the user to close it, or use another profile.");
        }

        var lookup = _ports.Find(processId);
        if (lookup.Port is not int port)
        {
            return Failed($"reuseIfRunning: the debug browser (processId {pid}) runs with this profile, but its DevTools port " +
                          $"cannot be used: {lookup.Detail} Nothing was launched. Close it with close_debug_browser and launch again.") with
            {
                ProcessId = processId,
                UserDataDir = userDataDir
            };
        }

        var version = await _cdp.GetBrowserVersionAsync(port, ReuseVersionTimeout, cancellationToken).ConfigureAwait(false);
        if (!version.Success)
        {
            return Failed($"reuseIfRunning: the debug browser (processId {pid}) runs with this profile on port {port}, but its " +
                          $"DevTools endpoint did not answer ({version.DescribeError()}). Nothing was launched. Close it with " +
                          "close_debug_browser and launch again.") with
            {
                ProcessId = processId,
                UserDataDir = userDataDir
            };
        }

        string? warning = null;
        if (!string.IsNullOrWhiteSpace(request.Url))
            warning = $"The browser was already running, so the url was not opened; its windows show what they showed.";

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
            StartTime = _native.GetProcessStartTime(processId)
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

        if (_hostGuard?.IsHostProcess(pid) == true || IsAncestorOfServer(pid))
        {
            return Refuse(pid, nameof(ProcessErrorCode.CannotTerminateHost),
                $"{imageName} (processId {shown}) belongs to the agent's host; closing it would end the agent's own session. Nothing was closed.");
        }

        var processName = Path.GetFileNameWithoutExtension(imageName);
        if (_targetPolicy?.Evaluate([processName]) is { Allowed: false } verdict)
        {
            return Refuse(pid, nameof(ProcessErrorCode.TargetDeniedByPolicy),
                $"'{processName}' (processId {shown}) is refused by the server's target policy: {verdict.Reason}. Nothing was closed. " +
                "Ask the user to close it.");
        }

        // The browser's own child processes, to end what is left of them once it exits; another program it opened is not one
        var helpers = (_native.GetDescendantsOf(pid) ?? [])
            .Where(d => string.Equals(d.ImageName, imageName, StringComparison.OrdinalIgnoreCase) && _hostGuard?.IsHostProcess(d.ProcessId) != true)
            .Select(d => (d.ProcessId, _native.GetProcessStartTime(d.ProcessId)))
            .ToList();

        var deletes = kind == DebugProfileKind.Temporary || (kind == DebugProfileKind.Named && deleteProfile);
        var warning = deleteProfile && kind == DebugProfileKind.Custom
            ? $"deleteProfile does not apply to a userDataDir that was passed: '{profile}' is kept."
            : null;
        return new DebugBrowserCloseCheck(
            new DebugBrowserCloseTarget(pid, processName, startTime, profile, kind, started != null, deletes, helpers, warning), null);
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
                string? notGraceful;
                using (await enterGate(cancellationToken).ConfigureAwait(false))
                {
                    notGraceful = await SendCloseAsync(pid, cancellationToken).ConfigureAwait(false);
                }

                graceful = notGraceful == null;
                exited = graceful == true &&
                         await process!.WaitForExitAsync((int)timeout.TotalMilliseconds, cancellationToken).ConfigureAwait(false);
                if (!exited)
                {
                    using (await enterGate(cancellationToken).ConfigureAwait(false))
                    {
                        // Still the browser the user agreed to close
                        if (_native.GetProcessStartTime(pid) is { } stillRunning && (target.StartTime == null || stillRunning == target.StartTime))
                        {
                            forced = End(process!);
                        }
                    }

                    exited = HasExited(process!) ||
                             await process!.WaitForExitAsync((int)EndedExitTimeout.TotalMilliseconds, cancellationToken).ConfigureAwait(false);
                    warning = Combine(warning, notGraceful != null
                        ? $"It was ended without Browser.close: {notGraceful}"
                        : $"It did not exit within {timeout.TotalMilliseconds.ToString(CultureInfo.InvariantCulture)} ms of Browser.close, so it was ended.");
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

    /// <summary>Sends Browser.close over the browser's own DevTools port; why it could not be sent, or null.</summary>
    private async Task<string?> SendCloseAsync(int processId, CancellationToken cancellationToken)
    {
        var lookup = _ports.Find(processId);
        if (lookup.Port is not int port)
            return lookup.Detail;

        var closed = await _cdp.CloseBrowserAsync(port, processId, CloseCommandTimeout, cancellationToken).ConfigureAwait(false);
        return closed.Success ? null : closed.Error;
    }

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

    private bool IsAncestorOfServer(int processId)
    {
        try
        {
            return _native.IsAncestorOfCurrentProcess(processId);
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception)
        {
            return false;
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
