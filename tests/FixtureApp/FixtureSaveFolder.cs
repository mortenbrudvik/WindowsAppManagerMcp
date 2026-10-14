using System.IO;

namespace WindowsAppManagerMcp.FixtureApp;

/// <summary>
/// The one folder the fixture's Save As button writes into (roadmap M3.F4): the folder of its event log, which the harness
/// creates for each test under the temp folder and deletes afterwards. A path the dialog returns anywhere else, a subfolder
/// included, is refused and nothing is written, so neither a test nor a person at the dialog can make the fixture write
/// outside it; without an event log there is no such folder and the button writes nothing. The dialog itself writes no
/// file: it is shown without its test file (CreateTestFile false). The E2E project compiles this file too, so the rule is
/// tested without starting the fixture.
/// </summary>
public static class FixtureSaveFolder
{
    /// <summary>The folder the fixture may save into, or null when it writes no event log.</summary>
    public static string? For(FixtureOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return string.IsNullOrEmpty(options.EventLogPath) ? null : Path.GetDirectoryName(Path.GetFullPath(options.EventLogPath));
    }

    /// <summary>
    /// Whether <paramref name="path"/> names a file directly in <paramref name="folder"/>: a full path, whose parent is that
    /// folder, with a plain file name (no alternate data stream). <paramref name="canonical"/> gives an existing folder's path
    /// one spelling (the fixture expands 8.3 short names with GetLongPathName), since the harness and the dialog may spell
    /// the same folder differently.
    /// </summary>
    public static bool Allows(string? folder, string? path, Func<string, string> canonical)
    {
        ArgumentNullException.ThrowIfNull(canonical);
        if (string.IsNullOrEmpty(folder) || string.IsNullOrEmpty(path) || !Path.IsPathFullyQualified(path) ||
            !Path.IsPathFullyQualified(folder))
        {
            return false;
        }

        var full = Path.GetFullPath(path);
        var name = Path.GetFileName(full);
        var parent = Path.GetDirectoryName(full);
        if (string.IsNullOrEmpty(name) || name.Contains(':') || parent == null)
        {
            return false;
        }

        return string.Equals(
            Path.TrimEndingDirectorySeparator(canonical(parent)),
            Path.TrimEndingDirectorySeparator(canonical(Path.GetFullPath(folder))),
            StringComparison.OrdinalIgnoreCase);
    }
}
