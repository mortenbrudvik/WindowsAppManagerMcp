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

    /// <summary>The most time the search that proves a single match unique may take (it ends earlier at the deadline).</summary>
    public static readonly TimeSpan UniquenessBudget = TimeSpan.FromSeconds(1);

    /// <summary>How long to keep waiting after the timeout for the search to stop and hand back what it found.</summary>
    private static readonly TimeSpan StopGrace = TimeSpan.FromSeconds(2);

    private readonly IUiaTree _tree;
    private readonly IUiaWorker _worker;
    private readonly IElementRegistry _registry;
    private readonly INativeWindowWrapper? _windows;
    private readonly IAccessibilityWarmup? _warmup;

    /// <param name="tree">The UI Automation tree.</param>
    /// <param name="worker">Runs the search under a deadline.</param>
    /// <param name="registry">Gives the matches their refs and resolves withinRef.</param>
    /// <param name="windows">The hung-window check and the application a stuck read is counted against; without it neither
    /// is done.</param>
    /// <param name="warmup">Warms up the web content of Chromium, Electron and WebView2 windows before they are searched
    /// (roadmap M3.3). The server always passes it; without it no window is warmed up.</param>
    public ElementSearchService(
        IUiaTree tree,
        IUiaWorker worker,
        IElementRegistry registry,
        INativeWindowWrapper? windows = null,
        IAccessibilityWarmup? warmup = null)
    {
        _tree = tree;
        _worker = worker;
        _registry = registry;
        _windows = windows;
        _warmup = warmup;
    }

    /// <inheritdoc />
    public async Task<FindElementsResult> FindAsync(ElementQuery query, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        var stopwatch = Stopwatch.StartNew();
        var handle = query.WindowHandle;
        var started = new SearchStarted();

        FindElementsResult Failure(string error, UIElementErrorCode code, string? title = null) =>
            new(false, (long)handle, title, 0, false, null, Elapsed(stopwatch), query.WithinRef, Error: error, ErrorCode: code,
                AccessibilityWarmup: started.Warmup);

        if (handle == nint.Zero)
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
                UiaTargets.TargetProcessId(_windows, handle),
                token => Find(query, criteria, maxResults, AccessibilityWarmup.BudgetFor(timeout), started, stopwatch, token),
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
            return started.Search is { } search && search.Snapshot() is { MatchCount: > 0 } partial
                ? Result(query, query.WithinRef, started.Title, partial with { Stop = ElementSearchStop.Timeout },
                    Describe(query, criteria, partial.Matches, started.Generation, null, _ => null, default), started.Warmup,
                    stopwatch)
                : Failure($"UI Automation did not answer in time: {ex.Message}", UIElementErrorCode.OperationTimedOut, started.Title);
        }
    }

    /// <summary>The search, on the worker thread.</summary>
    private FindElementsResult Find(
        ElementQuery query, ElementCriteria criteria, int maxResults, TimeSpan warmupBudget, SearchStarted started,
        Stopwatch stopwatch, CancellationToken token)
    {
        var handle = query.WindowHandle;

        FindElementsResult Failure(string error, UIElementErrorCode code, string? title = null) =>
            new(false, (long)handle, title, 0, false, null, Elapsed(stopwatch), query.WithinRef, Error: error, ErrorCode: code,
                AccessibilityWarmup: started.Warmup);

        try
        {
            if (UiaTargets.NotRespondingReason(_windows, handle, "its UI elements were not searched") is { } notResponding)
            {
                return Failure(notResponding, UIElementErrorCode.NotResponding);
            }

            if (!_tree.IsAvailable())
            {
                return Failure("UI Automation is not available on this system", UIElementErrorCode.AutomationUnavailable);
            }

            IUiaNode? window;
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

            // A window with web content builds its page's tree only once asked: ask, and wait for it a moment, so the search
            // does not miss the page's elements
            started.Warmup = query.WarmUp ? _warmup?.WarmUp(handle, warmupBudget, token) : null;

            var title = TryReadName(window);
            started.Title = title;
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
            ElementSearchOutcome outcome;
            var controlTypesOnServer = false;
            try
            {
                outcome = Run(criteria.Compile(), maxResults, root, started, token);
            }
            catch (NotSupportedException) when (criteria.ControlTypes is { Count: > 0 })
            {
                // The managed client cannot search for AppBar or SemanticZoom: test the control types here
                controlTypesOnServer = true;
                outcome = Run(criteria.Compile(controlTypesOnServer: true), maxResults, root, started, token);
            }

            if (outcome.MatchCount == 0 && outcome.Stop == ElementSearchStop.Timeout)
            {
                return Failure(
                    $"The timeout passed before any element matching {criteria.Describe()} was found ({outcome.Candidates} " +
                    "candidates checked). Try again with a larger timeoutMs, or search a smaller part with withinRef.",
                    UIElementErrorCode.OperationTimedOut, title);
            }

            var unique = query.WithinRef == null
                ? Uniqueness(criteria, controlTypesOnServer, outcome, window, token)
                : _ => null;
            return Result(query, withinRef, title, outcome,
                Describe(query, criteria, outcome.Matches, generation, root, unique, token), started.Warmup, stopwatch);
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

    private ElementSearchOutcome Run(
        (UiaCondition Condition, Func<UiaElement, bool>? Filter) compiled, int maxResults, IUiaNode root, SearchStarted started,
        CancellationToken token)
    {
        var search = new ElementSearch(_tree, compiled.Condition, compiled.Filter, maxResults);
        started.Search = search;
        return search.Run(root, token);
    }

    /// <summary>
    /// Whether the criteria fit each kept match alone in the window (see the remarks): true, false, or null when that is
    /// not known. <paramref name="outcome"/> is a search of the whole window.
    /// </summary>
    private Func<UiaElement, bool?> Uniqueness(
        ElementCriteria criteria, bool controlTypesOnServer, ElementSearchOutcome outcome, IUiaNode window, CancellationToken token)
    {
        var matches = outcome.Matches;
        var twins = new HashSet<UiaElement>(ReferenceEqualityComparer.Instance);
        for (var i = 0; i < matches.Count; i++)
        {
            for (var j = i + 1; j < matches.Count; j++)
            {
                if (ElementRegistry.SameElement(ElementRegistry.Fingerprint(matches[i], null), matches[j]))
                {
                    twins.Add(matches[i]);
                    twins.Add(matches[j]);
                }
            }
        }

        if (!outcome.Complete)
        {
            return m => twins.Contains(m) ? false : null;
        }

        if (!criteria.VisibleOnly && !criteria.EnabledOnly)
        {
            // Every element the criteria fit was a match; when all were kept, the ones without a twin are alone
            var allKept = outcome.MatchCount == matches.Count;
            return m => twins.Contains(m) ? false : allKept ? true : null;
        }

        if (outcome.MatchCount != 1 || token.IsCancellationRequested)
        {
            return m => twins.Contains(m) ? false : null;
        }

        // A single match among the visible or enabled ones: an offscreen or disabled twin would not have been found, so
        // search the window for its identity alone
        var match = matches[0];
        var proven = ProveAlone(criteria.Identity, controlTypesOnServer, match, window, token);
        return m => ReferenceEquals(m, match) ? proven : null;
    }

    /// <summary>
    /// True when one more search of the window finds no other element that the criteria and the match's fingerprint fit,
    /// false when it finds one, null when it could not tell.
    /// </summary>
    private bool? ProveAlone(ElementCriteria identity, bool controlTypesOnServer, UiaElement match, IUiaNode window, CancellationToken token)
    {
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(token);
        budget.CancelAfter(UniquenessBudget);
        try
        {
            var (condition, filter) = identity.Compile(controlTypesOnServer);
            var fingerprint = ElementRegistry.Fingerprint(match, null);
            var proof = new ElementSearch(
                    _tree, condition, e => (filter == null || filter(e)) && ElementRegistry.SameElement(fingerprint, e), keep: 2)
                .Run(window, budget.Token);
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
        ElementQuery query, string? withinRef, string? title, ElementSearchOutcome outcome, IReadOnlyList<FoundElement> elements,
        AccessibilityWarmupInfo? warmup, Stopwatch stopwatch)
    {
        ElementSearchTruncation? reason = outcome.Stop switch
        {
            ElementSearchStop.Timeout => ElementSearchTruncation.Timeout,
            ElementSearchStop.CandidateLimit => ElementSearchTruncation.SearchLimit,
            _ when outcome.SkippedParts > 0 => ElementSearchTruncation.PartsSkipped,
            _ when outcome.MatchCount > elements.Count => ElementSearchTruncation.MaxResults,
            _ => null
        };

        return new FindElementsResult(
            Success: true,
            WindowHandle: (long)query.WindowHandle,
            WindowTitle: title,
            MatchCount: outcome.MatchCount,
            Truncated: reason != null,
            Elements: elements,
            ElapsedMilliseconds: Elapsed(stopwatch),
            WithinRef: withinRef,
            TruncationReason: reason,
            AccessibilityWarmup: warmup);
    }

    /// <summary>
    /// The kept matches as find_ui_elements returns them, each registered. With <paramref name="root"/> null (an abandoned
    /// search, read on the caller's thread) nothing is read: every field comes from what the search read.
    /// </summary>
    private List<FoundElement> Describe(
        ElementQuery query, ElementCriteria criteria, IReadOnlyList<UiaElement> matches, long generation, IUiaNode? root,
        Func<UiaElement, bool?> unique, CancellationToken token)
    {
        var mayRead = root != null;
        var found = new List<FoundElement>(matches.Count);
        foreach (var match in matches)
        {
            string? elementRef = null;
            try
            {
                elementRef = _registry.Register(
                    query.WindowHandle, match, new ElementOrigin(generation, criteria.Identity, Unique: unique(match)));
            }
            catch (Exception)
            {
                // Returned without a ref
            }

            var element = Minimal(match, elementRef);
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

    /// <summary>The search of one call once it has started, for the caller when the worker abandons it.</summary>
    private sealed class SearchStarted
    {
        private ElementSearch? _search;
        private string? _title;
        private long _generation;
        private AccessibilityWarmupInfo? _warmup;

        public ElementSearch? Search
        {
            get => Volatile.Read(ref _search);
            set => Volatile.Write(ref _search, value);
        }

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
    }
}
