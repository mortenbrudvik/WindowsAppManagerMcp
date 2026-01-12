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
}
