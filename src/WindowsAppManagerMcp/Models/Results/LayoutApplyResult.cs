namespace WindowsAppManagerMcp.Models.Results;

/// <summary>
/// Outcome of applying a layout preset.
/// </summary>
/// <param name="Success">True only when every placement was matched (or launched) and arranged.</param>
/// <param name="ErrorCode">The <see cref="LayoutErrorCode"/> of the first problem; <paramref name="Errors"/> lists all of them.</param>
/// <param name="Focus">
/// One entry for each placement with focus set whose window was arranged, in order: what the verified
/// activation found (the same fields as focus_window). Null when no placement asked for focus.
/// </param>
public record LayoutApplyResult(
    bool Success,
    string PresetName,
    int WindowsArranged = 0,
    int WindowsNotFound = 0,
    int WindowsLaunched = 0,
    List<string>? Errors = null,
    string? ErrorCode = null,
    List<FocusResult>? Focus = null
);
