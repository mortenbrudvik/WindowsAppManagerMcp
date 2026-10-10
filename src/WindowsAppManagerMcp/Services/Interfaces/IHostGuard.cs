using WindowsAppManagerMcp.Models;

namespace WindowsAppManagerMcp.Services.Interfaces;

/// <summary>
/// Knows which processes and windows host the agent that started this server: its terminal, IDE
/// or chat app. Input that names no target lands in whatever window has focus, which is usually
/// that host; the input, close and kill paths ask this guard before they act on a window or
/// process the agent did not target explicitly.
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
    /// True for this server's own process, its ancestors (as recorded at start), and the process that
    /// owns the window showing its console (for example WindowsTerminal.exe or conhost.exe). Ending
    /// any of them ends or disrupts the agent's session. A recorded ID that now belongs to a process
    /// started later (a reused ID) is not the host.
    /// </summary>
    bool IsHostProcess(int processId);

    /// <summary>
    /// True when input to the window would reach the agent's host: the window, or its root owner (so
    /// dialogs and popups owned by the host count too), is the console host window or the startup
    /// foreground window, or belongs to a host process. Windows of the desktop shell (explorer.exe:
    /// the taskbar, the desktop and File Explorer) are excluded from the process rule, because the shell
    /// is an ancestor whenever the terminal was started from the Start menu or a shortcut.
    /// </summary>
    bool IsHostWindow(nint hWnd);
}
