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
    public static readonly TimeSpan LookupTimeout = TimeSpan.FromMilliseconds(CdpOptions.DefaultTimeoutMs);

    /// <summary>The least time a wait gets to connect and check the page once, also with timeoutMs 0.</summary>
    public static readonly TimeSpan MinimumCheckTime = TimeSpan.FromSeconds(3);

    /// <summary>A tab title is cut to this many characters in list_tabs.</summary>
    public const int MaxTitleLength = 300;

    /// <summary>A tab URL is cut to this many characters in list_tabs (with "..."): a data: URL can be megabytes.</summary>
    public const int MaxUrlLength = 2_000;

    private readonly INativeWindowWrapper _windows;
    private readonly IBrowserDetectionService _detection;
    private readonly IBrowserReadingService _reading;
    private readonly ICdpService _cdp;
    private readonly ICdpTabService _tabs;
    private readonly ITcpPortOwnerResolver _portOwners;
    private readonly ITargetPolicy? _policy;
    private readonly IDebugPortLocator? _portLocator;
    private readonly IInputSession? _session;

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
        _reading = reading;
        _cdp = cdp;
        _tabs = tabs;
        _portOwners = portOwners;
        _policy = policy;
        _portLocator = portLocator;
        _session = session;
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
        var browser = Resolve(target);
        if (browser.Refusal is { } refusal)
            return BrowserNavigateResult.Failed(refusal.Code, refusal.Error, targetId: target.TargetId);

        var timeout = TimeSpan.FromMilliseconds(options.TimeoutMs);
        var stopwatch = Stopwatch.StartNew();
        var tab = options.NewTab
            ? await _tabs.OpenTabAsync(browser.Port, browser.ProcessId, EnterGateAsync, timeout, cancellationToken).ConfigureAwait(false)
            : await FindTabAsync(browser, target.TargetId, cancellationToken).ConfigureAwait(false);
        if (!tab.Success)
            return BrowserNavigateResult.Failed(tab.ErrorCode.ToString(), tab.Error!, browser.Port, target.TargetId);

        var targetId = tab.Value!.Id;
        var newTab = options.NewTab ? true : (bool?)null;
        var remaining = timeout - stopwatch.Elapsed;
        var navigated = await _tabs.NavigateAsync(tab.Value, options.Url, options.WaitUntil, EnterGateAsync,
            remaining > TimeSpan.Zero ? remaining : TimeSpan.FromMilliseconds(1), cancellationToken).ConfigureAwait(false);
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
                Error: $"The browser could not load the page: {page.ErrorText}. The tab shows the browser's error page. Check the " +
                       "address and that the site can be reached from this computer.",
                ErrorCode: nameof(BrowserErrorCode.NavigationFailed));
        }

        var result = new BrowserNavigateResult(true, targetId, Cut(page.Url, MaxUrlLength), Cut(page.Title, MaxTitleLength),
            page.HttpStatus, page.LoadState, elapsed, newTab, browser.Port);
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

    /// <summary>The tab with <paramref name="targetId"/>, or the one the window shows.</summary>
    private Task<CdpResult<CdpTarget>> FindTabAsync(ResolvedBrowser browser, string? targetId, CancellationToken cancellationToken) =>
        targetId != null
            ? _cdp.FindTargetByIdAsync(browser.Port, browser.ProcessId, targetId, LookupTimeout, cancellationToken)
            : _cdp.FindTargetForWindowAsync(browser.Port, browser.Window!, LookupTimeout, cancellationToken);

    private Task<IDisposable> EnterGateAsync(CancellationToken cancellationToken) =>
        InputGateToolFilter.EnterAsync(_session, cancellationToken);

    /// <summary>
    /// The browser to work on, or why not: a window's process and its own debug port (or cdpPort), or the process that
    /// listens on cdpPort; a recognized Chromium browser the target policy allows.
    /// </summary>
    private ResolvedBrowser Resolve(BrowserTabTarget target)
    {
        if (target.WindowHandle is not { } handle)
        {
            return target.CdpPort is int port
                ? ResolvePort(port)
                : ResolvedBrowser.Refused(ToolErrorReporting.InvalidArgumentsErrorCode, "Pass the browser window (handle or window) or cdpPort. Nothing was done.");
        }

        if (handle == nint.Zero || !_windows.IsWindow(handle))
            return ResolvedBrowser.Refused(nameof(BrowserErrorCode.InvalidHandle), "Invalid window handle. Nothing was done.");

        var root = _windows.GetAncestor(handle, WindowAncestor.Root);
        handle = root != nint.Zero ? root : handle;
        var processId = _windows.GetWindowThreadProcessId(handle);
        var processName = _windows.GetProcessName(processId) ?? "Unknown";
        if (!_detection.IsBrowser(processName))
        {
            return ResolvedBrowser.Refused(nameof(BrowserErrorCode.NotABrowser),
                $"Window process '{processName}' is not a recognized browser. Nothing was done.");
        }

        if (UiaTargets.ReadVerdict(_windows, _policy, handle) is { Allowed: false } verdict)
            return ResolvedBrowser.Refused(nameof(BrowserErrorCode.TargetDeniedByPolicy), PolicyRefusal($"Window {(long)handle}", verdict));

        if (_reading.GetBrowserType(handle, processName) == "firefox")
            return NoCdp();

        int found;
        if (target.CdpPort is int given)
        {
            found = given;
        }
        else
        {
            var lookup = _portLocator?.Find((int)processId)
                         ?? new DebugPortLookup(DebugPortLookupStatus.NoDebugPort, null, "No debug port is looked for.");
            if (lookup.Port is not int located)
            {
                return ResolvedBrowser.Refused(
                    lookup.Status == DebugPortLookupStatus.HeldByOtherProcess
                        ? nameof(BrowserErrorCode.CdpPortOwnerMismatch)
                        : nameof(BrowserErrorCode.CdpUnavailable),
                    "The tab tools work through CDP only and need the DevTools port of the browser that owns the window, and none " +
                    $"was found. {lookup.Detail} Start the browser with launch_browser_with_debug, or pass cdpPort. Nothing was done.");
            }

            found = located;
        }

        var window = CdpWindowBinder.Describe(_windows, handle, processId, _windows.GetWindowText(handle));
        return new ResolvedBrowser(found, (int)processId, window, null);
    }

    /// <summary>
    /// The browser that listens on <paramref name="port"/>, named by no window: its process must be readable, a browser
    /// and allowed by the target policy (the port's owner is the target).
    /// </summary>
    private ResolvedBrowser ResolvePort(int port)
    {
        if (!_portOwners.TryGetListeningProcessId(port, out var owner))
        {
            return ResolvedBrowser.Refused(nameof(BrowserErrorCode.CdpPortOwnerMismatch),
                $"Could not read which process owns CDP port {port}, so it cannot be checked against the target policy. Nothing " +
                "was done. Pass the browser window instead.");
        }

        if (owner is not int processId)
        {
            return ResolvedBrowser.Refused(nameof(BrowserErrorCode.CdpUnavailable),
                $"Nothing listens on {CdpEndpointValidator.Host}:{port}: the browser may have exited, or the port is wrong. Nothing was done.");
        }

        var processName = _windows.GetProcessName((uint)processId);
        if (processName == null || !_detection.IsBrowser(processName))
        {
            return ResolvedBrowser.Refused(nameof(BrowserErrorCode.NotABrowser),
                $"CDP port {port} belongs to process {processId} ('{processName ?? "unknown"}'), which is not a recognized browser. " +
                "Nothing was done.");
        }

        if (_policy != null && _policy.EvaluateFailClosed([processName]) is { Allowed: false } verdict)
            return ResolvedBrowser.Refused(nameof(BrowserErrorCode.TargetDeniedByPolicy), PolicyRefusal($"The browser on port {port}", verdict));

        if (_reading.GetBrowserType(nint.Zero, processName) == "firefox")
            return NoCdp();

        return new ResolvedBrowser(port, processId, null, null);
    }

    private static ResolvedBrowser NoCdp() => ResolvedBrowser.Refused(nameof(BrowserErrorCode.CdpUnavailable),
        "Firefox has no CDP (its debug port speaks WebDriver BiDi); the tab tools need a Chromium browser started with a debug " +
        "port (launch_browser_with_debug). Nothing was done.");

    private static string PolicyRefusal(string what, TargetPolicyVerdict verdict) =>
        $"{what} is refused by the server's target policy: {verdict.Reason}. Nothing was done: the policy covers acting on a " +
        "browser through CDP as it covers input. It is set by the user in the server's environment and no tool argument changes " +
        "it; ask the user to do this step.";

    private static string? NoteFor(CdpTabListing listing) =>
        (listing.WindowBound, listing.ShownError) switch
        {
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

    /// <summary>The browser a call works on: its port, its process and the window named, or why it was refused.</summary>
    private sealed record ResolvedBrowser(int Port, int ProcessId, CdpWindow? Window, (string Code, string Error)? Refusal)
    {
        public static ResolvedBrowser Refused(string code, string error) => new(0, 0, null, (code, error));
    }
}
