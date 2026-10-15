using System.Diagnostics;
using WindowsAppManagerMcp.Models;
using WindowsAppManagerMcp.Services.Interfaces;

namespace WindowsAppManagerMcp.Services;

/// <summary>
/// Service for reading URLs and content from browser windows using UI Automation or CDP.
/// CDP is tried first on cdpPort, or without it on the debug port the window's own browser process listens on (roadmap
/// M5.3, <see cref="IDebugPortLocator"/>); every result says which method produced it (Source) and, after a fallback to
/// UI Automation, why CDP was not used (CdpError).
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
/// element) is read as its top-level window's (GA_ROOT). The CDP reads with cdpPort are not covered by the target policy:
/// they read the debug browser through the port the agent gave; no port is looked for in a denied browser. The CDP lookup is given the window's bounds, state and DPI scale, so it can bind the
/// window to its browser window before the title picks the tab (<see cref="CdpWindowBinder"/>).
/// </remarks>
public class BrowserReadingService : IBrowserReadingService
{
    /// <summary>What a call that requires CDP names in its error when requireCdp or targetId asks for CDP.</summary>
    private const string RequireCdpNeed = "requireCdp=true (or targetId)";

    /// <summary>What a selector's error names: it is read through CDP only (roadmap M5.6).</summary>
    private const string SelectorNeed = "selector (read through CDP only)";

    /// <summary>
    /// What a later page's error names (roadmap M5.6): with useCdp (the default) an offset above 0 continues a page of the
    /// text CDP read, and a page UI Automation read continues with useCdp=false (roadmap M5.3).
    /// </summary>
    private const string OffsetNeed = "offset above 0 (it continues a 'cdp' page; a 'uia' page continues with useCdp=false)";

    /// <summary>
    /// Added to the CDP error of a first page read through UI Automation that has more pages (roadmap M5.6): its nextOffset
    /// counts UI Automation's text, and without useCdp=false a later page is read through CDP, whose text differs.
    /// </summary>
    internal const string PageOnWithUseCdpFalseHint =
        "; page on with useCdp=false: nextOffset counts UI Automation's text, and without it CDP would read the page";

    /// <summary>Added to the error of a later page CDP could not read (roadmap M5.6): it never falls back.</summary>
    internal const string UiaPageContinuesHint = " A 'uia' page continues with useCdp=false.";

    private readonly INativeWindowWrapper _nativeWrapper;
    private readonly IBrowserDetectionService _browserDetection;
    private readonly ICdpService _cdpService;
    private readonly IUiaTree _tree;
    private readonly IUiaWorker _worker;
    private readonly BrowserUiaReader _reader;
    private readonly IAccessibilityWarmup? _warmup;
    private readonly ITargetPolicy? _policy;
    private readonly IDebugPortLocator? _portLocator;

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
    /// <param name="portLocator">Finds the debug port of the window's own process when no cdpPort is given (roadmap M5.3).
    /// The server always passes it; without it a window is read through CDP only with cdpPort.</param>
    public BrowserReadingService(
        INativeWindowWrapper nativeWrapper,
        IBrowserDetectionService browserDetection,
        ICdpService cdpService,
        IUiaTree tree,
        IUiaWorker worker,
        IAccessibilityWarmup? warmup = null,
        ITargetPolicy? policy = null,
        IDebugPortLocator? portLocator = null)
    {
        _nativeWrapper = nativeWrapper;
        _browserDetection = browserDetection;
        _cdpService = cdpService;
        _tree = tree;
        _worker = worker;
        _reader = new BrowserUiaReader(tree);
        _warmup = warmup;
        _policy = policy;
        _portLocator = portLocator;
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

            // Try CDP first: on cdpPort, else on the port the window's own process listens on (roadmap M5.3)
            var choice = ChoosePort(browserHandle, processId, browserType, cdp);
            var cdpError = choice.FallbackReason;
            if (cdp != null && choice.Port is int port)
            {
                var match = await FindTargetAsync(
                    port, Describe(browserHandle, processId, pageTitle), cdp, cdp.EffectiveTimeout, cancellationToken);

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
                        TargetId: target.Id,
                        CdpPort: port);
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
                        CdpError: match.DescribeError(),
                        CdpPort: port);
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
                    Error: choice.RequiredError,
                    ErrorCode: choice.RequiredErrorCode);
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

            // Try CDP first: on cdpPort, else on the port the window's own process listens on (roadmap M5.3). A selector is
            // CDP's alone, and with useCdp an offset above 0 continues a page of the text CDP read (roadmap M5.6): neither
            // falls back to UI Automation, which reads other text. With useCdp=false the offset counts UI Automation's text.
            var pagesOn = cdp is { UseCdp: true } && options.EffectiveOffset > 0;
            var required = cdp?.EffectiveRequired == true || options.Selector != null || pagesOn;
            var need = cdp?.EffectiveRequired == true ? RequireCdpNeed
                : options.Selector != null ? SelectorNeed
                : pagesOn ? OffsetNeed : RequireCdpNeed;
            var choice = ChoosePort(browserHandle, processId, browserType, cdp, need);
            var cdpError = choice.FallbackReason;
            if (cdp != null && choice.Port is int port)
            {
                var (snapshot, targetId) = await ReadContentViaCdpAsync(
                    port, Describe(browserHandle, processId, pageTitle), cdp, options, cancellationToken);

                if (snapshot.Success)
                {
                    var page = snapshot.Value!;

                    // The page sliced at most maxLength characters; a page also holds at most read_element_text's bytes
                    // of JSON, and the next one starts just past its text
                    var content = page.Content[..TextPages.FittingLength(
                        page.Content, options.EffectiveMaxLength, ElementReadService.MaxTextBytes)];
                    var end = page.Offset + content.Length;
                    var total = page.TotalLength ?? page.Offset + page.Content.Length;
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
                        TargetId: targetId,
                        CdpPort: port,
                        Offset: page.Offset,
                        TotalLength: total,
                        Truncated: end < total,
                        NextOffset: end < total ? end : null);
                }

                // A page too large for CDP is reported as it is, not as HtmlRequiresCdp's "CDP could not be used"
                if (required ||
                    (!options.TextOnly && snapshot.ErrorCode == BrowserErrorCode.CdpResponseTooLarge))
                {
                    return new BrowserContentResult(
                        Success: false,
                        Content: null,
                        Url: null,
                        BrowserType: browserType,
                        PageTitle: pageTitle,
                        ContentLength: null,
                        Error: need == OffsetNeed ? snapshot.Error + UiaPageContinuesHint : snapshot.Error,
                        ErrorCode: snapshot.ErrorCode,
                        Source: BrowserReadSource.Cdp,
                        TargetId: targetId,
                        CdpError: snapshot.DescribeError(),
                        CdpPort: port);
                }

                cdpError = snapshot.DescribeError();
            }
            else if (required)
            {
                return new BrowserContentResult(
                    Success: false,
                    Content: null,
                    Url: null,
                    BrowserType: browserType,
                    PageTitle: pageTitle,
                    ContentLength: null,
                    Error: choice.RequiredError,
                    ErrorCode: choice.RequiredErrorCode);
            }

            // UI Automation only reads text. Returning text when HTML was asked for would
            // silently change the format, so report it instead.
            if (!options.TextOnly)
            {
                if (choice.Denied)
                {
                    return new BrowserContentResult(
                        Success: false,
                        Content: null,
                        Url: null,
                        BrowserType: browserType,
                        PageTitle: pageTitle,
                        ContentLength: null,
                        Error: choice.RequiredError,
                        ErrorCode: choice.RequiredErrorCode);
                }

                var reason = cdpError != null ? $"CDP could not be used ({cdpError})." : choice.NoPortReason;
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
                           "The tab may still be loading, or the browser's accessibility tree is not available. " +
                           BrowserUiaReader.PageNotShownHint,
                    ErrorCode: BrowserErrorCode.ContentAreaNotFound,
                    Source: BrowserReadSource.Uia,
                    CdpError: cdpError,
                    AccessibilityWarmup: read.Warmup);
            }

            var uiaContent = shown.Text;

            // Past the end of the text an empty page is the answer; at its start the page has no text to read
            if (uiaContent == null || (uiaContent.Length == 0 && shown.Offset == 0))
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
                CdpError: cdpError != null && shown.NextOffset != null ? cdpError + PageOnWithUseCdpFalseHint : cdpError,
                AccessibilityWarmup: read.Warmup,
                Offset: shown.Offset,
                TotalLength: shown.TotalLength,
                Truncated: shown.NextOffset != null,
                NextOffset: shown.NextOffset,
                TextSkipped: shown.TextSkipped);
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
                "Try again with a larger timeoutMs or a smaller maxLength, or read it through CDP (a browser started " +
                "with launch_browser_with_debug, or cdpPort); if it keeps happening, the browser may be hung.",
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
            var page = _reader.ReadContent(window, windowTitle, pages, content.EffectiveOffset, content.EffectiveMaxLength,
                ElementReadService.MaxTextBytes, cancellationToken);
            return new UiaRead(title, null, page, warmup.Value);
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
        CdpWindow window,
        CdpOptions cdp,
        TimeSpan timeout,
        CancellationToken cancellationToken) =>
        cdp.TargetId is { } targetId
            ? _cdpService.FindTargetByIdAsync(port, window.ProcessId, targetId, timeout, cancellationToken)
            : _cdpService.FindTargetForWindowAsync(port, window, timeout, cancellationToken);

    /// <summary>
    /// Finds the tab to read (<see cref="FindTargetAsync"/>) and reads it, all within one time budget.
    /// </summary>
    private async Task<(CdpResult<CdpPageSnapshot> Snapshot, string? TargetId)> ReadContentViaCdpAsync(
        int port,
        CdpWindow window,
        CdpOptions cdp,
        BrowserContentOptions options,
        CancellationToken cancellationToken)
    {
        var timeout = cdp.EffectiveTimeout;
        var stopwatch = Stopwatch.StartNew();

        var match = await FindTargetAsync(port, window, cdp, timeout, cancellationToken);
        if (!match.Success)
            return (match.FailAs<CdpPageSnapshot>(), null);

        var target = match.Value!;
        var request = new CdpPageRequest(options.TextOnly, options.EffectiveOffset, options.EffectiveMaxLength, options.Selector);
        var snapshot = await ReadSnapshotWithinBudgetAsync(target, request, timeout, stopwatch, cancellationToken);
        return (snapshot, target.Id);
    }

    /// <summary>
    /// Reads a tab with whatever is left of the time budget that <paramref name="stopwatch"/> started.
    /// </summary>
    private async Task<CdpResult<CdpPageSnapshot>> ReadSnapshotWithinBudgetAsync(
        CdpTarget target,
        CdpPageRequest request,
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

        return await _cdpService.GetPageSnapshotAsync(target, request, remaining, cancellationToken);
    }

    /// <summary>
    /// The DevTools port to read the window through (roadmap M5.3): cdpPort when it is given; otherwise, for a Chromium
    /// browser the target policy allows, the port its own process listens on (<see cref="IDebugPortLocator"/>); none with
    /// useCdp=false or without CDP options. Without a port, also what a call that needs CDP returns, what a fallback says in
    /// cdpError (nothing for a browser started without a debug port, which reads through UI Automation as before), and why
    /// HTML cannot be read.
    /// </summary>
    /// <remarks>
    /// The port found on its own is the policy's to refuse: before M5.3 a window was read through CDP only with the agent's
    /// cdpPort, so a denied browser is not read through a port found for it, and its command line is not read to find one.
    /// An explicit cdpPort reads the debug browser as before (the CDP reads are not covered by the policy).
    /// </remarks>
    private PortChoice ChoosePort(
        nint browserHandle, uint processId, string browserType, CdpOptions? cdp, string need = RequireCdpNeed)
    {
        if (cdp is { UseCdp: false })
        {
            return new PortChoice(null, null, BrowserErrorCode.CdpUnavailable,
                $"{need} needs CDP, which useCdp=false turns off. Nothing was read.", "useCdp is false.");
        }

        if (cdp?.Port is int given)
            return new PortChoice(given, null, BrowserErrorCode.None, null, null);

        const string StartWithDebugPort = "Start the browser with launch_browser_with_debug, or pass cdpPort.";
        if (cdp == null || _portLocator == null)
        {
            return new PortChoice(null, null, BrowserErrorCode.CdpUnavailable,
                $"{need} needs a debug port, and none is looked for. {StartWithDebugPort}",
                "No cdpPort was given: pass the browser's, or start it with launch_browser_with_debug, whose windows are then " +
                "read through CDP.");
        }

        if (UiaTargets.ReadVerdict(_nativeWrapper, _policy, browserHandle) is { Allowed: false } verdict)
        {
            return new PortChoice(null, null, BrowserErrorCode.TargetDeniedByPolicy,
                UiaTargets.PolicyRefusalMessage(browserHandle, verdict), null);
        }

        // Firefox speaks WebDriver BiDi on its debug port, not CDP
        if (browserType == "firefox")
        {
            return new PortChoice(null, null, BrowserErrorCode.CdpUnavailable,
                $"{need} needs CDP, which Firefox does not have. Nothing was read.",
                "Firefox has no CDP; HTML is only available for a Chromium browser started with a debug port.");
        }

        var lookup = _portLocator.Find((int)processId);
        if (lookup.Port is int found)
            return new PortChoice(found, null, BrowserErrorCode.None, null, null);

        var code = lookup.Status == DebugPortLookupStatus.HeldByOtherProcess
            ? BrowserErrorCode.CdpPortOwnerMismatch
            : BrowserErrorCode.CdpUnavailable;
        return new PortChoice(
            null,
            lookup.WorthReporting ? $"{code}: {lookup.Detail}" : null,
            code,
            $"{need} needs the DevTools port of the browser that owns the window, and none was found. " +
            $"{lookup.Detail} {StartWithDebugPort}",
            lookup.Status == DebugPortLookupStatus.NoDebugPort
                ? "The browser was not started with a debug port: start it with launch_browser_with_debug, whose windows are " +
                  "then read through CDP, or pass cdpPort."
                : $"{lookup.Detail} Pass cdpPort if it was started with one, or start it with launch_browser_with_debug.");
    }

    /// <summary>The window a tab is looked up for: its process, its title and where it is (roadmap M5.3).</summary>
    private CdpWindow Describe(nint browserHandle, uint processId, string title)
    {
        CdpWindowGeometry? geometry = null;
        if (_nativeWrapper.GetWindowRect(browserHandle) is { } rect)
        {
            var state = _nativeWrapper.IsIconic(browserHandle)
                ? CdpWindowState.Minimized
                : _nativeWrapper.IsZoomed(browserHandle) ? CdpWindowState.Maximized : CdpWindowState.Normal;
            var dpi = _nativeWrapper.GetDpiForWindow(browserHandle);
            geometry = new CdpWindowGeometry(rect.Left, rect.Top, rect.Right, rect.Bottom, state, dpi > 0 ? dpi / 96.0 : 1.0);
        }

        return new CdpWindow((int)processId, title, geometry);
    }

    /// <summary>
    /// The port to read through, or null; without one, why a fallback did not use CDP (null when there is nothing to say),
    /// what a call that requires CDP returns, and why HTML cannot be read. TargetDeniedByPolicy as the required error code
    /// means the target policy refuses the window.
    /// </summary>
    private sealed record PortChoice(
        int? Port,
        string? FallbackReason,
        BrowserErrorCode RequiredErrorCode,
        string? RequiredError,
        string? NoPortReason)
    {
        public bool Denied => RequiredErrorCode == BrowserErrorCode.TargetDeniedByPolicy;
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
