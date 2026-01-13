namespace WindowsAppManagerMcp.Services.Interfaces;

/// <summary>
/// Service for detecting browser executables and determining browser-specific behaviors.
/// Used to auto-inject flags like --new-window when launching browsers with URLs.
/// </summary>
public interface IBrowserDetectionService
{
    /// <summary>
    /// Determines if the given executable is a known browser.
    /// Supports exact matches (chrome.exe) and variants (chromium, firefox-dev, etc.).
    /// </summary>
    /// <param name="executable">The executable path or name to check.</param>
    /// <returns>True if the executable is a recognized browser.</returns>
    bool IsBrowser(string executable);

    /// <summary>
    /// Determines if the arguments contain a URL (http://, https://, or file://).
    /// </summary>
    /// <param name="arguments">Command line arguments to check.</param>
    /// <returns>True if any argument appears to be a URL.</returns>
    bool ContainsUrl(string[]? arguments);

    /// <summary>
    /// Gets the command line flag for opening URLs in a new window.
    /// Most Chromium and Firefox-based browsers support --new-window.
    /// </summary>
    /// <param name="executable">The browser executable.</param>
    /// <returns>The new window flag (e.g., "--new-window"), or null if not supported.</returns>
    string? GetNewWindowFlag(string executable);
}
