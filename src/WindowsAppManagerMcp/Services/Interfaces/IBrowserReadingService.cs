using WindowsAppManagerMcp.Models;

namespace WindowsAppManagerMcp.Services.Interfaces;

/// <summary>
/// Service for reading URLs and content from browser windows using UI Automation or CDP.
/// </summary>
/// <remarks>
/// CDP is tried first on <see cref="CdpOptions.Port"/>, or without it on the debug port the window's own browser process
/// listens on (roadmap M5.3, <see cref="IDebugPortLocator"/>), unless <see cref="CdpOptions.UseCdp"/> is false. If CDP
/// fails, the result comes from UI Automation with <c>Source = "uia"</c> and the reason in <c>CdpError</c>, unless
/// <see cref="CdpOptions.Required"/> is set, in which case the CDP error is returned. A browser started without a debug
/// port is read through UI Automation without a <c>CdpError</c>.
/// </remarks>
public interface IBrowserReadingService
{
    /// <summary>
    /// Gets the URL from a browser window.
    /// </summary>
    /// <param name="browserHandle">The window handle of the browser.</param>
    /// <param name="cdp">Optional CDP settings (port, required, timeout). Null uses UI Automation only.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>Result containing the URL or error information.</returns>
    Task<BrowserUrlResult> GetBrowserUrlAsync(
        nint browserHandle,
        CdpOptions? cdp = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets the content from a browser window's page.
    /// HTML (TextOnly = false) is only available via CDP; UI Automation returns text only.
    /// </summary>
    /// <param name="browserHandle">The window handle of the browser.</param>
    /// <param name="options">Options for content extraction (max length, text-only, etc.).</param>
    /// <param name="cdp">Optional CDP settings (port, required, timeout). Null uses UI Automation only.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>Result containing the content or error information.</returns>
    Task<BrowserContentResult> GetBrowserContentAsync(
        nint browserHandle,
        BrowserContentOptions? options = null,
        CdpOptions? cdp = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets the URL from the currently active (foreground) browser window.
    /// </summary>
    /// <param name="cdp">Optional CDP settings (port, required, timeout). Null uses UI Automation only.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>Result containing the URL or error information.</returns>
    Task<BrowserUrlResult> GetActiveBrowserUrlAsync(
        CdpOptions? cdp = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Determines the browser type from a window handle and process name.
    /// </summary>
    /// <param name="browserHandle">The window handle.</param>
    /// <param name="processName">The process name.</param>
    /// <returns>The browser type (chrome, firefox, edge, etc.), or null if not a browser.</returns>
    string? GetBrowserType(nint browserHandle, string processName);

    /// <summary>
    /// Checks if the given window handle belongs to a browser.
    /// </summary>
    /// <param name="handle">The window handle to check.</param>
    /// <returns>True if the window is a recognized browser.</returns>
    bool IsBrowserWindow(nint handle);
}
