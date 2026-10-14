using WindowsAppManagerMcp.Models;
using WindowsAppManagerMcp.Models.Results;
using WindowsAppManagerMcp.Services.Interfaces;
using static WindowsAppManagerMcp.Models.WindowErrorCode;

namespace WindowsAppManagerMcp.Services;

/// <summary>
/// Places the windows of launches (roadmap M3.13; see <see cref="ILaunchPlacementService"/>). It adds no native call of
/// its own: the geometry is <see cref="IWindowService"/>'s and the activation <see cref="IForegroundService"/>'s.
/// </summary>
public sealed class LaunchPlacementService : ILaunchPlacementService
{
    /// <summary>
    /// How long after the placement the windows are first read again: apps such as browsers and VS Code restore their
    /// saved bounds shortly after they first show their window, which would undo a placement made at once.
    /// </summary>
    public static readonly TimeSpan FirstCheckDelay = TimeSpan.FromMilliseconds(250);

    /// <summary>The longest wait between two reads: each wait is twice the one before it, up to this.</summary>
    public static readonly TimeSpan MaxCheckInterval = TimeSpan.FromSeconds(1);

    /// <summary>How long a window must stay where it was placed before it is no longer watched.</summary>
    public static readonly TimeSpan StableTime = TimeSpan.FromSeconds(1);

    /// <summary>
    /// How long the windows are watched after they are placed, at most (roadmap M3.F27): an app that moves its window
    /// later than this is not followed.
    /// </summary>
    public static readonly TimeSpan SettleTime = TimeSpan.FromSeconds(3);

    /// <summary>
    /// How many times a window the app moved back is placed again; when the app moves it once more, it is left where the
    /// app put it, with a warning, so an app that fights the placement is not fought for the whole settle time.
    /// </summary>
    public const int MaxReapplies = 3;

    private readonly IWindowService _windows;
    private readonly IForegroundService _foreground;
    private readonly IInputSession? _session;
    private readonly TimeProvider _time;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;

    /// <param name="windows">Reads and places the windows.</param>
    /// <param name="foreground">Brings a window to the front (focus).</param>
    /// <param name="session">The input session: nothing is placed or focused while the user has paused input.</param>
    /// <param name="time">Measures how long the windows have been watched; default <see cref="TimeProvider.System"/>.</param>
    /// <param name="delay">
    /// Waits between two reads; default <see cref="Task.Delay(TimeSpan, TimeProvider, CancellationToken)"/> on
    /// <paramref name="time"/>. Tests pass one that moves a manual clock.
    /// </param>
    public LaunchPlacementService(
        IWindowService windows,
        IForegroundService foreground,
        IInputSession? session = null,
        TimeProvider? time = null,
        Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        _windows = windows;
        _foreground = foreground;
        _session = session;
        _time = time ?? TimeProvider.System;
        _delay = delay ?? ((wait, token) => Task.Delay(wait, _time, token));
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<LaunchPlacementOutcome>> PlaceAsync(
        IReadOnlyList<(nint Handle, LaunchPlacementPlan Plan)> placements,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(placements);

        var count = placements.Count;
        var failures = new WindowChangeResult?[count];
        var placed = new Frame?[count];
        var reapplied = new bool[count];
        var warnings = new string?[count];
        var steps = new LaunchPlacementStep[count];
        var activations = new FocusOutcome?[count];

        // 1. Each window placed as its plan asks, in order
        for (var i = 0; i < count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var (handle, plan) = placements[i];
            failures[i] = Refusal(handle);
            steps[i] = LaunchPlacementStep.Check;
            if (failures[i] != null || !plan.ChangesGeometry)
                continue;

            var (result, failedStep) = Apply(handle, plan);
            if (!result.Success)
            {
                failures[i] = result;
                steps[i] = failedStep;
                continue;
            }

            // A window two launches share (two documents in one window) ends where the later placement put it: only that
            // one is checked again, so the two do not undo each other
            for (var earlier = 0; earlier < i; earlier++)
            {
                if (placements[earlier].Handle == handle)
                    placed[earlier] = null;
            }

            placed[i] = Read(handle);
        }

        // 2. All of them together: watched until each stays where it was placed, and placed again when the app moves it back
        if (placed.Any(p => p != null))
            await SettleAsync(placements, placed, failures, steps, reapplied, warnings, cancellationToken).ConfigureAwait(false);

        // 3. Focus, in order, so the last window that asks for it ends in front
        for (var i = 0; i < count; i++)
        {
            var (handle, plan) = placements[i];
            if (!plan.Focus || failures[i] != null)
                continue;

            cancellationToken.ThrowIfCancellationRequested();
            var activation = activations[i] = Focus(handle);
            if (!activation.IsForeground)
            {
                failures[i] = WindowChangeResult.Failed(handle, activation.ErrorCode == None ? FocusFailed : activation.ErrorCode,
                    activation.Error ?? "The window did not come to the front.");
                steps[i] = LaunchPlacementStep.Focus;
            }
        }

        // 4. Every window as it is now
        var outcomes = new List<LaunchPlacementOutcome>(count);
        for (var i = 0; i < count; i++)
        {
            var handle = placements[i].Handle;
            var failure = failures[i];
            var window = failure?.ErrorCode == InvalidHandle ? null : _windows.GetWindowInfo(handle);
            outcomes.Add(new LaunchPlacementOutcome(
                handle,
                window,
                Focused: window != null && _foreground.IsInFront(handle),
                Reapplied: reapplied[i],
                ErrorCode: failure?.ErrorCode ?? None,
                Error: failure?.Error,
                Warning: failure == null ? warnings[i] : null,
                FailedStep: failure == null ? LaunchPlacementStep.None : steps[i],
                Activation: activations[i]));
        }

        return outcomes;
    }

    /// <summary>
    /// Watches the placed windows (roadmap M3.F27): reads them again at increasing intervals, from
    /// <see cref="FirstCheckDelay"/> up to <see cref="MaxCheckInterval"/>, and places again a window that moved, resized or
    /// changed state on its own since it was placed (an app that restores its saved bounds, once or more, early or late).
    /// A window is no longer watched once it has stayed where it was placed for <see cref="StableTime"/> (the wait before
    /// a read is cut so that it comes then), once the app has moved it back more than <see cref="MaxReapplies"/> times
    /// (it is left where the app put it, with a warning), or when a step fails; none is watched after
    /// <see cref="SettleTime"/>. The waits start over from the first interval after a window is placed again. Each
    /// window keeps its own time, and one wait serves them all.
    /// </summary>
    /// <param name="placed">Each watched window's frame as it was placed; null for a window that is not (or no longer) watched.</param>
    private async Task SettleAsync(
        IReadOnlyList<(nint Handle, LaunchPlacementPlan Plan)> placements,
        Frame?[] placed,
        WindowChangeResult?[] failures,
        LaunchPlacementStep[] steps,
        bool[] reapplied,
        string?[] warnings,
        CancellationToken cancellationToken)
    {
        var count = placements.Count;
        var placedAt = new TimeSpan[count];
        var reapplies = new int[count];
        var start = _time.GetTimestamp();
        var interval = FirstCheckDelay;
        while (true)
        {
            var elapsed = _time.GetElapsedTime(start);
            var left = SettleTime - elapsed;
            var watched = Enumerable.Range(0, count).Where(i => placed[i] != null).ToList();
            if (watched.Count == 0 || left <= TimeSpan.Zero)
                return;

            var untilStable = watched.Min(i => StableTime - (elapsed - placedAt[i]));
            var wait = Min(Min(interval, left), untilStable < TimeSpan.Zero ? TimeSpan.Zero : untilStable);
            await _delay(wait, cancellationToken).ConfigureAwait(false);
            interval = Min(interval * 2, MaxCheckInterval);

            var now = _time.GetElapsedTime(start);
            foreach (var i in watched)
            {
                var (handle, plan) = placements[i];
                steps[i] = LaunchPlacementStep.Settle;
                var frame = _windows.IsValidWindow(handle) ? Read(handle) : null;
                if (frame == null)
                {
                    failures[i] = WindowChangeResult.Failed(handle, InvalidHandle,
                        "The launch's window closed after it was placed (an app can replace its first window with another). Look " +
                        "for the app's window with find_windows and arrange it with the window tools; do not launch it again.");
                    placed[i] = null;
                    continue;
                }

                if (frame == placed[i])
                {
                    if (now - placedAt[i] >= StableTime)
                        placed[i] = null;

                    continue;
                }

                placed[i] = null;
                if (_session?.IsPaused == true)
                {
                    failures[i] = WindowChangeResult.Failed(handle, PausedByUser,
                        "The window was placed, but the app moved it again on its own (it restored its saved position or size), and it " +
                        "was not placed again because the user paused input (pause hotkey). Stop and ask the user; never resume " +
                        "input yourself.");
                    continue;
                }

                if (reapplies[i] == MaxReapplies)
                {
                    warnings[i] =
                        $"The app moved its window again each time it was placed ({MaxReapplies + 1} times), so after {MaxReapplies} " +
                        "more placements it was left where the app put it. Arrange it with snap_window or set_window_bounds once the " +
                        "app has settled; do not launch it again.";
                    continue;
                }

                reapplies[i]++;
                reapplied[i] = true;
                var (again, _) = Apply(handle, plan);
                if (!again.Success)
                {
                    failures[i] = again with
                    {
                        Error = "The window was placed, but the app moved it again on its own (it restored its saved position or " +
                                $"size), and placing it again failed: {again.Error}"
                    };
                    continue;
                }

                placed[i] = Read(handle);
                placedAt[i] = now;
                interval = FirstCheckDelay;
            }
        }
    }

    private static TimeSpan Min(TimeSpan a, TimeSpan b) => a < b ? a : b;

    /// <summary>Why a window is not placed at all, before anything is sent to it; null when it can be.</summary>
    private WindowChangeResult? Refusal(nint handle)
    {
        if (!_windows.IsValidWindow(handle) || _windows.GetWindowInfo(handle) is not { } window)
        {
            return WindowChangeResult.Failed(handle, InvalidHandle,
                "The launch's window closed before it could be placed. Look for the app's window with find_windows; do not launch it again.");
        }

        if (_session?.IsPaused == true)
        {
            return WindowChangeResult.Failed(handle, PausedByUser,
                "The app was launched, but its window was not placed or focused because the user paused input (pause hotkey). Stop " +
                "and ask the user; never resume input yourself.");
        }

        if (window.IsAgentHost)
        {
            return WindowChangeResult.Failed(handle, ProtectedWindow,
                $"The launch's window ({window.ProcessName}, '{window.Title}') belongs to the agent's own host (the terminal or editor " +
                "this agent runs in), so it was not moved or focused. If you really mean to arrange it, use snap_window, " +
                "set_window_bounds or focus_window with its handle.");
        }

        return _windows.EvaluateTargetPolicy(handle) is { Allowed: false } verdict
            ? WindowChangeResult.Failed(handle, TargetDeniedByPolicy, WindowService.WindowChangeRefusal(handle, verdict))
            : null;
    }

    /// <summary>
    /// The plan's geometry: snap or bounds (the visible frame, or for <see cref="BoundsFrame.Outer"/> the window rect;
    /// placing restores the window to normal first, without activating it), or a move to monitorIndex (maximized there for
    /// state maximized), or the state alone; then minimized for state minimized. Returns the step that failed with its
    /// result: <see cref="LaunchPlacementStep.Place"/> when the window was not placed, <see cref="LaunchPlacementStep.Minimize"/>
    /// when it was placed but not minimized.
    /// </summary>
    private (WindowChangeResult Result, LaunchPlacementStep FailedStep) Apply(nint handle, LaunchPlacementPlan plan)
    {
        var placed = Guarded(handle, () => plan switch
        {
            { Snap: { } snap } => _windows.SnapWindow(handle, snap, plan.MonitorIndex, bringToFront: true),
            { Bounds: { } b, Frame: BoundsFrame.Outer } => _windows.SetWindowBounds(handle, b.X, b.Y, b.Width, b.Height, bringToFront: true),
            { Bounds: { } b } => _windows.SetWindowFrame(handle, b.X, b.Y, b.Width, b.Height, bringToFront: true),
            { MonitorIndex: { } monitor } => _windows.MoveWindowToMonitor(handle, monitor,
                plan.State == LaunchWindowState.Maximized ? MonitorPositioning.Maximize : MonitorPositioning.Center, bringToFront: true),
            { State: LaunchWindowState.Maximized } => _windows.SetWindowState(handle, WindowStateRequest.Maximize),
            { State: LaunchWindowState.Normal } => RestoreToNormal(handle),
            _ => WindowChangeResult.Done(handle)
        });
        if (!placed.Success)
            return (placed, LaunchPlacementStep.Place);

        if (plan.State != LaunchWindowState.Minimized)
            return (placed, LaunchPlacementStep.None);

        var minimized = Guarded(handle, () => _windows.SetWindowState(handle, WindowStateRequest.Minimize));
        return (minimized, minimized.Success ? LaunchPlacementStep.None : LaunchPlacementStep.Minimize);
    }

    /// <summary>
    /// Runs a change of the window's geometry or state: a window that does not answer is NotResponding, and an unexpected
    /// failure MoveFailed.
    /// </summary>
    private static WindowChangeResult Guarded(nint handle, Func<WindowChangeResult> change)
    {
        try
        {
            return change();
        }
        catch (WindowNotRespondingException ex)
        {
            return WindowChangeResult.Failed(handle, NotResponding, ex.Message);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The launch has succeeded: a failure here is the placement's (MoveFailed), never the launch's
            return WindowChangeResult.Failed(handle, MoveFailed, $"Placing the window failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Brings the window to the front as focus_window does (<see cref="IForegroundService.EnsureForeground"/>) and returns
    /// what the activation found; a window that does not answer is NotResponding, and an unexpected failure (which never
    /// hides the launch) FocusFailed.
    /// </summary>
    private FocusOutcome Focus(nint handle)
    {
        try
        {
            return _foreground.EnsureForeground(handle);
        }
        catch (WindowNotRespondingException ex)
        {
            return new FocusOutcome { Target = handle, ErrorCode = NotResponding, Error = ex.Message };
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new FocusOutcome { Target = handle, ErrorCode = FocusFailed, Error = $"Bringing the window to the front failed: {ex.Message}" };
        }
    }

    /// <summary>
    /// Restore until the window is normal: twice for a window that was maximized before it was minimized, which the first
    /// Restore returns to maximized.
    /// </summary>
    private WindowChangeResult RestoreToNormal(nint handle)
    {
        for (var attempt = 0; attempt < 2 && _windows.GetWindowInfo(handle) is { State: not WindowState.Normal }; attempt++)
        {
            var result = _windows.SetWindowState(handle, WindowStateRequest.Restore);
            if (!result.Success)
                return result;
        }

        return _windows.GetWindowInfo(handle) is { State: not WindowState.Normal } window
            ? WindowChangeResult.Failed(handle, StateChangeFailed,
                $"The window did not become normal: it is {window.State.ToString().ToLowerInvariant()}. The application may have refused the change.")
            : WindowChangeResult.Done(handle);
    }

    /// <summary>Where a window is: its state, its visible frame (none when minimized) and its monitor; null when it is gone.</summary>
    private Frame? Read(nint handle) => _windows.GetWindowInfo(handle) is { } window
        ? new Frame(window.State, window.State == WindowState.Minimized ? null : window.VisibleBounds ?? window.Bounds, window.MonitorIndex)
        : null;

    private sealed record Frame(WindowState State, WindowRect? Bounds, int MonitorIndex);
}
