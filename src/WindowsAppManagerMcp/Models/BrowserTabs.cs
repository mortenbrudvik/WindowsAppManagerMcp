using System.Text.Json.Serialization;
using WindowsAppManagerMcp.Infrastructure;

namespace WindowsAppManagerMcp.Models;

/// <summary>What browser_navigate waits for after the browser started loading the page (roadmap M5.5).</summary>
[ToolEnum(ToolErrorReporting.InvalidArgumentsErrorCode, "waitUntil", NullMeans = "load")]
public enum BrowserWaitUntil
{
    /// <summary>The page's load event: the document and its images, styles and scripts are loaded (readyState complete).</summary>
    [JsonStringEnumMemberName("load")]
    Load,

    /// <summary>The page's DOMContentLoaded event: the document is parsed (readyState interactive or complete).</summary>
    [JsonStringEnumMemberName("domcontentloaded")]
    DomContentLoaded,

    /// <summary>Nothing: the call returns once the browser has started the navigation.</summary>
    [JsonStringEnumMemberName("none")]
    None
}

/// <summary>
/// Which browser and tab a tab tool works on (roadmap M5.5): a browser window, whose own debug port is found as the
/// reading tools find it (or <see cref="CdpPort"/>), or a debug port alone; and a tab id, or the tab the window shows.
/// </summary>
/// <param name="WindowHandle">The browser window, or null to name the browser by <see cref="CdpPort"/> alone.</param>
/// <param name="CdpPort">The DevTools port: with a window, it must be the window's process's; without one, its listener
/// is the browser, which the target policy and the browser check then apply to.</param>
/// <param name="TargetId">A tab id from list_tabs or an earlier result, or null for the tab the window shows.</param>
public sealed record BrowserTabTarget(nint? WindowHandle, int? CdpPort = null, string? TargetId = null);

/// <summary>
/// One tab of list_tabs (roadmap M5.5). WindowId is the browser's own id of the window the tab is in
/// (Browser.getWindowForTarget), not a window handle; InWindow is true for the tabs of the window named, when the window
/// could be bound to its browser window.
/// </summary>
public sealed record BrowserTabInfo(string TargetId, string Title, string Url, int? WindowId = null, bool? InWindow = null);

/// <summary>
/// list_tabs' result (roadmap M5.5). With a window: WindowBound says whether it was matched to its browser window (its
/// tabs then come first, with InWindow), and ShownTargetId is the tab it shows as the reads pick it: its title, and of
/// several tabs with it the one that is shown (CDP reports no active tab). TabCount is every page tab of the browser; at
/// most <see cref="MaxListedTabs"/> are listed
/// (Truncated).
/// </summary>
public sealed record BrowserTabsResult(
    bool Success,
    IReadOnlyList<BrowserTabInfo>? Tabs = null,
    int? TabCount = null,
    bool? Truncated = null,
    string? ShownTargetId = null,
    bool? WindowBound = null,
    int? CdpPort = null,
    string? Note = null,
    string? Error = null,
    string? ErrorCode = null)
{
    /// <summary>The most tabs one list_tabs result lists.</summary>
    public const int MaxListedTabs = 100;

    /// <summary>A failed result.</summary>
    public static BrowserTabsResult Failed(string errorCode, string error, int? cdpPort = null) =>
        new(false, CdpPort: cdpPort, Error: error, ErrorCode: errorCode);
}

/// <summary>activate_tab's result (roadmap M5.5): the tab brought to the front of its browser window.</summary>
public sealed record BrowserTabResult(
    bool Success,
    string? TargetId = null,
    string? Title = null,
    string? Url = null,
    int? CdpPort = null,
    string? Error = null,
    string? ErrorCode = null)
{
    /// <summary>A failed result.</summary>
    public static BrowserTabResult Failed(string errorCode, string error, int? cdpPort = null, string? targetId = null) =>
        new(false, targetId, CdpPort: cdpPort, Error: error, ErrorCode: errorCode);
}

/// <summary>What browser_navigate does (roadmap M5.5).</summary>
/// <param name="Url">The URL, already checked by <see cref="Services.BrowserNavigationUrls"/>.</param>
/// <param name="NewTab">Open a new tab for it instead of navigating the tab.</param>
/// <param name="WaitUntil">What to wait for once the navigation started.</param>
/// <param name="TimeoutMs">The time limit of the whole call.</param>
public sealed record BrowserNavigateOptions(string Url, bool NewTab, BrowserWaitUntil WaitUntil, int TimeoutMs, BrowserDialogAnswer? Dialog = null);

/// <summary>
/// browser_navigate's result (roadmap M5.5). LoadState is load or domcontentloaded (reached), sameDocument (a jump within
/// the page, which loads nothing), notWaited (waitUntil none), loading (the time ran out: WaitTimedOut, the page goes on
/// loading), failed (NavigationFailed: the tab shows the browser's error page) or download (NavigationFailed: the address
/// answered with a file, and the tab keeps its page). Url and Title are the page's once it loaded (after redirects);
/// HttpStatus the main document's status when the browser reports it. ErrorText is the browser's own reason when it could
/// not load the page (NavigationFailed). Dialog is the JavaScript dialog the navigation opened (a beforeunload prompt, a
/// dialog of the new page) when the dialog option answered it.
/// </summary>
public sealed record BrowserNavigateResult(
    bool Success,
    string? TargetId = null,
    string? Url = null,
    string? Title = null,
    int? HttpStatus = null,
    string? LoadState = null,
    long? ElapsedMs = null,
    bool? NewTab = null,
    int? CdpPort = null,
    string? ErrorText = null,
    BrowserOpenedDialog? Dialog = null,
    string? Error = null,
    string? ErrorCode = null)
{
    /// <summary>A failed result.</summary>
    public static BrowserNavigateResult Failed(string errorCode, string error, int? cdpPort = null, string? targetId = null) =>
        new(false, targetId, CdpPort: cdpPort, Error: error, ErrorCode: errorCode);
}

/// <summary>
/// What wait_for_browser waits for (roadmap M5.5): every condition given must hold at the same check. The URL, title and
/// text are matched as substrings, ignoring case; the selector is a CSS selector that must match an element.
/// </summary>
public sealed record BrowserWaitCondition(string? UrlContains, string? TitleContains, string? TextContains, string? Selector)
{
    /// <summary>True when no condition is given.</summary>
    public bool IsEmpty => UrlContains == null && TitleContains == null && TextContains == null && Selector == null;
}

/// <summary>
/// wait_for_browser's result (roadmap M5.5), shaped like wait_for_window's: Success is Satisfied; a wait that timed out
/// goes out with WaitTimedOut and the page as last seen (Url, Title).
/// </summary>
public sealed record BrowserWaitResult(
    bool Success,
    bool Satisfied = false,
    bool TimedOut = false,
    long ElapsedMs = 0,
    string? TargetId = null,
    string? Url = null,
    string? Title = null,
    int? CdpPort = null,
    string? Error = null,
    string? ErrorCode = null)
{
    /// <summary>A failed result.</summary>
    public static BrowserWaitResult Failed(string errorCode, string error, int? cdpPort = null, string? targetId = null) =>
        new(false, CdpPort: cdpPort, TargetId: targetId, Error: error, ErrorCode: errorCode);
}
