using WindowsAppManagerMcp.Models;
using WindowsAppManagerMcp.Services.Interfaces;

namespace WindowsAppManagerMcp.Services;

// searchVirtualized (roadmap M3.10): the items of the window's lists that are not realized yet, found through the lists'
// ItemContainer pattern. Only reads: realizing an item and scrolling it into view are element_action realize's, behind its
// guards (the input gate, pause, target policy, host guard) and audited.
public sealed partial class ElementSearchService
{
    /// <summary>The most lists searchVirtualized searches through their ItemContainer pattern in one call.</summary>
    public const int MaxVirtualizedContainers = 8;

    /// <summary>
    /// The most FindItemByProperty calls per list: each returns the next matching item (one cross-process call each), so this
    /// bounds a list whose items keep matching while they are skipped (already found, or not fitting the other criteria).
    /// </summary>
    public const int MaxVirtualizedProbes = 200;

    /// <summary>The list control types searchVirtualized looks for.</summary>
    private static readonly string[] ListControlTypes = ["List", "Tree", "DataGrid", "Table"];

    /// <summary>
    /// Why <paramref name="query"/>'s searchVirtualized cannot run, or null: it needs automationId, or name with nameMatch
    /// exact (FindItemByProperty compares one property, whole), and a window (it searches one window's lists).
    /// </summary>
    public static string? VirtualizedArgumentError(ElementQuery query)
    {
        ArgumentNullException.ThrowIfNull(query);
        if (!query.SearchVirtualized)
        {
            return null;
        }

        if (string.IsNullOrEmpty(query.AutomationId) && (string.IsNullOrEmpty(query.Name) || query.NameMatch != ElementNameMatch.Exact))
        {
            return "searchVirtualized asks the lists for an item by one whole property: give automationId, or name with nameMatch " +
                   "exact (not contains or regex).";
        }

        return query.WindowHandle == nint.Zero
            ? "searchVirtualized searches the lists of one window: pass handle, window or withinRef (not scope desktop alone)."
            : null;
    }

    /// <summary>
    /// The lists of <paramref name="root"/> and the items they found that the tree search did not, on the worker thread. Runs
    /// only after a tree search of the named window that finished and left room among maxResults.
    /// </summary>
    /// <param name="rootIsWithin">The root is withinRef's element: it is the list itself when it is one.</param>
    /// <param name="total">What the tree search found: its kept matches are not reported again.</param>
    private VirtualizedLookup SearchVirtualized(
        ElementCriteria criteria, IUiaNode root, bool rootIsWithin, SearchTotal total, int maxResults, CancellationToken token)
    {
        if (total.Stop != ElementSearchStop.None)
        {
            return VirtualizedLookup.Skipped("The tree search did not finish, so the lists were not searched for items that are not realized.");
        }

        var room = maxResults - total.Matches.Count;
        if (room <= 0 || total.MatchCount > total.Matches.Count)
        {
            return VirtualizedLookup.Skipped("maxResults matches were found in the tree already, so the lists were not searched.");
        }

        List<UiaElement> containers;
        try
        {
            containers = FindContainers(root, rootIsWithin, token);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return VirtualizedLookup.Skipped($"The lists could not be looked for: {ex.Message}");
        }

        if (containers.Count == 0)
        {
            return VirtualizedLookup.Skipped(
                $"No list ({string.Join(", ", ListControlTypes)}) was found {(rootIsWithin ? "at or below withinRef's element" : "in the window")}.");
        }

        if (!containers.Any(c => (c.Patterns & UiaPatterns.ItemContainer) != 0))
        {
            return new VirtualizedLookup([], false, containers,
                "No list here supports the ItemContainer pattern, so items that are not realized cannot be asked for: scroll the " +
                "list (element_action realize with maxScrolls, or element_action scroll and find_ui_elements withinRef).");
        }

        // The automation id names an item best; else its whole name. The other criteria are tested here on what was read
        var (property, value) = !string.IsNullOrEmpty(criteria.AutomationId)
            ? (UiaProperty.AutomationId, criteria.AutomationId)
            : (UiaProperty.Name, criteria.Name!);
        var (condition, filter) = (criteria with { VisibleOnly = false }).Compile(controlTypesOnServer: true);
        var evaluate = UiaConditionEvaluator.CanEvaluate(condition);
        var known = total.Matches.Select(m => m.Element.RuntimeId).OfType<IReadOnlyList<int>>().ToList();
        var items = new List<UiaElement>();
        var more = false;
        string? note = null;
        foreach (var container in containers.Where(c => (c.Patterns & UiaPatterns.ItemContainer) != 0))
        {
            if (token.IsCancellationRequested)
            {
                note = "The timeout passed before every list was searched.";
                break;
            }

            IUiaItemContainerPattern? pattern;
            try
            {
                pattern = _tree.GetPattern<IUiaItemContainerPattern>(container.Node);
            }
            catch (Exception ex) when (ex is not (TimeoutException or OperationCanceledException))
            {
                continue;
            }

            IUiaNode? after = null;
            for (var probe = 0; pattern != null && probe < MaxVirtualizedProbes; probe++)
            {
                if (token.IsCancellationRequested)
                {
                    note = "The timeout passed before every list was searched.";
                    break;
                }

                IUiaNode? item;
                UiaElement element;
                try
                {
                    item = pattern.FindItemByProperty(after, property, value);
                }
                catch (TimeoutException)
                {
                    note = "A list's provider did not answer in time; the items found until then are returned.";
                    break;
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // This list's provider failed (or the item to start after went away): the next list is searched
                    break;
                }

                if (item == null)
                {
                    break;
                }

                after = item;
                try
                {
                    element = _tree.ReadElement(item);
                }
                catch (TimeoutException)
                {
                    note = "A list's provider did not answer in time; the items found until then are returned.";
                    break;
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // A placeholder whose properties cannot be read, or an item that went away: the next one is asked for
                    continue;
                }

                if ((evaluate && !UiaConditionEvaluator.Matches(condition, element)) || (filter != null && !filter(element)))
                {
                    continue;
                }

                if (element.RuntimeId is { } id)
                {
                    if (known.Any(k => k.SequenceEqual(id)))
                    {
                        continue;
                    }

                    known.Add(id);
                }

                if (items.Count >= room)
                {
                    more = true;
                    break;
                }

                items.Add(element);
            }

            if (more || note != null)
            {
                break;
            }
        }

        return new VirtualizedLookup(items, more, containers, note);
    }

    /// <summary>
    /// The lists searchVirtualized searches: withinRef's element when it is a list (or supports ItemContainer), else the
    /// List, Tree, DataGrid and Table elements below the root, those with the ItemContainer pattern first, at most
    /// <see cref="MaxVirtualizedContainers"/>.
    /// </summary>
    private List<UiaElement> FindContainers(IUiaNode root, bool rootIsWithin, CancellationToken token)
    {
        if (rootIsWithin)
        {
            var self = _tree.ReadElement(root);
            if ((self.Patterns & UiaPatterns.ItemContainer) != 0 || ListControlTypes.Contains(self.Properties.ControlType))
            {
                return [self];
            }
        }

        var condition = UiaCondition.Or(ListControlTypes.Select(t => UiaCondition.PropertyEquals(UiaProperty.ControlType, t)).ToArray());
        var outcome = new ElementSearch(_tree, condition, filter: null, keep: MaxVirtualizedContainers * 4, maxCandidates: 200).Run(root, token);
        return outcome.Matches
            .OrderBy(c => (c.Patterns & UiaPatterns.ItemContainer) != 0 ? 0 : 1)
            .Take(MaxVirtualizedContainers)
            .ToList();
    }

    /// <summary>The result with what searchVirtualized found: its items after the tree's matches, and the lists with refs.</summary>
    private FindElementsResult WithVirtualized(
        FindElementsResult result, VirtualizedLookup lookup, ElementQuery query, ElementCriteria criteria, long generation,
        CancellationToken token)
    {
        var window = query.WindowHandle;
        var mayRead = true;
        var items = new List<FoundElement>(lookup.Items.Count);
        foreach (var item in lookup.Items)
        {
            string? itemRef = null;
            try
            {
                // Not proven unique: the provider compared one property, and the list may hold look-alikes further on
                itemRef = _registry.Register(window, item, new ElementOrigin(generation, criteria.Identity, Unique: null));
            }
            catch (Exception)
            {
                // Returned without a ref
            }

            var found = Minimal(item, itemRef) with { Virtualized = true };
            if (mayRead && query.Detail != ElementDetail.Minimal && !token.IsCancellationRequested)
            {
                try
                {
                    found = Read(found, item, query.Detail);
                }
                catch (TimeoutException)
                {
                    mayRead = false;
                }
            }

            items.Add(found);
        }

        var containers = lookup.Containers.Select(c => new VirtualizedContainer(
            TryRegister(window, c, generation),
            c.Properties.ControlType,
            string.IsNullOrEmpty(c.Properties.Name) ? null : c.Properties.Name,
            ItemContainer: (c.Patterns & UiaPatterns.ItemContainer) != 0,
            Scrollable: (c.Patterns & UiaPatterns.Scroll) != 0)).ToList();
        return result with
        {
            MatchCount = result.MatchCount + items.Count + (lookup.More ? 1 : 0),
            Elements = [.. result.Elements ?? [], .. items],
            Truncated = result.Truncated || lookup.More,
            TruncationReason = result.TruncationReason ?? (lookup.More ? ElementSearchTruncation.MaxResults : null),
            Virtualized = new VirtualizedSearchInfo(items.Count, containers, lookup.Note)
        };
    }

    private string? TryRegister(nint window, UiaElement element, long generation)
    {
        try
        {
            return _registry.Register(window, element, new ElementOrigin(generation));
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>What searchVirtualized found.</summary>
    /// <param name="Items">The matching items the tree search had not found, at most the room maxResults left.</param>
    /// <param name="More">Another such item exists beyond <paramref name="Items"/>.</param>
    /// <param name="Containers">The lists looked at.</param>
    /// <param name="Note">Why not everything could be searched, or null.</param>
    private sealed record VirtualizedLookup(IReadOnlyList<UiaElement> Items, bool More, IReadOnlyList<UiaElement> Containers, string? Note)
    {
        public static VirtualizedLookup Skipped(string note) => new([], false, [], note);
    }
}
