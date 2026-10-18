using System.Runtime.InteropServices;
using System.Windows.Automation;
using WindowsAppManagerMcp.Services.Interfaces;

namespace WindowsAppManagerMcp.Native;

/// <summary>
/// Wrapper around the Windows UI Automation API for asking a window to close (close_application's fallback, roadmap M2.5).
/// The browser reads it used to make go through IUiaTree now (roadmap M3.F24).
/// </summary>
public class UIAutomationWrapper : IUIAutomationWrapper
{
    /// <inheritdoc />
    public bool CloseWindow(nint hwnd, CancellationToken cancellationToken)
    {
        try
        {
            UiaClientStartup.Shared.EnsureStarted();
            var element = AutomationElement.FromHandle(hwnd);
            if (element == null || !element.TryGetCurrentPattern(WindowPattern.Pattern, out var pattern) || pattern is not WindowPattern window)
            {
                return false;
            }

            // A caller that has given up (the lookup above took too long) must not have the window closed after all
            if (cancellationToken.IsCancellationRequested)
            {
                return false;
            }

            window.Close();
            return true;
        }
        catch (Exception ex) when (IsExpectedCloseFailure(ex))
        {
            // Gone, not a window, or not closable through UI Automation now: nothing was closed through it
            return false;
        }
    }

    /// <summary>
    /// Whether a failure of <see cref="CloseWindow"/> is one a close expects, so it returns false quietly: the window is gone
    /// (ElementNotAvailableException, or a COM error <see cref="UiaComErrors.Translate"/> reads as gone or disconnected), the
    /// handle is no window (ArgumentException), the window cannot be closed now or is not enabled (InvalidOperationException),
    /// its provider does not support the call, or it did not answer in time (TimeoutException). Anything else (a provider that
    /// failed, a bug) reaches the caller, WindowCloseVerifier, which logs it as a warning and counts the close as not sent, so
    /// an unexpected failure is never swallowed without a trace (review finding F9).
    /// </summary>
    public static bool IsExpectedCloseFailure(Exception exception) => exception switch
    {
        ElementNotAvailableException or ArgumentException or InvalidOperationException or TimeoutException => true,
        COMException com => UiaComErrors.Translate(com) != null,
        _ => false
    };
}
