namespace WindowsAppManagerMcp.Models;

/// <summary>
/// Information about a single UI element.
/// </summary>
public record UIElementInfo(
    string? Name,
    string ControlType,
    string? AutomationId,
    BoundsDto Bounds,
    bool IsEnabled,
    bool IsOffscreen,
    IReadOnlyList<UIElementInfo>? Children = null
);

/// <summary>
/// Result of a UI element retrieval operation.
/// </summary>
public record UIElementResult(
    bool Success,
    long WindowHandle,
    string? WindowTitle,
    IReadOnlyList<UIElementInfo>? Elements,
    int TotalElementCount,
    int Depth,
    double ElapsedMilliseconds,
    string? Error = null,
    UIElementErrorCode ErrorCode = UIElementErrorCode.None
);

/// <summary>
/// Filter options for UI element retrieval.
/// </summary>
public record UIElementFilter(
    string[]? ControlTypes = null,
    bool InteractableOnly = false,
    bool VisibleOnly = true,
    int? MinWidth = null,
    int? MinHeight = null
);

/// <summary>
/// Error codes for UI element operations.
/// </summary>
public enum UIElementErrorCode
{
    None = 0,
    InvalidHandle = 1,
    WindowNotFound = 2,
    AutomationUnavailable = 3,
    ElementAccessFailed = 4,
    OperationTimedOut = 5,
    OperationException = 6
}
