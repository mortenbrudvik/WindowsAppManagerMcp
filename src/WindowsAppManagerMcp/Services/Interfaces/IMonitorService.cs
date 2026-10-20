using WindowsAppManagerMcp.Models;

namespace WindowsAppManagerMcp.Services.Interfaces;

public interface IMonitorService
{
    IReadOnlyList<MonitorInfo> GetAllMonitors();

    MonitorInfo GetPrimaryMonitor();

    MonitorInfo? GetMonitorAt(int x, int y);

    /// <summary>
    /// The monitor the window is on (MonitorFromWindow: the one with most of it, else the nearest); for a minimized window, the
    /// monitor it restores to, as Windows uses the rectangle from before the minimize. Null when it cannot be read: the window
    /// is gone, or the displays changed during the lookup.
    /// </summary>
    MonitorInfo? GetMonitorForWindow(nint windowHandle);

    /// <summary>The index of <see cref="GetMonitorForWindow"/>'s monitor; null when it cannot be read, never a guess (issue #12).</summary>
    int? GetMonitorIndex(nint windowHandle);

    MonitorRect CalculateSnapBounds(int monitorIndex, SnapPosition position);

    /// <summary>
    /// The monitor a `monitor` argument names (roadmap M6.7), resolved on the current monitors by
    /// <see cref="MonitorSelection.Resolve"/>; 'current' is the monitor of the foreground window.
    /// </summary>
    MonitorResolution ResolveMonitor(MonitorSelector selector);
}
