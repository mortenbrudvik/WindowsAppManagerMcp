using System.Text.RegularExpressions;
using WindowsAppManagerMcp.Models;
using WindowsAppManagerMcp.Services.Interfaces;

namespace WindowsAppManagerMcp.Services;

/// <summary>wait_for_element (roadmap M3.8); see <see cref="IElementWaitService"/>.</summary>
/// <remarks>
/// <para><b>The window.</b> With criteria and a window selector, the window is waited for first through wait_for_window's
/// machinery (<see cref="IWindowWaitService.WaitForConditionAsync"/>, condition exists, stableMs 0), within the same
/// timeout, so a wait right after a launch covers a window that is still opening. The selector must end up naming one
/// window (AmbiguousWindow with candidates otherwise). A gone wait does not wait for the window: when no window matches
/// at the start, or a window.handle had closed (IsWindow confirms it), none of its elements is there either (satisfied, with
/// a warning). A ref and withinRef name their window through the element registry, with no wait. A window of an app the
/// user's target policy denies ends the wait before the first check (TargetDeniedByPolicy, <see cref="UiaTargets.ReadVerdict"/>):
/// nothing of it is read.</para>
/// <para><b>Each check.</b> Criteria run find_ui_elements' search (<see cref="IElementSearchService"/>: the provider
/// evaluates them under a cache request, the window's hung check comes first) with visibleOnly and enabledOnly off, since
/// the condition decides what counts, and at most <see cref="CandidateLimit"/> matches described (detail standard only for
/// focused, toggle_state and value_equals). The condition holds when any of them satisfies it (gone: when a complete search
/// finds none); that element is returned with its ref. Without a scope, gone searches the window and its popups on every
/// check, whatever is in front (find_ui_elements' default narrows to the window alone while the app is not in front), and a
/// check that left one of the window's popups out unread (an app the target policy denies, the agent's host) cannot satisfy
/// it. A ref is resolved through the registry (<see cref="IElementRegistry.Resolve"/>) on a worker thread, with the reads the
/// condition needs. The web-content warm-up (roadmap M3.3) runs once, on the first check that reaches the window. Checks
/// back off from <see cref="MinPollIntervalMs"/> to <see cref="MaxPollIntervalMs"/>; each one gets the time left, at least
/// <see cref="MinCheckTimeoutMs"/> (so the last check is never cut short) and at most <see cref="MaxCheckTimeoutMs"/>, which
/// the full reads of long values (value_equals) share with the search. The search may take up to 2 s more to stop and hand
/// back what it found when a provider does not return from one call (ElementSearchService's grace, which find_ui_elements
/// shares: a provider call cannot be interrupted), so a wait can end up to about 3 s after its timeout.</para>
/// <para><b>Ends.</b> A check that cannot tell (the window does not respond, UI Automation does not answer in time or cannot
/// open a window Win32 still knows, the search did not cover the whole window, a provider error) is remembered as the last
/// observed state and the wait goes on. A window that closes (IsWindow is false, or its handle now names a window of another
/// process or class), a stale or ambiguous ref or withinRef, a ref this server session never issued, an unknown control
/// type, a name pattern that runs too long, or UI Automation being unavailable end it at once; for gone, a window that
/// closed or a ref or withinRef whose element is gone satisfies it.</para>
/// <para><b>Passwords.</b> A password field's value is never read, as in get_focused_element and find_ui_elements (and the
/// IUiaTree.ReadValue contract): value_equals skips password fields, and ends with InvalidArguments when only password
/// fields match. No value of a password field, and no expected text, appears in a result or a message.</para>
/// <para><b>Events.</b> No UI Automation event handlers: the seam has none, and adding StructureChanged/PropertyChanged
/// handlers needs both engines, registration and removal calls that cross into the target process (and can block on a hung
/// one) and a real-desktop run this change cannot do. Polling with backoff is the whole mechanism; events are a later
/// optimisation (roadmap M7.2).</para>
/// </remarks>
public sealed class ElementWaitService : IElementWaitService
{
    /// <summary>The longest wait, whatever a caller asks for.</summary>
    public const int MaxTimeoutMs = 60_000;

    /// <summary>The first interval between two checks; it grows by half each time up to <see cref="MaxPollIntervalMs"/>.</summary>
    public const int MinPollIntervalMs = 100;

    /// <summary>The longest interval between two checks.</summary>
    public const int MaxPollIntervalMs = 500;

    /// <summary>The least time one check gets, even when the wait has less left.</summary>
    public const int MinCheckTimeoutMs = 1_000;

    /// <summary>The most time one check gets (find_ui_elements' longest timeout).</summary>
    public const int MaxCheckTimeoutMs = 30_000;

    /// <summary>How many matches of the criteria each check tests.</summary>
    public const int CandidateLimit = 10;

    /// <summary>How often the wait reports its progress.</summary>
    private const int ProgressIntervalMs = 1_000;

    /// <summary>The longest name or value a message quotes.</summary>
    private const int QuoteLength = 100;

    private const string InvalidArgumentsCode = "InvalidArguments";

    private readonly IElementSearchService _search;
    private readonly IElementRegistry _registry;
    private readonly IWindowWaitService _windowWait;
    private readonly IUiaTree _tree;
    private readonly IUiaWorker _worker;
    private readonly INativeWindowWrapper? _windows;
    private readonly ITargetPolicy? _policy;
    private readonly TimeProvider _time;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;

    /// <param name="search">find_ui_elements' search, which each check of criteria runs.</param>
    /// <param name="registry">Resolves a ref (and finds the window of a ref or withinRef).</param>
    /// <param name="windowWait">Waits for the window of a selector (wait_for_window's machinery).</param>
    /// <param name="tree">Reads a ref's element (focus, toggle state, value).</param>
    /// <param name="worker">Runs those reads under a deadline.</param>
    /// <param name="windows">The hung-window check before a ref's reads, and the application a stuck read is counted
    /// against; without it neither is done.</param>
    /// <param name="time">The clock; default the system clock.</param>
    /// <param name="delay">Waits between two checks; default Task.Delay on <paramref name="time"/>. Tests pass one that moves
    /// a manual clock.</param>
    /// <param name="policy">The user's target policy, which refuses a wait on a window of an app it denies. The server always
    /// passes it; without it (or without <paramref name="windows"/>) no window is refused.</param>
    public ElementWaitService(
        IElementSearchService search,
        IElementRegistry registry,
        IWindowWaitService windowWait,
        IUiaTree tree,
        IUiaWorker worker,
        INativeWindowWrapper? windows = null,
        TimeProvider? time = null,
        Func<TimeSpan, CancellationToken, Task>? delay = null,
        ITargetPolicy? policy = null)
    {
        _search = search;
        _registry = registry;
        _windowWait = windowWait;
        _tree = tree;
        _worker = worker;
        _windows = windows;
        _policy = policy;
        _time = time ?? TimeProvider.System;
        _delay = delay ?? ((wait, token) => Task.Delay(wait, _time, token));
    }

    /// <inheritdoc />
    public async Task<WaitForElementResult> WaitAsync(
        ElementWaitRequest request,
        IProgress<OperationProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if ((request.Ref == null) == (request.Criteria == null))
        {
            throw new ArgumentException("Give either Ref or Criteria.", nameof(request));
        }

        var start = _time.GetTimestamp();
        var wait = new Wait(this, request, Math.Clamp(request.TimeoutMs, 0, MaxTimeoutMs), () => (long)_time.GetElapsedTime(start).TotalMilliseconds);
        return await wait.RunAsync(progress, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>The condition as wait_for_element names it.</summary>
    internal static string ConditionName(ElementWaitCondition condition) => condition switch
    {
        ElementWaitCondition.Exists => "exists",
        ElementWaitCondition.Gone => "gone",
        ElementWaitCondition.Enabled => "enabled",
        ElementWaitCondition.Visible => "visible",
        ElementWaitCondition.Focused => "focused",
        ElementWaitCondition.NameEquals => "name_equals",
        ElementWaitCondition.ValueEquals => "value_equals",
        ElementWaitCondition.ToggleState => "toggle_state",
        _ => condition.ToString()
    };

    /// <summary>toggle_state's expected value, or null when it is not on, off or indeterminate (any case).</summary>
    internal static UiaToggleState? ParseToggleState(string? expected) => expected?.Trim().ToLowerInvariant() switch
    {
        "on" => UiaToggleState.On,
        "off" => UiaToggleState.Off,
        "indeterminate" => UiaToggleState.Indeterminate,
        _ => null
    };

    /// <summary>The reads a check needs beyond what a search or a resolution reads anyway.</summary>
    [Flags]
    private enum Needs
    {
        None = 0,
        Focus = 1,
        Toggle = 2,
        Value = 4
    }

    private static Needs NeedsFor(ElementWaitCondition condition) => condition switch
    {
        ElementWaitCondition.Focused => Needs.Focus,
        ElementWaitCondition.ToggleState => Needs.Toggle,
        ElementWaitCondition.ValueEquals => Needs.Value,
        _ => Needs.None
    };

    /// <summary>What one check saw: satisfied (with the element), not yet (with what was seen), or the end of the wait.</summary>
    private sealed record Check(bool Satisfied, string State, FoundElement? Element = null, WaitForElementResult? Ended = null,
        string? Warning = null)
    {
        public static Check Met(FoundElement? element, string state, string? warning = null) => new(true, state, element, Warning: warning);

        public static Check NotYet(string state, FoundElement? element = null) => new(false, state, element);

        public static Check End(WaitForElementResult ended) => new(false, ended.LastObservedState ?? string.Empty, Ended: ended);
    }

    /// <summary>A ref's element read on a worker thread, with the reads the condition needs.</summary>
    private sealed record RefRead(ElementResolution Resolution, FoundElement? Element = null, string? FullValue = null);

    /// <summary>One wait: its request, its clock and what it has seen so far.</summary>
    private sealed class Wait(ElementWaitService service, ElementWaitRequest request, int timeoutMs, Func<long> elapsed)
    {
        private readonly ElementWaitCondition _condition = request.Condition;
        private readonly UiaToggleState? _toggle = ParseToggleState(request.Expected);
        private nint _window;
        private (uint ProcessId, string? ClassName)? _identity;
        private string? _ref = request.Ref;
        private int _polls;
        private int? _matchCount;
        private AccessibilityWarmupInfo? _warmup;
        private bool _warmedUp;
        private FoundElement? _lastElement;
        private string _lastState = "nothing was checked yet";

        // Set once the ref (or withinRef) was moved to a re-created element: the result says so
        private string? _movedWarning;

        public async Task<WaitForElementResult> RunAsync(IProgress<OperationProgress>? progress, CancellationToken cancellationToken)
        {
            if (await FindWindowAsync(progress, cancellationToken).ConfigureAwait(false) is { } early)
            {
                return early;
            }

            // Nothing of a window of an app the user denies is read, so nothing of it can be waited for
            if (UiaTargets.PolicyRefusal(service._windows, service._policy, _window) is { } denied)
            {
                return Ended(nameof(UIElementErrorCode.TargetDeniedByPolicy), denied);
            }

            _identity = ReadWindowIdentity();
            long lastProgress = 0;
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                _polls++;
                var check = _ref != null && request.Criteria == null
                    ? await CheckRefAsync(cancellationToken).ConfigureAwait(false)
                    : await CheckCriteriaAsync(cancellationToken).ConfigureAwait(false);

                if (check.Ended is { } ended)
                {
                    return ended with { ElapsedMs = elapsed(), Polls = _polls };
                }

                _lastState = check.State;
                _lastElement = check.Element;
                if (check.Satisfied)
                {
                    return Result(true) with { Element = check.Element, Warning = check.Warning ?? _movedWarning };
                }

                var now = elapsed();
                var remaining = timeoutMs - now;
                if (remaining <= 0)
                {
                    return TimedOut();
                }

                if (progress != null && now - lastProgress >= ProgressIntervalMs)
                {
                    lastProgress = now;
                    progress.Report(new OperationProgress((int)now, timeoutMs,
                        $"waiting for {ConditionName(_condition)}: {now} of {timeoutMs} ms, {_lastState}"));
                }

                var interval = (int)Math.Min(MaxPollIntervalMs, MinPollIntervalMs * Math.Pow(1.5, _polls - 1));
                await service._delay(TimeSpan.FromMilliseconds(Math.Min(interval, remaining)), cancellationToken).ConfigureAwait(false);
            }
        }

        /// <summary>
        /// The window to check: the ref's or withinRef's (from the registry), or the selector's once it is there. The result
        /// when the wait is over before the first check.
        /// </summary>
        private async Task<WaitForElementResult?> FindWindowAsync(IProgress<OperationProgress>? progress, CancellationToken cancellationToken)
        {
            var named = _ref ?? request.Criteria?.WithinRef;
            if (named != null)
            {
                var lookup = service._registry.Lookup(named);
                if (lookup.Success)
                {
                    _window = lookup.WindowHandle;
                    return null;
                }

                // The element of a ref this session issued is gone before the wait started (its window closed, or the ref
                // was dropped): gone holds, for the elements below withinRef's too, but say so. A ref this session never
                // issued names no element, so it says nothing about one being gone: it ends the wait as for any condition
                if (_condition == ElementWaitCondition.Gone && !lookup.NotIssued)
                {
                    _lastState = lookup.Error ?? "the ref is stale";
                    return Result(true) with
                    {
                        Warning = _ref != null
                            ? $"The element was already gone when the wait started: {lookup.Error} If you expected it to be " +
                              "there, check the ref."
                            : $"withinRef's element was already gone when the wait started, and the elements below it with it: " +
                              $"{lookup.Error} If you expected it to be there, check withinRef."
                    };
                }

                return Ended(lookup.ErrorCode.ToString(), _ref != null ? lookup.Error! : $"withinRef: {lookup.Error}");
            }

            var selector = request.Window ?? throw new ArgumentException("Criteria without withinRef need a window.", nameof(request));
            var gone = _condition == ElementWaitCondition.Gone;
            var outcome = await service._windowWait.WaitForConditionAsync(new WindowConditionRequest
            {
                Window = selector,
                Condition = WindowWaitCondition.Exists,
                StableMs = 0,
                // A gone wait does not wait for the window: when none is open, none of its elements is
                TimeoutMs = gone ? 0 : timeoutMs
            }, progress, cancellationToken).ConfigureAwait(false);

            if (outcome.Satisfied && outcome.Window is { } window)
            {
                _window = window.Handle;
                return null;
            }

            // A window.handle that is no window (the window wait's InvalidHandle, confirmed with IsWindow) had closed before
            // the wait started: none of its elements is there
            if (gone && selector.Handle is { } handle && outcome.ErrorCode == nameof(WindowErrorCode.InvalidHandle) &&
                service._windows?.IsWindow((nint)handle) != true)
            {
                _lastState = $"window {handle} is closed";
                return Result(true) with
                {
                    Warning = $"Window {handle} had already closed when the wait started, so none of its elements is there. If you " +
                              "expected it to be open, find it again with find_windows."
                };
            }

            if (outcome.Satisfied)
            {
                return Ended(nameof(WindowErrorCode.AmbiguousWindow),
                    $"{outcome.MatchCount} windows match {selector.Describe()}, so it is not clear whose elements to wait for; " +
                    "candidates lists them, front to back. Wait again with the one you mean as window {handle, processId}, or " +
                    "narrow the selector.") with
                {
                    Candidates = outcome.Candidates.Select(WindowCandidateDto.From).ToList()
                };
            }

            if (outcome.TimedOut && gone)
            {
                _lastState = $"no window matches {selector.Describe()}";
                return Result(true) with
                {
                    // A handle is read whatever the listing's rules: it no longer matches its filters
                    Warning = selector.Handle is { } namedHandle
                        ? $"Window {namedHandle} does not match {selector.Describe()} (it changed, or its handle now names another " +
                          "window), so none of the elements of the window meant is there. If you expected it to match, check it " +
                          "with get_window_info."
                        : $"No window matching {selector.Describe()} is open, so none of its elements is. If you expected it to " +
                          "be open, check the selector with find_windows (untitled windows need includeUntitled or className)."
                };
            }

            if (outcome.TimedOut)
            {
                _lastState = $"no window matching {selector.Describe()} appeared";
                return Result(false) with
                {
                    TimedOut = true,
                    ErrorCode = nameof(WindowErrorCode.WaitTimedOut),
                    Error = $"No window matching {selector.Describe()} appeared within {timeoutMs} ms, so no element was checked. " +
                            "Check what is open with find_windows; untitled windows need includeUntitled or className. " +
                            "Nothing was changed."
                };
            }

            return Ended(outcome.ErrorCode ?? nameof(WindowErrorCode.WindowNotFound), outcome.Error ?? "The window cannot be waited for.") with
            {
                Candidates = outcome.Candidates.Count > 0 ? outcome.Candidates.Select(WindowCandidateDto.From).ToList() : null
            };
        }

        /// <summary>One check of the criteria: find_ui_elements' search, then the condition on each match it describes.</summary>
        private async Task<Check> CheckCriteriaAsync(CancellationToken cancellationToken)
        {
            var criteria = request.Criteria!;
            var needs = NeedsFor(_condition);
            var gone = _condition == ElementWaitCondition.Gone;

            // The search and the full reads of long values share the check's time
            var checkTimeout = CheckTimeout();
            var checkEnds = elapsed() + (long)checkTimeout.TotalMilliseconds;
            var query = criteria with
            {
                WindowHandle = _window,
                VisibleOnly = false,
                EnabledOnly = false,
                MaxResults = _condition is ElementWaitCondition.Exists or ElementWaitCondition.Gone ? 1 : CandidateLimit,
                Detail = needs == Needs.None ? ElementDetail.Minimal : ElementDetail.Standard,
                Timeout = checkTimeout,
                // gone searches the window's popups on every check, whatever is in front: the default scope covers them only
                // while the app is in front, so a check run while another app is would miss a dialog that is a window of its
                // own (an unowned one, one owned by a hidden window, a WebView2 popup) and find nothing
                Scope = criteria.Scope ?? (gone && criteria.WithinRef == null ? ElementSearchScope.WindowAndPopups : null),
                // Until a check has reached the window: one that stopped before it (not responding) did not warm it up
                WarmUp = !_warmedUp
            };

            FindElementsResult found;
            try
            {
                found = await service._search.FindAsync(query, cancellationToken).ConfigureAwait(false);
            }
            catch (RegexMatchTimeoutException)
            {
                return Check.End(Ended(InvalidArgumentsCode,
                    $"name '{criteria.Name}' (nameMatch regex) took longer than {ElementCriteria.RegexTimeout.TotalMilliseconds:0} ms " +
                    "on an element's name, so the wait was stopped. Use a simpler pattern, or nameMatch contains."));
            }

            cancellationToken.ThrowIfCancellationRequested();
            _warmup ??= found.AccessibilityWarmup;
            _warmedUp |= found.Success || found.AccessibilityWarmup != null;

            if (!found.Success)
            {
                return SearchFailed(found);
            }

            if (found.RefMoved == true)
            {
                _movedWarning ??= found.Warning;
            }

            _matchCount = found.MatchCount;
            var complete = found.TruncationReason is null or ElementSearchTruncation.MaxResults;
            var elements = found.Elements ?? [];
            var what = Describe(criteria);

            if (gone)
            {
                // A popup of the window left out unread (an app the target policy denies, the agent's host) may hold it
                var popupsLeftOut = found.Scope == ElementSearchScope.WindowAndPopups ? found.WindowsExcluded ?? 0 : 0;
                return found.MatchCount switch
                {
                    0 when complete && popupsLeftOut > 0 => Check.NotYet(
                        $"no element matches {what} in the windows searched, but {popupsLeftOut} open popup" +
                        $"{(popupsLeftOut == 1 ? " of the window was" : "s of the window were")} left out unread (an app the " +
                        "target policy denies, or the agent's host), so it may still be there"),
                    0 when complete => Check.Met(null, $"no element matches {what}"),
                    0 => Check.NotYet($"no element matches {what} in the part searched, but the search did not cover the whole " +
                                      $"window ({found.TruncationReason})"),
                    _ => Check.NotYet($"{Count(found.MatchCount)} still match{(found.MatchCount == 1 ? "es" : "")} {what}" +
                                      (elements.Count > 0 ? $": {Name(elements[0])}" : ""), elements.FirstOrDefault())
                };
            }

            if (elements.Count == 0)
            {
                return Check.NotYet(complete
                    ? $"no element matches {what}"
                    : $"no element matches {what} in the part searched ({found.TruncationReason})");
            }

            string? firstState = null;
            var passwordFields = 0;
            foreach (var element in elements)
            {
                var (met, state) = await EvaluateAsync(element, needs, checkEnds, cancellationToken).ConfigureAwait(false);
                if (met)
                {
                    return Check.Met(element, state);
                }

                if (_condition == ElementWaitCondition.ValueEquals && element.IsPassword == true)
                {
                    passwordFields++;
                }

                firstState ??= state;
            }

            if (passwordFields == elements.Count)
            {
                return Check.End(Ended(InvalidArgumentsCode, PasswordMessage(elements.Count)) with { Element = elements[0] });
            }

            var others = found.MatchCount - 1;
            return Check.NotYet(firstState + (others > 0 ? $" ({Count(others)} more match{(others == 1 ? "es" : "")}, none {ConditionText()})" : ""),
                elements[0]);
        }

        /// <summary>A search that failed: the end of the wait, or a check that could not tell.</summary>
        private Check SearchFailed(FindElementsResult found)
        {
            var code = found.ErrorCode;
            switch (code)
            {
                case UIElementErrorCode.WindowNotFound or UIElementErrorCode.InvalidHandle:
                    // UI Automation failing to open the window does not prove it closed (a provider that restarts disconnects
                    // too): only Win32 does
                    if (!WindowClosed())
                    {
                        return Check.NotYet($"UI Automation could not read the window ({found.Error})", _lastElement);
                    }

                    if (_condition == ElementWaitCondition.Gone)
                    {
                        return Check.Met(null, "the window closed",
                            $"Window {(long)_window} closed, taking its elements with it. If only the element was meant to go, " +
                            "check that the application is still running.");
                    }

                    return Check.End(Ended(nameof(UIElementErrorCode.WindowNotFound),
                        $"Window {(long)_window} closed during the wait, so the element cannot become " +
                        $"{ConditionName(_condition)}. Find the window that replaced it with find_windows."));

                case UIElementErrorCode.ElementStale or UIElementErrorCode.AmbiguousElement
                    when _condition == ElementWaitCondition.Gone && request.Criteria?.WithinRef != null:
                    // withinRef's element is gone, and the elements below it with it
                    const string prefix = "withinRef: ";
                    var why = found.Error is { } error && error.StartsWith(prefix, StringComparison.Ordinal) ? error[prefix.Length..] : found.Error;
                    return Check.Met(null, "withinRef's element is gone",
                        $"withinRef's element is gone, and the elements below it with it: {why}");

                case UIElementErrorCode.InvalidControlType or UIElementErrorCode.AutomationUnavailable or
                    UIElementErrorCode.ElementStale or UIElementErrorCode.AmbiguousElement or UIElementErrorCode.TargetDeniedByPolicy:
                    return Check.End(Ended(code.ToString(), found.Error ?? code.ToString()));

                default:
                    // NotResponding, OperationTimedOut, ElementAccessFailed, OperationException: this check could not tell
                    return Check.NotYet($"the last check could not tell ({code}): {found.Error}", _lastElement);
            }
        }

        /// <summary>The window's process and class when the wait picked it, or null without the window wrapper.</summary>
        private (uint ProcessId, string? ClassName)? ReadWindowIdentity()
        {
            try
            {
                return service._windows is { } windows ? (windows.GetWindowThreadProcessId(_window), windows.GetClassName(_window)) : null;
            }
            catch (Exception)
            {
                return null;
            }
        }

        /// <summary>
        /// Whether Win32 says the window closed: IsWindow is false, or the handle now names a window of another process or
        /// class than the one the wait picked (a read that fails, PID 0 or an empty class, tells nothing). Without the window
        /// wrapper, true: a failed lookup is taken as a closed window.
        /// </summary>
        private bool WindowClosed()
        {
            if (service._windows is not { } windows)
            {
                return true;
            }

            try
            {
                if (!windows.IsWindow(_window))
                {
                    return true;
                }

                if (_identity is not { } before || ReadWindowIdentity() is not { } now)
                {
                    return false;
                }

                return (before.ProcessId != 0 && now.ProcessId != 0 && before.ProcessId != now.ProcessId) ||
                       (!string.IsNullOrEmpty(before.ClassName) && !string.IsNullOrEmpty(now.ClassName) &&
                        !string.Equals(before.ClassName, now.ClassName, StringComparison.Ordinal));
            }
            catch (Exception)
            {
                // It cannot be told now; the next check asks again
                return false;
            }
        }

        /// <summary>One check of the ref: its element resolved and read, then the condition.</summary>
        private async Task<Check> CheckRefAsync(CancellationToken cancellationToken)
        {
            var read = await ReadRefAsync(_ref!, NeedsFor(_condition), cancellationToken).ConfigureAwait(false);
            var resolution = read.Resolution;
            if (resolution.Element is not { } resolved)
            {
                var code = resolution.ErrorCode;
                if (code is UIElementErrorCode.ElementStale or UIElementErrorCode.AmbiguousElement)
                {
                    if (_condition == ElementWaitCondition.Gone)
                    {
                        return Check.Met(null, "the element is gone", code == UIElementErrorCode.AmbiguousElement
                            ? $"The element is gone; {resolution.CandidateCount} elements that look like it are there, and the ref " +
                              "follows none of them."
                            : null);
                    }

                    return Check.End(Ended(code.ToString(), resolution.Error ?? code.ToString()) with { Element = _lastElement });
                }

                return Check.NotYet($"the last check could not tell ({code}): {resolution.Error}", _lastElement);
            }

            // The element was found again under a ref of its own: follow that one (the asked-for one was dropped)
            _ref = resolved.Ref;
            var element = read.Element!;
            if (resolved.Moved)
            {
                // The element the ref was given for is gone; the ref names a re-created element that only fits its description
                _movedWarning ??= ElementRefs.MovedWarning(resolved.Ref);
                if (_condition == ElementWaitCondition.Gone)
                {
                    return Check.Met(null, "the element is gone",
                        $"The element is gone; {Name(element)} took its place and fits its description. {_movedWarning}");
                }
            }

            if (_condition == ElementWaitCondition.Gone)
            {
                return Check.NotYet($"{Name(element)} is still there", element);
            }

            var (met, state) = Evaluate(element, read.FullValue);
            if (!met && _condition == ElementWaitCondition.ValueEquals && element.IsPassword == true)
            {
                return Check.End(Ended(InvalidArgumentsCode, PasswordMessage(1)) with { Element = element });
            }

            return met ? Check.Met(element, state) : Check.NotYet(state, element);
        }

        /// <summary>
        /// The condition on one match of a search. A value cut to 200 characters that starts like the expected one is read
        /// in full through the match's ref, within what is left of the check's time (until <paramref name="checkEnds"/>, in
        /// milliseconds since the wait started).
        /// </summary>
        private async Task<(bool Met, string State)> EvaluateAsync(
            FoundElement element, Needs needs, long checkEnds, CancellationToken cancellationToken)
        {
            if (_condition != ElementWaitCondition.ValueEquals || element.ValueTruncated != true || element.IsPassword == true)
            {
                return Evaluate(element, element.Value);
            }

            // The search cut the value: it differs when the expected text does not start with what was read, and otherwise
            // it is read in full
            if (!request.Expected!.StartsWith(element.Value ?? string.Empty, StringComparison.Ordinal))
            {
                return (false, $"{Name(element)}: its value is {Quote(element.Value)}, not the expected one");
            }

            // The full reads share what the search left of the check's time, so they never add to how long a check takes:
            // none starts once it is used up
            var left = checkEnds - elapsed();
            if (left <= 0)
            {
                return (false, $"{Name(element)} has a value longer than {ElementInspectionService.MaxValueLength} characters that " +
                               "starts like the expected one, and the check's time ran out before it could be read in full");
            }

            var read = element.Ref != null
                ? await ReadRefAsync(element.Ref, needs, cancellationToken, TimeSpan.FromMilliseconds(left)).ConfigureAwait(false)
                : null;
            return read is { Resolution.Success: true }
                ? Evaluate(element, read.FullValue)
                : (false, $"{Name(element)} has a value longer than {ElementInspectionService.MaxValueLength} characters that " +
                          $"could not be read in full ({read?.Resolution.ErrorCode.ToString() ?? "it has no ref"})");
        }

        /// <summary>The condition on an element; <paramref name="fullValue"/> is its whole value, when it was read.</summary>
        private (bool Met, string State) Evaluate(FoundElement element, string? fullValue)
        {
            var name = Name(element);
            switch (_condition)
            {
                case ElementWaitCondition.Exists:
                    return (true, $"{name} exists");

                case ElementWaitCondition.Enabled:
                    return (element.IsEnabled, $"{name} is {(element.IsEnabled ? "enabled" : "disabled")}");

                case ElementWaitCondition.Visible:
                    if (element.IsOffscreen)
                    {
                        return (false, $"{name} is offscreen (scrolled out of view, collapsed or hidden)");
                    }

                    return element.Bounds is { Width: > 0, Height: > 0 }
                        ? (true, $"{name} is visible")
                        : (false, $"{name} has no size on screen");

                case ElementWaitCondition.Focused:
                    return element.HasKeyboardFocus switch
                    {
                        true => (true, $"{name} has keyboard focus"),
                        false => (false, $"{name} does not have keyboard focus"),
                        null => (false, $"{name}: its keyboard focus could not be read")
                    };

                case ElementWaitCondition.NameEquals:
                    return string.Equals(element.Name ?? string.Empty, request.Expected, StringComparison.Ordinal)
                        ? (true, $"{name} has the expected name")
                        : (false, $"{name}: its name is {Quote(element.Name)}, not the expected one");

                case ElementWaitCondition.ToggleState:
                    if (element.ToggleState is { } state)
                    {
                        return (state == _toggle, $"{name} is {state}");
                    }

                    return (false, element.Patterns.Contains("Toggle")
                        ? $"{name}: its toggle state could not be read"
                        : $"{name} has no Toggle pattern (it is not a check box or toggle button)");

                case ElementWaitCondition.ValueEquals:
                    if (element.IsPassword == true)
                    {
                        return (false, $"{name} is a password field, whose value is never read");
                    }

                    if (!element.Patterns.Contains("Value"))
                    {
                        return (false, $"{name} has no Value pattern (it holds no editable text value)");
                    }

                    if (fullValue == null)
                    {
                        return (false, $"{name}: its value could not be read");
                    }

                    return string.Equals(fullValue, request.Expected, StringComparison.Ordinal)
                        ? (true, $"{name} has the expected value")
                        : (false, $"{name}: its value is {Quote(fullValue)}, not the expected one");

                default:
                    return (false, $"{name} exists");
            }
        }

        /// <summary>
        /// A ref's element, resolved on a UI Automation worker thread after its window's hung check, with the reads
        /// <paramref name="needs"/> asks for and its click point, within <paramref name="timeout"/> (default: the time a check
        /// gets). Never throws for UI Automation: a read that ran out of time is OperationTimedOut.
        /// </summary>
        private async Task<RefRead> ReadRefAsync(string elementRef, Needs needs, CancellationToken cancellationToken, TimeSpan? timeout = null)
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(timeout ?? CheckTimeout());
            try
            {
                return await service._worker.RunAsync(
                    UiaTargets.TargetProcessId(service._windows, _window),
                    token => service.ReadRef(elementRef, _window, needs, token),
                    deadline.Token,
                    TimeSpan.Zero).ConfigureAwait(false);
            }
            catch (TimeoutException) when (cancellationToken.IsCancellationRequested)
            {
                throw new OperationCanceledException(cancellationToken);
            }
            catch (TimeoutException ex)
            {
                return new RefRead(ElementResolution.Failed(UIElementErrorCode.OperationTimedOut,
                    $"UI Automation did not answer in time: {ex.Message}"));
            }
        }

        private TimeSpan CheckTimeout() =>
            TimeSpan.FromMilliseconds(Math.Clamp(timeoutMs - elapsed(), MinCheckTimeoutMs, MaxCheckTimeoutMs));

        private WaitForElementResult Result(bool satisfied) => new(
            Success: satisfied,
            Satisfied: satisfied,
            TimedOut: false,
            ElapsedMs: elapsed(),
            Polls: _polls,
            WindowHandle: _window == nint.Zero ? null : (long)_window,
            MatchCount: _matchCount,
            LastObservedState: satisfied ? null : _lastState,
            AccessibilityWarmup: _warmup,
            Warning: _movedWarning,
            RefMoved: _movedWarning != null ? true : null);

        private WaitForElementResult Ended(string errorCode, string error) => Result(false) with
        {
            Element = _lastElement,
            ErrorCode = errorCode,
            Error = error
        };

        private WaitForElementResult TimedOut() => Result(false) with
        {
            TimedOut = true,
            Element = _lastElement,
            ErrorCode = nameof(WindowErrorCode.WaitTimedOut),
            Error = $"{Outcome()} within {timeoutMs} ms ({_polls} checks): {_lastState}. Nothing was changed; wait again (a " +
                    "longer timeoutMs) or check the state with a read tool."
        };

        /// <summary>What did not happen, for the timeout's message.</summary>
        private string Outcome()
        {
            var condition = ConditionName(_condition);
            if (request.Criteria is not { } criteria)
            {
                return _condition == ElementWaitCondition.Gone ? $"Element {_ref} was still there" : $"Element {_ref} did not become {condition}";
            }

            var what = Describe(criteria);
            return _condition switch
            {
                ElementWaitCondition.Exists => $"No element matching {what} appeared",
                ElementWaitCondition.Gone => $"Elements matching {what} were still there",
                _ => $"No element matching {what} became {condition}"
            };
        }

        /// <summary>What the condition means, for "none ..." in messages.</summary>
        private string ConditionText() => _condition switch
        {
            ElementWaitCondition.Enabled => "enabled",
            ElementWaitCondition.Visible => "visible",
            ElementWaitCondition.Focused => "focused",
            ElementWaitCondition.NameEquals => "with the expected name",
            ElementWaitCondition.ValueEquals => "with the expected value",
            ElementWaitCondition.ToggleState => $"{_toggle}",
            _ => ConditionName(_condition)
        };

        private static string PasswordMessage(int count) =>
            $"value_equals cannot check {(count == 1 ? "a password field" : $"the {count} matching elements, all password fields")}: " +
            "the server never reads a password field's value, not even to compare it. Wait for another condition (exists, " +
            "enabled, focused), or check the result of the step that filled it.";

        private static string Describe(ElementQuery criteria) => new ElementCriteria(
            Name: criteria.Name, NameMatch: criteria.NameMatch, AutomationId: criteria.AutomationId,
            ControlTypes: criteria.ControlTypes, ClassName: criteria.ClassName).Describe() +
            (criteria.WithinRef != null ? $" below {criteria.WithinRef}" : "");

        private static string Count(int count) => count == 1 ? "1 element" : $"{count} elements";
    }

    /// <summary>The reads of a ref's element, on the worker thread (see <see cref="Wait"/>'s ReadRefAsync).</summary>
    private RefRead ReadRef(string elementRef, nint window, Needs needs, CancellationToken token)
    {
        if (UiaTargets.NotRespondingReason(_windows, window, "its element was not read") is { } notResponding)
        {
            return new RefRead(ElementResolution.Failed(UIElementErrorCode.NotResponding, notResponding));
        }

        var resolution = _registry.Resolve(elementRef, token);
        if (resolution.Element is not { } resolved)
        {
            return new RefRead(resolution);
        }

        var node = resolved.Node;
        var element = ElementSearchService.Minimal(resolved.Element, resolved.Ref);
        var patterns = resolved.Element.Patterns;
        if (!element.IsOffscreen && element.Bounds != null && Try(() => _tree.GetClickablePoint(node)) is { } point)
        {
            element = element with { ClickPoint = new ScreenPoint(point.X, point.Y) };
        }

        if ((needs & Needs.Focus) != 0)
        {
            element = element with { HasKeyboardFocus = Try(() => _tree.ReadInteraction(node))?.HasKeyboardFocus };
        }

        if ((needs & Needs.Toggle) != 0 && (patterns & UiaPatterns.Toggle) != 0)
        {
            element = element with { ToggleState = TryState(() => _tree.GetPattern<IUiaTogglePattern>(node)?.ToggleState) };
        }

        string? fullValue = null;
        if ((needs & Needs.Value) != 0)
        {
            // A password field's value is never read, not even to compare it
            var identity = Try(() => _tree.ReadIdentity(node));
            var isPassword = identity?.IsPassword == true;
            if (identity != null && !isPassword && (patterns & UiaPatterns.Value) != 0)
            {
                fullValue = Try(() => _tree.ReadValue(node));
            }

            var (value, truncated) = ElementInspectionService.Truncate(fullValue);
            element = element with
            {
                Value = value,
                ValueTruncated = truncated ? true : null,
                IsPassword = isPassword ? true : null
            };
        }

        return new RefRead(resolution, element, fullValue);
    }

    /// <summary>A read of an element's details; null when it fails. A timeout is thrown: it ends the check.</summary>
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

    /// <summary>An element for messages: "Button 'Save' (w4120035e9)".</summary>
    private static string Name(FoundElement element) =>
        $"{element.ControlType} {Quote(element.Name)}{(element.Ref != null ? $" ({element.Ref})" : "")}";

    private static string Quote(string? text) =>
        string.IsNullOrEmpty(text) ? "''" : $"'{(text.Length > QuoteLength ? text[..QuoteLength] + "..." : text)}'";
}
