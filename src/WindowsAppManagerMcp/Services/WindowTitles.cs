using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace WindowsAppManagerMcp.Services;

/// <summary>
/// Browser-aware window title comparison, shared by the window selectors (roadmap M2.4), find_windows and layout matching,
/// and by the CDP tab matcher (roadmap M0.7, <see cref="CdpTargetMatcher"/>). Window titles carry characters nobody types:
/// Edge writes "Microsoft​ Edge" with a U+200B zero-width space, and adds " and N more pages" after the active tab's
/// title when a window has several tabs ("Docs and 2 more pages - Personal - Microsoft Edge"). Both would make a title
/// filter miss the window it was written for.
/// </summary>
public static class WindowTitles
{
    /// <summary>
    /// Edge's " and N more pages" segment after the active tab's title (English only: Edge translates it). The CDP matcher
    /// anchors it after a tab title; <see cref="ForMatching"/> removes it before " - " or the end of the title.
    /// </summary>
    public const string MorePagesPattern = @" and \d+ more pages?";

    private static readonly Regex MorePagesSegment = new(
        MorePagesPattern + "(?= - |$)",
        RegexOptions.CultureInvariant);

    /// <summary>
    /// The title as it reads: invisible format characters (Unicode category Cf, such as the U+200B zero-width space in
    /// Edge window titles, and bidirectional marks) removed, whitespace runs collapsed to one space, and trimmed. Empty
    /// for null.
    /// </summary>
    public static string Normalize(string? title)
    {
        if (string.IsNullOrEmpty(title))
            return string.Empty;

        var builder = new StringBuilder(title.Length);
        var pendingSpace = false;

        foreach (var c in title)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.Format)
                continue;

            if (char.IsWhiteSpace(c))
            {
                pendingSpace = builder.Length > 0;
                continue;
            }

            if (pendingSpace)
            {
                builder.Append(' ');
                pendingSpace = false;
            }

            builder.Append(c);
        }

        return builder.ToString();
    }

    /// <summary>
    /// <see cref="Normalize"/>d, and without Edge's " and N more pages" segment, so that the title of a window with several
    /// tabs reads like the title of the same window with one: "Docs - Personal - Microsoft Edge".
    /// </summary>
    public static string ForMatching(string? title)
    {
        var normalized = Normalize(title);
        return normalized.Contains(" more page", StringComparison.Ordinal)
            ? MorePagesSegment.Replace(normalized, string.Empty, 1)
            : normalized;
    }

    /// <summary>
    /// Whether the window title contains <paramref name="part"/>, ignoring case, invisible format characters and
    /// whitespace runs, with or without Edge's " and N more pages" segment. An empty part matches every title.
    /// </summary>
    public static bool Contains(string? title, string? part)
    {
        var wanted = Normalize(part);
        if (wanted.Length == 0)
            return true;

        var normalized = Normalize(title);
        return normalized.Contains(wanted, StringComparison.OrdinalIgnoreCase) ||
               ForMatching(normalized).Contains(wanted, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Whether the whole window title is <paramref name="expected"/>, ignoring case, invisible format characters and
    /// whitespace runs, with or without Edge's " and N more pages" segment.
    /// </summary>
    public static bool AreEqual(string? title, string? expected)
    {
        var wanted = ForMatching(expected);
        var normalized = Normalize(title);
        return string.Equals(normalized, wanted, StringComparison.OrdinalIgnoreCase) ||
               string.Equals(ForMatching(normalized), wanted, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Whether <paramref name="pattern"/> matches the window title, read as <see cref="Normalize"/> gives it or without
    /// Edge's " and N more pages" segment.
    /// </summary>
    /// <exception cref="RegexMatchTimeoutException">The pattern took longer than its timeout on this title.</exception>
    public static bool Matches(string? title, Regex pattern)
    {
        ArgumentNullException.ThrowIfNull(pattern);

        var normalized = Normalize(title);
        if (pattern.IsMatch(normalized))
            return true;

        var forMatching = ForMatching(normalized);
        return !string.Equals(forMatching, normalized, StringComparison.Ordinal) && pattern.IsMatch(forMatching);
    }

    /// <summary>The longest a titleRegex may run on one title (roadmap M2.4): a pattern that backtracks badly fails instead of hanging the call.</summary>
    public static readonly TimeSpan RegexTimeout = TimeSpan.FromMilliseconds(100);

    /// <summary>
    /// A titleRegex compiled for matching: case-insensitive, culture-invariant, with <see cref="RegexTimeout"/>. Null with an
    /// error for a pattern that is not a valid .NET regular expression.
    /// </summary>
    public static Regex? CompileTitleRegex(string pattern, out string? error)
    {
        ArgumentNullException.ThrowIfNull(pattern);

        try
        {
            error = null;
            return new Regex(pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, RegexTimeout);
        }
        catch (ArgumentException ex)
        {
            error = $"titleRegex '{pattern}' is not a valid .NET regular expression: {ex.Message}";
            return null;
        }
    }
}
