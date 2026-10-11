using System.Security;
using System.Text.RegularExpressions;
using Microsoft.Win32;
using WindowsAppManagerMcp.Models;
using WindowsAppManagerMcp.Services.Interfaces;

namespace WindowsAppManagerMcp.Services;

/// <summary>
/// Resolves launch targets the way ShellExecuteEx would, except that bare names are never
/// resolved from the current or working directory.
/// </summary>
/// <remarks>
/// ShellExecuteEx searches a bare name in the current directory first, so a repository containing
/// notepad.exe or code.cmd would hijack launch_application("notepad"). This resolver searches
/// the documented locations without the current directory (Windows directory, System32, absolute
/// PATH entries, then App Paths in HKCU and HKLM) and returns the absolute path, so the file that
/// was checked is the file that runs. The result also tells the caller whether the target is a
/// batch file, whose arguments cmd.exe will re-parse.
/// <para>
/// The order is ShellExecuteEx's documented one without the current directory, on purpose not App Paths
/// first: a per-user App Paths key (HKCU, writable by any program of the user) would then shadow the
/// copies in the Windows directory and System32. PATH is enumerated by hand (SearchPathW cannot leave
/// the current directory out); empty, '.', relative and drive-relative entries are skipped. A bare name
/// without an extension tries the program types (.com, .exe, .bat, .cmd) in PATHEXT's order, as cmd.exe
/// does, but no script types such as .vbs or .js (the planted-file check covers every PATHEXT type). A bare name found in
/// none of these places is reported as <see cref="ResolvedLaunchTarget.NotFound"/>, never passed on.
/// </para>
/// </remarks>
public partial class ExecutableResolver : IExecutableResolver
{
    // The only types a bare name resolves to, in PATHEXT's order (in this order when PATHEXT lists none of them).
    // A path without an extension always uses these.
    private static readonly string[] ProgramExtensions = [".com", ".exe", ".bat", ".cmd"];

    private const string AppPathsKey = @"Software\Microsoft\Windows\CurrentVersion\App Paths";

    private readonly Func<string, bool> _fileExists;
    private readonly Func<string?> _getPathVariable;
    private readonly Func<string, IReadOnlyList<string>> _getAppPathCandidates;
    private readonly string _windowsDirectory;
    private readonly string _systemDirectory;
    private readonly Func<string> _getCurrentDirectory;
    private readonly Func<string?> _getPathExtVariable;

    /// <summary>
    /// Creates a resolver that uses the real file system, PATH and registry.
    /// </summary>
    public ExecutableResolver()
        : this(
            File.Exists,
            () => Environment.GetEnvironmentVariable("PATH"),
            ReadAppPathsFromRegistry,
            Environment.GetFolderPath(Environment.SpecialFolder.Windows),
            Environment.SystemDirectory,
            () => Environment.CurrentDirectory,
            () => Environment.GetEnvironmentVariable("PATHEXT"))
    {
    }

    /// <summary>
    /// Creates a resolver with injectable environment access (used by tests).
    /// </summary>
    /// <param name="getPathExtVariable">
    /// Reads PATHEXT, the order in which a bare name without an extension tries .com, .exe, .bat and .cmd (its other
    /// types count only for the planted-file check). Null (or a value without any of them) means that order.
    /// </param>
    public ExecutableResolver(
        Func<string, bool> fileExists,
        Func<string?> getPathVariable,
        Func<string, IReadOnlyList<string>> getAppPathCandidates,
        string windowsDirectory,
        string systemDirectory,
        Func<string> getCurrentDirectory,
        Func<string?>? getPathExtVariable = null)
    {
        _fileExists = fileExists;
        _getPathVariable = getPathVariable;
        _getAppPathCandidates = getAppPathCandidates;
        _windowsDirectory = windowsDirectory;
        _systemDirectory = systemDirectory;
        _getCurrentDirectory = getCurrentDirectory;
        _getPathExtVariable = getPathExtVariable ?? (() => null);
    }

    public ResolvedLaunchTarget Resolve(string executable, string? workingDirectory)
    {
        if (string.IsNullOrWhiteSpace(executable) || UriSchemeRegex().IsMatch(executable))
        {
            // URIs are handed to their protocol handler; the receiving program is not known.
            return new ResolvedLaunchTarget(executable, LaunchTargetKind.Other);
        }

        return IsBareName(executable)
            ? ResolveBareName(executable, workingDirectory)
            : ResolvePath(executable);
    }

    /// <summary>
    /// Reads the (Default) value of App Paths\&lt;keyName&gt; from HKCU, then HKLM.
    /// REG_EXPAND_SZ values are expanded by the registry API.
    /// </summary>
    public static IReadOnlyList<string> ReadAppPathsFromRegistry(string keyName)
    {
        var results = new List<string>();
        if (string.IsNullOrWhiteSpace(keyName) || keyName.IndexOfAny(['\\', '/']) >= 0)
        {
            return results;
        }

        foreach (var hive in new[] { Registry.CurrentUser, Registry.LocalMachine })
        {
            try
            {
                using var key = hive.OpenSubKey($@"{AppPathsKey}\{keyName}");
                if (key?.GetValue(null) is string value && !string.IsNullOrWhiteSpace(value))
                {
                    results.Add(value);
                }
            }
            catch (Exception ex) when (ex is SecurityException or UnauthorizedAccessException or IOException)
            {
                // Unreadable key: treat as not registered
            }
        }

        return results;
    }

    /// <summary>
    /// Classifies a resolved path by its extension.
    /// </summary>
    public static LaunchTargetKind Classify(string path)
    {
        var extension = Path.GetExtension(path);
        if (extension.Equals(".exe", StringComparison.OrdinalIgnoreCase) ||
            extension.Equals(".com", StringComparison.OrdinalIgnoreCase))
        {
            return LaunchTargetKind.NativeExecutable;
        }

        if (extension.Equals(".bat", StringComparison.OrdinalIgnoreCase) ||
            extension.Equals(".cmd", StringComparison.OrdinalIgnoreCase))
        {
            return LaunchTargetKind.BatchFile;
        }

        return LaunchTargetKind.Other;
    }

    private ResolvedLaunchTarget ResolvePath(string path)
    {
        // ShellExecute tries program extensions for a path without one ("C:\Tools\deploy" can run deploy.cmd).
        if (!Path.HasExtension(path) && !SafeFileExists(path))
        {
            foreach (var extension in ProgramExtensions)
            {
                var candidate = path + extension;
                if (SafeFileExists(candidate))
                {
                    return new ResolvedLaunchTarget(candidate, Classify(candidate));
                }
            }
        }

        return new ResolvedLaunchTarget(path, Classify(path));
    }

    private ResolvedLaunchTarget ResolveBareName(string name, string? workingDirectory)
    {
        var candidateNames = GetCandidateFileNames(name, plantedCheck: false);

        foreach (var directory in GetSearchDirectories())
        {
            var found = FindInDirectory(directory, candidateNames);
            if (found != null)
            {
                return new ResolvedLaunchTarget(found, Classify(found));
            }
        }

        var fromAppPaths = FindInAppPaths(name);
        if (fromAppPaths != null)
        {
            return new ResolvedLaunchTarget(fromAppPaths, Classify(fromAppPaths));
        }

        // Not found in a trusted location. ShellExecute would still look in the current directory
        // (and possibly the working directory); report a match there instead of letting it run.
        var plantedNames = GetCandidateFileNames(name, plantedCheck: true);
        foreach (var directory in GetUntrustedDirectories(workingDirectory))
        {
            var planted = FindInDirectory(directory, plantedNames);
            if (planted != null)
            {
                return new ResolvedLaunchTarget(name, LaunchTargetKind.Other, FoundOnlyInWorkingDirectory: planted);
            }
        }

        // Found nowhere: the caller must not hand the bare name to ShellExecute, whose own search starts in the
        // current directory and tries more file types (.lnk, .pif) than the check above
        return new ResolvedLaunchTarget(name, LaunchTargetKind.Other, NotFound: true);
    }

    /// <param name="plantedCheck">
    /// False for the search of the trusted places: only the program types (.com, .exe, .bat, .cmd) in PATHEXT's order,
    /// so a bare name never resolves to a script that Windows Script Host, an MMC snap-in or another handler runs
    /// (PATHEXT lists .vbs, .js, .wsf and .msc by default; ShellExecute's own search does not try them). True for the
    /// planted-file check: every PATHEXT type as well, since a planted file of any of them is worth reporting.
    /// </param>
    private IReadOnlyList<string> GetCandidateFileNames(string name, bool plantedCheck)
    {
        if (Path.HasExtension(name))
        {
            return [name];
        }

        var pathExt = GetPathExtExtensions();
        var programs = pathExt.Where(extension => ProgramExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase)).ToList();
        if (programs.Count == 0)
        {
            programs.AddRange(ProgramExtensions);
        }

        var extensions = plantedCheck
            ? programs.Concat(pathExt).Concat(ProgramExtensions).Distinct(StringComparer.OrdinalIgnoreCase)
            : programs;
        return extensions.Select(extension => name + extension).ToArray();
    }

    /// <summary>
    /// The usable PATHEXT entries in their order (as cmd.exe tries them), lower case; empty when PATHEXT is not set.
    /// An entry is usable when it is a dot followed by at least one character that is valid in a file name and is not
    /// a dot or a path separator.
    /// </summary>
    private IReadOnlyList<string> GetPathExtExtensions()
    {
        string? pathExt;
        try
        {
            pathExt = _getPathExtVariable();
        }
        catch (Exception)
        {
            pathExt = null;
        }

        return (pathExt ?? string.Empty)
            .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(IsUsableExtension)
            .Select(extension => extension.ToLowerInvariant()) // PATHEXT is upper case; paths read better in lower case
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static bool IsUsableExtension(string extension) =>
        extension.Length > 1 &&
        extension[0] == '.' &&
        extension.IndexOf('.', 1) < 0 &&
        extension.IndexOfAny(['\\', '/', ':']) < 0 &&
        extension.IndexOfAny(Path.GetInvalidFileNameChars()) < 0;

    private IEnumerable<string> GetSearchDirectories()
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var directory in new[] { _windowsDirectory, _systemDirectory })
        {
            if (IsUsableDirectory(directory) && seen.Add(NormalizeDirectory(directory)))
            {
                yield return directory;
            }
        }

        var pathVariable = _getPathVariable();
        if (string.IsNullOrEmpty(pathVariable))
        {
            yield break;
        }

        foreach (var rawEntry in pathVariable.Split(';'))
        {
            var entry = Environment.ExpandEnvironmentVariables(rawEntry.Trim().Trim('"').Trim());

            // Empty, '.', relative and drive-relative entries resolve against the current directory.
            if (!IsUsableDirectory(entry) || !seen.Add(NormalizeDirectory(entry)))
            {
                continue;
            }

            yield return entry;
        }
    }

    private IEnumerable<string> GetUntrustedDirectories(string? workingDirectory)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        string? currentDirectory;
        try
        {
            currentDirectory = _getCurrentDirectory();
        }
        catch (Exception)
        {
            currentDirectory = null;
        }

        foreach (var directory in new[] { currentDirectory, workingDirectory })
        {
            if (IsUsableDirectory(directory) && seen.Add(NormalizeDirectory(directory!)))
            {
                yield return directory!;
            }
        }
    }

    private string? FindInAppPaths(string name)
    {
        var keyName = Path.HasExtension(name) ? name : name + ".exe";

        IReadOnlyList<string> values;
        try
        {
            values = _getAppPathCandidates(keyName);
        }
        catch (Exception)
        {
            return null;
        }

        foreach (var rawValue in values)
        {
            var value = rawValue.Trim().Trim('"').Trim();
            if (value.Length == 0 || !IsFullyQualified(value))
            {
                continue;
            }

            if (SafeFileExists(value))
            {
                return value;
            }

            // The (Default) value may omit the .exe extension.
            if (!Path.HasExtension(value) && SafeFileExists(value + ".exe"))
            {
                return value + ".exe";
            }
        }

        return null;
    }

    private string? FindInDirectory(string directory, IReadOnlyList<string> candidateNames)
    {
        foreach (var candidateName in candidateNames)
        {
            string candidate;
            try
            {
                candidate = Path.Combine(directory, candidateName);
            }
            catch (ArgumentException)
            {
                continue;
            }

            if (SafeFileExists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    private bool SafeFileExists(string path)
    {
        try
        {
            return _fileExists(path);
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static bool IsBareName(string executable)
    {
        return executable.IndexOfAny(['\\', '/', ':']) < 0;
    }

    private static bool IsUsableDirectory(string? directory)
    {
        return !string.IsNullOrWhiteSpace(directory) && IsFullyQualified(directory);
    }

    private static bool IsFullyQualified(string path)
    {
        try
        {
            return Path.IsPathFullyQualified(path);
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private static string NormalizeDirectory(string directory)
    {
        return directory.TrimEnd('\\', '/');
    }

    // Same shape the validator uses: a scheme of two or more characters followed by ':'.
    [GeneratedRegex(@"^[a-zA-Z][a-zA-Z0-9+.-]+:")]
    private static partial Regex UriSchemeRegex();
}
