using System.Text.Json.Serialization;
using WindowsAppManagerMcp.Infrastructure;
using WindowsAppManagerMcp.Services.Interfaces;

namespace WindowsAppManagerMcp.Models;

/// <summary>How find_ui_elements compares an element's name with its <c>name</c> argument (roadmap M3.4).</summary>
[ToolEnum(WindowsAppManagerMcp.Infrastructure.ToolErrorReporting.InvalidArgumentsErrorCode, "nameMatch value", NullMeans = "exact")]
public enum ElementNameMatch
{
    /// <summary>The whole name, ignoring case; searched by the provider.</summary>
    [JsonStringEnumMemberName("exact")]
    Exact,

    /// <summary>A part of the name, ignoring case.</summary>
    [JsonStringEnumMemberName("contains")]
    Contains,

    /// <summary>A .NET regular expression that must match somewhere in the name (100 ms per name at most).</summary>
    [JsonStringEnumMemberName("regex")]
    Regex
}

/// <summary>How much find_ui_elements reports about each element (roadmap M3.4).</summary>
[ToolEnum(WindowsAppManagerMcp.Infrastructure.ToolErrorReporting.InvalidArgumentsErrorCode, "detail level", NullMeans = "minimal")]
public enum ElementDetail
{
    /// <summary>Ref, name, control type, automation id, class, bounds, click point, enabled, offscreen and patterns.</summary>
    [JsonStringEnumMemberName("minimal")]
    Minimal,

    /// <summary>Minimal, plus the value, toggle, expand/collapse and selection state, focus and the password flag.</summary>
    [JsonStringEnumMemberName("standard")]
    Standard,

    /// <summary>Standard, plus framework, process, native window handle, help text, keys and localized control type.</summary>
    [JsonStringEnumMemberName("full")]
    Full
}

/// <summary>Why find_ui_elements did not return every match (roadmap M3.4).</summary>
public enum ElementSearchTruncation
{
    /// <summary>More elements matched than maxResults; matchCount is how many matched in all.</summary>
    MaxResults = 1,

    /// <summary>The timeout passed before the whole window was searched; matchCount counts the part searched.</summary>
    Timeout = 2,

    /// <summary>
    /// The search stopped after the provider had matched 2000 elements before the name filter (a contains or regex name
    /// with no other criterion matches nearly everything); matchCount counts the part searched. Add controlType,
    /// automationId or className.
    /// </summary>
    SearchLimit = 3,

    /// <summary>
    /// Parts of the window could not be searched (their elements disappeared or their provider failed), or, with a scope
    /// beyond the window, windows of the scope could not be read (they did not respond, or closed meanwhile).
    /// </summary>
    PartsSkipped = 4,

    /// <summary>
    /// scope desktop: more windows were open than one search covers (<see cref="Services.PopupService.DesktopWindowLimit"/>);
    /// scope windowAndPopups: more popups were open than <see cref="Services.PopupService.MaxPopups"/>. The ones furthest
    /// back were not searched (roadmap M3.5).
    /// </summary>
    WindowLimit = 5
}

/// <summary>A point in physical screen pixels, mouse_click's default (screen) space.</summary>
public record ScreenPoint(int X, int Y);

/// <summary>
/// One element find_ui_elements found (roadmap M3.4). The fields after <paramref name="Patterns"/> are set only for
/// detail standard or full, and are left out when they do not apply.
/// </summary>
/// <param name="Ref">The element's ref, for later calls; null when the element has no runtime id, except for an item
/// searchVirtualized found through its list, whose ref names it by the list's answer (roadmap M3.F37). Serialized first.</param>
/// <param name="AutomationId">Cut to <see cref="AppText.MaxLength"/> characters by the tools that return it
/// (<see cref="Bounded"/>, roadmap M3.F14); whole where a service reads it.</param>
/// <param name="ClickPoint">Where a click hits the element: UI Automation's clickable point, or else the centre of its
/// bounds. Null when it is offscreen or has no bounds.</param>
/// <param name="Patterns">The control patterns it supports, action patterns first (Invoke, Toggle, Value, SelectionItem,
/// ExpandCollapse, RangeValue), then ScrollItem, Scroll, Window, Text, Grid, Table, ItemContainer, VirtualizedItem.</param>
/// <param name="Value">standard: the Value pattern's text, at most 200 characters; never read for a password field.</param>
/// <param name="ValueTruncated">standard: true when <paramref name="Value"/> was cut; left out otherwise.</param>
/// <param name="IsPassword">standard: true for a password field (its value is never read); left out otherwise.</param>
/// <param name="ToggleState">standard: a check box's state (On, Off, Indeterminate).</param>
/// <param name="ExpandCollapseState">standard: Collapsed, Expanded, PartiallyExpanded or LeafNode.</param>
/// <param name="IsSelected">standard: whether a list item, tab or radio button is selected.</param>
/// <param name="HasKeyboardFocus">standard: whether it has keyboard focus.</param>
/// <param name="IsKeyboardFocusable">standard: whether it can take keyboard focus.</param>
/// <param name="FrameworkId">full: the UI framework, for example Win32, WPF, XAML, WinForm, DirectUI or Chrome.</param>
/// <param name="NativeWindowHandle">full: its own window handle, when it is a window of its own.</param>
/// <param name="WindowHandle">The top-level window the element is in, when that is not the result's window: a popup (a menu,
/// a dropdown, a dialog) of a windowAndPopups search, or another window of a desktop search (roadmap M3.5). Its ref is
/// registered under that window. Left out for an element of the window searched.</param>
/// <param name="Virtualized">searchVirtualized (roadmap M3.10): true for an item its list's ItemContainer pattern found that the
/// tree search did not: usually one the list has not realized (it is not in the tree yet: offscreen, no bounds, no
/// clickPoint), else a realized one scrolled out of view, which the tree search leaves out with visibleOnly. element_action
/// realize makes it real and scrolls it into view. wait_for_element sets it on the element of a ref that is still an item its
/// list has not realized. Left out otherwise.</param>
public record FoundElement(
    [property: JsonPropertyOrder(-1)] string? Ref,
    string? Name,
    string ControlType,
    string? AutomationId,
    string? ClassName,
    BoundsDto? Bounds,
    ScreenPoint? ClickPoint,
    bool IsEnabled,
    bool IsOffscreen,
    IReadOnlyList<string> Patterns,
    string? Value = null,
    bool? ValueTruncated = null,
    bool? IsPassword = null,
    UiaToggleState? ToggleState = null,
    UiaExpandCollapseState? ExpandCollapseState = null,
    bool? IsSelected = null,
    bool? HasKeyboardFocus = null,
    bool? IsKeyboardFocusable = null,
    string? FrameworkId = null,
    int? ProcessId = null,
    long? NativeWindowHandle = null,
    string? HelpText = null,
    string? AccessKey = null,
    string? AcceleratorKey = null,
    string? LocalizedControlType = null,
    long? WindowHandle = null,
    bool? Virtualized = null
)
{
    /// <summary>
    /// The element as the tools return it: its AutomationId cut to <see cref="AppText.MaxLength"/> characters (roadmap
    /// M3.F14). The search compared the whole one.
    /// </summary>
    public FoundElement Bounded() => this with { AutomationId = AppText.Cut(AutomationId) };
}

/// <summary>Result of find_ui_elements (roadmap M3.4).</summary>
/// <param name="WindowHandle">The window searched (the window of withinRef when only that was given).</param>
/// <param name="WindowTitle">Its title, cut to <see cref="AppText.MaxLength"/> characters by find_ui_elements
/// (<see cref="Bounded"/>, roadmap M3.F14).</param>
/// <param name="WithinRef">The ref whose element's descendants were searched, or null for the whole window: the ref asked
/// for, or the ref the element has now when it was found again with a ref of its own (the asked-for one was dropped).</param>
/// <param name="MatchCount">How many elements matched in the part of the window that was searched: all of it unless
/// <paramref name="TruncationReason"/> is Timeout, SearchLimit or PartsSkipped.</param>
/// <param name="Truncated">True when <paramref name="Elements"/> does not hold every match, or the search did not cover
/// the whole window; <paramref name="TruncationReason"/> says why.</param>
/// <param name="Elements">The first maxResults matches, nearest the window's top levels first, then in tree order.</param>
/// <param name="AccessibilityWarmup">Set when the window shows web content (Chromium, Electron or WebView2) and this call
/// waited for its accessibility tree before searching (roadmap M3.3); null otherwise, also for a window an earlier call
/// warmed up.</param>
/// <param name="Scope">The windows the search covered (roadmap M3.5): the scope asked for, or the default one chose
/// (windowAndPopups when the window, a window it owns or another window of its process was in the foreground, else
/// window). Null on a failure before the search.</param>
/// <param name="WindowsSearched">With scope windowAndPopups or desktop: how many windows were searched, the window itself
/// included. Left out for scope window.</param>
/// <param name="WindowsExcluded">With scope desktop: visible windows left out because the target policy denies their process
/// or they are the agent's own host windows; with scope windowAndPopups: popups left out for the same reasons. Left out when
/// none was.</param>
/// <param name="Virtualized">With searchVirtualized (roadmap M3.10): the lists searched through their ItemContainer pattern
/// and how many items only they found. Left out otherwise.</param>
/// <param name="RefMoved">True when withinRef was moved to a re-created element that alone fits its description (the
/// element it was given for is gone), with <paramref name="Warning"/>; left out otherwise.</param>
/// <param name="Warning">Why the result needs a check although it succeeded: withinRef moved.</param>
public record FindElementsResult(
    bool Success,
    long WindowHandle,
    string? WindowTitle,
    int MatchCount,
    bool Truncated,
    IReadOnlyList<FoundElement>? Elements,
    double ElapsedMilliseconds,
    string? WithinRef = null,
    ElementSearchTruncation? TruncationReason = null,
    string? Error = null,
    UIElementErrorCode ErrorCode = UIElementErrorCode.None,
    AccessibilityWarmupInfo? AccessibilityWarmup = null,
    ElementSearchScope? Scope = null,
    int? WindowsSearched = null,
    int? WindowsExcluded = null,
    VirtualizedSearchInfo? Virtualized = null,
    bool? RefMoved = null,
    string? Warning = null
)
{
    /// <summary>
    /// The result as find_ui_elements returns it: the window title and each element's AutomationId cut to
    /// <see cref="AppText.MaxLength"/> characters (roadmap M3.F14). The search compared the whole values, and the services
    /// that search for themselves (respond_to_dialog, menu_path, element_action) read them whole.
    /// </summary>
    public FindElementsResult Bounded() =>
        this with { WindowTitle = AppText.Cut(WindowTitle), Elements = Elements?.Select(e => e.Bounded()).ToList() };

    /// <summary>
    /// The matches the search left out for their process: an element of another process than its window's that the target
    /// policy refuses (an embedded WebView2, a child window of another app; see <see cref="Services.ForeignElements"/>). Null
    /// when it left none out. For the callers that need one match (read_element_text, element_action by criteria), so that
    /// they can say the policy refused it rather than that the search did not finish; find_ui_elements' output says it with
    /// <see cref="Warning"/> and truncationReason PartsSkipped, so this is not part of it.
    /// </summary>
    [JsonIgnore]
    public PolicyLeftOutMatches? PolicyLeftOut { get; init; }
}

/// <summary>The matches a search left out for the target policy (<see cref="FindElementsResult.PolicyLeftOut"/>).</summary>
/// <param name="Count">How many matches were left out.</param>
/// <param name="Warning">What the result's warning says about them: whose they are and why the policy refuses them.</param>
/// <param name="OtherwiseComplete">Whether the search covered the rest of its scope (no timeout, search limit, window limit or
/// other part skipped), so that these are the only matches it did not return.</param>
public sealed record PolicyLeftOutMatches(int Count, string Warning, bool OtherwiseComplete);

/// <summary>
/// What find_ui_elements' searchVirtualized searched (roadmap M3.10): the lists of the window (or of withinRef's element),
/// and how many matching items were found through their ItemContainer pattern that the tree search had not found.
/// </summary>
/// <param name="ItemsFound">The matches only the lists' ItemContainer pattern found (each has virtualized true).</param>
/// <param name="Containers">The lists searched for, at most <see cref="Services.ElementSearchService.MaxVirtualizedContainers"/>:
/// List, Tree, DataGrid and Table elements, withinRef's element itself when it is one.</param>
/// <param name="Note">Why nothing more could be searched (no list, no ItemContainer pattern, the timeout, a provider that
/// failed), and how many placeholders were not tested on controlType (roadmap M3.F37), or null. A lookup that did not search every list to its end also sets the result's truncation reason (Timeout or
/// PartsSkipped).</param>
public record VirtualizedSearchInfo(int ItemsFound, IReadOnlyList<VirtualizedContainer> Containers, string? Note = null);

/// <summary>A list searchVirtualized looked at (roadmap M3.10).</summary>
/// <param name="Ref">The list's ref (element_action scroll scrolls it; find_ui_elements withinRef searches it).</param>
/// <param name="ItemContainer">Whether it supports the ItemContainer pattern, so its items were searched realized or not. A
/// list without it can only be searched by scrolling it (element_action realize with maxScrolls).</param>
/// <param name="Scrollable">Whether it has a Scroll pattern of its own.</param>
public record VirtualizedContainer(string? Ref, string ControlType, string? Name, bool ItemContainer, bool Scrollable);

/// <summary>
/// What find_ui_elements searches for (roadmap M3.4). The criteria are combined with AND; at least one of
/// <paramref name="Name"/>, <paramref name="AutomationId"/>, <paramref name="ControlTypes"/> and
/// <paramref name="ClassName"/> is set (the tool checks it).
/// </summary>
/// <param name="WindowHandle">The window to search; zero only with <see cref="ElementSearchScope.Desktop"/> (no window
/// named: the desktop's windows are searched in z-order).</param>
/// <param name="WithinRef">Search only below this ref's element (it must belong to <paramref name="WindowHandle"/>).</param>
/// <param name="ControlTypes">Control type names (UiaControlTypes.Names); an element of any of them matches.</param>
/// <param name="MaxResults">How many matches to return (1-100).</param>
/// <param name="Timeout">How long the search and the reads of the matches may take.</param>
/// <param name="Scope">Which windows to search (roadmap M3.5): null for the default (windowAndPopups when the window or one
/// of its popups is in the foreground, else window); always the window alone with <paramref name="WithinRef"/>. Every
/// window is searched under the one <paramref name="Timeout"/>, <paramref name="MaxResults"/> and candidate limit.</param>
public record ElementQuery(
    nint WindowHandle,
    string? WithinRef = null,
    string? Name = null,
    ElementNameMatch NameMatch = ElementNameMatch.Exact,
    string? AutomationId = null,
    IReadOnlyList<string>? ControlTypes = null,
    string? ClassName = null,
    bool VisibleOnly = true,
    bool EnabledOnly = false,
    int MaxResults = 20,
    ElementDetail Detail = ElementDetail.Minimal,
    TimeSpan? Timeout = null,
    ElementSearchScope? Scope = null
)
{
    /// <summary>
    /// Whether a window with web content is warmed up before the search (roadmap M3.3; default true). wait_for_element
    /// (roadmap M3.8) warms up once, on its first check, and turns it off for the checks after it.
    /// </summary>
    public bool WarmUp { get; init; } = true;

    /// <summary>
    /// Also search the items of the window's lists that are not realized yet (roadmap M3.10; default false): after the tree
    /// search, the List, Tree, DataGrid and Table elements of the named window (or withinRef's element) that support the
    /// ItemContainer pattern are asked for items whose automation id (else whole name) equals the criterion
    /// (FindItemByProperty, compared by the provider, usually case-sensitively; when no item has the text exactly, it is asked
    /// for again in the usual other cases: roadmap M3.F19). The other criteria are tested on each item, but controlType not on
    /// a placeholder whose control type is Custom or unknown (roadmap M3.F37: File Explorer's). Only reads: nothing is realized
    /// or scrolled.
    /// Needs automationId, or name with nameMatch exact (<see cref="Services.ElementSearchService.VirtualizedArgumentError"/>).
    /// </summary>
    public bool SearchVirtualized { get; init; }

    /// <summary>
    /// Whether the windowAndPopups scope also searches the popups of the agent's own host (its terminal or editor, and the
    /// windows they own); default false: a read leaves them out and counts them in windowsExcluded, as the desktop scope
    /// leaves host windows out. element_action and menu_path set it: the input guard checks the window of the element they
    /// act on, and allowHostTarget lifts the host rule for a named host window and the windows it owns. Popups of an app the
    /// target policy denies are left out either way: they are never read.
    /// </summary>
    public bool IncludeHostPopups { get; init; }
}
