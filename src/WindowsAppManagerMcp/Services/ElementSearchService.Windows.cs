using WindowsAppManagerMcp.Models;
using WindowsAppManagerMcp.Services.Interfaces;

namespace WindowsAppManagerMcp.Services;

/// <summary>
/// The windowAndPopups and desktop scopes, read window by window (roadmap M3.F13); see <see cref="ElementSearchService"/>.
/// </summary>
/// <remarks>
/// <para>The worker counts a read that misses its deadline against the application it names, and refuses that application's
/// next reads while too many are stuck (<see cref="IUiaWorker"/>). A search of several windows is therefore not one read:
/// the start (the named window's hung check, node, warm-up and title, and the scope's windows) is a read counted against the
/// named window's application (or the unknown-target bucket for a desktop search that names no window); each window's search
/// is a read of its own, counted against that window's application (<see cref="UiaTargets.TargetProcessId"/>); and so are the
/// reads after them that touch a window's elements: searchVirtualized's lookup in the named window's lists, each window's
/// proofs that its matches are unique, each window's matches described, and the virtualized items described. A provider
/// stuck in a popup of another process (a WebView2 child process, a shell extension's menu) or in another window of the
/// desktop then counts against that application alone, and does not refuse the next search of the window named.</para>
/// <para>The reads run one at a time, in the order the one read made them, under the one deadline: what is searched, the
/// limits each window's search gets, the order of the matches, the counts and the truncation reasons are those of a single
/// read. A window other than the named one whose application the worker refuses is skipped at once and counted, as a window
/// that does not respond is (truncationReason PartsSkipped); a read after the windows' searches that the worker refuses
/// leaves out only what it would have added (that window's proofs and details: its matches are described from what the
/// search read; the lists' items, with a note). The named window's application being refused, at the start or for its own
/// search, fails the call with OperationTimedOut, as the window scope's single read does. A read the worker abandons at the
/// deadline ends the call as an abandoned single read does: with the matches found until then, described from what the
/// search read.</para>
/// </remarks>
public sealed partial class ElementSearchService
{
    /// <summary>A search of the windowAndPopups or desktop scope, a worker read per step and window (see the remarks).</summary>
    private async Task<FindElementsResult> FindAcrossAsync(SearchCall call, TimeSpan warmupBudget, CancellationToken token)
    {
        var handle = call.Query.WindowHandle;
        if (await ReadAsync(call, handle, t => Open(call, warmupBudget, t), token).ConfigureAwait(false) is { } failure)
        {
            return failure;
        }

        for (var index = 0; index < call.Windows.Count; index++)
        {
            if (CandidatesFor(call, index, token, out var end) is not { } candidatesLeft)
            {
                if (end)
                {
                    break;
                }

                continue;
            }

            var at = index;
            var window = call.Windows[at];
            var more = false;
            try
            {
                if (await ReadAsync(call, window, t =>
                    {
                        more = SearchWindow(call, at, candidatesLeft, t);
                        return null;
                    }, token).ConfigureAwait(false) is { } searchFailure)
                {
                    return searchFailure;
                }
            }
            catch (UiaReadRefusedException) when (window != handle)
            {
                // Earlier reads of this window's application are still stuck: skipped at once, as a window that does not
                // respond is, and the other windows are still searched
                call.Started.SkipWindow();
                continue;
            }

            if (!more)
            {
                break;
            }
        }

        if (Gathered(call) is { } timedOut)
        {
            return timedOut;
        }

        // searchVirtualized (roadmap M3.10): the named window's lists, asked for items they have not realized
        VirtualizedLookup? virtualized = null;
        if (call.Query.SearchVirtualized && call.Root != null)
        {
            try
            {
                if (await ReadAsync(call, handle, t =>
                    {
                        virtualized = Virtualized(call, t);
                        return null;
                    }, token).ConfigureAwait(false) is { } lookupFailure)
                {
                    return lookupFailure;
                }
            }
            catch (UiaReadRefusedException ex)
            {
                virtualized = VirtualizedLookup.Skipped(
                    $"The lists were not searched for items that are not realized: {ex.Message}", ElementSearchTruncation.PartsSkipped);
            }
        }

        var verdicts = new Dictionary<UiaElement, bool?>(ReferenceEqualityComparer.Instance);
        if (MayProveUnique(call, virtualized))
        {
            // One budget for every window's proof, the named window's first, each in a read of that window's application
            using var budget = CancellationTokenSource.CreateLinkedTokenSource(token);
            budget.CancelAfter(UniquenessBudget);
            foreach (var searched in call.Started.Searched.Where(w => w.Kept.Count > 0).OrderBy(w => w.Handle == handle ? 0 : 1))
            {
                Dictionary<UiaElement, bool?>? proven = null;
                try
                {
                    if (await ReadAsync(call, searched.Handle, _ =>
                        {
                            proven = Verdicts(call, [searched], budget.Token);
                            return null;
                        }, token).ConfigureAwait(false) is { } proofFailure)
                    {
                        return proofFailure;
                    }
                }
                catch (UiaReadRefusedException)
                {
                    // Not proven: the window's matches are registered without a verdict
                }

                foreach (var (match, verdict) in proven ?? Enumerable.Empty<KeyValuePair<UiaElement, bool?>>())
                {
                    verdicts[match] = verdict;
                }
            }
        }

        bool? Unique(UiaElement match) => verdicts.TryGetValue(match, out var verdict) ? verdict : null;

        // Each window's matches in a read of its application, in the order searched
        var elements = new List<FoundElement>(call.Total.Matches.Count);
        foreach (var group in call.Total.Matches.GroupBy(m => m.Window))
        {
            var matches = group.ToList();
            List<FoundElement>? described = null;
            try
            {
                if (await ReadAsync(call, group.Key, t =>
                    {
                        described = Describe(call.Query, call.Criteria, matches, call.Generation, mayRead: true, Unique, t);
                        return null;
                    }, token).ConfigureAwait(false) is { } describeFailure)
                {
                    return describeFailure;
                }
            }
            catch (UiaReadRefusedException)
            {
                // Described from what the search read, with no further call
                described = Describe(call.Query, call.Criteria, matches, call.Generation, mayRead: false, Unique, default);
            }

            elements.AddRange(described ?? []);
        }

        var result = Result(call, elements);
        if (virtualized != null)
        {
            var lookup = virtualized;
            var described = result;
            FindElementsResult? withItems = null;
            try
            {
                if (await ReadAsync(call, handle, t =>
                    {
                        withItems = WithVirtualized(described, lookup, call.Query, call.Criteria, call.Generation, t);
                        return null;
                    }, token).ConfigureAwait(false) is { } itemsFailure)
                {
                    return itemsFailure;
                }
            }
            catch (UiaReadRefusedException)
            {
                // The items with their refs, described from what the lookup read
                withItems = WithVirtualized(described, lookup, call.Query, call.Criteria, call.Generation, new CancellationToken(canceled: true));
            }

            result = withItems ?? result;
        }

        return Finished(call, result);
    }

    /// <summary>
    /// One step of the search on a worker thread, counted against the application of <paramref name="window"/> (none for
    /// no window): <paramref name="read"/>'s failure, with UI Automation's failures turned into the call's (see
    /// <see cref="Guarded"/>), or null to go on.
    /// </summary>
    /// <exception cref="UiaReadRefusedException">Earlier reads of that application are still stuck; nothing was read.</exception>
    /// <exception cref="TimeoutException">The worker abandoned the read at the deadline.</exception>
    private Task<FindElementsResult?> ReadAsync(
        SearchCall call, nint window, Func<CancellationToken, FindElementsResult?> read, CancellationToken token) =>
        _worker.RunAsync(TargetOf(window), t => Guarded(call, t, () => read(t)), token, StopGrace);
}
