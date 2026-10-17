namespace WindowsAppManagerMcp.Models.Results;

/// <summary>
/// Outcome of applying a layout preset.
/// </summary>
/// <param name="Success">True only when every placement was matched (or launched) and arranged, and focused where it asks to be.</param>
/// <param name="ErrorCode">The error code of the first problem; <paramref name="Errors"/> lists all of them.</param>
/// <param name="Focus">
/// One entry for each placement with focus set whose window was arranged, in order: what the verified
/// activation found (the same fields as focus_window). Null when there is none: no placement asked for focus, or
/// no window of such a placement was arranged. apply_layout returns it as focus.
/// </param>
/// <param name="Placements">What happened to each placement, in placement order; null when the preset was not applied at all.</param>
/// <param name="DryRun">A dry run (roadmap M6.8): the placements say what would happen, and nothing was changed.</param>
public record LayoutApplyResult(
    bool Success,
    string PresetName,
    int WindowsArranged = 0,
    int WindowsNotFound = 0,
    int WindowsLaunched = 0,
    List<string>? Errors = null,
    string? ErrorCode = null,
    List<FocusResult>? Focus = null,
    List<LayoutPlacementResult>? Placements = null,
    bool DryRun = false
)
{
    /// <summary>The overall outcome: every placement applied, some of them, or none; dryRun for a dry run.</summary>
    public string Status => DryRun
        ? LayoutApplyStatus.DryRun
        : Success
            ? LayoutApplyStatus.Applied
            : WindowsArranged > 0 ? LayoutApplyStatus.Partial : LayoutApplyStatus.Failed;
}

/// <summary>The values of <see cref="LayoutApplyResult.Status"/>.</summary>
public static class LayoutApplyStatus
{
    /// <summary>Every placement was arranged (and focused where it asks to be).</summary>
    public const string Applied = "applied";

    /// <summary>Some placements were arranged, others were not (or not focused).</summary>
    public const string Partial = "partial";

    /// <summary>No window was arranged.</summary>
    public const string Failed = "failed";

    /// <summary>A dry run (roadmap M6.8): nothing was arranged; the placements say what would be.</summary>
    public const string DryRun = "dryRun";
}

/// <summary>What apply_layout did with one placement.</summary>
/// <param name="Order">The placement's order in the preset.</param>
/// <param name="Status">One of <see cref="LayoutPlacementStatus"/>.</param>
/// <param name="Window">The window the placement was given; null when none was.</param>
/// <param name="Launched">True when the window was launched for this placement (launchMissing); null otherwise.</param>
/// <param name="Focused">For a placement with focus: whether its window is in front; null otherwise.</param>
/// <param name="ErrorCode">Why the placement was not (fully) applied; null when it was.</param>
/// <param name="Error">The reason, in words.</param>
/// <param name="LaunchCommand">
/// What launchMissing started for this placement (its program, or an app's shell:AppsFolder entry), whether or not the launch
/// worked, or in a dry run would start; null when nothing was launched.
/// </param>
/// <param name="Target">
/// A dry run's target (roadmap M6.8): where the window would go, in virtual-screen pixels (the window rect; a snap position's
/// visible frame; for a maximized placement the rectangle it is given before it is maximized). Null outside a dry run.
/// </param>
/// <param name="MonitorFallback">
/// How the placement's monitor was found when the monitor it saved is not connected (roadmap M6.8): "name" (another monitor
/// with its EDID name) or "index" (its monitor index). Null when the saved monitor was found, or none was saved.
/// </param>
public sealed record LayoutPlacementResult(
    int Order,
    string Status,
    LayoutWindowRef? Window = null,
    bool? Launched = null,
    bool? Focused = null,
    string? ErrorCode = null,
    string? Error = null,
    string? LaunchCommand = null,
    BoundsDto? Target = null,
    string? MonitorFallback = null);

/// <summary>The window a placement was given: its handle, title and process name.</summary>
public sealed record LayoutWindowRef(long Handle, string Title, string ProcessName)
{
    /// <summary>The reference of <paramref name="window"/>.</summary>
    public static LayoutWindowRef From(WindowInfo window) =>
        new(window.Handle.ToInt64(), window.Title, window.ProcessName);
}

/// <summary>The values of <see cref="LayoutPlacementResult.Status"/>.</summary>
public static class LayoutPlacementStatus
{
    /// <summary>
    /// The window was arranged. An errorCode means it was not brought to the front, or not minimized, or (a launched window,
    /// roadmap M3.F27) its app moved it back and it was not placed again.
    /// </summary>
    public const string Applied = "applied";

    /// <summary>No open window matched, and none was launched.</summary>
    public const string NotFound = "notFound";

    /// <summary>The placement has no criterion the chosen matchBy can use, so it was skipped.</summary>
    public const string NotApplicable = "notApplicable";

    /// <summary>The placement could not be applied: its monitor does not exist, its launch failed, or the window could not be arranged.</summary>
    public const string Failed = "failed";

    /// <summary>A dry run (roadmap M6.8): the window was found and would be arranged.</summary>
    public const string WouldApply = "wouldApply";

    /// <summary>A dry run (roadmap M6.8): no window matched, and launchMissing would start one.</summary>
    public const string WouldLaunch = "wouldLaunch";
}

/// <summary>The values of <see cref="LayoutPlacementResult.MonitorFallback"/> (roadmap M6.8).</summary>
public static class LayoutMonitorFallback
{
    /// <summary>The saved monitor is not connected; the one monitor with its EDID name was used.</summary>
    public const string Name = "name";

    /// <summary>The saved monitor is not connected; the placement's monitor index was used.</summary>
    public const string Index = "index";
}

/// <summary>How apply_layout applies a preset (roadmap M6.8).</summary>
/// <param name="DryRun">Plan only: say what each placement would do, and change, focus and launch nothing.</param>
/// <param name="Placements">
/// Only these placements, by their 0-based place in the preset (as get_layout lists them); null for all. Every placement is
/// still matched, so each of these gets the window it would get in a full apply.
/// </param>
/// <param name="LaunchRefusal">
/// Why the user has not confirmed the launches of an agent-written preset (declined, or the client cannot ask), for the error
/// of each placement that would launch; null when nobody was asked.
/// </param>
public sealed record LayoutApplyOptions(bool DryRun = false, IReadOnlyList<int>? Placements = null, string? LaunchRefusal = null);
