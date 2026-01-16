namespace WindowsAppManagerMcp.Models;

/// <summary>
/// Result of getting a browser URL.
/// </summary>
public record BrowserUrlResult(
    bool Success,
    string? Url,
    string? BrowserType,
    string? PageTitle,
    string? Error,
    BrowserErrorCode ErrorCode
);

/// <summary>
/// Result of getting browser page content.
/// </summary>
public record BrowserContentResult(
    bool Success,
    string? Content,
    string? Url,
    string? BrowserType,
    string? PageTitle,
    int? ContentLength,
    string? Error,
    BrowserErrorCode ErrorCode
);

/// <summary>
/// Options for browser content extraction.
/// </summary>
public record BrowserContentOptions(
    int? MaxLength = null,
    bool TextOnly = true
);

/// <summary>
/// Result of launching a browser with debug mode enabled.
/// </summary>
public record DebugBrowserLaunchResult(
    bool Success,
    int? ProcessId,
    nint? WindowHandle,
    int? CdpPort,
    string? UserDataDir,
    string? Error,
    string? ErrorCode
);
