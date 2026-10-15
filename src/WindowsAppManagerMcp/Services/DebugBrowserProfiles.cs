using WindowsAppManagerMcp.Models;

namespace WindowsAppManagerMcp.Services;

/// <summary>
/// The server's own profile folders for debug browsers (roadmap M5.4): named profiles kept between launches under
/// %LOCALAPPDATA%\WindowsAppManagerMcp\browser-profiles\&lt;name&gt;, and a new temporary profile per launch under
/// %TEMP%\WindowsAppManagerMcp\browser-debug\&lt;32 hex digits&gt;. Only a folder directly under one of these roots, with a
/// name of its kind, is the server's: close_debug_browser closes an untracked browser only when its command line names
/// such a folder, and only such a folder is ever deleted. The user's real browser profile is never one of them.
/// </summary>
/// <remarks>
/// Temporary profiles live under %TEMP% so that Windows' own cleanup removes what the server could not; a launch also
/// sweeps the ones a closed or crashed browser left (<see cref="SweepStaleTemporary"/>).
/// </remarks>
public sealed class DebugBrowserProfiles
{
    /// <summary>The longest profile name.</summary>
    public const int MaxNameLength = 64;

    /// <summary>A temporary profile younger than this is never swept: a browser may be starting with it (another server's).</summary>
    public static readonly TimeSpan SweepMinimumAge = TimeSpan.FromMinutes(10);

    private static readonly TimeSpan DeleteRetryInterval = TimeSpan.FromMilliseconds(200);

    /// <summary>Windows' reserved device names, which cannot name a folder.</summary>
    private static readonly HashSet<string> ReservedNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM0", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT0", "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9"
    };

    private readonly Func<DateTime> _utcNow;

    /// <param name="namedRoot">The folder that holds the named profiles.</param>
    /// <param name="temporaryRoot">The folder that holds the temporary profiles.</param>
    /// <param name="utcNow">The clock the sweep compares folder ages with; default the system clock.</param>
    public DebugBrowserProfiles(string namedRoot, string temporaryRoot, Func<DateTime>? utcNow = null)
    {
        NamedRoot = Normalize(namedRoot);
        TemporaryRoot = Normalize(temporaryRoot);
        _utcNow = utcNow ?? (() => DateTime.UtcNow);
    }

    /// <summary>The server's folders: %LOCALAPPDATA%\WindowsAppManagerMcp\browser-profiles and %TEMP%\WindowsAppManagerMcp\browser-debug.</summary>
    public static DebugBrowserProfiles FromEnvironment() => new(
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), LayoutStorageOptions.AppDataFolderName, "browser-profiles"),
        Path.Combine(Path.GetTempPath(), LayoutStorageOptions.AppDataFolderName, "browser-debug"));

    /// <summary>The folder that holds the named profiles.</summary>
    public string NamedRoot { get; }

    /// <summary>The folder that holds the temporary profiles.</summary>
    public string TemporaryRoot { get; }

    /// <summary>
    /// Why <paramref name="name"/> cannot name a profile, or null: 1 to <see cref="MaxNameLength"/> ASCII letters, digits,
    /// '-' and '_', starting with a letter or digit, and not a Windows device name (CON, NUL, COM1, ...). It becomes one
    /// folder name, so it cannot reach another folder.
    /// </summary>
    public static string? NameRefusal(string? name)
    {
        if (string.IsNullOrEmpty(name) || name.Length > MaxNameLength || !char.IsAsciiLetterOrDigit(name[0]) ||
            !name.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_'))
        {
            return $"profile must be 1 to {MaxNameLength} letters, digits, '-' or '_', starting with a letter or digit, but it is " +
                   $"'{Shown(name)}'.";
        }

        return ReservedNames.Contains(name)
            ? $"profile '{name}' is a Windows device name, which cannot name a folder; choose another name."
            : null;
    }

    /// <summary>The folder of the named profile <paramref name="name"/> (checked with <see cref="NameRefusal"/> first).</summary>
    public string NamedPath(string name)
    {
        if (NameRefusal(name) is { } refusal)
            throw new ArgumentException(refusal, nameof(name));

        return Path.Combine(NamedRoot, name);
    }

    /// <summary>A new temporary profile folder's path (not created).</summary>
    public string NewTemporaryPath() => Path.Combine(TemporaryRoot, Guid.NewGuid().ToString("N"));

    /// <summary>
    /// Whose <paramref name="userDataDir"/> is: <see cref="DebugProfileKind.Temporary"/> for a folder directly under
    /// <see cref="TemporaryRoot"/> named by 32 hex digits, <see cref="DebugProfileKind.Named"/> for one directly under
    /// <see cref="NamedRoot"/> with a valid profile name, <see cref="DebugProfileKind.Custom"/> for anything else (also a
    /// path that is not a full path).
    /// </summary>
    public DebugProfileKind KindOf(string? userDataDir)
    {
        if (string.IsNullOrWhiteSpace(userDataDir) || !Path.IsPathFullyQualified(userDataDir.Trim()))
            return DebugProfileKind.Custom;

        string path;
        try
        {
            path = Normalize(userDataDir);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return DebugProfileKind.Custom;
        }

        var parent = Path.GetDirectoryName(path);
        var name = Path.GetFileName(path);
        if (parent == null || string.IsNullOrEmpty(name))
            return DebugProfileKind.Custom;

        if (SamePath(parent, TemporaryRoot) && name.Length == 32 && name.All(char.IsAsciiHexDigit))
            return DebugProfileKind.Temporary;

        if (SamePath(parent, NamedRoot) && NameRefusal(name) == null)
            return DebugProfileKind.Named;

        return DebugProfileKind.Custom;
    }

    /// <summary>A full path without a trailing separator, for comparisons.</summary>
    public static string Normalize(string path) =>
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(path.Trim()));

    /// <summary>Whether two paths name the same folder, ignoring case and a trailing separator.</summary>
    public static bool SamePath(string? a, string? b)
    {
        if (a == null || b == null)
            return false;

        try
        {
            return string.Equals(Normalize(a), Normalize(b), StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    /// <summary>
    /// Deletes a profile folder of the server's own (<see cref="KindOf"/> Temporary or Named), trying again for up to
    /// <paramref name="budget"/> while the browser's processes release their files. Returns why it was not deleted, or
    /// null when it is gone (also when it never existed). A folder that is not the server's, or that is a link
    /// (junction or symbolic link) to another folder, is never deleted.
    /// </summary>
    public async Task<string?> DeleteAsync(string userDataDir, TimeSpan budget, CancellationToken cancellationToken = default)
    {
        if (KindOf(userDataDir) == DebugProfileKind.Custom)
            return $"'{userDataDir}' is not one of the server's own profile folders, so it is not deleted.";

        var path = Normalize(userDataDir);
        var deadline = _utcNow() + budget;
        while (true)
        {
            var failure = TryDelete(path);
            if (failure == null)
                return null;

            var left = deadline - _utcNow();
            if (left <= TimeSpan.Zero)
                return failure;

            await Task.Delay(left < DeleteRetryInterval ? left : DeleteRetryInterval, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Deletes the temporary profiles that a browser left behind (closed by the user, ended, or crashed, or a server that
    /// stopped), and returns how many: each folder under <see cref="TemporaryRoot"/> with a temporary profile's name, older
    /// than <see cref="SweepMinimumAge"/>, whose lockfile no browser holds (<see cref="ChromiumProfileLock"/>) and that
    /// <paramref name="keep"/> does not keep. Best effort: a folder that cannot be deleted now is left for the next sweep.
    /// </summary>
    public int SweepStaleTemporary(Func<string, bool>? keep = null)
    {
        IEnumerable<string> folders;
        try
        {
            if (!Directory.Exists(TemporaryRoot))
                return 0;

            folders = Directory.EnumerateDirectories(TemporaryRoot).ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return 0;
        }

        var swept = 0;
        foreach (var folder in folders)
        {
            try
            {
                if (KindOf(folder) != DebugProfileKind.Temporary || keep?.Invoke(folder) == true)
                    continue;

                if (_utcNow() - Directory.GetCreationTimeUtc(folder) < SweepMinimumAge)
                    continue;

                if (ChromiumProfileLock.HeldReason(folder) != null)
                    continue;

                if (TryDelete(Normalize(folder)) == null)
                    swept++;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
            {
                // Left for the next sweep
            }
        }

        return swept;
    }

    /// <summary>One attempt to delete <paramref name="path"/>; why it failed, or null.</summary>
    private static string? TryDelete(string path)
    {
        try
        {
            var info = new DirectoryInfo(path);
            if (!info.Exists)
                return null;

            if (info.Attributes.HasFlag(FileAttributes.ReparsePoint))
                return $"'{path}' is a link to another folder, so it is not deleted.";

            try
            {
                info.Delete(recursive: true);
            }
            catch (UnauthorizedAccessException)
            {
                // A read-only file stops the delete; the browser marks none, but a copied profile may have them
                ClearReadOnly(info);
                info.Delete(recursive: true);
            }

            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return ex.Message;
        }
    }

    /// <summary>Clears the read-only attribute of the files under <paramref name="folder"/>, without following links.</summary>
    private static void ClearReadOnly(DirectoryInfo folder)
    {
        var options = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            AttributesToSkip = FileAttributes.ReparsePoint,
            IgnoreInaccessible = true
        };
        foreach (var file in folder.EnumerateFiles("*", options))
        {
            if (file.Attributes.HasFlag(FileAttributes.ReadOnly))
                file.Attributes &= ~FileAttributes.ReadOnly;
        }
    }

    /// <summary>A name as an error shows it: on one line and at most 80 characters.</summary>
    private static string Shown(string? name)
    {
        var text = new string((name ?? string.Empty).Select(c => char.IsControl(c) ? ' ' : c).ToArray());
        return text.Length > 80 ? text[..79] + "…" : text;
    }
}
