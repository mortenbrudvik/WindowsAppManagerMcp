namespace WindowsAppManagerMcp.Models;

/// <summary>
/// Where browser data came from: the Chrome DevTools Protocol or Windows UI Automation.
/// </summary>
public static class BrowserReadSource
{
    public const string Cdp = "cdp";
    public const string Uia = "uia";
}

/// <summary>
/// Where the UI Automation read of a browser found the URL (roadmap M5.7): the page's Document, which holds the address the
/// tab committed, or the address bar, which shows it (Chrome leaves out the scheme there).
/// </summary>
public static class BrowserUrlSource
{
    public const string Document = "document";
    public const string AddressBar = "addressBar";
}

/// <summary>
/// Result of getting a browser URL.
/// </summary>
/// <param name="Source">"cdp" or "uia": the method that produced (or failed to produce) the result.</param>
/// <param name="TargetId">The CDP target (tab) ID when the result came from CDP.</param>
/// <param name="CdpError">Why CDP was not used ("Code: message") when a cdpPort was given or a debug browser's port was looked for,
/// but the result came from UI Automation.</param>
/// <param name="DisplayUrl">UI Automation: the address bar's text, when it is URL-like (roadmap M5.7); it can lack the
/// scheme.</param>
/// <param name="UrlSource">UI Automation: where <paramref name="Url"/> came from, <see cref="BrowserUrlSource"/>.</param>
/// <param name="IsEditing">UI Automation: whether the address bar has the keyboard focus, so its text may be typed rather
/// than the page's address; null when no address bar was read.</param>
/// <param name="CdpPort">CDP: the DevTools port that was read, given as cdpPort or found for the window (roadmap M5.3).</param>
public record BrowserUrlResult(
    bool Success,
    string? Url,
    string? BrowserType,
    string? PageTitle,
    string? Error,
    BrowserErrorCode ErrorCode,
    string? Source = null,
    string? TargetId = null,
    string? CdpError = null,
    string? DisplayUrl = null,
    string? UrlSource = null,
    bool? IsEditing = null,
    int? CdpPort = null
);

/// <summary>
/// Result of getting browser page content.
/// </summary>
/// <param name="Source">"cdp" or "uia": the method that produced (or failed to produce) the result.</param>
/// <param name="TargetId">The CDP target (tab) ID when the result came from CDP.</param>
/// <param name="CdpError">Why CDP was not used ("Code: message") when a cdpPort was given or a debug browser's port was looked for,
/// but the result came from UI Automation.</param>
/// <param name="AccessibilityWarmup">Set when the page is web content (Chromium) and the UI Automation read waited for its
/// accessibility tree first (roadmap M3.3, M3.F24); also on a failed read.</param>
/// <param name="CdpPort">CDP: the DevTools port that was read, given as cdpPort or found for the window (roadmap M5.3).</param>
/// <param name="ContentLength">How many characters <paramref name="Content"/> holds.</param>
/// <param name="Offset">Where the page starts in the text of <paramref name="Source"/> (roadmap M5.6): the offset asked for,
/// or the end of a shorter text. CDP counts UTF-16 characters of the page's innerText or HTML (of the selector's matches);
/// UI Automation counts the Text pattern's characters, or those of the text collected from the page's elements.</param>
/// <param name="TotalLength">The whole text's length, when known: always through CDP (measured in the page, before it is
/// cut), through UI Automation once the last page was read.</param>
/// <param name="Truncated">True when more text follows the page.</param>
/// <param name="NextOffset">The offset of the next page, when more text follows.</param>
/// <param name="TextSkipped">True when a UI Automation page was cut inside one of the provider's characters that was
/// longer than the page (read_element_text's textSkipped): the rest of that character is not returned, and
/// <paramref name="NextOffset"/> is after it. Null otherwise.</param>
public record BrowserContentResult(
    bool Success,
    string? Content,
    string? Url,
    string? BrowserType,
    string? PageTitle,
    int? ContentLength,
    string? Error,
    BrowserErrorCode ErrorCode,
    string? Source = null,
    string? TargetId = null,
    string? CdpError = null,
    AccessibilityWarmupInfo? AccessibilityWarmup = null,
    int? CdpPort = null,
    int? Offset = null,
    int? TotalLength = null,
    bool? Truncated = null,
    int? NextOffset = null,
    bool? TextSkipped = null
);

/// <summary>
/// Options for browser content extraction: one page of the page's text or HTML (roadmap M5.6).
/// </summary>
/// <param name="MaxLength">The most characters to return, from 1 to <see cref="MaxMaxLength"/>; a page also holds at most
/// <see cref="Services.ElementReadService.MaxTextBytes"/> bytes of JSON, as read_element_text's do.</param>
/// <param name="TextOnly">True for the text, false for the HTML (CDP only).</param>
/// <param name="Offset">Where the page starts, in the text of the source that reads it (0 or more).</param>
/// <param name="Selector">A CSS selector whose matches' text (or HTML) is read instead of the whole page's, one per line;
/// CDP only, so it never falls back to UI Automation. Null for the whole page.</param>
public record BrowserContentOptions(
    int MaxLength = BrowserContentOptions.DefaultMaxLength,
    bool TextOnly = true,
    int Offset = 0,
    string? Selector = null)
{
    /// <summary>The page length when none is given: read_element_text's.</summary>
    public const int DefaultMaxLength = 20_000;

    /// <summary>The longest page: read_element_text's.</summary>
    public const int MaxMaxLength = Services.ElementReadService.MaxTextLength;

    /// <summary>The page length after clamping to 1-<see cref="MaxMaxLength"/>.</summary>
    public int EffectiveMaxLength => Math.Clamp(MaxLength, 1, MaxMaxLength);

    /// <summary>The offset, never below 0.</summary>
    public int EffectiveOffset => Math.Max(0, Offset);
}

/// <summary>
/// Options for reading a browser window via CDP (Chrome DevTools Protocol).
/// </summary>
/// <param name="Port">The browser's debug port, or null to use the port the window's own process listens on, when it was
/// started with one (roadmap M5.3).</param>
/// <param name="Required">If true, a CDP failure is returned as an error instead of falling back to UI Automation.</param>
/// <param name="TimeoutMs">Time budget for all CDP requests of one call, and another for its UI Automation read (roadmap
/// M3.F24). Clamped to [MinTimeoutMs, MaxTimeoutMs].</param>
/// <param name="TargetId">The CDP tab to read instead of the one the window shows (roadmap M5.2), or null. It must be a tab
/// of the browser on <paramref name="Port"/>, and implies <paramref name="Required"/>.</param>
/// <param name="UseCdp">False reads through UI Automation only: no port is looked for and nothing is connected (roadmap
/// M5.3).</param>
public record CdpOptions(
    int? Port = null,
    bool Required = false,
    int TimeoutMs = CdpOptions.DefaultTimeoutMs,
    string? TargetId = null,
    bool UseCdp = true)
{
    public const int DefaultTimeoutMs = 10000;
    public const int MinTimeoutMs = 1000;
    public const int MaxTimeoutMs = 30000;

    /// <summary>The time budget after clamping.</summary>
    public TimeSpan EffectiveTimeout => TimeSpan.FromMilliseconds(Math.Clamp(TimeoutMs, MinTimeoutMs, MaxTimeoutMs));

    /// <summary>
    /// Whether a CDP failure is returned instead of falling back to UI Automation: requireCdp, or a targetId, which UI
    /// Automation cannot read (it sees only the tab the window shows).
    /// </summary>
    public bool EffectiveRequired => Required || TargetId != null;
}

/// <summary>
/// What launch_browser_with_debug starts (roadmap M5.1).
/// </summary>
/// <param name="Browser">chrome, edge or brave.</param>
/// <param name="Url">A URL to open, or null.</param>
/// <param name="Port">The DevTools port: <see cref="EphemeralPort"/> lets the browser pick a free one and report it in
/// the profile's DevToolsActivePort file; otherwise <see cref="MinExplicitPort"/> to 65535.</param>
/// <param name="UserDataDir">The profile folder, or null for a new folder under %TEMP%.</param>
/// <param name="ExecutablePath">The browser's .exe, or null to look for it (App Paths, Program Files, %LOCALAPPDATA%).</param>
/// <param name="WaitTimeoutMs">How long to wait for the browser's window.</param>
public sealed record DebugBrowserLaunchRequest(
    string Browser = "chrome",
    string? Url = null,
    int Port = DebugBrowserLaunchRequest.EphemeralPort,
    string? UserDataDir = null,
    string? ExecutablePath = null,
    int WaitTimeoutMs = 10000)
{
    /// <summary>Port 0: the browser picks a free port, so no other program can take it between a check and the start.</summary>
    public const int EphemeralPort = 0;

    /// <summary>The lowest explicit port: below it are the well-known ports of system services.</summary>
    public const int MinExplicitPort = 1024;

    public const int MaxPort = 65535;

    /// <summary>Whether <paramref name="port"/> is 0 or an explicit port from 1024 to 65535.</summary>
    public static bool IsValidPort(int port) => port == EphemeralPort || port is >= MinExplicitPort and <= MaxPort;
}

/// <summary>
/// Result of launching a browser with debug mode enabled.
/// </summary>
/// <param name="ProcessId">The browser process that was started. A failure after the start still names it (and the
/// window and profile) when it is running, so that it can be closed.</param>
/// <param name="CdpPort">The DevTools port the browser listens on: the one it reported for port 0, else the one asked for.
/// Null when the port is not known to be the browser's: it reported none, it exited, or the readiness check found the
/// port held by another process or could not tell whose it is.</param>
/// <param name="Warning">What the launch's window wait found, when it was not a window (see LaunchResult.Warning).</param>
/// <param name="LaunchStatus">What the launch led to (LaunchResult.Status), when the browser was started.</param>
/// <param name="CdpReady">True once the DevTools endpoint answered /json/version, false when a launch that started the
/// browser failed before it did; null when it was not checked (nothing was started, or the server has no readiness check).</param>
/// <param name="BrowserVersion">The browser and version the endpoint reported, when it is ready.</param>
/// <param name="CdpEndpoint">The DevTools HTTP endpoint, http://127.0.0.1:port, when it is ready.</param>
/// <param name="BrowserWsEndpoint">The browser-level DevTools WebSocket URL from /json/version, when it is ready.</param>
public record DebugBrowserLaunchResult(
    bool Success,
    int? ProcessId,
    nint? WindowHandle,
    int? CdpPort,
    string? UserDataDir,
    string? Error,
    string? ErrorCode,
    string? Warning = null,
    LaunchStatus? LaunchStatus = null,
    bool? CdpReady = null,
    string? BrowserVersion = null,
    string? CdpEndpoint = null,
    string? BrowserWsEndpoint = null
);

/// <summary>
/// A debug browser that has started and reported its DevTools port, for <see cref="Services.Interfaces.IDebugBrowserReadiness"/>.
/// </summary>
/// <param name="StartTime">The started process's start time, read right after the start, so that a check can tell the
/// browser from a process that got its PID after it exited; null when it could not be read.</param>
public sealed record DebugBrowserEndpoint(string Browser, int ProcessId, int Port, string UserDataDir, DateTime? StartTime = null);

/// <summary>Why a debug browser's DevTools endpoint is not ready.</summary>
public enum DebugBrowserNotReadyReason
{
    /// <summary>It did not answer within the wait (nothing listened, or each request timed out).</summary>
    NotAnswered,

    /// <summary>It answered, but not as a browser's DevTools endpoint does; asking again would not change that.</summary>
    NotABrowsersAnswer,

    /// <summary>Another process than the started browser listens on the port, so the port is not the browser's.</summary>
    PortHeldByOtherProcess,

    /// <summary>The port's owner could not be read and nothing else shows the answer is the started browser's.</summary>
    PortOwnerUnknown,

    /// <summary>The started browser exited before its endpoint answered.</summary>
    BrowserExited
}

/// <summary>Whether a debug browser's DevTools endpoint answers, and if not, why (a sentence for the result's error).</summary>
/// <param name="BrowserVersion">The browser and version its /json/version reported, for example "Chrome/141.0.7390.55".</param>
/// <param name="CdpEndpoint">The DevTools HTTP endpoint that answered, http://127.0.0.1:port.</param>
/// <param name="BrowserWebSocketUrl">The browser-level DevTools WebSocket URL from /json/version, checked to be ws:// on
/// 127.0.0.1 and on the port.</param>
/// <param name="Reason">Why it is not ready; null when it is ready, or when the check does not say.</param>
public sealed record DebugBrowserReadiness(
    bool Ready,
    string? Error = null,
    string? BrowserVersion = null,
    string? CdpEndpoint = null,
    string? BrowserWebSocketUrl = null,
    DebugBrowserNotReadyReason? Reason = null);
