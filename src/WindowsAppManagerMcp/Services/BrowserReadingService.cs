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
/// yet does not come back empty (D-BRW-15); get_browser_url reads only the address bar, the browser's own UI, and does not
/// wait for the page. The CDP reads are not covered by the target policy: they read the debug browser through its port.
/// </remarks>
public class BrowserReadingService : IBrowserReadingService
{
    private const string CdpPortRequiredMessage =
        "requireCdp=true needs cdpPort: the debug port of the browser instance that owns the window.";

    private static readonly TimeSpan DefaultCdpTimeout = TimeSpan.FromMilliseconds(CdpOptions.DefaultTimeoutMs);

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
                var match = await _cdpService.FindTargetForWindowAsync(
                    port, (int)processId, pageTitle, cdp.EffectiveTimeout, cancellationToken);

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

                if (cdp.Required)
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
            else if (cdp?.Required == true)
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
                browserHandle, browserType, content: null, (cdp ?? new CdpOptions()).EffectiveTimeout, cancellationToken);
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
            var url = read.Url;

            if (string.IsNullOrEmpty(url))
            {
                return new BrowserUrlResult(
                    Success: false,
                    Url: null,
                    BrowserType: browserType,
                    PageTitle: pageTitle,
                    Error: "Could not find or read the URL bar",
                    ErrorCode: BrowserErrorCode.UrlBarNotFound,
                    Source: BrowserReadSource.Uia,
                    CdpError: cdpError);
            }

            return new BrowserUrlResult(
                Success: true,
                Url: url,
                BrowserType: browserType,
                PageTitle: pageTitle,
                Error: null,
                ErrorCode: BrowserErrorCode.None,
                Source: BrowserReadSource.Uia,
                CdpError: cdpError);
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
                    port, (int)processId, pageTitle, options, cdp.EffectiveTimeout, cancellationToken);

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

                if (cdp.Required)
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
            else if (cdp?.Required == true)
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
                    : "No cdpPort was given; pass the debug port of a browser started with --remote-debugging-port (see launch_browser_with_debug).";
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
                browserHandle, browserType, options, (cdp ?? new CdpOptions()).EffectiveTimeout, cancellationToken);
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
            var uiaContent = read.Content;

            if (string.IsNullOrEmpty(uiaContent))
            {
                return new BrowserContentResult(
                    Success: false,
                    Content: null,
                    Url: read.Url,
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
                Url: read.Url,
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
    public async Task<bool> IsCdpAvailableAsync(int port = 9222, CancellationToken cancellationToken = default)
    {
        var targets = await _cdpService.GetPageTargetsAsync(port, DefaultCdpTimeout, cancellationToken);
        return targets.Success;
    }

    /// <inheritdoc />
    public async Task<BrowserContentResult> GetContentViaCdpAsync(
        int port = 9222,
        int targetIndex = 0,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var stopwatch = Stopwatch.StartNew();
            var targets = await _cdpService.GetPageTargetsAsync(port, DefaultCdpTimeout, cancellationToken);
            if (!targets.Success)
            {
                return new BrowserContentResult(
                    Success: false,
                    Content: null,
                    Url: null,
                    BrowserType: "cdp",
                    PageTitle: null,
                    ContentLength: null,
                    Error: targets.Error,
                    ErrorCode: targets.ErrorCode,
                    Source: BrowserReadSource.Cdp,
                    CdpError: targets.DescribeError());
            }

            var pages = targets.Value!;
            if (pages.Count == 0)
            {
                return new BrowserContentResult(
                    Success: false,
                    Content: null,
                    Url: null,
                    BrowserType: "cdp",
                    PageTitle: null,
                    ContentLength: null,
                    Error: "No browser targets found",
                    ErrorCode: BrowserErrorCode.ContentAreaNotFound,
                    Source: BrowserReadSource.Cdp);
            }

            if (targetIndex < 0 || targetIndex >= pages.Count)
            {
                return new BrowserContentResult(
                    Success: false,
                    Content: null,
                    Url: null,
                    BrowserType: "cdp",
                    PageTitle: null,
                    ContentLength: null,
                    Error: $"Target index {targetIndex} out of range (found {pages.Count} targets)",
                    ErrorCode: BrowserErrorCode.InvalidHandle,
                    Source: BrowserReadSource.Cdp);
            }

            var target = pages[targetIndex];
            var snapshot = await ReadSnapshotWithinBudgetAsync(
                target, textOnly: true, maxLength: null, DefaultCdpTimeout, stopwatch, cancellationToken);

            if (!snapshot.Success || string.IsNullOrEmpty(snapshot.Value!.Content))
            {
                return new BrowserContentResult(
                    Success: false,
                    Content: null,
                    Url: target.Url,
                    BrowserType: "cdp",
                    PageTitle: target.Title,
                    ContentLength: null,
                    Error: snapshot.Success ? "Could not extract content via CDP" : snapshot.Error,
                    ErrorCode: snapshot.Success ? BrowserErrorCode.ContentExtractionFailed : snapshot.ErrorCode,
                    Source: BrowserReadSource.Cdp,
                    TargetId: target.Id,
                    CdpError: snapshot.DescribeError());
            }

            var page = snapshot.Value;
            return new BrowserContentResult(
                Success: true,
                Content: page.Content,
                Url: page.Url,
                BrowserType: "cdp",
                PageTitle: page.Title,
                ContentLength: page.Content.Length,
                Error: null,
                ErrorCode: BrowserErrorCode.None,
                Source: BrowserReadSource.Cdp,
                TargetId: target.Id);
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
                BrowserType: "cdp",
                PageTitle: null,
                ContentLength: null,
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
    /// Reads the browser window through UI Automation (roadmap M3.F24): its title and address bar, and with
    /// <paramref name="content"/> its page as well, warmed up first. The target policy is checked before anything is read;
    /// then the read runs on the worker under <paramref name="timeout"/>, and its failures come back as an error code.
    /// </summary>
    /// <param name="content">The content options of get_browser_content, or null to read only the title and the URL.</param>
    private async Task<UiaRead> ReadThroughUiaAsync(
        nint browserHandle,
        string browserType,
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
                token => ReadThroughUia(browserHandle, browserType, content, AccessibilityWarmup.BudgetFor(timeout), warmup, token),
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
    /// The UI Automation read, on the worker thread: whether the window responds, then (for the content) the warm-up, then
    /// the window's title, its address bar and its page. A window UI Automation has no element for reads as nothing found,
    /// as the old wrapper reported it.
    /// </summary>
    private UiaRead ReadThroughUia(
        nint browserHandle,
        string browserType,
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

        if (content != null)
        {
            warmup.Value = _warmup?.WarmUp(browserHandle, warmupBudget, cancellationToken);
        }

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

        var url = _reader.ReadUrl(window, browserType, cancellationToken);
        var text = content != null ? _reader.ReadContent(window, content.MaxLength, cancellationToken) : null;
        return new UiaRead(title, url, text, warmup.Value);
    }

    /// <summary>
    /// Finds the tab shown by the window and reads it, all within one time budget.
    /// </summary>
    private async Task<(CdpResult<CdpPageSnapshot> Snapshot, string? TargetId)> ReadContentViaCdpAsync(
        int port,
        int windowProcessId,
        string windowTitle,
        BrowserContentOptions options,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();

        var match = await _cdpService.FindTargetForWindowAsync(
            port, windowProcessId, windowTitle, timeout, cancellationToken);
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

    /// <summary>
    /// What the UI Automation read of a browser window found: the window's title as UI Automation names it, the address
    /// bar's text and the page's text (each null when nothing was found) and the warm-up; or, with an error code, why
    /// nothing was read.
    /// </summary>
    private sealed record UiaRead(
        string? Title,
        string? Url,
        string? Content,
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
