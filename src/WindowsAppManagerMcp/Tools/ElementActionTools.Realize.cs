using System.Diagnostics;
using System.Globalization;
using WindowsAppManagerMcp.Infrastructure;
using WindowsAppManagerMcp.Models;
using WindowsAppManagerMcp.Services;

namespace WindowsAppManagerMcp.Tools;

// realize by criteria (roadmap M3.10): the item found through its list's ItemContainer pattern, or, with maxScrolls, by
// scrolling the window's one visible list page by page and searching it after each page. Every page is an element_action
// scroll through the action service, and the item is realized through it too, so each step passes the same checks (the
// input gate, the user's pause, the target policy and deny list, the host guard); the call is audited as element_action. A
// search that ends without the item puts the list back where it was, through one more such scroll (roadmap M3.F19).
public partial class ElementActionTools
{
    /// <summary>The most pages realize scrolls with maxScrolls.</summary>
    public const int MaxScrollsLimit = 100;

    /// <summary>The list control types realize scrolls.</summary>
    private static readonly string[] ScrollableLists = ["List", "Tree", "DataGrid", "Table"];

    /// <summary>How many of the window's visible lists the scroll fallback reads to choose the one to scroll.</summary>
    private const int MaxListsRead = 20;

    internal const string MaxScrollsDescription =
        "realize with element criteria only: when the item is found neither in the tree nor through its list's ItemContainer " +
        "pattern (a web or custom list), scroll the window's one visible list (List, Tree, DataGrid or Table; of several, the " +
        "one that scrolls itself; or listRef) a page at a time, " +
        "at most this many pages (down to its end, then back up), searching it after each page, and realize the item once " +
        "exactly one matches (two: AmbiguousElement). " +
        "Every page is an element_action scroll with every check; the list stays where the item was found, or is put back " +
        "where it was. Default: 0 (no scrolling); more than 100 is cut to 100. timeoutMs bounds the whole search (each step gets what is left, " +
        "at least a second): raise it for long lists.";

    internal const string ListRefDescription =
        "realize with maxScrolls: the list to scroll when the window has several that scroll (a ref from find_ui_elements' " +
        "virtualized.containers).";

    /// <summary>Whether criteria can be searched for through a list's ItemContainer pattern (one whole property).</summary>
    private static bool FitsVirtualizedSearch(string? name, ElementNameMatch nameMatch, string? automationId) =>
        !string.IsNullOrEmpty(automationId) || (!string.IsNullOrEmpty(name) && nameMatch == ElementNameMatch.Exact);

    /// <summary>
    /// The scroll fallback: the list (<paramref name="namedList"/>, or the window's one visible list, or of several the one with
    /// a Scroll pattern of its own) scrolled a page at a time (down to its end, then up), searched below its ref after each
    /// page, until the criteria match or <paramref name="maxScrolls"/> pages or the timeout have passed. When they pass, or a
    /// page's search fails other than with several matches (AmbiguousElement, which leaves the list where they are), the list
    /// is put back where it was before the first page (roadmap M3.F19).
    /// </summary>
    /// <param name="realizeRequest">The realize request for the item's ref, once it is found.</param>
    /// <param name="timeout">The whole search's time (timeoutMs).</param>
    /// <param name="allowHostTarget">allowHostTarget, for each page's scroll.</param>
    /// <param name="namedList">listRef: the list the agent named, used without looking for lists (roadmap M3.F18).</param>
    /// <param name="notFound">The first search's ElementNotFound, returned when there is no list to scroll.</param>
    private async Task<ElementActionResult> RealizeByScrollingAsync(
        ElementQuery query, Func<string, ElementActionRequest> realizeRequest, TimeSpan timeout, bool allowHostTarget, int maxScrolls,
        string? namedList, Stopwatch stopwatch, ElementActionResult notFound, CancellationToken cancellationToken)
    {
        var actionName = ToolEnum<ElementActionKind>.Name(ElementActionKind.Realize);
        ElementActionResult Fail(string code, string error) =>
            new(false, actionName, WindowHandle: (long)query.WindowHandle, Error: error, ErrorCode: code,
                ElapsedMilliseconds: Math.Round(stopwatch.Elapsed.TotalMilliseconds, 2));

        string listRef;
        string listName;
        if (namedList != null)
        {
            listRef = namedList;
            listName = $"list {namedList}";
        }
        else
        {
            var lists = await _search.FindAsync(new ElementQuery(
                WindowHandle: query.WindowHandle,
                ControlTypes: ScrollableLists,
                VisibleOnly: true,
                MaxResults: MaxListsRead,
                Timeout: Remaining(timeout, stopwatch),
                // The window's own lists, not a dropdown's; its open popups' too only when the criteria's scope names them
                // (roadmap M3.F19), not by the default that follows the app being in front
                Scope: query.Scope == ElementSearchScope.WindowAndPopups ? ElementSearchScope.WindowAndPopups : ElementSearchScope.Window)
            {
                IncludeHostPopups = query.IncludeHostPopups
            }, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            if (!lists.Success || lists.Elements is not { Count: > 0 } found || found[0].Ref == null)
            {
                return notFound with
                {
                    Error = notFound.Error + " No visible list (List, Tree, DataGrid or Table) was found in the window to scroll, so " +
                            "maxScrolls did nothing."
                };
            }

            if (ListToScroll(lists.MatchCount, found) is not { Ref: { } chosenRef } chosen)
            {
                var refs = string.Join(", ", found.Select(e => $"{e.Ref} {e.ControlType} '{e.Name}'"));
                return Fail(nameof(UIElementErrorCode.AmbiguousElement),
                    $"The item was not found, and the window shows {lists.MatchCount} lists ({refs}), more than one of which may " +
                    "scroll, so it is not known which one to scroll. Nothing was scrolled. Pass listRef with the list you mean " +
                    "(find_ui_elements' virtualized.containers lists them), or narrow the window.");
            }

            listRef = chosenRef;
            listName = $"{chosen.ControlType}{(string.IsNullOrEmpty(chosen.Name) ? string.Empty : $" '{chosen.Name}'")} ({listRef})";
        }

        // A list in a popup is an element of the popup's own window, so each page is searched there
        var listWindow = _registry.Lookup(listRef) is { Success: true, WindowHandle: var registered } && registered != nint.Zero
            ? registered
            : query.WindowHandle;
        var direction = ElementScrollDirection.Down;
        var turned = false;
        var pages = 0;
        double? start = null;
        for (var step = 0; pages < maxScrolls && step < maxScrolls + 2; step++)
        {
            if (stopwatch.Elapsed >= timeout)
            {
                var putBack = await PutBackAsync(listRef, start, pages, allowHostTarget, timeout, stopwatch, cancellationToken)
                    .ConfigureAwait(false);
                return Fail(nameof(UIElementErrorCode.OperationTimedOut),
                    $"The timeout passed after {pages} pages of {listName} were searched; the item was not found. {putBack} Raise " +
                    "timeoutMs, or search with find_ui_elements first.");
            }

            var scrolled = await _actions.ActAsync(new ElementActionRequest(
                listRef, ElementActionKind.Scroll, Direction: direction, Amount: ElementScrollAmount.Page,
                AllowHostTarget: allowHostTarget, Timeout: Remaining(timeout, stopwatch)), cancellationToken).ConfigureAwait(false);
            if (!scrolled.Success)
            {
                return scrolled with
                {
                    Action = actionName,
                    Error = $"Scrolling {listName} to find the item stopped after {pages} pages: {scrolled.Error}"
                };
            }

            if (step == 0)
            {
                // Where the list was: the first page's state before it (the list's own position, or its scrollable ancestor's)
                start = scrolled.Before?.VerticalScrollPercent;
            }

            var moved = scrolled.Method is not (null or ElementActionMethod.None);
            if (moved)
            {
                pages++;
                var item = await FindOneAsync(query with
                {
                    WindowHandle = listWindow,
                    WithinRef = listRef,
                    Scope = null,
                    SearchVirtualized = false,
                    Timeout = Remaining(timeout, stopwatch)
                }, ElementActionKind.Realize, cancellationToken).ConfigureAwait(false);
                if (item.Ref is { } itemRef)
                {
                    var realized = await _actions.ActAsync(realizeRequest(itemRef) with { Timeout = Remaining(timeout, stopwatch) },
                        cancellationToken).ConfigureAwait(false);
                    return realized with
                    {
                        Warnings = [.. realized.Warnings ?? [], $"Found after scrolling {listName} {pages} pages ({ToolEnum<ElementScrollDirection>.Name(direction)})."]
                    };
                }

                if (item.Failure is { ErrorCode: not nameof(UIElementErrorCode.ElementNotFound) } failure)
                {
                    var after = $"{failure.Error} (after scrolling {listName} {pages} pages)";
                    if (failure.ErrorCode == nameof(UIElementErrorCode.AmbiguousElement))
                    {
                        // Several items match on this page: the list stays where they are, so the refs the error gives stay on
                        // screen
                        return failure with { Error = after };
                    }

                    // The page's search ended the search without the item (it did not finish, or the policy refuses its only
                    // matches): the list goes back where it was, as at the end of the search
                    var putBack = await PutBackAsync(listRef, start, pages, allowHostTarget, timeout, stopwatch, cancellationToken)
                        .ConfigureAwait(false);
                    return failure with { Error = $"{after}. {putBack}" };
                }
            }

            if (scrolled.Scroll?.AtEnd ?? !moved)
            {
                if (turned)
                {
                    break;
                }

                turned = true;
                direction = ElementScrollDirection.Up;
            }
        }

        var restored = await PutBackAsync(listRef, start, pages, allowHostTarget, timeout, stopwatch, cancellationToken).ConfigureAwait(false);
        return Fail(nameof(UIElementErrorCode.ElementNotFound),
            $"No element matches the criteria after {pages} pages of {listName} were searched" +
            (pages >= maxScrolls ? $" (maxScrolls {maxScrolls} reached)" : " (down to its end and back up to its start)") +
            $". {restored} Nothing was realized. Check the criteria with find_ui_elements.");
    }

    /// <summary>
    /// Puts the list back where it was before a search that did not find the item (roadmap M3.F19): an element_action scroll
    /// of the list to its vertical position before the first page (the Scroll pattern's SetScrollPercent), through the action
    /// service with every check, as each page was. The sentence that says so, or why the list stays where the search ended.
    /// </summary>
    /// <param name="start">The list's vertical position before the first page, or null when it was not read.</param>
    private async Task<string> PutBackAsync(
        string listRef, double? start, int pages, bool allowHostTarget, TimeSpan timeout, Stopwatch stopwatch,
        CancellationToken cancellationToken)
    {
        if (pages == 0)
        {
            return "The list did not move.";
        }

        if (start is not { } percent)
        {
            return "The list was left where the search ended: its position before the search was not read (it reported none).";
        }

        var at = $"{Percent(percent)} down";
        var restored = await _actions.ActAsync(new ElementActionRequest(
            listRef, ElementActionKind.Scroll, Direction: ElementScrollDirection.Up, AllowHostTarget: allowHostTarget,
            Timeout: Remaining(timeout, stopwatch), ScrollPercent: percent), cancellationToken).ConfigureAwait(false);
        if (!restored.Success)
        {
            return $"The list could not be put back where it was ({at}), so it was left where the search ended: {restored.Error}";
        }

        // A provider that scrolls by items may stop near the position rather than on it
        return restored.OutcomeVerified == false && restored.Scroll?.VerticalPercent is { } now
            ? $"The list was scrolled back toward where it was ({at}), but it is {Percent(now)} down now."
            : $"The list was put back where it was ({at}).";
    }

    private static string Percent(double percent) => percent.ToString("0.##", CultureInfo.InvariantCulture) + "%";

    /// <summary>
    /// The list to scroll of the visible lists <paramref name="found"/> (<paramref name="matchCount"/> in all), or null when it is
    /// not known: the only one, or of several the only one with a Scroll pattern of its own (roadmap M3.F18: a web page's
    /// navigation or footer list scrolls with the page, not by itself). When the search returned only some of the lists, one
    /// it left out may scroll too, so there is no choice.
    /// </summary>
    private static FoundElement? ListToScroll(int matchCount, IReadOnlyList<FoundElement> found)
    {
        if (matchCount == 1)
        {
            return found[0];
        }

        if (matchCount > found.Count)
        {
            return null;
        }

        var scrolling = found.Where(e => e.Patterns.Contains("Scroll", StringComparer.Ordinal)).Take(2).ToList();
        return scrolling.Count == 1 ? scrolling[0] : null;
    }

    /// <summary>What is left of the timeout, at least a second (a search's shortest timeout): every page, search and the final
    /// realize get this, so the one timeoutMs bounds the whole search.</summary>
    private static TimeSpan Remaining(TimeSpan timeout, Stopwatch stopwatch)
    {
        var left = timeout - stopwatch.Elapsed;
        return left < TimeSpan.FromSeconds(1) ? TimeSpan.FromSeconds(1) : left;
    }
}
