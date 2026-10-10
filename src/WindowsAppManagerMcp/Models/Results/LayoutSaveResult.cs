namespace WindowsAppManagerMcp.Models.Results;

/// <summary>
/// Outcome of saving (or capturing and saving) a layout preset.
/// </summary>
/// <param name="Success">True when the preset was written to disk.</param>
/// <param name="Preset">The preset that was saved, or that would have been saved when <paramref name="Success"/> is false.</param>
/// <param name="Replaced">True when an existing preset with the same name was overwritten.</param>
/// <param name="ErrorMessage">Why the preset was not saved.</param>
/// <param name="ErrorCode">A <see cref="LayoutErrorCode"/> name when <paramref name="Success"/> is false.</param>
public record LayoutSaveResult(
    bool Success,
    LayoutPreset? Preset,
    bool Replaced = false,
    string? ErrorMessage = null,
    string? ErrorCode = null
);
