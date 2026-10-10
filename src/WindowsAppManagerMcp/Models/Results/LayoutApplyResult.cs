namespace WindowsAppManagerMcp.Models.Results;

/// <summary>
/// Outcome of applying a layout preset.
/// </summary>
/// <param name="Success">True only when every placement was matched (or launched) and arranged.</param>
/// <param name="ErrorCode">The <see cref="LayoutErrorCode"/> of the first problem; <paramref name="Errors"/> lists all of them.</param>
public record LayoutApplyResult(
    bool Success,
    string PresetName,
    int WindowsArranged = 0,
    int WindowsNotFound = 0,
    int WindowsLaunched = 0,
    List<string>? Errors = null,
    string? ErrorCode = null
);
