namespace WindowsAppManagerMcp.Services.Interfaces;

/// <summary>
/// Abstraction over Windows UI Automation API for browser reading operations.
/// This interface enables unit testing by allowing mock implementations.
/// </summary>
public interface IUIAutomationWrapper
{
    /// <summary>
    /// Gets the automation element from a window handle.
    /// </summary>
    /// <param name="hwnd">The window handle.</param>
    /// <returns>True if the element was successfully retrieved.</returns>
    bool TryGetElementFromHandle(nint hwnd, out object? element);

    /// <summary>
    /// Finds the URL bar element in a browser window.
    /// </summary>
    /// <param name="browserHandle">The browser window handle.</param>
    /// <param name="browserType">The type of browser (chrome, firefox, edge, etc.).</param>
    /// <returns>The URL from the address bar, or null if not found.</returns>
    string? GetBrowserUrl(nint browserHandle, string browserType);

    /// <summary>
    /// Gets the window title using UI Automation.
    /// </summary>
    /// <param name="hwnd">The window handle.</param>
    /// <returns>The window title, or null if not found.</returns>
    string? GetWindowTitle(nint hwnd);

    /// <summary>
    /// Extracts text content from a browser's content area.
    /// </summary>
    /// <param name="browserHandle">The browser window handle.</param>
    /// <param name="browserType">The type of browser.</param>
    /// <param name="maxLength">Maximum length of content to return.</param>
    /// <returns>The text content from the browser, or null if extraction failed.</returns>
    string? GetBrowserContent(nint browserHandle, string browserType, int? maxLength = null);

    /// <summary>
    /// Checks if UI Automation is available on this system.
    /// </summary>
    /// <returns>True if UI Automation is available.</returns>
    bool IsAvailable();
}
