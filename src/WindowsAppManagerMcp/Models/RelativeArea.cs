namespace WindowsAppManagerMcp.Models;

/// <summary>
/// The part of a monitor a relative rectangle (percentages or fractions) is measured against (roadmap M6.7). Not yet a
/// tool argument: set_window_bounds' relativeTo maps onto it when M6.7 adds unit=percent.
/// </summary>
public enum RelativeArea
{
    /// <summary>The monitor's work area: its bounds without the taskbar and docked app bars. Snap positions use it too.</summary>
    WorkArea,

    /// <summary>The monitor's full bounds, taskbar included.</summary>
    Monitor
}
