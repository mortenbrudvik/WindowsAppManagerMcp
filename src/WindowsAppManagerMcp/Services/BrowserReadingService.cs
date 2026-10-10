using System.Diagnostics;
using WindowsAppManagerMcp.Models;
using WindowsAppManagerMcp.Services.Interfaces;

namespace WindowsAppManagerMcp.Services;

/// <summary>
/// Service for reading URLs and content from browser windows using UI Automation or CDP.
/// When a CDP port is specified, CDP is tried first; every result says which method produced it
/// (Source) and, after a fallback to UI Automation, why CDP was not used (CdpError).
/// </summary>
public class BrowserReadingService : IBrowserReadingService
{
    private const string CdpPortRequiredMessage =
        "requireCdp=true needs cdpPort: the debug port of the browser instance that owns the window.";

    private static readonly TimeSpan DefaultCdpTimeout = TimeSpan.FromMilliseconds(CdpOptions.DefaultTimeoutMs);

    private readonly IUIAutomationWrapper _uiAutomation;
    private readonly INativeWindowWrapper _nativeWrapper;
    private readonly IBrowserDetectionService _browserDetection;
    private readonly ICdpService _cdpService;

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

    public BrowserReadingService(
        IUIAutomationWrapper uiAutomation,
        INativeWindowWrapper nativeWrapper,
        IBrowserDetectionService browserDetection,
        ICdpService cdpService)
    {
        _uiAutomation = uiAutomation;
        _nativeWrapper = nativeWrapper;
        _browserDetection = browserDetection;
        _cdpService = cdpService;
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
            if (!_uiAutomation.IsAvailable())
            {
                return new BrowserUrlResult(
                    Success: false,
                    Url: null,
                    BrowserType: browserType,
                    PageTitle: pageTitle,
                    Error: "UI Automation is not available",
                    ErrorCode: BrowserErrorCode.AutomationUnavailable,
                    Source: BrowserReadSource.Uia,
                    CdpError: cdpError);
            }

            // Get title via UI Automation if not already set
            pageTitle = _uiAutomation.GetWindowTitle(browserHandle) ?? pageTitle;

            // Extract URL via UI Automation
            var url = _uiAutomation.GetBrowserUrl(browserHandle, browserType);

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
            if (!_uiAutomation.IsAvailable())
            {
                return new BrowserContentResult(
                    Success: false,
                    Content: null,
                    Url: null,
                    BrowserType: browserType,
                    PageTitle: pageTitle,
                    ContentLength: null,
                    Error: "UI Automation is not available",
                    ErrorCode: BrowserErrorCode.AutomationUnavailable,
                    Source: BrowserReadSource.Uia,
                    CdpError: cdpError);
            }

            // Get title via UI Automation
            pageTitle = _uiAutomation.GetWindowTitle(browserHandle) ?? pageTitle;

            // Get URL
            var url = _uiAutomation.GetBrowserUrl(browserHandle, browserType);

            // Extract content via UI Automation
            var uiaContent = _uiAutomation.GetBrowserContent(browserHandle, browserType, options.MaxLength);

            if (string.IsNullOrEmpty(uiaContent))
            {
                return new BrowserContentResult(
                    Success: false,
                    Content: null,
                    Url: url,
                    BrowserType: browserType,
                    PageTitle: pageTitle,
                    ContentLength: null,
                    Error: "Could not extract content from browser",
                    ErrorCode: BrowserErrorCode.ContentExtractionFailed,
                    Source: BrowserReadSource.Uia,
                    CdpError: cdpError);
            }

            return new BrowserContentResult(
                Success: true,
                Content: uiaContent,
                Url: url,
                BrowserType: browserType,
                PageTitle: pageTitle,
                ContentLength: uiaContent.Length,
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
}
