using System.Globalization;
using WindowsAppManagerMcp.Models;
using WindowsAppManagerMcp.Native;
using WindowsAppManagerMcp.Services.Interfaces;

namespace WindowsAppManagerMcp.Services;

// searchVirtualized (roadmap M3.10): the items of the window's lists that are not realized yet, found through the lists'
// ItemContainer pattern. Only reads: realizing an item and scrolling it into view are element_action realize's, behind its
// guards (the input gate, pause, target policy, host guard) and audited. A placeholder that does not say what it is (Custom
// or unknown) is matched on the property the list compared and the other criteria, not on controlType (roadmap M3.F37).
public sealed partial class ElementSearchService
{
    /// <summary>The most lists searchVirtualized searches through their ItemContainer pattern in one call.</summary>
    public const int MaxVirtualizedContainers = 8;

    /// <summary>
    /// The most FindItemByProperty calls per list: each returns the next matching item (one cross-process call each), so this
    /// bounds a list whose items keep matching while they are skipped (already found, or not fitting the other criteria).
    /// </summary>
    public const int MaxVirtualizedProbes = 200;

    private const string TimedOutNote = "The timeout passed before every list was searched; the items found until then are returned.";
    private const string NoAnswerNote = "A list's provider did not answer in time; the items found until then are returned.";

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
    /// <param name="window">The named window, whose root is <paramref name="root"/> unless <paramref name="rootIsWithin"/>.</param>
    /// <param name="rootIsWithin">The root is withinRef's element: it is the list itself when it is one.</param>
    /// <param name="total">What the tree search found: its kept matches are not reported again.</param>
    private VirtualizedLookup SearchVirtualized(
        ElementCriteria criteria, nint window, IUiaNode root, bool rootIsWithin, SearchTotal total, int maxResults, CancellationToken token)
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
        string? containersSkipped;
        try
        {
            (containers, containersSkipped) = FindContainers(window, root, rootIsWithin, token);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || token.IsCancellationRequested)
        {
            return VirtualizedLookup.Skipped(
                $"The lists could not be looked for: {ex.Message}",
                ex is TimeoutException or OperationCanceledException ? ElementSearchTruncation.Timeout : ElementSearchTruncation.PartsSkipped);
        }

        if (containers.Count == 0)
        {
            return containersSkipped != null
                ? VirtualizedLookup.Skipped(containersSkipped,
                    token.IsCancellationRequested ? ElementSearchTruncation.Timeout : ElementSearchTruncation.PartsSkipped)
                : VirtualizedLookup.Skipped(
                    $"No list ({string.Join(", ", ListControlTypes)}) was found {(rootIsWithin ? "at or below withinRef's element" : "in the window")}.");
        }

        if (!containers.Any(c => (c.Patterns & UiaPatterns.ItemContainer) != 0))
        {
            return new VirtualizedLookup([], false, containers,
                "No list here supports the ItemContainer pattern, so items that are not realized cannot be asked for: scroll the " +
                "list (element_action realize with maxScrolls, or element_action scroll and find_ui_elements withinRef)." +
                (containersSkipped == null ? string.Empty : " " + containersSkipped),
                containersSkipped == null ? null : ElementSearchTruncation.PartsSkipped);
        }

        // The automation id names an item best; else its whole name. The other criteria are tested here on what was read
        var (property, value) = !string.IsNullOrEmpty(criteria.AutomationId)
            ? (UiaProperty.AutomationId, criteria.AutomationId)
            : (UiaProperty.Name, criteria.Name!);
        var (condition, filter) = (criteria with { VisibleOnly = false }).Compile(controlTypesOnServer: true);
        var evaluate = UiaConditionEvaluator.CanEvaluate(condition);

        // A placeholder that does not say what it is (Explorer's are Custom, its realized items ListItems) is not held to
        // controlType, which only the realized item can answer; the other criteria still apply (roadmap M3.F37)
        var (untypedCondition, untypedFilter) = (criteria with { VisibleOnly = false, ControlTypes = null }).Compile(controlTypesOnServer: true);
        var evaluateUntyped = UiaConditionEvaluator.CanEvaluate(untypedCondition);
        var untypedKept = 0;
        var known = total.Matches.Select(m => m.Element.RuntimeId).OfType<IReadOnlyList<int>>().ToList();
        var items = new List<VirtualizedItem>();
        var more = false;
        var matched = false;

        // Why the lookup did not finish: every way it stops early is reported as truncation, so a caller that needs exactly
        // one match (element_action) does not take a partial lookup for a complete one. A timeout outranks a skipped part
        var stop = containersSkipped == null ? (ElementSearchTruncation?)null : ElementSearchTruncation.PartsSkipped;
        var notes = new List<string>();
        if (containersSkipped != null)
        {
            notes.Add(containersSkipped);
        }

        void Stopped(ElementSearchTruncation reason, string note)
        {
            if (stop != ElementSearchTruncation.Timeout)
            {
                stop = reason;
            }

            if (!notes.Contains(note))
            {
                notes.Add(note);
            }
        }

        try
        {
            // The tree search ignores case, but a list compares the text as it is (WPF: case-sensitively): when no list has an
            // item with the exact text, the lists are asked again with it in the usual other cases (roadmap M3.F19)
            var asking = new List<string> { value };
            for (var pass = 0; pass < asking.Count; pass++)
            {
                var asked = asking[pass];
                foreach (var container in containers.Where(c => (c.Patterns & UiaPatterns.ItemContainer) != 0))
                {
                    if (token.IsCancellationRequested)
                    {
                        Stopped(ElementSearchTruncation.Timeout, TimedOutNote);
                        break;
                    }

                    IUiaItemContainerPattern? pattern;
                    try
                    {
                        pattern = _tree.GetPattern<IUiaItemContainerPattern>(container.Node);
                    }
                    catch (TimeoutException)
                    {
                        Stopped(ElementSearchTruncation.Timeout, NoAnswerNote);
                        break;
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        Stopped(ElementSearchTruncation.PartsSkipped, $"A list's ItemContainer pattern could not be read: {ex.Message}");
                        continue;
                    }

                    IUiaNode? after = null;
                    var finished = pattern == null;
                    for (var probe = 0; pattern != null && probe < MaxVirtualizedProbes; probe++)
                    {
                        if (token.IsCancellationRequested)
                        {
                            Stopped(ElementSearchTruncation.Timeout, TimedOutNote);
                            break;
                        }

                        IUiaNode? item;
                        UiaElement element;
                        try
                        {
                            item = pattern.FindItemByProperty(after, property, asked);
                        }
                        catch (TimeoutException)
                        {
                            Stopped(ElementSearchTruncation.Timeout, NoAnswerNote);
                            break;
                        }
                        catch (Exception ex) when (ex is not OperationCanceledException)
                        {
                            // This list's provider failed (or the item to start after went away): its other items are not known, so
                            // the lookup is not complete. The next list is searched
                            Stopped(ElementSearchTruncation.PartsSkipped, $"A list's provider failed, so not all of its items were searched: {ex.Message}");
                            finished = true;
                            break;
                        }

                        if (item == null)
                        {
                            finished = true;
                            break;
                        }

                        matched = true;
                        after = item;
                        try
                        {
                            element = ReadItem(item);
                        }
                        catch (TimeoutException)
                        {
                            Stopped(ElementSearchTruncation.Timeout, NoAnswerNote);
                            break;
                        }
                        catch (Exception ex) when (ex is not OperationCanceledException)
                        {
                            // An item that went away, or a placeholder that does not even name itself: the provider matched it, but it
                            // cannot be checked or named. The next one is asked for
                            Stopped(ElementSearchTruncation.PartsSkipped, "An item a list matched could not be read, so it was skipped.");
                            continue;
                        }

                        var untyped = criteria.ControlTypes is { Count: > 0 } && IsUntypedPlaceholder(element);
                        if (untyped
                                ? (evaluateUntyped && !UiaConditionEvaluator.Matches(untypedCondition, element)) ||
                                  (untypedFilter != null && !untypedFilter(element))
                                : (evaluate && !UiaConditionEvaluator.Matches(condition, element)) || (filter != null && !filter(element)))
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

                        items.Add(new VirtualizedItem(element, new ElementItemLocator(container, property, asked)));
                        if (untyped && !criteria.ControlTypes!.Contains(element.Properties.ControlType, StringComparer.OrdinalIgnoreCase))
                        {
                            untypedKept++;
                        }
                    }

                    if (more || stop == ElementSearchTruncation.Timeout)
                    {
                        break;
                    }

                    if (!finished)
                    {
                        Stopped(ElementSearchTruncation.PartsSkipped,
                            $"A list kept matching after {MaxVirtualizedProbes} items were asked for, so the rest of it was not searched. " +
                            "Add criteria (automationId), or search below the list with withinRef.");
                    }
                }

                if (more || stop == ElementSearchTruncation.Timeout || matched)
                {
                    break;
                }

                if (pass == 0)
                {
                    asking.AddRange(OtherCases(value));
                }
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            Stopped(ElementSearchTruncation.Timeout, TimedOutNote);
        }

        if (untypedKept > 0)
        {
            notes.Add($"{untypedKept} item{(untypedKept == 1 ? " is a placeholder" : "s are placeholders")} the list has not " +
                      "realized that do not say what they are (control type Custom or unknown), so controlType was not tested on " +
                      "them: they are returned with the control type they have now. element_action realize makes the item real.");
        }

        return new VirtualizedLookup(items, more, containers, notes.Count == 0 ? null : string.Join(" ", notes), stop);
    }

    /// <summary>
    /// An item a list returned for one it has not realized that does not say what it is (roadmap M3.F37): a placeholder (read
    /// property by property, or without a runtime id) whose control type is Custom or unknown. File Explorer's Items View
    /// returns such a Custom element, without a runtime id, for an item it has not realized, and a ListItem once it has.
    /// </summary>
    private static bool IsUntypedPlaceholder(UiaElement element) =>
        (element.Virtualized || element.RuntimeId is not { Count: > 0 }) &&
        element.Properties.ControlType is "Custom" or UiaControlTypes.Unknown;

    /// <summary>
    /// <paramref name="value"/> in the cases a list's text is usually in, other than its own: all lower case, all upper case,
    /// a capital first letter, and capital words (roadmap M3.F19). A provider compares one whole property as it is, so a
    /// name the tree search finds ignoring case is asked for in these when no item has it exactly.
    /// </summary>
    private static IEnumerable<string> OtherCases(string value)
    {
        var lower = value.ToLowerInvariant();
        var sentence = lower.Length == 0 ? lower : char.ToUpperInvariant(lower[0]) + lower[1..];
        return new[] { lower, value.ToUpperInvariant(), sentence, CultureInfo.InvariantCulture.TextInfo.ToTitleCase(lower) }
            .Where(c => !string.Equals(c, value, StringComparison.Ordinal))
            .Distinct(StringComparer.Ordinal);
    }

    /// <summary>
    /// An item a list's ItemContainer pattern returned, with the walk's properties. A realized item reads in one call; one the
    /// list has not realized is often a placeholder that fails that read as a whole for the properties it does not serve
    /// (WPF's fails its bounds, IsOffscreen and IsEnabled), so it is then read property by property
    /// (<see cref="IUiaTree.ReadVirtualizedItem"/>): offscreen, without bounds, marked virtualized.
    /// </summary>
    /// <exception cref="UiaElementUnavailableException">The item is gone, or does not even serve its runtime id and control
    /// type.</exception>
    private UiaElement ReadItem(IUiaNode item)
    {
        try
        {
            return _tree.ReadElement(item);
        }
        catch (UiaElementUnavailableException)
        {
            return _tree.ReadVirtualizedItem(item).Element;
        }
    }

    /// <summary>
    /// The lists searchVirtualized searches: withinRef's element when it is a list (or supports ItemContainer), else the
    /// List, Tree, DataGrid and Table elements below the root, those with the ItemContainer pattern first, then those on
    /// screen, at most <see cref="MaxVirtualizedContainers"/>; and why lists were left out, or null. The lists of the windows
    /// the window owns (its dialogs) are not its own.
    /// </summary>
    private (List<UiaElement> Containers, string? Skipped) FindContainers(nint window, IUiaNode root, bool rootIsWithin, CancellationToken token)
    {
        if (rootIsWithin)
        {
            var self = _tree.ReadElement(root);
            if ((self.Patterns & UiaPatterns.ItemContainer) != 0 || ListControlTypes.Contains(self.Properties.ControlType))
            {
                return ([self], null);
            }
        }

        var condition = UiaCondition.Or(ListControlTypes.Select(t => UiaCondition.PropertyEquals(UiaProperty.ControlType, t)).ToArray());
        var outcome = new ElementSearch(_tree, condition, filter: null, keep: MaxVirtualizedContainers * 4, maxCandidates: 200,
            leaveOut: rootIsWithin ? null : OwnedWindows.Below(_windows, window)).Run(root, token);
        var ordered = outcome.Matches
            .OrderBy(c => (c.Patterns & UiaPatterns.ItemContainer) != 0 ? 0 : 1)
            .ThenBy(c => c.Properties.IsOffscreen ? 1 : 0)
            .ToList();
        var withContainer = ordered.Count(c => (c.Patterns & UiaPatterns.ItemContainer) != 0);
        string? skipped = null;
        if (outcome.Stop != ElementSearchStop.None || outcome.SkippedParts > 0 || outcome.MatchCount > outcome.Matches.Count)
        {
            skipped = "Not every list of the window could be looked for (the search for them did not finish).";
        }
        else if (withContainer > MaxVirtualizedContainers)
        {
            skipped = $"{withContainer} lists support the ItemContainer pattern; the first {MaxVirtualizedContainers} (those on " +
                      "screen first) were searched. Search below the one you mean with withinRef.";
        }

        return (ordered.Take(MaxVirtualizedContainers).ToList(), skipped);
    }

    /// <summary>
    /// The result with what searchVirtualized found: its items after the tree's matches, and the lists with refs. An item of
    /// another process than the window's that the target policy refuses is left out, as a tree match is
    /// (<see cref="LeftOutForProcess"/>).
    /// </summary>
    private FindElementsResult WithVirtualized(
        FindElementsResult result, VirtualizedLookup lookup, ElementQuery query, ElementCriteria criteria, long generation,
        ForeignMatches foreign, CancellationToken token)
    {
        var window = query.WindowHandle;
        var mayRead = true;
        var items = new List<FoundElement>(lookup.Items.Count);
        foreach (var (item, locator) in lookup.Items)
        {
            if (LeftOutForProcess(foreign, window, item, query.Detail, ref mayRead, out var identity, token))
            {
                continue;
            }

            string? itemRef = null;
            try
            {
                // Not proven unique: the provider compared one property, and the list may hold look-alikes further on. An item
                // without a runtime id is named by the list's answer (roadmap M3.F37), unless the list gave that answer for
                // another item too: then the two cannot be told apart, and neither gets a ref
                var alike = item.RuntimeId is not { Count: > 0 } &&
                            lookup.Items.Count(other => other.Element.RuntimeId is not { Count: > 0 } && other.Locator == locator) > 1;
                itemRef = alike
                    ? null
                    : _registry.Register(window, item, new ElementOrigin(generation, criteria.Identity, Unique: null, Item: locator));
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
                    found = Read(found, item, query.Detail, identity);
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
            Truncated = result.Truncated || lookup.More || lookup.Stop != null,
            TruncationReason = MostSerious(result.TruncationReason, lookup.Stop, lookup.More ? ElementSearchTruncation.MaxResults : null),
            Virtualized = new VirtualizedSearchInfo(items.Count, containers, lookup.Note)
        };
    }

    /// <summary>
    /// The truncation reason that says most about what was not searched: a timeout, then the search limit, a skipped part
    /// and the window limit, then maxResults (every part searched, only more matches than returned).
    /// </summary>
    private static ElementSearchTruncation? MostSerious(params ElementSearchTruncation?[] reasons) =>
        reasons.OfType<ElementSearchTruncation>()
            .OrderBy(r => r switch
            {
                ElementSearchTruncation.Timeout => 0,
                ElementSearchTruncation.SearchLimit => 1,
                ElementSearchTruncation.PartsSkipped => 2,
                ElementSearchTruncation.WindowLimit => 3,
                _ => 4
            })
            .Select(r => (ElementSearchTruncation?)r)
            .FirstOrDefault();

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
    /// <param name="Items">The matching items the tree search had not found, at most the room maxResults left, each with the
    /// list that returned it and what it was asked.</param>
    /// <param name="More">Another such item exists beyond <paramref name="Items"/>.</param>
    /// <param name="Containers">The lists looked at.</param>
    /// <param name="Note">Why not everything could be searched, or null.</param>
    /// <param name="Stop">Why the lookup did not search every item of every list, as the result's truncation reason: Timeout
    /// (the deadline, or a provider that did not answer), PartsSkipped (a provider that failed, an item that could not be
    /// read, lists left out by the limits). Null when it finished, or when it did not run because the tree search's own
    /// truncation already says why.</param>
    private sealed record VirtualizedLookup(
        IReadOnlyList<VirtualizedItem> Items, bool More, IReadOnlyList<UiaElement> Containers, string? Note, ElementSearchTruncation? Stop = null)
    {
        public static VirtualizedLookup Skipped(string note, ElementSearchTruncation? stop = null) => new([], false, [], note, stop);
    }

    /// <summary>An item a list returned, and where: the list and the property and value it was asked for.</summary>
    private sealed record VirtualizedItem(UiaElement Element, ElementItemLocator Locator);
}
