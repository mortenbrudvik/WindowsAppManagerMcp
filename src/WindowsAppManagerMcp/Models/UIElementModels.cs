namespace WindowsAppManagerMcp.Models;

/// <summary>
/// Information about a single UI element.
/// </summary>
/// <param name="Bounds">The element's bounding box in physical screen pixels, or null when the element has
/// no position on screen (UI Automation reports an empty rectangle for it).</param>
/// <param name="Children">The returned elements inside this one, or null when there are none. Elements that
/// fail a filter are left out, and the elements inside them that pass are listed here instead.</param>
public record UIElementInfo(
    string? Name,
    string ControlType,
    string? AutomationId,
    BoundsDto? Bounds,
    bool IsEnabled,
    bool IsOffscreen,
    IReadOnlyList<UIElementInfo>? Children = null
);

/// <summary>
/// Result of a UI element retrieval operation.
/// </summary>
/// <param name="TotalElementCount">How many elements the result contains, nested ones included.</param>
/// <param name="Depth">The maxDepth that was applied: how many levels below the window were walked
/// (the window's children are level 1).</param>
/// <param name="SkippedElementCount">Elements, or lists of children, left out because they disappeared or their
/// provider failed while the tree was walked. Greater than zero means the tree may be incomplete.</param>
/// <param name="DepthReached">The deepest level the walk read (the window's children are level 1). When it
/// equals <paramref name="Depth"/>, there may be elements below the limit.</param>
/// <param name="Truncated">True when the tree is incomplete because the walk timed out or elements could
/// not be read. <paramref name="TruncationReason"/> says which. The maxDepth limit does not set it.</param>
/// <param name="TruncationReason">Why the tree is incomplete, or null when it is not.</param>
public record UIElementResult(
    bool Success,
    long WindowHandle,
    string? WindowTitle,
    IReadOnlyList<UIElementInfo>? Elements,
    int TotalElementCount,
    int Depth,
    double ElapsedMilliseconds,
    string? Error = null,
    UIElementErrorCode ErrorCode = UIElementErrorCode.None,
    int SkippedElementCount = 0,
    int DepthReached = 0,
    bool Truncated = false,
    UIElementTruncationReason? TruncationReason = null
);

/// <summary>
/// Why a UI element tree is incomplete.
/// </summary>
public enum UIElementTruncationReason
{
    /// <summary>The timeout passed during the walk; the elements read until then are returned.</summary>
    Timeout = 1,

    /// <summary>Some elements, or the children of some elements, could not be read (see SkippedElementCount).</summary>
    ElementsSkipped = 2
}

/// <summary>
/// Filter options for UI element retrieval. Every filter decides only whether an element itself is
/// returned: an element that fails one is left out, and the elements inside it that pass are
/// returned in its place. Only <see cref="VisibleOnly"/> leaves out a whole subtree.
/// </summary>
/// <param name="ControlTypes">Control type names to keep (see UiaControlTypes.Names), or null for all.</param>
/// <param name="InteractableOnly">Keep only elements that are enabled and either keyboard-focusable or support
/// an action pattern (Invoke, Toggle, Value, SelectionItem, ExpandCollapse, RangeValue).</param>
/// <param name="VisibleOnly">Leave out elements reported as offscreen, together with everything inside them.</param>
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
    OperationException = 6,

    /// <summary>A controlTypes entry is not a known control type name.</summary>
    InvalidControlType = 7,

    /// <summary>No element has keyboard focus, or there is no element at the point.</summary>
    ElementNotFound = 8,

    /// <summary>The point is not on any monitor.</summary>
    CoordinatesOutOfBounds = 9
}

/// <summary>
/// A detailed snapshot of one UI element, returned by get_focused_element and get_element_at_point.
/// </summary>
/// <param name="Bounds">The bounding box in physical screen pixels, or null when the element has no position on screen.</param>
/// <param name="FrameworkId">The UI framework that provides the element, for example Win32, WPF, XAML,
/// WinForm, DirectUI or Chrome.</param>
/// <param name="Patterns">The action patterns the element supports: Invoke, Toggle, Value, SelectionItem,
/// ExpandCollapse and RangeValue. Empty when it supports none.</param>
/// <param name="Value">The element's value through the Value pattern, at most
/// 200 characters. Null when it has none, and always null for password fields.</param>
/// <param name="ValueTruncated">True when <paramref name="Value"/> was cut to 200 characters.</param>
public record UIElementDetails(
    string? Name,
    string ControlType,
    string? AutomationId,
    string? ClassName,
    string? FrameworkId,
    BoundsDto? Bounds,
    bool IsEnabled,
    bool IsOffscreen,
    bool IsKeyboardFocusable,
    bool HasKeyboardFocus,
    bool IsPassword,
    IReadOnlyList<string> Patterns,
    string? Value = null,
    bool ValueTruncated = false
);

/// <summary>
/// An element above another one in the UI Automation tree.
/// </summary>
public record UIElementAncestor(
    string? Name,
    string ControlType,
    string? AutomationId,
    string? ClassName,
    BoundsDto? Bounds
);

/// <summary>
/// Result of get_focused_element.
/// </summary>
/// <param name="Element">The element that has keyboard focus, or null when the call failed.</param>
/// <param name="WindowHandle">The top-level window that contains the element, or null when it could not be
/// determined (for example when the element has no window of its own above it).</param>
/// <param name="WindowTitle">The title of <paramref name="WindowHandle"/>.</param>
/// <param name="ProcessId">The process that provides the element.</param>
/// <param name="ProcessName">The name of that process, without ".exe".</param>
public record FocusedElementResult(
    bool Success,
    UIElementDetails? Element,
    long? WindowHandle,
    string? WindowTitle,
    int? ProcessId,
    string? ProcessName,
    double ElapsedMilliseconds,
    string? Error = null,
    UIElementErrorCode ErrorCode = UIElementErrorCode.None
);

/// <summary>
/// Result of get_element_at_point.
/// </summary>
/// <param name="X">The point that was looked up, in physical screen pixels.</param>
/// <param name="Element">The element at the point, or null when the call failed.</param>
/// <param name="Ancestors">The elements above it, nearest first, up to the requested number and never above
/// the top-level window. Empty when none were requested.</param>
/// <param name="WindowHandle">The root of the window hit-tested at the point (WindowFromPoint, then its root
/// window). The hit test skips hidden windows and disabled child windows, but not a disabled top-level window
/// such as the owner of a modal dialog. The element's own window is used only when no window is hit; null
/// when neither is known.</param>
/// <param name="WindowTitle">The title of <paramref name="WindowHandle"/>.</param>
/// <param name="ProcessId">The process that provides the element.</param>
/// <param name="ProcessName">The name of that process, without ".exe".</param>
public record ElementAtPointResult(
    bool Success,
    int X,
    int Y,
    UIElementDetails? Element,
    IReadOnlyList<UIElementAncestor>? Ancestors,
    long? WindowHandle,
    string? WindowTitle,
    int? ProcessId,
    string? ProcessName,
    double ElapsedMilliseconds,
    string? Error = null,
    UIElementErrorCode ErrorCode = UIElementErrorCode.None
);
