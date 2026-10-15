namespace WindowsAppManagerMcp.Services;

/// <summary>
/// Finds the .exe of a browser for launch_browser_with_debug (roadmap M5.1), in this order:
/// <list type="number">
/// <item>App Paths (HKCU, then HKLM, <see cref="ExecutableResolver.ReadAppPathsFromRegistry"/>), where installers
/// register the browser they installed, wherever that is;</item>
/// <item>the install folder under Program Files, then Program Files (x86) (system-wide installs, on any system drive);</item>
/// <item>the install folder under %LOCALAPPDATA% (per-user installs of Chrome and Brave).</item>
/// </list>
/// An explicit path is used as given and nothing else is tried. Nothing is resolved from the server's current directory
/// or PATH: a chrome.exe planted there would be started with an open debug port. An App Paths value counts only when it
/// is a full path to the browser's own file name, so a registration of another program is not started.
/// </summary>
public sealed class DebugBrowserLocator
{
    private sealed record BrowserInstall(string ExecutableName, string InstallFolder);

    private static readonly Dictionary<string, BrowserInstall> Installs = new(StringComparer.OrdinalIgnoreCase)
    {
        ["chrome"] = new("chrome.exe", @"Google\Chrome\Application"),
        ["edge"] = new("msedge.exe", @"Microsoft\Edge\Application"),
        ["brave"] = new("brave.exe", @"BraveSoftware\Brave-Browser\Application"),
    };

    private readonly Func<string, bool> _fileExists;
    private readonly Func<string, IReadOnlyList<string>> _appPaths;
    private readonly Func<Environment.SpecialFolder, string> _folderPath;

    /// <summary>A locator that reads the real file system and registry.</summary>
    public DebugBrowserLocator()
        : this(File.Exists, ExecutableResolver.ReadAppPathsFromRegistry, Environment.GetFolderPath)
    {
    }

    /// <param name="fileExists">Whether a file exists.</param>
    /// <param name="appPaths">The App Paths values of an executable name, HKCU first.</param>
    /// <param name="folderPath">A special folder's path, empty when it has none.</param>
    public DebugBrowserLocator(
        Func<string, bool> fileExists,
        Func<string, IReadOnlyList<string>> appPaths,
        Func<Environment.SpecialFolder, string> folderPath)
    {
        _fileExists = fileExists;
        _appPaths = appPaths;
        _folderPath = folderPath;
    }

    /// <summary>The file name of a supported browser's .exe (chrome.exe, msedge.exe, brave.exe), or null.</summary>
    public static string? ExecutableName(string browser) =>
        Installs.TryGetValue(browser, out var install) ? install.ExecutableName : null;

    /// <summary>
    /// The browser's .exe and the places tried, in order: App Paths values and install paths, or "App Paths\name" when
    /// nothing is registered.
    /// </summary>
    public DebugBrowserLocation Locate(string browser, string? executablePath = null)
    {
        if (!string.IsNullOrWhiteSpace(executablePath))
        {
            var path = executablePath.Trim();
            return new DebugBrowserLocation(_fileExists(path) ? path : null, [path]);
        }

        if (!Installs.TryGetValue(browser, out var install))
            return new DebugBrowserLocation(null, []);

        var tried = new List<string>();
        var registered = _appPaths(install.ExecutableName);
        if (registered.Count == 0)
            tried.Add($@"App Paths\{install.ExecutableName}");

        foreach (var value in registered)
        {
            var path = value.Trim().Trim('"');
            tried.Add(path);
            if (Path.IsPathFullyQualified(path) &&
                string.Equals(Path.GetFileName(path), install.ExecutableName, StringComparison.OrdinalIgnoreCase) &&
                _fileExists(path))
                return new DebugBrowserLocation(path, tried);
        }

        foreach (var folder in new[] { Environment.SpecialFolder.ProgramFiles, Environment.SpecialFolder.ProgramFilesX86, Environment.SpecialFolder.LocalApplicationData })
        {
            var root = _folderPath(folder);
            if (string.IsNullOrEmpty(root))
                continue;

            var path = Path.Combine(root, install.InstallFolder, install.ExecutableName);
            if (tried.Contains(path, StringComparer.OrdinalIgnoreCase))
                continue; // a 32-bit view where both Program Files are the same folder, or the App Paths value again

            tried.Add(path);
            if (_fileExists(path))
                return new DebugBrowserLocation(path, tried);
        }

        return new DebugBrowserLocation(null, tried);
    }
}

/// <summary>Where a browser's .exe was found (null when it was not), and every place tried in order.</summary>
public sealed record DebugBrowserLocation(string? Path, IReadOnlyList<string> Tried);
