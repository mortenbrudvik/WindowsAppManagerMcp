namespace WindowsAppManagerMcp.Services.Interfaces;

/// <summary>
/// Abstraction over native Windows API calls for window management.
/// This interface enables unit testing by allowing mock implementations.
/// </summary>
public interface INativeWindowWrapper
{
    /// <summary>
    /// Enumerates all top-level windows by calling the callback for each window.
    /// </summary>
    void EnumWindows(Func<nint, bool> callback);

    /// <summary>
    /// Determines whether the specified window is visible.
    /// </summary>
    bool IsWindowVisible(nint hWnd);

    /// <summary>
    /// Determines whether the specified window handle identifies an existing window.
    /// </summary>
    bool IsWindow(nint hWnd);

    /// <summary>
    /// Determines whether the specified window is minimized (iconic).
    /// </summary>
    bool IsIconic(nint hWnd);

    /// <summary>
    /// Determines whether the specified window is maximized.
    /// </summary>
    bool IsZoomed(nint hWnd);

    /// <summary>
    /// Gets the title (text) of the specified window.
    /// </summary>
    string GetWindowText(nint hWnd);

    /// <summary>
    /// Gets the process ID that created the specified window.
    /// </summary>
    uint GetWindowThreadProcessId(nint hWnd);

    /// <summary>
    /// Gets the bounding rectangle of the specified window.
    /// Returns null if the operation fails.
    /// </summary>
    (int Left, int Top, int Right, int Bottom)? GetWindowRect(nint hWnd);

    /// <summary>
    /// Sets the window position and size. The window also goes to the top of the Z order (HWND_TOP)
    /// unless <paramref name="flags"/> has SWP_NOZORDER, and it is activated unless they have SWP_NOACTIVATE.
    /// When Windows refuses it, the result carries the Win32 error read right after the call:
    /// ERROR_ACCESS_DENIED (5) when User Interface Privilege Isolation blocks changes to a window of a more
    /// privileged process (an app running as administrator), ERROR_INVALID_WINDOW_HANDLE (1400) when the
    /// window is gone.
    /// </summary>
    Win32CallResult SetWindowPos(nint hWnd, int x, int y, int width, int height, uint flags);

    /// <summary>
    /// Sets the show state of the specified window.
    /// </summary>
    bool ShowWindow(nint hWnd, int cmdShow);

    /// <summary>
    /// Brings the window to the foreground and activates it.
    /// </summary>
    bool SetForegroundWindow(nint hWnd);

    /// <summary>
    /// Brings the window to the top of the Z order and activates it (it is SetWindowPos without
    /// SWP_NOACTIVATE). To raise a window without activating it, use <see cref="SetWindowPos"/> without
    /// SWP_NOZORDER and with SWP_NOACTIVATE.
    /// </summary>
    bool BringWindowToTop(nint hWnd);

    /// <summary>
    /// Gets the handle to the foreground window.
    /// </summary>
    nint GetForegroundWindow();

    /// <summary>
    /// Gets an ancestor of the window: its parent, its top-level root, or its root owner.
    /// Returns zero when the handle is not a window.
    /// </summary>
    nint GetAncestor(nint hWnd, WindowAncestor ancestor);

    /// <summary>
    /// Gets the desktop shell's window (Progman for explorer.exe), or zero when no shell is running.
    /// </summary>
    nint GetShellWindow();

    /// <summary>
    /// Posts a message to the message queue of the specified window. When Windows refuses it, the
    /// result carries the Win32 error read right after the call: ERROR_ACCESS_DENIED (5) when User
    /// Interface Privilege Isolation blocks the message because the window belongs to a more privileged
    /// process, ERROR_INVALID_WINDOW_HANDLE (1400) when the window is gone, ERROR_NOT_ENOUGH_QUOTA
    /// (1816) when its message queue is full.
    /// </summary>
    Win32CallResult PostMessage(nint hWnd, uint msg, nint wParam, nint lParam);

    /// <summary>
    /// Gets the process ID of the current process.
    /// </summary>
    uint GetCurrentProcessId();

    /// <summary>
    /// Gets the name of the process with the given ID, without the ".exe" extension (for example
    /// "msedge"). Returns null when the process has exited or its name cannot be read.
    /// </summary>
    string? GetProcessName(uint processId);

    // Window identity and state reads (M1.6). All of them only read; none can change a window.

    /// <summary>
    /// Enumerates the child windows of <paramref name="hWndParent"/>, including their descendants,
    /// by calling the callback for each one until it returns false. A zero handle has no children
    /// (unlike the Win32 call, it does not list the top-level windows).
    /// </summary>
    void EnumChildWindows(nint hWndParent, Func<nint, bool> callback);

    /// <summary>
    /// Gets the window class name (for example "Notepad", "#32770" for a dialog or "Shell_TrayWnd" for
    /// the taskbar). Returns an empty string when the handle is not a window.
    /// </summary>
    string GetClassName(nint hWnd);

    /// <summary>
    /// Reads a window value with GetWindowLongPtr, for example GWL_EXSTYLE (-20) or GWL_STYLE (-16).
    /// Returns 0 when the handle is not a window.
    /// </summary>
    nint GetWindowLongPtr(nint hWnd, int index);

    /// <summary>
    /// Gets a related window with GetWindow, for example GW_OWNER (4) or GW_ENABLEDPOPUP (6).
    /// Returns 0 when there is none.
    /// </summary>
    nint GetWindow(nint hWnd, uint command);

    /// <summary>
    /// Whether DWM cloaks the window (DWMWA_CLOAKED): it is on another virtual desktop, is a suspended
    /// UWP frame, or its app hid it this way. IsWindowVisible is still true for a cloaked window, but
    /// nothing of it is on screen. Returns false when the attribute cannot be read.
    /// </summary>
    bool IsCloaked(nint hWnd);

    /// <summary>
    /// Why DWM cloaks the window (the DWMWA_CLOAKED value): 0 when it is not cloaked or the attribute cannot be read,
    /// DWM_CLOAKED_APP (1) when its app cloaked it, DWM_CLOAKED_SHELL (2) when the shell did, as for a window on another
    /// virtual desktop, DWM_CLOAKED_INHERITED (4) when it inherits its owner's cloak.
    /// </summary>
    int GetCloakReason(nint hWnd);

    /// <summary>
    /// Gets the visible frame of the window (DWMWA_EXTENDED_FRAME_BOUNDS), without the invisible
    /// resize borders that GetWindowRect includes, in physical screen pixels. Returns null when the
    /// attribute cannot be read.
    /// </summary>
    (int Left, int Top, int Right, int Bottom)? GetExtendedFrameBounds(nint hWnd);

    /// <summary>
    /// Whether Windows considers the window's thread hung (IsHungAppWindow: it has not processed
    /// messages for several seconds).
    /// </summary>
    bool IsHungAppWindow(nint hWnd);

    /// <summary>
    /// Whether the window's thread answers a message within <paramref name="timeoutMs"/>: SendMessageTimeout with
    /// WM_NULL, which every window ignores, and SMTO_ABORTIFHUNG, so a thread Windows already reports hung is not
    /// waited for at all. True when it answered; false only when it did not answer in time (ERROR_TIMEOUT, which is also
    /// what a thread Windows reports hung gives); null when the message could not be sent for another reason, so nothing
    /// is known about the thread: the window is gone (ERROR_INVALID_WINDOW_HANDLE), or User Interface Privilege Isolation
    /// blocks it (ERROR_ACCESS_DENIED, a window of a more privileged process). It blocks the caller for up to
    /// <paramref name="timeoutMs"/> and changes nothing in the window.
    /// </summary>
    bool? RespondsWithin(nint hWnd, uint timeoutMs);

    /// <summary>
    /// Gets the DPI of the window (GetDpiForWindow). The value depends on the window's DPI awareness:
    /// 96 for a DPI-unaware window on any monitor, the system DPI for a system-aware window, and the
    /// DPI of its monitor only for a per-monitor-aware window. Read a monitor's DPI with
    /// GetDpiForMonitor (INativeMonitorWrapper) instead. Returns 0 when the handle is not a window.
    /// </summary>
    uint GetDpiForWindow(nint hWnd);
}
