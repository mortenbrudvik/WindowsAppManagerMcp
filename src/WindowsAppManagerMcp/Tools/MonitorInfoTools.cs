using System.ComponentModel;
using ModelContextProtocol.Server;
using WindowsAppManagerMcp.Models;
using WindowsAppManagerMcp.Services;
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
    [Description("Get information about all connected displays/monitors including bounds, work area (excluding taskbar), scale factor " +
                 "and dpi, id (stays with the monitor on its port), friendlyName, isInternal (a built-in panel) and relation (left, " +
                 "right, above or below the primary); a monitor parameter takes its index, id or name. virtualScreen: the bounds of " +
                 "all monitors together, which take_screenshot captures with monitor 'all'.")]
    public MonitorsResult GetMonitors() => ReadMonitors(_monitorService, _screen);

    /// <summary>
    /// get_monitors' result, which the wam://monitors resource returns too (roadmap M6.9), so a change to one changes both.
    /// </summary>
    internal static MonitorsResult ReadMonitors(IMonitorService monitorService, IScreenCaptureWrapper screen)
    {
        var monitors = monitorService.GetAllMonitors();
        var primary = monitors.FirstOrDefault(m => m.IsPrimary);
        var primaryIndex = primary?.Index ?? 0;
        var (x, y, width, height) = screen.GetVirtualScreenBounds();

        return new MonitorsResult(
            Monitors: monitors.Select(m => ToDto(m, primary)).ToList(),
            PrimaryIndex: primaryIndex,
            VirtualScreen: new VirtualScreenBounds(x, y, width, height)
        );
    }

    /// <summary>
    /// A monitor as get_monitors and the wam://monitors resource return it, with its identity and DPI
    /// (roadmap M6.7) and, given the <paramref name="primary"/>, where it is from it.
    /// </summary>
    internal static MonitorInfoDto ToDto(MonitorInfo monitor, MonitorInfo? primary = null) => new(
        Index: monitor.Index,
        Name: monitor.DeviceName,
        IsPrimary: monitor.IsPrimary,
        Bounds: new BoundsDto(monitor.Bounds.X, monitor.Bounds.Y, monitor.Bounds.Width, monitor.Bounds.Height),
        WorkArea: new BoundsDto(monitor.WorkArea.X, monitor.WorkArea.Y, monitor.WorkArea.Width, monitor.WorkArea.Height),
        ScaleFactor: monitor.ScaleFactor
    )
    {
        Id = monitor.Id,
        FriendlyName = monitor.FriendlyName,
        Dpi = monitor.Dpi,
        IsInternal = monitor.IsInternal == true ? true : null,
        Relation = primary is null ? null : MonitorSelection.RelationToPrimary(monitor, primary)
    };
}

public record MonitorsResult(
    IReadOnlyList<MonitorInfoDto> Monitors,
    int PrimaryIndex,
    VirtualScreenBounds VirtualScreen
);

/// <summary>
/// A monitor in get_monitors (roadmap M6.7 added the fields after ScaleFactor): Id and FriendlyName are left out when the
/// display configuration could not be read for it, IsInternal unless it is a built-in panel, and Relation for the primary.
/// </summary>
public record MonitorInfoDto(
    int Index,
    string Name,
    bool IsPrimary,
    BoundsDto Bounds,
    BoundsDto WorkArea,
    double ScaleFactor
)
{
    /// <summary>The monitor's id ("m-" and 8 hex digits): the same while the monitor stays on its port.</summary>
    public string? Id { get; init; }

    /// <summary>The monitor's EDID name, such as "DELL U2720Q".</summary>
    public string? FriendlyName { get; init; }

    /// <summary>The effective DPI: 96 at 100%.</summary>
    public int Dpi { get; init; }

    /// <summary>True for a built-in panel (a laptop's screen); left out otherwise.</summary>
    public bool? IsInternal { get; init; }

    /// <summary>Where the monitor is from the primary: left, right, above or below; left out for the primary.</summary>
    public string? Relation { get; init; }
}
