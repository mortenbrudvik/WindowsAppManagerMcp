using System.Diagnostics;
using System.Text.RegularExpressions;
using WindowsAppManagerMcp.Models;
using WindowsAppManagerMcp.Services.Interfaces;

namespace WindowsAppManagerMcp.Services;

/// <summary>find_ui_elements (roadmap M3.4); see <see cref="IElementSearchService"/>.</summary>
/// <remarks>
/// <para>Order of work: the target policy's check of the named window (TargetDeniedByPolicy, before the worker, see
/// <see cref="UiaTargets.ReadVerdict"/>; a withinRef search names withinRef's window), then all in one read on the worker
/// thread: the hung-window check (NotResponding), the window's node
/// (WindowNotFound), the warm-up of a window that shows web content (Chromium, Electron, WebView2: see
/// <see cref="IAccessibilityWarmup"/>, roadmap M3.3) within a third of the timeout, withinRef resolved through the
/// registry (its own codes: ElementStale, AmbiguousElement), the search
/// (<see cref="ElementSearch"/>, keeping the first maxResults matches and counting the rest), then for each kept match the
/// list row it is in (<see cref="ElementRows"/>: up to 8 ancestors, each read once for all the matches), its ref, its click
/// point (UI Automation's clickable point, else the centre of its bounds; none for an offscreen element) and, for detail
/// standard or full, its state, each one call per property group. A withinRef moved to a re-created element
/// (<see cref="ResolvedElement.Moved"/>) is searched below, and the result says so (refMoved, warning).</para>
/// <para>Each match is registered with the search's identity criteria as its selector and whether they fit only it in the
/// window (<see cref="ElementOrigin.Unique"/>), which decides whether a resolution may later move its ref to the element
/// re-created. Proven only by a complete search of the whole window (not below withinRef): without visibleOnly or
/// enabledOnly, when every match was kept, each one whose fingerprint no other match shares; with them, which can hide an
/// offscreen or disabled twin, a single match is checked by one more search of the window for its identity alone, limited
/// to <see cref="UniquenessBudget"/>. Two kept matches with one fingerprint are not unique. A tree search cannot see the
/// rows a virtualizing list has not realized, so a ListItem, TreeItem or DataItem named by its text (no automation id)
/// that the tree proved alone is also checked with its list (the nearest ancestor with the ItemContainer pattern, at most 3
/// levels up): two FindItemByProperty calls by its name, which see realized and unrealized items alike; another item of
/// that name makes it not unique, and a virtualized item whose list cannot be asked is not proven (in the same budget).</para>
/// <para><b>Scope</b> (roadmap M3.5): <see cref="ElementSearchScope.WindowAndPopups"/> also searches the window's popups
/// (<see cref="IPopupService.GetPopupList"/>: its menus, dropdowns, flyouts and dialogs, which are top-level windows of their
/// own), front first and before the window, since an open popup is in front of it, but not a popup of an app the target
/// policy denies, nor one of the agent's host unless <see cref="ElementQuery.IncludeHostPopups"/> (both counted in
/// windowsExcluded, as the desktop scope counts such windows); <see cref="ElementSearchScope.Desktop"/>
/// searches the windows of <see cref="IPopupService.GetDesktopWindows"/> (the named window first). Every window is searched
/// under the one deadline, the one maxResults and the one candidate limit (each window's search gets what the windows before
/// it left, and the popups searched before the named window leave it <see cref="NamedWindowCandidateReserve"/>), so a scope
/// never multiplies the cost and a large popup cannot crowd out the window the caller named. Each match is registered under
/// its own top-level window, the window a resolution searches. Only the named window is warmed up and searched below
/// withinRef; it not responding or not being found fails the call as before, while another window that does not respond,
/// closed meanwhile, fails or does not answer in time while the deadline has not passed is skipped and counted
/// (truncationReason PartsSkipped), and the search goes on with the next window. More popups than
/// <see cref="PopupService.MaxPopups"/> is truncationReason WindowLimit. Uniqueness (below) is judged per window, and the
/// extra searches that prove single visible matches unique share one <see cref="UniquenessBudget"/>, the named window's
/// first. Each window's search covers its own tree: UI Automation also lists the windows it owns (its dialogs) among its
/// children, and they are left out (<see cref="OwnedWindows"/>), in every scope, so an owned dialog is searched only as
/// its own window (a popup of windowAndPopups); an element that still comes from two windows of the scope (an owner whose
/// tree could not be split) is kept once, from the first.</para>
/// <para>A search of the window scope is one worker read, counted against the window's application. A search of several
/// windows reads each window on its own, counted against that window's application (roadmap M3.F13,
/// ElementSearchService.Windows.cs), so a provider stuck in another window of the scope, such as a WebView2 child process or
/// one of the desktop's windows, counts against its own application and not the named window's; a window whose
/// application has too many stuck reads is skipped and counted, as one that does not respond is.</para>
/// <para>A search the managed client cannot express (AppBar or SemanticZoom) runs again with the control types tested on
/// the server. Reads of a match's details that fail leave those fields out; once the deadline has passed, or a provider did
/// not answer in time, the remaining matches are described from what the search read (no further call). A read the worker
/// abandons still returns the matches found until then, described the same way, with truncationReason Timeout.</para>
/// <para><b>searchVirtualized</b> (roadmap M3.10, ElementSearchService.Virtualized.cs): after a tree search of the named
/// window that finished with room left among maxResults, its lists (List, Tree, DataGrid, Table; withinRef's element itself
/// when it is one) that support the ItemContainer pattern are asked for the items whose automation id or whole name equals the
/// criterion, realized or not, one FindItemByProperty call per item, within the same deadline. Items the tree search did not
/// find (by runtime id) are added with virtualized true and a ref. Only reads: realizing and scrolling are element_action's.</para>
/// </remarks>
public sealed partial class ElementSearchService : IElementSearchService
{
    /// <summary>The most matches one call returns.</summary>
    public const int MaxResultsLimit = 100;

    /// <summary>The default timeout of a search.</summary>
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(5);

    /// <summary>
    /// The share of the candidate limit the popups searched first leave to the named window of a windowAndPopups search,
    /// so a popup with thousands of elements cannot keep the window the caller named from being searched.
    /// </summary>
    public const int NamedWindowCandidateReserve = ElementSearch.MaxCandidates / 2;

    /// <summary>
    /// The most time the searches that prove single matches unique may take together, one per window (they end earlier at
    /// the deadline).
    /// </summary>
    public static readonly TimeSpan UniquenessBudget = TimeSpan.FromSeconds(1);

    /// <summary>How many levels above a list item its list (the ItemContainer pattern) is looked for when proving it alone.</summary>
    private const int MaxItemContainerLevels = 3;

    /// <summary>How long to keep waiting after the timeout for the search to stop and hand back what it found.</summary>
    private static readonly TimeSpan StopGrace = TimeSpan.FromSeconds(2);

    private readonly IUiaTree _tree;
    private readonly IUiaWorker _worker;
    private readonly IElementRegistry _registry;
    private readonly INativeWindowWrapper? _windows;
    private readonly IAccessibilityWarmup? _warmup;
    private readonly IPopupService? _popups;
    private readonly ITargetPolicy? _policy;

    /// <param name="tree">The UI Automation tree.</param>
    /// <param name="worker">Runs the search under a deadline.</param>
    /// <param name="registry">Gives the matches their refs and resolves withinRef.</param>
    /// <param name="windows">The hung-window check and the application a stuck read is counted against; without it neither
    /// is done.</param>
    /// <param name="warmup">Warms up the web content of Chromium, Electron and WebView2 windows before they are searched
    /// (roadmap M3.3). The server always passes it; without it no window is warmed up.</param>
    /// <param name="popups">The windows of the windowAndPopups and desktop scopes (roadmap M3.5). The server always passes
    /// it; without it every search covers the window alone.</param>
    /// <param name="policy">The user's target policy, which refuses a search of a window of an app it denies. The server always
    /// passes it; without it (or without <paramref name="windows"/>) no named window is refused.</param>
    public ElementSearchService(
        IUiaTree tree,
        IUiaWorker worker,
        IElementRegistry registry,
        INativeWindowWrapper? windows = null,
        IAccessibilityWarmup? warmup = null,
        IPopupService? popups = null,
        ITargetPolicy? policy = null)
    {
        _tree = tree;
        _worker = worker;
        _registry = registry;
        _windows = windows;
        _warmup = warmup;
        _popups = popups;
        _policy = policy;
    }

    /// <inheritdoc />
    public async Task<FindElementsResult> FindAsync(ElementQuery query, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        var stopwatch = Stopwatch.StartNew();
        var handle = query.WindowHandle;
        var started = new SearchStarted { MaxResults = Math.Clamp(query.MaxResults, 1, MaxResultsLimit) };

        FindElementsResult Failure(string error, UIElementErrorCode code, string? title = null) =>
            new(false, (long)handle, title, 0, false, null, Elapsed(stopwatch), query.WithinRef, Error: error, ErrorCode: code,
                AccessibilityWarmup: started.Warmup);

        var scope = ScopeOf(query);
        if (handle == nint.Zero && scope != ElementSearchScope.Desktop)
        {
            return Failure("Invalid window handle (zero)", UIElementErrorCode.InvalidHandle);
        }

        if (VirtualizedArgumentError(query) is { } virtualizedError)
        {
            throw new ArgumentException(virtualizedError, nameof(query));
        }

        if (UIElementService.ParseControlTypes(query.ControlTypes, out var controlTypes) is { } invalidControlTypes)
        {
            return Failure(invalidControlTypes, UIElementErrorCode.InvalidControlType);
        }

        Regex? nameRegex = null;
        if (query.NameMatch == ElementNameMatch.Regex && !string.IsNullOrEmpty(query.Name))
        {
            nameRegex = ElementCriteria.CompileNameRegex(query.Name, out var regexError) ??
                        throw new ArgumentException(regexError, nameof(query));
        }

        var criteria = new ElementCriteria(
            Name: string.IsNullOrEmpty(query.Name) ? null : query.Name,
            NameMatch: query.NameMatch,
            NameRegex: nameRegex,
            AutomationId: string.IsNullOrEmpty(query.AutomationId) ? null : query.AutomationId,
            ControlTypes: controlTypes?.Order(StringComparer.Ordinal).ToList(),
            ClassName: string.IsNullOrEmpty(query.ClassName) ? null : query.ClassName,
            VisibleOnly: query.VisibleOnly,
            EnabledOnly: query.EnabledOnly);
        var maxResults = Math.Clamp(query.MaxResults, 1, MaxResultsLimit);
        var timeout = query.Timeout ?? DefaultTimeout;

        // Before the worker: no warm-up and no UI Automation call reaches a window of an app the user denies, named or not
        if (UiaTargets.PolicyRefusal(_windows, _policy, handle) is { } denied)
        {
            return Failure(denied, UIElementErrorCode.TargetDeniedByPolicy);
        }

        var call = new SearchCall(query, scope, criteria, maxResults, started, stopwatch);
        var warmupBudget = AccessibilityWarmup.BudgetFor(timeout);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(timeout);
        try
        {
            // A scope of several windows reads each one on its own, counted against its own application (roadmap M3.F13)
            return scope == ElementSearchScope.Window
                ? await _worker.RunAsync(TargetOf(handle), token => Find(call, warmupBudget, token), deadline.Token, StopGrace)
                    .ConfigureAwait(false)
                : await FindAcrossAsync(call, warmupBudget, deadline.Token).ConfigureAwait(false);
        }
        catch (RegexMatchTimeoutException)
        {
            // Also a TimeoutException, but the name pattern's, not UI Automation's: the tool reports InvalidArguments
            throw;
        }
        catch (TimeoutException) when (cancellationToken.IsCancellationRequested)
        {
            throw new OperationCanceledException(cancellationToken);
        }
        catch (UiaReadRefusedException ex)
        {
            // Earlier reads of the named window's application are still stuck; nothing was waited for
            return Failure(ex.Message, UIElementErrorCode.OperationTimedOut);
        }
        catch (TimeoutException ex)
        {
            // The worker gave up on a search stuck in an application that does not answer: what it found is still returned,
            // described from what the search read
            return started.Snapshot() is { MatchCount: > 0 } partial
                ? Result(query, query.WithinRef, started.Title, partial with { Stop = ElementSearchStop.Timeout },
                    Describe(query, criteria, partial.Matches, started.Generation, mayRead: false, _ => null, default), started.Warmup,
                    stopwatch) with { Scope = scope }
                : Failure($"UI Automation did not answer in time: {ex.Message}", UIElementErrorCode.OperationTimedOut, started.Title);
        }
    }

    /// <summary>
    /// The scope a query searches: the window alone below withinRef, the scope asked for, or the default (windowAndPopups
    /// when the window, a window it owns or another window of its process is in front, else window). Without the popup
    /// service, the window alone.
    /// </summary>
    private ElementSearchScope ScopeOf(ElementQuery query)
    {
        if (query.WithinRef != null || _popups == null)
        {
            return ElementSearchScope.Window;
        }

        if (query.WindowHandle == nint.Zero)
        {
            // Only a desktop search may name no window; any other is an invalid handle, as it always was
            return query.Scope ?? ElementSearchScope.Window;
        }

        return query.Scope ?? (_popups.IsInForeground(query.WindowHandle) ? ElementSearchScope.WindowAndPopups : ElementSearchScope.Window);
    }

    /// <summary>
    /// The windows a scope covers, in the order searched, how many it left out (the agent's host windows and the windows of
    /// apps the target policy denies, see <see cref="PopupList.ForReading"/>), and how many were over its limit (for
    /// windowAndPopups, 1 when more popups were open than <see cref="PopupService.MaxPopups"/>).
    /// </summary>
    private (IReadOnlyList<nint> Windows, int Excluded, int OverLimit) WindowsOf(ElementQuery query, ElementSearchScope scope)
    {
        var handle = query.WindowHandle;
        return scope switch
        {
            ElementSearchScope.WindowAndPopups when _popups != null =>
                _popups.GetPopupList(handle) is { } list && list.ForReading(query.IncludeHostPopups) is var (popups, excluded)
                    ? ([.. popups.Select(p => p.Handle), handle], excluded, list.Truncated ? 1 : 0)
                    : ([handle], 0, 0),
            ElementSearchScope.Desktop when _popups != null =>
                _popups.GetDesktopWindows(handle, PopupService.DesktopWindowLimit) is var set ? (set.Windows, set.Excluded, set.OverLimit) : default,
            _ => ([handle], 0, 0)
        };
    }

    /// <summary>The search of the window scope, in one read on the worker thread.</summary>
    private FindElementsResult Find(SearchCall call, TimeSpan warmupBudget, CancellationToken token) =>
        Guarded(call, token, () =>
        {
            if (Open(call, warmupBudget, token) is { } failure)
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

                if (!SearchWindow(call, index, candidatesLeft, token))
                {
                    break;
                }
            }

            if (Gathered(call) is { } timedOut)
            {
                return timedOut;
            }

            // searchVirtualized (roadmap M3.10): the named window's lists, asked for items they have not realized
            var virtualized = Virtualized(call, token);
            var unique = MayProveUnique(call, virtualized) ? Uniqueness(call, token) : _ => null;
            var result = Result(call, Describe(call.Query, call.Criteria, call.Total.Matches, call.Generation, mayRead: true, unique, token));
            if (virtualized != null)
            {
                result = WithVirtualized(result, virtualized, call.Query, call.Criteria, call.Generation, token);
            }

            return Finished(call, result);
        })!;

    /// <summary>
    /// The start of the search, on the worker thread: the named window's hung check, node, warm-up and title, withinRef
    /// resolved, and the scope's windows. A failure ends the call; null when the windows can be searched.
    /// </summary>
    private FindElementsResult? Open(SearchCall call, TimeSpan warmupBudget, CancellationToken token)
    {
        var query = call.Query;
        var handle = query.WindowHandle;
        var started = call.Started;
        if (handle != nint.Zero &&
            UiaTargets.NotRespondingReason(_windows, handle, "its UI elements were not searched") is { } notResponding)
        {
            return call.Failure(notResponding, UIElementErrorCode.NotResponding);
        }

        if (!_tree.IsAvailable())
        {
            return call.Failure("UI Automation is not available on this system", UIElementErrorCode.AutomationUnavailable);
        }

        IUiaNode? window = null;
        if (handle != nint.Zero)
        {
            try
            {
                window = _tree.FromHandle(handle);
            }
            catch (UiaElementUnavailableException)
            {
                window = null;
            }

            if (window == null)
            {
                return call.Failure("Window not found or handle is invalid", UIElementErrorCode.WindowNotFound);
            }

            // A window with web content builds its page's tree only once asked: ask, and wait for it a moment, so the
            // search does not miss the page's elements
            started.Warmup = query.WarmUp ? _warmup?.WarmUp(handle, warmupBudget, token) : null;
            started.Title = TryReadName(window);
        }

        var title = started.Title;
        call.Root = window;
        call.WithinRef = query.WithinRef;
        if (query.WithinRef != null)
        {
            var within = _registry.Resolve(query.WithinRef, token);
            if (within.Element is not { } resolved)
            {
                return call.Failure($"withinRef: {within.Error}", within.ErrorCode, title);
            }

            if (resolved.WindowHandle != handle)
            {
                return call.Failure(
                    $"withinRef '{resolved.Ref}' is an element of window {(long)resolved.WindowHandle}, not of the window searched " +
                    $"({(long)handle}). Pass only withinRef, or the window it belongs to.",
                    UIElementErrorCode.ElementStale, title);
            }

            call.Root = resolved.Node;

            // The ref the element has now: when it was found again and already had a ref of its own, the asked-for one
            // was dropped
            call.WithinRef = resolved.Ref;

            // Searched below the element the ref was moved to, and said so: the element the ref was given for is gone
            call.WithinMoved = resolved.Moved;
        }

        started.Generation = _registry.NextGeneration();
        (call.Windows, call.Excluded, call.OverLimit) = WindowsOf(query, call.Scope);
        call.Compiled = call.Criteria.Compile();
        return null;
    }

    /// <summary>
    /// The candidates the search of the scope's window at <paramref name="index"/> may use, or null when it is not searched
    /// and the search has stopped: then <paramref name="end"/> says whether the windows after it are not searched either (the
    /// deadline, the candidate limit) or whether this window only had the named window's share left.
    /// </summary>
    private static int? CandidatesFor(SearchCall call, int index, CancellationToken token, out bool end)
    {
        end = true;
        var started = call.Started;
        if (token.IsCancellationRequested)
        {
            started.Stop(ElementSearchStop.Timeout);
            return null;
        }

        var candidatesLeft = ElementSearch.MaxCandidates - started.Candidates;
        if (candidatesLeft <= 0)
        {
            started.Stop(ElementSearchStop.CandidateLimit);
            return null;
        }

        // The popups searched before the named window leave it a share of the candidate limit, so a large palette cannot
        // keep the window the caller named from being searched
        var handle = call.Query.WindowHandle;
        if (call.Windows[index] != handle && handle != nint.Zero && call.Windows.Skip(index + 1).Contains(handle))
        {
            candidatesLeft -= NamedWindowCandidateReserve;
            if (candidatesLeft <= 0)
            {
                started.Stop(ElementSearchStop.CandidateLimit);
                end = false;
                return null;
            }
        }

        end = false;
        return candidatesLeft;
    }

    /// <summary>
    /// The search of the scope's window at <paramref name="index"/>, on the worker thread; false when the search ends with
    /// it. Another window than the named one that cannot be opened or searched is skipped and counted (see the remarks).
    /// </summary>
    private bool SearchWindow(SearchCall call, int index, int candidatesLeft, CancellationToken token)
    {
        var started = call.Started;
        var target = call.Windows[index];
        var isNamed = target == call.Query.WindowHandle;
        var node = isNamed ? call.Root : OpenOther(target);
        if (node == null)
        {
            if (token.IsCancellationRequested)
            {
                started.Stop(ElementSearchStop.Timeout);
                return false;
            }

            started.SkipWindow();
            return true;
        }

        var keep = Math.Max(1, call.MaxResults - started.Kept);

        // A window's own tree: the windows it owns are searched as windows of their own (below withinRef there are none)
        var leaveOut = isNamed && call.Query.WithinRef != null ? null : OwnedWindows.Below(_windows, target);
        ElementSearchOutcome outcome;
        try
        {
            try
            {
                outcome = Run(call.Compiled, keep, candidatesLeft, target, node, leaveOut, started, token);
            }
            catch (UiaConditionNotSupportedException) when (call.Criteria.ControlTypes is { Count: > 0 } && !call.ControlTypesOnServer)
            {
                // The managed client cannot search for AppBar or SemanticZoom: test the control types here, in this window
                // and the next ones
                call.ControlTypesOnServer = true;
                call.Compiled = call.Criteria.Compile(controlTypesOnServer: true);
                outcome = Run(call.Compiled, keep, candidatesLeft, target, node, leaveOut, started, token);
            }
        }
        catch (Exception ex) when (!isNamed && !token.IsCancellationRequested &&
                                   ex is not (OperationCanceledException or RegexMatchTimeoutException))
        {
            // A popup that closed meanwhile, or whose provider failed or did not answer in time: the other windows are
            // still searched
            started.Abandon(target);
            started.SkipWindow();
            return true;
        }

        if (!isNamed && outcome.Stop == ElementSearchStop.Timeout && !token.IsCancellationRequested)
        {
            // A popup whose provider did not answer in time, with time left: a part skipped, not the end of the search
            outcome = outcome with { Stop = ElementSearchStop.None, SkippedParts = outcome.SkippedParts + 1 };
        }

        started.Complete(target, node, outcome);
        return outcome.Stop == ElementSearchStop.None || (!isNamed && !token.IsCancellationRequested);
    }

    /// <summary>
    /// What the windows' searches found together, kept as <see cref="SearchCall.Total"/>; a failure when the deadline
    /// passed before any match.
    /// </summary>
    private static FindElementsResult? Gathered(SearchCall call)
    {
        var total = call.Total = call.Started.Snapshot();
        return total.MatchCount == 0 && total.Stop == ElementSearchStop.Timeout
            ? call.Failure(
                $"The timeout passed before any element matching {call.Criteria.Describe()} was found ({total.Candidates} " +
                "candidates checked). Try again with a larger timeoutMs, or search a smaller part with withinRef.",
                UIElementErrorCode.OperationTimedOut, call.Started.Title)
            : null;
    }

    /// <summary>searchVirtualized's lookup in the named window's lists, on the worker thread; null when not asked.</summary>
    private VirtualizedLookup? Virtualized(SearchCall call, CancellationToken token) =>
        call.Query.SearchVirtualized && call.Root != null
            ? SearchVirtualized(call.Criteria, call.Query.WindowHandle, call.Root, call.Query.WithinRef != null, call.Total,
                call.MaxResults, token)
            : null;

    /// <summary>
    /// Whether the matches may be proven unique: not below withinRef, and not when items found through a list (look-alikes
    /// of a tree match), or a lookup that stopped early and may have missed one, came with them.
    /// </summary>
    private static bool MayProveUnique(SearchCall call, VirtualizedLookup? virtualized) =>
        call.Query.WithinRef == null && virtualized is not ({ Items.Count: > 0 } or { Stop: not null });

    private static FindElementsResult Result(SearchCall call, IReadOnlyList<FoundElement> elements) =>
        Result(call.Query, call.WithinRef, call.Started.Title, call.Total, elements, call.Started.Warmup, call.Stopwatch);

    /// <summary>The result with withinRef's move and the scope's fields.</summary>
    private static FindElementsResult Finished(SearchCall call, FindElementsResult result)
    {
        if (call.WithinMoved)
        {
            result = result with { RefMoved = true, Warning = "withinRef: " + ElementRefs.MovedWarning(call.WithinRef!) };
        }

        return call.Scope == ElementSearchScope.Window
            ? result with { Scope = call.Scope }
            : result with
            {
                Scope = call.Scope,
                WindowsSearched = call.Started.Searched.Count,
                WindowsExcluded = call.Excluded > 0 ? call.Excluded : null,
                Truncated = result.Truncated || call.OverLimit > 0,
                TruncationReason = result.TruncationReason is { } reason && reason != ElementSearchTruncation.MaxResults
                    ? reason
                    : call.OverLimit > 0 ? ElementSearchTruncation.WindowLimit : result.TruncationReason
            };
    }

    /// <summary>
    /// A read of the search on the worker thread, with UI Automation's failures turned into the call's failure: what
    /// <paramref name="read"/> returns (a failure, or null to go on).
    /// </summary>
    private static FindElementsResult? Guarded(SearchCall call, CancellationToken token, Func<FindElementsResult?> read)
    {
        try
        {
            return read();
        }
        catch (UiaElementUnavailableException ex)
        {
            return call.Failure($"The element searched below is no longer available: {ex.Message}", UIElementErrorCode.ElementAccessFailed);
        }
        catch (RegexMatchTimeoutException)
        {
            // The tool turns it into InvalidArguments
            throw;
        }
        catch (TimeoutException ex)
        {
            return call.Failure($"UI Automation did not answer in time: {ex.Message}", UIElementErrorCode.OperationTimedOut);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            return call.Failure("The timeout passed before the search started. Try again with a larger timeoutMs.",
                UIElementErrorCode.OperationTimedOut);
        }
        catch (Exception ex)
        {
            return call.Failure($"Error searching UI elements: {ex.Message}", UIElementErrorCode.OperationException);
        }
    }

    /// <summary>The application a stuck read of <paramref name="window"/> is counted against (none for no window).</summary>
    private int TargetOf(nint window) => window == nint.Zero ? 0 : UiaTargets.TargetProcessId(_windows, window);

    /// <summary>
    /// The node of a window of the scope other than the named one, or null when it must be skipped: it does not respond
    /// (it would hold the search until the deadline), it closed, UI Automation cannot read it, or its provider did not
    /// answer in time (a slow WebView2 popup; the caller tells the deadline apart by its token).
    /// </summary>
    private IUiaNode? OpenOther(nint window)
    {
        if (UiaTargets.NotRespondingReason(_windows, window) != null)
        {
            return null;
        }

        try
        {
            return _tree.FromHandle(window);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return null;
        }
    }

    private ElementSearchOutcome Run(
        (UiaCondition Condition, Func<UiaElement, bool>? Filter) compiled, int keep, int maxCandidates, nint window, IUiaNode root,
        Func<UiaElement, bool>? leaveOut, SearchStarted started, CancellationToken token)
    {
        var search = new ElementSearch(_tree, compiled.Condition, compiled.Filter, keep, maxCandidates, leaveOut);
        started.Begin(window, search);
        return search.Run(root, token);
    }

    /// <summary>
    /// Whether the criteria fit each kept match alone in its window (see the remarks): true, false, or null when that is
    /// not known. Each searched window's outcome is a search of that whole window (or of withinRef's element, for which the
    /// caller asks nothing).
    /// </summary>
    private Func<UiaElement, bool?> Uniqueness(SearchCall call, CancellationToken token)
    {
        // One budget for every window's proof, the named window's first
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(token);
        budget.CancelAfter(UniquenessBudget);
        var verdicts = Verdicts(call, call.Started.Searched, budget.Token);
        return m => verdicts.TryGetValue(m, out var verdict) ? verdict : null;
    }

    /// <summary>
    /// The verdicts of <see cref="Uniqueness"/> for the matches kept from <paramref name="searched"/>, the named window's
    /// first, within <paramref name="budget"/>; a match without one is not known.
    /// </summary>
    private Dictionary<UiaElement, bool?> Verdicts(SearchCall call, IReadOnlyList<SearchedWindow> searched, CancellationToken budget)
    {
        var criteria = call.Criteria;
        var verdicts = new Dictionary<UiaElement, bool?>(ReferenceEqualityComparer.Instance);
        foreach (var window in searched.OrderBy(w => w.Handle == call.Query.WindowHandle ? 0 : 1))
        {
            var outcome = window.Outcome;
            var matches = window.Kept;
            var twins = new HashSet<UiaElement>(ReferenceEqualityComparer.Instance);
            for (var i = 0; i < outcome.Matches.Count; i++)
            {
                for (var j = i + 1; j < outcome.Matches.Count; j++)
                {
                    if (ElementRegistry.SameElement(ElementRegistry.Fingerprint(outcome.Matches[i], null), outcome.Matches[j]))
                    {
                        twins.Add(outcome.Matches[i]);
                        twins.Add(outcome.Matches[j]);
                    }
                }
            }

            bool? Unproven(UiaElement m) => twins.Contains(m) ? false : null;
            if (!outcome.Complete)
            {
                foreach (var match in matches)
                {
                    verdicts[match] = Unproven(match);
                }

                continue;
            }

            if (!criteria.VisibleOnly && !criteria.EnabledOnly)
            {
                // Every element the criteria fit in this window was a match; when all were kept, the ones without a twin are
                // alone in it
                var allKept = outcome.MatchCount == matches.Count;
                foreach (var match in matches)
                {
                    verdicts[match] = twins.Contains(match) ? false : allKept ? true : null;
                }

                continue;
            }

            if (outcome.MatchCount != 1 || matches.Count != 1 || budget.IsCancellationRequested)
            {
                foreach (var match in matches)
                {
                    verdicts[match] = Unproven(match);
                }

                continue;
            }

            // A single match in its window among the visible or enabled ones: an offscreen or disabled twin would not have
            // been found, so search the window for its identity alone
            verdicts[matches[0]] = ProveAlone(criteria.Identity, call.ControlTypesOnServer, matches[0], window, budget);
        }

        // A list item named by its text (no automation id): its look-alikes may be rows the list has not realized, which no
        // tree search sees, so the list itself is asked
        foreach (var match in verdicts.Where(v => v.Value == true).Select(v => v.Key).ToList())
        {
            if (ElementRegistry.NameIsIdentity(ElementRegistry.Fingerprint(match, null)))
            {
                verdicts[match] = ProveAmongItems(match, budget);
            }
        }

        return verdicts;
    }

    /// <summary>
    /// For a list, tree or grid item named by its text that the tree proved alone: whether its list (the nearest ancestor with
    /// the ItemContainer pattern, at most <see cref="MaxItemContainerLevels"/> up) holds no other item of that name, realized
    /// or not (two FindItemByProperty calls). Null when that cannot be told: no such list while the item is virtualized (a
    /// list that does not realize all its rows and cannot be asked), a provider that fails, or the budget. An item of a list
    /// that does not virtualize keeps the tree's proof.
    /// </summary>
    private bool? ProveAmongItems(UiaElement item, CancellationToken budget)
    {
        try
        {
            var current = item.Node;
            for (var level = 0; level < MaxItemContainerLevels; level++)
            {
                budget.ThrowIfCancellationRequested();
                if (_tree.GetParent(current) is not { } parent)
                {
                    break;
                }

                var read = _tree.ReadElement(parent);
                if ((read.Patterns & UiaPatterns.ItemContainer) != 0)
                {
                    if (_tree.GetPattern<IUiaItemContainerPattern>(parent) is not { } list)
                    {
                        return null;
                    }

                    var name = item.Properties.Name ?? string.Empty;
                    if (list.FindItemByProperty(null, UiaProperty.Name, name) is not { } first)
                    {
                        return null;
                    }

                    budget.ThrowIfCancellationRequested();
                    if (list.FindItemByProperty(first, UiaProperty.Name, name) != null)
                    {
                        return false;
                    }

                    return _tree.AreSameElement(first, item.Node) ? true : null;
                }

                if (ElementRows.EndsTheSearch(read.Properties.ControlType))
                {
                    break;
                }

                current = parent;
            }

            return (item.Patterns & UiaPatterns.VirtualizedItem) != 0 ? null : true;
        }
        catch (Exception)
        {
            // Best effort: not proven (the budget, an item that is gone, a provider failure)
            return null;
        }
    }

    /// <summary>
    /// True when one more search of the window finds no other element that the criteria and the match's fingerprint fit,
    /// false when it finds one, null when it could not tell.
    /// </summary>
    /// <param name="window">The window searched: its own tree, without the windows it owns (as the search and the
    /// registry's resolution search it).</param>
    /// <param name="budget">The deadline, or the end of <see cref="UniquenessBudget"/> when that comes first.</param>
    private bool? ProveAlone(ElementCriteria identity, bool controlTypesOnServer, UiaElement match, SearchedWindow window, CancellationToken budget)
    {
        try
        {
            var (condition, filter) = identity.Compile(controlTypesOnServer);
            var fingerprint = ElementRegistry.Fingerprint(match, null);
            var proof = new ElementSearch(
                    _tree, condition, e => (filter == null || filter(e)) && ElementRegistry.SameElement(fingerprint, e), keep: 2,
                    leaveOut: OwnedWindows.Below(_windows, window.Handle))
                .Run(window.Node, budget);
            return proof.MatchCount switch
            {
                > 1 => false,
                1 when proof.Complete && proof.Matches[0].RuntimeId is { } id && match.RuntimeId is { } matchId &&
                       id.SequenceEqual(matchId) => true,
                _ => null
            };
        }
        catch (Exception)
        {
            // Best effort: not proven (a name pattern that ran too long on an offscreen name, a provider failure)
            return null;
        }
    }

    private static FindElementsResult Result(
        ElementQuery query, string? withinRef, string? title, SearchTotal total, IReadOnlyList<FoundElement> elements,
        AccessibilityWarmupInfo? warmup, Stopwatch stopwatch)
    {
        ElementSearchTruncation? reason = total.Stop switch
        {
            ElementSearchStop.Timeout => ElementSearchTruncation.Timeout,
            ElementSearchStop.CandidateLimit => ElementSearchTruncation.SearchLimit,
            _ when total.SkippedParts > 0 => ElementSearchTruncation.PartsSkipped,
            _ when total.MatchCount > elements.Count => ElementSearchTruncation.MaxResults,
            _ => null
        };

        return new FindElementsResult(
            Success: true,
            WindowHandle: (long)query.WindowHandle,
            WindowTitle: title,
            MatchCount: total.MatchCount,
            Truncated: reason != null,
            Elements: elements,
            ElapsedMilliseconds: Elapsed(stopwatch),
            WithinRef: withinRef,
            TruncationReason: reason,
            AccessibilityWarmup: warmup);
    }

    /// <summary>
    /// The kept matches as find_ui_elements returns them, each registered under the window it was found in, with the list
    /// row it is in (<see cref="ElementRows"/>: read up its ancestors, each ancestor once for all the matches). With
    /// <paramref name="mayRead"/> false (an abandoned search, read on the caller's thread) nothing is read: every field
    /// comes from what the search read, and no row is recorded.
    /// </summary>
    private List<FoundElement> Describe(
        ElementQuery query, ElementCriteria criteria, IReadOnlyList<(nint Window, UiaElement Element)> matches, long generation,
        bool mayRead, Func<UiaElement, bool?> unique, CancellationToken token)
    {
        var found = new List<FoundElement>(matches.Count);
        var rows = ElementRows.For(_tree);
        foreach (var (window, match) in matches)
        {
            ElementRow? row = null;
            if (mayRead && !token.IsCancellationRequested)
            {
                try
                {
                    row = rows.Of(match, token);
                }
                catch (TimeoutException)
                {
                    // A provider that does not answer: describe the rest from what the search read
                    mayRead = false;
                }
                catch (Exception)
                {
                    // An ancestor that is gone or failed, or the deadline: registered without its row
                }
            }

            string? elementRef = null;
            try
            {
                elementRef = _registry.Register(
                    window, match, new ElementOrigin(generation, criteria.Identity, Unique: unique(match), Row: row));
            }
            catch (Exception)
            {
                // Returned without a ref
            }

            var element = Minimal(match, elementRef);
            if (window != query.WindowHandle)
            {
                element = element with { WindowHandle = (long)window };
            }

            if (mayRead && !token.IsCancellationRequested)
            {
                try
                {
                    element = Read(element, match, query.Detail);
                }
                catch (TimeoutException)
                {
                    // A provider that does not answer: describe the rest from what the search read
                    mayRead = false;
                }
            }

            found.Add(element);
        }

        return found;
    }

    /// <summary>A match as detail minimal reports it, from what the search read (no further call); the click point is the
    /// centre of its bounds. Shared with wait_for_element (roadmap M3.8).</summary>
    internal static FoundElement Minimal(UiaElement match, string? elementRef)
    {
        var properties = match.Properties;
        var bounds = properties.Bounds.ToBounds();
        return new FoundElement(
            Ref: elementRef,
            Name: EmptyToNull(properties.Name),
            ControlType: properties.ControlType,
            AutomationId: EmptyToNull(properties.AutomationId),
            ClassName: EmptyToNull(match.ClassName),
            Bounds: bounds,
            ClickPoint: properties.IsOffscreen ? null : Centre(bounds),
            IsEnabled: properties.IsEnabled,
            IsOffscreen: properties.IsOffscreen,
            Patterns: UiaPatternNames.All(match.Patterns));
    }

    /// <summary>The click point, and the details <paramref name="detail"/> asks for, read now; a read that fails is left out.</summary>
    private FoundElement Read(FoundElement element, UiaElement match, ElementDetail detail)
    {
        var node = match.Node;
        if (!element.IsOffscreen && element.Bounds != null && Try(() => _tree.GetClickablePoint(node)) is { } point)
        {
            element = element with { ClickPoint = new ScreenPoint(point.X, point.Y) };
        }

        if (detail == ElementDetail.Minimal)
        {
            return element;
        }

        var patterns = match.Patterns;
        var identity = Try(() => _tree.ReadIdentity(node));
        var interaction = Try(() => _tree.ReadInteraction(node));
        var isPassword = identity?.IsPassword == true;

        // A password field's value is never read, not even to mask it
        var (value, truncated) = !isPassword && identity != null && (patterns & UiaPatterns.Value) != 0
            ? ElementInspectionService.Truncate(Try(() => _tree.ReadValue(node)))
            : (null, false);

        element = element with
        {
            Value = value,
            ValueTruncated = truncated ? true : null,
            IsPassword = isPassword ? true : null,
            ToggleState = (patterns & UiaPatterns.Toggle) != 0
                ? TryState(() => _tree.GetPattern<IUiaTogglePattern>(node)?.ToggleState)
                : null,
            ExpandCollapseState = (patterns & UiaPatterns.ExpandCollapse) != 0
                ? TryState(() => _tree.GetPattern<IUiaExpandCollapsePattern>(node)?.ExpandCollapseState)
                : null,
            IsSelected = (patterns & UiaPatterns.SelectionItem) != 0
                ? TryState(() => _tree.GetPattern<IUiaSelectionItemPattern>(node)?.IsSelected)
                : null,
            HasKeyboardFocus = interaction?.HasKeyboardFocus,
            IsKeyboardFocusable = interaction?.IsKeyboardFocusable ?? match.IsKeyboardFocusable
        };

        if (detail != ElementDetail.Full)
        {
            return element;
        }

        var description = Try(() => _tree.ReadDescription(node));
        return element with
        {
            ClassName = element.ClassName ?? EmptyToNull(identity?.ClassName),
            FrameworkId = EmptyToNull(identity?.FrameworkId),
            ProcessId = identity is { ProcessId: > 0 } ? identity.ProcessId : null,
            NativeWindowHandle = identity is { NativeWindowHandle: var hwnd } && hwnd != nint.Zero ? (long)hwnd : null,
            HelpText = EmptyToNull(description?.HelpText),
            AccessKey = EmptyToNull(description?.AccessKey),
            AcceleratorKey = EmptyToNull(description?.AcceleratorKey),
            LocalizedControlType = EmptyToNull(description?.LocalizedControlType)
        };
    }

    /// <summary>A read of a match's details; null when it fails. A timeout is thrown: it ends the reads.</summary>
    private static T? Try<T>(Func<T?> read) where T : class
    {
        try
        {
            return read();
        }
        catch (Exception ex) when (ex is not TimeoutException)
        {
            return null;
        }
    }

    private static T? Try<T>(Func<T?> read) where T : struct
    {
        try
        {
            return read();
        }
        catch (Exception ex) when (ex is not TimeoutException)
        {
            return null;
        }
    }

    private static T? TryState<T>(Func<T?> read) where T : struct => Try(read);

    private static ScreenPoint? Centre(BoundsDto? bounds) =>
        bounds is { Width: > 0, Height: > 0 } b ? new ScreenPoint(b.X + b.Width / 2, b.Y + b.Height / 2) : null;

    private string? TryReadName(IUiaNode node)
    {
        try
        {
            return EmptyToNull(_tree.Read(node).Name);
        }
        catch (Exception ex) when (ex is not TimeoutException)
        {
            return null;
        }
    }

    private static string? EmptyToNull(string? value) => string.IsNullOrEmpty(value) ? null : value;

    private static double Elapsed(Stopwatch stopwatch) => Math.Round(stopwatch.Elapsed.TotalMilliseconds, 2);

    /// <summary>
    /// One call's search: what it was asked, and what each step found for the steps after it (one read on the worker thread
    /// for the window scope, a read per step and window for the others; one at a time).
    /// </summary>
    private sealed class SearchCall(
        ElementQuery query, ElementSearchScope scope, ElementCriteria criteria, int maxResults, SearchStarted started, Stopwatch stopwatch)
    {
        public ElementQuery Query => query;

        public ElementSearchScope Scope => scope;

        public ElementCriteria Criteria => criteria;

        public int MaxResults => maxResults;

        public SearchStarted Started => started;

        public Stopwatch Stopwatch => stopwatch;

        public long Generation => started.Generation;

        /// <summary>The named window's node, or withinRef's element; null for a desktop search that names no window.</summary>
        public IUiaNode? Root { get; set; }

        /// <summary>The ref searched below, as it is now (see <see cref="ResolvedElement.Moved"/>).</summary>
        public string? WithinRef { get; set; }

        public bool WithinMoved { get; set; }

        /// <summary>The windows of the scope, in the order searched, and how many it left out (see <see cref="WindowsOf"/>).</summary>
        public IReadOnlyList<nint> Windows { get; set; } = [];

        public int Excluded { get; set; }

        public int OverLimit { get; set; }

        /// <summary>The criteria as the provider's condition and the server's filter.</summary>
        public (UiaCondition Condition, Func<UiaElement, bool>? Filter) Compiled { get; set; }

        /// <summary>The control types are tested on the server (the managed client cannot search for AppBar or SemanticZoom).</summary>
        public bool ControlTypesOnServer { get; set; }

        /// <summary>What every window's search found, once they are all done (see <see cref="Gathered"/>).</summary>
        public SearchTotal Total { get; set; } = new([], 0, 0, ElementSearchStop.None, 0);

        public FindElementsResult Failure(string error, UIElementErrorCode code, string? title = null) =>
            new(false, (long)query.WindowHandle, title, 0, false, null, Elapsed(stopwatch), query.WithinRef, Error: error,
                ErrorCode: code, AccessibilityWarmup: started.Warmup);
    }

    /// <summary>A window of the scope, searched: its node, its search's outcome and the matches kept from it.</summary>
    /// <param name="Repeated">The outcome's kept matches that a window searched before had kept already (the same runtime
    /// id): not kept again, and not counted.</param>
    private sealed record SearchedWindow(
        nint Handle, IUiaNode Node, ElementSearchOutcome Outcome, IReadOnlyList<UiaElement> Kept, int Repeated = 0);

    /// <summary>What the searches of every window of the scope found, together.</summary>
    /// <param name="Matches">The kept matches, each with the window it was found in, in the order searched.</param>
    /// <param name="SkippedParts">Parts of the windows that could not be searched, plus the windows that could not be.</param>
    private sealed record SearchTotal(
        IReadOnlyList<(nint Window, UiaElement Element)> Matches, int MatchCount, int Candidates, ElementSearchStop Stop, int SkippedParts);

    /// <summary>
    /// The search of one call once it has started: the windows searched so far and the search running, for the caller when
    /// the worker abandons it, and the totals the next window's search starts from (what is left of maxResults and of the
    /// candidate limit).
    /// </summary>
    private sealed class SearchStarted
    {
        private readonly object _gate = new();
        private readonly List<SearchedWindow> _searched = [];
        private (nint Window, ElementSearch Search)? _current;
        private int _skippedWindows;
        private ElementSearchStop _stop;
        private string? _title;
        private long _generation;
        private AccessibilityWarmupInfo? _warmup;

        /// <summary>How many matches the call returns, from all its windows together.</summary>
        public int MaxResults { get; init; } = MaxResultsLimit;

        public string? Title
        {
            get => Volatile.Read(ref _title);
            set => Volatile.Write(ref _title, value);
        }

        public long Generation
        {
            get => Interlocked.Read(ref _generation);
            set => Interlocked.Exchange(ref _generation, value);
        }

        public AccessibilityWarmupInfo? Warmup
        {
            get => Volatile.Read(ref _warmup);
            set => Volatile.Write(ref _warmup, value);
        }

        /// <summary>The windows whose search ran, in order.</summary>
        public IReadOnlyList<SearchedWindow> Searched
        {
            get
            {
                lock (_gate)
                {
                    return _searched.ToList();
                }
            }
        }

        /// <summary>The matches kept from the windows searched.</summary>
        public int Kept
        {
            get
            {
                lock (_gate)
                {
                    return _searched.Sum(w => w.Kept.Count);
                }
            }
        }

        /// <summary>The provider's matches in the windows searched, before the name filter.</summary>
        public int Candidates
        {
            get
            {
                lock (_gate)
                {
                    return _searched.Sum(w => w.Outcome.Candidates);
                }
            }
        }

        public void Begin(nint window, ElementSearch search)
        {
            lock (_gate)
            {
                _current = (window, search);
            }
        }

        /// <summary>The running search of <paramref name="window"/> failed: what it found does not count.</summary>
        public void Abandon(nint window)
        {
            lock (_gate)
            {
                if (_current is { } current && current.Window == window)
                {
                    _current = null;
                }
            }
        }

        public void Complete(nint window, IUiaNode node, ElementSearchOutcome outcome)
        {
            lock (_gate)
            {
                var (kept, repeated) = Keep(outcome, _searched);
                _searched.Add(new SearchedWindow(window, node, outcome, kept, repeated));
                _current = null;
            }
        }

        /// <summary>
        /// What a window's search keeps: what is left of maxResults, without an element a window searched before kept
        /// already. The windows a window owns are left out of its search (<see cref="OwnedWindows"/>); a root that could not
        /// be split is searched whole, so an owned dialog can still come twice, and the first copy (a popup, searched before
        /// the window that owns it) is the one kept. Under the lock.
        /// </summary>
        private (List<UiaElement> Kept, int Repeated) Keep(ElementSearchOutcome outcome, IReadOnlyList<SearchedWindow> before)
        {
            var seen = before.SelectMany(w => w.Kept).Select(m => m.RuntimeId).OfType<IReadOnlyList<int>>().ToList();
            var fresh = outcome.Matches.Where(m => m.RuntimeId is not { Count: > 0 } id || !seen.Any(s => s.SequenceEqual(id))).ToList();
            var room = Math.Max(0, MaxResults - before.Sum(w => w.Kept.Count));
            return (fresh.Take(room).ToList(), outcome.Matches.Count - fresh.Count);
        }

        public void SkipWindow()
        {
            lock (_gate)
            {
                _skippedWindows++;
            }
        }

        /// <summary>The search stopped before the next window.</summary>
        public void Stop(ElementSearchStop stop)
        {
            lock (_gate)
            {
                if (_stop == ElementSearchStop.None)
                {
                    _stop = stop;
                }
            }
        }

        /// <summary>Everything found so far, the running search included; safe to call from any thread.</summary>
        public SearchTotal Snapshot()
        {
            lock (_gate)
            {
                var windows = _searched.Select(w => (w.Handle, w.Outcome, w.Kept, w.Repeated)).ToList();
                if (_current is { } current)
                {
                    var running = current.Search.Snapshot();
                    var (kept, repeated) = Keep(running, _searched);
                    windows.Add((current.Window, running, kept, repeated));
                }

                var stop = windows.Select(w => w.Outcome.Stop).FirstOrDefault(s => s != ElementSearchStop.None);
                return new SearchTotal(
                    Matches: windows.SelectMany(w => w.Kept.Select(m => (w.Handle, m))).ToList(),
                    MatchCount: windows.Sum(w => w.Outcome.MatchCount - w.Repeated),
                    Candidates: windows.Sum(w => w.Outcome.Candidates),
                    Stop: stop != ElementSearchStop.None ? stop : _stop,
                    SkippedParts: windows.Sum(w => w.Outcome.SkippedParts) + _skippedWindows);
            }
        }
    }
}
