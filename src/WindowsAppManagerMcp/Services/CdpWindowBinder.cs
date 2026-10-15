using WindowsAppManagerMcp.Services.Interfaces;

namespace WindowsAppManagerMcp.Services;

/// <summary>
/// Binds a desktop browser window to the browser windows CDP reports for its tabs (roadmap M5.3): Browser.getWindowForTarget
/// gives each tab's windowId and that window's bounds, in DIPs, and its state. A normal window is the CDP window whose
/// bounds, scaled by the window's DPI (GetDpiForWindow / 96), are its GetWindowRect within a few pixels; a maximized,
/// minimized or full-screen window reports its restored bounds, so it can be any CDP window in that state. The binding
/// narrows the tabs a title is matched among; it never picks a tab by itself, since CDP reports no active tab.
/// </summary>
/// <remarks>
/// Chromium opens a new window 10 DIPs below and to the right of the last one, so the tolerance stays under that. On a
/// monitor whose scale differs from the primary one's, Chromium's DIP positions are not the pixels divided by the scale,
/// so such a window may match none; the title over all tabs then decides, as before M5.3.
/// </remarks>
public static class CdpWindowBinder
{
    /// <summary>The ids of the CDP windows <paramref name="window"/> can be.</summary>
    public static IReadOnlySet<int> MatchingWindows(CdpWindowGeometry window, IEnumerable<CdpBrowserWindow> browserWindows) =>
        browserWindows.Where(candidate => Matches(window, candidate)).Select(candidate => candidate.WindowId).ToHashSet();

    /// <summary>Whether <paramref name="window"/> can be the CDP window <paramref name="candidate"/>.</summary>
    public static bool Matches(CdpWindowGeometry window, CdpBrowserWindow candidate)
    {
        switch (window.State)
        {
            case CdpWindowState.Minimized:
                return candidate.State == "minimized";
            case CdpWindowState.Maximized:
                return candidate.State == "maximized";
        }

        // Full screen hides the frame without maximizing the window; its reported bounds are the restored ones
        if (candidate.State == "fullscreen")
            return true;

        if (candidate.State != "normal")
            return false;

        var scale = window.Scale > 0 ? window.Scale : 1.0;
        var tolerance = TolerancePx(scale);
        return Near(window.Left, candidate.Left * scale, tolerance) &&
               Near(window.Top, candidate.Top * scale, tolerance) &&
               Near(window.Right - window.Left, candidate.Width * scale, tolerance) &&
               Near(window.Bottom - window.Top, candidate.Height * scale, tolerance);
    }

    /// <summary>How far, in pixels, a scaled edge may be from the window's: 3 DIPs of rounding, and one pixel.</summary>
    public static int TolerancePx(double scale) => (int)Math.Ceiling(3 * scale) + 1;

    private static bool Near(int pixels, double scaled, int tolerance) => Math.Abs(pixels - scaled) <= tolerance;
}
