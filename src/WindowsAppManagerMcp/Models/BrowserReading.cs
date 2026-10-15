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
/// <param name="CdpError">Why CDP was not used ("Code: message") when a cdpPort was given but the result came from UI Automation.</param>
/// <param name="DisplayUrl">UI Automation: the address bar's text, when it is URL-like (roadmap M5.7); it can lack the
/// scheme.</param>
/// <param name="UrlSource">UI Automation: where <paramref name="Url"/> came from, <see cref="BrowserUrlSource"/>.</param>
/// <param name="IsEditing">UI Automation: whether the address bar has the keyboard focus, so its text may be typed rather
/// than the page's address; null when no address bar was read.</param>
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
    bool? IsEditing = null
);

/// <summary>
/// Result of getting browser page content.
/// </summary>
/// <param name="Source">"cdp" or "uia": the method that produced (or failed to produce) the result.</param>
/// <param name="TargetId">The CDP target (tab) ID when the result came from CDP.</param>
/// <param name="CdpError">Why CDP was not used ("Code: message") when a cdpPort was given but the result came from UI Automation.</param>
/// <param name="AccessibilityWarmup">Set when the page is web content (Chromium) and the UI Automation read waited for its
/// accessibility tree first (roadmap M3.3, M3.F24); also on a failed read.</param>
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
    AccessibilityWarmupInfo? AccessibilityWarmup = null
);

/// <summary>
/// Options for browser content extraction.
/// </summary>
public record BrowserContentOptions(
    int? MaxLength = null,
    bool TextOnly = true
);

/// <summary>
/// Options for reading a browser window via CDP (Chrome DevTools Protocol).
/// </summary>
/// <param name="Port">The browser's debug port, or null to use UI Automation only.</param>
/// <param name="Required">If true, a CDP failure is returned as an error instead of falling back to UI Automation.</param>
/// <param name="TimeoutMs">Time budget for all CDP requests of one call, and another for its UI Automation read (roadmap
/// M3.F24). Clamped to [MinTimeoutMs, MaxTimeoutMs].</param>
/// <param name="TargetId">The CDP tab to read instead of the one the window shows (roadmap M5.2), or null. It must be a tab
/// of the browser on <paramref name="Port"/>, and implies <paramref name="Required"/>.</param>
public record CdpOptions(
    int? Port = null,
    bool Required = false,
    int TimeoutMs = CdpOptions.DefaultTimeoutMs,
    string? TargetId = null)
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
/// <param name="CdpPort">The DevTools port the browser listens on: the one it reported for port 0, else the one asked for.</param>
/// <param name="Warning">What the launch's window wait found, when it was not a window (see LaunchResult.Warning).</param>
/// <param name="LaunchStatus">What the launch led to (LaunchResult.Status), when the browser was started.</param>
public record DebugBrowserLaunchResult(
    bool Success,
    int? ProcessId,
    nint? WindowHandle,
    int? CdpPort,
    string? UserDataDir,
    string? Error,
    string? ErrorCode,
    string? Warning = null,
    LaunchStatus? LaunchStatus = null
);

/// <summary>
/// A debug browser that has started and reported its DevTools port, for <see cref="Services.Interfaces.IDebugBrowserReadiness"/>.
/// </summary>
public sealed record DebugBrowserEndpoint(string Browser, int ProcessId, int Port, string UserDataDir);

/// <summary>Whether a debug browser's DevTools endpoint answers, and if not, why (a sentence for the result's error).</summary>
public sealed record DebugBrowserReadiness(bool Ready, string? Error = null);
