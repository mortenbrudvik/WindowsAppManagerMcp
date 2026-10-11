using WindowsAppManagerMcp.Models;

namespace WindowsAppManagerMcp.Services.Interfaces;

public interface IWindowService
{
    /// <summary>Lists the visible user-facing app windows (see <see cref="IsUserFacingAppWindow"/>), in z-order.</summary>
    IReadOnlyList<WindowInfo> GetAllWindows(bool includeMinimized = true);

    /// <summary>
    /// Shorthand for <see cref="FindWindows(WindowQuery)"/> with these filters. A handle skips the
    /// user-facing filter.
    /// </summary>
    IReadOnlyList<WindowInfo> FindWindows(
        string? titleContains = null,
        string? processName = null,
        int? processId = null,
        nint? handle = null,
        bool visibleOnly = true);

    /// <summary>
    /// Finds the windows that match every filter of <paramref name="query"/>, in z-order. Lists only
    /// user-facing app windows unless the query names a handle or class name or sets IncludeShell.
    /// </summary>
    IReadOnlyList<WindowInfo> FindWindows(WindowQuery query);

    /// <summary>
    /// The foreground window, read without any listing filter: it can be untitled (a menu, a popup,
    /// the taskbar). Null only when no window is in the foreground.
    /// </summary>
    WindowInfo? GetForegroundWindow();

    /// <summary>
    /// Reads any window by handle, without the listing filter: untitled, cloaked, tool and shell
    /// windows are described too, with their ClassName. Null when the handle is not a window.
    /// </summary>
    WindowInfo? GetWindowInfo(nint handle);

    /// <summary>
    /// The listing predicate: the window is a top-level window (not a child control), visible, not
    /// cloaked, not a tool window (unless it also has WS_EX_APPWINDOW), not the desktop or a taskbar,
    /// has a title, and does not belong to the server itself. Owned windows such as dialogs count as
    /// user-facing. It answers for one window exactly what the default listings decide for it.
    /// </summary>
    bool IsUserFacingAppWindow(nint handle);

    // The window changes (roadmap M2.2). Each refuses an invalid handle (InvalidHandle) and a window the target policy
    // denies (TargetDeniedByPolicy) before anything is sent, and reports a refused SetWindowPos with its Win32 error
    // (5: AccessDenied). A window that does not answer throws WindowNotRespondingException (IWindowCallRunner).

    WindowChangeResult MoveWindow(nint handle, int x, int y, bool bringToFront = true);

    WindowChangeResult ResizeWindow(nint handle, int width, int height);

    WindowChangeResult SetWindowBounds(nint handle, int x, int y, int width, int height, bool bringToFront = true);

    /// <summary>
    /// Minimizes, maximizes or restores the window, and succeeds only when the window is seen in that state within
    /// WindowService.StateChangeTimeout (StateChangeFailed otherwise). Restore is the Windows Restore command: a minimized
    /// window returns to the state it had before (normal or maximized), any other window becomes normal.
    /// </summary>
    WindowChangeResult SetWindowState(nint handle, WindowStateRequest state);

    /// <summary>
    /// Snaps the window on monitor <paramref name="monitorIndex"/>, or on its own monitor when it is null. An index that
    /// is not a monitor fails with InvalidMonitorIndex and the valid range.
    /// </summary>
    WindowChangeResult SnapWindow(nint handle, SnapPosition position, int? monitorIndex = null, bool bringToFront = true);

    /// <summary>
    /// Moves the window to monitor <paramref name="monitorIndex"/> (InvalidMonitorIndex with the valid range when it is not
    /// one). Maximize moves the window there first, then maximizes it, and succeeds only when it is maximized there.
    /// </summary>
    WindowChangeResult MoveWindowToMonitor(nint handle, int monitorIndex, MonitorPositioning positioning = MonitorPositioning.Center, bool bringToFront = true);

    bool IsValidWindow(nint handle);

    /// <summary>
    /// The server's target policy's verdict on the window (roadmap M1.12): it is matched by its process, and a
    /// UWP frame window also by the app it hosts, as the listings report them. The Window Control tools,
    /// close_window and window activation refuse a window it denies. Allowed when the server has no policy.
    /// </summary>
    TargetPolicyVerdict EvaluateTargetPolicy(nint handle);

    /// <summary>
    /// Asks the window to close by posting WM_CLOSE; the application can still prompt to save or refuse.
    /// Unless <paramref name="force"/> is true, nothing is posted to a protected window
    /// (<see cref="WindowErrorCode.ProtectedWindow"/>): the desktop shell (the window, the top-level window
    /// it is part of, or its root owner is the shell window, or a Progman, WorkerW, Shell_TrayWnd or
    /// Shell_SecondaryTrayWnd window of the shell's process; a visible top-level dialog is checked without
    /// its owners, since WM_CLOSE only cancels a dialog) and the agent's host
    /// (<see cref="IHostGuard.IsHostWindow"/>). A refused message is
    /// reported with its Win32 error: 5 is <see cref="WindowErrorCode.AccessDenied"/>.
    /// </summary>
    WindowCloseResult CloseWindow(nint handle, bool force);

    /// <summary>
    /// What <see cref="CloseWindow"/> would refuse, without sending anything: InvalidHandle, TargetDeniedByPolicy (force
    /// does not override it) or, unless <paramref name="force"/>, ProtectedWindow. Null when the close can go ahead.
    /// </summary>
    WindowCloseResult? CheckClose(nint handle, bool force);

    // Verified close (roadmap M2.5). These only read windows.

    /// <summary>
    /// The windows that <paramref name="handle"/> owns, directly or through other owned windows (GW_OWNER), in z-order:
    /// its dialogs, popups and palettes. Hidden ones (IME and other helper windows) only with
    /// <paramref name="includeHidden"/>. The enabled popup of a disabled owner is marked blocking. Empty when the handle
    /// is not a window.
    /// </summary>
    IReadOnlyList<OwnedWindowInfo> GetOwnedWindows(nint handle, bool includeHidden = false);

    /// <summary>
    /// The prompt that keeps <paramref name="handle"/> from closing, found through Win32 alone: when the window (or the
    /// owner of the dialog) is disabled, its enabled popup (GW_ENABLEDPOPUP) or a visible dialog box (#32770) it owns,
    /// with the dialog's Static text and Button captions. A non-modal dialog of an enabled window (Find, a palette) is no
    /// prompt. Null when there is none or the handle is not a window. Prompts drawn inside the window (a WinUI
    /// ContentDialog) need UI Automation (<see cref="IWindowCloseVerifier"/>).
    /// </summary>
    ClosePrompt? FindBlockingDialog(nint handle);

    /// <summary>
    /// The application windows the user can switch to (the Alt-Tab set), in z-order: visible, not cloaked, top-level,
    /// unowned (or WS_EX_APPWINDOW), not tool windows, not the desktop shell, not text-input or GDI+ helper windows (IME,
    /// MSCTFIME UI, GDI+ Hook Window Class) and not the server's own. A UWP frame is reported under the app it hosts.
    /// close_application closes exactly these windows of its target processes.
    /// </summary>
    IReadOnlyList<WindowInfo> GetApplicationWindows();

    // Batch operations. Each changes one window after another, with delayBetweenMs (clamped to 0-5000) between two
    // windows; the cancellation token ends the batch between windows and during that delay: the windows already
    // changed stay changed, and the call throws OperationCanceledException. Each window gets its own result, in order:
    // a window that fails, or does not answer (NotResponding), does not stop the batch.
    Task<IReadOnlyList<WindowChangeResult>> SetWindowBoundsBatchAsync(
        IReadOnlyList<(nint Handle, int X, int Y, int Width, int Height, bool BringToFront)> placements,
        int delayBetweenMs = 50,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<WindowChangeResult>> SnapWindowsBatchAsync(
        IReadOnlyList<(nint Handle, SnapPosition Position, int? MonitorIndex, bool BringToFront)> placements,
        int delayBetweenMs = 50,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<WindowChangeResult>> SetWindowStateBatchAsync(
        IReadOnlyList<(nint Handle, WindowStateRequest State)> changes,
        int delayBetweenMs = 30,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// <see cref="CloseWindow"/> for each window, in order, with the same <paramref name="force"/>. A
    /// refused or failed window does not stop the batch; each result says what happened to its window.
    /// </summary>
    Task<IReadOnlyList<WindowCloseResult>> CloseWindowsBatchAsync(
        IReadOnlyList<nint> handles,
        bool force,
        int delayBetweenMs = 50,
        CancellationToken cancellationToken = default);
}
