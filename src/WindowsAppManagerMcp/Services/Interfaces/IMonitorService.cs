using WindowsAppManagerMcp.Models;

namespace WindowsAppManagerMcp.Services.Interfaces;

public interface IMonitorService
{
    IReadOnlyList<MonitorInfo> GetAllMonitors();

    MonitorInfo GetPrimaryMonitor();

    MonitorInfo? GetMonitorAt(int x, int y);

    MonitorInfo? GetMonitorForWindow(nint windowHandle);

    int GetMonitorIndex(nint windowHandle);

    MonitorRect CalculateSnapBounds(int monitorIndex, SnapPosition position);

    /// <summary>
    /// The monitor a `monitor` argument names (roadmap M6.7), resolved on the current monitors by
    /// <see cref="MonitorSelection.Resolve"/>; 'current' is the monitor of the foreground window.
    /// </summary>
    MonitorResolution ResolveMonitor(MonitorSelector selector);
}
