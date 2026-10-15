using System.Text.RegularExpressions;
using WindowsAppManagerMcp.Models;
using WindowsAppManagerMcp.Services.Interfaces;

namespace WindowsAppManagerMcp.Services;

/// <summary>
/// Finds the CDP page target (tab) that a browser window is showing, based on the window title.
/// </summary>
/// <remarks>
/// A browser window's title is the active tab's title followed by browser decorations, for example
/// "Inbox - Google Chrome" or "Docs and 2 more pages - Personal - Microsoft Edge" (Edge puts a
/// U+200B zero-width space inside "Microsoft Edge"). A tab matches only when its whole title starts the window title and is followed by the end of
/// the title, a " - " separator, or Edge's " and N more pages" segment. Substrings, empty titles
/// and URL hosts never match. When several tabs match, the longest (most specific) title wins;
/// a tie between different tabs is reported as ambiguous instead of guessed.
/// </remarks>
public static class CdpTargetMatcher
{
    private static readonly string[] Separators = [" - ", " — ", " – "];

    private static readonly Regex EdgeMorePagesSegment = new(
        "^" + WindowTitles.MorePagesPattern + "(?: - |$)",
        RegexOptions.CultureInvariant);

    private const int MaxListedCandidates = 5;

    /// <summary>
    /// Matches a window title to exactly one tab.
    /// </summary>
    /// <returns>The matched tab, or CdpTargetNotFound / CdpTargetAmbiguous.</returns>
    public static CdpResult<CdpTarget> Match(string? windowTitle, IReadOnlyList<CdpTarget> targets)
    {
        var window = NormalizeTitle(windowTitle);
        if (window.Length == 0)
        {
            return CdpResult<CdpTarget>.Fail(
                BrowserErrorCode.CdpTargetNotFound,
                "The window has no title, so it cannot be matched to a CDP tab.");
        }

        if (targets.Count == 0)
        {
            return CdpResult<CdpTarget>.Fail(
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
            return CdpResult<CdpTarget>.Fail(
                BrowserErrorCode.CdpTargetNotFound,
                $"None of the {targets.Count} open tab(s) has the title shown by the window ('{window}'). " +
                "The window may belong to a different browser instance than the CDP port, or the tab has no title yet.");
        }

        var longest = matches.Max(m => m.Title.Length);
        var best = matches.Where(m => m.Title.Length == longest).ToList();

        if (best.Count > 1)
        {
            var listed = string.Join("; ", best
                .Take(MaxListedCandidates)
                .Select(m => $"targetId {m.Target.Id} ({m.Target.Url})"));

            return CdpResult<CdpTarget>.Fail(
                BrowserErrorCode.CdpTargetAmbiguous,
                $"{best.Count} tabs have the title '{best[0].Title}', so the tab shown by the window cannot be identified: {listed}. " +
                "To read one of them, pass one as targetId to get_browser_url or get_browser_content, with the same window (and the same cdpPort, if you passed one).");
        }

        return CdpResult<CdpTarget>.Ok(best[0].Target);
    }

    /// <summary>
    /// Normalizes a title for comparison: removes invisible format characters (such as the
    /// U+200B zero-width space in Edge window titles), collapses whitespace runs to one space, and trims.
    /// The window selectors use the same normalizer (<see cref="WindowTitles.Normalize"/>).
    /// </summary>
    public static string NormalizeTitle(string? title) => WindowTitles.Normalize(title);

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
