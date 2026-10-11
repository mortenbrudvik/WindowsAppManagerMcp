using System.Text.RegularExpressions;
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

    /// <summary>The provider had matched <see cref="ElementSearch.MaxCandidates"/> elements before the client filter.</summary>
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
/// top of the tree breadth first (at most <see cref="MaxExpansions"/> nodes; each costs one cheap listing of its children
/// and, for a level of at most <see cref="MaxExpandWidth"/> children, one cached read of them, whose elements are tested
/// here with <see cref="UiaConditionEvaluator"/>) until it has <see cref="TargetParts"/> parts. A level wider than that is
/// left whole: one call searches it. Then it runs one provider-side FindAll per part, in tree order, and between parts it
/// stops when the token is cancelled (<see cref="ElementSearchStop.Timeout"/>) or when the provider has matched
/// <see cref="MaxCandidates"/> elements (<see cref="ElementSearchStop.CandidateLimit"/>), returning what it found. One
/// part's FindAll is still a single call: the UIA3 engine's transaction timeout ends it after about 5 s (a
/// TimeoutException, which ends the search with what it found), and the caller's worker deadline abandons it.</para>
/// <para>Order: the matches of the expanded levels first, nearest the root first, then those of each part in tree order.</para>
/// <para>A condition the evaluator cannot test on cached properties (FrameworkId, ProcessId) is not split: the root is
/// searched in one call. A part that disappears or whose provider fails is skipped and counted; a timeout ends the search;
/// a <see cref="NotSupportedException"/> (the managed client cannot search for AppBar or SemanticZoom) is thrown to the
/// caller, which can move that criterion into the client filter. An exception of the client filter (a name pattern that
/// ran too long) ends the search and goes to the caller.</para>
/// <para>It runs on a UI Automation worker thread. <see cref="Snapshot"/> can be read from another thread while it runs, so
/// a caller whose worker abandoned the search still gets what it had found.</para>
/// </remarks>
public sealed class ElementSearch
{
    /// <summary>The search stops once the provider has matched this many elements (before the client filter).</summary>
    public const int MaxCandidates = 2000;

    /// <summary>At most this many nodes are expanded to split the tree.</summary>
    public const int MaxExpansions = 16;

    /// <summary>The tree is split until it has this many parts.</summary>
    public const int TargetParts = 12;

    /// <summary>A node with more children than this is not expanded: one FindAll searches it.</summary>
    public const int MaxExpandWidth = 50;

    private readonly object _gate = new();
    private readonly IUiaTree _tree;
    private readonly UiaCondition _condition;
    private readonly Func<UiaElement, bool>? _filter;
    private readonly int _keep;
    private readonly List<UiaElement> _matches = [];
    private int _matchCount;
    private int _candidates;
    private int _skippedParts;
    private ElementSearchStop _stop;

    /// <param name="tree">The UI Automation tree.</param>
    /// <param name="condition">The condition the provider evaluates.</param>
    /// <param name="filter">A further test on each element the provider matched (a name that contains a text or matches a
    /// regular expression), or null.</param>
    /// <param name="keep">How many matches to keep; the rest are only counted.</param>
    public ElementSearch(IUiaTree tree, UiaCondition condition, Func<UiaElement, bool>? filter, int keep)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(keep, 1);
        _tree = tree;
        _condition = condition;
        _filter = filter;
        _keep = keep;
    }

    /// <summary>Searches the elements below <paramref name="root"/> (not the root itself).</summary>
    /// <exception cref="NotSupportedException">The engine cannot express the condition.</exception>
    /// <exception cref="UiaElementUnavailableException">The root is gone.</exception>
    public ElementSearchOutcome Run(IUiaNode root, CancellationToken cancellationToken)
    {
        try
        {
            var parts = UiaConditionEvaluator.CanEvaluate(_condition)
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
                    found = _tree.FindAll(part, UiaScope.Descendants, _condition, cancellationToken);
                }
                catch (Exception ex) when (ex is not (NotSupportedException or TimeoutException or OperationCanceledException)
                                           && !ReferenceEquals(part, root))
                {
                    // This part disappeared or its provider failed; the others are still searched
                    Count(ref _skippedParts);
                    continue;
                }

                foreach (var element in found)
                {
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
                    parts[index] = (node, depth, true);
                    continue;
                }

                children = _tree.GetChildElements(node, cancellationToken);
            }
            catch (Exception ex) when (ex is not (TimeoutException or OperationCanceledException) && !(isRoot && ex is UiaElementUnavailableException))
            {
                // The node's level could not be read here: its FindAll searches it
                parts[index] = (node, depth, true);
                continue;
            }

            // The children read here are tested here: the parts' FindAll calls search only below them
            foreach (var child in children.Where(c => UiaConditionEvaluator.Matches(_condition, c)))
            {
                Consider(child);
            }

            parts.RemoveAt(index);
            parts.InsertRange(index, children.Select(c => (c.Node, depth + 1, false)));
        }

        return parts.Select(p => p.Node).ToList();
    }

    /// <summary>An element the condition matched: counted as a candidate, and kept or counted when it passes the filter.</summary>
    private void Consider(UiaElement element)
    {
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
            if (_candidates >= MaxCandidates)
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
/// provider would test it: strings compared ordinally (ignoring case when the condition says so), a missing string as
/// empty, control types by id when the engine read one and by name otherwise, runtime ids element by element.
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

    private static bool ControlTypeMatches(UiaElement element, string name) =>
        element.ControlTypeId is { } id && UiaControlTypes.GetId(name) is { } wanted
            ? id == wanted
            : string.Equals(element.Properties.ControlType, name, StringComparison.OrdinalIgnoreCase);
}
