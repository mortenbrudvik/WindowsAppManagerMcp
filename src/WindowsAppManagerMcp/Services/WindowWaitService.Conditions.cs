using System.Text.RegularExpressions;
using WindowsAppManagerMcp.Models;
using WindowsAppManagerMcp.Services.Interfaces;

namespace WindowsAppManagerMcp.Services;

/// <summary>
/// The waits for a window condition (roadmap M3.7, wait_for_window). See <see cref="IWindowWaitService.WaitForConditionAsync"/>.
/// </summary>
/// <remarks>
/// Each poll resolves the selector the way IWindowService.ResolveWindow does (a handle or foreground through it, filters
/// through FindWindows, so the listing's rules and includeUntitled, includeCloaked and className apply), which only reads
/// windows and never sends one a message. The only wait on an application is input_idle's WaitForInputIdle, bounded to
/// <see cref="MaxInputIdleWaitMs"/> per poll and skipped while Windows reports the window hung. Window events
/// (<see cref="IWindowEventSource"/>, roadmap M3.F22) end the sleep between two polls early, so the next poll comes sooner,
/// but no sooner than <see cref="WaitWakeUp.MinSleepMs"/>: the polls alone decide, stableMs is measured by them, and a wait
/// without events (no event source, or one that cannot subscribe) polls exactly as before.
/// </remarks>
public sealed partial class WindowWaitService
{
    /// <summary>How often a condition wait reads the windows after its first <see cref="FastPollPeriodMs"/>.</summary>
    public const int SlowPollIntervalMs = 250;

    /// <summary>How long a condition wait reads the windows every <see cref="PollIntervalMs"/> before it backs off.</summary>
    public const int FastPollPeriodMs = 2000;

    /// <summary>The longest one input_idle poll waits in WaitForInputIdle.</summary>
    public const int MaxInputIdleWaitMs = 250;

    /// <summary>
    /// The most windows a result lists when several satisfy the condition, are in the way, or count for all or count (issue
    /// #14); its MatchCount is always all of them. An early end lists up to 5 (<see cref="WindowResolution.MaxCandidates"/>).
    /// </summary>
    public const int MaxSetWindows = 20;

    /// <summary>How often a condition wait reports its progress.</summary>
    private const int ProgressIntervalMs = 1000;

    private const string InvalidArgumentsCode = "InvalidArguments";

    public async Task<WindowConditionResult> WaitForConditionAsync(
        WindowConditionRequest request,
        IProgress<OperationProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Window);

        var timeoutMs = Math.Clamp(request.TimeoutMs, 0, MaxTimeoutMs);
        var start = _time.GetTimestamp();
        long Elapsed() => (long)_time.GetElapsedTime(start).TotalMilliseconds;

        // The selector is checked as ResolveWindow checks it, so one that cannot be used ends the wait before it starts
        var initial = _windows.ResolveWindow(request.Window);
        if (initial.ErrorCode == InvalidArgumentsCode)
            return Ended(InvalidArgumentsCode, initial.Error ?? "window cannot be used.", Elapsed());

        // title_changed follows one window, the one the selector names now
        if (request.Condition == WindowWaitCondition.TitleChanged)
        {
            // A handle names its window whatever its title: the title filters describe the title that is to change, and may
            // have changed already, so only the process and class filters guard against a reused handle
            if (request.Window.Handle.HasValue && HasTitleFilter(request.Window))
                initial = _windows.ResolveWindow(WithoutTitleFilters(request.Window));

            if (!initial.Success)
            {
                var error = initial.Error ?? "No single window matched.";
                if (initial.ErrorCode == nameof(WindowErrorCode.WindowNotFound) && HasTitleFilter(request.Window))
                {
                    error += " The title may already have changed: title_changed follows the window the selector names when the call " +
                             "starts, so name it by handle or by its process, not by the title that changes, and pass the title you " +
                             "read before acting as previousTitle.";
                }

                return Ended(initial.ErrorCode ?? nameof(WindowErrorCode.WindowNotFound), error, Elapsed(), initial.Candidates,
                    initial.MatchCount);
            }
        }

        var watch = new ConditionWatch(this, request, timeoutMs, initial,
            request.NewOnly ? ShownWindows(request.Window.IncludeCloaked) : null);
        long lastProgress = 0;
        WindowEvents? events = null;

        try
        {
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();

                // An event from here on may come after the poll read: it wakes the sleep after it
                events?.WakeUp?.Reset();
                var now = Elapsed();
                if (watch.Poll(now, Elapsed) is { } done)
                    return done with { ElapsedMs = Elapsed() };

                if (progress != null && now - lastProgress >= ProgressIntervalMs)
                {
                    lastProgress = now;
                    progress.Report(new OperationProgress((int)now, timeoutMs,
                        $"waiting for {Waited(request)}: {now} of {timeoutMs} ms, {watch.Status}"));
                }

                var remaining = timeoutMs - Elapsed();
                if (remaining <= 0)
                    return watch.TimedOut() with { ElapsedMs = Elapsed() };

                var interval = TimeSpan.FromMilliseconds(Math.Min(now < FastPollPeriodMs ? PollIntervalMs : SlowPollIntervalMs, remaining));

                // Subscribed before the first sleep, so a wait its first poll answers installs nothing
                events ??= SubscribeToWindowEvents();
                if (events?.WakeUp is { } wakeUp)
                    await wakeUp.SleepAsync(interval, _delay, cancellationToken).ConfigureAwait(false);
                else
                    await _delay(interval, cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            events?.Subscription?.Dispose();
        }
    }

    /// <summary>
    /// A condition wait's window events (roadmap M3.F22): the wake-up they signal, and the subscription to end. Both are
    /// null when there is no event source or it could not subscribe: the wait then polls as it did without events.
    /// </summary>
    private sealed record WindowEvents(WaitWakeUp? WakeUp, IDisposable? Subscription);

    private WindowEvents SubscribeToWindowEvents()
    {
        if (_events == null)
            return new WindowEvents(null, null);

        var wakeUp = new WaitWakeUp();
        try
        {
            return new WindowEvents(wakeUp, _events.Subscribe(wakeUp.Signal));
        }
        catch (Exception)
        {
            // The events only make the wait quicker; it polls without them
            return new WindowEvents(null, null);
        }
    }

    /// <summary>
    /// newOnly's baseline: the top-level windows open when the wait starts, by the listing's rules: visible, and not cloaked
    /// unless the selector takes cloaked windows. A window that was hidden then (a tray app's main window, a dialog an app
    /// keeps hidden for reuse) and is shown during the wait is new.
    /// </summary>
    private HashSet<nint> ShownWindows(bool includeCloaked) =>
        EnumerateTopLevelWindows().Where(h => _native.IsWindowVisible(h) && (includeCloaked || !_native.IsCloaked(h))).ToHashSet();

    /// <summary>The condition as wait_for_window names it.</summary>
    internal static string ConditionName(WindowWaitCondition condition) => condition switch
    {
        WindowWaitCondition.Exists => "exists",
        WindowWaitCondition.Gone => "gone",
        WindowWaitCondition.Foreground => "foreground",
        WindowWaitCondition.Visible => "visible",
        WindowWaitCondition.Minimized => "minimized",
        WindowWaitCondition.Maximized => "maximized",
        WindowWaitCondition.TitleChanged => "title_changed",
        WindowWaitCondition.InputIdle => "input_idle",
        _ => condition.ToString()
    };

    /// <summary>What a wait waits for, for progress messages: "minimized (all)", "exists (exactly 3)".</summary>
    private static string Waited(WindowConditionRequest request) =>
        ConditionName(request.Condition) + (request.All ? " (all)" : "") + (request.Count is { } count ? $" ({count.Describe()})" : "");

    private static bool HasTitleFilter(WindowSelector selector) =>
        !string.IsNullOrEmpty(selector.TitleContains) || !string.IsNullOrEmpty(selector.TitleEquals) || !string.IsNullOrEmpty(selector.TitleRegex);

    /// <summary>The selector without its title filters.</summary>
    private static WindowSelector WithoutTitleFilters(WindowSelector selector) =>
        selector with { TitleContains = null, TitleEquals = null, TitleRegex = null };

    /// <summary>Only the selector's title filters, for messages.</summary>
    private static WindowSelector TitleFilters(WindowSelector selector) =>
        new() { TitleContains = selector.TitleContains, TitleEquals = selector.TitleEquals, TitleRegex = selector.TitleRegex };

    private static WindowConditionResult Ended(
        string errorCode, string error, long elapsedMs, IReadOnlyList<WindowInfo>? candidates = null, int matchCount = 0) => new()
    {
        ErrorCode = errorCode,
        Error = error,
        ElapsedMs = elapsedMs,
        Candidates = candidates?.Take(WindowResolution.MaxCandidates).ToList() ?? [],
        MatchCount = matchCount
    };

    /// <summary>
    /// The state of one condition wait between polls: since when each window has satisfied the condition, since when no
    /// window has matched (gone), which windows have held it together and since when (all and count), the new title and since
    /// when (title_changed), and what the last poll saw.
    /// </summary>
    private sealed class ConditionWatch
    {
        private readonly WindowWaitService _service;
        private readonly WindowConditionRequest _request;
        private readonly WindowSelector _selector;
        private readonly int _timeoutMs;
        private readonly int _stableMs;
        private readonly HashSet<nint> _excluded;
        private readonly HashSet<nint>? _openAtStart;
        private readonly Dictionary<nint, long> _since = new();
        private readonly WindowInfo? _tracked;
        private readonly string _previousTitle = string.Empty;
        private readonly bool _titleFiltersMissedAtStart;
        private long? _goneSince;
        private HashSet<nint>? _held;
        private long _heldSince;
        private (string Title, long Since)? _newTitle;
        private int? _matchesAtStart;
        private int? _unindexedAtStart;
        private int _unindexed;
        private bool _handleIsNoWindow;
        private IReadOnlyList<WindowInfo> _matches = [];
        private WindowInfo? _lastSeen;
        private string? _note;

        public ConditionWatch(
            WindowWaitService service, WindowConditionRequest request, int timeoutMs, WindowResolution initial, HashSet<nint>? openAtStart)
        {
            _service = service;
            _request = request;
            _selector = request.Window;
            _timeoutMs = timeoutMs;
            // A condition cannot hold longer than the wait: timeoutMs 0 is one check
            _stableMs = Math.Clamp(request.StableMs, 0, timeoutMs);
            _excluded = new HashSet<nint>(request.ExcludeHandles);
            _openAtStart = openAtStart;
            if (request.Condition == WindowWaitCondition.TitleChanged)
            {
                _tracked = initial.Window;
                _previousTitle = request.PreviousTitle ?? initial.Window!.Title;

                // A handle named by a title it no longer had at the start: the change most likely came before the call
                _titleFiltersMissedAtStart = request.PreviousTitle == null && HasTitleFilter(_selector) && TitleMissed(initial.Window!);
            }
        }

        /// <summary>Whether the window's title does not match the selector's title filters (a pattern that runs too long: no).</summary>
        private bool TitleMissed(WindowInfo window)
        {
            try
            {
                return WindowMatching.Mismatches(window, TitleFilters(_selector)).Count > 0;
            }
            catch (RegexMatchTimeoutException)
            {
                return false;
            }
        }

        /// <summary>What the last poll saw, for progress messages.</summary>
        public string Status => _request.Condition switch
        {
            WindowWaitCondition.TitleChanged => $"title '{_lastSeen?.Title ?? _previousTitle}'",
            _ => $"{_matches.Count} matching window{(_matches.Count == 1 ? "" : "s")}"
        };

        /// <summary>A gone wait, or a count whose most is 0, which waits as gone.</summary>
        private bool WaitsAsGone => _request.Condition == WindowWaitCondition.Gone || _request.Count?.Range().Most == 0;

        /// <summary>A wait with all or count: its result lists the windows that count (issue #14).</summary>
        private bool IsSetWait => _request.All || _request.Count != null;

        /// <summary>One poll: the result when the wait is over (satisfied, or ended early), null to poll again.</summary>
        public WindowConditionResult? Poll(long now, Func<long> elapsed) =>
            WaitsAsGone ? PollGone(now)
            : _request.Count is { } count ? PollCount(now, count)
            : _request.All ? PollAll(now)
            : _request.Condition == WindowWaitCondition.TitleChanged ? PollTitle(now)
            : PollWindows(now, elapsed);

        /// <summary>
        /// exists, visible, minimized, maximized, foreground and input_idle: a window that counts satisfies the condition, held
        /// for stableMs.
        /// </summary>
        private WindowConditionResult? PollWindows(long now, Func<long> elapsed)
        {
            if (Read() is { } ended)
                return ended;

            var foreground = _request.Condition == WindowWaitCondition.Foreground ? _service._native.GetForegroundWindow() : nint.Zero;
            var satisfying = _request.Condition == WindowWaitCondition.Foreground
                ? _matches.Where(w => w.Handle == foreground).ToList()
                : _matches.Where(Holds).ToList();

            foreach (var gone in _since.Keys.Where(h => satisfying.All(w => w.Handle != h)).ToList())
                _since.Remove(gone);
            foreach (var window in satisfying)
                _since.TryAdd(window.Handle, now);

            // Only the windows that have held for stableMs satisfy it: one that just appeared beside them does not count yet
            var stable = satisfying.Where(w => now - _since[w.Handle] >= _stableMs).ToList();
            if (stable.Count == 0)
                return null;

            if (_request.Condition != WindowWaitCondition.InputIdle)
                return Satisfied(stable);

            if (stable.Count > 1)
            {
                return Ended(nameof(WindowErrorCode.AmbiguousWindow),
                    $"{stable.Count} windows match {_selector.Describe()}, so input_idle cannot tell whose process to wait for; " +
                    "candidates lists them, front to back. Wait again with the one you mean as window {handle, processId}, or narrow " +
                    "the selector.", elapsed(), stable, stable.Count);
            }

            return CheckInputIdle(stable[0], elapsed);
        }

        /// <summary>
        /// input_idle on a window that has stayed: not while Windows reports it hung, then a bounded WaitForInputIdle on its
        /// (the app's) process. A process that cannot report it (no message loop) counts once its window responds.
        /// </summary>
        private WindowConditionResult? CheckInputIdle(WindowInfo window, Func<long> elapsed)
        {
            _lastSeen = window;
            if (_service._native.IsHungAppWindow(window.Handle))
            {
                _note = "the window is not responding (Windows reports it hung)";
                return null;
            }

            var waitMs = (int)Math.Clamp(_timeoutMs - elapsed(), 0, MaxInputIdleWaitMs);
            var idle = _service._processes.WaitForInputIdle(window.ProcessId, waitMs);
            if (idle == false)
            {
                _note = $"its process ({window.ProcessName}, PID {window.ProcessId}) did not report input idle";
                return null;
            }

            return Satisfied([window]) with { IdleCheck = idle == true ? IdleCheck.InputIdle : IdleCheck.RespondingOnly };
        }

        /// <summary>all: every window that counts holds the condition, and at least one counts, the same windows for stableMs.</summary>
        private WindowConditionResult? PollAll(long now)
        {
            if (Read() is { } ended)
                return ended;

            return _matches.Count > 0 && _matches.All(Holds) ? HoldSet(now) : Unheld();
        }

        /// <summary>count: as many windows count as it asks for, the same windows for stableMs.</summary>
        private WindowConditionResult? PollCount(long now, WindowCount count)
        {
            if (Read() is { } ended)
                return ended;

            var (fewest, most) = count.Range();
            return _matches.Count >= fewest && (most is not { } max || _matches.Count <= max) ? HoldSet(now) : Unheld();
        }

        /// <summary>
        /// The set holds the condition now: satisfied once the same windows have held it for stableMs. A window that comes
        /// into the set or leaves it starts the time again, also when the count stays the same.
        /// </summary>
        private WindowConditionResult? HoldSet(long now)
        {
            var handles = _matches.Select(w => w.Handle).ToHashSet();
            if (_held?.SetEquals(handles) != true)
            {
                _held = handles;
                _heldSince = now;
            }

            return now - _heldSince >= _stableMs ? SetResult(_matches) with { Satisfied = true } : null;
        }

        /// <summary>The set does not hold the condition: the next time it does, the time starts again.</summary>
        private WindowConditionResult? Unheld()
        {
            _held = null;
            return null;
        }

        /// <summary>gone (and a count whose most is 0): no window that counts matches, for stableMs.</summary>
        private WindowConditionResult? PollGone(long now)
        {
            if (Read(handleClosedEnds: false) is { } ended)
                return ended;

            _matchesAtStart ??= _matches.Count;
            _unindexedAtStart ??= _unindexed;
            if (_matches.Count > 0)
            {
                _goneSince = null;
                return null;
            }

            _goneSince ??= now;
            if (now - _goneSince < _stableMs)
                return null;

            return new WindowConditionResult
            {
                Satisfied = true,
                IsSet = IsSetWait,
                Warning = _matchesAtStart == 0 ? NothingAtStartWarning() : null
            };
        }

        /// <summary>The warning of a gone wait that had nothing to close: what the selector named at the start.</summary>
        private string NothingAtStartWarning() => _selector.Handle switch
        {
            { } handle when _handleIsNoWindow => $"Window {handle} had already closed when the wait started.",
            // A handle is read whatever the listing's rules, so only its filters can have left it out
            { } handle => $"Window {handle} did not match {_selector.Describe()} when the wait started (it changed, or its handle " +
                          "now names another window), so nothing had to close. If you expected it to match, check it with get_window_info.",
            // Windows matched, but fewer than the index needs: it named none
            _ when _selector.Index is { } index && _unindexedAtStart > 0 =>
                $"Only {_unindexedAtStart} window{(_unindexedAtStart == 1 ? "" : "s")} matched {(_selector with { Index = null }).Describe()} " +
                $"when the wait started, so index {index} (counted from 0, front to back) named none and nothing had to close.",
            _ => $"No window matched {_selector.Describe()} when the wait started, so nothing had to close. If you expected one to be " +
                 "open, check the selector with find_windows (untitled windows need includeUntitled or className)."
        };

        /// <summary>title_changed: the window's title differs from the previous title, and the new title has held for stableMs.</summary>
        private WindowConditionResult? PollTitle(long now)
        {
            var handle = _tracked!.Handle;
            if (_service._windows.GetWindowInfo(handle) is not { } window)
            {
                return Ended(nameof(WindowErrorCode.InvalidHandle),
                    $"Window {handle} closed before its title changed from '{_previousTitle}'. Find the window that replaced it " +
                    "with find_windows.", now) with { PreviousTitle = _previousTitle };
            }

            _lastSeen = window;
            if (string.Equals(WindowTitles.Normalize(window.Title), WindowTitles.Normalize(_previousTitle), StringComparison.Ordinal))
            {
                _newTitle = null;
                return null;
            }

            if (_newTitle?.Title != window.Title)
                _newTitle = (window.Title, now);

            return now - _newTitle!.Value.Since >= _stableMs
                ? Satisfied([window]) with { PreviousTitle = _previousTitle }
                : null;
        }

        /// <summary>
        /// Reads the windows that count into <see cref="_matches"/>: the selector resolved, minus the excluded windows and,
        /// with newOnly, the windows open at the start; then the selector's index picks one. Returns the result when the
        /// wait must end: a titleRegex that timed out, or (unless <paramref name="handleClosedEnds"/> is false) a
        /// window.handle that is no window and so can never satisfy the condition.
        /// </summary>
        private WindowConditionResult? Read(bool handleClosedEnds = true)
        {
            List<WindowInfo> matches;
            try
            {
                if (_selector.Handle.HasValue || _selector.Foreground)
                {
                    var resolution = _service._windows.ResolveWindow(_selector);
                    if (resolution.ErrorCode == InvalidArgumentsCode)
                        return Ended(InvalidArgumentsCode, resolution.Error ?? "window cannot be used.", 0);

                    _handleIsNoWindow = _selector.Handle.HasValue && resolution.ErrorCode == nameof(WindowErrorCode.InvalidHandle);
                    if (handleClosedEnds && _selector.Handle is { } handle && _handleIsNoWindow)
                    {
                        return Ended(nameof(WindowErrorCode.InvalidHandle),
                            $"window.handle {handle} is no window (it closed), so it cannot become {ConditionName(_request.Condition)}. " +
                            "Find the window again with find_windows, or wait for it by its filters without the handle.", 0);
                    }

                    matches = resolution.Success && resolution.Window != null ? [resolution.Window] : [];
                }
                else
                {
                    matches = _service._windows.FindWindows(_selector.ToQuery()).ToList();
                }
            }
            catch (RegexMatchTimeoutException)
            {
                return Ended(InvalidArgumentsCode, WindowService.RegexTimeoutMessage(_selector.TitleRegex), 0);
            }

            matches.RemoveAll(w => _excluded.Contains(w.Handle) || _openAtStart?.Contains(w.Handle) == true);
            _unindexed = matches.Count;
            if (_selector.Index is { } index && !_selector.Handle.HasValue && !_selector.Foreground)
                matches = index < matches.Count ? [matches[index]] : [];

            _matches = matches;
            return null;
        }

        private static bool IsOnScreen(WindowInfo window) =>
            window.IsVisible && !window.IsCloaked && window.State != WindowState.Minimized;

        /// <summary>Whether the window is as visible, minimized or maximized wants it; for the other conditions, true.</summary>
        private bool Holds(WindowInfo window) => _request.Condition switch
        {
            WindowWaitCondition.Visible => IsOnScreen(window),
            WindowWaitCondition.Minimized => window.State == WindowState.Minimized,
            WindowWaitCondition.Maximized => window.State == WindowState.Maximized,
            _ => true
        };

        /// <summary>What visible, minimized and maximized want a window to be, for messages: "on screen", "minimized".</summary>
        private string Wanted => _request.Condition == WindowWaitCondition.Visible ? "on screen" : ConditionName(_request.Condition);

        /// <summary>What a window that does not hold visible, minimized or maximized is instead, for messages.</summary>
        private string Unlike(WindowInfo window) =>
            _request.Condition == WindowWaitCondition.Visible ? OffScreenReason(window) : window.State.ToString().ToLowerInvariant();

        private static WindowConditionResult Satisfied(IReadOnlyList<WindowInfo> windows) => new()
        {
            Satisfied = true,
            Window = windows.Count == 1 ? windows[0] : null,
            Candidates = windows.Count > 1 ? windows.Take(MaxSetWindows).ToList() : [],
            MatchCount = windows.Count
        };

        /// <summary>The windows that count, as the result of a wait with all or count lists them.</summary>
        private static WindowConditionResult SetResult(IReadOnlyList<WindowInfo> windows) => new()
        {
            IsSet = true,
            Candidates = windows.Take(MaxSetWindows).ToList(),
            MatchCount = windows.Count
        };

        /// <summary>
        /// The timeout: what was last seen (the windows that count, all of them for all or count), the window in the way when
        /// there is one, and what to do.
        /// </summary>
        public WindowConditionResult TimedOut()
        {
            var what = $"{_selector.Describe()}{Qualifier()}";
            var waited = $"within {_timeoutMs} ms";
            WindowInfo? window = null;
            IReadOnlyList<WindowInfo> others = [];
            string error;

            switch (_request.Condition)
            {
                case var _ when WaitsAsGone:
                    (window, others) = OneOrSeveral(_matches);
                    error = $"{_matches.Count} window{(_matches.Count == 1 ? "" : "s")} matching {what} {(_matches.Count == 1 ? "is" : "are")} " +
                            $"still open after {_timeoutMs} ms{(window != null ? $": '{window.Title}'" : "")}. It may show a prompt " +
                            "(get_owned_windows), or the app keeps it open; check it before you go on.";
                    break;

                case WindowWaitCondition.TitleChanged:
                    window = _lastSeen ?? _tracked;
                    error = $"The title of window {_tracked!.Handle} is still '{_previousTitle}' after {_timeoutMs} ms." +
                            (_titleFiltersMissedAtStart
                                ? $" It already did not match {TitleFilters(_selector).Describe()} when the wait started, so it may have " +
                                  "changed before the call: pass the title you read before acting as previousTitle."
                                : "");
                    break;

                case var _ when _request.Count is { } count:
                    error = CountTimedOut(count, what);
                    break;

                case var _ when _request.All:
                    error = AllTimedOut(what, waited);
                    break;

                case WindowWaitCondition.Visible or WindowWaitCondition.Minimized or WindowWaitCondition.Maximized when _matches.Count > 0:
                    // A window on screen (minimized, maximized) at the end came to be so less than stableMs before it
                    (window, others) = OneOrSeveral(_matches);
                    error = (_matches.Any(Holds)
                                ? $"No window matching {what} stayed {Wanted} for {_stableMs} ms {waited}: "
                                : $"No window matching {what} {(_request.Condition == WindowWaitCondition.Visible ? "came on screen" : $"was {Wanted}")} {waited}: ") +
                            string.Join("; ", _matches.Take(WindowResolution.MaxCandidates).Select(w =>
                                $"'{w.Title}' is {(Holds(w) ? $"{Wanted}, but for less than {_stableMs} ms" : Unlike(w))}")) + ".";
                    break;

                case WindowWaitCondition.Foreground when _matches.Count > 0:
                    (window, others) = OneOrSeveral(_matches);
                    var front = _service._windows.GetForegroundWindow();
                    error = front != null && _matches.Any(w => w.Handle == front.Handle)
                        ? $"No window matching {what} stayed in front for {_stableMs} ms {waited}: '{front.Title}' ({front.ProcessName}, " +
                          $"PID {front.ProcessId}) came to the front less than {_stableMs} ms before the timeout."
                        : $"No window matching {what} came to the front {waited}; the window in front is " +
                          $"{(front == null ? "none" : $"'{front.Title}' ({front.ProcessName}, PID {front.ProcessId})")}. " +
                          "Bring it there with focus_window if that is what you want.";
                    break;

                case WindowWaitCondition.InputIdle when _lastSeen != null:
                    window = _lastSeen;
                    error = $"Window '{_lastSeen.Title}' was not ready for input {waited}: {_note ?? "it did not report input idle"}.";
                    break;

                default:
                    // exists, and a condition that saw no window: the windows that appeared, as last seen
                    (window, others) = OneOrSeveral(_matches);
                    error = _matches.Count > 0
                        ? $"Windows matching {what} appeared, but none stayed for {_stableMs} ms {waited}."
                        : $"No window matching {what} appeared {waited}. Check what is open with find_windows; {NoMatchHints()}.";
                    break;
            }

            if (IsSetWait)
                return SetResult(_matches) with { TimedOut = true, ErrorCode = nameof(WindowErrorCode.WaitTimedOut), Error = error + NothingChanged };

            return new WindowConditionResult
            {
                TimedOut = true,
                Window = window,
                Candidates = others,
                MatchCount = _request.Condition == WindowWaitCondition.TitleChanged ? 0 : _matches.Count,
                PreviousTitle = _request.Condition == WindowWaitCondition.TitleChanged ? _previousTitle : null,
                ErrorCode = nameof(WindowErrorCode.WaitTimedOut),
                Error = error + NothingChanged
            };
        }

        private const string NothingChanged = " Nothing was changed; wait again (a longer timeoutMs) or check the state with a read tool.";

        /// <summary>A count's timeout: how many windows counted, and whether the same ones held for stableMs.</summary>
        private string CountTimedOut(WindowCount count, string what)
        {
            var open = $"{_matches.Count} window{(_matches.Count == 1 ? "" : "s")} matching {what} {(_matches.Count == 1 ? "is" : "are")} " +
                       $"open after {_timeoutMs} ms";
            if (_held != null)
            {
                return $"{open}, {count.Describe()}, but not the same windows for {_stableMs} ms: one opened or closed less than " +
                       $"{_stableMs} ms before the timeout.";
            }

            return $"{open}, not {count.Describe()}." + (_matches.Count == 0 ? $" Check what is open with find_windows; {NoMatchHints()}." : "");
        }

        /// <summary>An all wait's timeout: no window, the windows that are not as wanted, or a set that changed.</summary>
        private string AllTimedOut(string what, string waited)
        {
            if (_matches.Count == 0)
            {
                return $"No window matching {what} is open after {_timeoutMs} ms, and all needs at least one. Check what is open with " +
                       $"find_windows; {NoMatchHints()}.";
            }

            var holding = _matches.Count(Holds);
            if (holding == _matches.Count && holding == 1)
            {
                return $"The one window matching {what} is {Wanted}, but not for {_stableMs} ms {waited}: it came, or came to be " +
                       $"{Wanted}, less than {_stableMs} ms before the timeout.";
            }

            if (holding == _matches.Count)
            {
                return $"All {_matches.Count} windows matching {what} are {Wanted}, but not the same windows for {_stableMs} ms {waited}: " +
                       $"one came, went or came to be {Wanted} less than {_stableMs} ms before the timeout.";
            }

            return $"{holding} of {_matches.Count} windows matching {what} {(holding == 1 ? "is" : "are")} {Wanted} after {_timeoutMs} ms: " +
                   string.Join("; ", _matches.Where(w => !Holds(w)).Take(WindowResolution.MaxCandidates).Select(w => $"'{w.Title}' is {Unlike(w)}")) +
                   ".";
        }

        private static (WindowInfo? Window, IReadOnlyList<WindowInfo> Candidates) OneOrSeveral(IReadOnlyList<WindowInfo> windows) =>
            windows.Count == 1 ? (windows[0], []) : (null, windows.Take(MaxSetWindows).ToList());

        private static string OffScreenReason(WindowInfo window) =>
            window.State == WindowState.Minimized ? "minimized"
            : window.IsCloaked ? "cloaked (on another virtual desktop, or suspended)"
            : "hidden";

        /// <summary>Which windows count, beside the selector: " that was not open when the wait started", the excluded ones.</summary>
        private string Qualifier()
        {
            var parts = new List<string>();
            if (_openAtStart != null)
                parts.Add("not open when the wait started");
            if (_excluded.Count > 0)
                parts.Add(_excluded.Count == 1 ? "not the excluded handle" : $"not one of the {_excluded.Count} excluded handles");
            return parts.Count == 0 ? "" : $" ({string.Join(", ", parts)})";
        }

        /// <summary>What the listing leaves out, for a selector that matched nothing.</summary>
        private string NoMatchHints()
        {
            var hints = new List<string>();
            if (!string.IsNullOrEmpty(_selector.ProcessName) && !_selector.ProcessNameContains)
                hints.Add("processName is the whole process name (set processNameContains for a part of it)");
            if (!_selector.IncludeCloaked)
                hints.Add("windows on other virtual desktops need includeCloaked");
            if (!_selector.IncludeUntitled && string.IsNullOrEmpty(_selector.ClassName))
                hints.Add("untitled windows need includeUntitled or className");
            hints.Add("hidden windows only by handle");
            return string.Join(", ", hints);
        }
    }
}
