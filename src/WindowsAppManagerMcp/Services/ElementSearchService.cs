using System.Diagnostics;
using System.Text.RegularExpressions;
using WindowsAppManagerMcp.Models;
using WindowsAppManagerMcp.Services.Interfaces;

namespace WindowsAppManagerMcp.Services;

/// <summary>find_ui_elements (roadmap M3.4); see <see cref="IElementSearchService"/>.</summary>
/// <remarks>
/// <para>Order of work, all in one read on the worker thread: the hung-window check (NotResponding), the window's node
/// (WindowNotFound), the warm-up of a window that shows web content (Chromium, Electron, WebView2: see
/// <see cref="IAccessibilityWarmup"/>, roadmap M3.3) within a third of the timeout, withinRef resolved through the
/// registry (its own codes: ElementStale, AmbiguousElement), the search
/// (<see cref="ElementSearch"/>, keeping the first maxResults matches and counting the rest), then for each kept match its
/// ref, its click point (UI Automation's clickable point, else the centre of its bounds; none for an offscreen element) and,
/// for detail standard or full, its state, each one call per property group.</para>
/// <para>Each match is registered with the search's identity criteria as its selector and whether they fit only it in the
/// window (<see cref="ElementOrigin.Unique"/>), which decides whether a resolution may later move its ref to the element
/// re-created. Proven only by a complete search of the whole window (not below withinRef): without visibleOnly or
/// enabledOnly, when every match was kept, each one whose fingerprint no other match shares; with them, which can hide an
/// offscreen or disabled twin, a single match is checked by one more search of the window for its identity alone, limited
/// to <see cref="UniquenessBudget"/>. Two kept matches with one fingerprint are not unique.</para>
/// <para><b>Scope</b> (roadmap M3.5): <see cref="ElementSearchScope.WindowAndPopups"/> also searches the window's popups
/// (<see cref="IPopupService.GetPopups"/>: its menus, dropdowns, flyouts and dialogs, which are top-level windows of their
/// own), front first and before the window, since an open popup is in front of it; <see cref="ElementSearchScope.Desktop"/>
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
/// first.</para>
/// <para>The whole search is one worker read, counted against the named window's application (or the unknown-target
/// bucket for a desktop search that names no window): a provider stuck in another window of the scope, such as a WebView2
/// child process or one of the desktop's windows, counts against that application's stuck reads too. Windows that do not
/// respond are skipped before they are read, which keeps that rare.</para>
/// <para>A search the managed client cannot express (AppBar or SemanticZoom) runs again with the control types tested on
/// the server. Reads of a match's details that fail leave those fields out; once the deadline has passed, or a provider did
/// not answer in time, the remaining matches are described from what the search read (no further call). A read the worker
/// abandons still returns the matches found until then, described the same way, with truncationReason Timeout.</para>
/// </remarks>
public sealed class ElementSearchService : IElementSearchService
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

    /// <summary>How long to keep waiting after the timeout for the search to stop and hand back what it found.</summary>
    private static readonly TimeSpan StopGrace = TimeSpan.FromSeconds(2);

    private readonly IUiaTree _tree;
    private readonly IUiaWorker _worker;
    private readonly IElementRegistry _registry;
    private readonly INativeWindowWrapper? _windows;
    private readonly IAccessibilityWarmup? _warmup;
    private readonly IPopupService? _popups;

    /// <param name="tree">The UI Automation tree.</param>
    /// <param name="worker">Runs the search under a deadline.</param>
    /// <param name="registry">Gives the matches their refs and resolves withinRef.</param>
    /// <param name="windows">The hung-window check and the application a stuck read is counted against; without it neither
    /// is done.</param>
    /// <param name="warmup">Warms up the web content of Chromium, Electron and WebView2 windows before they are searched
    /// (roadmap M3.3). The server always passes it; without it no window is warmed up.</param>
    /// <param name="popups">The windows of the windowAndPopups and desktop scopes (roadmap M3.5). The server always passes
    /// it; without it every search covers the window alone.</param>
    public ElementSearchService(
        IUiaTree tree,
        IUiaWorker worker,
        IElementRegistry registry,
        INativeWindowWrapper? windows = null,
        IAccessibilityWarmup? warmup = null,
        IPopupService? popups = null)
    {
        _tree = tree;
        _worker = worker;
        _registry = registry;
        _windows = windows;
        _warmup = warmup;
        _popups = popups;
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

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(timeout);
        try
        {
            return await _worker.RunAsync(
                handle == nint.Zero ? 0 : UiaTargets.TargetProcessId(_windows, handle),
                token => Find(query, scope, criteria, maxResults, AccessibilityWarmup.BudgetFor(timeout), started, stopwatch, token),
                deadline.Token,
                StopGrace).ConfigureAwait(false);
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
            // Earlier reads of the same application are still stuck; nothing was waited for
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
    /// when the window or one of its popups is in front, else window). Without the popup service, the window alone.
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
    /// The windows a scope covers, in the order searched, how many it left out (desktop only), and how many were over its
    /// limit (for windowAndPopups, 1 when more popups were open than <see cref="PopupService.MaxPopups"/>).
    /// </summary>
    private (IReadOnlyList<nint> Windows, int Excluded, int OverLimit) WindowsOf(nint handle, ElementSearchScope scope) => scope switch
    {
        ElementSearchScope.WindowAndPopups when _popups != null =>
            _popups.GetPopupList(handle) is { } list
                ? ([.. list.Popups.Select(p => p.Handle), handle], 0, list.Truncated ? 1 : 0)
                : ([handle], 0, 0),
        ElementSearchScope.Desktop when _popups != null =>
            _popups.GetDesktopWindows(handle, PopupService.DesktopWindowLimit) is var set ? (set.Windows, set.Excluded, set.OverLimit) : default,
        _ => ([handle], 0, 0)
    };

    /// <summary>The search, on the worker thread.</summary>
    private FindElementsResult Find(
        ElementQuery query, ElementSearchScope scope, ElementCriteria criteria, int maxResults, TimeSpan warmupBudget,
        SearchStarted started, Stopwatch stopwatch, CancellationToken token)
    {
        var handle = query.WindowHandle;

        FindElementsResult Failure(string error, UIElementErrorCode code, string? title = null) =>
            new(false, (long)handle, title, 0, false, null, Elapsed(stopwatch), query.WithinRef, Error: error, ErrorCode: code,
                AccessibilityWarmup: started.Warmup);

        try
        {
            if (handle != nint.Zero &&
                UiaTargets.NotRespondingReason(_windows, handle, "its UI elements were not searched") is { } notResponding)
            {
                return Failure(notResponding, UIElementErrorCode.NotResponding);
            }

            if (!_tree.IsAvailable())
            {
                return Failure("UI Automation is not available on this system", UIElementErrorCode.AutomationUnavailable);
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
                    return Failure("Window not found or handle is invalid", UIElementErrorCode.WindowNotFound);
                }

                // A window with web content builds its page's tree only once asked: ask, and wait for it a moment, so the
                // search does not miss the page's elements
                started.Warmup = _warmup?.WarmUp(handle, warmupBudget, token);
                started.Title = TryReadName(window);
            }

            var title = started.Title;
            var root = window;
            var withinRef = query.WithinRef;
            if (query.WithinRef != null)
            {
                var within = _registry.Resolve(query.WithinRef, token);
                if (within.Element is not { } resolved)
                {
                    return Failure($"withinRef: {within.Error}", within.ErrorCode, title);
                }

                if (resolved.WindowHandle != handle)
                {
                    return Failure(
                        $"withinRef '{resolved.Ref}' is an element of window {(long)resolved.WindowHandle}, not of the window searched " +
                        $"({(long)handle}). Pass only withinRef, or the window it belongs to.",
                        UIElementErrorCode.ElementStale, title);
                }

                root = resolved.Node;

                // The ref the element has now: when it was found again and already had a ref of its own, the asked-for one
                // was dropped
                withinRef = resolved.Ref;
            }

            var generation = _registry.NextGeneration();
            started.Generation = generation;
            var (windows, excluded, overLimit) = WindowsOf(handle, scope);
            var compiled = criteria.Compile();
            var controlTypesOnServer = false;
            for (var index = 0; index < windows.Count; index++)
            {
                var target = windows[index];
                if (token.IsCancellationRequested)
                {
                    started.Stop(ElementSearchStop.Timeout);
                    break;
                }

                var candidatesLeft = ElementSearch.MaxCandidates - started.Candidates;
                if (candidatesLeft <= 0)
                {
                    started.Stop(ElementSearchStop.CandidateLimit);
                    break;
                }

                var isNamed = target == handle;

                // The popups searched before the named window leave it a share of the candidate limit, so a large palette
                // cannot keep the window the caller named from being searched
                if (!isNamed && handle != nint.Zero && windows.Skip(index + 1).Contains(handle))
                {
                    candidatesLeft -= NamedWindowCandidateReserve;
                    if (candidatesLeft <= 0)
                    {
                        started.Stop(ElementSearchStop.CandidateLimit);
                        continue;
                    }
                }

                var node = isNamed ? root : OpenOther(target);
                if (node == null)
                {
                    if (token.IsCancellationRequested)
                    {
                        started.Stop(ElementSearchStop.Timeout);
                        break;
                    }

                    started.SkipWindow();
                    continue;
                }

                var keep = Math.Max(1, maxResults - started.Kept);
                ElementSearchOutcome outcome;
                try
                {
                    try
                    {
                        outcome = Run(compiled, keep, candidatesLeft, target, node, started, token);
                    }
                    catch (NotSupportedException) when (criteria.ControlTypes is { Count: > 0 } && !controlTypesOnServer)
                    {
                        // The managed client cannot search for AppBar or SemanticZoom: test the control types here, in this
                        // window and the next ones
                        controlTypesOnServer = true;
                        compiled = criteria.Compile(controlTypesOnServer: true);
                        outcome = Run(compiled, keep, candidatesLeft, target, node, started, token);
                    }
                }
                catch (Exception ex) when (!isNamed && !token.IsCancellationRequested &&
                                           ex is not (OperationCanceledException or RegexMatchTimeoutException))
                {
                    // A popup that closed meanwhile, or whose provider failed or did not answer in time: the other windows
                    // are still searched
                    started.Abandon(target);
                    started.SkipWindow();
                    continue;
                }

                if (!isNamed && outcome.Stop == ElementSearchStop.Timeout && !token.IsCancellationRequested)
                {
                    // A popup whose provider did not answer in time, with time left: a part skipped, not the end of the search
                    outcome = outcome with { Stop = ElementSearchStop.None, SkippedParts = outcome.SkippedParts + 1 };
                }

                started.Complete(target, node, outcome);
                if (outcome.Stop != ElementSearchStop.None && (isNamed || token.IsCancellationRequested))
                {
                    break;
                }
            }

            var total = started.Snapshot();
            if (total.MatchCount == 0 && total.Stop == ElementSearchStop.Timeout)
            {
                return Failure(
                    $"The timeout passed before any element matching {criteria.Describe()} was found ({total.Candidates} " +
                    "candidates checked). Try again with a larger timeoutMs, or search a smaller part with withinRef.",
                    UIElementErrorCode.OperationTimedOut, title);
            }

            var unique = query.WithinRef == null
                ? Uniqueness(criteria, controlTypesOnServer, started.Searched, handle, token)
                : _ => null;
            var result = Result(query, withinRef, title, total,
                Describe(query, criteria, total.Matches, generation, mayRead: true, unique, token), started.Warmup, stopwatch);
            return scope == ElementSearchScope.Window
                ? result with { Scope = scope }
                : result with
                {
                    Scope = scope,
                    WindowsSearched = started.Searched.Count,
                    WindowsExcluded = excluded > 0 ? excluded : null,
                    Truncated = result.Truncated || overLimit > 0,
                    TruncationReason = result.TruncationReason is { } reason && reason != ElementSearchTruncation.MaxResults
                        ? reason
                        : overLimit > 0 ? ElementSearchTruncation.WindowLimit : result.TruncationReason
                };
        }
        catch (UiaElementUnavailableException ex)
        {
            return Failure($"The element searched below is no longer available: {ex.Message}", UIElementErrorCode.ElementAccessFailed);
        }
        catch (RegexMatchTimeoutException)
        {
            // The tool turns it into InvalidArguments
            throw;
        }
        catch (TimeoutException ex)
        {
            return Failure($"UI Automation did not answer in time: {ex.Message}", UIElementErrorCode.OperationTimedOut);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            return Failure("The timeout passed before the search started. Try again with a larger timeoutMs.",
                UIElementErrorCode.OperationTimedOut);
        }
        catch (Exception ex)
        {
            return Failure($"Error searching UI elements: {ex.Message}", UIElementErrorCode.OperationException);
        }
    }

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
        SearchStarted started, CancellationToken token)
    {
        var search = new ElementSearch(_tree, compiled.Condition, compiled.Filter, keep, maxCandidates);
        started.Begin(window, search);
        return search.Run(root, token);
    }

    /// <summary>
    /// Whether the criteria fit each kept match alone in its window (see the remarks): true, false, or null when that is
    /// not known. Each searched window's outcome is a search of that whole window (or of withinRef's element, for which the
    /// caller asks nothing).
    /// </summary>
    private Func<UiaElement, bool?> Uniqueness(
        ElementCriteria criteria, bool controlTypesOnServer, IReadOnlyList<SearchedWindow> searched, nint named,
        CancellationToken token)
    {
        var verdicts = new Dictionary<UiaElement, bool?>(ReferenceEqualityComparer.Instance);

        // One budget for every window's proof, the named window's first
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(token);
        budget.CancelAfter(UniquenessBudget);
        foreach (var window in searched.OrderBy(w => w.Handle == named ? 0 : 1))
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
            verdicts[matches[0]] = ProveAlone(criteria.Identity, controlTypesOnServer, matches[0], window.Node, budget.Token);
        }

        return m => verdicts.TryGetValue(m, out var verdict) ? verdict : null;
    }

    /// <summary>
    /// True when one more search of the window finds no other element that the criteria and the match's fingerprint fit,
    /// false when it finds one, null when it could not tell.
    /// </summary>
    /// <param name="budget">The deadline, or the end of <see cref="UniquenessBudget"/> when that comes first.</param>
    private bool? ProveAlone(ElementCriteria identity, bool controlTypesOnServer, UiaElement match, IUiaNode window, CancellationToken budget)
    {
        try
        {
            var (condition, filter) = identity.Compile(controlTypesOnServer);
            var fingerprint = ElementRegistry.Fingerprint(match, null);
            var proof = new ElementSearch(
                    _tree, condition, e => (filter == null || filter(e)) && ElementRegistry.SameElement(fingerprint, e), keep: 2)
                .Run(window, budget);
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
    /// The kept matches as find_ui_elements returns them, each registered under the window it was found in. With
    /// <paramref name="mayRead"/> false (an abandoned search, read on the caller's thread) nothing is read: every field
    /// comes from what the search read.
    /// </summary>
    private List<FoundElement> Describe(
        ElementQuery query, ElementCriteria criteria, IReadOnlyList<(nint Window, UiaElement Element)> matches, long generation,
        bool mayRead, Func<UiaElement, bool?> unique, CancellationToken token)
    {
        var found = new List<FoundElement>(matches.Count);
        foreach (var (window, match) in matches)
        {
            string? elementRef = null;
            try
            {
                elementRef = _registry.Register(
                    window, match, new ElementOrigin(generation, criteria.Identity, Unique: unique(match)));
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

    private static FoundElement Minimal(UiaElement match, string? elementRef)
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

    /// <summary>A window of the scope, searched: its node, its search's outcome and the matches kept from it.</summary>
    private sealed record SearchedWindow(nint Handle, IUiaNode Node, ElementSearchOutcome Outcome, IReadOnlyList<UiaElement> Kept);

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
                var room = Math.Max(0, MaxResults - _searched.Sum(w => w.Kept.Count));
                _searched.Add(new SearchedWindow(window, node, outcome, outcome.Matches.Take(room).ToList()));
                _current = null;
            }
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
                var windows = _searched.Select(w => (w.Handle, w.Outcome, Kept: (IReadOnlyList<UiaElement>)w.Kept)).ToList();
                if (_current is { } current)
                {
                    var running = current.Search.Snapshot();
                    var room = Math.Max(0, MaxResults - windows.Sum(w => w.Kept.Count));
                    windows.Add((current.Window, running, running.Matches.Take(room).ToList()));
                }

                var stop = windows.Select(w => w.Outcome.Stop).FirstOrDefault(s => s != ElementSearchStop.None);
                return new SearchTotal(
                    Matches: windows.SelectMany(w => w.Kept.Select(m => (w.Handle, m))).ToList(),
                    MatchCount: windows.Sum(w => w.Outcome.MatchCount),
                    Candidates: windows.Sum(w => w.Outcome.Candidates),
                    Stop: stop != ElementSearchStop.None ? stop : _stop,
                    SkippedParts: windows.Sum(w => w.Outcome.SkippedParts) + _skippedWindows);
            }
        }
    }
}
