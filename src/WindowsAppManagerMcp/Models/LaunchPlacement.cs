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

    /// <summary>Maximized on its monitor (or on monitor). Windows has no maximize that does not activate.</summary>
    [JsonStringEnumMemberName("maximized")]
    [ToolValueAlias("maximize", Listed = false)]
    Maximized,

    /// <summary>Placed first (with snap, bounds or monitor), then minimized.</summary>
    [JsonStringEnumMemberName("minimized")]
    [ToolValueAlias("minimize", Listed = false)]
    Minimized
}

/// <summary>
/// The visible frame a launch placement puts the window at, in physical virtual-screen pixels: the placement's own input
/// type, so the results that write a <see cref="BoundsDto"/> keep their non-null members. All four are required: they are
/// nullable only so that a missing one (or a null) is refused by <see cref="LaunchPlacementPlan.TryCreate"/>
/// (InvalidArguments, for a batch item alone) instead of binding 0 and putting the window at the origin or making it 0 pixels
/// wide; the input schema lists them as required integers (<see cref="ToolRequiredAttribute"/>). An unknown member, such as
/// a unit, is refused (InvalidArguments) rather than ignored.
/// </summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record PlacementBounds(
    [property: ToolRequired] int? X,
    [property: ToolRequired] int? Y,
    [property: ToolRequired] int? Width,
    [property: ToolRequired] int? Height);

/// <summary>
/// Where launch_application and a launch_applications_batch item put the launch's window once it is found (roadmap
/// M3.13): snapped or at bounds (not both), on a monitor, in a state, and in front. The strings are checked by the tool
/// (<see cref="LaunchPlacementPlan.TryCreate"/>), so a bad batch item fails alone. Monitor (roadmap M6.7) is resolved to an
/// index before the check (LaunchPlacing). An unknown member, such as the monitorIndex 3.0.0 removed, is refused
/// (InvalidArguments), so it is never ignored.
/// </summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record LaunchPlacement(
    [property: ToolValues(typeof(SnapPosition))]
    [property: Description("Snap position, as snap_window takes it; not together with bounds")]
    string? Snap = null,
    [property: Description("The visible frame to place the window at: x, y, width and height in physical virtual-screen pixels, as set_window_bounds takes them; not together with snap")]
    PlacementBounds? Bounds = null,
    [property: ToolValues(typeof(LaunchWindowState))]
    [property: Description("normal, maximized (not with snap or bounds; activates the window) or minimized (after it is placed; not with focus)")]
    string? State = null,
    [property: Description("Bring the window to the front afterwards, as focus_window does (default false)")]
    bool? Focus = null,
    [property: Description(MonitorSelectorDescriptions.Monitor + " Snapped, else centred or maximized, there; not with bounds")]
    MonitorSelector? Monitor = null);

/// <summary>
/// A placement that was checked: the parsed values, consistent with each other. It is made only by its two factories, each
/// with its own rules: <see cref="TryCreate(LaunchPlacement, int?, out LaunchPlacementPlan?, out string?, out string?)"/> for a
/// launch tool's placement, and <see cref="ForLayout"/> for a window apply_layout launched. Snap, State and Focus cannot be
/// changed afterwards, not even with a with expression. <see cref="MonitorIndex"/>, <see cref="Bounds"/> and
/// <see cref="MonitorKey"/> can, because the placement service and LaunchPlacing set them again when they find the monitor
/// again or place the rectangle on it as it is then; the type does not check such a change, so it must keep the factory's
/// rules: no bounds on a snapped or maximized plan of TryCreate, and a monitor key on every plan of ForLayout.
/// </summary>
public sealed record LaunchPlacementPlan
{
    private LaunchPlacementPlan(SnapPosition? snap, int? monitorIndex, WindowRect? bounds, LaunchWindowState? state, bool focus)
    {
        Snap = snap;
        MonitorIndex = monitorIndex;
        Bounds = bounds;
        State = state;
        Focus = focus;
    }

    /// <summary>The snap position the window is snapped to; null for none.</summary>
    public SnapPosition? Snap { get; }

    /// <summary>
    /// The monitor, as an index: where the window is snapped, centred or maximized. The placement service sets it again when
    /// it finds a named monitor again by <see cref="MonitorKey"/>.
    /// </summary>
    public int? MonitorIndex { get; init; }

    /// <summary>
    /// The rectangle in virtual-screen pixels (<see cref="Frame"/> says which frame). The placement service sets it again from
    /// <see cref="RelativeBounds"/> on the monitor as it is when it places the window.
    /// </summary>
    public WindowRect? Bounds { get; init; }

    /// <summary>The state the window is left in; null to leave it as it comes.</summary>
    public LaunchWindowState? State { get; }

    /// <summary>Whether the window is brought to the front afterwards.</summary>
    public bool Focus { get; }

    /// <summary>
    /// Which frame <see cref="Bounds"/> is: the visible frame (<see cref="BoundsFrame.Visible"/>, as set_window_bounds and the
    /// launch tools' placement take it), or the window rect with its invisible borders (<see cref="BoundsFrame.Outer"/>), the
    /// rect layouts save and apply, for the windows apply_layout launches (roadmap M3.F27).
    /// </summary>
    public BoundsFrame Frame { get; private init; } = BoundsFrame.Visible;

    /// <summary>
    /// The monitor a name resolved to (roadmap M6.7, M6.8): its id, or its device name when it has none. The window appears
    /// up to the launch's wait later, and docking can renumber the monitors meanwhile, so the placement service finds that
    /// monitor again by this key when it places the window, and fails with MonitorNotFound when it is gone. Null for a
    /// plain index, which is placed as given.
    /// </summary>
    public string? MonitorKey { get; init; }

    /// <summary>
    /// <see cref="Bounds"/> as fractions of the work area of the <see cref="MonitorKey"/> monitor (apply_layout's launches,
    /// roadmap M6.8): the placement service places them on that monitor's work area as it is when it places the window. Null
    /// when Bounds are pixels as given.
    /// </summary>
    public RelativePosition? RelativeBounds { get; private init; }

    /// <summary>True when the plan moves, sizes or changes the state of the window (anything but focus).</summary>
    public bool ChangesGeometry => Snap != null || Bounds != null || MonitorIndex != null || State != null;

    /// <summary>
    /// Checks a placement with its monitor as an index (<see cref="TryCreate(LaunchPlacement, int?, out LaunchPlacementPlan?, out string?, out string?)"/>):
    /// an index monitor as given; a named one has no index until LaunchPlacing resolves it.
    /// </summary>
    public static bool TryCreate(LaunchPlacement placement, out LaunchPlacementPlan? plan, out string? errorCode, out string? error) =>
        TryCreate(placement, placement?.Monitor?.Index, out plan, out errorCode, out error);

    /// <summary>
    /// Checks a placement whose monitor resolved to <paramref name="monitorIndex"/>: snap and state must be known values
    /// (InvalidSnapPosition, InvalidState); bounds without x, y, width or height (missing or null: bound, it would be 0),
    /// snap and bounds, bounds and monitor, snap or bounds with state maximized, and focus with state minimized are refused,
    /// as are a size below 1 pixel, a negative monitor index (InvalidMonitorIndex) and a placement that asks for nothing
    /// (InvalidArguments). On failure <paramref name="errorCode"/> and <paramref name="error"/> say why.
    /// </summary>
    public static bool TryCreate(LaunchPlacement placement, int? monitorIndex, out LaunchPlacementPlan? plan, out string? errorCode,
        out string? error)
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
        if (placement.Bounds is { } given && MissingMembers(given) is { Length: > 0 } missing)
        {
            return Fail(invalid, $"placement.bounds has no {missing}: it needs x, y, width and height, in physical virtual-screen pixels.",
                out errorCode, out error);
        }

        if (snap != null && placement.Bounds != null)
        {
            return Fail(invalid, "placement takes snap or bounds, not both.", out errorCode, out error);
        }

        if (placement.Bounds != null && (monitorIndex != null || placement.Monitor != null))
        {
            return Fail(invalid, "placement.bounds are virtual-screen coordinates, which already name the monitor: leave out " +
                                 "monitor, or use snap with monitor.", out errorCode, out error);
        }

        if (placement.Bounds is { Width: { } width, Height: { } height } && (width < 1 || height < 1))
        {
            return Fail(invalid, $"placement.bounds width and height must be at least 1 pixel, but they are {width} x {height}.",
                out errorCode, out error);
        }

        if (monitorIndex is < 0)
        {
            return Fail(nameof(WindowErrorCode.InvalidMonitorIndex),
                $"placement monitor {monitorIndex} is not a monitor: indexes start at 0 (see get_monitors).", out errorCode, out error);
        }

        if (state == LaunchWindowState.Maximized && (snap != null || placement.Bounds != null))
        {
            return Fail(invalid, "placement.state 'maximized' fills the monitor, so it takes no snap or bounds (use monitor to pick the monitor).",
                out errorCode, out error);
        }

        var focus = placement.Focus == true;
        if (focus && state == LaunchWindowState.Minimized)
        {
            return Fail(invalid, "placement.focus cannot bring a window to the front that state 'minimized' minimizes.", out errorCode, out error);
        }

        var candidate = new LaunchPlacementPlan(
            snap, monitorIndex,
            placement.Bounds is { X: { } x, Y: { } y, Width: { } w, Height: { } h } ? new WindowRect(x, y, w, h) : null,
            state, focus);
        if (!candidate.ChangesGeometry && !candidate.Focus)
        {
            return Fail(invalid, "placement asks for nothing: give snap, bounds, monitor, state or focus: true, or leave placement out.",
                out errorCode, out error);
        }

        plan = candidate;
        errorCode = null;
        error = null;
        return true;
    }

    /// <summary>
    /// The plan of a window apply_layout launched for a placement (roadmap M3.F27, M6.8), as LayoutService arranges an open
    /// window. Its rules are the layout's own, not <see cref="TryCreate(LaunchPlacement, int?, out LaunchPlacementPlan?, out string?, out string?)"/>'s:
    /// <list type="bullet">
    /// <item>the monitor is always named by <paramref name="monitorKey"/> (its id, or its device name without one), so the
    /// placement service finds it again when the window appears;</item>
    /// <item>exactly one of: <paramref name="snap"/> (snapped on monitor <paramref name="monitorIndex"/>), <paramref name="bounds"/>
    /// (the window rect, <see cref="BoundsFrame.Outer"/>, with the same rectangle as fractions of the monitor's work area in
    /// <paramref name="relativeBounds"/>; at least 1 pixel each way), or neither with state maximized (maximized on monitor
    /// <paramref name="monitorIndex"/>);</item>
    /// <item>bounds with state maximized are the restore rectangle the window gets before it is maximized, which a launch
    /// tool's placement refuses; a snap is never maximized;</item>
    /// <item>focus with state minimized: the window stays minimized and is not brought to the front (minimized wins, as for an
    /// open window), where a launch tool's placement refuses the pair.</item>
    /// </list>
    /// </summary>
    /// <exception cref="ArgumentException">The arguments break one of these rules: a bug in the caller, never the preset's.</exception>
    public static LaunchPlacementPlan ForLayout(string monitorKey, int monitorIndex, SnapPosition? snap = null, WindowRect? bounds = null,
        RelativePosition? relativeBounds = null, LaunchWindowState? state = null, bool focus = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(monitorKey);
        ArgumentOutOfRangeException.ThrowIfNegative(monitorIndex);
        if (snap != null && bounds != null)
            throw new ArgumentException("A layout's launch is snapped or given bounds, not both.", nameof(bounds));

        if ((bounds == null) != (relativeBounds == null))
            throw new ArgumentException("A layout's bounds come with the same rectangle as fractions of the work area.", nameof(relativeBounds));

        if (bounds is { } rect && (rect.Width < 1 || rect.Height < 1))
            throw new ArgumentOutOfRangeException(nameof(bounds), rect, "A layout's bounds are at least 1 pixel wide and high.");

        if (snap != null && state == LaunchWindowState.Maximized)
            throw new ArgumentException("A layout's snapped launch is not maximized.", nameof(state));

        if (snap == null && bounds == null && state != LaunchWindowState.Maximized)
            throw new ArgumentException("A layout's launch without snap or bounds is maximized on its monitor.", nameof(state));

        // Minimized wins over focus: a window the layout minimizes is not brought to the front
        var focused = focus && state != LaunchWindowState.Minimized;
        return bounds != null
            ? new LaunchPlacementPlan(null, null, bounds, state, focused)
            {
                Frame = BoundsFrame.Outer, MonitorKey = monitorKey, RelativeBounds = relativeBounds
            }
            : new LaunchPlacementPlan(snap, monitorIndex, null, state, focused) { MonitorKey = monitorKey };
    }

    /// <summary>The members <paramref name="bounds"/> lacks, as "x", "x or y", "x, y or width" ...; empty when it has all four.</summary>
    private static string MissingMembers(PlacementBounds bounds)
    {
        var missing = new[] { ("x", bounds.X), ("y", bounds.Y), ("width", bounds.Width), ("height", bounds.Height) }
            .Where(member => member.Item2 is null)
            .Select(member => member.Item1)
            .ToList();
        return missing.Count switch
        {
            0 => string.Empty,
            1 => missing[0],
            _ => $"{string.Join(", ", missing[..^1])} or {missing[^1]}"
        };
    }

    private static bool Fail(string code, string message, out string? errorCode, out string? error)
    {
        errorCode = code;
        error = message;
        return false;
    }
}
