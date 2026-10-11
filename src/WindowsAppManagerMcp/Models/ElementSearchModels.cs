using System.Text.Json.Serialization;
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

    /// <summary>Parts of the window could not be searched (their elements disappeared or their provider failed).</summary>
    PartsSkipped = 4
}

/// <summary>A point in physical screen pixels, the same space as click's coordinates.</summary>
public record ScreenPoint(int X, int Y);

/// <summary>
/// One element find_ui_elements found (roadmap M3.4). The fields after <paramref name="Patterns"/> are set only for
/// detail standard or full, and are left out when they do not apply.
/// </summary>
/// <param name="Ref">The element's ref, for later calls; null when the element has no runtime id. Serialized first.</param>
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
    string? LocalizedControlType = null
);

/// <summary>Result of find_ui_elements (roadmap M3.4).</summary>
/// <param name="WindowHandle">The window searched (the window of withinRef when only that was given).</param>
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
    AccessibilityWarmupInfo? AccessibilityWarmup = null
);

/// <summary>
/// What find_ui_elements searches for (roadmap M3.4). The criteria are combined with AND; at least one of
/// <paramref name="Name"/>, <paramref name="AutomationId"/>, <paramref name="ControlTypes"/> and
/// <paramref name="ClassName"/> is set (the tool checks it).
/// </summary>
/// <param name="WindowHandle">The window to search.</param>
/// <param name="WithinRef">Search only below this ref's element (it must belong to <paramref name="WindowHandle"/>).</param>
/// <param name="ControlTypes">Control type names (UiaControlTypes.Names); an element of any of them matches.</param>
/// <param name="MaxResults">How many matches to return (1-100).</param>
/// <param name="Timeout">How long the search and the reads of the matches may take.</param>
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
    TimeSpan? Timeout = null
);
