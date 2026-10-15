using System.Diagnostics;
using WindowsAppManagerMcp.Models;
using WindowsAppManagerMcp.Services.Interfaces;

namespace WindowsAppManagerMcp.Services;

/// <summary>
/// Service for reading URLs and content from browser windows using UI Automation or CDP.
/// When a CDP port is specified, CDP is tried first; every result says which method produced it
/// (Source) and, after a fallback to UI Automation, why CDP was not used (CdpError).
/// </summary>
/// <remarks>
/// The UI Automation reads go through <see cref="IUiaTree"/> as the other UI Automation tools' reads do (roadmap M3.F24,
/// <see cref="BrowserUiaReader"/>): one call on the <see cref="IUiaWorker"/>, counted against the browser's process, under
/// the call's timeoutMs of its own (the time CDP took does not count against it). Before it, as for the other reads of a
/// window, the user's target policy is checked (TargetDeniedByPolicy, <see cref="UiaTargets.ReadVerdict"/>), and then whether
/// the window responds (NotResponding). get_browser_content warms up the page first (<see cref="IAccessibilityWarmup"/>,
/// roadmap M3.3), within a third of the timeout, so the first read of a page whose accessibility tree Chromium has not built
/// yet does not come back empty (D-BRW-15); get_browser_url does not wait for the page. Both read the shown tab's page
/// through its render widget (<see cref="IAccessibilityWarmup.FindPages"/>), whose Document holds the address the tab
/// committed, and get_browser_url the address bar as well (roadmap M5.7); asking for the page's element turns on Chromium's
/// accessibility tree for it, as any UI Automation client does. A child window's handle (a render widget's, from an
/// element) is read as its top-level window's (GA_ROOT). The CDP reads are not covered by the target policy: they read the
/// debug browser through its port.
/// </remarks>
public class BrowserReadingService : IBrowserReadingService
{
    private const string CdpPortRequiredMessage =
        "requireCdp=true (or targetId) needs cdpPort: the debug port of the browser instance that owns the window.";

    private readonly INativeWindowWrapper _nativeWrapper;
    private readonly IBrowserDetectionService _browserDetection;
    private readonly ICdpService _cdpService;
    private readonly IUiaTree _tree;
    private readonly IUiaWorker _worker;
    private readonly BrowserUiaReader _reader;
    private readonly IAccessibilityWarmup? _warmup;
    private readonly ITargetPolicy? _policy;

    // Map process names to browser types
    private static readonly Dictionary<string, string> ProcessToBrowserType = new(StringComparer.OrdinalIgnoreCase)
    {
        // Chrome family
        { "chrome", "chrome" },
        { "chromium", "chromium" },

        // Edge
        { "msedge", "edge" },

        // Firefox family
        { "firefox", "firefox" },
        { "waterfox", "firefox" },
        { "librewolf", "firefox" },
        { "floorp", "firefox" },

        // Brave
        { "brave", "brave" },

        // Opera
        { "opera", "opera" },

        // Vivaldi
        { "vivaldi", "vivaldi" },

        // Arc
        { "arc", "chrome" }
    };

    /// <param name="nativeWrapper">The window and process reads.</param>
    /// <param name="browserDetection">Tells browsers from other programs.</param>
    /// <param name="cdpService">The CDP reads.</param>
    /// <param name="tree">The UI Automation tree the UI Automation reads go through (roadmap M3.F24).</param>
    /// <param name="worker">Runs the UI Automation reads under their deadline.</param>
    /// <param name="warmup">Warms up the page before get_browser_content reads it (roadmap M3.3). The server always passes
    /// it; without it no page is warmed up.</param>
    /// <param name="policy">The user's target policy, which refuses the UI Automation reads of a window of an app it denies.
    /// The server always passes it; without it no window is refused.</param>
    public BrowserReadingService(
        INativeWindowWrapper nativeWrapper,
        IBrowserDetectionService browserDetection,
        ICdpService cdpService,
        IUiaTree tree,
        IUiaWorker worker,
        IAccessibilityWarmup? warmup = null,
        ITargetPolicy? policy = null)
    {
        _nativeWrapper = nativeWrapper;
        _browserDetection = browserDetection;
        _cdpService = cdpService;
        _tree = tree;
        _worker = worker;
        _reader = new BrowserUiaReader(tree);
        _warmup = warmup;
        _policy = policy;
    }

    /// <inheritdoc />
    public async Task<BrowserUrlResult> GetBrowserUrlAsync(
        nint browserHandle,
        CdpOptions? cdp = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            // Validate handle
            if (browserHandle == nint.Zero || !_nativeWrapper.IsWindow(browserHandle))
            {
                return new BrowserUrlResult(
                    Success: false,
                    Url: null,
                    BrowserType: null,
                    PageTitle: null,
                    Error: "Invalid window handle",
                    ErrorCode: BrowserErrorCode.InvalidHandle);
            }

            browserHandle = TopLevel(browserHandle);

            // Get process info
            var processId = _nativeWrapper.GetWindowThreadProcessId(browserHandle);
            var processName = GetProcessName(processId);

            // Check if it's a browser
            if (!_browserDetection.IsBrowser(processName))
            {
                return new BrowserUrlResult(
                    Success: false,
                    Url: null,
                    BrowserType: null,
                    PageTitle: null,
                    Error: $"Window process '{processName}' is not a recognized browser",
                    ErrorCode: BrowserErrorCode.NotABrowser);
            }

            // Get browser type
            var browserType = GetBrowserType(browserHandle, processName) ?? "unknown";

            // Get window title (page title)
            var pageTitle = _nativeWrapper.GetWindowText(browserHandle);

            // Try CDP first if a port is specified
            string? cdpError = null;
            if (cdp?.Port is int port)
            {
                var match = await FindTargetAsync(port, (int)processId, pageTitle, cdp, cdp.EffectiveTimeout, cancellationToken);

                if (match.Success)
                {
                    var target = match.Value!;
                    return new BrowserUrlResult(
                        Success: true,
                        Url: target.Url,
                        BrowserType: browserType,
                        PageTitle: target.Title,
                        Error: null,
                        ErrorCode: BrowserErrorCode.None,
                        Source: BrowserReadSource.Cdp,
                        TargetId: target.Id);
                }

                if (cdp.EffectiveRequired)
                {
                    return new BrowserUrlResult(
                        Success: false,
                        Url: null,
                        BrowserType: browserType,
                        PageTitle: pageTitle,
                        Error: match.Error,
                        ErrorCode: match.ErrorCode,
                        Source: BrowserReadSource.Cdp,
                        CdpError: match.DescribeError());
                }

                cdpError = match.DescribeError();
            }
            else if (cdp?.EffectiveRequired == true)
            {
                return new BrowserUrlResult(
                    Success: false,
                    Url: null,
                    BrowserType: browserType,
                    PageTitle: pageTitle,
                    Error: CdpPortRequiredMessage,
                    ErrorCode: BrowserErrorCode.CdpUnavailable);
            }

            // Fall back to UI Automation
            var read = await ReadThroughUiaAsync(
                browserHandle, pageTitle, content: null, (cdp ?? new CdpOptions()).EffectiveTimeout, cancellationToken);
            if (read.ErrorCode != BrowserErrorCode.None)
            {
                return new BrowserUrlResult(
                    Success: false,
                    Url: null,
                    BrowserType: browserType,
                    PageTitle: pageTitle,
                    Error: read.Error,
                    ErrorCode: read.ErrorCode,
                    Source: BrowserReadSource.Uia,
                    CdpError: cdpError);
            }

            // The window's title as UI Automation names it, else its window text
            pageTitle = read.Title ?? pageTitle;
            var url = read.Url ?? WindowWithoutElement;

            return new BrowserUrlResult(
                Success: url.ErrorCode == BrowserErrorCode.None,
                Url: url.Url,
                BrowserType: browserType,
                PageTitle: pageTitle,
                Error: url.Error,
                ErrorCode: url.ErrorCode,
                Source: BrowserReadSource.Uia,
                CdpError: cdpError,
                DisplayUrl: url.DisplayUrl,
                UrlSource: url.UrlSource,
                IsEditing: url.IsEditing);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new BrowserUrlResult(
                Success: false,
                Url: null,
                BrowserType: null,
                PageTitle: null,
                Error: $"Exception: {ex.Message}",
                ErrorCode: BrowserErrorCode.OperationException);
        }
    }

    /// <inheritdoc />
    public async Task<BrowserContentResult> GetBrowserContentAsync(
        nint browserHandle,
        BrowserContentOptions? options = null,
        CdpOptions? cdp = null,
        CancellationToken cancellationToken = default)
    {
        options ??= new BrowserContentOptions();

        try
        {
            // Validate handle
            if (browserHandle == nint.Zero || !_nativeWrapper.IsWindow(browserHandle))
            {
                return new BrowserContentResult(
                    Success: false,
                    Content: null,
                    Url: null,
                    BrowserType: null,
                    PageTitle: null,
                    ContentLength: null,
                    Error: "Invalid window handle",
                    ErrorCode: BrowserErrorCode.InvalidHandle);
            }

            browserHandle = TopLevel(browserHandle);

            // Get process info
            var processId = _nativeWrapper.GetWindowThreadProcessId(browserHandle);
            var processName = GetProcessName(processId);

            // Check if it's a browser
            if (!_browserDetection.IsBrowser(processName))
            {
                return new BrowserContentResult(
                    Success: false,
                    Content: null,
                    Url: null,
                    BrowserType: null,
                    PageTitle: null,
                    ContentLength: null,
                    Error: $"Window process '{processName}' is not a recognized browser",
                    ErrorCode: BrowserErrorCode.NotABrowser);
            }

            // Get browser type
            var browserType = GetBrowserType(browserHandle, processName) ?? "unknown";

            // Get window title (page title)
            var pageTitle = _nativeWrapper.GetWindowText(browserHandle);

            // Try CDP first if a port is specified
            string? cdpError = null;
            if (cdp?.Port is int port)
            {
                var (snapshot, targetId) = await ReadContentViaCdpAsync(
                    port, (int)processId, pageTitle, cdp, options, cancellationToken);

                if (snapshot.Success)
                {
                    var page = snapshot.Value!;
                    var content = Truncate(page.Content, options.MaxLength);
                    return new BrowserContentResult(
                        Success: true,
                        Content: content,
                        Url: page.Url,
                        BrowserType: browserType,
                        PageTitle: page.Title,
                        ContentLength: content.Length,
                        Error: null,
                        ErrorCode: BrowserErrorCode.None,
                        Source: BrowserReadSource.Cdp,
                        TargetId: targetId);
                }

                // A page too large for CDP is reported as it is, not as HtmlRequiresCdp's "CDP could not be used"
                if (cdp.EffectiveRequired ||
                    (!options.TextOnly && snapshot.ErrorCode == BrowserErrorCode.CdpResponseTooLarge))
                {
                    return new BrowserContentResult(
                        Success: false,
                        Content: null,
                        Url: null,
                        BrowserType: browserType,
                        PageTitle: pageTitle,
                        ContentLength: null,
                        Error: snapshot.Error,
                        ErrorCode: snapshot.ErrorCode,
                        Source: BrowserReadSource.Cdp,
                        TargetId: targetId,
                        CdpError: snapshot.DescribeError());
                }

                cdpError = snapshot.DescribeError();
            }
            else if (cdp?.EffectiveRequired == true)
            {
                return new BrowserContentResult(
                    Success: false,
                    Content: null,
                    Url: null,
                    BrowserType: browserType,
                    PageTitle: pageTitle,
                    ContentLength: null,
                    Error: CdpPortRequiredMessage,
                    ErrorCode: BrowserErrorCode.CdpUnavailable);
            }

            // UI Automation only reads text. Returning text when HTML was asked for would
            // silently change the format, so report it instead.
            if (!options.TextOnly)
            {
                var reason = cdpError != null
                    ? $"CDP could not be used ({cdpError})."
                    : "No cdpPort was given; pass the cdpPort that launch_browser_with_debug returned (for a browser started otherwise, its --remote-debugging-port).";
                return new BrowserContentResult(
                    Success: false,
                    Content: null,
                    Url: null,
                    BrowserType: browserType,
                    PageTitle: pageTitle,
                    ContentLength: null,
                    Error: $"textOnly=false returns HTML, which is only available via CDP. {reason} " +
                           "Use textOnly=true to read the page text with UI Automation.",
                    ErrorCode: BrowserErrorCode.HtmlRequiresCdp,
                    CdpError: cdpError);
            }

            // Fall back to UI Automation
            var read = await ReadThroughUiaAsync(
                browserHandle, pageTitle, options, (cdp ?? new CdpOptions()).EffectiveTimeout, cancellationToken);
            if (read.ErrorCode != BrowserErrorCode.None)
            {
                return new BrowserContentResult(
                    Success: false,
                    Content: null,
                    Url: null,
                    BrowserType: browserType,
                    PageTitle: pageTitle,
                    ContentLength: null,
                    Error: read.Error,
                    ErrorCode: read.ErrorCode,
                    Source: BrowserReadSource.Uia,
                    CdpError: cdpError,
                    AccessibilityWarmup: read.Warmup);
            }

            // The window's title as UI Automation names it, else its window text
            pageTitle = read.Title ?? pageTitle;
            if (read.Content is not { Found: true } shown)
            {
                return new BrowserContentResult(
                    Success: false,
                    Content: null,
                    Url: null,
                    BrowserType: browserType,
                    PageTitle: pageTitle,
                    ContentLength: null,
                    Error: "Found no page in the browser window through UI Automation: no shown, onscreen Document of a tab. " +
                           "The tab may still be loading, or the browser's accessibility tree is not available; read again, " +
                           "or read it through CDP (cdpPort).",
                    ErrorCode: BrowserErrorCode.ContentAreaNotFound,
                    Source: BrowserReadSource.Uia,
                    CdpError: cdpError,
                    AccessibilityWarmup: read.Warmup);
            }

            var uiaContent = shown.Text;
            if (string.IsNullOrEmpty(uiaContent))
            {
                return new BrowserContentResult(
                    Success: false,
                    Content: null,
                    Url: shown.Url,
                    BrowserType: browserType,
                    PageTitle: pageTitle,
                    ContentLength: null,
                    Error: "Could not extract content from browser",
                    ErrorCode: BrowserErrorCode.ContentExtractionFailed,
                    Source: BrowserReadSource.Uia,
                    CdpError: cdpError,
                    AccessibilityWarmup: read.Warmup);
            }

            return new BrowserContentResult(
                Success: true,
                Content: uiaContent,
                Url: shown.Url,
                BrowserType: browserType,
                PageTitle: pageTitle,
                ContentLength: uiaContent.Length,
                Error: null,
                ErrorCode: BrowserErrorCode.None,
                Source: BrowserReadSource.Uia,
                CdpError: cdpError,
                AccessibilityWarmup: read.Warmup);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new BrowserContentResult(
                Success: false,
                Content: null,
                Url: null,
                BrowserType: null,
                PageTitle: null,
                ContentLength: null,
                Error: $"Exception: {ex.Message}",
                ErrorCode: BrowserErrorCode.OperationException);
        }
    }

    /// <inheritdoc />
    public async Task<BrowserUrlResult> GetActiveBrowserUrlAsync(
        CdpOptions? cdp = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            // Get foreground window
            var foregroundHandle = _nativeWrapper.GetForegroundWindow();

            if (foregroundHandle == nint.Zero)
            {
                return new BrowserUrlResult(
                    Success: false,
                    Url: null,
                    BrowserType: null,
                    PageTitle: null,
                    Error: "No foreground window found",
                    ErrorCode: BrowserErrorCode.NoBrowserInForeground);
            }

            // Check if it's a browser
            var processId = _nativeWrapper.GetWindowThreadProcessId(foregroundHandle);
            var processName = GetProcessName(processId);

            if (!_browserDetection.IsBrowser(processName))
            {
                return new BrowserUrlResult(
                    Success: false,
                    Url: null,
                    BrowserType: null,
                    PageTitle: null,
                    Error: $"Foreground window '{processName}' is not a browser",
                    ErrorCode: BrowserErrorCode.NoBrowserInForeground);
            }

            // Get URL from the browser
            return await GetBrowserUrlAsync(foregroundHandle, cdp, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new BrowserUrlResult(
                Success: false,
                Url: null,
                BrowserType: null,
                PageTitle: null,
                Error: $"Exception: {ex.Message}",
                ErrorCode: BrowserErrorCode.OperationException);
        }
    }

    /// <inheritdoc />
    public string? GetBrowserType(nint browserHandle, string processName)
    {
        // Try direct mapping first
        if (ProcessToBrowserType.TryGetValue(processName, out var browserType))
        {
            return browserType;
        }

        // Try partial matching for variants (e.g., chrome-dev, firefox-nightly)
        var lowerName = processName.ToLowerInvariant();

        if (lowerName.Contains("chrome") || lowerName.Contains("chromium"))
            return "chrome";

        if (lowerName.Contains("edge") || lowerName.Contains("msedge"))
            return "edge";

        if (lowerName.Contains("firefox") || lowerName.Contains("waterfox") ||
            lowerName.Contains("librewolf") || lowerName.Contains("floorp") ||
            lowerName.Contains("zen-browser"))
            return "firefox";

        if (lowerName.Contains("brave"))
            return "brave";

        if (lowerName.Contains("opera"))
            return "opera";

        if (lowerName.Contains("vivaldi"))
            return "vivaldi";

        // Default to chromium-style for unknown browsers
        // (most modern browsers are Chromium-based)
        if (_browserDetection.IsBrowser(processName))
            return "chrome";

        return null;
    }

    /// <inheritdoc />
    public bool IsBrowserWindow(nint handle)
    {
        if (handle == nint.Zero || !_nativeWrapper.IsWindow(handle))
            return false;

        var processId = _nativeWrapper.GetWindowThreadProcessId(handle);
        var processName = GetProcessName(processId);

        return _browserDetection.IsBrowser(processName);
    }


    /// <summary>
    /// Reads the browser window through UI Automation (roadmap M3.F24): its title and the page's address, or with
    /// <paramref name="content"/> its page, warmed up first in both cases (roadmap M5.7). The target policy is checked
    /// before anything is read; then the read runs on the worker under <paramref name="timeout"/>, and its failures come
    /// back as an error code.
    /// </summary>
    /// <param name="windowText">The window's text, the title when UI Automation cannot read the window's name.</param>
    /// <param name="content">The content options of get_browser_content, or null to read only the title and the URL.</param>
    private async Task<UiaRead> ReadThroughUiaAsync(
        nint browserHandle,
        string windowText,
        BrowserContentOptions? content,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        if (UiaTargets.ReadVerdict(_nativeWrapper, _policy, browserHandle) is { Allowed: false } verdict)
        {
            return UiaRead.Failed(UiaTargets.PolicyRefusalMessage(browserHandle, verdict), BrowserErrorCode.TargetDeniedByPolicy);
        }

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(timeout);
        var warmup = new WarmupSlot();
        try
        {
            return await _worker.RunAsync(
                UiaTargets.TargetProcessId(_nativeWrapper, browserHandle),
                token => ReadThroughUia(browserHandle, windowText, content, AccessibilityWarmup.BudgetFor(timeout), warmup, token),
                deadline.Token,
                TimeSpan.Zero).ConfigureAwait(false);
        }
        catch (TimeoutException) when (cancellationToken.IsCancellationRequested)
        {
            // The request was cancelled, not timed out: let the SDK report it
            throw new OperationCanceledException(cancellationToken);
        }
        catch (UiaReadRefusedException ex)
        {
            // Earlier reads of the same browser are still stuck; nothing was waited for
            return UiaRead.Failed(ex.Message, BrowserErrorCode.OperationTimedOut, warmup.Value);
        }
        catch (Exception ex) when (ex is TimeoutException || (ex is OperationCanceledException && !cancellationToken.IsCancellationRequested))
        {
            // The worker gave up at the deadline, or the read stopped there
            var reason = ex is TimeoutException ? $" ({ex.Message})" : string.Empty;
            return UiaRead.Failed(
                $"Reading the browser window through UI Automation did not finish within {timeout.TotalMilliseconds:0} ms{reason}. " +
                "Try again with a larger timeoutMs or a smaller maxLength, or read it through CDP (cdpPort); if it keeps " +
                "happening, the browser may be hung.",
                BrowserErrorCode.OperationTimedOut,
                warmup.Value);
        }
    }

    /// <summary>
    /// The UI Automation read, on the worker thread: whether the window responds, then the warm-up, then the window's title
    /// and the shown pages, then the page's address (get_browser_url, which trusts the address bar's focus only while the
    /// browser is the foreground window) or its text (get_browser_content, which takes the address from the page and does
    /// not look up the address bar). A window UI Automation has no element for reads as nothing found.
    /// </summary>
    private UiaRead ReadThroughUia(
        nint browserHandle,
        string windowText,
        BrowserContentOptions? content,
        TimeSpan warmupBudget,
        WarmupSlot warmup,
        CancellationToken cancellationToken)
    {
        if (UiaTargets.NotRespondingReason(_nativeWrapper, browserHandle, "the browser was not read") is { } notResponding)
        {
            return UiaRead.Failed(notResponding, BrowserErrorCode.NotResponding);
        }

        if (!_tree.IsAvailable())
        {
            return UiaRead.Failed("UI Automation is not available", BrowserErrorCode.AutomationUnavailable);
        }

        // The URL's read too: the page's Document holds the address, and the warm-up gives the render widgets shown now
        // (FindPages), not the ones an earlier read's warm-up of the window left behind
        warmup.Value = _warmup?.WarmUp(browserHandle, warmupBudget, cancellationToken);

        IUiaNode? window;
        try
        {
            window = _tree.FromHandle(browserHandle);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The window is gone, or the provider failed: nothing is found
            window = null;
        }

        if (window == null)
        {
            return new UiaRead(null, null, null, warmup.Value);
        }

        string? title;
        try
        {
            title = _tree.Read(window).Name;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The window text stands in for it; the address bar and the page are still read
            title = null;
        }

        // The shown tabs' pages, through their render widgets: the ones the warm-up found
        var pages = _warmup?.FindPages(browserHandle, cancellationToken)?.Select(page => page.Root).ToList();
        var windowTitle = title ?? windowText;
        if (content != null)
        {
            return new UiaRead(title, null, _reader.ReadContent(window, windowTitle, pages, content.MaxLength, cancellationToken), warmup.Value);
        }

        var foreground = TopLevel(_nativeWrapper.GetForegroundWindow()) == browserHandle;
        return new UiaRead(title, _reader.ReadUrl(window, windowTitle, pages, foreground, cancellationToken), null, warmup.Value);
    }

    /// <summary>
    /// The top-level window of <paramref name="handle"/> (GA_ROOT): a browser is read by its frame, also when given the
    /// handle of a window inside it, such as a render widget's from an element; the handle itself when it has none.
    /// </summary>
    private nint TopLevel(nint handle)
    {
        var root = _nativeWrapper.GetAncestor(handle, WindowAncestor.Root);
        return root != nint.Zero ? root : handle;
    }

    /// <summary>
    /// The tab to read: the one with <see cref="CdpOptions.TargetId"/> when it is given (roadmap M5.2), otherwise the one
    /// the window shows.
    /// </summary>
    private Task<CdpResult<CdpTarget>> FindTargetAsync(
        int port,
        int windowProcessId,
        string windowTitle,
        CdpOptions cdp,
        TimeSpan timeout,
        CancellationToken cancellationToken) =>
        cdp.TargetId is { } targetId
            ? _cdpService.FindTargetByIdAsync(port, windowProcessId, targetId, timeout, cancellationToken)
            : _cdpService.FindTargetForWindowAsync(port, windowProcessId, windowTitle, timeout, cancellationToken);

    /// <summary>
    /// Finds the tab to read (<see cref="FindTargetAsync"/>) and reads it, all within one time budget.
    /// </summary>
    private async Task<(CdpResult<CdpPageSnapshot> Snapshot, string? TargetId)> ReadContentViaCdpAsync(
        int port,
        int windowProcessId,
        string windowTitle,
        CdpOptions cdp,
        BrowserContentOptions options,
        CancellationToken cancellationToken)
    {
        var timeout = cdp.EffectiveTimeout;
        var stopwatch = Stopwatch.StartNew();

        var match = await FindTargetAsync(port, windowProcessId, windowTitle, cdp, timeout, cancellationToken);
        if (!match.Success)
            return (match.FailAs<CdpPageSnapshot>(), null);

        var target = match.Value!;
        var snapshot = await ReadSnapshotWithinBudgetAsync(
            target, options.TextOnly, options.MaxLength, timeout, stopwatch, cancellationToken);
        return (snapshot, target.Id);
    }

    /// <summary>
    /// Reads a tab with whatever is left of the time budget that <paramref name="stopwatch"/> started.
    /// </summary>
    private async Task<CdpResult<CdpPageSnapshot>> ReadSnapshotWithinBudgetAsync(
        CdpTarget target,
        bool textOnly,
        int? maxLength,
        TimeSpan budget,
        Stopwatch stopwatch,
        CancellationToken cancellationToken)
    {
        var remaining = budget - stopwatch.Elapsed;
        if (remaining <= TimeSpan.Zero)
        {
            return CdpResult<CdpPageSnapshot>.Fail(
                BrowserErrorCode.CdpTimeout,
                $"The CDP time budget of {(long)budget.TotalMilliseconds} ms was used up before tab {target.Id} could be read.");
        }

        return await _cdpService.GetPageSnapshotAsync(target, textOnly, maxLength, remaining, cancellationToken);
    }

    private static string Truncate(string content, int? maxLength) =>
        maxLength is int max && max >= 0 && content.Length > max ? content[..max] : content;


    /// <summary>The name of the process, or "Unknown" when it has exited or cannot be read.</summary>
    private string GetProcessName(uint processId) => _nativeWrapper.GetProcessName(processId) ?? "Unknown";

    /// <summary>The address of a window UI Automation has no element for.</summary>
    private static readonly BrowserUiaUrl WindowWithoutElement = BrowserUiaUrl.Failed(
        BrowserErrorCode.UrlBarNotFound, "UI Automation has no element for the browser window, so neither the page's address " +
        "nor the address bar could be read.", null, null, null);

    /// <summary>
    /// What the UI Automation read of a browser window found: the window's title as UI Automation names it (null when it
    /// could not be read), the page's address (get_browser_url) or its page (get_browser_content), each null when the window
    /// has no element, and the warm-up; or, with an error code, why nothing was read.
    /// </summary>
    private sealed record UiaRead(
        string? Title,
        BrowserUiaUrl? Url,
        BrowserUiaContent? Content,
        AccessibilityWarmupInfo? Warmup,
        string? Error = null,
        BrowserErrorCode ErrorCode = BrowserErrorCode.None)
    {
        public static UiaRead Failed(string error, BrowserErrorCode code, AccessibilityWarmupInfo? warmup = null) =>
            new(null, null, null, warmup, error, code);
    }

    /// <summary>What the warm-up did, kept where the caller can read it when the worker abandons the read.</summary>
    private sealed class WarmupSlot
    {
        private AccessibilityWarmupInfo? _value;

        public AccessibilityWarmupInfo? Value
        {
            get => Volatile.Read(ref _value);
            set => Volatile.Write(ref _value, value);
        }
    }
}
