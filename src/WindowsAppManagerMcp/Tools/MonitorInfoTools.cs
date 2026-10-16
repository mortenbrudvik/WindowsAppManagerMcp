using System.ComponentModel;
using ModelContextProtocol.Server;
using WindowsAppManagerMcp.Models;
using WindowsAppManagerMcp.Services.Interfaces;

namespace WindowsAppManagerMcp.Tools;

[McpServerToolType]
public class MonitorInfoTools
{
    private readonly IMonitorService _monitorService;
    private readonly IScreenCaptureWrapper _screen;

    /// <param name="monitorService">The monitors.</param>
    /// <param name="screen">The virtual screen's bounds, read as list_screens and the captures read them (roadmap M6.1).</param>
    public MonitorInfoTools(IMonitorService monitorService, IScreenCaptureWrapper screen)
    {
        _monitorService = monitorService;
        _screen = screen ?? throw new ArgumentNullException(nameof(screen));
    }

    [McpServerTool(Name = "get_monitors", Title = "List Monitors", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("Get information about all connected displays/monitors including bounds, work area (excluding taskbar), and scale factor, " +
                 "and virtualScreen: the bounds of all monitors together, which take_screenshot captures with monitorIndex -1.")]
    public MonitorsResult GetMonitors()
    {
        var monitors = _monitorService.GetAllMonitors();
        var primaryIndex = monitors.FirstOrDefault(m => m.IsPrimary)?.Index ?? 0;
        var (x, y, width, height) = _screen.GetVirtualScreenBounds();

        return new MonitorsResult(
            Monitors: monitors.Select(m => new MonitorInfoDto(
                Index: m.Index,
                Name: m.DeviceName,
                IsPrimary: m.IsPrimary,
                Bounds: new BoundsDto(m.Bounds.X, m.Bounds.Y, m.Bounds.Width, m.Bounds.Height),
                WorkArea: new BoundsDto(m.WorkArea.X, m.WorkArea.Y, m.WorkArea.Width, m.WorkArea.Height),
                ScaleFactor: m.ScaleFactor
            )).ToList(),
            PrimaryIndex: primaryIndex,
            VirtualScreen: new VirtualScreenBounds(x, y, width, height)
        );
    }

    [McpServerTool(Name = "get_primary_monitor", Title = "Get Primary Monitor", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("Get information about the primary display. Convenience method that returns the main monitor's details.")]
    public MonitorInfoDto GetPrimaryMonitor()
    {
        var monitor = _monitorService.GetPrimaryMonitor();
        return new MonitorInfoDto(
            Index: monitor.Index,
            Name: monitor.DeviceName,
            IsPrimary: monitor.IsPrimary,
            Bounds: new BoundsDto(monitor.Bounds.X, monitor.Bounds.Y, monitor.Bounds.Width, monitor.Bounds.Height),
            WorkArea: new BoundsDto(monitor.WorkArea.X, monitor.WorkArea.Y, monitor.WorkArea.Width, monitor.WorkArea.Height),
            ScaleFactor: monitor.ScaleFactor
        );
    }
}

public record MonitorsResult(
    IReadOnlyList<MonitorInfoDto> Monitors,
    int PrimaryIndex,
    VirtualScreenBounds VirtualScreen
);

public record MonitorInfoDto(
    int Index,
    string Name,
    bool IsPrimary,
    BoundsDto Bounds,
    BoundsDto WorkArea,
    double ScaleFactor
);
