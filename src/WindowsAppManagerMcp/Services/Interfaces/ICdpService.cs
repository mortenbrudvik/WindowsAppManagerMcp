namespace WindowsAppManagerMcp.Services.Interfaces;

/// <summary>
/// Service for interacting with browsers via Chrome DevTools Protocol (CDP).
/// Provides richer content access when the browser is started with a debug port.
/// </summary>
public interface ICdpService
{
    /// <summary>
    /// Checks if a CDP debug port is available.
    /// </summary>
    /// <param name="port">The port to check (default 9222).</param>
    /// <returns>True if CDP is available on the specified port.</returns>
    Task<bool> IsDebugPortAvailableAsync(int port = 9222);

    /// <summary>
    /// Gets information about all available browser targets (tabs).
    /// </summary>
    /// <param name="port">The CDP port.</param>
    /// <returns>List of available targets with their URLs and titles.</returns>
    Task<IReadOnlyList<CdpTarget>> GetTargetsAsync(int port = 9222);

    /// <summary>
    /// Gets the current URL from a CDP target.
    /// </summary>
    /// <param name="targetId">The target ID (from GetTargetsAsync).</param>
    /// <param name="port">The CDP port.</param>
    /// <returns>The URL, or null if not available.</returns>
    Task<string?> GetUrlAsync(string targetId, int port = 9222);

    /// <summary>
    /// Gets the page content (HTML or text) from a CDP target.
    /// </summary>
    /// <param name="targetId">The target ID.</param>
    /// <param name="port">The CDP port.</param>
    /// <param name="textOnly">If true, extracts text content only; if false, returns full HTML.</param>
    /// <returns>The page content.</returns>
    Task<string?> GetPageContentAsync(string targetId, int port = 9222, bool textOnly = true);

    /// <summary>
    /// Gets the page title from a CDP target.
    /// </summary>
    /// <param name="targetId">The target ID.</param>
    /// <param name="port">The CDP port.</param>
    /// <returns>The page title.</returns>
    Task<string?> GetPageTitleAsync(string targetId, int port = 9222);

    /// <summary>
    /// Finds a CDP target that matches the given window title.
    /// </summary>
    /// <param name="windowTitle">The window title to match.</param>
    /// <param name="port">The CDP port.</param>
    /// <returns>The matching target, or null if not found.</returns>
    Task<CdpTarget?> FindTargetByTitleAsync(string windowTitle, int port = 9222);
}

/// <summary>
/// Represents a CDP browser target (tab).
/// </summary>
public record CdpTarget(
    string Id,
    string Type,
    string Title,
    string Url,
    string? WebSocketDebuggerUrl
);
