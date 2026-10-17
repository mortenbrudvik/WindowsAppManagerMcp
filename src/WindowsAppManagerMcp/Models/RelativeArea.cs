using System.Text.Json.Serialization;

namespace WindowsAppManagerMcp.Models;

/// <summary>
/// The part of a monitor a relative rectangle (percentages or fractions) is measured against (roadmap M6.7):
/// set_window_bounds' relativeTo with unit 'percent'.
/// </summary>
[ToolEnum(WindowsAppManagerMcp.Infrastructure.ToolErrorReporting.InvalidArgumentsErrorCode, "relativeTo", NullMeans = "workArea")]
public enum RelativeArea
{
    /// <summary>The monitor's work area: its bounds without the taskbar and docked app bars. Snap positions use it too.</summary>
    [JsonStringEnumMemberName("workArea")]
    WorkArea,

    /// <summary>The monitor's full bounds, taskbar included.</summary>
    [JsonStringEnumMemberName("monitor")]
    Monitor
}
