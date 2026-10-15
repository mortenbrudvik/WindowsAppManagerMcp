namespace WindowsAppManagerMcp.Models;

/// <summary>
/// A monitor as EnumDisplayMonitors reports it, with its identity from the display configuration (roadmap M6.7) when that
/// could be read. <see cref="Index"/> is the monitor's place in EnumDisplayMonitors order and can change when monitors are
/// docked, added or removed; <see cref="Id"/> follows the physical monitor.
/// </summary>
public record MonitorInfo(
    int Index,
    string DeviceName,
    bool IsPrimary,
    MonitorRect Bounds,
    MonitorRect WorkArea,
    double ScaleFactor
)
{
    /// <summary>
    /// The monitor's effective DPI (GetDpiForMonitor, MDT_EFFECTIVE_DPI). The native wrapper sets what Windows reports;
    /// a monitor built without it gets <see cref="ScaleFactor"/> times 96.
    /// </summary>
    public int Dpi { get; init; } = (int)Math.Round(ScaleFactor * 96);

    /// <summary>
    /// A short stable id, "m-" plus 8 hex digits, hashed from the monitor's device path
    /// (<see cref="WindowsAppManagerMcp.Services.MonitorIdentity.IdOf(string)"/>). It stays the same while the monitor
    /// stays on the same port; moving it to another port or dock gives it a new id. Null when the display configuration
    /// could not be read or reports no device path for the monitor (some remote, virtual and EDID-less displays).
    /// </summary>
    public string? Id { get; init; }

    /// <summary>The monitor's EDID name; in clone mode the names of every cloned monitor joined with " + ". Null when unknown.</summary>
    public string? FriendlyName { get; init; }

    /// <summary>
    /// Whether the monitor is a built-in panel (an internal, embedded DisplayPort or embedded UDI connector); in clone
    /// mode, whether any cloned monitor is. Null when the display configuration could not be read for the monitor.
    /// </summary>
    public bool? IsInternal { get; init; }
}
