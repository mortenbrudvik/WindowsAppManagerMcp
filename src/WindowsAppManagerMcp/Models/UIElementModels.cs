using System.Text.Json.Serialization;
using WindowsAppManagerMcp.Infrastructure;

namespace WindowsAppManagerMcp.Models;

/// <summary>
/// Information about a single UI element.
/// </summary>
/// <param name="Bounds">The element's bounding box in physical screen pixels, or null when the element has
/// no position on screen (UI Automation reports an empty rectangle for it).</param>
/// <param name="Children">The returned elements inside this one, or null when there are none. Elements that
/// fail a filter are left out, and the elements inside them that pass are listed here instead.</param>
/// <param name="Depth">format=flat only: the element's level in the returned tree (1 for the elements listed directly under
/// the window). Null in the tree format, where the nesting shows it.</param>
/// <param name="Parent">format=flat only: the index in the flat list of the returned element this one is inside, or null
/// when it is listed directly under the window.</param>
/// <param name="FrameworkId">The UI framework that provides the element (for example Win32, WPF, XAML, WinForm, DirectUI or
/// Chrome, which is web content in a Chromium, Electron or WebView2 app), set only where it differs from that of the
/// returned element it is inside, or, for an element listed directly under the window, from the result's
/// <see cref="UIElementResult.FrameworkId"/> (roadmap M3.3). Null means the same framework as there.</param>
/// <param name="Ref">The element's ref (roadmap M3.4), for example "w4120035e17": later calls name the element by it. The same
/// element keeps its ref in every read of this server session, and a ref never silently names another element (see
/// <see cref="Services.Interfaces.IElementRegistry"/>). Null when the element has no runtime id, or the walk ran without the
/// element registry. Serialized first.</param>
public record UIElementInfo(
    string? Name,
    string ControlType,
    string? AutomationId,
    BoundsDto? Bounds,
    bool IsEnabled,
    bool IsOffscreen,
    IReadOnlyList<UIElementInfo>? Children = null,
    int? Depth = null,
    int? Parent = null,
    string? FrameworkId = null,
    [property: JsonPropertyOrder(-1)] string? Ref = null
)
{
    /// <summary>
    /// This element with its AutomationId, and its children's, cut to <see cref="AppText.MaxLength"/> characters as get_ui_elements and
    /// capture_with_elements return them (roadmap M3.F31); the element itself when nothing needed a cut.
    /// </summary>
    public UIElementInfo Bounded()
    {
        List<UIElementInfo>? children = null;
        for (var i = 0; i < (Children?.Count ?? 0); i++)
        {
            var child = Children![i].Bounded();
            if (!ReferenceEquals(child, Children[i]))
            {
                children ??= [.. Children];
                children[i] = child;
            }
        }

        var automationId = AppText.Cut(AutomationId);
        return children == null && automationId == AutomationId
            ? this
            : this with { AutomationId = automationId, Children = children ?? Children };
    }
}

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
/// <param name="Truncated">True when the tree is incomplete because the walk timed out, stopped at maxElements, or
/// elements could not be read. <paramref name="TruncationReason"/> says which. The maxDepth limit does not set it.</param>
/// <param name="TruncationReason">Why the tree is incomplete, or null when it is not.</param>
/// <param name="WindowState">The state of the window's top-level window when the walk ended (roadmap M2.9): a
/// Minimized window's elements are offscreen, so visibleOnly leaves them all out and the tree is empty. Null when the
/// window could not be read.</param>
/// <param name="Text">format=text only: the elements as one compact line each, indented two spaces per level (see
/// Tools.UIElementTextFormat); <paramref name="Elements"/> is null then.</param>
/// <param name="FrameworkId">The UI framework of the window itself (for example Win32, WPF or Chrome); the elements name
/// theirs only where it changes (<see cref="UIElementInfo.FrameworkId"/>). Null when the window did not report one.</param>
/// <param name="AccessibilityWarmup">Set when the window shows web content (Chromium, Electron or WebView2) and this call
/// waited for its accessibility tree (roadmap M3.3); null otherwise, also for a window an earlier call warmed up.</param>
/// <param name="OwnedWindows">The windows the window owns (an open dialog or popup) that UI Automation listed among its
/// children, which the walk left out with everything inside them (roadmap M3.F9): each is a window of its own, read by its
/// handle, and its elements get refs only there. Null when there were none.</param>
/// <param name="Warnings">Why a tree may not show what the window holds (roadmap M3.F16, D-UIA-07), one sentence each that
/// starts with a stable code and a colon (<see cref="UIElementWarnings"/>): the window is minimized, so its elements are
/// offscreen, or it runs at a higher integrity level than the server, so UI Automation cannot read it. Null when there is
/// nothing to say.</param>
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
    UIElementTruncationReason? TruncationReason = null,
    WindowState? WindowState = null,
    string? Text = null,
    string? FrameworkId = null,
    AccessibilityWarmupInfo? AccessibilityWarmup = null,
    IReadOnlyList<long>? OwnedWindows = null,
    IReadOnlyList<string>? Warnings = null
)
{
    /// <summary>
    /// This result with its window title and its elements' AutomationIds cut to <see cref="AppText.MaxLength"/> characters
    /// (<see cref="UIElementInfo.Bounded"/>, roadmap M3.F31); the result itself when nothing needed a cut.
    /// </summary>
    public UIElementResult Bounded()
    {
        List<UIElementInfo>? elements = null;
        for (var i = 0; i < (Elements?.Count ?? 0); i++)
        {
            var element = Elements![i].Bounded();
            if (!ReferenceEquals(element, Elements[i]))
            {
                elements ??= [.. Elements];
                elements[i] = element;
            }
        }

        var title = AppText.Cut(WindowTitle);
        return elements == null && title == WindowTitle ? this : this with { WindowTitle = title, Elements = elements ?? Elements };
    }
}

/// <summary>
/// The warnings a UI element walk can carry (roadmap M3.F16). Each one starts with a stable code and a colon, as the
/// captures' warnings do (<see cref="CaptureWarnings"/>), so an agent can match on the code; the rest of the sentence says
/// what it means and what to do.
/// </summary>
public static class UIElementWarnings
{
    /// <summary>
    /// The window, or the top-level window it is in, is minimized: UI Automation reports all its elements offscreen, so
    /// visibleOnly leaves them out, and the bounds of those returned are not where they will be once it is restored.
    /// </summary>
    public const string WindowMinimized = "WindowMinimized";

    /// <summary>
    /// The window's process runs at a higher integrity level than the server (for example as administrator): Windows
    /// keeps UI Automation from the server out of it, so its tree comes back empty or with only what Windows itself
    /// reports about the window. The same name as the input guard's code, which refuses input to it.
    /// </summary>
    public const string TargetElevated = "TargetElevated";
}

/// <summary>
/// Why a UI element tree is incomplete.
/// </summary>
public enum UIElementTruncationReason
{
    /// <summary>The timeout passed during the walk; the elements read until then are returned.</summary>
    Timeout = 1,

    /// <summary>Some elements, or the children of some elements, could not be read (see SkippedElementCount).</summary>
    ElementsSkipped = 2,

    /// <summary>
    /// The walk stopped once maxElements elements were returned (roadmap M3.1). It reads breadth first, so the
    /// elements left out are deeper in the tree, or later in the level where it stopped.
    /// </summary>
    MaxElements = 3
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
/// <param name="MaxElements">Stop the walk once this many elements are returned (truncationReason MaxElements), or null
/// for no limit. Not a filter: it bounds the walk, which reads breadth first so the levels nearest the window are kept.</param>
/// <param name="AccessibilityWarmupBudget">The longest wait for the accessibility tree of a window that shows web content
/// (roadmap M3.3), or null for <see cref="Services.AccessibilityWarmup.MaxWait"/>. Not a filter: the tools set it to a
/// share of their timeout (<see cref="Services.AccessibilityWarmup.BudgetFor"/>), so the walk keeps time to read.</param>
/// <param name="ActionableOnly">Keep only elements that support an action pattern (Invoke, Toggle, Value, SelectionItem,
/// ExpandCollapse, RangeValue: <see cref="Services.Interfaces.UiaPatterns.Actions"/>), enabled or not: the elements an agent
/// can act on with element_action. Unlike <see cref="InteractableOnly"/>, keyboard focus alone does not count, and a disabled
/// element is kept (capture_with_elements' marks show it dimmed; roadmap M3.11).</param>
/// <param name="StopGrace">How long a walk that was asked to stop may stay in a UI Automation call before it is abandoned
/// and what it read so far is returned, or null for the default (2 s). Not a filter: get_desktop_state passes less, so the
/// walk is back before its own deadline.</param>
public record UIElementFilter(
    string[]? ControlTypes = null,
    bool InteractableOnly = false,
    bool VisibleOnly = true,
    int? MinWidth = null,
    int? MinHeight = null,
    int? MaxElements = null,
    TimeSpan? AccessibilityWarmupBudget = null,
    bool ActionableOnly = false,
    TimeSpan? StopGrace = null
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

    /// <summary>
    /// No element has keyboard focus, there is no element at the point, or the element criteria of read_element_text or
    /// element_action (roadmap M3.9, M3.6) matched no element of the window.
    /// </summary>
    ElementNotFound = 8,

    /// <summary>The point is not on any monitor.</summary>
    CoordinatesOutOfBounds = 9,

    /// <summary>
    /// The window does not respond: Windows reports it hung, or it did not answer a message within 500 ms. Nothing
    /// was read, because a UI Automation read would block until it recovers (roadmap M3.1).
    /// </summary>
    NotResponding = 10,

    /// <summary>
    /// An element ref (roadmap M3.4) no longer names an element: the element is gone and could not be found again for
    /// certain, its window closed, the ref expired from the registry, or this server session never issued it (refs are
    /// not meant to survive a restart). Find the element again (find_ui_elements) and use the new ref.
    /// </summary>
    ElementStale = 11,

    /// <summary>
    /// An element ref's element is gone and several elements now fit its description, so the ref is not moved to any of
    /// them: a ref never silently names a different element (roadmap M3.4). Find the element again with a narrower query.
    /// </summary>
    AmbiguousElement = 12,

    /// <summary>
    /// The element supports none of the UI Automation patterns (or the structure) the call needs: read_table on an element
    /// that is neither a grid nor a list of rows (roadmap M3.9; nothing was read), or element_action on an element without
    /// the patterns the action needs and no mouse fallback allowed or possible (roadmap M3.6; nothing was done, the message
    /// lists the patterns it has).
    /// </summary>
    PatternNotSupported = 13,

    // element_action (roadmap M3.6). The tool also returns the codes of the parts it reuses, under their own names: the
    // element ref's (ElementStale, AmbiguousElement, NotResponding, OperationTimedOut), the input guard's for the element's
    // window (TargetDeniedByPolicy, TargetIsAgentHost, TargetElevated, BlockedByModal, SessionLocked, DesktopUnavailable),
    // the pause and the gate (PausedByUser, InputBusy), activation's, and the input tools' for a mouse or keyboard path.

    /// <summary>element_action: the element is disabled. Nothing was done.</summary>
    ElementNotEnabled = 14,

    /// <summary>element_action: the element is offscreen (scrolled out of view, in a collapsed or hidden part). Nothing was done.</summary>
    ElementOffscreen = 15,

    /// <summary>element_action: set_value, append_text or set_range_value on a read-only element. Nothing was done.</summary>
    ElementReadOnly = 16,

    /// <summary>
    /// element_action's keyboard path (set_value mode type or paste): after focusing the element, keyboard focus was not on
    /// it, so no key was sent (select-all would have reached another control); or focus moved off it to another control
    /// later, which the input guard's recheck before every batch of keys caught (roadmap M3.F17), so no more keys were sent.
    /// </summary>
    FocusNotOnElement = 17,

    /// <summary>
    /// element_action: the application reported an error for the action (the element refused it, or it is not available in
    /// its current state). Whether anything changed is in before/after.
    /// </summary>
    ActionFailed = 18,

    /// <summary>
    /// element_action: the action was sent but had not returned when the timeout passed, and no new window of the
    /// application appeared. It may still take effect: read the element's state before you repeat it.
    /// </summary>
    ActionTimedOut = 19,

    /// <summary>
    /// element_action: earlier actions on the application are still blocked (a dialog they opened is still open), so this
    /// one was not started. Deal with the dialog first.
    /// </summary>
    ActionsPending = 20,

    // respond_to_dialog and file_dialog (roadmap M3.5, M3.14). They act through element_action, so they also return its
    // codes and the ones it reuses (PausedByUser, TargetDeniedByPolicy, BlockedByModal, ElementNotFound, AmbiguousElement...).

    /// <summary>
    /// respond_to_dialog, file_dialog: no dialog was found: the window in front (or the window named) is no dialog box and
    /// has no open dialog, or (file_dialog) the dialog has no file name box, so it is no Open or Save As dialog. Also, for a
    /// respond_to_dialog press, a window named that is no dialog and that no modal dialog disables: it stands only for its
    /// modal dialog, so an open dialog that leaves it enabled is pressed only by its own handle (the error names it).
    /// Nothing was done.
    /// </summary>
    DialogNotFound = 21,

    /// <summary>
    /// file_dialog: the path was typed and the dialog's default button pressed, but the dialog did not close: it showed a
    /// prompt that file_dialog does not answer (the result's prompt has its text and buttons: answer it with
    /// respond_to_dialog), it navigated into a folder, or it was still open when the timeout passed.
    /// </summary>
    DialogNotCompleted = 22,

    /// <summary>
    /// file_dialog with action save: the file exists and overwrite was not true. Nothing was typed when the server saw the
    /// file first; when the application asked whether to replace it, the answer was No and the dialog is still open.
    /// </summary>
    FileExists = 23,

    /// <summary>
    /// The user's target policy denies the window's app (its process, or for a UWP frame the app it hosts), so none of its
    /// UI elements was read: get_ui_elements, find_ui_elements, get_focused_element, get_element_at_point, read_element_text,
    /// read_table, wait_for_element and get_active_popups refuse it as the captures do, before any warm-up or read. Inside a
    /// window the policy allows, an element of a denied app's process (an embedded WebView2, another app's child window) is
    /// refused by its ref the same way (read_element_text, read_table, wait_for_element, element_action), and so are criteria
    /// whose only matches are such elements. get_ui_elements' walk (and capture_with_elements') leaves it out with a warning
    /// of this code; find_ui_elements leaves it out of the matches as a part skipped (PartsSkipped, with a warning), and
    /// read_table returns its cell as null with a warning. The same name as the input guard's code; no tool argument changes it.
    /// </summary>
    TargetDeniedByPolicy = 24,

    /// <summary>
    /// get_active_popups without a window named: the window in front is the agent's own host (its terminal, editor or chat
    /// app), which is not read unless it is named, as get_desktop_state does not read it. Nothing was listed or read. The
    /// same name as the input guard's code.
    /// </summary>
    TargetIsAgentHost = 25
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
/// <param name="Ref">The element's ref (roadmap M3.4), as in <see cref="UIElementInfo.Ref"/>; null when the element has no
/// runtime id or no window was found for it. Serialized first.</param>
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
    bool ValueTruncated = false,
    [property: JsonPropertyOrder(-1)] string? Ref = null
)
{
    /// <summary>This element with its AutomationId cut to <see cref="AppText.MaxLength"/> characters (roadmap M3.F31); itself when it needed none.</summary>
    public UIElementDetails Bounded() => AppText.Cut(AutomationId) is var id && id == AutomationId ? this : this with { AutomationId = id };
}

/// <summary>
/// An element above another one in the UI Automation tree.
/// </summary>
/// <param name="FrameworkId">The UI framework that provides the ancestor (for example Win32, WPF or Chrome).</param>
public record UIElementAncestor(
    string? Name,
    string ControlType,
    string? AutomationId,
    string? ClassName,
    BoundsDto? Bounds,
    string? FrameworkId = null
)
{
    /// <summary>This ancestor with its AutomationId cut to <see cref="AppText.MaxLength"/> characters (roadmap M3.F31); itself when it needed none.</summary>
    public UIElementAncestor Bounded() => AppText.Cut(AutomationId) is var id && id == AutomationId ? this : this with { AutomationId = id };
}

/// <summary>
/// Result of get_focused_element.
/// </summary>
/// <param name="Element">The element that has keyboard focus, or null when the call failed.</param>
/// <param name="WindowHandle">The top-level window that contains the element, or null when it could not be
/// determined (for example when the element has no window of its own above it).</param>
/// <param name="WindowTitle">The title of <paramref name="WindowHandle"/>.</param>
/// <param name="ProcessId">The process that provides the element.</param>
/// <param name="ProcessName">The name of that process, without ".exe".</param>
/// <param name="AccessibilityWarmup">Set when the foreground window shows web content (Chromium, Electron or WebView2) and
/// this call waited for its accessibility tree first (roadmap M3.3); null otherwise.</param>
public record FocusedElementResult(
    bool Success,
    UIElementDetails? Element,
    long? WindowHandle,
    string? WindowTitle,
    int? ProcessId,
    string? ProcessName,
    double ElapsedMilliseconds,
    string? Error = null,
    UIElementErrorCode ErrorCode = UIElementErrorCode.None,
    AccessibilityWarmupInfo? AccessibilityWarmup = null
)
{
    /// <summary>
    /// This result with its window title and its element's AutomationId cut to <see cref="AppText.MaxLength"/> characters, as
    /// get_focused_element returns them (roadmap M3.F31); the result itself when nothing needed a cut.
    /// </summary>
    public FocusedElementResult Bounded()
    {
        var element = Element?.Bounded();
        var title = AppText.Cut(WindowTitle);
        return ReferenceEquals(element, Element) && title == WindowTitle ? this : this with { Element = element, WindowTitle = title };
    }
}

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
/// <param name="AccessibilityWarmup">Set when the window at the point shows web content (Chromium, Electron or WebView2)
/// and this call waited for its accessibility tree first (roadmap M3.3); null otherwise.</param>
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
    UIElementErrorCode ErrorCode = UIElementErrorCode.None,
    AccessibilityWarmupInfo? AccessibilityWarmup = null
)
{
    /// <summary>
    /// This result with its window title and the AutomationIds of its element and ancestors cut to <see cref="AppText.MaxLength"/>
    /// characters, as get_element_at_point returns them (roadmap M3.F31); the result itself when nothing needed a cut.
    /// </summary>
    public ElementAtPointResult Bounded()
    {
        var element = Element?.Bounded();
        var ancestors = Ancestors?.Select(a => a.Bounded()).ToList();
        var title = AppText.Cut(WindowTitle);
        var ancestorsCut = ancestors != null && ancestors.Where((a, i) => !ReferenceEquals(a, Ancestors![i])).Any();
        return ReferenceEquals(element, Element) && !ancestorsCut && title == WindowTitle
            ? this
            : this with { Element = element, Ancestors = ancestorsCut ? ancestors : Ancestors, WindowTitle = title };
    }
}

/// <summary>
/// What the accessibility warm-up of a window with web content did (roadmap M3.3). Chromium, and the Electron and WebView2
/// apps built on it, build the accessibility tree of a page only once a UI Automation client asks for it, so the first
/// read of such a window can see an empty document. The server asks, then waits a moment for the document to fill.
/// </summary>
/// <param name="Status">Ready: the web content has elements. Partial: it has some, but more were still being added when
/// the wait ended. NotReady: no web content came within the wait, so what was read shows the app's frame, not its page.</param>
/// <param name="Host">Chromium (a Chromium browser, an Electron or a CEF app) or WebView2 (web content inside another app).</param>
/// <param name="WaitedMs">How long the call waited for the web content, in milliseconds (0 when it was there at once).</param>
/// <param name="Polls">How many times the web content was checked.</param>
/// <param name="DocumentChildCount">How many elements the page's documents had at the last check, or null when no document
/// was found.</param>
/// <param name="Warning">What it means and what to do, when <paramref name="Status"/> is not Ready.</param>
public record AccessibilityWarmupInfo(
    AccessibilityWarmupStatus Status,
    WebContentHost Host,
    int WaitedMs,
    int Polls,
    int? DocumentChildCount,
    string? Warning = null
);

/// <summary>The outcome of an accessibility warm-up (<see cref="AccessibilityWarmupInfo"/>).</summary>
public enum AccessibilityWarmupStatus
{
    /// <summary>The page's document has elements, and their number had stopped changing (or they were there at once).</summary>
    Ready = 1,

    /// <summary>The document has elements, but their number was still changing when the wait ended.</summary>
    Partial = 2,

    /// <summary>The document had no elements, or no document was found, when the wait ended.</summary>
    NotReady = 3
}

/// <summary>What kind of program shows the web content of a window (<see cref="AccessibilityWarmupInfo"/>).</summary>
public enum WebContentHost
{
    /// <summary>A Chromium-based browser (Chrome, Edge, Brave, ...), an Electron app (VS Code, Slack, Discord, ...) or a CEF app.</summary>
    Chromium = 1,

    /// <summary>A WebView2 control (msedgewebview2.exe) inside another app, such as the new Outlook or Teams.</summary>
    WebView2 = 2
}
