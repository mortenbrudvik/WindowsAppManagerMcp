using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using WindowsAppManagerMcp.Models;
using WindowsAppManagerMcp.Services.Interfaces;

namespace WindowsAppManagerMcp.Services;

/// <summary>
/// Service for interacting with browsers via Chrome DevTools Protocol (CDP).
/// Lists tabs over HTTP and reads one tab per call over one DevTools connection, through <see cref="ICdpTransport"/>.
/// </summary>
/// <remarks>
/// Every HTTP request, WebSocket connect and command is bounded by the caller's timeout, so a page that never answers
/// (for example because a JavaScript alert is open) cannot hang the tool call. Each read opens its own connection and
/// disposes it before it returns (roadmap M5.2): nothing CDP-related outlives the call, so no call can be handed a tab
/// or a connection another call chose.
/// </remarks>
public class CdpService : ICdpService
{
    private const string Host = CdpEndpointValidator.Host;

    /// <summary>
    /// The most tabs a window is bound among (one Browser.getWindowForTarget each); with more, the title alone decides.
    /// </summary>
    private const int MaxBoundTabs = 200;

    /// <summary>The longest the window binding may take (<see cref="BindingBudget"/>).</summary>
    private static readonly TimeSpan MaxBindingTime = TimeSpan.FromSeconds(2);

    private readonly ICdpTransport _transport;
    private readonly ITcpPortOwnerResolver _portOwnerResolver;

    /// <param name="transport">The DevTools HTTP and WebSocket transport.</param>
    /// <param name="portOwnerResolver">Tells which process listens on a debug port.</param>
    public CdpService(ICdpTransport transport, ITcpPortOwnerResolver portOwnerResolver)
    {
        _transport = transport;
        _portOwnerResolver = portOwnerResolver;
    }

    /// <inheritdoc />
    public async Task<CdpResult<IReadOnlyList<CdpTarget>>> GetPageTargetsAsync(
        int port,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        var listed = await _transport.GetJsonAsync(port, "/json/list", timeout, cancellationToken).ConfigureAwait(false);
        if (!listed.Success)
            return listed.FailAs<IReadOnlyList<CdpTarget>>();

        using var document = listed.Value!;
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Array)
        {
            return CdpResult<IReadOnlyList<CdpTarget>>.Fail(
                BrowserErrorCode.CdpProtocolError,
                $"{Host}:{port} did not return a CDP target list (a JSON {root.ValueKind} instead of an array). " +
                "Is this a browser debug port?");
        }

        IReadOnlyList<CdpTarget> pages = root.EnumerateArray()
            .Where(t => CdpReplies.GetString(t, "type") == "page")
            .Select(t => new CdpTarget(
                Id: CdpReplies.GetString(t, "id"),
                Type: "page",
                Title: CdpReplies.GetString(t, "title"),
                Url: CdpReplies.GetString(t, "url"),
                WebSocketDebuggerUrl: CdpReplies.GetString(t, "webSocketDebuggerUrl") is { Length: > 0 } url ? url : null,
                Port: port))
            .ToList();

        return CdpResult<IReadOnlyList<CdpTarget>>.Ok(pages);
    }

    /// <inheritdoc />
    public async Task<CdpResult<CdpBrowserVersion>> GetBrowserVersionAsync(
        int port,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        var answered = await _transport.GetJsonAsync(port, "/json/version", timeout, cancellationToken).ConfigureAwait(false);
        if (!answered.Success)
            return answered.FailAs<CdpBrowserVersion>();

        using var document = answered.Value!;
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
        {
            return CdpResult<CdpBrowserVersion>.Fail(
                BrowserErrorCode.CdpProtocolError,
                $"{Host}:{port} did not return a DevTools version for GET /json/version (a JSON {root.ValueKind} instead of " +
                "an object). Is this a browser debug port?");
        }

        // Chromium always lists the browser's absolute URL; a program that answers without one is not its DevTools
        // endpoint. Checked here, not by ValidateWebSocketUrl, whose CdpUnavailable ("nothing listens") would be asked again
        var listed = CdpReplies.GetString(root, "webSocketDebuggerUrl");
        if (listed.Length == 0)
        {
            return CdpResult<CdpBrowserVersion>.Fail(
                BrowserErrorCode.CdpProtocolError,
                $"{Host}:{port} answered GET /json/version without a webSocketDebuggerUrl for the browser. Is this a browser debug port?");
        }

        if (!Uri.TryCreate(listed, UriKind.Absolute, out _))
        {
            return CdpResult<CdpBrowserVersion>.Fail(
                BrowserErrorCode.CdpProtocolError,
                $"{Host}:{port} answered GET /json/version with a webSocketDebuggerUrl for the browser that is not an absolute " +
                "URL. Is this a browser debug port?");
        }

        var webSocketUrl = CdpEndpointValidator.ValidateWebSocketUrl(listed, port, "the browser");
        if (!webSocketUrl.Success)
            return webSocketUrl.FailAs<CdpBrowserVersion>();

        if (!webSocketUrl.Value!.AbsolutePath.StartsWith("/devtools/browser/", StringComparison.Ordinal))
        {
            return CdpResult<CdpBrowserVersion>.Fail(
                BrowserErrorCode.CdpProtocolError,
                $"Refusing the browser WebSocket URL '{webSocketUrl.Value}' that {Host}:{port} listed: a browser's is under " +
                "/devtools/browser/. Is this a browser debug port?");
        }

        return CdpResult<CdpBrowserVersion>.Ok(new CdpBrowserVersion(
            Browser: CdpReplies.GetString(root, "Browser"),
            ProtocolVersion: CdpReplies.GetString(root, "Protocol-Version"),
            WebSocketDebuggerUrl: webSocketUrl.Value,
            Port: port));
    }

    /// <inheritdoc />
    public async Task<CdpResult<CdpTarget>> FindTargetForWindowAsync(
        int port,
        CdpWindow window,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(window);
        var checkedPort = CdpEndpointValidator.ValidatePort(port);
        if (!checkedPort.Success)
            return checkedPort.FailAs<CdpTarget>();

        // Fails closed since M5.3, as the targetId path does: CDP is now used without cdpPort, and a title is no proof that
        // whatever answers on the port is the window's browser (a program on the port could list tabs with that title)
        if (!_portOwnerResolver.TryGetListeningProcessId(port, out var portOwner))
        {
            return CdpResult<CdpTarget>.Fail(
                BrowserErrorCode.CdpPortOwnerMismatch,
                $"Could not read which process owns CDP port {port}, so it cannot be tied to the window of process " +
                $"{DescribeProcess(window.ProcessId)}. Nothing was read. Check that the browser is still running on that " +
                "port, or leave cdpPort out to use the window's own debug port.");
        }

        if (portOwner is not int owner)
            return NothingListens<CdpTarget>(port, "leave cdpPort out to use the window's own debug port");

        if (owner != window.ProcessId)
            return PortOwnerMismatch<CdpTarget>(port, owner, window.ProcessId);

        var stopwatch = Stopwatch.StartNew();
        var targets = await GetPageTargetsAsync(port, timeout, cancellationToken).ConfigureAwait(false);
        if (!targets.Success)
            return targets.FailAs<CdpTarget>();

        var all = targets.Value!;
        if (window.Geometry is { } geometry && all.Count is > 1 and <= MaxBoundTabs)
        {
            // Best effort, so only a share of the budget: a stalled browser connection leaves the page read its time
            var windows = await GetBrowserWindowsAsync(port, all, BindingBudget(timeout - stopwatch.Elapsed), cancellationToken)
                .ConfigureAwait(false);
            if (windows is { Count: > 0 })
            {
                var ids = CdpWindowBinder.MatchingWindows(geometry, windows.Values);
                var inWindow = all.Where(t => windows.TryGetValue(t.Id, out var w) && ids.Contains(w.WindowId)).ToList();
                if (inWindow.Count > 0)
                {
                    var bound = CdpTargetMatcher.Match(window.Title, inWindow);
                    if (bound.Success || bound.ErrorCode == BrowserErrorCode.CdpTargetAmbiguous)
                        return bound;
                }
            }
        }

        // Last resort, as before M5.3: the title among all tabs
        return CdpTargetMatcher.Match(window.Title, all);
    }

    /// <summary>
    /// The time the window binding may take of what is left of the budget: a third, at most two seconds, so the title
    /// fallback and the page read keep most of it when the browser's connection stalls.
    /// </summary>
    public static TimeSpan BindingBudget(TimeSpan remaining)
    {
        if (remaining <= TimeSpan.Zero)
            return TimeSpan.Zero;

        var third = remaining / 3;
        return third < MaxBindingTime ? third : MaxBindingTime;
    }

    /// <summary>
    /// The browser window of each tab (Browser.getWindowForTarget, one command per tab over one connection to the
    /// browser's DevTools URL from /json/version, closed before it returns); a tab the browser says is in no browser
    /// window (an error reply: not a tab of the tab strip) is left out. Null when the browser's connection cannot be used
    /// or any reply did not come (timed out, connection closed), so the caller falls back to the title among all tabs: a
    /// partial binding could narrow the tabs to the wrong ones.
    /// </summary>
    private async Task<IReadOnlyDictionary<string, CdpBrowserWindow>?> GetBrowserWindowsAsync(
        int port,
        IReadOnlyList<CdpTarget> targets,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        if (timeout <= TimeSpan.Zero)
            return null;

        var version = await GetBrowserVersionAsync(port, timeout, cancellationToken).ConfigureAwait(false);
        var left = timeout - stopwatch.Elapsed;
        if (!version.Success || left <= TimeSpan.Zero)
            return null;

        var connected = await _transport
            .ConnectAsync(version.Value!.WebSocketDebuggerUrl, port, "the browser", left, cancellationToken)
            .ConfigureAwait(false);
        if (!connected.Success)
            return null;

        await using var connection = connected.Value!;
        var remaining = timeout - stopwatch.Elapsed;
        if (remaining <= TimeSpan.Zero)
            return null;

        var sends = targets
            .Select(t => (t.Id, Reply: connection.SendAsync("Browser.getWindowForTarget", new { targetId = t.Id }, remaining, cancellationToken)))
            .ToList();
        await Task.WhenAll(sends.Select(s => s.Reply)).ConfigureAwait(false);

        var windows = new Dictionary<string, CdpBrowserWindow>(StringComparer.Ordinal);
        foreach (var (id, reply) in sends)
        {
            var answered = reply.Result;
            if (!answered.Success)
                return null;

            if (answered.Value!.Error == null && ParseBrowserWindow(answered.Value.Result) is { } browserWindow)
                windows[id] = browserWindow;
        }

        return windows;
    }

    /// <summary>Browser.getWindowForTarget's {windowId, bounds: {left, top, width, height, windowState}}, or null.</summary>
    private static CdpBrowserWindow? ParseBrowserWindow(JsonElement result)
    {
        if (result.ValueKind != JsonValueKind.Object ||
            !TryGetInt(result, "windowId", out var windowId) ||
            !result.TryGetProperty("bounds", out var bounds) || bounds.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var state = CdpReplies.GetString(bounds, "windowState");
        TryGetInt(bounds, "left", out var left);
        TryGetInt(bounds, "top", out var top);
        TryGetInt(bounds, "width", out var width);
        TryGetInt(bounds, "height", out var height);
        return new CdpBrowserWindow(windowId, left, top, width, height, state.Length > 0 ? state : "normal");
    }

    private static bool TryGetInt(JsonElement element, string name, out int value)
    {
        value = 0;
        return element.TryGetProperty(name, out var property) &&
               property.ValueKind == JsonValueKind.Number &&
               property.TryGetInt32(out value);
    }

    /// <inheritdoc />
    public async Task<CdpResult<CdpTarget>> FindTargetByIdAsync(
        int port,
        int windowProcessId,
        string targetId,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        // A port outside 1-65535 has no owner to read; say the port is wrong, not that the browser may be gone
        var checkedPort = CdpEndpointValidator.ValidatePort(port);
        if (!checkedPort.Success)
            return checkedPort.FailAs<CdpTarget>();

        if (!_portOwnerResolver.TryGetListeningProcessId(port, out var portOwner))
        {
            return CdpResult<CdpTarget>.Fail(
                BrowserErrorCode.CdpPortOwnerMismatch,
                $"Could not read which process owns CDP port {port}, so tab {targetId} cannot be tied to the window of " +
                $"process {DescribeProcess(windowProcessId)}. Nothing was read. Check that the browser is still running " +
                "on that port, or pass the window without targetId.");
        }

        if (portOwner is not int owner)
            return NothingListens<CdpTarget>(port, "pass the window without targetId");

        if (owner != windowProcessId)
            return PortOwnerMismatch<CdpTarget>(port, owner, windowProcessId);

        if (!CdpEndpointValidator.IsValidTargetId(targetId))
        {
            return CdpResult<CdpTarget>.Fail(
                BrowserErrorCode.CdpTargetNotFound,
                "The targetId is not a CDP tab id (1 to 128 letters, digits, '.', '_' or '-'), so no tab was looked up.");
        }

        var targets = await GetPageTargetsAsync(port, timeout, cancellationToken).ConfigureAwait(false);
        if (!targets.Success)
            return targets.FailAs<CdpTarget>();

        var target = targets.Value!.FirstOrDefault(t => string.Equals(t.Id, targetId, StringComparison.Ordinal));
        return target != null ? CdpResult<CdpTarget>.Ok(target) : TargetIdNotFound(port, targetId);
    }

    /// <inheritdoc />
    public async Task<CdpResult<CdpPageSnapshot>> GetPageSnapshotAsync(
        CdpTarget target,
        CdpPageRequest request,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var label = $"tab {target.Id}";
        var webSocketUrl = CdpEndpointValidator.ValidateWebSocketUrl(target.WebSocketDebuggerUrl, target.Port, label);
        if (!webSocketUrl.Success)
            return webSocketUrl.FailAs<CdpPageSnapshot>();

        var stopwatch = Stopwatch.StartNew();
        var connected = await _transport
            .ConnectAsync(webSocketUrl.Value!, target.Port, label, timeout, cancellationToken)
            .ConfigureAwait(false);
        if (!connected.Success)
            return connected.FailAs<CdpPageSnapshot>();

        CdpResult<CdpReply> evaluated;
        Task<CdpResult<CdpReply>> enable;
        await using (var connection = connected.Value!)
        {
            var remaining = timeout - stopwatch.Elapsed;

            // Page.enable makes the browser report JavaScript dialogs (Page.javascriptDialogOpening), which explains
            // why Runtime.evaluate does not answer. Its reply is not awaited: a page blocked by a dialog may never send it.
            enable = connection.SendAsync("Page.enable", null, remaining, cancellationToken);
            var evaluate = connection.SendAsync("Runtime.evaluate", new
            {
                expression = BuildSnapshotExpression(request),
                returnByValue = true,
                awaitPromise = false,
                timeout = (int)timeout.TotalMilliseconds
            }, remaining, cancellationToken);

            try
            {
                evaluated = await evaluate.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                _ = enable.ContinueWith(static t => _ = t.Exception, TaskScheduler.Default);
                throw;
            }
        }

        if (evaluated.Success)
        {
            return evaluated.Value!.Error is { } fault
                ? CdpReplies.ToFailure<CdpPageSnapshot>("Runtime.evaluate", fault)
                : ParseEvaluateResult(evaluated.Value.Result, request);
        }

        var failure = evaluated.ErrorCode == BrowserErrorCode.CdpTimeout
            ? CdpResult<CdpPageSnapshot>.Fail(
                BrowserErrorCode.CdpTimeout,
                $"The page in tab {target.Id} did not answer within {FormatMs(timeout)}. It probably has a " +
                "JavaScript dialog (alert/confirm/prompt/beforeunload) open, is paused in the debugger, or is busy. " +
                "If a dialog is open, inspect it (capture_window) and decide deliberately how to close it, " +
                "or ask the user, before you retry; do not press Enter blindly, since Enter accepts a confirm " +
                "or beforeunload dialog. Or read the window with UI Automation (useCdp=false).")
            : evaluated.FailAs<CdpPageSnapshot>();

        // A refused Page.enable never fails a read that worked; it explains one that did not
        if (enable.IsCompletedSuccessfully && enable.Result is { Success: true, Value.Error: { } enableFault })
        {
            failure = failure with
            {
                Error = $"{failure.Error} Page.enable was refused: {enableFault.Message} ({enableFault.Code}), so dialogs " +
                        "could not be detected."
            };
        }

        return failure;
    }

    private static CdpResult<CdpPageSnapshot> ParseEvaluateResult(JsonElement result, CdpPageRequest request)
    {
        if (result.ValueKind != JsonValueKind.Object)
        {
            return CdpResult<CdpPageSnapshot>.Fail(
                BrowserErrorCode.CdpProtocolError,
                "The Runtime.evaluate response has no result.");
        }

        if (result.TryGetProperty("exceptionDetails", out var exceptionDetails))
        {
            // Only a program that is not a real DevTools endpoint sends another kind; reading it as an object would throw
            if (exceptionDetails.ValueKind != JsonValueKind.Object)
            {
                return CdpResult<CdpPageSnapshot>.Fail(
                    BrowserErrorCode.CdpProtocolError,
                    "The Runtime.evaluate response could not be read: its exceptionDetails is a JSON " +
                    $"{exceptionDetails.ValueKind}, not an object.");
            }

            return CdpResult<CdpPageSnapshot>.Fail(
                BrowserErrorCode.CdpEvaluationFailed,
                $"The page threw while its content was read: {DescribeException(exceptionDetails)}");
        }

        if (!result.TryGetProperty("result", out var remoteObject) ||
            remoteObject.ValueKind != JsonValueKind.Object ||
            !remoteObject.TryGetProperty("value", out var value) ||
            value.ValueKind != JsonValueKind.Object)
        {
            return CdpResult<CdpPageSnapshot>.Fail(
                BrowserErrorCode.CdpProtocolError,
                "The Runtime.evaluate response does not contain the page snapshot.");
        }

        // The script always reports where its slice starts and the whole length it measured
        if (ReadCount(value, "offset") is not int offset || ReadCount(value, "totalLength") is not int totalLength)
        {
            return CdpResult<CdpPageSnapshot>.Fail(
                BrowserErrorCode.CdpProtocolError,
                "The Runtime.evaluate response does not say where the page's slice starts or how long its content is.");
        }

        // The script runs in the page's world, where the page can replace what it calls (Math.min, String.prototype.slice):
        // a slice that is not the one asked for would make paging repeat or skip text, or never end
        var content = CdpReplies.GetString(value, "content");
        if (offset != Math.Min(Math.Max(0, request.Offset), totalLength) ||
            content.Length > Math.Max(1, request.MaxLength) || content.Length > totalLength - offset)
        {
            return CdpResult<CdpPageSnapshot>.Fail(
                BrowserErrorCode.CdpProtocolError,
                $"The page returned another slice than the one asked for (offset {offset} and {content.Length} characters of " +
                $"{totalLength}, for offset {request.Offset} and at most {request.MaxLength}), so it was not used. The page may " +
                "have replaced the functions the read uses; read it with UI Automation (useCdp: false).");
        }

        if (request.Selector != null && ReadCount(value, "matches") == 0)
        {
            return CdpResult<CdpPageSnapshot>.Fail(
                BrowserErrorCode.ContentExtractionFailed,
                $"No element on the page matches the selector '{request.Selector}', so nothing was read. Check the selector, " +
                "or leave it out to read the whole page.");
        }

        return CdpResult<CdpPageSnapshot>.Ok(new CdpPageSnapshot(
            Title: CdpReplies.GetString(value, "title"),
            Url: CdpReplies.GetString(value, "url"),
            Content: content,
            Offset: offset,
            TotalLength: totalLength));
    }

    /// <summary>A count the script reported: a whole number from 0 to <see cref="int.MaxValue"/>, else null.</summary>
    private static int? ReadCount(JsonElement value, string name) =>
        value.TryGetProperty(name, out var count) && count.ValueKind == JsonValueKind.Number &&
        count.TryGetInt32(out var number) && number >= 0
            ? number
            : null;

    private static string DescribeException(JsonElement exceptionDetails)
    {
        if (exceptionDetails.TryGetProperty("exception", out var exception) &&
            exception.ValueKind == JsonValueKind.Object &&
            CdpReplies.GetString(exception, "description") is { Length: > 0 } description)
        {
            return description;
        }

        return CdpReplies.GetString(exceptionDetails, "text") is { Length: > 0 } text ? text : "unknown exception";
    }

    /// <summary>
    /// Builds the script that reads title, URL and one slice of the content in one round trip (roadmap M5.6). The page
    /// measures the whole text (innerText) or HTML (outerHTML), of the body or of each element the selector matches (one per
    /// line), and returns only the slice from the offset, at most maxLength UTF-16 characters, never ending between the two
    /// halves of a surrogate pair, with the offset it starts at and the whole length: only the slice crosses the DevTools
    /// connection, so a page of any size can be read a slice at a time. The selector goes into the script as a JSON string
    /// literal, so it is only ever data; one that is not valid CSS makes querySelectorAll throw (CdpEvaluationFailed).
    /// </summary>
    private static string BuildSnapshotExpression(CdpPageRequest request)
    {
        var offset = Math.Max(0, request.Offset).ToString(CultureInfo.InvariantCulture);
        var max = Math.Max(1, request.MaxLength).ToString(CultureInfo.InvariantCulture);
        string content;
        if (request.Selector == null)
        {
            var whole = request.TextOnly
                ? "document.body ? document.body.innerText : ''"
                : "document.documentElement ? document.documentElement.outerHTML : ''";
            content = $"const matches = null; const content = String({whole}); ";
        }
        else
        {
            // A plain loop, not Array.from: Prototype.js and MooTools replace Array.from with one that ignores its map function
            var each = request.TextOnly
                ? "typeof e.innerText === 'string' ? e.innerText : (e.textContent || '')"
                : "e.outerHTML";
            content = $"const found = document.querySelectorAll({JsonSerializer.Serialize(request.Selector)}); " +
                      "const matches = found.length; const parts = []; " +
                      $"for (let i = 0; i < found.length; i++) {{ const e = found[i]; parts.push({each}); }} " +
                      "const content = parts.join('\\n'); ";
        }

        return "(() => { " +
               content +
               $"const offset = {offset}; const max = {max}; " +
               "const total = content.length; " +
               "const start = Math.min(offset, total); " +
               "let end = Math.min(total, start + max); " +
               "if (end < total && end - start > 1) { const c = content.charCodeAt(end - 1); if (c >= 0xD800 && c <= 0xDBFF) { end--; } } " +
               "return { title: document.title, url: location.href, content: content.slice(start, end), offset: start, totalLength: total, matches: matches }; " +
               "})()";
    }

    /// <inheritdoc />
    public async Task<CdpResult<bool>> CloseBrowserAsync(
        int port,
        int browserProcessId,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        var checkedPort = CdpEndpointValidator.ValidatePort(port);
        if (!checkedPort.Success)
            return checkedPort.FailAs<bool>();

        // A browser-level connection controls the whole browser: only the browser's own port is closed
        if (!_portOwnerResolver.TryGetListeningProcessId(port, out var portOwner))
        {
            return CdpResult<bool>.Fail(
                BrowserErrorCode.CdpPortOwnerMismatch,
                $"Could not read which process owns CDP port {port}, so nothing was sent to it.");
        }

        if (portOwner is not int owner)
            return NothingListens<bool>(port, "check that the browser still runs");

        if (owner != browserProcessId)
        {
            return CdpResult<bool>.Fail(
                BrowserErrorCode.CdpPortOwnerMismatch,
                $"CDP port {port} belongs to process {DescribeProcess(owner)}, not to the browser (process " +
                $"{DescribeProcess(browserProcessId)}), so nothing was sent to it.");
        }

        var stopwatch = Stopwatch.StartNew();
        var version = await GetBrowserVersionAsync(port, timeout, cancellationToken).ConfigureAwait(false);
        if (!version.Success)
            return version.FailAs<bool>();

        var left = timeout - stopwatch.Elapsed;
        if (left <= TimeSpan.Zero)
            return CdpResult<bool>.Fail(BrowserErrorCode.CdpTimeout, $"The browser on port {port} did not answer within {FormatMs(timeout)}.");

        var connected = await _transport
            .ConnectAsync(version.Value!.WebSocketDebuggerUrl, port, "the browser", left, cancellationToken)
            .ConfigureAwait(false);
        if (!connected.Success)
            return connected.FailAs<bool>();

        await using var connection = connected.Value!;
        left = timeout - stopwatch.Elapsed;
        var reply = await connection
            .SendAsync("Browser.close", null, left > TimeSpan.Zero ? left : TimeSpan.FromMilliseconds(1), cancellationToken)
            .ConfigureAwait(false);
        if (reply.Success)
        {
            return reply.Value!.Error is { } fault
                ? CdpResult<bool>.Fail(BrowserErrorCode.CdpProtocolError, $"The browser refused Browser.close: {fault.Message} ({fault.Code}).")
                : CdpResult<bool>.Ok(true);
        }

        // A browser that closes may end the connection before it answers; a busy one may not answer in time although it
        // got Browser.close (or a stalled send may have delivered it), so the caller still waits for it to exit
        return reply.ErrorCode switch
        {
            BrowserErrorCode.CdpTargetClosed => CdpResult<bool>.Ok(true),
            BrowserErrorCode.CdpTimeout => CdpResult<bool>.Ok(false),
            _ => reply.FailAs<bool>()
        };
    }

    private static CdpResult<T> PortOwnerMismatch<T>(int port, int portOwner, int windowProcessId) => CdpResult<T>.Fail(
        BrowserErrorCode.CdpPortOwnerMismatch,
        $"CDP port {port} belongs to process {DescribeProcess(portOwner)}, but the window belongs to " +
        $"process {DescribeProcess(windowProcessId)}. Pass the debug port of the browser instance that owns " +
        "this window (for example the cdpPort that launch_browser_with_debug returned), or leave cdpPort out to use the " +
        "window's own debug port.");

    private static CdpResult<T> NothingListens<T>(int port, string retry) => CdpResult<T>.Fail(
        BrowserErrorCode.CdpUnavailable,
        $"Nothing listens on {Host}:{port}. Nothing was read: the browser may have exited, or the port is wrong. " +
        $"Check the port, or {retry}.");

    private static CdpResult<CdpTarget> TargetIdNotFound(int port, string targetId) => CdpResult<CdpTarget>.Fail(
        BrowserErrorCode.CdpTargetNotFound,
        $"No tab with targetId {targetId} on port {port}; it may have closed. Pass the window without targetId, " +
        "or a targetId from a newer result.");

    private static string FormatMs(TimeSpan timeout) =>
        $"{(long)timeout.TotalMilliseconds} ms";

    private static string DescribeProcess(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            return $"{processId} ({process.ProcessName})";
        }
        catch
        {
            return processId.ToString(CultureInfo.InvariantCulture);
        }
    }
}
