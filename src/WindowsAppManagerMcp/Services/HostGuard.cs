using Microsoft.Extensions.Logging;
using WindowsAppManagerMcp.Models;
using WindowsAppManagerMcp.Services.Interfaces;

namespace WindowsAppManagerMcp.Services;

/// <summary>
/// Records the agent's host when it is created and answers whether a process or window belongs to
/// it. Created at server start by <see cref="HostGuardStartup"/>, so the foreground window it records
/// is the one the user started the agent from, not whatever is in front at the first tool call.
/// </summary>
/// <remarks>
/// When the ancestor processes cannot be read at start (<see cref="HostSnapshot.AncestorsUnknown"/>, review F3), the host
/// is known only by its console and the window in front then, so the terminal or editor that started the server is not
/// recognized by its process. The guard then reads the ancestors again when a check needs them, at most once per
/// <see cref="AncestorRetryInterval"/> (find_windows checks every window it lists), and records them once a read works,
/// which also ends get_server_info's warning. Until then <see cref="IsHostWindow"/> fails closed where a cheap check per
/// window cannot clear it: a window of a process that may be an ancestor, one not shown to have started after the server,
/// counts as the host's (the desktop shell's excepted, as always), so what the host's windows are refused is refused for it
/// too: input (TargetIsAgentHost), run_steps' run window and the reads of the window in front, closing it without force and
/// placing a launch's window (ProtectedWindow). Window changes by handle are not refused for the host (owner decision D14),
/// so not for these either. A process that started later, such as an app the agent launched, stays usable; a UWP app's frame
/// belongs to ApplicationFrameHost, which normally started before the server, so it counts. Each such refusal says that the
/// window counts only because the ancestors are unknown (<see cref="IsHostWindowOnlyWhileAncestorsUnknown"/>,
/// <see cref="AncestorsUnknownNote"/>), rather than call it the agent's terminal. A transient failure at start costs
/// at most one interval; a list that stays unreadable keeps the older windows refused. <see cref="IsHostProcess"/> does not
/// fail closed that way: its callers (kill_process, close_debug_browser) read the ancestors themselves and refuse with
/// ProcessTreeUnreadable when they cannot, which names the reason. The live check kill_process uses,
/// IsAncestorOfCurrentProcess, reads the same process list as the re-read, so it would tell a window check nothing more.
/// </remarks>
public sealed class HostGuard : IHostGuard
{
    /// <summary>
    /// How often, at most, the ancestors are read again while they are unknown: often enough that a transient failure at start
    /// heals within a second, and seldom enough that a list that stays unreadable costs one failed read a second, however many
    /// windows are checked.
    /// </summary>
    public static readonly TimeSpan AncestorRetryInterval = TimeSpan.FromSeconds(1);

    /// <summary>
    /// The sentence a host refusal adds for a window that counts as the host's only while the ancestors are unknown
    /// (<see cref="IsHostWindowOnlyWhileAncestorsUnknown"/>), so the agent is not told that an app is its terminal when it only
    /// may be. It can reach the user too, in close_window's question about force, so it gives the agent no instruction.
    /// </summary>
    public const string AncestorsUnknownNote =
        " It counts as the host's only because the server cannot read its ancestor processes: until it can, every window of a " +
        "program that started before the server does, so it may be another app's (get_server_info warns about it).";

    /// <summary>
    /// <see cref="AncestorsUnknownNote"/> for a window read while its being the host's rests only on the ancestors being
    /// unknown (<see cref="WindowInfo.IsAgentHostOnlyWhileAncestorsUnknown"/>); empty for any other window.
    /// </summary>
    public static string AncestorsUnknownNoteFor(WindowInfo window) =>
        window is { IsAgentHost: true, IsAgentHostOnlyWhileAncestorsUnknown: true } ? AncestorsUnknownNote : string.Empty;

    /// <summary>
    /// <see cref="AncestorsUnknownNote"/> when one of <paramref name="windows"/> counts as the host's only while the ancestors are
    /// unknown (<see cref="IHostGuard.IsHostWindowOnlyWhileAncestorsUnknown"/>); empty otherwise, also without a guard.
    /// </summary>
    public static string AncestorsUnknownNoteFor(IHostGuard? guard, params nint[] windows) =>
        guard != null && windows.Any(guard.IsHostWindowOnlyWhileAncestorsUnknown) ? AncestorsUnknownNote : string.Empty;

    private readonly INativeWindowWrapper _windows;
    private readonly INativeProcessWrapper _processes;
    private readonly ILogger<HostGuard>? _logger;
    private readonly TimeProvider _time;
    private readonly object _ancestorRead = new();

    /// <summary>
    /// What is recorded, replaced as a whole when the ancestors are read after all, so that a check reads one consistent
    /// record: the snapshot, and the processes besides the server all of whose windows are host windows (the ancestors, the
    /// console host and the console server). <see cref="IsHostProcess"/> also counts the startup foreground window's process.
    /// </summary>
    private volatile HostRecord _record;

    /// <summary>The windows recorded by identity: the console host window and the startup foreground window.</summary>
    private readonly IReadOnlyList<HostWindow> _hostWindows;

    /// <summary>When the ancestors were last read (<see cref="TimeProvider.GetTimestamp"/>), while they are unknown.</summary>
    private long _lastAncestorRead;

    /// <summary>The server's own start time, read once when a window is checked while the ancestors are unknown.</summary>
    private Lazy<DateTime?>? _serverStart;

    public HostGuard(INativeWindowWrapper windows, INativeProcessWrapper processes, ILogger<HostGuard>? logger = null,
        TimeProvider? time = null)
    {
        _windows = windows ?? throw new ArgumentNullException(nameof(windows));
        _processes = processes ?? throw new ArgumentNullException(nameof(processes));
        _logger = logger;
        _time = time ?? TimeProvider.System;

        var host = Record();
        _lastAncestorRead = _time.GetTimestamp();
        _record = new HostRecord(host, HostProcessesOf(host));
        _hostWindows = new[] { host.ConsoleHostWindow, host.StartupForegroundWindow }
            .OfType<HostWindow>()
            .ToList();

        // Information, so the record behind a later refusal shows in the server log (it is enabled for
        // this category, see ServiceRegistration.AddWindowsAppManagerLogLevels)
        _logger?.LogInformation(
            "Agent host recorded: ancestors {Ancestors}, console host window {ConsoleHost} (process {ConsoleHostProcess}), " +
            "console servers {ConsoleServers}, startup foreground window {Foreground} (process {ForegroundProcess}), shell process {ShellProcess}",
            string.Join(", ", host.Ancestors.Select(a => a.ProcessId)),
            host.ConsoleHostWindow?.Handle,
            host.ConsoleHostWindow?.Process.ProcessId,
            string.Join(", ", host.ConsoleServerProcesses.Select(p => p.ProcessId)),
            host.StartupForegroundWindow?.Handle,
            host.StartupForegroundWindow?.Process.ProcessId,
            host.ShellProcessId);
    }

    /// <summary>
    /// What is recorded. While the ancestors are unknown, reading it tries to read them again (at most once per
    /// <see cref="AncestorRetryInterval"/>), so get_server_info warns only while they still cannot be read.
    /// </summary>
    public HostSnapshot Host => Current().Host;

    public bool IsHostProcess(int processId)
    {
        var record = Current();
        if (IsSessionProcess(record, processId))
        {
            return true;
        }

        // Ending the process that owned the startup foreground window closes that window, which is
        // where a delegated terminal shows the agent
        return processId > 0 &&
               record.Host.StartupForegroundWindow is { } foreground &&
               foreground.Process.ProcessId == processId &&
               IsStillRunningAs(foreground.Process);
    }

    /// <summary>
    /// The server, a recorded ancestor or the console host's process: every window of these reaches
    /// the agent's session.
    /// </summary>
    private bool IsSessionProcess(HostRecord record, int processId)
    {
        if (processId <= 0)
        {
            return false;
        }

        if (processId == record.Host.ServerProcessId)
        {
            return true;
        }

        return record.HostProcesses.Any(host => host.ProcessId == processId && IsStillRunningAs(host));
    }

    public bool IsHostWindow(nint hWnd) => Match(hWnd) != HostWindowMatch.None;

    // Known ancestors: no window is the host's only for want of them, and the owner chain need not be read again
    public bool IsHostWindowOnlyWhileAncestorsUnknown(nint hWnd) =>
        Current().Host.AncestorsUnknown && Match(hWnd) == HostWindowMatch.MayBeAncestor;

    /// <summary>Why a window is the host's, as <see cref="Match"/> tells it.</summary>
    private enum HostWindowMatch
    {
        /// <summary>It is not.</summary>
        None,

        /// <summary>A recorded window, or a window of the server, a known ancestor or the console host.</summary>
        Known,

        /// <summary>Only because the ancestors are unknown: its process is not shown to have started after the server.</summary>
        MayBeAncestor
    }

    private HostWindowMatch Match(nint hWnd)
    {
        if (hWnd == nint.Zero)
        {
            return HostWindowMatch.None;
        }

        var record = Current();

        // The window, its top-level window and every window that owns it (GW_OWNER as well as
        // GA_ROOTOWNER, so WPF and WinForms dialogs of a host window count too)
        var candidates = WindowOwnerChain.SelfAndOwners(_windows, hWnd);

        // Recorded windows by identity, including popups and dialogs they own
        if (candidates.Any(IsRecordedHostWindow))
        {
            return HostWindowMatch.Known;
        }

        // Windows of the server, its ancestors and the console host, except the desktop shell's. The
        // startup foreground window's process is left out here: only its recorded window counts
        var processIds = candidates
            .Select(candidate => (int)_windows.GetWindowThreadProcessId(candidate))
            .Where(processId => processId != 0 && processId != record.Host.ShellProcessId)
            .ToList();
        if (processIds.Any(processId => IsSessionProcess(record, processId)))
        {
            return HostWindowMatch.Known;
        }

        // While the ancestors are unknown, a process that may be one counts too (fail closed, see the remarks)
        return record.Host.AncestorsUnknown && processIds.Any(MayBeAncestor) ? HostWindowMatch.MayBeAncestor : HostWindowMatch.None;
    }

    /// <summary>
    /// While the ancestors are unknown: false only for a process shown to have started after the server, which no ancestor
    /// can have (each parent starts before its child, and a reused ancestor PID belongs to a later process). A start time that
    /// cannot be read, the server's or the process's, leaves it a possible ancestor.
    /// </summary>
    private bool MayBeAncestor(int processId)
    {
        var server = LazyInitializer.EnsureInitialized(ref _serverStart,
            () => new Lazy<DateTime?>(() => Read("server start time", () => _processes.GetProcessStartTime(Environment.ProcessId), null)));
        return server.Value is not { } serverStart ||
               Read("process start time", () => _processes.GetProcessStartTime(processId), null) is not { } started ||
               started <= serverStart;
    }

    /// <summary>
    /// The record, after reading the ancestors again when they are unknown and <see cref="AncestorRetryInterval"/> has passed
    /// since the last read. A read that works replaces the record (and is logged); one that fails leaves it unknown.
    /// </summary>
    private HostRecord Current()
    {
        var record = _record;
        if (!record.Host.AncestorsUnknown)
        {
            return record;
        }

        lock (_ancestorRead)
        {
            record = _record;
            if (!record.Host.AncestorsUnknown || _time.GetElapsedTime(_lastAncestorRead) < AncestorRetryInterval)
            {
                return record;
            }

            _lastAncestorRead = _time.GetTimestamp();
            if (ReadAncestorsAgain() is not { } ancestors)
            {
                return record;
            }

            var host = record.Host with { Ancestors = ancestors, AncestorsUnknown = false };
            _record = new HostRecord(host, HostProcessesOf(host));
            _logger?.LogInformation(
                "Agent host guard read the ancestor processes after all: {Ancestors}. The terminal or editor that started the " +
                "server is now recognized by its process", string.Join(", ", ancestors.Select(a => a.ProcessId)));
            return _record;
        }
    }

    /// <summary>
    /// The ancestors, or null when the process list still cannot be read. The failure was warned about at start, so another
    /// is only a debug line.
    /// </summary>
    private IReadOnlyList<HostProcess>? ReadAncestorsAgain()
    {
        try
        {
            return _processes.GetAncestorProcesses()?.Select(a => new HostProcess(a.ProcessId, a.StartTime)).ToList();
        }
        catch (Exception ex)
        {
            _logger?.LogDebug(ex, "Agent host guard still could not read the ancestor processes");
            return null;
        }
    }

    private static IReadOnlyList<HostProcess> HostProcessesOf(HostSnapshot host) =>
        host.Ancestors
            .Concat(host.ConsoleHostWindow is { } consoleHost ? [consoleHost.Process] : [])
            .Concat(host.ConsoleServerProcesses)
            .ToList();

    /// <summary>A snapshot and the processes whose windows are all host windows, as one value.</summary>
    private sealed record HostRecord(HostSnapshot Host, IReadOnlyList<HostProcess> HostProcesses);

    private bool IsRecordedHostWindow(nint hWnd) =>
        _hostWindows.Any(host =>
            host.Handle == hWnd &&
            (int)_windows.GetWindowThreadProcessId(hWnd) == host.Process.ProcessId &&
            IsStillRunningAs(host.Process));

    /// <summary>
    /// False only when the process ID now belongs to a process that started at a different time (a
    /// reused ID). When either start time is unknown the recorded process is assumed: fail safe.
    /// </summary>
    private bool IsStillRunningAs(HostProcess host)
    {
        if (host.StartTime is not { } recorded)
        {
            return true;
        }

        return _processes.GetProcessStartTime(host.ProcessId) is not { } current || current == recorded;
    }

    private HostSnapshot Record()
    {
        // Null when they could not be read (review F3): the wrapper answers null when its process list cannot be read, and
        // Read gives null (and logs) when it throws. Either way they are recorded as unknown, not as "no ancestors"
        var unreadable = false;
        var ancestors = Read(
            "ancestor processes",
            () =>
            {
                var read = _processes.GetAncestorProcesses();
                unreadable = read == null;
                return read?.Select(a => new HostProcess(a.ProcessId, a.StartTime)).ToList();
            },
            null);
        if (unreadable)
        {
            _logger?.LogWarning(
                "Agent host guard could not read the ancestor processes (the process list could not be read), so the terminal or " +
                "editor that started the server is not known by its process. It reads them again when a check needs them, at " +
                "most once a second; until it can, every window of a process that started before the server, except the desktop " +
                "shell's, counts as the host's. kill_process and close_debug_browser read the ancestors themselves and refuse " +
                "when they cannot.");
        }

        var shellWindow = Read("shell window", _windows.GetShellWindow, nint.Zero);
        var shellProcessId = shellWindow == nint.Zero
            ? 0
            : Read("shell process", () => (int)_windows.GetWindowThreadProcessId(shellWindow), 0);

        var consoleWindow = Read("console window", _processes.GetConsoleWindow, nint.Zero);
        var consoleHostWindow = consoleWindow == nint.Zero ? null : Read("console host window", () => RecordRootOwner(consoleWindow), null);
        IReadOnlyList<HostProcess> consoleServers = consoleWindow == nint.Zero
            ? []
            : Read("console server processes", () => RecordConsoleServers(consoleWindow, consoleHostWindow, shellProcessId), []);

        var foreground = Read("foreground window", _windows.GetForegroundWindow, nint.Zero);
        var startupForeground = foreground == nint.Zero ? null : Read("foreground window owner", () => RecordRootOwner(foreground), null);
        if (startupForeground != null && startupForeground.Process.ProcessId == shellProcessId)
        {
            // The desktop, the taskbar or a folder: not where an agent runs
            startupForeground = null;
        }

        return new HostSnapshot(
            ServerProcessId: Environment.ProcessId,
            Ancestors: ancestors ?? [],
            ConsoleWindow: consoleWindow,
            ConsoleHostWindow: consoleHostWindow,
            StartupForegroundWindow: startupForeground,
            ShellProcessId: shellProcessId == 0 ? null : shellProcessId)
        {
            ConsoleServerProcesses = consoleServers,
            AncestorsUnknown = ancestors == null
        };
    }

    /// <summary>
    /// The console server of the server's console: the process that owns the console window, and every
    /// conhost.exe or OpenConsole.exe that the process showing the console (the terminal) started. Windows can
    /// report a console window as owned by its first client rather than by the console server; the terminal
    /// starts the console server of each of its consoles (ConPTY), so the second rule finds it either way. The
    /// desktop shell is left out: it starts the console servers of every console the user opens.
    /// </summary>
    private IReadOnlyList<HostProcess> RecordConsoleServers(nint consoleWindow, HostWindow? consoleHostWindow, int shellProcessId)
    {
        var processIds = new List<int>();
        var owner = (int)_windows.GetWindowThreadProcessId(consoleWindow);
        if (owner != 0)
        {
            processIds.Add(owner);
        }

        if (consoleHostWindow is { } host && host.Process.ProcessId != shellProcessId)
        {
            processIds.AddRange((_processes.GetDescendantsOf(host.Process.ProcessId) ?? [])
                .Where(process => IsConsoleServerImage(process.ImageName))
                .Select(process => process.ProcessId));
        }

        return processIds
            .Distinct()
            .Select(processId => new HostProcess(processId, _processes.GetProcessStartTime(processId)))
            .ToList();
    }

    private static bool IsConsoleServerImage(string imageName) =>
        TargetPolicySettings.NormalizeProcessName(imageName) is var name &&
        (string.Equals(name, "conhost", StringComparison.OrdinalIgnoreCase) ||
         string.Equals(name, "OpenConsole", StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Records the window at the top of a window's owner chain (<see cref="WindowOwnerChain.TopOwner"/>)
    /// with its process, or null when the window is gone. An owned dialog in front at startup, Win32 or
    /// WPF, is recorded as the application window that owns it.
    /// </summary>
    private HostWindow? RecordRootOwner(nint hWnd)
    {
        var rootOwner = WindowOwnerChain.TopOwner(_windows, hWnd);
        if (rootOwner == nint.Zero)
        {
            rootOwner = hWnd;
        }

        var processId = (int)_windows.GetWindowThreadProcessId(rootOwner);
        return processId == 0
            ? null
            : new HostWindow(rootOwner, new HostProcess(processId, _processes.GetProcessStartTime(processId)));
    }

    /// <summary>
    /// Runs one read of the recording. A failed read must not stop the server from starting, so it
    /// is logged and the rest is still recorded.
    /// </summary>
    private T Read<T>(string what, Func<T> read, T fallback)
    {
        try
        {
            return read();
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Agent host guard could not read the {What}; it protects less of the host", what);
            return fallback;
        }
    }
}
