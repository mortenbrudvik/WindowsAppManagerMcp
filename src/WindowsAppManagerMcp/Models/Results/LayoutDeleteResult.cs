namespace WindowsAppManagerMcp.Models.Results;

/// <summary>
/// Outcome of deleting a layout preset.
/// </summary>
/// <param name="Success">True when the preset's file(s) were removed.</param>
/// <param name="Name">The preset name that was requested.</param>
/// <param name="ErrorMessage">Why the preset was not deleted.</param>
/// <param name="ErrorCode">A <see cref="LayoutErrorCode"/> name when <paramref name="Success"/> is false.</param>
public record LayoutDeleteResult(
    bool Success,
    string Name,
    string? ErrorMessage = null,
    string? ErrorCode = null
);
