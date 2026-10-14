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
        catch
        {
            // Gone, not a window, or the provider failed: nothing was closed through UI Automation
            return false;
        }
    }
}
