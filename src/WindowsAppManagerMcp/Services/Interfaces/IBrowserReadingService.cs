using WindowsAppManagerMcp.Models;

namespace WindowsAppManagerMcp.Services.Interfaces;

/// <summary>
/// Service for reading URLs and content from browser windows using UI Automation or CDP.
/// </summary>
public interface IBrowserReadingService
{
    /// <summary>
    /// Gets the URL from a browser window's address bar.
    /// </summary>
    /// <param name="browserHandle">The window handle of the browser.</param>
    /// <param name="cdpPort">Optional CDP port to use for extraction (more reliable if available).</param>
    /// <returns>Result containing the URL or error information.</returns>
    BrowserUrlResult GetBrowserUrl(nint browserHandle, int? cdpPort = null);

    /// <summary>
    /// Gets the text content from a browser window's page.
    /// </summary>
    /// <param name="browserHandle">The window handle of the browser.</param>
    /// <param name="options">Options for content extraction (max length, text-only, etc.).</param>
    /// <param name="cdpPort">Optional CDP port to use for extraction (more reliable if available).</param>
    /// <returns>Result containing the content or error information.</returns>
    BrowserContentResult GetBrowserContent(nint browserHandle, BrowserContentOptions? options = null, int? cdpPort = null);

    /// <summary>
    /// Gets the URL from the currently active (foreground) browser window.
    /// </summary>
    /// <param name="cdpPort">Optional CDP port to use for extraction.</param>
    /// <returns>Result containing the URL or error information.</returns>
    BrowserUrlResult GetActiveBrowserUrl(int? cdpPort = null);

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

    /// <summary>
    /// Checks if CDP is available on the specified port.
    /// </summary>
    /// <param name="port">The port to check (default 9222).</param>
    /// <returns>True if CDP is available.</returns>
    Task<bool> IsCdpAvailableAsync(int port = 9222);

    /// <summary>
    /// Gets URL and content using CDP directly (without window handle).
    /// </summary>
    /// <param name="port">The CDP port.</param>
    /// <param name="targetIndex">Index of the target/tab (0 = first).</param>
    /// <returns>Result containing URL and content.</returns>
    Task<BrowserContentResult> GetContentViaCdpAsync(int port = 9222, int targetIndex = 0);
}
