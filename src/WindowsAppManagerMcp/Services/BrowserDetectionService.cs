using WindowsAppManagerMcp.Services.Interfaces;

namespace WindowsAppManagerMcp.Services;

/// <summary>
/// Service for detecting browser executables using a tiered matching approach.
/// Tier 1: Exact matches for common browsers
/// Tier 2: Prefix patterns for browser variants (chrome-canary, firefox-dev, etc.)
/// Tier 3: Suffix patterns for portable/custom installations
/// </summary>
public class BrowserDetectionService : IBrowserDetectionService
{
    // Tier 1: Exact matches - guaranteed browsers (O(1) lookup)
    private static readonly HashSet<string> ExactBrowserMatches = new(StringComparer.OrdinalIgnoreCase)
    {
        // Chrome family
        "chrome", "chrome.exe",
        "google-chrome", "google-chrome.exe",

        // Edge family
        "msedge", "msedge.exe",

        // Firefox family
        "firefox", "firefox.exe",

        // Brave
        "brave", "brave.exe",

        // Opera family
        "opera", "opera.exe",

        // Vivaldi
        "vivaldi", "vivaldi.exe",

        // Arc (Windows version)
        "arc", "arc.exe",

        // Chromium
        "chromium", "chromium.exe"
    };

    // Tier 2: Prefix patterns for browser variants
    private static readonly string[] BrowserPrefixes =
    [
        // Chrome variants
        "chrome-",           // chrome-canary, chrome-dev, chrome-beta
        "chromium-",         // chromium-browser
        "ungoogled-chromium",

        // Edge variants
        "msedge-",           // msedge-canary, msedge-dev, msedge-beta

        // Firefox variants
        "firefox-",          // firefox-dev, firefox-nightly, firefox-esr
        "firefoxdeveloper",
        "waterfox",
        "librewolf",
        "floorp",
        "zen-browser",

        // Opera variants
        "opera-",            // opera-developer, opera-beta
        "opera_",            // opera_developer

        // Brave variants
        "brave-",            // brave-browser, brave-nightly

        // Vivaldi variants
        "vivaldi-",          // vivaldi-snapshot

        // Other Chromium-based
        "thorium"
    ];

    // Tier 3: Suffix patterns for portable/custom installations
    private static readonly string[] BrowserSuffixes =
    [
        "-browser",          // zen-browser, waterfox-browser
        "-portable"          // chrome-portable, firefox-portable
    ];

    /// <inheritdoc />
    public bool IsBrowser(string executable)
    {
        if (string.IsNullOrWhiteSpace(executable))
            return false;

        var name = Path.GetFileName(executable);
        var nameWithoutExt = Path.GetFileNameWithoutExtension(name);

        // Tier 1: Exact match (O(1))
        if (ExactBrowserMatches.Contains(name) ||
            ExactBrowserMatches.Contains(nameWithoutExt))
            return true;

        // Tier 2: Prefix match
        foreach (var prefix in BrowserPrefixes)
        {
            if (nameWithoutExt.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        // Tier 3: Suffix match
        foreach (var suffix in BrowserSuffixes)
        {
            if (nameWithoutExt.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    /// <inheritdoc />
    public bool ContainsUrl(string[]? arguments)
    {
        if (arguments == null || arguments.Length == 0)
            return false;

        return arguments.Any(a =>
            a.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
            a.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ||
            a.StartsWith("file://", StringComparison.OrdinalIgnoreCase));
    }

    /// <inheritdoc />
    public string? GetNewWindowFlag(string executable)
    {
        if (!IsBrowser(executable))
            return null;

        // Most Chromium and Firefox-based browsers support --new-window
        // If we discover browsers that don't support it, we can add exceptions here
        return "--new-window";
    }
}
