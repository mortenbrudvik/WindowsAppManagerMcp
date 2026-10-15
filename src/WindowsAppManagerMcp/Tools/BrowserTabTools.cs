using System.ComponentModel;
using ModelContextProtocol.Server;
using WindowsAppManagerMcp.Infrastructure;
using WindowsAppManagerMcp.Models;
using WindowsAppManagerMcp.Services;
using WindowsAppManagerMcp.Services.Interfaces;

namespace WindowsAppManagerMcp.Tools;

/// <summary>
/// list_tabs, activate_tab, browser_navigate and wait_for_browser (roadmap M5.5): a debug browser's tabs, navigation with a
/// load wait, and waits on a page, through CDP only. list_tabs and wait_for_browser only read (toolset browser);
/// activate_tab and browser_navigate change the browser (toolset browser-actions, off by default), take the input gate
/// for the command that changes it, are audited, and are refused while the user has paused input.
/// </summary>
[McpServerToolType]
public class BrowserTabTools
{
    /// <summary>browser_navigate's default time limit.</summary>
    public const int DefaultNavigateTimeoutMs = 15_000;

    /// <summary>The shortest time limit browser_navigate takes.</summary>
    public const int MinNavigateTimeoutMs = 1_000;

    /// <summary>The longest time limit browser_navigate and wait_for_browser take.</summary>
    public const int MaxTimeoutMs = 60_000;

    /// <summary>wait_for_browser's default time limit.</summary>
    public const int DefaultWaitTimeoutMs = 10_000;

    private readonly IBrowserTabService _tabs;
    private readonly IWindowService? _windowService;
    private readonly IInputSession? _session;

    /// <param name="tabs">The tab service.</param>
    /// <param name="windowService">Resolves the window selector; the server always passes it.</param>
    /// <param name="session">Refuses the tools that change the browser while input is paused; the server always passes it.</param>
    public BrowserTabTools(IBrowserTabService tabs, IWindowService? windowService = null, IInputSession? session = null)
    {
        _tabs = tabs;
        _windowService = windowService;
        _session = session;
    }

    internal const string HandleDescription = "Browser window handle. Pass handle, window or cdpPort.";

    internal const string WindowDescription = "Alternative to handle: a window selector as in find_windows.";

    internal const string CdpPortDescription = "DevTools port; found from the window when omitted. Alone, it names the browser listening on it.";

    internal const string TargetIdDescription = "Tab id from list_tabs.";

    internal const string ShownTabDefault = " Default: the tab the window shows.";

    internal const string CdpOnly = " CDP only: a browser with a debug port (launch_browser_with_debug).";

    [McpServerTool(Name = "list_tabs", Title = "List Browser Tabs", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("List a debug browser's tabs (targetId, title, url, windowId). With a window, its tabs come first (inWindow) and " +
                 "shownTargetId is the one it shows when its title tells." + CdpOnly)]
    public async Task<BrowserTabsResult> ListTabs(
        [Description(HandleDescription)] long? handle = null,
        [Description(WindowDescription)] WindowSelector? window = null,
        [Description(CdpPortDescription)] int? cdpPort = null,
        CancellationToken cancellationToken = default)
    {
        var target = Target(handle, window, cdpPort, targetId: null, "Nothing was read.");
        return await _tabs.ListTabsAsync(target, cancellationToken).ConfigureAwait(false);
    }

    [McpServerTool(Name = "activate_tab", Title = "Activate Browser Tab", ReadOnly = false, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("Show a debug browser's tab in its window; the browser may bring the window to the front." + CdpOnly)]
    public async Task<BrowserTabResult> ActivateTab(
        [Description(TargetIdDescription)] string targetId,
        [Description(HandleDescription)] long? handle = null,
        [Description(WindowDescription)] WindowSelector? window = null,
        [Description(CdpPortDescription)] int? cdpPort = null,
        CancellationToken cancellationToken = default)
    {
        InputGateToolFilter.ThrowIfPaused(_session);
        var target = Target(handle, window, cdpPort, RequireTargetId(targetId), "Nothing was done.");
        return await _tabs.ActivateTabAsync(target, cancellationToken).ConfigureAwait(false);
    }

    [McpServerTool(Name = "browser_navigate", Title = "Navigate Browser Tab", ReadOnly = false, Destructive = true, Idempotent = false, OpenWorld = true)]
    [Description("Open an http(s) or about:blank URL in a debug browser's tab (shown tab, targetId or newTab) and wait for it to load. " +
                 "Returns url, title, httpStatus, loadState; NavigationFailed with errorText; WaitTimedOut while still loading." + CdpOnly)]
    public async Task<BrowserNavigateResult> BrowserNavigate(
        [Description("http(s) URL, or about:blank.")] string url,
        [Description(HandleDescription)] long? handle = null,
        [Description(WindowDescription)] WindowSelector? window = null,
        [Description(CdpPortDescription)] int? cdpPort = null,
        [Description(TargetIdDescription + ShownTabDefault)] string? targetId = null,
        [Description("Open it in a new tab (in the browser's last active window).")] bool newTab = false,
        [Description("load, domcontentloaded or none.")] BrowserWaitUntil waitUntil = BrowserWaitUntil.Load,
        [Description("Time limit in ms for the navigation and load (1000-60000).")] int timeoutMs = DefaultNavigateTimeoutMs,
        [Description(BrowserPageTools.DialogDescription)] BrowserDialogChoice? dialog = null,
        [Description(BrowserPageTools.PromptTextDescription)] string? promptText = null,
        CancellationToken cancellationToken = default)
    {
        InputGateToolFilter.ThrowIfPaused(_session);
        var answer = BrowserPageTools.DialogAnswer(dialog, promptText);
        if (BrowserNavigationUrls.Refusal(url) is { } badUrl)
            throw new ToolErrorException(ToolErrorReporting.InvalidArgumentsErrorCode, badUrl);

        if (timeoutMs is < MinNavigateTimeoutMs or > MaxTimeoutMs)
        {
            throw new ToolErrorException(ToolErrorReporting.InvalidArgumentsErrorCode,
                $"timeoutMs must be {MinNavigateTimeoutMs}-{MaxTimeoutMs}, but it is {timeoutMs}. Nothing was done.");
        }

        if (newTab && targetId != null)
        {
            throw new ToolErrorException(ToolErrorReporting.InvalidArgumentsErrorCode,
                "newTab opens a new tab, so targetId cannot be used with it. Nothing was done.");
        }

        var target = Target(handle, window, cdpPort, targetId == null ? null : RequireTargetId(targetId), "Nothing was done.",
            needsTab: !newTab);
        return await _tabs.NavigateAsync(target, new BrowserNavigateOptions(url, newTab, waitUntil, timeoutMs, answer), cancellationToken)
            .ConfigureAwait(false);
    }

    [McpServerTool(Name = "wait_for_browser", Title = "Wait for Browser Page", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = true)]
    [Description("Wait until a debug browser tab's page meets every condition given (the *Contains ignore case). Returns satisfied, " +
                 "elapsedMs, url, title; WaitTimedOut with the page as last seen." + CdpOnly)]
    public async Task<BrowserWaitResult> WaitForBrowser(
        [Description(HandleDescription)] long? handle = null,
        [Description(WindowDescription)] WindowSelector? window = null,
        [Description(CdpPortDescription)] int? cdpPort = null,
        [Description(TargetIdDescription + ShownTabDefault)] string? targetId = null,
        [Description("The URL contains this.")] string? urlContains = null,
        [Description("The title contains this.")] string? titleContains = null,
        [Description("The visible text contains this.")] string? textContains = null,
        [Description("A CSS selector that matches an element.")] string? selector = null,
        [Description("Time limit in ms (0-60000; 0 checks once).")] int timeoutMs = DefaultWaitTimeoutMs,
        CancellationToken cancellationToken = default)
    {
        var condition = new BrowserWaitCondition(Given(urlContains), Given(titleContains), Given(textContains), Given(selector));
        if (condition.IsEmpty)
        {
            throw new ToolErrorException(ToolErrorReporting.InvalidArgumentsErrorCode,
                "Pass at least one condition: urlContains, titleContains, textContains or selector. Nothing was waited for.");
        }

        if (timeoutMs is < 0 or > MaxTimeoutMs)
        {
            throw new ToolErrorException(ToolErrorReporting.InvalidArgumentsErrorCode,
                $"timeoutMs must be 0-{MaxTimeoutMs}, but it is {timeoutMs}. Nothing was waited for.");
        }

        var target = Target(handle, window, cdpPort, targetId == null ? null : RequireTargetId(targetId), "Nothing was waited for.",
            needsTab: true);
        return await _tabs.WaitAsync(target, condition, timeoutMs, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// The browser and tab a call names: a window (handle or selector) with an optional cdpPort, or cdpPort alone, which then
    /// needs a targetId when the call works on one tab (<paramref name="needsTab"/>: no window shows a tab to default to).
    /// </summary>
    private BrowserTabTarget Target(long? handle, WindowSelector? window, int? cdpPort, string? targetId, string nothingDone, bool needsTab = false) =>
        Target(_windowService, handle, window, cdpPort, targetId, nothingDone, needsTab);

    /// <summary>
    /// The browser and tab a CDP tool names (shared with the DOM bridge tools, roadmap M5.8): a window (handle or selector)
    /// with an optional cdpPort, or cdpPort alone, which then needs a targetId when the call works on one tab.
    /// </summary>
    internal static BrowserTabTarget Target(
        IWindowService? windowService, long? handle, WindowSelector? window, int? cdpPort, string? targetId, string nothingDone, bool needsTab)
    {
        if (cdpPort is < 1 or > 65535)
        {
            throw new ToolErrorException(ToolErrorReporting.InvalidArgumentsErrorCode,
                $"cdpPort must be a TCP port from 1 to 65535, but it is {cdpPort}. {nothingDone}");
        }

        var windowHandle = WindowTargeting.Optional(windowService, handle, window);
        if (windowHandle == null && cdpPort == null)
        {
            throw new ToolErrorException(ToolErrorReporting.InvalidArgumentsErrorCode,
                $"Pass the browser window (handle or window) or its cdpPort. {nothingDone}");
        }

        if (windowHandle == null && needsTab && targetId == null)
        {
            throw new ToolErrorException(ToolErrorReporting.InvalidArgumentsErrorCode,
                $"With cdpPort and no window, pass targetId (from list_tabs): no window names the tab. {nothingDone}");
        }

        return new BrowserTabTarget(windowHandle, cdpPort, targetId);
    }

    internal static string RequireTargetId(string? targetId) =>
        targetId != null && CdpEndpointValidator.IsValidTargetId(targetId)
            ? targetId
            : throw new ToolErrorException(ToolErrorReporting.InvalidArgumentsErrorCode,
                "targetId must be a tab id from list_tabs or an earlier result (1 to 128 letters, digits, '.', '_' or '-'). Nothing was done.");

    private static string? Given(string? value) => string.IsNullOrEmpty(value) ? null : value;
}
