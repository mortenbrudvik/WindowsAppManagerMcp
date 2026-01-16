using System.ComponentModel;
using ModelContextProtocol.Server;
using WindowsAppManagerMcp.Models;
using WindowsAppManagerMcp.Services.Interfaces;

namespace WindowsAppManagerMcp.Tools;

[McpServerToolType]
public class BrowserReadingTools
{
    private readonly IBrowserReadingService _browserReadingService;
    private readonly IProcessService _processService;

    public BrowserReadingTools(IBrowserReadingService browserReadingService, IProcessService processService)
    {
        _browserReadingService = browserReadingService;
        _processService = processService;
    }

    [McpServerTool(Name = "get_browser_url")]
    [Description("Get the current URL from a browser window's address bar. Uses Windows UI Automation to read the URL, or CDP (Chrome DevTools Protocol) if a port is specified. Supports Chrome, Edge, Firefox, Brave, Opera, Vivaldi, and other Chromium/Firefox-based browsers.")]
    public BrowserUrlResultDto GetBrowserUrl(
        [Description("Window handle of the browser (as integer). Get this from find_windows or get_foreground_window.")]
        long handle,
        [Description("Optional CDP port. If the browser was started with --remote-debugging-port=9222, specify 9222 here for more reliable extraction.")]
        int? cdpPort = null)
    {
        var result = _browserReadingService.GetBrowserUrl((nint)handle, cdpPort);
        return ToDto(result);
    }

    [McpServerTool(Name = "get_browser_content")]
    [Description("Extract text content from a browser window's page. Uses Windows UI Automation to read visible text, or CDP (Chrome DevTools Protocol) for richer content access. CDP captures JavaScript-rendered content more reliably. Supports Chrome, Edge, Firefox, Brave, Opera, Vivaldi, and other Chromium/Firefox-based browsers.")]
    public BrowserContentResultDto GetBrowserContent(
        [Description("Window handle of the browser (as integer). Get this from find_windows or get_foreground_window.")]
        long handle,
        [Description("Maximum length of content to return. If not specified, returns all available content.")]
        int? maxLength = null,
        [Description("If true (default), returns only text content. If false (and using CDP), returns full HTML.")]
        bool textOnly = true,
        [Description("Optional CDP port. If the browser was started with --remote-debugging-port=9222, specify 9222 here for more reliable content extraction including JavaScript-rendered content.")]
        int? cdpPort = null)
    {
        var options = new BrowserContentOptions(MaxLength: maxLength, TextOnly: textOnly);
        var result = _browserReadingService.GetBrowserContent((nint)handle, options, cdpPort);
        return ToDto(result);
    }

    [McpServerTool(Name = "get_active_browser_url")]
    [Description("Get the URL from the currently active (foreground) browser window. Convenience method that combines get_foreground_window and get_browser_url. Returns an error if the foreground window is not a browser.")]
    public BrowserUrlResultDto GetActiveBrowserUrl(
        [Description("Optional CDP port. If the browser was started with --remote-debugging-port=9222, specify 9222 here for more reliable extraction.")]
        int? cdpPort = null)
    {
        var result = _browserReadingService.GetActiveBrowserUrl(cdpPort);
        return ToDto(result);
    }

    [McpServerTool(Name = "launch_browser_with_debug")]
    [Description("Launch a browser with CDP (Chrome DevTools Protocol) debug port enabled. This enables richer content extraction via get_browser_content with the cdpPort parameter. Uses a separate user data directory by default to avoid conflicts with existing browser sessions.")]
    public DebugBrowserLaunchResultDto LaunchBrowserWithDebug(
        [Description("Browser to launch: chrome, edge, or brave")]
        string browser = "chrome",
        [Description("URL to open (optional)")]
        string? url = null,
        [Description("CDP debug port (default: 9222). Use this port value with get_browser_content's cdpPort parameter.")]
        int port = 9222,
        [Description("Separate user data directory to avoid conflicts with existing sessions. If not specified, a temporary directory is created automatically.")]
        string? userDataDir = null)
    {
        var result = _processService.LaunchBrowserWithDebug(browser, url, port, userDataDir);
        return ToDto(result);
    }

    private static BrowserUrlResultDto ToDto(BrowserUrlResult result) => new(
        Success: result.Success,
        Url: result.Url,
        BrowserType: result.BrowserType,
        PageTitle: result.PageTitle,
        Error: result.Error,
        ErrorCode: result.ErrorCode.ToString()
    );

    private static BrowserContentResultDto ToDto(BrowserContentResult result) => new(
        Success: result.Success,
        Content: result.Content,
        Url: result.Url,
        BrowserType: result.BrowserType,
        PageTitle: result.PageTitle,
        ContentLength: result.ContentLength,
        Error: result.Error,
        ErrorCode: result.ErrorCode.ToString()
    );

    private static DebugBrowserLaunchResultDto ToDto(DebugBrowserLaunchResult result) => new(
        Success: result.Success,
        ProcessId: result.ProcessId,
        WindowHandle: result.WindowHandle,
        CdpPort: result.CdpPort,
        UserDataDir: result.UserDataDir,
        Error: result.Error,
        ErrorCode: result.ErrorCode
    );
}

/// <summary>
/// DTO for browser URL result returned by MCP tools.
/// </summary>
public record BrowserUrlResultDto(
    bool Success,
    string? Url,
    string? BrowserType,
    string? PageTitle,
    string? Error,
    string ErrorCode
);

/// <summary>
/// DTO for browser content result returned by MCP tools.
/// </summary>
public record BrowserContentResultDto(
    bool Success,
    string? Content,
    string? Url,
    string? BrowserType,
    string? PageTitle,
    int? ContentLength,
    string? Error,
    string ErrorCode
);

/// <summary>
/// DTO for debug browser launch result returned by MCP tools.
/// </summary>
public record DebugBrowserLaunchResultDto(
    bool Success,
    int? ProcessId,
    nint? WindowHandle,
    int? CdpPort,
    string? UserDataDir,
    string? Error,
    string? ErrorCode
);
