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
public record LayoutApplyResult(
    bool Success,
    string PresetName,
    int WindowsArranged = 0,
    int WindowsNotFound = 0,
    int WindowsLaunched = 0,
    List<string>? Errors = null,
    string? ErrorCode = null,
    List<FocusResult>? Focus = null,
    List<LayoutPlacementResult>? Placements = null
)
{
    /// <summary>The overall outcome: every placement applied, some of them, or none.</summary>
    public string Status => Success
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
/// The placement's launchCommand when it was run for this placement (launchMissing), whether or not the launch worked;
/// null when nothing was launched.
/// </param>
public sealed record LayoutPlacementResult(
    int Order,
    string Status,
    LayoutWindowRef? Window = null,
    bool? Launched = null,
    bool? Focused = null,
    string? ErrorCode = null,
    string? Error = null,
    string? LaunchCommand = null);

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
}
