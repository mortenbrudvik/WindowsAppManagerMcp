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
/// <see cref="MaxInputIdleWaitMs"/> per poll and skipped while Windows reports the window hung. It polls for now; event
/// wake-ups (SetWinEventHook) come with roadmap M7.2.
/// </remarks>
public sealed partial class WindowWaitService
{
    /// <summary>How often a condition wait reads the windows after its first <see cref="FastPollPeriodMs"/>.</summary>
    public const int SlowPollIntervalMs = 250;

    /// <summary>How long a condition wait reads the windows every <see cref="PollIntervalMs"/> before it backs off.</summary>
    public const int FastPollPeriodMs = 2000;

    /// <summary>The longest one input_idle poll waits in WaitForInputIdle.</summary>
    public const int MaxInputIdleWaitMs = 250;

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
        if (request.Condition == WindowWaitCondition.TitleChanged && !initial.Success)
        {
            return Ended(initial.ErrorCode ?? nameof(WindowErrorCode.WindowNotFound), initial.Error ?? "No single window matched.",
                Elapsed(), initial.Candidates, initial.MatchCount);
        }

        var watch = new ConditionWatch(this, request, timeoutMs, initial,
            request.NewOnly ? new HashSet<nint>(EnumerateTopLevelWindows()) : null);
        long lastProgress = 0;

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var now = Elapsed();
            if (watch.Poll(now, Elapsed) is { } done)
                return done with { ElapsedMs = Elapsed() };

            if (progress != null && now - lastProgress >= ProgressIntervalMs)
            {
                lastProgress = now;
                progress.Report(new OperationProgress((int)now, timeoutMs,
                    $"waiting for {ConditionName(request.Condition)}: {now} of {timeoutMs} ms, {watch.Status}"));
            }

            var remaining = timeoutMs - Elapsed();
            if (remaining <= 0)
                return watch.TimedOut() with { ElapsedMs = Elapsed() };

            var interval = now < FastPollPeriodMs ? PollIntervalMs : SlowPollIntervalMs;
            await _delay(TimeSpan.FromMilliseconds(Math.Min(interval, remaining)), cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>The condition as wait_for_window names it.</summary>
    internal static string ConditionName(WindowWaitCondition condition) => condition switch
    {
        WindowWaitCondition.Exists => "exists",
        WindowWaitCondition.Gone => "gone",
        WindowWaitCondition.Foreground => "foreground",
        WindowWaitCondition.Visible => "visible",
        WindowWaitCondition.TitleChanged => "title_changed",
        WindowWaitCondition.InputIdle => "input_idle",
        _ => condition.ToString()
    };

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
    /// window has matched (gone), the new title and since when (title_changed), and what the last poll saw.
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
        private long? _goneSince;
        private (string Title, long Since)? _newTitle;
        private int? _matchesAtStart;
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
            }
        }

        /// <summary>What the last poll saw, for progress messages.</summary>
        public string Status => _request.Condition switch
        {
            WindowWaitCondition.TitleChanged => $"title '{_lastSeen?.Title ?? _previousTitle}'",
            _ => $"{_matches.Count} matching window{(_matches.Count == 1 ? "" : "s")}"
        };

        /// <summary>One poll: the result when the wait is over (satisfied, or ended early), null to poll again.</summary>
        public WindowConditionResult? Poll(long now, Func<long> elapsed) => _request.Condition switch
        {
            WindowWaitCondition.Gone => PollGone(now),
            WindowWaitCondition.TitleChanged => PollTitle(now),
            _ => PollWindows(now, elapsed)
        };

        /// <summary>exists, visible, foreground and input_idle: a window that counts satisfies the condition, held for stableMs.</summary>
        private WindowConditionResult? PollWindows(long now, Func<long> elapsed)
        {
            if (Read() is { } ended)
                return ended;

            var foreground = _request.Condition == WindowWaitCondition.Foreground ? _service._native.GetForegroundWindow() : nint.Zero;
            var satisfying = _request.Condition switch
            {
                WindowWaitCondition.Visible => _matches.Where(IsOnScreen).ToList(),
                WindowWaitCondition.Foreground => _matches.Where(w => w.Handle == foreground).ToList(),
                _ => _matches.ToList()
            };

            foreach (var gone in _since.Keys.Where(h => satisfying.All(w => w.Handle != h)).ToList())
                _since.Remove(gone);
            foreach (var window in satisfying)
                _since.TryAdd(window.Handle, now);

            var stable = satisfying.Where(w => now - _since[w.Handle] >= _stableMs).ToList();
            if (stable.Count == 0)
                return null;

            if (_request.Condition != WindowWaitCondition.InputIdle)
                return Satisfied(satisfying);

            if (satisfying.Count > 1)
            {
                return Ended(nameof(WindowErrorCode.AmbiguousWindow),
                    $"{satisfying.Count} windows match {_selector.Describe()}, so input_idle cannot tell whose process to wait for; " +
                    "candidates lists them, front to back. Wait again with the one you mean as window {handle, processId}, or narrow " +
                    "the selector.", elapsed(), satisfying, satisfying.Count);
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

        /// <summary>gone: no window that counts matches, for stableMs.</summary>
        private WindowConditionResult? PollGone(long now)
        {
            if (Read(handleClosedEnds: false) is { } ended)
                return ended;

            _matchesAtStart ??= _matches.Count;
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
                Warning = _matchesAtStart == 0
                    ? $"No window matched {_selector.Describe()} when the wait started, so nothing had to close. If you expected one " +
                      "to be open, check the selector with find_windows (untitled windows need includeUntitled or className)."
                    : null
            };
        }

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

                    if (handleClosedEnds && _selector.Handle is { } handle && resolution.ErrorCode == nameof(WindowErrorCode.InvalidHandle))
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
            if (_selector.Index is { } index && !_selector.Handle.HasValue && !_selector.Foreground)
                matches = index < matches.Count ? [matches[index]] : [];

            _matches = matches;
            return null;
        }

        private static bool IsOnScreen(WindowInfo window) =>
            window.IsVisible && !window.IsCloaked && window.State != WindowState.Minimized;

        private static WindowConditionResult Satisfied(IReadOnlyList<WindowInfo> windows) => new()
        {
            Satisfied = true,
            Window = windows.Count == 1 ? windows[0] : null,
            Candidates = windows.Count > 1 ? windows.Take(WindowResolution.MaxCandidates).ToList() : [],
            MatchCount = windows.Count
        };

        /// <summary>The timeout: what was last seen, the window in the way when there is one, and what to do.</summary>
        public WindowConditionResult TimedOut()
        {
            var what = $"{_selector.Describe()}{Qualifier()}";
            var waited = $"within {_timeoutMs} ms";
            WindowInfo? window = null;
            IReadOnlyList<WindowInfo> others = [];
            string error;

            switch (_request.Condition)
            {
                case WindowWaitCondition.Gone:
                    (window, others) = OneOrSeveral(_matches);
                    error = $"{_matches.Count} window{(_matches.Count == 1 ? "" : "s")} matching {what} {(_matches.Count == 1 ? "is" : "are")} " +
                            $"still open after {_timeoutMs} ms{(window != null ? $": '{window.Title}'" : "")}. It may show a prompt " +
                            "(get_owned_windows), or the app keeps it open; check it before you go on.";
                    break;

                case WindowWaitCondition.TitleChanged:
                    window = _lastSeen ?? _tracked;
                    error = $"The title of window {_tracked!.Handle} is still '{_previousTitle}' after {_timeoutMs} ms.";
                    break;

                case WindowWaitCondition.Visible when _matches.Count > 0:
                    (window, others) = OneOrSeveral(_matches);
                    error = $"No window matching {what} came on screen {waited}: " +
                            string.Join("; ", _matches.Take(WindowResolution.MaxCandidates).Select(w => $"'{w.Title}' is {OffScreenReason(w)}")) + ".";
                    break;

                case WindowWaitCondition.Foreground when _matches.Count > 0:
                    (window, others) = OneOrSeveral(_matches);
                    var front = _service._windows.GetForegroundWindow();
                    error = $"No window matching {what} came to the front {waited}; the window in front is " +
                            $"{(front == null ? "none" : $"'{front.Title}' ({front.ProcessName}, PID {front.ProcessId})")}. " +
                            "Bring it there with focus_window if that is what you want.";
                    break;

                case WindowWaitCondition.InputIdle when _lastSeen != null:
                    window = _lastSeen;
                    error = $"Window '{_lastSeen.Title}' was not ready for input {waited}: {_note ?? "it did not report input idle"}.";
                    break;

                default:
                    error = _matches.Count > 0
                        ? $"Windows matching {what} appeared, but none stayed for {_stableMs} ms {waited}."
                        : $"No window matching {what} appeared {waited}. Check what is open with find_windows; {NoMatchHints()}.";
                    break;
            }

            return new WindowConditionResult
            {
                TimedOut = true,
                Window = window,
                Candidates = others,
                MatchCount = _request.Condition == WindowWaitCondition.TitleChanged ? 0 : _matches.Count,
                PreviousTitle = _request.Condition == WindowWaitCondition.TitleChanged ? _previousTitle : null,
                ErrorCode = nameof(WindowErrorCode.WaitTimedOut),
                Error = error + " Nothing was changed; wait again (a longer timeoutMs) or check the state with a read tool."
            };
        }

        private static (WindowInfo? Window, IReadOnlyList<WindowInfo> Candidates) OneOrSeveral(IReadOnlyList<WindowInfo> windows) =>
            windows.Count == 1 ? (windows[0], []) : (null, windows.Take(WindowResolution.MaxCandidates).ToList());

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
