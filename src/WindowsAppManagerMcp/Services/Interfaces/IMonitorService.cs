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
}
