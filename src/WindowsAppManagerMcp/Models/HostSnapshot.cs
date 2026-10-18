namespace WindowsAppManagerMcp.Models;

/// <summary>
/// What <c>IHostGuard</c> recorded at server start about the agent's host: the processes and
/// windows that keyboard and mouse input must not reach unless they are targeted explicitly.
/// </summary>
/// <param name="ServerProcessId">This server's own process.</param>
/// <param name="Ancestors">
/// The server's ancestor processes, parent first (for example the agent, the shell, the terminal and
/// whatever started it). The walk stops at a parent that started after its child, since that PID was reused.
/// Empty, with <see cref="AncestorsUnknown"/>, while they could not be read; read on a later check, they are what that read
/// found (an ancestor that has exited by then, and those above it, are not among them).
/// </param>
/// <param name="ConsoleWindow">GetConsoleWindow() of the server, or zero when it has no console window.</param>
/// <param name="ConsoleHostWindow">
/// The root owner of <see cref="ConsoleWindow"/>: the window that shows the server's console. Under
/// Windows Terminal this is the terminal window (the console window is a hidden pseudo-window it
/// owns), under conhost the console window itself. Null when there is no console window.
/// </param>
/// <param name="StartupForegroundWindow">
/// The root owner of the foreground window when the server started; usually the window the user
/// just typed the agent's prompt in. It covers hosts that are not ancestors, such as Windows
/// Terminal when it is the default terminal and took over a console started elsewhere. Null when
/// nothing was in the foreground or it belonged to the desktop shell.
/// </param>
/// <param name="ShellProcessId">
/// The desktop shell's process (the owner of GetShellWindow(), normally explorer.exe), or null when
/// no shell is running.
/// </param>
public sealed record HostSnapshot(
    int ServerProcessId,
    IReadOnlyList<HostProcess> Ancestors,
    nint ConsoleWindow,
    HostWindow? ConsoleHostWindow,
    HostWindow? StartupForegroundWindow,
    int? ShellProcessId)
{
    /// <summary>
    /// The console server of the server's console (conhost.exe, or OpenConsole.exe under Windows Terminal and
    /// other ConPTY hosts): the process that owns <see cref="ConsoleWindow"/>, and the console servers that the
    /// process of <see cref="ConsoleHostWindow"/> started (Windows can report a console window as owned by its
    /// first client instead). Under a pseudo-console the console server is neither an ancestor nor the
    /// terminal, but ending it ends the console the agent's shell runs in. Empty when there is no console window.
    /// </summary>
    public IReadOnlyList<HostProcess> ConsoleServerProcesses { get; init; } = [];

    /// <summary>
    /// True while the server's ancestors could not be read (the process list could not be read, review F3): at start, and
    /// until the host guard reads them on a later check (it tries again at most once a second, and then records them and
    /// clears this). <see cref="Ancestors"/> is then empty because they are unknown, not because there are none, so the host
    /// guard counts every window of a process that started before the server as the host's (fail closed). get_server_info
    /// warns about it; kill_process and close_debug_browser read the ancestors themselves and refuse when they cannot.
    /// </summary>
    public bool AncestorsUnknown { get; init; }
}

/// <summary>
/// A process recorded by the host guard. The start time tells it apart from a later process that
/// reuses the same ID; null when it could not be read.
/// </summary>
public sealed record HostProcess(int ProcessId, DateTime? StartTime);

/// <summary>
/// A top-level window recorded by the host guard, with the process that owned it when it was recorded.
/// </summary>
public sealed record HostWindow(nint Handle, HostProcess Process);
