using Microsoft.Extensions.Logging;
using WindowsAppManagerMcp.Models;
using WindowsAppManagerMcp.Services.Interfaces;

namespace WindowsAppManagerMcp.Services;

/// <summary>
/// Records the agent's host when it is created and answers whether a process or window belongs to
/// it. Created at server start by <see cref="HostGuardStartup"/>, so the foreground window it records
/// is the one the user started the agent from, not whatever is in front at the first tool call.
/// </summary>
public sealed class HostGuard : IHostGuard
{
    private readonly INativeWindowWrapper _windows;
    private readonly INativeProcessWrapper _processes;
    private readonly ILogger<HostGuard>? _logger;

    /// <summary>
    /// The processes, besides the server, all of whose windows are host windows: the ancestors and the
    /// console host. <see cref="IsHostProcess"/> also counts the startup foreground window's process.
    /// </summary>
    private readonly IReadOnlyList<HostProcess> _hostProcesses;

    /// <summary>The windows recorded by identity: the console host window and the startup foreground window.</summary>
    private readonly IReadOnlyList<HostWindow> _hostWindows;

    public HostGuard(INativeWindowWrapper windows, INativeProcessWrapper processes, ILogger<HostGuard>? logger = null)
    {
        _windows = windows ?? throw new ArgumentNullException(nameof(windows));
        _processes = processes ?? throw new ArgumentNullException(nameof(processes));
        _logger = logger;

        Host = Record();
        _hostProcesses = Host.ConsoleHostWindow is { } consoleHost
            ? [.. Host.Ancestors, consoleHost.Process]
            : Host.Ancestors;
        _hostWindows = new[] { Host.ConsoleHostWindow, Host.StartupForegroundWindow }
            .OfType<HostWindow>()
            .ToList();

        // Information, so the record behind a later refusal shows in the server log (it is enabled for
        // this category, see ServiceRegistration.AddWindowsAppManagerLogLevels)
        _logger?.LogInformation(
            "Agent host recorded: ancestors {Ancestors}, console host window {ConsoleHost} (process {ConsoleHostProcess}), " +
            "startup foreground window {Foreground} (process {ForegroundProcess}), shell process {ShellProcess}",
            string.Join(", ", Host.Ancestors.Select(a => a.ProcessId)),
            Host.ConsoleHostWindow?.Handle,
            Host.ConsoleHostWindow?.Process.ProcessId,
            Host.StartupForegroundWindow?.Handle,
            Host.StartupForegroundWindow?.Process.ProcessId,
            Host.ShellProcessId);
    }

    public HostSnapshot Host { get; }

    public bool IsHostProcess(int processId)
    {
        if (IsSessionProcess(processId))
        {
            return true;
        }

        // Ending the process that owned the startup foreground window closes that window, which is
        // where a delegated terminal shows the agent
        return processId > 0 &&
               Host.StartupForegroundWindow is { } foreground &&
               foreground.Process.ProcessId == processId &&
               IsStillRunningAs(foreground.Process);
    }

    /// <summary>
    /// The server, a recorded ancestor or the console host's process: every window of these reaches
    /// the agent's session.
    /// </summary>
    private bool IsSessionProcess(int processId)
    {
        if (processId <= 0)
        {
            return false;
        }

        if (processId == Host.ServerProcessId)
        {
            return true;
        }

        return _hostProcesses.Any(host => host.ProcessId == processId && IsStillRunningAs(host));
    }

    public bool IsHostWindow(nint hWnd)
    {
        if (hWnd == nint.Zero)
        {
            return false;
        }

        // The window, its top-level window and every window that owns it (GW_OWNER as well as
        // GA_ROOTOWNER, so WPF and WinForms dialogs of a host window count too)
        var candidates = WindowOwnerChain.SelfAndOwners(_windows, hWnd);

        // Recorded windows by identity, including popups and dialogs they own
        if (candidates.Any(IsRecordedHostWindow))
        {
            return true;
        }

        // Windows of the server, its ancestors and the console host, except the desktop shell's. The
        // startup foreground window's process is left out here: only its recorded window counts.
        return candidates.Any(candidate =>
        {
            var processId = (int)_windows.GetWindowThreadProcessId(candidate);
            return processId != 0 && processId != Host.ShellProcessId && IsSessionProcess(processId);
        });
    }

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
        var ancestors = Read(
            "ancestor processes",
            () => _processes.GetAncestorProcesses().Select(a => new HostProcess(a.ProcessId, a.StartTime)).ToList(),
            []);

        var shellWindow = Read("shell window", _windows.GetShellWindow, nint.Zero);
        var shellProcessId = shellWindow == nint.Zero
            ? 0
            : Read("shell process", () => (int)_windows.GetWindowThreadProcessId(shellWindow), 0);

        var consoleWindow = Read("console window", _processes.GetConsoleWindow, nint.Zero);
        var consoleHostWindow = consoleWindow == nint.Zero ? null : Read("console host window", () => RecordRootOwner(consoleWindow), null);

        var foreground = Read("foreground window", _windows.GetForegroundWindow, nint.Zero);
        var startupForeground = foreground == nint.Zero ? null : Read("foreground window owner", () => RecordRootOwner(foreground), null);
        if (startupForeground != null && startupForeground.Process.ProcessId == shellProcessId)
        {
            // The desktop, the taskbar or a folder: not where an agent runs
            startupForeground = null;
        }

        return new HostSnapshot(
            ServerProcessId: Environment.ProcessId,
            Ancestors: ancestors,
            ConsoleWindow: consoleWindow,
            ConsoleHostWindow: consoleHostWindow,
            StartupForegroundWindow: startupForeground,
            ShellProcessId: shellProcessId == 0 ? null : shellProcessId);
    }

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
