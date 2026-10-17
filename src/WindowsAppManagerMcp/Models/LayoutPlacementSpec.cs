using System.ComponentModel;

namespace WindowsAppManagerMcp.Models;

/// <summary>
/// A placement an agent writes with save_layout's placements (roadmap M6.8, owner decision D15). Its fields have the names
/// get_layout's placements have, so get_layout's output goes back into save_layout unchanged (fields save_layout does not
/// take, such as order, are ignored: the array's order is the order). Checked by
/// <see cref="Services.LayoutPresetSchema.TryBuildPlacement"/>; a bad one fails the save with InvalidPlacement.
/// </summary>
public sealed record LayoutPlacementSpec(
    [property: Description("Part of the window's process name, or all of it with exactProcessName")]
    string? ProcessName = null,
    [property: Description("Text the window title contains")]
    string? TitleContains = null,
    [property: Description("A snap_window position; or position")]
    string? Snap = null,
    [property: Description("x, y, width, height: fractions (0-1) of the work area")]
    RelativePosition? Position = null,
    [property: Description("A monitor: index, 'primary' (default), 'left', 'right', or an id or name from get_monitors")]
    MonitorSelector? Monitor = null,
    [property: ToolValues(typeof(LaunchWindowState))]
    [property: Description("maximized needs no snap or position")]
    string? State = null,
    bool? Focus = null,
    [property: Description("What launchMissing starts: executable or appId, no arguments")]
    LayoutLaunch? Launch = null,
    bool? ExactProcessName = null,
    string? ClassName = null,
    string? TitlePattern = null,
    string? ExePath = null,
    int? Ordinal = null,
    string? MonitorName = null,
    [property: Description("Restore rectangle of a maximized or minimized window, as position")]
    RelativePosition? NormalPosition = null);
