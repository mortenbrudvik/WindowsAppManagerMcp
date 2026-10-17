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
    /// Finds the windows that match every filter of <paramref name="query"/>, in its sort order (z-order by default), at
    /// most its limit. Lists only user-facing app windows unless the query names a handle or class name or sets
    /// IncludeShell.
    /// </summary>
    /// <exception cref="ArgumentException">The query's titleRegex is not a valid regular expression, or its limit is below 1.</exception>
    /// <exception cref="System.Text.RegularExpressions.RegexMatchTimeoutException">The titleRegex took longer than 100 ms on a title.</exception>
    IReadOnlyList<WindowInfo> FindWindows(WindowQuery query);

    /// <summary>
    /// Resolves a window selector to exactly one window (roadmap M2.4), never silently to the first of several. A
    /// selector with a handle reads that window, unfiltered, and refuses it with HandleMismatch when it no longer matches
    /// the selector's other filters (InvalidHandle when it is no window); one with foreground does the same for the window
    /// in front (WindowNotFound when it does not match). Otherwise the filters run as <see cref="FindWindows(WindowQuery)"/>
    /// (visible windows, the user-facing filter unless a class name is given): one match is found, none is
    /// WindowNotFound, several are AmbiguousWindow with up to 5 candidates in z-order, unless the selector's index picks one
    /// (WindowNotFound when it is past the matches). A selector that names nothing, or has an invalid value or titleRegex,
    /// is InvalidArguments. Only reads; never sends a message to a window.
    /// </summary>
    WindowResolution ResolveWindow(WindowSelector selector);

    /// <summary>
    /// Reads one window in full for get_window_info (roadmap M2.4): <see cref="GetWindowInfo"/> plus whether it is in
    /// front, its z-order position, topmost, enabled, responding and its DPI. Null when the handle is not a window.
    /// </summary>
    WindowDetails? GetWindowDetails(nint handle);

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
    //
    // The placements (roadmap M2.3) restore a minimized or maximized window to normal first, without activating it
    // (SW_SHOWNOACTIVATE, at most twice, for a window minimized from maximized), and fail with MoveFailed or ResizeFailed
    // when it stays minimized or maximized. They place the visible frame (BoundsFrame.Visible) unless stated otherwise,
    // never activate the window (bringToFront only raises it), and place it a second time when the first placement took
    // it to a monitor with another scale, after the application has applied its own DPI change.

    /// <summary>Moves the window's visible frame to (x, y), keeping its size (SWP_NOSIZE).</summary>
    WindowChangeResult MoveWindow(nint handle, int x, int y, bool bringToFront = true);

    /// <summary>Resizes the window's visible frame, keeping its top-left corner (SWP_NOMOVE). Never raises it.</summary>
    WindowChangeResult ResizeWindow(nint handle, int width, int height);

    /// <summary>
    /// Resizes the window's visible frame, keeping its top-left corner (SWP_NOMOVE), and raises it without activating it when
    /// <paramref name="bringToFront"/> is true (set_window_bounds with a size only, roadmap M6.1). Fails with ResizeFailed.
    /// </summary>
    WindowChangeResult ResizeWindow(nint handle, int width, int height, bool bringToFront);

    /// <summary>
    /// Moves and resizes the window rect (GetWindowRect, with the invisible borders: <see cref="BoundsFrame.Outer"/>), the
    /// rect layouts save and apply. set_window_bounds uses <see cref="SetWindowFrame"/>.
    /// </summary>
    WindowChangeResult SetWindowBounds(nint handle, int x, int y, int width, int height, bool bringToFront = true);

    /// <summary>
    /// Moves and resizes the window's visible frame (<see cref="BoundsFrame.Visible"/>): the window rect becomes the
    /// target grown by the invisible borders, so the frame the user sees is exactly the target.
    /// </summary>
    WindowChangeResult SetWindowFrame(nint handle, int x, int y, int width, int height, bool bringToFront = true);

    /// <summary>
    /// Waits up to <paramref name="timeout"/> until two reads of the window's frame, one poll interval apart, are equal:
    /// the application has stopped moving or resizing it (an animation, an app that applies its own saved bounds).
    /// Returns at once for zero, and when the window is gone. Only reads; never blocks on the window's thread.
    /// </summary>
    void WaitForSettledFrame(nint handle, TimeSpan timeout);

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
    /// one), at its normal size clamped to the target work area, and succeeds only when the window is on that monitor
    /// afterwards. To a monitor with another scale the window is moved in two steps: onto the monitor at its size
    /// (SWP_NOSIZE), where the application rescales itself for the new DPI, then sized and positioned from the size it
    /// chose; the size is never scaled in advance. Maximize moves the window there first, then maximizes it, and succeeds
    /// only when it is maximized there.
    /// </summary>
    WindowChangeResult MoveWindowToMonitor(nint handle, int monitorIndex, MonitorPositioning positioning = MonitorPositioning.Center, bool bringToFront = true);

    /// <summary>
    /// set_window_zorder (roadmap M6.7): changes only the window's place in the z-order, without moving, resizing or activating
    /// it, and reads it back. <paramref name="above"/> is the window <see cref="ZOrderMode.Above"/> places it directly above
    /// (topmost too when that window is). A child window's handle stands for its top-level window. Refused like any window
    /// change (InvalidHandle, TargetDeniedByPolicy, AccessDenied); ZOrderNotApplied when the read-back shows the window was not
    /// made topmost, not unpinned, or not placed above. The windows it makes topmost are recorded in <see cref="TopmostPins"/>.
    /// </summary>
    /// <exception cref="WindowNotRespondingException">The window did not answer in time.</exception>
    WindowChangeResult SetWindowZOrder(nint handle, ZOrderMode mode, nint? above = null);

    bool IsValidWindow(nint handle);

    /// <summary>
    /// The server's target policy's verdict on the window (roadmap M1.12): it is matched by its process, and a
    /// UWP frame window also by the app it hosts, as the listings report them. The Window Control tools,
    /// close_window and window activation refuse a window it denies. Fails closed: while the policy denies
    /// anything, a window whose process (or hosted app) cannot be named is refused
    /// (<see cref="TargetPolicyVerdict.Unreadable"/>). Allowed when the server has no policy.
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

    /// <summary>
    /// The application windows on other virtual desktops: windows that would be in <see cref="GetApplicationWindows"/>
    /// but that the shell cloaks (DWM_CLOAKED_SHELL), and that the virtual desktop manager does not put on the current
    /// desktop or on none (a suspended or prelaunched Store frame is cloaked the same way, and is on no desktop); a window
    /// it cannot place is kept. close_application leaves them alone and reports them, so an application with unsaved work
    /// on another desktop is never reported closed without a word.
    /// </summary>
    IReadOnlyList<WindowInfo> GetApplicationWindowsOnOtherDesktops();

    // Batch operations. Each changes one window after another, with delayBetweenMs (clamped to 0-5000) between two
    // windows; the cancellation token ends the batch between windows and during that delay: the windows already
    // changed stay changed, and the call throws OperationCanceledException. Each window gets its own result, in order:
    // a window that fails, or does not answer (NotResponding), does not stop the batch. The bounds batch places the
    // visible frame, as set_window_bounds does.
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
