using WindowsAppManagerMcp.Infrastructure;
using WindowsAppManagerMcp.Models;

namespace WindowsAppManagerMcp.Services;

/// <summary>
/// The path file_dialog types into an Open or Save As dialog (roadmap M3.14), checked the way the captures check their
/// outputPath before anything touches the file system or the dialog.
/// </summary>
/// <remarks>
/// <para>Absolute and on a drive letter: a network path (\\server\share) makes the application, and the dialog itself while
/// it checks the name, open an SMB or WebDAV connection, which can leak the user's NTLM hash; a device path (\\?\, \\.\)
/// reaches raw devices. A drive letter the user mapped to a share is accepted, as for outputPath.</para>
/// <para>No wildcards, quotes or the other characters a file name cannot hold: a dialog takes "*.txt" as a filter and
/// "a" "b" as several files. No alternate data stream (a colon in the file name). No environment variable (%NAME%) that
/// expands: the dialog expands it, so the checks here would be of another path.</para>
/// <para>Open may name any existing file or folder (a folder makes the dialog show it, which a folder picker takes as the
/// answer): the server only types the path into the user's own dialog, which the application then reads with the user's
/// rights, as type_text into that box would. Save needs the folder to exist and the path not to be a folder; an existing
/// file needs overwrite true. These checks read only whether the path exists.</para>
/// </remarks>
public static class FileDialogPaths
{
    /// <summary>The longest path accepted.</summary>
    public const int MaxLength = 1024;

    private static readonly char[] Forbidden = ['*', '?', '"', '<', '>', '|'];

    /// <summary>The full path to type, or the failure: InvalidArguments for the path's form, FileExists for save.</summary>
    public static FileDialogPathCheck Check(string? path, FileDialogAction action, bool overwrite)
    {
        var given = path?.Trim() ?? string.Empty;
        if (given.Length == 0)
        {
            return Invalid("path is the file to open or save, as a full path such as C:\\Users\\me\\Documents\\report.txt.");
        }

        if (given.Length > MaxLength)
        {
            return Invalid($"path is {given.Length} characters long; at most {MaxLength} are accepted.");
        }

        if (given.IndexOfAny(Forbidden) >= 0 || given.Any(char.IsControl))
        {
            return Invalid("path holds a character a file name cannot have (* ? \" < > | or a control character); a dialog takes " +
                           "wildcards as a filter. Name one file.");
        }

        if (!string.Equals(Environment.ExpandEnvironmentVariables(given), given, StringComparison.Ordinal))
        {
            // The dialog expands %NAME% itself, so the path checked here would not be the one opened or saved (and an
            // existing file could pass the overwrite check under another name)
            return Invalid("path holds an environment variable (%NAME%) that the dialog would expand. Pass the expanded path.");
        }

        string full;
        try
        {
            if (!Path.IsPathFullyQualified(given) || !IsOnADriveLetter(given))
            {
                return Invalid("path must be a full path on a drive letter, such as C:\\Users\\me\\report.txt. Relative, network " +
                               "(\\\\server\\share) and device (\\\\?\\, \\\\.\\) paths are not accepted.");
            }

            full = Path.GetFullPath(given);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return Invalid($"path is not a valid path: {ex.Message}");
        }

        var name = Path.GetFileName(full);
        if (name.Contains(':', StringComparison.Ordinal))
        {
            return Invalid("path names an alternate data stream (a colon in the file name). Use a plain file name.");
        }

        if (action == FileDialogAction.Open)
        {
            return File.Exists(full) || Directory.Exists(full)
                ? FileDialogPathCheck.Ok(full)
                : Invalid($"'{full}' does not exist, so an Open dialog cannot open it. Check the path (list the folder first).");
        }

        if (name.Length == 0 || Directory.Exists(full))
        {
            return Invalid($"'{full}' is a folder; save needs a file name.");
        }

        var folder = Path.GetDirectoryName(full);
        if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder))
        {
            return Invalid($"The folder of '{full}' does not exist. The dialog does not create folders: save into an existing one.");
        }

        return !overwrite && File.Exists(full)
            ? new FileDialogPathCheck(null, nameof(UIElementErrorCode.FileExists),
                $"'{full}' already exists and overwrite is false. Nothing was typed. Pass overwrite true to replace it, or choose another name.")
            : FileDialogPathCheck.Ok(full);
    }

    private static bool IsOnADriveLetter(string path) =>
        path.Length >= 3 && char.IsAsciiLetter(path[0]) && path[1] == ':' && path[2] is '\\' or '/';

    private static FileDialogPathCheck Invalid(string error) => new(null, ToolErrorReporting.InvalidArgumentsErrorCode, error + " Nothing was done.");
}

/// <summary>The full path file_dialog types, or why it is refused.</summary>
public sealed record FileDialogPathCheck(string? FullPath, string? ErrorCode, string? Error)
{
    public static FileDialogPathCheck Ok(string fullPath) => new(fullPath, null, null);
}
