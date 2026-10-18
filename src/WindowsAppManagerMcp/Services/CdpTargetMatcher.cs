using System.Text.RegularExpressions;
using WindowsAppManagerMcp.Models;
using WindowsAppManagerMcp.Services.Interfaces;

namespace WindowsAppManagerMcp.Services;

/// <summary>
/// Finds the CDP page targets (tabs) whose title a browser window shows, based on the window title.
/// </summary>
/// <remarks>
/// <para>
/// A browser window's title is the active tab's title followed by browser decorations, for example
/// "Inbox - Google Chrome" or "Docs and 2 more pages - Personal - Microsoft Edge" (Edge puts a
/// U+200B zero-width space inside "Microsoft Edge"). A tab matches only when its whole title starts the window title and is followed by the end of
/// the title, a " - " separator, or Edge's " and N more pages" segment. Substrings, empty titles
/// and URL hosts never match.
/// </para>
/// <para>
/// The title alone picks a tab only when exactly one tab matches. When several do, the longest title is not taken: a page
/// sets its own title, so a background tab titled "Inbox - Google Chrome" matches the window "Inbox - Google Chrome" as
/// well as the shown tab "Inbox" does, and in Edge "Inbox - Personal" cannot be told from the legitimate "GitHub - Pull
/// requests" by titles at all. <see cref="CdpShownTab"/> then reads which of the candidates is shown, where the page
/// cannot lie about it; this class stays pure title logic.
/// </para>
/// </remarks>
public static class CdpTargetMatcher
{
    private static readonly string[] Separators = [" - ", " — ", " – "];

    private static readonly Regex EdgeMorePagesSegment = new(
        "^" + WindowTitles.MorePagesPattern + "(?: - |$)",
        RegexOptions.CultureInvariant);

    private const int MaxListedCandidates = 5;

    /// <summary>A title in an ambiguity message is cut to this many characters: a page sets its own title.</summary>
    private const int MaxQuotedTitleLength = 200;

    /// <summary>
    /// Matches a window title to exactly one tab, by the title alone: the only candidate
    /// (<see cref="FindCandidates"/>), else CdpTargetAmbiguous, also when one title is longer than the other.
    /// </summary>
    /// <returns>The matched tab, or CdpTargetNotFound / CdpTargetAmbiguous.</returns>
    public static CdpResult<CdpTarget> Match(string? windowTitle, IReadOnlyList<CdpTarget> targets)
    {
        var found = FindCandidates(windowTitle, targets);
        if (!found.Success)
            return found.FailAs<CdpTarget>();

        return found.Value.Count == 1
            ? CdpResult<CdpTarget>.Ok(found.Value[0])
            : Ambiguous(windowTitle, found.Value, "the title alone does not tell, and CDP reports no active tab.");
    }

    /// <summary>
    /// Every tab whose title the window shows, the longest titles first (in the order listed among the same length).
    /// </summary>
    /// <returns>One or more tabs, or CdpTargetNotFound (no title, no tabs, or no tab with the window's title).</returns>
    public static CdpResult<IReadOnlyList<CdpTarget>> FindCandidates(string? windowTitle, IReadOnlyList<CdpTarget> targets)
    {
        ArgumentNullException.ThrowIfNull(targets);
        var window = NormalizeTitle(windowTitle);
        if (window.Length == 0)
        {
            return CdpResult<IReadOnlyList<CdpTarget>>.Fail(
                BrowserErrorCode.CdpTargetNotFound,
                "The window has no title, so it cannot be matched to a CDP tab.");
        }

        if (targets.Count == 0)
        {
            return CdpResult<IReadOnlyList<CdpTarget>>.Fail(
                BrowserErrorCode.CdpTargetNotFound,
                "The CDP endpoint reports no open tabs (page targets).");
        }

        var matches = new List<(CdpTarget Target, string Title)>();
        foreach (var target in targets)
        {
            var title = NormalizeTitle(target.Title);
            if (title.Length > 0 && IsTitleShownByWindow(window, title))
            {
                matches.Add((target, title));
            }
        }

        if (matches.Count == 0)
        {
            return CdpResult<IReadOnlyList<CdpTarget>>.Fail(
                BrowserErrorCode.CdpTargetNotFound,
                $"None of the {targets.Count} open tab(s) has the title shown by the window ('{window}'). " +
                "The window may belong to a different browser instance than the CDP port, or the tab has no title yet.");
        }

        IReadOnlyList<CdpTarget> candidates = matches.OrderByDescending(m => m.Title.Length).Select(m => m.Target).ToList();
        return CdpResult<IReadOnlyList<CdpTarget>>.Ok(candidates);
    }

    /// <summary>
    /// CdpTargetAmbiguous for several <paramref name="candidates"/> of <paramref name="windowTitle"/>: why the tab the
    /// window shows cannot be told (<paramref name="why"/>, a sentence that starts in lower case), and their ids and URLs
    /// to pass as targetId.
    /// </summary>
    public static CdpResult<CdpTarget> Ambiguous(string? windowTitle, IReadOnlyList<CdpTarget> candidates, string why)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        var titles = candidates.Select(c => NormalizeTitle(c.Title)).Distinct(StringComparer.Ordinal).ToList();
        var which = titles.Count == 1
            ? $"the title '{Cut(titles[0])}'"
            : $"a title the window's title ('{Cut(NormalizeTitle(windowTitle))}') starts with";
        var listed = string.Join("; ", candidates
            .Take(MaxListedCandidates)
            .Select(c => $"targetId {c.Id} ({c.Url})"));
        var more = candidates.Count > MaxListedCandidates ? $" and {candidates.Count - MaxListedCandidates} more" : string.Empty;

        return CdpResult<CdpTarget>.Fail(
            BrowserErrorCode.CdpTargetAmbiguous,
            $"{candidates.Count} tabs have {which}, so the tab shown by the window cannot be identified: {why} " +
            $"The tabs: {listed}{more}. " +
            "To read one of them, pass one as targetId to get_browser_url or get_browser_content, with the same window (and the same cdpPort, if you passed one).");
    }

    /// <summary>
    /// Normalizes a title for comparison: removes invisible format characters (such as the
    /// U+200B zero-width space in Edge window titles), collapses whitespace runs to one space, and trims.
    /// The window selectors use the same normalizer (<see cref="WindowTitles.Normalize"/>).
    /// </summary>
    public static string NormalizeTitle(string? title) => WindowTitles.Normalize(title);

    private static string Cut(string title) =>
        title.Length > MaxQuotedTitleLength ? title[..(MaxQuotedTitleLength - 3)] + "..." : title;

    private static bool IsTitleShownByWindow(string window, string tabTitle)
    {
        if (!window.StartsWith(tabTitle, StringComparison.Ordinal))
            return false;

        var rest = window.AsSpan(tabTitle.Length);
        if (rest.IsEmpty)
            return true;

        foreach (var separator in Separators)
        {
            if (rest.StartsWith(separator, StringComparison.Ordinal))
                return true;
        }

        return EdgeMorePagesSegment.IsMatch(window[tabTitle.Length..]);
    }
}
