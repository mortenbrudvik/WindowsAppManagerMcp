using System.Diagnostics;
using System.Text.Json;
using WindowsAppManagerMcp.Models;
using WindowsAppManagerMcp.Services.Interfaces;

namespace WindowsAppManagerMcp.Services;

/// <summary>
/// The CDP side of list_tabs, activate_tab, browser_navigate and wait_for_browser (roadmap M5.5), over
/// <see cref="ICdpTransport"/>: one connection per call, closed before it returns, as the reads do (M5.2).
/// </summary>
/// <remarks>
/// <para>
/// Tabs are listed from /json/list and placed in their browser windows with Browser.getWindowForTarget (the M5.3
/// binding). CDP reports no active tab, so the tab a window shows is the one of its tabs whose title the window shows.
/// Activating and opening a tab go over the browser's own DevTools connection (Target.activateTarget,
/// Target.createTarget), not the /json/activate and /json/new pages.
/// </para>
/// <para>
/// Navigation registers the load event (Page.loadEventFired or Page.domContentEventFired) before it sends Page.navigate,
/// so an event that comes at once is not missed; the event only wakes the wait up. Chromium answers Page.navigate once the
/// server has answered (the reply's errorText and isDownload need the response), so a slow server keeps the reply back: a
/// reply that does not come in time is a navigation still loading when the tab answers a check, and CdpTimeout when it does
/// not (waitUntil none does not wait for the reply past the gate's hold). The reply comes when the browser sends the new
/// document to the page, which may still be showing the old one, so the old document's state never counts: the wait first
/// sees a new document in Page.getFrameTree (the navigation's loaderId, or any other than the one read before
/// Page.navigate, as when the new page replaced itself at once), then reads its document.readyState, every
/// <see cref="PollInterval"/> or when the event comes.
/// </para>
/// </remarks>
public sealed class CdpTabService : ICdpTabService
{
    private const string Host = CdpEndpointValidator.Host;

    /// <summary>How often a wait checks the page.</summary>
    public static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(200);

    /// <summary>
    /// How long the input gate is held for a command that changes the browser: until the browser answers it, at most this
    /// long. The command is on its way by then; the gate orders it with the input calls, it does not wait for the page.
    /// </summary>
    public static readonly TimeSpan GateHold = CdpGatedSend.GateHold;

    /// <summary>The loadState of a navigation that became a download: the tab keeps its page (NavigationFailed).</summary>
    public const string DownloadState = "download";

    /// <summary>
    /// Page.navigate's errorText for a 4xx/5xx answer without a body: the server answered, and the tab shows the browser's
    /// "HTTP ERROR" page, so it is loaded as a 404 page with a body is, not failed.
    /// </summary>
    private const string HttpResponseCodeFailure = "net::ERR_HTTP_RESPONSE_CODE_FAILURE";

    /// <summary>What a page that answers nothing probably has, and what to do, as the reads say it.</summary>
    private const string UnansweredAdvice =
        "It probably has a JavaScript dialog (alert/confirm/prompt/beforeunload) open, is paused in the debugger, or is busy. " +
        "If a dialog is open, inspect it (capture_window) and decide deliberately how to close it, or ask the user, before " +
        "you retry; do not press Enter blindly, since Enter accepts a confirm or beforeunload dialog.";

    /// <summary>The longest one check of a wait (one command) may take before the wait goes on to the next.</summary>
    private static readonly TimeSpan MaxCheckTime = TimeSpan.FromSeconds(5);

    /// <summary>The longest the main frame's document may be read for before a navigation.</summary>
    private static readonly TimeSpan MaxPreNavigationLookup = TimeSpan.FromSeconds(1);

    /// <summary>The most of a navigation's time kept to ask the tab why Page.navigate got no answer.</summary>
    private static readonly TimeSpan MaxUnansweredProbeTime = TimeSpan.FromMilliseconds(500);

    /// <summary>The longest the browser-window lookup of list_tabs may take.</summary>
    private static readonly TimeSpan MaxWindowLookupTime = TimeSpan.FromSeconds(5);

    /// <summary>How long a tab that was just opened may take to appear in /json/list.</summary>
    private static readonly TimeSpan NewTabListingDelay = TimeSpan.FromMilliseconds(100);

    private readonly ICdpService _cdp;
    private readonly ICdpTransport _transport;
    private readonly ITcpPortOwnerResolver _portOwners;

    /// <param name="cdp">The tab list, /json/version and the browser windows.</param>
    /// <param name="transport">The DevTools connections.</param>
    /// <param name="portOwners">Tells which process listens on a debug port.</param>
    public CdpTabService(ICdpService cdp, ICdpTransport transport, ITcpPortOwnerResolver portOwners)
    {
        _cdp = cdp;
        _transport = transport;
        _portOwners = portOwners;
    }

    /// <inheritdoc />
    public async Task<CdpResult<CdpTabListing>> ListTabsAsync(
        int port, int processId, CdpWindow? window, TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        if (CheckOwner(port, processId) is { } refused)
            return refused.FailAs<CdpTabListing>();

        var stopwatch = Stopwatch.StartNew();
        var listed = await _cdp.GetPageTargetsAsync(port, timeout, cancellationToken).ConfigureAwait(false);
        if (!listed.Success)
            return listed.FailAs<CdpTabListing>();

        var all = listed.Value!;
        IReadOnlyDictionary<string, CdpBrowserWindow>? windows = null;
        if (all.Count is > 0 and <= CdpService.MaxBoundTabs)
        {
            // Best effort: without it the tabs are listed without their windows
            var left = timeout - stopwatch.Elapsed;
            windows = await CdpBrowserWindows.QueryAsync(
                _cdp, _transport, port, all, left < MaxWindowLookupTime ? left / 2 : MaxWindowLookupTime, cancellationToken)
                .ConfigureAwait(false);
        }

        int? WindowOf(CdpTarget target) => windows != null && windows.TryGetValue(target.Id, out var w) ? w.WindowId : null;

        if (window == null)
        {
            return CdpResult<CdpTabListing>.Ok(new CdpTabListing(
                all.Select(t => new CdpTab(t, WindowOf(t), false)).ToList(), false, null, null));
        }

        var bound = window.Geometry is { } geometry && windows is { Count: > 0 }
            ? CdpWindowBinder.MatchingWindows(geometry, windows.Values)
            : new HashSet<int>();
        var inWindow = all.Where(t => WindowOf(t) is int id && bound.Contains(id)).ToList();

        // As the reads pick the tab (CdpService.FindTargetForWindowAsync): the title among the window's tabs, else among all
        var shown = inWindow.Count > 0 ? CdpTargetMatcher.Match(window.Title, inWindow) : null;
        if (shown is not { Success: true } && shown?.ErrorCode != BrowserErrorCode.CdpTargetAmbiguous)
            shown = CdpTargetMatcher.Match(window.Title, all);

        var tabs = inWindow.Select(t => new CdpTab(t, WindowOf(t), true))
            .Concat(all.Except(inWindow).Select(t => new CdpTab(t, WindowOf(t), false)))
            .ToList();
        return CdpResult<CdpTabListing>.Ok(new CdpTabListing(
            tabs, inWindow.Count > 0, shown.Success ? shown.Value : null, shown.Success ? null : shown.DescribeError()));
    }

    /// <inheritdoc />
    public async Task<CdpResult<CdpTarget>> ActivateAsync(
        CdpTarget target, CdpGate gate, TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(target);
        var stopwatch = Stopwatch.StartNew();
        var connected = await ConnectToBrowserAsync(target.Port, timeout, cancellationToken).ConfigureAwait(false);
        if (!connected.Success)
            return connected.FailAs<CdpTarget>();

        await using var connection = connected.Value!;
        var reply = await SendGatedAsync(
            connection, gate, "Target.activateTarget", new { targetId = target.Id }, timeout - stopwatch.Elapsed, cancellationToken)
            .ConfigureAwait(false);
        if (!reply.Success)
            return reply.FailAs<CdpTarget>();

        return reply.Value!.Error is { } fault
            ? NoSuchTarget<CdpTarget>(fault, target.Id) ?? CdpReplies.ToFailure<CdpTarget>("Target.activateTarget", fault)
            : CdpResult<CdpTarget>.Ok(target);
    }

    /// <inheritdoc />
    public async Task<CdpResult<CdpTarget>> OpenTabAsync(
        int port, int processId, CdpGate gate, TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        if (CheckOwner(port, processId) is { } refused)
            return refused.FailAs<CdpTarget>();

        var stopwatch = Stopwatch.StartNew();
        string targetId;
        var connected = await ConnectToBrowserAsync(port, timeout, cancellationToken).ConfigureAwait(false);
        if (!connected.Success)
            return connected.FailAs<CdpTarget>();

        await using (var connection = connected.Value!)
        {
            var reply = await SendGatedAsync(
                connection, gate, "Target.createTarget", new { url = BrowserNavigationUrls.AboutBlank }, timeout - stopwatch.Elapsed,
                cancellationToken).ConfigureAwait(false);
            if (!reply.Success)
                return reply.FailAs<CdpTarget>();

            if (reply.Value!.Error is { } fault)
                return CdpReplies.ToFailure<CdpTarget>("Target.createTarget", fault);

            targetId = CdpReplies.GetString(reply.Value.Result, "targetId");
            if (!CdpEndpointValidator.IsValidTargetId(targetId))
            {
                return CdpResult<CdpTarget>.Fail(
                    BrowserErrorCode.CdpProtocolError,
                    $"The browser on {Host}:{port} answered Target.createTarget without a valid targetId.");
            }
        }

        // The new tab is listed once the browser has set it up, usually at once
        while (true)
        {
            var remaining = timeout - stopwatch.Elapsed;
            var listed = await _cdp.GetPageTargetsAsync(port, remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero, cancellationToken)
                .ConfigureAwait(false);
            if (listed.Success && listed.Value!.FirstOrDefault(t => t.Id == targetId) is { } opened)
                return CdpResult<CdpTarget>.Ok(opened);

            if (timeout - stopwatch.Elapsed <= NewTabListingDelay)
            {
                return listed.Success
                    ? CdpResult<CdpTarget>.Fail(
                        BrowserErrorCode.CdpTargetNotFound,
                        $"The browser opened tab {targetId}, but did not list it within {FormatMs(timeout)}; it may have been " +
                        "closed at once. Call list_tabs to see the browser's tabs.")
                    : listed.FailAs<CdpTarget>();
            }

            await Task.Delay(NewTabListingDelay, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <inheritdoc />
    public async Task<CdpResult<CdpNavigation>> NavigateAsync(
        CdpTarget target, string url, BrowserWaitUntil waitUntil, CdpGate gate, TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(target);
        var stopwatch = Stopwatch.StartNew();
        var connected = await ConnectToTabAsync(target, timeout, cancellationToken).ConfigureAwait(false);
        if (!connected.Success)
            return connected.FailAs<CdpNavigation>();

        await using var connection = connected.Value!;

        // Page.enable makes the browser report dialogs (a beforeunload prompt blocks the navigation) and the load events.
        // Its reply is not awaited: a page blocked by a dialog may never send it
        var enable = connection.SendAsync("Page.enable", null, timeout, cancellationToken);
        _ = enable.ContinueWith(static t => _ = t.Exception, TaskScheduler.Default);

        if (waitUntil == BrowserWaitUntil.None)
            return await NavigateWithoutWaitingAsync(connection, url, gate, timeout - stopwatch.Elapsed, cancellationToken).ConfigureAwait(false);

        // Network.enable reports the document's response, whose status a 4xx/5xx answer without a body has nowhere else
        // (net::ERR_HTTP_RESPONSE_CODE_FAILURE); not awaited either
        var network = connection.SendAsync("Network.enable", null, timeout, cancellationToken);
        _ = network.ContinueWith(static t => _ = t.Exception, TaskScheduler.Default);

        // The document before the navigation: any other one in the main frame is past the navigation's commit, also one
        // the new page replaced at once (location.replace, a meta refresh), whose loaderId is not the navigation's
        var before = await connection.SendAsync("Page.getFrameTree", null, PreNavigationLookup(timeout - stopwatch.Elapsed), cancellationToken)
            .ConfigureAwait(false);
        if (!before.Success && IsFinal(before.ErrorCode))
            return before.FailAs<CdpNavigation>();

        var oldFrame = before.Success && before.Value!.Error == null ? MainFrame(before.Value.Result) : null;

        // Registered before Page.navigate, so an event that comes at once is not missed
        using var loaded = waitUntil == BrowserWaitUntil.Load
            ? connection.ExpectEvent("Page.loadEventFired")
            : connection.ExpectEvent("Page.domContentEventFired");
        using var response = connection.ExpectEvent("Network.responseReceived", p =>
            CdpReplies.GetString(p, "type") == "Document" &&
            (oldFrame is not { } frame || CdpReplies.GetString(p, "frameId") == frame.FrameId));

        // Chromium answers Page.navigate once the server has answered (its errorText and isDownload need the response), so a
        // slow server keeps the reply back: a part of the time is kept to ask the tab why when it does not come
        var left = timeout - stopwatch.Elapsed;
        if (left <= TimeSpan.Zero)
        {
            return CdpResult<CdpNavigation>.Fail(
                BrowserErrorCode.CdpTimeout, "The time limit was used up before Page.navigate could be sent. Nothing was changed.");
        }

        var reply = await SendGatedAsync(connection, gate, "Page.navigate", new { url }, left - UnansweredProbeTime(left), cancellationToken)
            .ConfigureAwait(false);
        if (!reply.Success)
        {
            if (reply.ErrorCode != BrowserErrorCode.CdpTimeout)
                return reply.FailAs<CdpNavigation>();

            // The tab answers while its old page waits for a slow server: the navigation goes on loading. A tab that does not
            // answer either is blocked (a dialog the browser did not report to this connection) or busy
            var waiting = await ProbeAsync(connection, Bounded(timeout - stopwatch.Elapsed), cancellationToken).ConfigureAwait(false);
            if (waiting.Failure is { } blocked)
                return blocked.FailAs<CdpNavigation>();

            return waiting.ReadyState != null
                ? CdpResult<CdpNavigation>.Ok(new CdpNavigation(null, null, null, "loading", true))
                : CdpResult<CdpNavigation>.Fail(
                    BrowserErrorCode.CdpTimeout,
                    $"Tab {target.Id} answered neither Page.navigate nor a check within {FormatMs(timeout)}. {UnansweredAdvice} " +
                    "The navigation may still go on; check it with get_browser_url before you retry.");
        }

        if (reply.Value!.Error is { } fault)
            return CdpReplies.ToFailure<CdpNavigation>("Page.navigate", fault);

        var errorText = CdpReplies.GetString(reply.Value.Result, "errorText");
        var httpError = errorText == HttpResponseCodeFailure;
        if (errorText.Length > 0 && !httpError)
            return CdpResult<CdpNavigation>.Ok(Failed(reply.Value.Result, errorText));

        var loaderId = CdpReplies.GetString(reply.Value.Result, "loaderId");
        if (loaderId.Length == 0)
        {
            // A jump within the page (a fragment, history.pushState) loads no document and fires no load event
            var page = await ProbeAsync(connection, Bounded(timeout - stopwatch.Elapsed), cancellationToken).ConfigureAwait(false);
            return page.Failure is { } jumpFailure
                ? jumpFailure.FailAs<CdpNavigation>()
                : CdpResult<CdpNavigation>.Ok(new CdpNavigation(page.Url, page.Title, page.Status, "sameDocument", false));
        }

        var wakeUp = loaded.WaitAsync(timeout - stopwatch.Elapsed, cancellationToken);
        var committed = false;
        PageProbe? last = null;
        while (true)
        {
            var remaining = timeout - stopwatch.Elapsed;
            if (remaining <= TimeSpan.Zero)
                break;

            if (!committed)
            {
                var tree = await connection.SendAsync("Page.getFrameTree", null, Bounded(remaining), cancellationToken).ConfigureAwait(false);
                if (!tree.Success && IsFinal(tree.ErrorCode))
                    return tree.FailAs<CdpNavigation>();

                var now = tree.Success && tree.Value!.Error == null ? MainFrame(tree.Value.Result)?.LoaderId : null;
                committed = now is { Length: > 0 } && (now == loaderId || (oldFrame != null && now != oldFrame.LoaderId));
            }

            if (committed)
            {
                var page = await ProbeAsync(connection, Bounded(timeout - stopwatch.Elapsed), cancellationToken).ConfigureAwait(false);
                if (page.Failure is { } failure)
                    return failure.FailAs<CdpNavigation>();

                if (page.ReadyState != null)
                {
                    last = httpError ? await AsServerErrorAsync(page, url, response, cancellationToken).ConfigureAwait(false) : page;
                    if (IsReady(page.ReadyState, waitUntil))
                    {
                        return CdpResult<CdpNavigation>.Ok(new CdpNavigation(
                            last.Url, last.Title, last.Status, waitUntil == BrowserWaitUntil.Load ? "load" : "domcontentloaded", false));
                    }
                }
            }

            remaining = timeout - stopwatch.Elapsed;
            if (remaining <= TimeSpan.Zero)
                break;

            var pause = Task.Delay(remaining < PollInterval ? remaining : PollInterval, cancellationToken);
            if (wakeUp is { IsCompleted: false })
                await Task.WhenAny(wakeUp, pause).ConfigureAwait(false);
            else
                await pause.ConfigureAwait(false);

            cancellationToken.ThrowIfCancellationRequested();
            if (wakeUp is { IsCompleted: true })
                wakeUp = null;
        }

        return CdpResult<CdpNavigation>.Ok(new CdpNavigation(last?.Url, last?.Title, last?.Status, "loading", true));
    }

    /// <summary>
    /// waitUntil none: Page.navigate is sent inside the gate, which is held until the browser answers or
    /// <see cref="GateHold"/> has passed, and the call returns then. A reply that came by then is read (a refused URL, an
    /// address that does not load, a download); one that has not (the server has not answered yet) is not waited for.
    /// </summary>
    private static async Task<CdpResult<CdpNavigation>> NavigateWithoutWaitingAsync(
        ICdpConnection connection, string url, CdpGate gate, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var notWaited = CdpResult<CdpNavigation>.Ok(new CdpNavigation(null, null, null, "notWaited", false));
        if (timeout <= TimeSpan.Zero)
        {
            return CdpResult<CdpNavigation>.Fail(
                BrowserErrorCode.CdpTimeout, "The time limit was used up before Page.navigate could be sent. Nothing was changed.");
        }

        var sent = await StartGatedAsync(connection, gate, "Page.navigate", new { url }, timeout, cancellationToken).ConfigureAwait(false);
        if (!sent.IsCompleted)
        {
            _ = sent.ContinueWith(static t => _ = t.Exception, TaskScheduler.Default);
            return notWaited;
        }

        var reply = await sent.ConfigureAwait(false);
        if (!reply.Success)
            return reply.ErrorCode == BrowserErrorCode.CdpTimeout ? notWaited : reply.FailAs<CdpNavigation>();

        if (reply.Value!.Error is { } fault)
            return CdpReplies.ToFailure<CdpNavigation>("Page.navigate", fault);

        var errorText = CdpReplies.GetString(reply.Value.Result, "errorText");
        return errorText.Length > 0 && errorText != HttpResponseCodeFailure
            ? CdpResult<CdpNavigation>.Ok(Failed(reply.Value.Result, errorText))
            : notWaited;
    }

    /// <summary>
    /// A navigation Page.navigate answered with an errorText: a download (isDownload; Chromium reports net::ERR_ABORTED and
    /// the tab keeps its page), or a page the browser could not load (it shows its error page).
    /// </summary>
    private static CdpNavigation Failed(JsonElement reply, string errorText) =>
        new(null, null, null, IsTrue(reply, "isDownload") ? DownloadState : "failed", false, errorText);

    /// <summary>
    /// The browser's error page for a 4xx/5xx answer without a body, read as that answer: the address asked for (the error
    /// page's own is an internal chrome-error: one) and the status of the document's response, which the page does not know.
    /// </summary>
    private static async Task<PageProbe> AsServerErrorAsync(
        PageProbe page, string url, ICdpEventWaiter response, CancellationToken cancellationToken)
    {
        var status = page.Status;
        if (status == null)
        {
            // The response came before the commit, so the event is in already when there is one
            var received = await response.WaitAsync(TimeSpan.FromMilliseconds(1), cancellationToken).ConfigureAwait(false);
            if (received.Success && received.Value.TryGetProperty("response", out var answer) && answer.ValueKind == JsonValueKind.Object &&
                answer.TryGetProperty("status", out var s) && s.ValueKind == JsonValueKind.Number && s.TryGetInt32(out var code) &&
                code is >= 100 and <= 999)
            {
                status = code;
            }
        }

        var shown = page.Url is { } at && at.StartsWith("chrome-error:", StringComparison.OrdinalIgnoreCase) ? url : page.Url;
        return page with { Url = shown, Status = status };
    }

    /// <inheritdoc />
    public async Task<CdpResult<CdpWaitOutcome>> WaitAsync(
        CdpTarget target, BrowserWaitCondition condition, TimeSpan timeout, TimeSpan minimumCheckTime,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(condition);
        var stopwatch = Stopwatch.StartNew();
        var firstBudget = timeout > minimumCheckTime ? timeout : minimumCheckTime;
        var connected = await ConnectToTabAsync(target, firstBudget, cancellationToken).ConfigureAwait(false);
        if (!connected.Success)
            return connected.FailAs<CdpWaitOutcome>();

        await using var connection = connected.Value!;

        // Page.enable makes the browser report a dialog, which ends the wait (CdpDialogOpen); its reply is not awaited
        var enable = connection.SendAsync("Page.enable", null, firstBudget, cancellationToken);
        _ = enable.ContinueWith(static t => _ = t.Exception, TaskScheduler.Default);

        var expression = BuildWaitExpression(condition);
        CdpWaitOutcome? last = null;
        var first = true;
        var answered = false;
        while (true)
        {
            var budget = first ? firstBudget - stopwatch.Elapsed : timeout - stopwatch.Elapsed;
            if (budget <= TimeSpan.Zero && !first)
                break;

            var check = await CheckAsync(connection, expression, condition, Bounded(budget > TimeSpan.Zero ? budget : TimeSpan.FromMilliseconds(1)),
                cancellationToken).ConfigureAwait(false);
            if (!check.Success && IsFinal(check.ErrorCode))
                return check;

            // A check the page did not answer in time is skipped; one it answered, even between two documents, is not
            answered |= check.Success;
            if (check is { Success: true, Value: { } seen })
            {
                last = seen;
                if (seen.Satisfied)
                    return check;
            }

            first = false;
            var remaining = timeout - stopwatch.Elapsed;
            if (remaining <= TimeSpan.Zero)
                break;

            await Task.Delay(remaining < PollInterval ? remaining : PollInterval, cancellationToken).ConfigureAwait(false);
        }

        // No check answered at all: the page is blocked, by a dialog the browser does not report to a new connection (Edge
        // does not report one that was open before), the debugger or a script; waiting longer does not help
        if (!answered)
        {
            return CdpResult<CdpWaitOutcome>.Fail(
                BrowserErrorCode.CdpTimeout,
                $"The page in tab {target.Id} answered no check within {stopwatch.ElapsedMilliseconds} ms. {UnansweredAdvice} " +
                "Or read the window with UI Automation (useCdp=false).");
        }

        return CdpResult<CdpWaitOutcome>.Ok(last ?? new CdpWaitOutcome(false, null, null,
            "the page could not be checked: it was between two documents or busy at every check"));
    }

    /// <summary>
    /// One check of a wait: the condition's result, or null when the page answered but could not be checked this time (it
    /// was between two documents); CdpTimeout when it did not answer in time (busy, or blocked); a failure that ends the
    /// wait (a dialog, a closed tab, a selector that is not valid CSS).
    /// </summary>
    private static async Task<CdpResult<CdpWaitOutcome>> CheckAsync(
        ICdpConnection connection, string expression, BrowserWaitCondition condition, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var evaluated = await connection.SendAsync("Runtime.evaluate", new
        {
            expression,
            returnByValue = true,
            awaitPromise = false
        }, timeout, cancellationToken).ConfigureAwait(false);

        if (!evaluated.Success)
            return evaluated.FailAs<CdpWaitOutcome>();

        // An error reply or an exception: the page is between two documents (its context was destroyed); check again
        if (evaluated.Value!.Error != null || ValueOf(evaluated.Value.Result) is not { } value)
            return CdpResult<CdpWaitOutcome>.Ok(null!);

        var badSelector = CdpReplies.GetString(value, "badSelector");
        if (badSelector.Length > 0)
        {
            return CdpResult<CdpWaitOutcome>.Fail(
                BrowserErrorCode.CdpEvaluationFailed,
                $"The selector '{condition.Selector}' is not valid CSS ({badSelector}). Nothing was waited for.");
        }

        var url = CdpReplies.GetString(value, "url");
        var title = CdpReplies.GetString(value, "title");
        var missing = new List<string>();
        if (condition.UrlContains != null && !url.Contains(condition.UrlContains, StringComparison.OrdinalIgnoreCase))
            missing.Add($"the URL does not contain '{condition.UrlContains}'");
        if (condition.TitleContains != null && !title.Contains(condition.TitleContains, StringComparison.OrdinalIgnoreCase))
            missing.Add($"the title does not contain '{condition.TitleContains}'");
        if (condition.TextContains != null && !IsTrue(value, "text"))
            missing.Add($"the page's text does not contain '{condition.TextContains}'");
        if (condition.Selector != null && !IsTrue(value, "selector"))
            missing.Add($"no element matches '{condition.Selector}'");

        return CdpResult<CdpWaitOutcome>.Ok(new CdpWaitOutcome(missing.Count == 0, url, title,
            missing.Count == 0 ? null : string.Join("; ", missing)));
    }

    /// <summary>
    /// The script of one wait check: the page's URL and title, whether its text contains the text (case-insensitive) and
    /// whether an element matches the selector. The text and the selector go in as JSON string literals, so they are only
    /// ever data; a selector that is not valid CSS is reported as badSelector, not thrown.
    /// </summary>
    public static string BuildWaitExpression(BrowserWaitCondition condition)
    {
        var text = condition.TextContains == null ? "null" : JsonSerializer.Serialize(condition.TextContains.ToLowerInvariant());
        var selector = condition.Selector == null ? "null" : JsonSerializer.Serialize(condition.Selector);
        return "(() => { " +
               $"const text = {text}; const selector = {selector}; " +
               "const r = { url: location.href, title: document.title }; " +
               "if (text !== null) { const b = document.body; r.text = !!b && String(b.innerText).toLowerCase().indexOf(text) >= 0; } " +
               "if (selector !== null) { try { r.selector = document.querySelector(selector) !== null; } " +
               "catch (e) { r.badSelector = String((e && e.message) || e) || 'invalid selector'; } } " +
               "return r; })()";
    }

    /// <summary>The script that reads a page's load state, URL, title and main document's HTTP status.</summary>
    public const string ProbeExpression =
        "(() => { let status = null; " +
        "try { const n = performance.getEntriesByType('navigation')[0]; if (n && n.responseStatus) { status = n.responseStatus; } } catch (e) { } " +
        "return { readyState: document.readyState, url: location.href, title: document.title, status: status }; })()";

    private static async Task<PageProbe> ProbeAsync(ICdpConnection connection, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var evaluated = await connection.SendAsync("Runtime.evaluate", new
        {
            expression = ProbeExpression,
            returnByValue = true,
            awaitPromise = false
        }, timeout, cancellationToken).ConfigureAwait(false);

        if (!evaluated.Success)
            return IsFinal(evaluated.ErrorCode) ? new PageProbe(null, null, null, null, evaluated.FailAs<bool>()) : PageProbe.Unknown;

        if (evaluated.Value!.Error != null || ValueOf(evaluated.Value.Result) is not { } value)
            return PageProbe.Unknown;

        int? status = value.TryGetProperty("status", out var s) && s.ValueKind == JsonValueKind.Number && s.TryGetInt32(out var code) &&
                      code is >= 100 and <= 999
            ? code
            : null;
        var readyState = CdpReplies.GetString(value, "readyState");
        return new PageProbe(readyState.Length > 0 ? readyState : null, CdpReplies.GetString(value, "url"),
            CdpReplies.GetString(value, "title"), status, null);
    }

    /// <summary>What a page probe read: its readyState (null when the page could not be read this time), URL, title and status.</summary>
    private sealed record PageProbe(string? ReadyState, string? Url, string? Title, int? Status, CdpResult<bool>? Failure)
    {
        public static PageProbe Unknown { get; } = new(null, null, null, null, null);
    }

    private static bool IsReady(string readyState, BrowserWaitUntil waitUntil) => waitUntil == BrowserWaitUntil.Load
        ? readyState == "complete"
        : readyState is "interactive" or "complete";

    /// <summary>The value of a Runtime.evaluate reply's result with returnByValue, when it is an object and nothing threw.</summary>
    private static JsonElement? ValueOf(JsonElement result) =>
        result.ValueKind == JsonValueKind.Object &&
        !result.TryGetProperty("exceptionDetails", out _) &&
        result.TryGetProperty("result", out var remote) && remote.ValueKind == JsonValueKind.Object &&
        remote.TryGetProperty("value", out var value) && value.ValueKind == JsonValueKind.Object
            ? value
            : null;

    private static bool IsTrue(JsonElement value, string name) =>
        value.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.True;

    /// <summary>Page.getFrameTree's main frame: its id, and its loaderId, the document the tab has now; null when not read.</summary>
    private static FrameDocument? MainFrame(JsonElement result) =>
        result.ValueKind == JsonValueKind.Object &&
        result.TryGetProperty("frameTree", out var tree) && tree.ValueKind == JsonValueKind.Object &&
        tree.TryGetProperty("frame", out var frame) && CdpReplies.GetString(frame, "loaderId") is { Length: > 0 } loaderId
            ? new FrameDocument(CdpReplies.GetString(frame, "id"), loaderId)
            : null;

    /// <summary>A frame and the document it has (its loaderId).</summary>
    private sealed record FrameDocument(string FrameId, string LoaderId);

    /// <summary>How long the main frame's document may be read for before the navigation: at most a second or a quarter of the time.</summary>
    private static TimeSpan PreNavigationLookup(TimeSpan remaining) =>
        Bounded(remaining / 4 < MaxPreNavigationLookup ? remaining / 4 : MaxPreNavigationLookup);

    /// <summary>The part of a navigation's time kept to ask the tab why Page.navigate got no answer: at most half a second or a quarter.</summary>
    private static TimeSpan UnansweredProbeTime(TimeSpan remaining) =>
        remaining / 4 < MaxUnansweredProbeTime ? remaining / 4 : MaxUnansweredProbeTime;

    /// <summary>
    /// Whether a failed command ends a wait: a dialog, a closed connection, an answer too large or unreadable. A timeout
    /// does not (the page may be busy for a moment).
    /// </summary>
    private static bool IsFinal(BrowserErrorCode code) => code != BrowserErrorCode.CdpTimeout;

    private static TimeSpan Bounded(TimeSpan remaining) =>
        remaining <= TimeSpan.Zero ? TimeSpan.FromMilliseconds(1) : remaining < MaxCheckTime ? remaining : MaxCheckTime;

    /// <summary>Sends a command that changes the browser inside the input gate (<see cref="CdpGatedSend"/>).</summary>
    private static Task<CdpResult<CdpReply>> SendGatedAsync(
        ICdpConnection connection, CdpGate gate, string method, object parameters, TimeSpan timeout, CancellationToken cancellationToken) =>
        CdpGatedSend.SendAsync(connection, gate, method, parameters, timeout, cancellationToken);

    /// <summary>Sends a command inside the gate and returns once the gate is let go (<see cref="CdpGatedSend.StartAsync"/>).</summary>
    private static Task<Task<CdpResult<CdpReply>>> StartGatedAsync(
        ICdpConnection connection, CdpGate gate, string method, object parameters, TimeSpan timeout, CancellationToken cancellationToken) =>
        CdpGatedSend.StartAsync(connection, gate, method, parameters, timeout, cancellationToken);

    /// <summary>The browser-level DevTools connection (/json/version's webSocketDebuggerUrl).</summary>
    private async Task<CdpResult<ICdpConnection>> ConnectToBrowserAsync(int port, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        var version = await _cdp.GetBrowserVersionAsync(port, timeout, cancellationToken).ConfigureAwait(false);
        if (!version.Success)
            return version.FailAs<ICdpConnection>();

        return await _transport.ConnectAsync(
            version.Value!.WebSocketDebuggerUrl, port, "the browser", Remaining(timeout, stopwatch), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>The tab's own DevTools connection, at the WebSocket URL its port listed.</summary>
    private async Task<CdpResult<ICdpConnection>> ConnectToTabAsync(CdpTarget target, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var label = $"tab {target.Id}";
        var webSocketUrl = CdpEndpointValidator.ValidateWebSocketUrl(target.WebSocketDebuggerUrl, target.Port, label);
        if (!webSocketUrl.Success)
            return webSocketUrl.FailAs<ICdpConnection>();

        return await _transport.ConnectAsync(webSocketUrl.Value!, target.Port, label, timeout, cancellationToken).ConfigureAwait(false);
    }

    private static TimeSpan Remaining(TimeSpan timeout, Stopwatch stopwatch)
    {
        var left = timeout - stopwatch.Elapsed;
        return left > TimeSpan.Zero ? left : TimeSpan.FromMilliseconds(1);
    }

    /// <summary>
    /// Refuses a port that is not <paramref name="processId"/>'s (roadmap M5.5, as the reads do since M5.3): the browser
    /// whose window or port the policy allowed must be the one that answers. Fails closed when the owner cannot be read.
    /// </summary>
    private CdpResult<bool>? CheckOwner(int port, int processId)
    {
        var checkedPort = CdpEndpointValidator.ValidatePort(port);
        if (!checkedPort.Success)
            return checkedPort.FailAs<bool>();

        if (!_portOwners.TryGetListeningProcessId(port, out var owner))
        {
            return CdpResult<bool>.Fail(
                BrowserErrorCode.CdpPortOwnerMismatch,
                $"Could not read which process owns CDP port {port}, so it cannot be tied to the browser (process {processId}). " +
                "Nothing was done.");
        }

        if (owner is not int listener)
        {
            return CdpResult<bool>.Fail(
                BrowserErrorCode.CdpUnavailable,
                $"Nothing listens on {Host}:{port}: the browser may have exited, or the port is wrong. Nothing was done.");
        }

        return listener == processId
            ? null
            : CdpResult<bool>.Fail(
                BrowserErrorCode.CdpPortOwnerMismatch,
                $"CDP port {port} belongs to process {listener}, not to the browser's process {processId}. Nothing was done. " +
                "Pass the debug port of the browser that owns the window, or leave cdpPort out to use the window's own.");
    }

    /// <summary>CdpTargetNotFound for a CDP error that says no target has the id (the tab closed), else null.</summary>
    private static CdpResult<T>? NoSuchTarget<T>(CdpProtocolFault fault, string targetId) =>
        fault.Message.Contains("target", StringComparison.OrdinalIgnoreCase) &&
        (fault.Message.Contains("No target", StringComparison.OrdinalIgnoreCase) ||
         fault.Message.Contains("not found", StringComparison.OrdinalIgnoreCase))
            ? CdpResult<T>.Fail(BrowserErrorCode.CdpTargetNotFound,
                $"No tab with targetId {targetId}; it may have closed ({fault.Message}). Call list_tabs for the tabs there are now.")
            : null;

    private static string FormatMs(TimeSpan timeout) => $"{(long)timeout.TotalMilliseconds} ms";
}
