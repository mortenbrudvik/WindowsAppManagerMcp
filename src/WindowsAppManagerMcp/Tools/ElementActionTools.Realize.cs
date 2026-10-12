using System.Diagnostics;
using WindowsAppManagerMcp.Infrastructure;
using WindowsAppManagerMcp.Models;
using WindowsAppManagerMcp.Services;

namespace WindowsAppManagerMcp.Tools;

// realize by criteria (roadmap M3.10): the item found through its list's ItemContainer pattern, or, with maxScrolls, by
// scrolling the window's one visible list page by page and searching it after each page. Every page is an element_action
// scroll through the action service, and the item is realized through it too, so each step passes the same checks (the
// input gate, the user's pause, the target policy and deny list, the host guard); the call is audited as element_action.
public partial class ElementActionTools
{
    /// <summary>The most pages realize scrolls with maxScrolls.</summary>
    public const int MaxScrollsLimit = 100;

    /// <summary>The list control types realize scrolls.</summary>
    private static readonly string[] ScrollableLists = ["List", "Tree", "DataGrid", "Table"];

    internal const string MaxScrollsDescription =
        "realize with element criteria only: when the item is found neither in the tree nor through its list's ItemContainer " +
        "pattern (a web or custom list), scroll the window's one visible list (List, Tree, DataGrid or Table) a page at a time, " +
        "at most this many pages (down to its end, then back up), searching it after each page, and realize the first match. " +
        "Every page is an element_action scroll with every check; the list stays where the item was found (or where the search " +
        "ended). Default: 0 (no scrolling); max 100. timeoutMs bounds the whole search (each step gets what is left, at least " +
        "a second): raise it for long lists.";

    /// <summary>Whether criteria can be searched for through a list's ItemContainer pattern (one whole property).</summary>
    private static bool FitsVirtualizedSearch(string? name, ElementNameMatch nameMatch, string? automationId) =>
        !string.IsNullOrEmpty(automationId) || (!string.IsNullOrEmpty(name) && nameMatch == ElementNameMatch.Exact);

    /// <summary>
    /// The scroll fallback: the window's one visible list scrolled a page at a time (down to its end, then up), searched below
    /// its ref after each page, until the criteria match or <paramref name="maxScrolls"/> pages or the timeout have passed.
    /// </summary>
    /// <param name="notFound">The first search's ElementNotFound, returned when there is no list to scroll.</param>
    private async Task<ElementActionResult> RealizeByScrollingAsync(
        ElementQuery query, ElementActionRequest request, int maxScrolls, Stopwatch stopwatch, ElementActionResult notFound,
        CancellationToken cancellationToken)
    {
        var timeout = request.Timeout ?? ElementActionService.DefaultTimeout;
        var actionName = ToolEnum<ElementActionKind>.Name(ElementActionKind.Realize);
        ElementActionResult Fail(string code, string error) =>
            new(false, actionName, WindowHandle: (long)query.WindowHandle, Error: error, ErrorCode: code,
                ElapsedMilliseconds: Math.Round(stopwatch.Elapsed.TotalMilliseconds, 2));

        var lists = await _search.FindAsync(new ElementQuery(
            WindowHandle: query.WindowHandle,
            ControlTypes: ScrollableLists,
            VisibleOnly: true,
            MaxResults: 5,
            Timeout: Remaining(timeout, stopwatch),
            // The window's own lists, not a dropdown's
            Scope: ElementSearchScope.Window), cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        if (!lists.Success || lists.Elements is not { Count: > 0 } found || found[0].Ref is not { } listRef)
        {
            return notFound with
            {
                Error = notFound.Error + " No visible list (List, Tree, DataGrid or Table) was found in the window to scroll, so " +
                        "maxScrolls did nothing."
            };
        }

        if (lists.MatchCount > 1)
        {
            var refs = string.Join(", ", found.Select(e => $"{e.Ref} {e.ControlType} '{e.Name}'"));
            return Fail(nameof(UIElementErrorCode.AmbiguousElement),
                $"The item was not found, and the window shows {lists.MatchCount} lists ({refs}), so it is not known which one to " +
                "scroll. Nothing was scrolled. Scroll the one you mean (element_action scroll with its ref) and search it " +
                "(find_ui_elements withinRef), or narrow the window.");
        }

        var listName = $"{found[0].ControlType}{(string.IsNullOrEmpty(found[0].Name) ? string.Empty : $" '{found[0].Name}'")} ({listRef})";
        var direction = ElementScrollDirection.Down;
        var turned = false;
        var pages = 0;
        for (var step = 0; pages < maxScrolls && step < maxScrolls + 2; step++)
        {
            if (stopwatch.Elapsed >= timeout)
            {
                return Fail(nameof(UIElementErrorCode.OperationTimedOut),
                    $"The timeout passed after {pages} pages of {listName} were searched; the item was not found. The list was left " +
                    "where the search ended. Raise timeoutMs, or search with find_ui_elements first.");
            }

            var scrolled = await _actions.ActAsync(new ElementActionRequest(
                listRef, ElementActionKind.Scroll, Direction: direction, Amount: ElementScrollAmount.Page,
                AllowHostTarget: request.AllowHostTarget, Timeout: Remaining(timeout, stopwatch)), cancellationToken).ConfigureAwait(false);
            if (!scrolled.Success)
            {
                return scrolled with
                {
                    Action = actionName,
                    Error = $"Scrolling {listName} to find the item stopped after {pages} pages: {scrolled.Error}"
                };
            }

            var moved = scrolled.Method is not (null or ElementActionMethod.None);
            if (moved)
            {
                pages++;
                var item = await FindOneAsync(query with
                {
                    WithinRef = listRef,
                    Scope = null,
                    SearchVirtualized = false,
                    Timeout = Remaining(timeout, stopwatch)
                }, ElementActionKind.Realize, cancellationToken).ConfigureAwait(false);
                if (item.Ref is { } itemRef)
                {
                    var realized = await _actions.ActAsync(request with { Ref = itemRef, Timeout = Remaining(timeout, stopwatch) },
                        cancellationToken).ConfigureAwait(false);
                    return realized with
                    {
                        Warnings = [.. realized.Warnings ?? [], $"Found after scrolling {listName} {pages} pages ({ToolEnum<ElementScrollDirection>.Name(direction)})."]
                    };
                }

                if (item.Failure is { ErrorCode: not nameof(UIElementErrorCode.ElementNotFound) } failure)
                {
                    return failure with { Error = $"{failure.Error} (after scrolling {listName} {pages} pages)" };
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

        return Fail(nameof(UIElementErrorCode.ElementNotFound),
            $"No element matches the criteria after {pages} pages of {listName} were searched" +
            (pages >= maxScrolls ? $" (maxScrolls {maxScrolls} reached)" : " (down to its end and back up to its start)") +
            ". The list was left where the search ended. Nothing was realized. Check the criteria with find_ui_elements.");
    }

    /// <summary>What is left of the timeout, at least a second (a search's shortest timeout): every page, search and the final
    /// realize get this, so the one timeoutMs bounds the whole search.</summary>
    private static TimeSpan Remaining(TimeSpan timeout, Stopwatch stopwatch)
    {
        var left = timeout - stopwatch.Elapsed;
        return left < TimeSpan.FromSeconds(1) ? TimeSpan.FromSeconds(1) : left;
    }
}
