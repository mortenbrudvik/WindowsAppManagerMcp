using System.Text.Json.Serialization;
using WindowsAppManagerMcp.Infrastructure;
using WindowsAppManagerMcp.Services.Interfaces;

namespace WindowsAppManagerMcp.Models;

/// <summary>What element_action does to its element (roadmap M3.6).</summary>
[ToolEnum(ToolErrorReporting.InvalidArgumentsErrorCode, "action")]
public enum ElementActionKind
{
    /// <summary>Invoke pattern (a button, menu item or link); else the MSAA default action (UIA3 engine).</summary>
    [JsonStringEnumMemberName("invoke")]
    Invoke,

    /// <summary>What a click would do, through a pattern: Invoke, Toggle, SelectionItem, ExpandCollapse, then the MSAA default action.</summary>
    [JsonStringEnumMemberName("click")]
    Click,

    /// <summary>A guarded double click at the element's click point (mouse input, always).</summary>
    [JsonStringEnumMemberName("double_click")]
    DoubleClick,

    /// <summary>A guarded right click at the element's click point (mouse input, always).</summary>
    [JsonStringEnumMemberName("right_click")]
    RightClick,

    /// <summary>Brings the element's window to the front and gives the element keyboard focus.</summary>
    [JsonStringEnumMemberName("focus")]
    Focus,

    /// <summary>Replaces the element's value with value (mode value, type or paste).</summary>
    [JsonStringEnumMemberName("set_value")]
    SetValue,

    /// <summary>Adds value at the end of the element's value (mode value, type or paste).</summary>
    [JsonStringEnumMemberName("append_text")]
    AppendText,

    /// <summary>Toggle pattern: once, or until the state value names (on, off, indeterminate), at most three times.</summary>
    [JsonStringEnumMemberName("toggle")]
    Toggle,

    /// <summary>SelectionItem pattern: select this item only.</summary>
    [JsonStringEnumMemberName("select")]
    Select,

    /// <summary>SelectionItem pattern: add this item to a multiple selection.</summary>
    [JsonStringEnumMemberName("add_to_selection")]
    AddToSelection,

    /// <summary>SelectionItem pattern: remove this item from the selection.</summary>
    [JsonStringEnumMemberName("remove_from_selection")]
    RemoveFromSelection,

    /// <summary>ExpandCollapse pattern: expand (a combo box, tree item, menu).</summary>
    [JsonStringEnumMemberName("expand")]
    Expand,

    /// <summary>ExpandCollapse pattern: collapse.</summary>
    [JsonStringEnumMemberName("collapse")]
    Collapse,

    /// <summary>ScrollItem pattern: its container scrolls it into view.</summary>
    [JsonStringEnumMemberName("scroll_into_view")]
    ScrollIntoView,

    /// <summary>Scroll pattern of the element or its nearest scrollable ancestor: direction and amount.</summary>
    [JsonStringEnumMemberName("scroll")]
    Scroll,

    /// <summary>RangeValue pattern: sets the number in value (a slider or spinner).</summary>
    [JsonStringEnumMemberName("set_range_value")]
    SetRangeValue,

    /// <summary>VirtualizedItem pattern: makes a virtualized list item real.</summary>
    [JsonStringEnumMemberName("realize")]
    Realize,

    /// <summary>Window pattern: closes a window drawn inside an application (not a top-level window: close_window).</summary>
    [JsonStringEnumMemberName("window_close")]
    WindowClose,

    /// <summary>Window pattern: minimizes a window inside an application (not a top-level window: set_window_state).</summary>
    [JsonStringEnumMemberName("window_minimize")]
    WindowMinimize,

    /// <summary>Window pattern: maximizes a window inside an application (not a top-level window: set_window_state).</summary>
    [JsonStringEnumMemberName("window_maximize")]
    WindowMaximize,

    /// <summary>
    /// A path of menu items in value, such as "File>Save As" (roadmap M3.5): each item but the last is expanded, the last is
    /// invoked, each through this tool's own action with its guards, through the submenu windows the items open.
    /// </summary>
    [JsonStringEnumMemberName("menu_path")]
    MenuPath
}

/// <summary>What element_action may do when no pattern carries out the action (roadmap M3.6).</summary>
[ToolEnum(ToolErrorReporting.InvalidArgumentsErrorCode, "fallback", NullMeans = "none")]
public enum ElementActionFallback
{
    /// <summary>Nothing: the call fails with PatternNotSupported.</summary>
    [JsonStringEnumMemberName("none")]
    None,

    /// <summary>A guarded mouse click at the element's click point, after its window is brought to the front.</summary>
    [JsonStringEnumMemberName("mouse")]
    Mouse
}

/// <summary>How set_value and append_text put the text in (roadmap M3.6).</summary>
[ToolEnum(ToolErrorReporting.InvalidArgumentsErrorCode, "mode", NullMeans = "value")]
public enum ElementValueMode
{
    /// <summary>The Value pattern, without keyboard input; works on background windows.</summary>
    [JsonStringEnumMemberName("value")]
    Value,

    /// <summary>Keyboard: focus the element, select all (set_value) or go to the end (append_text), type the text.</summary>
    [JsonStringEnumMemberName("type")]
    Type,

    /// <summary>Keyboard: as type, but the text goes on the clipboard and is pasted with Ctrl+V (it replaces the clipboard).</summary>
    [JsonStringEnumMemberName("paste")]
    Paste
}

/// <summary>Which way element_action's scroll moves the content into view (roadmap M3.6).</summary>
[ToolEnum(ToolErrorReporting.InvalidArgumentsErrorCode, "direction")]
public enum ElementScrollDirection
{
    [JsonStringEnumMemberName("up")]
    Up,

    [JsonStringEnumMemberName("down")]
    Down,

    [JsonStringEnumMemberName("left")]
    Left,

    [JsonStringEnumMemberName("right")]
    Right
}

/// <summary>How far element_action's scroll moves (roadmap M3.6).</summary>
[ToolEnum(ToolErrorReporting.InvalidArgumentsErrorCode, "amount", NullMeans = "page")]
public enum ElementScrollAmount
{
    /// <summary>A page (the Scroll pattern's large increment).</summary>
    [JsonStringEnumMemberName("page")]
    Page,

    /// <summary>A line or a small step (the small increment).</summary>
    [JsonStringEnumMemberName("line")]
    Line
}

/// <summary>Whether an element_action call has finished.</summary>
public enum ElementActionStatus
{
    /// <summary>The action returned; before and after say what changed.</summary>
    Done,

    /// <summary>
    /// The action (Invoke, Toggle, ExpandCollapse, a close) had not returned when the timeout passed, and a new window of
    /// the application appeared: a modal dialog that the action opened and that it waits for. openedPopup names it; read
    /// and answer it, then read the element again.
    /// </summary>
    InvokedPendingModal
}

/// <summary>How element_action carried out its action.</summary>
public enum ElementActionMethod
{
    /// <summary>Nothing needed doing: the element was already in the asked-for state.</summary>
    None,

    Invoke,
    Toggle,
    Value,
    SelectionItem,
    ExpandCollapse,
    ScrollItem,
    Scroll,
    RangeValue,
    VirtualizedItem,
    Window,

    /// <summary>The MSAA default action (LegacyIAccessible, UIA3 engine only).</summary>
    LegacyIAccessible,

    /// <summary>UI Automation SetFocus.</summary>
    Focus,

    /// <summary>Guarded mouse input at the element's click point.</summary>
    Mouse,

    /// <summary>Guarded keyboard input into the focused element (set_value mode type or paste).</summary>
    Keyboard
}

/// <summary>
/// An element's state as element_action read it before and after the action (roadmap M3.6). A field is left out when the
/// element does not support the pattern that holds it, or the read failed.
/// </summary>
/// <param name="Value">The Value pattern's text, at most 200 characters; never read for a password field (see
/// <paramref name="IsPassword"/>).</param>
/// <param name="RangeValue">The RangeValue pattern's value.</param>
/// <param name="HorizontalScrollPercent">The Scroll pattern's position (0-100), of the element or its scrollable ancestor.</param>
/// <param name="WindowVisualState">The Window pattern's state: Normal, Maximized or Minimized.</param>
public sealed record ElementState(
    string? Name,
    bool IsEnabled,
    bool IsOffscreen,
    bool? HasKeyboardFocus = null,
    string? Value = null,
    bool? ValueTruncated = null,
    bool? IsPassword = null,
    UiaToggleState? ToggleState = null,
    UiaExpandCollapseState? ExpandCollapseState = null,
    bool? IsSelected = null,
    double? RangeValue = null,
    double? HorizontalScrollPercent = null,
    double? VerticalScrollPercent = null,
    UiaWindowVisualState? WindowVisualState = null);

/// <summary>Where a scroll left its container (roadmap M3.6).</summary>
/// <param name="HorizontalPercent">0-100, or null when the container cannot scroll horizontally.</param>
/// <param name="VerticalPercent">0-100, or null when it cannot scroll vertically.</param>
/// <param name="AtEnd">True when the container cannot scroll further in the direction asked.</param>
/// <param name="ContainerRef">The ref of the scrolled container when it is an ancestor of the element.</param>
public sealed record ElementScrollPosition(double? HorizontalPercent, double? VerticalPercent, bool AtEnd, string? ContainerRef = null);

/// <summary>A top-level window of the element's application that appeared during the action (roadmap M3.6).</summary>
public sealed record OpenedWindow(long Handle, string Title, string? ClassName, int ProcessId, string? ProcessName);

/// <summary>Result of element_action (roadmap M3.6).</summary>
/// <param name="Ref">The element's ref: the one given, the one the element has now when it was found again, or the one the
/// element criteria found.</param>
/// <param name="WindowHandle">The element's window.</param>
/// <param name="Status">Done, or InvokedPendingModal when the action is still waiting for a dialog it opened.</param>
/// <param name="Method">How the action was carried out: the pattern, Mouse, Keyboard or None.</param>
/// <param name="OutcomeVerified">True when the state read afterwards shows the action's effect (a toggle in the asked-for
/// state, the value read back, the item selected, ...), false when it does not, null when the action has no state to read
/// (invoke, click) or it could not be read (a password field).</param>
/// <param name="OpenedPopup">A new top-level window of the element's application that appeared during the action (a dialog,
/// menu or dropdown), the topmost one when several did.</param>
/// <param name="ReceivingWindow">The window the mouse or keyboard input went to, for a Mouse or Keyboard method.</param>
/// <param name="Steps">menu_path: the items opened, in order, with their refs and the submenu windows they opened (also on a
/// failure: the items done before it).</param>
/// <param name="RefMoved">True when the ref was moved to a re-created element that alone fits its description (the element
/// it was given for is gone): the action is refused with ElementStale until a read returns that element again. Left out
/// otherwise.</param>
/// <param name="Observed">With observe 'state' or 'screenshot' (roadmap M3.12): get_desktop_state's observation of the
/// element's window after the action; serialized last.</param>
public sealed record ElementActionResult(
    bool Success,
    string Action,
    string? Ref = null,
    long WindowHandle = 0,
    ElementActionStatus? Status = null,
    ElementActionMethod? Method = null,
    ElementState? Before = null,
    ElementState? After = null,
    bool? OutcomeVerified = null,
    ElementScrollPosition? Scroll = null,
    OpenedWindow? OpenedPopup = null,
    WindowSummary? ReceivingWindow = null,
    IReadOnlyList<string>? Warnings = null,
    bool? RefMoved = null,
    string? Error = null,
    string? ErrorCode = null,
    double ElapsedMilliseconds = 0,
    IReadOnlyList<MenuPathStep>? Steps = null,
    DesktopState? Observed = null);

/// <summary>What element_action is asked to do (roadmap M3.6); the tool builds it from its arguments.</summary>
/// <param name="Ref">The element, as a ref (the tool resolves element criteria to one first).</param>
/// <param name="Value">set_value and append_text: the text; set_range_value: the number; toggle: on, off or indeterminate (or
/// null: toggle once); menu_path: the path ("File>Save As"), which the tool carries out item by item.</param>
/// <param name="AllowHostTarget">Lifts the host guard for the element's window (the tool allows it only for a window named by
/// handle or ref).</param>
/// <param name="Timeout">How long the action may block (a dialog it opened), and the bound of each read.</param>
public sealed record ElementActionRequest(
    string Ref,
    ElementActionKind Action,
    string? Value = null,
    ElementValueMode Mode = ElementValueMode.Value,
    ElementScrollDirection? Direction = null,
    ElementScrollAmount Amount = ElementScrollAmount.Page,
    ElementActionFallback Fallback = ElementActionFallback.None,
    bool AllowHostTarget = false,
    TimeSpan? Timeout = null);
