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
    /// Sets the window position and size.
    /// </summary>
    bool SetWindowPos(nint hWnd, int x, int y, int width, int height, uint flags);

    /// <summary>
    /// Sets the show state of the specified window.
    /// </summary>
    bool ShowWindow(nint hWnd, int cmdShow);

    /// <summary>
    /// Brings the window to the foreground and activates it.
    /// </summary>
    bool SetForegroundWindow(nint hWnd);

    /// <summary>
    /// Brings the window to the top of the Z order.
    /// </summary>
    bool BringWindowToTop(nint hWnd);

    /// <summary>
    /// Gets the handle to the foreground window.
    /// </summary>
    nint GetForegroundWindow();

    /// <summary>
    /// Posts a message to the message queue of the specified window.
    /// </summary>
    bool PostMessage(nint hWnd, uint msg, nint wParam, nint lParam);

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
    /// Gets the DPI of the window (GetDpiForWindow), for example 96 at 100% scaling. Returns 0 when
    /// the handle is not a window.
    /// </summary>
    uint GetDpiForWindow(nint hWnd);
}
