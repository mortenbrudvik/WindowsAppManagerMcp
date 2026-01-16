using System.Diagnostics;
using WindowsAppManagerMcp.Models;
using WindowsAppManagerMcp.Services.Interfaces;

namespace WindowsAppManagerMcp.Services;

/// <summary>
/// Service for reading URLs and content from browser windows using UI Automation or CDP.
/// When CDP is available and specified, it provides more reliable content extraction.
/// </summary>
public class BrowserReadingService : IBrowserReadingService
{
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
    public BrowserUrlResult GetBrowserUrl(nint browserHandle, int? cdpPort = null)
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
            var processName = GetProcessNameFromId((int)processId);

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

            // Try CDP first if port is specified
            if (cdpPort.HasValue)
            {
                var cdpResult = TryGetUrlViaCdp(pageTitle, cdpPort.Value, browserType);
                if (cdpResult != null)
                    return cdpResult;
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
                    ErrorCode: BrowserErrorCode.AutomationUnavailable);
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
                    ErrorCode: BrowserErrorCode.UrlBarNotFound);
            }

            return new BrowserUrlResult(
                Success: true,
                Url: url,
                BrowserType: browserType,
                PageTitle: pageTitle,
                Error: null,
                ErrorCode: BrowserErrorCode.None);
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
    public BrowserContentResult GetBrowserContent(nint browserHandle, BrowserContentOptions? options = null, int? cdpPort = null)
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
            var processName = GetProcessNameFromId((int)processId);

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

            // Try CDP first if port is specified
            if (cdpPort.HasValue)
            {
                var cdpResult = TryGetContentViaCdp(pageTitle, cdpPort.Value, browserType, options);
                if (cdpResult != null)
                    return cdpResult;
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
                    ErrorCode: BrowserErrorCode.AutomationUnavailable);
            }

            // Get title via UI Automation
            pageTitle = _uiAutomation.GetWindowTitle(browserHandle) ?? pageTitle;

            // Get URL
            var url = _uiAutomation.GetBrowserUrl(browserHandle, browserType);

            // Extract content via UI Automation
            var content = _uiAutomation.GetBrowserContent(browserHandle, browserType, options.MaxLength);

            if (string.IsNullOrEmpty(content))
            {
                return new BrowserContentResult(
                    Success: false,
                    Content: null,
                    Url: url,
                    BrowserType: browserType,
                    PageTitle: pageTitle,
                    ContentLength: null,
                    Error: "Could not extract content from browser",
                    ErrorCode: BrowserErrorCode.ContentExtractionFailed);
            }

            return new BrowserContentResult(
                Success: true,
                Content: content,
                Url: url,
                BrowserType: browserType,
                PageTitle: pageTitle,
                ContentLength: content.Length,
                Error: null,
                ErrorCode: BrowserErrorCode.None);
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
    public BrowserUrlResult GetActiveBrowserUrl(int? cdpPort = null)
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
            var processName = GetProcessNameFromId((int)processId);

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
            return GetBrowserUrl(foregroundHandle, cdpPort);
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
    public async Task<bool> IsCdpAvailableAsync(int port = 9222)
    {
        return await _cdpService.IsDebugPortAvailableAsync(port);
    }

    /// <inheritdoc />
    public async Task<BrowserContentResult> GetContentViaCdpAsync(int port = 9222, int targetIndex = 0)
    {
        try
        {
            var isAvailable = await _cdpService.IsDebugPortAvailableAsync(port);
            if (!isAvailable)
            {
                return new BrowserContentResult(
                    Success: false,
                    Content: null,
                    Url: null,
                    BrowserType: "cdp",
                    PageTitle: null,
                    ContentLength: null,
                    Error: $"CDP not available on port {port}",
                    ErrorCode: BrowserErrorCode.AutomationUnavailable);
            }

            var targets = await _cdpService.GetTargetsAsync(port);
            if (targets.Count == 0)
            {
                return new BrowserContentResult(
                    Success: false,
                    Content: null,
                    Url: null,
                    BrowserType: "cdp",
                    PageTitle: null,
                    ContentLength: null,
                    Error: "No browser targets found",
                    ErrorCode: BrowserErrorCode.ContentAreaNotFound);
            }

            if (targetIndex >= targets.Count)
            {
                return new BrowserContentResult(
                    Success: false,
                    Content: null,
                    Url: null,
                    BrowserType: "cdp",
                    PageTitle: null,
                    ContentLength: null,
                    Error: $"Target index {targetIndex} out of range (found {targets.Count} targets)",
                    ErrorCode: BrowserErrorCode.InvalidHandle);
            }

            var target = targets[targetIndex];
            var content = await _cdpService.GetPageContentAsync(target.Id, port, textOnly: true);
            var title = await _cdpService.GetPageTitleAsync(target.Id, port);

            if (string.IsNullOrEmpty(content))
            {
                return new BrowserContentResult(
                    Success: false,
                    Content: null,
                    Url: target.Url,
                    BrowserType: "cdp",
                    PageTitle: title ?? target.Title,
                    ContentLength: null,
                    Error: "Could not extract content via CDP",
                    ErrorCode: BrowserErrorCode.ContentExtractionFailed);
            }

            return new BrowserContentResult(
                Success: true,
                Content: content,
                Url: target.Url,
                BrowserType: "cdp",
                PageTitle: title ?? target.Title,
                ContentLength: content.Length,
                Error: null,
                ErrorCode: BrowserErrorCode.None);
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
        var processName = GetProcessNameFromId((int)processId);

        return _browserDetection.IsBrowser(processName);
    }

    private BrowserUrlResult? TryGetUrlViaCdp(string windowTitle, int port, string browserType)
    {
        try
        {
            var isAvailable = _cdpService.IsDebugPortAvailableAsync(port).GetAwaiter().GetResult();
            if (!isAvailable)
                return null;

            var target = _cdpService.FindTargetByTitleAsync(windowTitle, port).GetAwaiter().GetResult();
            if (target == null)
                return null;

            var title = _cdpService.GetPageTitleAsync(target.Id, port).GetAwaiter().GetResult();

            return new BrowserUrlResult(
                Success: true,
                Url: target.Url,
                BrowserType: browserType,
                PageTitle: title ?? target.Title,
                Error: null,
                ErrorCode: BrowserErrorCode.None);
        }
        catch
        {
            return null;
        }
    }

    private BrowserContentResult? TryGetContentViaCdp(string windowTitle, int port, string browserType, BrowserContentOptions options)
    {
        try
        {
            var isAvailable = _cdpService.IsDebugPortAvailableAsync(port).GetAwaiter().GetResult();
            if (!isAvailable)
                return null;

            var target = _cdpService.FindTargetByTitleAsync(windowTitle, port).GetAwaiter().GetResult();
            if (target == null)
                return null;

            var content = _cdpService.GetPageContentAsync(target.Id, port, options.TextOnly).GetAwaiter().GetResult();
            var title = _cdpService.GetPageTitleAsync(target.Id, port).GetAwaiter().GetResult();

            if (string.IsNullOrEmpty(content))
                return null;

            // Apply max length if specified
            if (options.MaxLength.HasValue && content.Length > options.MaxLength.Value)
            {
                content = content[..options.MaxLength.Value];
            }

            return new BrowserContentResult(
                Success: true,
                Content: content,
                Url: target.Url,
                BrowserType: browserType,
                PageTitle: title ?? target.Title,
                ContentLength: content.Length,
                Error: null,
                ErrorCode: BrowserErrorCode.None);
        }
        catch
        {
            return null;
        }
    }

    private static string GetProcessNameFromId(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            return process.ProcessName;
        }
        catch
        {
            return "Unknown";
        }
    }
}
