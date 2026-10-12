using WindowsAppManagerMcp.Models;

namespace WindowsAppManagerMcp.Services;

/// <summary>How a window title shows a file or folder a launch opens (<see cref="LaunchTitles.Shows"/>).</summary>
public enum TitleShows
{
    /// <summary>The title does not show the name as a whole name.</summary>
    Nothing,

    /// <summary>
    /// The title shows the name with no path in front of it, so a file or folder of the same name in another folder
    /// would look the same.
    /// </summary>
    NameOnly,

    /// <summary>The title shows the launched full path.</summary>
    FullPath,

    /// <summary>The title shows the name after another path: another file or folder of that name.</summary>
    OtherPath
}

/// <summary>
/// Whether a window title shows what a launch opens, for reuseExisting (roadmap M3.13) and for the launch wait's
/// reused-window signal (roadmap M2.6). A name counts only as a whole name, and a name shown after a path counts only when
/// that path is the launched one: "C:\other\todo.txt - Notepad++" does not show C:\notes\todo.txt. A web page's title is
/// set by the page, so a host name in it is never taken as a sign of the launch.
/// </summary>
public static class LaunchTitles
{
    /// <summary>
    /// Whether a window title shows <paramref name="name"/> as a whole name, not as part of a longer one: on each side of
    /// an occurrence (case-insensitive) is the end of the title; or a character that cannot continue a name (anything but
    /// a letter, digit, '.', '-', '_' or whitespace: a path separator, '*', a bullet, '(', ':', ...); or whitespace that is not
    /// followed, on that side, by a letter or digit. So "todo.txt - Notepad", "*todo.txt - Notepad"
    /// and "C:\notes\todo.txt - Notepad++" show todo.txt; "old-todo.txt", "todo.txt.bak" and "old todo.txt" do not, nor
    /// do "MyApp2", "MyApp.Tests" or "MyApp Tests" show MyApp. Says nothing about a path in front of the name: see
    /// <see cref="Shows"/>.
    /// </summary>
    public static bool ShowsName(string title, string name)
    {
        ArgumentNullException.ThrowIfNull(title);
        return Occurrences(title, name).Any();
    }

    /// <summary>
    /// How <paramref name="title"/> shows <paramref name="hint"/>: <see cref="TitleShows.OtherPath"/> when any whole-name
    /// occurrence follows a path separator and the path there is not the hint's full path (or the full path is not
    /// known); else <see cref="TitleShows.FullPath"/> when an occurrence ends the full path; else
    /// <see cref="TitleShows.NameOnly"/> when the name is shown alone; else <see cref="TitleShows.Nothing"/>. The path is
    /// compared ignoring case, with '/' and '\' alike, and must start at the start of the title or after a character that
    /// cannot continue a path (a space, '[', '(', '*', ...).
    /// </summary>
    public static TitleShows Shows(string title, DocumentHint hint)
    {
        ArgumentNullException.ThrowIfNull(title);
        ArgumentNullException.ThrowIfNull(hint);

        var shows = TitleShows.Nothing;
        foreach (var at in Occurrences(title, hint.Name))
        {
            if (at == 0 || !IsSeparator(title[at - 1]))
            {
                if (shows == TitleShows.Nothing)
                    shows = TitleShows.NameOnly;
                continue;
            }

            if (hint.FullPath == null || !EndsWithPath(title, at + hint.Name.Length, hint.FullPath))
                return TitleShows.OtherPath;

            shows = TitleShows.FullPath;
        }

        return shows;
    }

    /// <summary>Where <paramref name="name"/> occurs in <paramref name="title"/> as a whole name.</summary>
    private static IEnumerable<int> Occurrences(string title, string name)
    {
        if (string.IsNullOrEmpty(name))
            yield break;

        for (var at = title.IndexOf(name, StringComparison.OrdinalIgnoreCase); at >= 0;
             at = at + 1 < title.Length ? title.IndexOf(name, at + 1, StringComparison.OrdinalIgnoreCase) : -1)
        {
            if (EndsName(title, at - 1, -1) && EndsName(title, at + name.Length, +1))
                yield return at;
        }
    }

    /// <summary>Whether the title's character at <paramref name="index"/> (beside a name, on the side <paramref name="step"/> points to) ends that name.</summary>
    private static bool EndsName(string title, int index, int step)
    {
        if (index < 0 || index >= title.Length)
            return true;

        var c = title[index];
        if (char.IsWhiteSpace(c))
        {
            var beyond = index + step;
            return beyond < 0 || beyond >= title.Length || !char.IsLetterOrDigit(title[beyond]);
        }

        return !(char.IsLetterOrDigit(c) || c is '.' or '-' or '_');
    }

    /// <summary>Whether the title's text that ends at <paramref name="end"/> is <paramref name="path"/>, as a whole path.</summary>
    private static bool EndsWithPath(string title, int end, string path)
    {
        var start = end - path.Length;
        if (path.Length == 0 || start < 0)
            return false;

        for (var i = 0; i < path.Length; i++)
        {
            char shown = title[start + i], launched = path[i];
            if (IsSeparator(shown) ? !IsSeparator(launched) : char.ToUpperInvariant(shown) != char.ToUpperInvariant(launched))
                return false;
        }

        if (start == 0)
            return true;

        var before = title[start - 1];
        return !(char.IsLetterOrDigit(before) || IsSeparator(before) || before is '.' or '-' or '_' or ':' or '~');
    }

    private static bool IsSeparator(char c) => c is '\\' or '/';
}
