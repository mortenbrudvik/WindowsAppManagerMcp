using System.Globalization;
using System.Text.RegularExpressions;
using WindowsAppManagerMcp.Models;
using WindowsAppManagerMcp.Native;
using WindowsAppManagerMcp.Services.Interfaces;

namespace WindowsAppManagerMcp.Services;

/// <summary>Why an <see cref="ElementSearch"/> stopped before it had searched everything below its root.</summary>
public enum ElementSearchStop
{
    /// <summary>It searched everything (parts that failed are counted in <see cref="ElementSearchOutcome.SkippedParts"/>).</summary>
    None,

    /// <summary>Its cancellation token was cancelled (the caller's deadline), or a provider did not answer in time.</summary>
    Timeout,

    /// <summary>The provider had matched the candidate limit (<see cref="ElementSearch.MaxCandidates"/> at most) before the client filter.</summary>
    CandidateLimit
}

/// <summary>What an <see cref="ElementSearch"/> found.</summary>
/// <param name="Matches">The first matches, at most the number the search keeps, in the order found.</param>
/// <param name="MatchCount">Every match found, kept or not.</param>
/// <param name="Candidates">The elements the provider matched before the client filter.</param>
/// <param name="SkippedParts">Parts of the tree that could not be searched: their elements disappeared or their provider
/// failed.</param>
public sealed record ElementSearchOutcome(
    IReadOnlyList<UiaElement> Matches,
    int MatchCount,
    int Candidates,
    ElementSearchStop Stop,
    int SkippedParts)
{
    /// <summary>True when everything below the root was searched.</summary>
    public bool Complete => Stop == ElementSearchStop.None && SkippedParts == 0;
}

/// <summary>
/// A search of the elements below a root, with the condition evaluated by the provider (IUiaTree.FindAll) and real limits
/// on its cost (roadmap M3.4). Used by find_ui_elements and by the element registry when it finds an element again.
/// </summary>
/// <remarks>
/// <para>One FindAll over a whole window cannot be stopped once it has started, and its cost grows with the window: a web
/// page or a large document can hold tens of thousands of elements. So the search splits the tree first. It expands the
/// top of the tree breadth first (at most <see cref="MaxExpansions"/> nodes with several children, and up to
/// <see cref="MaxChainExpansions"/> more with a single child; each costs one cheap listing of its children
/// and, for a level of at most <see cref="MaxExpandWidth"/> children, one cached read of them, whose elements are tested
/// here with <see cref="UiaConditionEvaluator"/>) until it has <see cref="TargetParts"/> parts. A level wider than that is
/// left whole: one call searches it. Then it runs one provider-side FindAll per part, in tree order. Between parts (and,
/// when a client filter runs, between the elements of a part's results), it stops when the token is cancelled (<see cref="ElementSearchStop.Timeout"/>)
/// or when the provider has matched <see cref="MaxCandidates"/> elements (<see cref="ElementSearchStop.CandidateLimit"/>),
/// returning what it found. The limits are checked between provider calls, not inside one: a part's FindAll is a single
/// call that returns every match of that part (a level wider than <see cref="MaxExpandWidth"/>, such as a list of ten
/// thousand rows, is one part). The UIA3 engine's transaction timeout ends such a call after about 5 s (a
/// TimeoutException, which ends the search with what it found); under the managed engine (WAM_UIA_ENGINE=uia2) only the
/// caller's worker deadline abandons it, and the stuck thread counts against the application until it returns. Searching
/// a wide level child by child instead would turn one call into thousands.</para>
/// <para>Order: the matches of the expanded levels first, nearest the root first, then those of each part in tree order.</para>
/// <para>Repeats (roadmap M3.F43): an element the search reaches twice (the same runtime id: a provider that lists a subtree
/// under two parents, as a WinUI 3 window lists its WebView2's page, or two parts that overlap) is one match, counted and kept
/// where it was first found. An element without a runtime id cannot be told from another, and counts each time.</para>
/// <para>Owned windows: the root's children that <c>leaveOut</c> names (<see cref="OwnedWindows"/>: the dialogs and popups
/// the HWND proxy lists under the top-level window that owns them) are left out with everything below them, so a search of
/// a window covers its own tree only. When the root is split, they are not made parts. A root wider than
/// <see cref="MaxExpandWidth"/> is searched whole, so its first children are read one at a time while they are owned
/// windows (the HWND proxy lists them first, at most <see cref="MaxOwnedWindows"/>), each such window is searched for the
/// condition, and its matches are not counted in the root's. A root that cannot be split otherwise (a condition the
/// evaluator cannot test, a level that cannot be read) is searched whole, owned windows included; the callers that search
/// several windows drop such duplicates by runtime id.</para>
/// <para>A condition the evaluator cannot test on cached properties (FrameworkId, ProcessId) is not split: the root is
/// searched in one call. A part that disappears or whose provider fails (also one that does not support the search,
/// UIA_E_NOTSUPPORTED) is skipped and counted; a timeout ends the search; a <see cref="UiaConditionNotSupportedException"/>
/// (the managed client cannot search for AppBar or SemanticZoom) is thrown to the caller, which can move that criterion
/// into the client filter. An exception of the client filter (a name pattern that
/// ran too long) ends the search and goes to the caller.</para>
/// <para>Narrowing (roadmap M3.F12): a caller whose client filter tests a name that contains a text can pass a substring
/// condition (<see cref="ElementCriteria.NameNarrowing"/>) that every element the filter accepts also matches (the server's
/// callers pass it only with WAM_UIA_PROVIDER_CONTAINS=1, <see cref="ProviderContainsOptions"/>, until Windows' substring
/// search is checked on a real desktop). Under the UIA3
/// engine it is added to the condition, so the provider returns, and the limits count, only the elements whose names have the
/// text in them; the filter still runs on every element returned, so a provider that ignores the condition or misapplies it
/// adds nothing. The managed engine has no substring flag and is never given it. When the engine refuses it
/// (<see cref="UiaConditionNotSupportedException"/>: Windows before 10 1809, which the UIA3 engine then remembers), the search
/// goes on without it, from the part that refused it, without splitting again. A complete search finds the same matches,
/// in the same order, with or without it. Only the cost limits can tell them apart: the candidate limit counts what the
/// provider matched, so a search that the limit stopped without the narrowing gets further with it, and finds what it
/// found without it first, in the same order; and, costing less, a search gets further before the deadline.</para>
/// <para>It runs on a UI Automation worker thread. <see cref="Snapshot"/> can be read from another thread while it runs, so
/// a caller whose worker abandoned the search still gets what it had found.</para>
/// </remarks>
public sealed class ElementSearch
{
    /// <summary>The search stops once the provider has matched this many elements (before the client filter).</summary>
    public const int MaxCandidates = 2000;

    /// <summary>At most this many nodes with several children are expanded to split the tree.</summary>
    public const int MaxExpansions = 16;

    /// <summary>
    /// A node with a single child is expanded without counting against <see cref="MaxExpansions"/>, at most this many
    /// times: the wrapper panes at the top of a browser or Electron window would otherwise use up the split.
    /// </summary>
    public const int MaxChainExpansions = 32;

    /// <summary>The tree is split until it has this many parts.</summary>
    public const int TargetParts = 12;

    /// <summary>A node with more children than this is not expanded: one FindAll searches it.</summary>
    public const int MaxExpandWidth = 50;

    /// <summary>A root searched whole has at most this many owned windows left out (the popups a window lists, at most).</summary>
    public const int MaxOwnedWindows = PopupService.MaxPopups;

    private readonly object _gate = new();
    private readonly IUiaTree _tree;
    private readonly UiaCondition _condition;
    private readonly Func<UiaElement, bool>? _filter;
    private readonly Func<UiaElement, bool>? _leaveOut;
    private readonly int _keep;
    private readonly int _maxCandidates;
    private readonly List<UiaElement> _matches = [];

    // The matches inside the owned windows of a root searched whole, by runtime id: they are not the root's
    private HashSet<string>? _leftOut;

    // The elements the condition matched so far, by runtime id: one the tree reaches again is not counted twice
    private readonly HashSet<string> _considered = new(StringComparer.Ordinal);

    // The condition with the narrowing, until the engine refuses it; null without one
    private UiaCondition? _narrowed;
    private int _matchCount;
    private int _candidates;
    private int _skippedParts;
    private ElementSearchStop _stop;

    /// <param name="tree">The UI Automation tree.</param>
    /// <param name="condition">The condition the provider evaluates.</param>
    /// <param name="filter">A further test on each element the provider matched (a name that contains a text or matches a
    /// regular expression), or null.</param>
    /// <param name="keep">How many matches to keep; the rest are only counted.</param>
    /// <param name="maxCandidates">The search stops once the provider has matched this many elements (at most
    /// <see cref="MaxCandidates"/>, the default): a search of several windows (roadmap M3.5) passes what is left of one
    /// limit for all of them.</param>
    /// <param name="leaveOut">The root's children to leave out with everything below them (see the remarks:
    /// <see cref="OwnedWindows.Below"/> for a window's root), or null to search everything below the root.</param>
    /// <param name="narrowing">A condition every element <paramref name="filter"/> accepts also matches, for the provider to
    /// test under the UIA3 engine (see the remarks: <see cref="ElementCriteria.NameNarrowing"/>), or null.</param>
    public ElementSearch(
        IUiaTree tree, UiaCondition condition, Func<UiaElement, bool>? filter, int keep, int maxCandidates = MaxCandidates,
        Func<UiaElement, bool>? leaveOut = null, UiaCondition? narrowing = null)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(keep, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxCandidates, 1);
        _tree = tree;
        _condition = condition;
        _filter = filter;
        _leaveOut = leaveOut;
        _keep = keep;
        _maxCandidates = Math.Min(maxCandidates, MaxCandidates);
        _narrowed = narrowing != null && tree.Engine == UiaEngine.Uia3 ? UiaCondition.And(condition, narrowing) : null;
    }

    /// <summary>The condition the provider and the split test: with the narrowing while the engine takes it.</summary>
    private UiaCondition Condition => _narrowed ?? _condition;

    /// <summary>Searches the elements below <paramref name="root"/> (not the root itself).</summary>
    /// <exception cref="UiaConditionNotSupportedException">The engine cannot express the condition.</exception>
    /// <exception cref="UiaElementUnavailableException">The root is gone.</exception>
    public ElementSearchOutcome Run(IUiaNode root, CancellationToken cancellationToken)
    {
        try
        {
            var parts = UiaConditionEvaluator.CanEvaluate(Condition)
                ? Split(root, cancellationToken)
                : [root];
            foreach (var part in parts)
            {
                if (StopRequested(cancellationToken))
                {
                    break;
                }

                IReadOnlyList<UiaElement> found;
                try
                {
                    found = Find(part, UiaScope.Descendants, cancellationToken);
                }
                catch (Exception ex) when (ex is not (UiaConditionNotSupportedException or TimeoutException or OperationCanceledException)
                                           && !ReferenceEquals(part, root))
                {
                    // This part disappeared or its provider failed; the others are still searched
                    Count(ref _skippedParts);
                    continue;
                }

                // With a client filter (a name pattern), the limits also apply within one part's results: the filter is not
                // run on more than MaxCandidates elements, or after the deadline. Without one, counting what the provider
                // already returned costs nothing, and the counts stay exact
                foreach (var element in found)
                {
                    if (_filter != null && StopRequested(cancellationToken))
                    {
                        break;
                    }

                    Consider(element);
                }
            }
        }
        catch (Exception ex) when ((ex is TimeoutException and not RegexMatchTimeoutException) ||
                                   (ex is OperationCanceledException && cancellationToken.IsCancellationRequested))
        {
            // A provider that did not answer in time (the UIA3 transaction timeout), or the deadline: what was found stays.
            // A name pattern that ran too long (a RegexMatchTimeoutException, also a TimeoutException) is the caller's
            // mistake, and goes to the caller
            SetStop(ElementSearchStop.Timeout);
        }

        return Snapshot();
    }

    /// <summary>What the search has found so far; safe to call from any thread.</summary>
    public ElementSearchOutcome Snapshot()
    {
        lock (_gate)
        {
            return new ElementSearchOutcome(_matches.ToList(), _matchCount, _candidates, _stop, _skippedParts);
        }
    }

    /// <summary>
    /// Splits the tree below <paramref name="root"/> into parts, testing the children of every node it expands; returns
    /// the nodes whose descendants are still to be searched, in tree order.
    /// </summary>
    private List<IUiaNode> Split(IUiaNode root, CancellationToken cancellationToken)
    {
        // The parts in tree order, each with its depth and whether it is to be searched whole
        var parts = new List<(IUiaNode Node, int Depth, bool Whole)> { (root, 0, false) };
        var expansions = 0;
        var chainExpansions = 0;
        while (expansions < MaxExpansions && parts.Count < TargetParts && !StopRequested(cancellationToken))
        {
            // Breadth first: the shallowest part not yet expanded
            var index = -1;
            for (var i = 0; i < parts.Count; i++)
            {
                if (!parts[i].Whole && (index < 0 || parts[i].Depth < parts[index].Depth))
                {
                    index = i;
                }
            }

            if (index < 0)
            {
                break;
            }

            expansions++;
            var (node, depth, _) = parts[index];
            var isRoot = ReferenceEquals(node, root);
            IReadOnlyList<UiaElement> children;
            try
            {
                var listed = _tree.GetChildren(node, cancellationToken);
                if (listed.Count == 0)
                {
                    parts.RemoveAt(index);
                    continue;
                }

                if (listed.Count > MaxExpandWidth)
                {
                    if (isRoot && _leaveOut != null)
                    {
                        LeaveOutOwnedWindows(listed, cancellationToken);
                    }

                    parts[index] = (node, depth, true);
                    continue;
                }

                if (listed.Count == 1 && chainExpansions < MaxChainExpansions)
                {
                    chainExpansions++;
                    expansions--;
                }

                children = _tree.GetChildElements(node, cancellationToken);
            }
            catch (Exception ex) when (ex is not (TimeoutException or OperationCanceledException) && !(isRoot && ex is UiaElementUnavailableException))
            {
                // The node's level could not be read here: its FindAll searches it
                parts[index] = (node, depth, true);
                continue;
            }

            // A window the root's window owns is searched only as its own window
            if (isRoot && _leaveOut != null)
            {
                children = children.Where(c => !_leaveOut(c)).ToList();
            }

            // The children read here are tested here: the parts' FindAll calls search only below them
            foreach (var child in children.Where(c => UiaConditionEvaluator.Matches(Condition, c)))
            {
                Consider(child);
            }

            parts.RemoveAt(index);
            parts.InsertRange(index, children.Select(c => (c.Node, depth + 1, false)));
        }

        return parts.Select(p => p.Node).ToList();
    }

    /// <summary>
    /// For a root searched whole: reads its first children one at a time while they are windows it owns, and notes the
    /// elements in each that the condition matches, so the root's search does not count them. A child that cannot be read
    /// ends the reads.
    /// </summary>
    private void LeaveOutOwnedWindows(IReadOnlyList<IUiaNode> children, CancellationToken cancellationToken)
    {
        for (var i = 0; i < Math.Min(children.Count, MaxOwnedWindows); i++)
        {
            UiaElement child;
            try
            {
                child = _tree.ReadElement(children[i]);
            }
            catch (Exception ex) when (ex is not (TimeoutException or OperationCanceledException))
            {
                return;
            }

            if (!_leaveOut!(child))
            {
                return;
            }

            IReadOnlyList<UiaElement> inside;
            try
            {
                inside = Find(children[i], UiaScope.Subtree, cancellationToken);
            }
            catch (Exception ex) when (ex is not (NotSupportedException or TimeoutException or OperationCanceledException))
            {
                // The owned window closed meanwhile: nothing of it is in the root's search either
                continue;
            }

            _leftOut ??= new HashSet<string>(StringComparer.Ordinal);
            foreach (var element in inside)
            {
                if (element.RuntimeId is { Count: > 0 } id)
                {
                    _leftOut.Add(RuntimeKey(id));
                }
            }
        }
    }

    /// <summary>
    /// The provider's search of <paramref name="node"/>: with the narrowing while the engine takes it, else (and from then on)
    /// with the condition alone.
    /// </summary>
    private IReadOnlyList<UiaElement> Find(IUiaNode node, UiaScope scope, CancellationToken cancellationToken)
    {
        if (_narrowed is { } narrowed)
        {
            try
            {
                return _tree.FindAll(node, scope, narrowed, cancellationToken);
            }
            catch (UiaConditionNotSupportedException)
            {
                // The engine cannot search for a substring: the client filter alone tests the name, as without a narrowing
                _narrowed = null;
            }
        }

        return _tree.FindAll(node, scope, _condition, cancellationToken);
    }

    private static string RuntimeKey(IReadOnlyList<int> runtimeId) =>
        string.Join('.', runtimeId.Select(i => i.ToString(CultureInfo.InvariantCulture)));

    /// <summary>
    /// An element the condition matched: counted as a candidate, and kept or counted when it passes the filter. One met
    /// again (the same runtime id: a provider that lists a subtree under two parents, as a WinUI 3 WebView2 lists its page,
    /// roadmap M3.F43) was considered already, and is not counted again.
    /// </summary>
    private void Consider(UiaElement element)
    {
        if (element.RuntimeId is { Count: > 0 } id)
        {
            var key = RuntimeKey(id);
            if (_leftOut?.Contains(key) == true)
            {
                // An element of a window the root's window owns
                return;
            }

            if (!_considered.Add(key))
            {
                return;
            }
        }

        var passes = _filter == null || _filter(element);
        lock (_gate)
        {
            _candidates++;

            if (!passes)
            {
                return;
            }

            _matchCount++;
            if (_matches.Count < _keep)
            {
                _matches.Add(element);
            }
        }
    }

    private bool StopRequested(CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            SetStop(ElementSearchStop.Timeout);
            return true;
        }

        lock (_gate)
        {
            if (_candidates >= _maxCandidates)
            {
                _stop = _stop == ElementSearchStop.None ? ElementSearchStop.CandidateLimit : _stop;
                return true;
            }

            return _stop != ElementSearchStop.None;
        }
    }

    private void SetStop(ElementSearchStop stop)
    {
        lock (_gate)
        {
            if (_stop == ElementSearchStop.None)
            {
                _stop = stop;
            }
        }
    }

    private void Count(ref int counter)
    {
        lock (_gate)
        {
            counter++;
        }
    }
}

/// <summary>
/// Tests a <see cref="UiaCondition"/> on the properties an element was read with (<see cref="UiaElement"/>), the way a
/// provider would test it: strings compared ordinally (ignoring case when the condition says so), a substring searched for
/// the same way, a missing string as empty, control types by id when the engine read one and by name otherwise, runtime
/// ids element by element.
/// </summary>
public static class UiaConditionEvaluator
{
    /// <summary>
    /// True when every property the condition compares is one a <see cref="UiaElement"/> carries (not FrameworkId or
    /// ProcessId).
    /// </summary>
    public static bool CanEvaluate(UiaCondition condition) => condition switch
    {
        UiaTrueCondition => true,
        UiaPropertyCondition property => property.Property is not (UiaProperty.FrameworkId or UiaProperty.ProcessId),
        UiaPropertyContainsCondition contains => contains.Property is not UiaProperty.FrameworkId,
        UiaAndCondition and => and.Conditions.All(CanEvaluate),
        UiaOrCondition or => or.Conditions.All(CanEvaluate),
        UiaNotCondition not => CanEvaluate(not.Condition),
        _ => false
    };

    /// <summary>Whether <paramref name="element"/> matches <paramref name="condition"/>.</summary>
    /// <exception cref="NotSupportedException">The condition compares a property the element was not read with.</exception>
    public static bool Matches(UiaCondition condition, UiaElement element) => condition switch
    {
        UiaTrueCondition => true,
        UiaPropertyCondition property => PropertyMatches(property, element),
        UiaPropertyContainsCondition contains => ContainsMatches(contains, element),
        UiaAndCondition and => and.Conditions.All(c => Matches(c, element)),
        UiaOrCondition or => or.Conditions.Any(c => Matches(c, element)),
        UiaNotCondition not => !Matches(not.Condition, element),
        _ => throw new ArgumentException($"Unknown condition {condition.GetType().Name}.", nameof(condition))
    };

    private static bool PropertyMatches(UiaPropertyCondition condition, UiaElement element)
    {
        var comparison = condition.IgnoreCase ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        return condition.Property switch
        {
            UiaProperty.Name => string.Equals(element.Properties.Name ?? string.Empty, (string)condition.Value, comparison),
            UiaProperty.AutomationId => string.Equals(element.Properties.AutomationId ?? string.Empty, (string)condition.Value, comparison),
            UiaProperty.ClassName => string.Equals(element.ClassName ?? string.Empty, (string)condition.Value, comparison),
            UiaProperty.ControlType => ControlTypeMatches(element, (string)condition.Value),
            UiaProperty.IsEnabled => element.Properties.IsEnabled == (bool)condition.Value,
            UiaProperty.IsOffscreen => element.Properties.IsOffscreen == (bool)condition.Value,
            UiaProperty.IsKeyboardFocusable => element.IsKeyboardFocusable == (bool)condition.Value,
            UiaProperty.RuntimeId => element.RuntimeId is { } id && id.SequenceEqual((IReadOnlyList<int>)condition.Value),
            _ => throw new NotSupportedException($"{condition.Property} is not read with an element.")
        };
    }

    private static bool ContainsMatches(UiaPropertyContainsCondition condition, UiaElement element)
    {
        var value = condition.Property switch
        {
            UiaProperty.Name => element.Properties.Name,
            UiaProperty.AutomationId => element.Properties.AutomationId,
            UiaProperty.ClassName => element.ClassName,
            _ => throw new NotSupportedException($"{condition.Property} is not read with an element.")
        };
        return (value ?? string.Empty).Contains(
            condition.Value, condition.IgnoreCase ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
    }

    private static bool ControlTypeMatches(UiaElement element, string name) =>
        element.ControlTypeId is { } id && UiaControlTypes.GetId(name) is { } wanted
            ? id == wanted
            : string.Equals(element.Properties.ControlType, name, StringComparison.OrdinalIgnoreCase);
}
