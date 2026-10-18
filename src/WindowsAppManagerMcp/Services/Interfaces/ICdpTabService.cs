using WindowsAppManagerMcp.Models;

namespace WindowsAppManagerMcp.Services.Interfaces;

/// <summary>
/// The CDP side of the tab tools (roadmap M5.5): list a browser's tabs with the window each is in, bring a tab to the
/// front, open a tab, navigate a tab and wait for its load, and wait for a page condition. Every call opens its own
/// DevTools connections and closes them before it returns (M5.2: no pool), and is bounded by its timeout; failures come
/// back as a failed <see cref="CdpResult{T}"/>, and only the caller's cancellation throws.
/// </summary>
/// <remarks>
/// The calls that change the browser (activate, open, navigate) take the input gate through <see cref="CdpGate"/> just
/// around the command that changes it, and release it before they wait for the page, so a slow page never holds up the
/// input and window tools. Every tab list is used, and every connection gets a command, only while the browser's process
/// still listens on the port (review F4): a tab's connection and the browser's connection for a tab are tied to the browser
/// that listed the tab (<see cref="CdpTarget.OwnerProcessId"/>), and fail with CdpPortOwnerMismatch, nothing sent, when the
/// port changed hands or its listener cannot be read.
/// </remarks>
public interface ICdpTabService
{
    /// <summary>
    /// Lists the page targets (tabs) of the browser on <paramref name="port"/>, which must belong to process
    /// <paramref name="processId"/> (CdpPortOwnerMismatch otherwise, also when its owner cannot be read). With
    /// <paramref name="window"/>, binds it to its browser window as the reads do (<see cref="Services.CdpWindowBinder"/>):
    /// the listing says which tabs are in it and which one it shows, picked as the reads pick it
    /// (<see cref="ICdpService.FindTargetForWindowAsync"/>: its title, and of several tabs with it the one that is shown).
    /// </summary>
    Task<CdpResult<CdpTabListing>> ListTabsAsync(
        int port, int processId, CdpWindow? window, TimeSpan timeout, CancellationToken cancellationToken = default);

    /// <summary>
    /// Brings <paramref name="target"/> to the front of its browser window (Target.activateTarget over the browser's
    /// DevTools connection); the browser may also bring that window to the front. Returns the tab as listed.
    /// </summary>
    Task<CdpResult<CdpTarget>> ActivateAsync(
        CdpTarget target, CdpGate gate, TimeSpan timeout, CancellationToken cancellationToken = default);

    /// <summary>
    /// Opens a new tab on about:blank in the browser on <paramref name="port"/> (Target.createTarget over the browser's
    /// DevTools connection), which must belong to process <paramref name="processId"/>. The browser puts it in its most
    /// recently active window and shows it there. Returns the new tab as /json/list lists it.
    /// </summary>
    Task<CdpResult<CdpTarget>> OpenTabAsync(
        int port, int processId, CdpGate gate, TimeSpan timeout, CancellationToken cancellationToken = default);

    /// <summary>
    /// Navigates <paramref name="target"/> to <paramref name="url"/> (Page.navigate) and waits for
    /// <paramref name="waitUntil"/>. NavigationFailed with the browser's errorText when it could not load the page;
    /// CdpDialogOpen when a dialog (such as beforeunload) blocks it, unless <paramref name="dialogAnswer"/> answers the first
    /// dialog it opens (<see cref="CdpNavigation.Dialog"/>; roadmap M5.8, dialogs). A load that does not finish in time is a
    /// success with <see cref="CdpNavigation.TimedOut"/>: the page goes on loading.
    /// </summary>
    Task<CdpResult<CdpNavigation>> NavigateAsync(
        CdpTarget target, string url, BrowserWaitUntil waitUntil, CdpGate gate, TimeSpan timeout,
        CancellationToken cancellationToken = default, BrowserDialogAnswer? dialogAnswer = null);

    /// <summary>
    /// Checks <paramref name="condition"/> on <paramref name="target"/>'s page until it holds or
    /// <paramref name="timeout"/> has passed, over one connection (each check is one Runtime.evaluate). A wait that ends
    /// without the condition is a success with <see cref="CdpWaitOutcome.Satisfied"/> false; a dialog, a closed tab or a
    /// selector that is not valid CSS is a failure.
    /// </summary>
    /// <param name="minimumCheckTime">The least time the connect and one check get, also when <paramref name="timeout"/>
    /// is shorter (0 checks once).</param>
    Task<CdpResult<CdpWaitOutcome>> WaitAsync(
        CdpTarget target, BrowserWaitCondition condition, TimeSpan timeout, TimeSpan minimumCheckTime,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Takes the input gate for a command that changes the browser, or refuses (a ToolErrorException: PausedByUser,
/// InputBusy); the result is disposed to release it.
/// </summary>
public delegate Task<IDisposable> CdpGate(CancellationToken cancellationToken);

/// <summary>A tab of <see cref="ICdpTabService.ListTabsAsync"/>: the target and the browser window it is in, when known.</summary>
public sealed record CdpTab(CdpTarget Target, int? WindowId, bool InWindow);

/// <summary>
/// What <see cref="ICdpTabService.ListTabsAsync"/> found.
/// </summary>
/// <param name="Tabs">Every page tab, those in the window first.</param>
/// <param name="WindowBound">Whether the window was matched to its browser window; false without a window.</param>
/// <param name="Shown">The tab the window shows, picked as the reads pick it (its title, and of several tabs with it the one
/// that is shown); else null.</param>
/// <param name="ShownError">Why no tab could be told as the shown one (CdpTargetNotFound, CdpTargetAmbiguous).</param>
public sealed record CdpTabListing(IReadOnlyList<CdpTab> Tabs, bool WindowBound, CdpTarget? Shown, string? ShownError);

/// <summary>
/// What <see cref="ICdpTabService.NavigateAsync"/> did: the page's URL and title when it was last seen, its HTTP status
/// when known, and how far it loaded (<see cref="BrowserNavigateResult.LoadState"/>); with <paramref name="ErrorText"/>,
/// the browser's reason why it could not load the page (net::ERR_NAME_NOT_RESOLVED and the like).
/// </summary>
public sealed record CdpNavigation(
    string? Url, string? Title, int? HttpStatus, string LoadState, bool TimedOut, string? ErrorText = null, CdpAnsweredDialog? Dialog = null);

/// <summary>What <see cref="ICdpTabService.WaitAsync"/> saw last: whether the condition held, and the page's URL and title.</summary>
public sealed record CdpWaitOutcome(bool Satisfied, string? Url, string? Title, string? Detail);
