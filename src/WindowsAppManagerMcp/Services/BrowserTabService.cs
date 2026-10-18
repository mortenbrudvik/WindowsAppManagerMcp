using System.Diagnostics;
using WindowsAppManagerMcp.Infrastructure;
using WindowsAppManagerMcp.Models;
using WindowsAppManagerMcp.Services.Interfaces;

namespace WindowsAppManagerMcp.Services;

/// <summary>
/// list_tabs, activate_tab, browser_navigate and wait_for_browser (roadmap M5.5): finds the browser and its debug port for
/// a window (as the reading tools do since M5.3) or a port, checks it against the target policy, finds the tab, and leaves
/// the CDP work to <see cref="ICdpTabService"/>.
/// </summary>
/// <remarks>
/// The target policy applies to every CDP action, also with an explicit cdpPort (the reads exempt an explicit port; an
/// action does not): for a window, the window's process (fail closed, as the UI Automation reads decide), and for a port
/// alone, the process that listens on it. CDP then checks that the port's listener is that process. The commands that
/// change the browser take the input gate through <see cref="InputGateToolFilter.EnterAsync"/>, which refuses while the
/// user has paused input (PausedByUser) and waits for a running input call.
/// </remarks>
public sealed class BrowserTabService : IBrowserTabService
{
    /// <summary>How long finding the browser's tab may take, apart from the call's own time limit.</summary>
    public static readonly TimeSpan LookupTimeout = BrowserCdpTargets.LookupTimeout;

    /// <summary>The least time a wait gets to connect and check the page once, also with timeoutMs 0.</summary>
    public static readonly TimeSpan MinimumCheckTime = TimeSpan.FromSeconds(3);

    /// <summary>A tab title is cut to this many characters in list_tabs.</summary>
    public const int MaxTitleLength = 300;

    /// <summary>A tab URL is cut to this many characters in list_tabs (with "..."): a data: URL can be megabytes.</summary>
    public const int MaxUrlLength = 2_000;

    private readonly INativeWindowWrapper _windows;
    private readonly IBrowserDetectionService _detection;
    private readonly ICdpTabService _tabs;
    private readonly ITcpPortOwnerResolver _portOwners;
    private readonly IInputSession? _session;
    private readonly BrowserCdpTargets _targets;

    /// <param name="windows">The window and process reads.</param>
    /// <param name="detection">Tells browsers from other programs.</param>
    /// <param name="reading">Tells the browser family (Firefox has no CDP).</param>
    /// <param name="cdp">Finds the tab a window shows, or a tab by id.</param>
    /// <param name="tabs">The CDP side of the tab tools.</param>
    /// <param name="portOwners">Tells which process listens on a port given without a window.</param>
    /// <param name="policy">The user's target policy; the server always passes it.</param>
    /// <param name="portLocator">Finds a window's own debug port; the server always passes it. Without it, cdpPort is needed.</param>
    /// <param name="session">The input session the gate checks for a pause; the server always passes it.</param>
    public BrowserTabService(
        INativeWindowWrapper windows,
        IBrowserDetectionService detection,
        IBrowserReadingService reading,
        ICdpService cdp,
        ICdpTabService tabs,
        ITcpPortOwnerResolver portOwners,
        ITargetPolicy? policy = null,
        IDebugPortLocator? portLocator = null,
        IInputSession? session = null)
    {
        _windows = windows;
        _detection = detection;
        _tabs = tabs;
        _portOwners = portOwners;
        _session = session;
        _targets = new BrowserCdpTargets(windows, detection, reading, cdp, portOwners, policy, portLocator);
    }

    /// <inheritdoc />
    public async Task<BrowserTabsResult> ListTabsAsync(BrowserTabTarget target, CancellationToken cancellationToken = default)
    {
        var browser = Resolve(target);
        if (browser.Refusal is { } refusal)
            return BrowserTabsResult.Failed(refusal.Code, refusal.Error);

        var listed = await _tabs.ListTabsAsync(browser.Port, browser.ProcessId, browser.Window, LookupTimeout, cancellationToken)
            .ConfigureAwait(false);
        if (!listed.Success)
            return BrowserTabsResult.Failed(listed.ErrorCode.ToString(), listed.Error!, browser.Port);

        var listing = listed.Value!;
        var tabs = listing.Tabs.Take(BrowserTabsResult.MaxListedTabs)
            .Select(tab => new BrowserTabInfo(
                tab.Target.Id,
                Cut(tab.Target.Title, MaxTitleLength),
                Cut(tab.Target.Url, MaxUrlLength),
                tab.WindowId,
                listing.WindowBound ? tab.InWindow : null))
            .ToList();
        return new BrowserTabsResult(
            Success: true,
            Tabs: tabs,
            TabCount: listing.Tabs.Count,
            Truncated: listing.Tabs.Count > tabs.Count ? true : null,
            ShownTargetId: listing.Shown?.Id,
            WindowBound: browser.Window == null ? null : listing.WindowBound,
            CdpPort: browser.Port,
            Note: browser.Window == null ? null : NoteFor(listing));
    }

    /// <inheritdoc />
    public async Task<BrowserTabResult> ActivateTabAsync(BrowserTabTarget target, CancellationToken cancellationToken = default)
    {
        var browser = Resolve(target);
        if (browser.Refusal is { } refusal)
            return BrowserTabResult.Failed(refusal.Code, refusal.Error, targetId: target.TargetId);

        var tab = await FindTabAsync(browser, target.TargetId, cancellationToken).ConfigureAwait(false);
        if (!tab.Success)
            return BrowserTabResult.Failed(tab.ErrorCode.ToString(), tab.Error!, browser.Port, target.TargetId);

        var activated = await _tabs.ActivateAsync(tab.Value!, EnterGateAsync, LookupTimeout, cancellationToken).ConfigureAwait(false);
        if (!activated.Success)
            return BrowserTabResult.Failed(activated.ErrorCode.ToString(), activated.Error!, browser.Port, tab.Value!.Id);

        var shown = activated.Value!;
        return new BrowserTabResult(true, shown.Id, Cut(shown.Title, MaxTitleLength), Cut(shown.Url, MaxUrlLength), browser.Port);
    }

    /// <inheritdoc />
    public async Task<BrowserNavigateResult> NavigateAsync(
        BrowserTabTarget target, BrowserNavigateOptions options, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (DevToolsPortRefusal(options.Url) is { } devTools)
            return BrowserNavigateResult.Failed(ToolErrorReporting.InvalidArgumentsErrorCode, devTools, targetId: target.TargetId);

        var browser = Resolve(target);
        if (browser.Refusal is { } refusal)
            return BrowserNavigateResult.Failed(refusal.Code, refusal.Error, targetId: target.TargetId);

        var timeout = TimeSpan.FromMilliseconds(options.TimeoutMs);
        var stopwatch = Stopwatch.StartNew();
        var tab = options.NewTab
            ? await _tabs.OpenTabAsync(browser.Port, browser.ProcessId, EnterGateAsync, timeout, cancellationToken).ConfigureAwait(false)
            : await FindTabAsync(browser, target.TargetId, cancellationToken).ConfigureAwait(false);
        if (!tab.Success)
        {
            // A new tab the browser opened before the failure is left on about:blank: the fields name it, as below when the
            // pause or a busy gate stops its navigation
            return tab.OpenedTargetId is { } opened
                ? BrowserNavigateResult.Failed(tab.ErrorCode.ToString(), tab.Error!, browser.Port, opened) with
                {
                    NewTab = true,
                    ElapsedMs = stopwatch.ElapsedMilliseconds
                }
                : BrowserNavigateResult.Failed(tab.ErrorCode.ToString(), tab.Error!, browser.Port, target.TargetId);
        }

        var targetId = tab.Value!.Id;
        var newTab = options.NewTab ? true : (bool?)null;
        var remaining = timeout - stopwatch.Elapsed;
        CdpResult<CdpNavigation> navigated;
        try
        {
            navigated = await _tabs.NavigateAsync(tab.Value, options.Url, options.WaitUntil, EnterGateAsync,
                remaining > TimeSpan.Zero ? remaining : TimeSpan.FromMilliseconds(1), cancellationToken, options.Dialog).ConfigureAwait(false);
        }
        catch (ToolErrorException refused) when (options.NewTab)
        {
            // The gate was let go once the tab was opened, and the pause or a busy gate came before Page.navigate took it
            // again: the tab is there, so the result names it instead of the gate's "nothing was done"
            var why = refused.ErrorCode == nameof(WindowErrorCode.PausedByUser)
                ? $"input is paused ({InputControlSources.PauseCause(_session?.PauseState?.PausedBy, null)})"
                : "another input or window call held the input gate";
            return BrowserNavigateResult.Failed(refused.ErrorCode,
                $"A new tab {targetId} was opened on about:blank, but it was not navigated: {why}. " +
                (refused.ErrorCode == nameof(WindowErrorCode.PausedByUser)
                    ? "Stop and ask the user how to continue; do not try to resume input yourself."
                    : $"Navigate it with targetId {targetId} when your other calls have finished."),
                browser.Port, targetId) with { NewTab = true, ElapsedMs = stopwatch.ElapsedMilliseconds };
        }

        var elapsed = stopwatch.ElapsedMilliseconds;
        if (!navigated.Success)
        {
            return BrowserNavigateResult.Failed(navigated.ErrorCode.ToString(), navigated.Error!, browser.Port, targetId) with
            {
                NewTab = newTab,
                ElapsedMs = elapsed
            };
        }

        var page = navigated.Value!;
        if (page.ErrorText != null)
        {
            return new BrowserNavigateResult(false, targetId, LoadState: page.LoadState, ElapsedMs: elapsed, NewTab: newTab,
                CdpPort: browser.Port, ErrorText: page.ErrorText,
                Error: page.LoadState == CdpTabService.DownloadState
                    ? $"The address answered with a file, which the browser downloads ({page.ErrorText}); the tab still shows the " +
                      "page it had. Do not navigate to it again to read it: that downloads it again."
                    : $"The browser could not load the page: {page.ErrorText}. The tab shows the browser's error page. Check the " +
                      "address and that the site can be reached from this computer.",
                ErrorCode: nameof(BrowserErrorCode.NavigationFailed));
        }

        var result = new BrowserNavigateResult(true, targetId, Cut(page.Url, MaxUrlLength), Cut(page.Title, MaxTitleLength),
            page.HttpStatus, page.LoadState, elapsed, newTab, browser.Port,
            Dialog: page.Dialog is { } answered
                ? new BrowserOpenedDialog(answered.Dialog.Type, Cut(answered.Dialog.Message, CdpDialogText.MaxDialogMessageLength)!,
                    answered.Accepted ? BrowserOpenedDialog.Accepted : BrowserOpenedDialog.Dismissed)
                : null);
        return page.TimedOut
            ? result with
            {
                Success = false,
                ErrorCode = nameof(WindowErrorCode.WaitTimedOut),
                Error = $"The page did not reach '{WaitUntilName(options.WaitUntil)}' within {options.TimeoutMs} ms; it goes on loading " +
                        "(loadState 'loading'). Wait for what you need with wait_for_browser, or read the page; navigate again only " +
                        "if it does not load."
            }
            : result;
    }

    /// <inheritdoc />
    public async Task<BrowserWaitResult> WaitAsync(
        BrowserTabTarget target, BrowserWaitCondition condition, int timeoutMs, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(condition);
        var browser = Resolve(target);
        if (browser.Refusal is { } refusal)
            return BrowserWaitResult.Failed(refusal.Code, refusal.Error, targetId: target.TargetId);

        var tab = await FindTabAsync(browser, target.TargetId, cancellationToken).ConfigureAwait(false);
        if (!tab.Success)
            return BrowserWaitResult.Failed(tab.ErrorCode.ToString(), tab.Error!, browser.Port, target.TargetId);

        var stopwatch = Stopwatch.StartNew();
        var waited = await _tabs.WaitAsync(tab.Value!, condition, TimeSpan.FromMilliseconds(timeoutMs), MinimumCheckTime, cancellationToken)
            .ConfigureAwait(false);
        var elapsed = stopwatch.ElapsedMilliseconds;
        if (!waited.Success)
            return BrowserWaitResult.Failed(waited.ErrorCode.ToString(), waited.Error!, browser.Port, tab.Value!.Id) with { ElapsedMs = elapsed };

        var outcome = waited.Value!;
        var result = new BrowserWaitResult(outcome.Satisfied, outcome.Satisfied, !outcome.Satisfied, elapsed, tab.Value!.Id,
            Cut(outcome.Url, MaxUrlLength), Cut(outcome.Title, MaxTitleLength),
            browser.Port);
        return outcome.Satisfied
            ? result
            : result with
            {
                ErrorCode = nameof(WindowErrorCode.WaitTimedOut),
                Error = $"The page did not meet the condition within {timeoutMs} ms: {outcome.Detail}. Nothing was changed: wait again " +
                        "with a longer timeoutMs, or read the page (get_browser_content with this targetId)."
            };
    }

    /// <summary>
    /// Why <paramref name="url"/> is not opened as a loopback address a browser listens on, or null
    /// (<see cref="BrowserNavigationUrls.BrowserPortRefusal"/>, which launch_browser_with_debug's url shares).
    /// </summary>
    private string? DevToolsPortRefusal(string url) =>
        BrowserNavigationUrls.BrowserPortRefusal(
            url, _portOwners, processId => _windows.GetProcessName((uint)processId), _detection.IsBrowser, BrowserNavigationUrls.NothingDone);

    /// <summary>The tab with <paramref name="targetId"/>, or the one the window shows.</summary>
    private Task<CdpResult<CdpTarget>> FindTabAsync(ResolvedBrowser browser, string? targetId, CancellationToken cancellationToken) =>
        _targets.FindTabAsync(browser, targetId, cancellationToken);

    private Task<IDisposable> EnterGateAsync(CancellationToken cancellationToken) =>
        InputGateToolFilter.EnterAsync(_session, cancellationToken);

    private ResolvedBrowser Resolve(BrowserTabTarget target) => _targets.Resolve(target);

    private static string? NoteFor(CdpTabListing listing) =>
        (listing.WindowBound, listing.ShownError) switch
        {
            // Over CdpService.MaxBoundTabs no tab's browser window is looked up for the list (CdpTabService.ListTabsAsync)
            (false, _) when listing.Tabs.Count > CdpService.MaxBoundTabs =>
                $"The browser has more than {CdpService.MaxBoundTabs} tabs, too many to look up the browser window of each, so " +
                "the tabs are listed without windowId and the window's tabs are not marked (inWindow)." +
                (listing.Shown == null ? " No tab could be told as the one it shows." : string.Empty),
            (false, _) => "The window could not be matched to its browser window, so its tabs are not marked (inWindow); every tab of " +
                          "the browser is listed." + (listing.Shown == null ? " No tab could be told as the one it shows." : string.Empty),
            (true, { } why) => $"No tab could be told as the one the window shows ({why}).",
            _ => null
        };

    internal static string WaitUntilName(BrowserWaitUntil waitUntil) => waitUntil switch
    {
        BrowserWaitUntil.DomContentLoaded => "domcontentloaded",
        BrowserWaitUntil.None => "none",
        _ => "load"
    };

    [return: System.Diagnostics.CodeAnalysis.NotNullIfNotNull(nameof(text))]
    private static string? Cut(string? text, int max) => text != null && text.Length > max ? text[..(max - 3)] + "..." : text;
}
