namespace WindowsAppManagerMcp.Services.Interfaces;

/// <summary>
/// Abstraction over the Windows UI Automation API for asking a window to close (close_application's fallback, roadmap
/// M2.5). This interface enables unit testing by allowing mock implementations. Its browser reads moved onto
/// <see cref="IUiaTree"/> (roadmap M3.F24, <see cref="BrowserUiaReader"/>).
/// </summary>
public interface IUIAutomationWrapper
{
    /// <summary>
    /// Asks the window to close through UI Automation (WindowPattern.Close), as its title bar's Close button does: the
    /// application can still prompt to save or refuse. close_application uses it for a packaged app's frame window when
    /// Windows does not accept the close message (roadmap M2.5). It is an action, not a read: it changes the window.
    /// </summary>
    /// <param name="hwnd">The window handle.</param>
    /// <param name="cancellationToken">Checked right before the close is requested: once it is cancelled (the caller's
    /// deadline passed while the window was looked up), nothing is requested and the result is false.</param>
    /// <returns>True when the close was requested; false when the window has no Window pattern, is gone, the call failed,
    /// or <paramref name="cancellationToken"/> was cancelled first.</returns>
    bool CloseWindow(nint hwnd, CancellationToken cancellationToken);
}
