using System.Text.RegularExpressions;

namespace WindowsAppManagerMcp.Services;

/// <summary>
/// Recognizes the programs built on Chromium, which read --force-renderer-accessibility (launch_application's
/// forceAccessibility, roadmap M3.3): Chromium-based browsers (Chrome, Edge, Brave, Vivaldi), Electron apps (VS Code,
/// Slack, Discord) and CEF apps (Spotify). The flag is added only to them, never to another program, whose meaning for it
/// is unknown.
/// </summary>
/// <remarks>
/// A program counts when its .exe exists and Chromium's resource files, icudtl.dat (ICU's data) and resources.pak, lie
/// next to it, as in an Electron or CEF app, or in a version-named folder next to it, as in a Chromium browser
/// (Application\131.0.6778.86\). The name is not trusted alone, so a program called chrome.exe somewhere else does not
/// count. An installer, uninstaller, updater or helper next to those files (unins000.exe, Update.exe, crashpad_handler.exe)
/// is not the program itself and does not count either.
/// </remarks>
public sealed partial class ChromiumPrograms
{
    /// <summary>The switch that makes Chromium build the accessibility tree of every page from the start.</summary>
    public const string ForceRendererAccessibility = "--force-renderer-accessibility";

    private static readonly string[] ResourceFiles = ["icudtl.dat", "resources.pak"];

    // Parts of the names of programs that are shipped next to a Chromium program without being one
    private static readonly string[] HelperNameParts =
        ["unins", "uninstall", "update", "setup", "install", "squirrel", "elevat", "crashpad", "helper", "notification", "proxy", "pwa_launcher"];

    private readonly Func<string, bool> _fileExists;
    private readonly Func<string, IEnumerable<string>> _directories;

    /// <summary>Reads the real file system.</summary>
    public ChromiumPrograms()
        : this(File.Exists, ListDirectories)
    {
    }

    /// <param name="fileExists">Whether a file exists.</param>
    /// <param name="directories">The folders directly inside a folder, as full paths (none when it cannot be read).</param>
    public ChromiumPrograms(Func<string, bool> fileExists, Func<string, IEnumerable<string>> directories)
    {
        _fileExists = fileExists;
        _directories = directories;
    }

    /// <summary>
    /// Why <paramref name="executablePath"/> is not a Chromium, Electron or CEF program, or null when it is one.
    /// </summary>
    /// <param name="executablePath">The resolved, fully qualified path of the program to launch.</param>
    public string? WhyNot(string executablePath)
    {
        try
        {
            if (!Path.IsPathFullyQualified(executablePath) ||
                !string.Equals(Path.GetExtension(executablePath), ".exe", StringComparison.OrdinalIgnoreCase))
            {
                return "it is not a program's .exe";
            }

            if (!_fileExists(executablePath))
            {
                return "the program does not exist";
            }

            var name = Path.GetFileNameWithoutExtension(executablePath);
            if (HelperNameParts.Any(part => name.Contains(part, StringComparison.OrdinalIgnoreCase)))
            {
                return "it is an installer, updater or helper, not the program itself";
            }

            var folder = Path.GetDirectoryName(executablePath);
            if (string.IsNullOrEmpty(folder))
            {
                return "it is not a program's .exe";
            }

            if (HasResources(folder) || _directories(folder).Any(d => VersionFolder().IsMatch(Path.GetFileName(d)) && HasResources(d)))
            {
                return null;
            }

            return "Chromium's resource files (icudtl.dat and resources.pak) are not next to it, so it is not a Chromium, " +
                   "Electron or CEF program";
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return "its folder cannot be read";
        }
    }

    /// <summary>True when <paramref name="argument"/> already asks for renderer accessibility.</summary>
    public static bool IsForceAccessibilityArgument(string argument) =>
        string.Equals(argument, ForceRendererAccessibility, StringComparison.OrdinalIgnoreCase) ||
        argument.StartsWith(ForceRendererAccessibility + "=", StringComparison.OrdinalIgnoreCase);

    private bool HasResources(string folder) => ResourceFiles.All(file => _fileExists(Path.Combine(folder, file)));

    private static IEnumerable<string> ListDirectories(string folder)
    {
        try
        {
            return Directory.EnumerateDirectories(folder).ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return [];
        }
    }

    [GeneratedRegex(@"^\d+(\.\d+){1,3}$")]
    private static partial Regex VersionFolder();
}
