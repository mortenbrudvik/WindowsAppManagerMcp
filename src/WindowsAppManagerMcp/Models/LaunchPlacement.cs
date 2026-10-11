using System.ComponentModel;
using System.Text.Json.Serialization;

namespace WindowsAppManagerMcp.Models;

/// <summary>The state a launch placement leaves the window in (roadmap M3.13).</summary>
[ToolEnum(nameof(WindowErrorCode.InvalidState), "placement state")]
public enum LaunchWindowState
{
    /// <summary>Normal: neither minimized nor maximized.</summary>
    [JsonStringEnumMemberName("normal")]
    [ToolValueAlias("restore", Listed = false)]
    Normal,

    /// <summary>Maximized on its monitor (or on monitorIndex). Windows has no maximize that does not activate.</summary>
    [JsonStringEnumMemberName("maximized")]
    [ToolValueAlias("maximize", Listed = false)]
    Maximized,

    /// <summary>Placed first (with snap, bounds or monitorIndex), then minimized.</summary>
    [JsonStringEnumMemberName("minimized")]
    [ToolValueAlias("minimize", Listed = false)]
    Minimized
}

/// <summary>
/// Where launch_application and a launch_applications_batch item put the launch's window once it is found (roadmap
/// M3.13): snapped or at bounds (not both), on a monitor, in a state, and in front. The strings are checked by the tool
/// (<see cref="LaunchPlacementPlan.TryCreate"/>), so a bad batch item fails alone.
/// </summary>
public sealed record LaunchPlacement(
    [property: ToolValues(typeof(SnapPosition))]
    [property: Description("Snap position, as snap_window takes it; not together with bounds")]
    string? Snap = null,
    [property: Description("Monitor (0-based, see get_monitors): where snap places the window; without snap and bounds the window is centred on it (with state 'maximized', maximized there). Not together with bounds")]
    int? MonitorIndex = null,
    [property: Description("The visible frame to place the window at: x, y, width and height in physical virtual-screen pixels, as set_window_bounds takes them; not together with snap")]
    BoundsDto? Bounds = null,
    [property: ToolValues(typeof(LaunchWindowState))]
    [property: Description("normal, maximized (not with snap or bounds; activates the window) or minimized (after it is placed; not with focus)")]
    string? State = null,
    [property: Description("Bring the window to the front afterwards, as focus_window does (default false)")]
    bool? Focus = null);

/// <summary>A <see cref="LaunchPlacement"/> that was checked: the parsed values, consistent with each other.</summary>
public sealed record LaunchPlacementPlan(
    SnapPosition? Snap,
    int? MonitorIndex,
    WindowRect? Bounds,
    LaunchWindowState? State,
    bool Focus)
{
    /// <summary>True when the plan moves, sizes or changes the state of the window (anything but focus).</summary>
    public bool ChangesGeometry => Snap != null || Bounds != null || MonitorIndex != null || State != null;

    /// <summary>
    /// Checks a placement: snap and state must be known values (InvalidSnapPosition, InvalidState); snap and bounds, bounds
    /// and monitorIndex, snap or bounds with state maximized, and focus with state minimized are refused, as are a
    /// size below 1 pixel, a negative monitorIndex (InvalidMonitorIndex) and a placement that asks for nothing
    /// (InvalidArguments). On failure <paramref name="errorCode"/> and <paramref name="error"/> say why.
    /// </summary>
    public static bool TryCreate(LaunchPlacement placement, out LaunchPlacementPlan? plan, out string? errorCode, out string? error)
    {
        ArgumentNullException.ThrowIfNull(placement);
        plan = null;

        SnapPosition? snap = null;
        if (placement.Snap != null)
        {
            if (!ToolEnum<SnapPosition>.TryParse(placement.Snap, out var position, out var snapError))
            {
                return Fail(ToolEnum<SnapPosition>.ErrorCode, $"placement.snap: {snapError}", out errorCode, out error);
            }

            snap = position;
        }

        LaunchWindowState? state = null;
        if (placement.State != null)
        {
            if (!ToolEnum<LaunchWindowState>.TryParse(placement.State, out var parsed, out var stateError))
            {
                return Fail(ToolEnum<LaunchWindowState>.ErrorCode, $"placement.state: {stateError}", out errorCode, out error);
            }

            state = parsed;
        }

        const string invalid = "InvalidArguments";
        if (snap != null && placement.Bounds != null)
        {
            return Fail(invalid, "placement takes snap or bounds, not both.", out errorCode, out error);
        }

        if (placement.Bounds != null && placement.MonitorIndex != null)
        {
            return Fail(invalid, "placement.bounds are virtual-screen coordinates, which already name the monitor: leave out " +
                                 "monitorIndex, or use snap with monitorIndex.", out errorCode, out error);
        }

        if (placement.Bounds is { } bounds && (bounds.Width < 1 || bounds.Height < 1))
        {
            return Fail(invalid, $"placement.bounds width and height must be at least 1 pixel, but they are {bounds.Width} x {bounds.Height}.",
                out errorCode, out error);
        }

        if (placement.MonitorIndex is < 0)
        {
            return Fail(nameof(WindowErrorCode.InvalidMonitorIndex),
                $"placement.monitorIndex {placement.MonitorIndex} is not a monitor: indexes start at 0 (see get_monitors).", out errorCode, out error);
        }

        if (state == LaunchWindowState.Maximized && (snap != null || placement.Bounds != null))
        {
            return Fail(invalid, "placement.state 'maximized' fills the monitor, so it takes no snap or bounds (use monitorIndex to pick the monitor).",
                out errorCode, out error);
        }

        var focus = placement.Focus == true;
        if (focus && state == LaunchWindowState.Minimized)
        {
            return Fail(invalid, "placement.focus cannot bring a window to the front that state 'minimized' minimizes.", out errorCode, out error);
        }

        var candidate = new LaunchPlacementPlan(
            snap, placement.MonitorIndex,
            placement.Bounds is { } b ? new WindowRect(b.X, b.Y, b.Width, b.Height) : null,
            state, focus);
        if (!candidate.ChangesGeometry && !candidate.Focus)
        {
            return Fail(invalid, "placement asks for nothing: give snap, bounds, monitorIndex, state or focus: true, or leave placement out.",
                out errorCode, out error);
        }

        plan = candidate;
        errorCode = null;
        error = null;
        return true;
    }

    private static bool Fail(string code, string message, out string? errorCode, out string? error)
    {
        errorCode = code;
        error = message;
        return false;
    }
}
