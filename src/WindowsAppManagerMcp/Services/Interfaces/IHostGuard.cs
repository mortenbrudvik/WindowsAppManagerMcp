using WindowsAppManagerMcp.Models;

namespace WindowsAppManagerMcp.Services.Interfaces;

/// <summary>
/// Knows which processes and windows host the agent that started this server: its terminal, IDE
/// or chat app. Input that names no target lands in whatever window has focus, which is usually
/// that host. The input path (M1.5) and the close and kill guards (M1.7) are meant to ask this guard
/// before they act on a window or process the agent did not target explicitly.
/// </summary>
/// <remarks>
/// The host is recorded once, when the server starts (<see cref="Host"/>): the server's ancestor
/// processes, the window that shows its console, and the foreground window at that moment.
/// Answers are fail-safe: when a recorded process can no longer be verified it still counts as the
/// host, and only a process ID that was reused by a newer process stops counting.
/// </remarks>
public interface IHostGuard
{
    /// <summary>What was recorded at server start.</summary>
    HostSnapshot Host { get; }

    /// <summary>
    /// True for a process whose end ends or disrupts the agent's session: this server's own process,
    /// its ancestors (as recorded at start), the process that owns the window showing its console (for
    /// example WindowsTerminal.exe, or the console's client process such as cmd.exe under classic
    /// conhost, which makes the client the console window's owner), the console server of its console
    /// (conhost.exe or OpenConsole.exe, see <see cref="HostSnapshot.ConsoleServerProcesses"/>), and the
    /// process that owned the startup foreground window. The last one covers Windows Terminal as the default terminal when
    /// the server has no console window: the terminal is then neither an ancestor nor the console
    /// host, but killing it closes the agent's terminal. Kill and close-process guards use this.
    /// A recorded ID that now belongs to a process started later (a reused ID) is not the host.
    /// </summary>
    /// <remarks>
    /// Fail-safe: when the server starts while an unrelated application is in front, that application
    /// counts as the host process too. <see cref="IsHostWindow"/> is narrower for that process: only the
    /// recorded window and the popups it owns are host windows, not every window of the process.
    /// </remarks>
    bool IsHostProcess(int processId);

    /// <summary>
    /// True when input to the window would reach the agent's host: the window, or its root owner (so
    /// dialogs and popups owned by the host count too), is the console host window or the startup
    /// foreground window, or belongs to the server, an ancestor or the console host's process. The
    /// other windows of the startup foreground window's process are not host windows, so one
    /// application that happened to be in front at startup does not block input to all of its windows.
    /// Windows of the desktop shell (explorer.exe: the taskbar, the desktop and File Explorer) are
    /// excluded from the process rule, because the shell is an ancestor whenever the terminal was
    /// started from the Start menu or a shortcut.
    /// </summary>
    bool IsHostWindow(nint hWnd);
}
