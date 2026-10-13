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
    /// How long after the placement the windows are read again: apps such as browsers and VS Code restore their saved
    /// bounds shortly after they first show their window, which would undo a placement made at once.
    /// </summary>
    public static readonly TimeSpan DefaultRecheckDelay = TimeSpan.FromMilliseconds(500);

    private readonly IWindowService _windows;
    private readonly IForegroundService _foreground;
    private readonly IInputSession? _session;
    private readonly TimeSpan _recheckDelay;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;

    /// <param name="windows">Reads and places the windows.</param>
    /// <param name="foreground">Brings a window to the front (focus).</param>
    /// <param name="session">The input session: nothing is placed or focused while the user has paused input.</param>
    /// <param name="recheckDelay">How long to wait before the windows are read again; default <see cref="DefaultRecheckDelay"/>.</param>
    /// <param name="delay">Waits; default <see cref="Task.Delay(TimeSpan, CancellationToken)"/> (tests pass their own).</param>
    public LaunchPlacementService(
        IWindowService windows,
        IForegroundService foreground,
        IInputSession? session = null,
        TimeSpan? recheckDelay = null,
        Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        _windows = windows;
        _foreground = foreground;
        _session = session;
        _recheckDelay = recheckDelay ?? DefaultRecheckDelay;
        _delay = delay ?? Task.Delay;
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

        // 1. Each window placed as its plan asks, in order
        for (var i = 0; i < count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var (handle, plan) = placements[i];
            failures[i] = Refusal(handle);
            if (failures[i] != null || !plan.ChangesGeometry)
                continue;

            var result = Apply(handle, plan);
            if (!result.Success)
            {
                failures[i] = result;
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

        // 2. Once for all of them: read again after a moment, and place once more a window the app moved back
        if (placed.Any(p => p != null))
        {
            await _delay(_recheckDelay, cancellationToken).ConfigureAwait(false);
            for (var i = 0; i < count; i++)
            {
                if (placed[i] is not { } before || Read(placements[i].Handle) == before)
                    continue;

                var (handle, plan) = placements[i];
                if (_session?.IsPaused == true)
                {
                    failures[i] = WindowChangeResult.Failed(handle, PausedByUser,
                        "The window was placed, but the app moved it again on its own (it restored its saved position or size), and it " +
                        "was not placed a second time because the user paused input (pause hotkey). Stop and ask the user; never resume " +
                        "input yourself.");
                    continue;
                }

                reapplied[i] = true;
                var again = Apply(handle, plan);
                if (!again.Success)
                {
                    failures[i] = again with
                    {
                        Error = "The window was placed, but the app moved it again on its own (it restored its saved position or " +
                                $"size), and placing it a second time failed: {again.Error}"
                    };
                }
            }
        }

        // 3. Focus, in order, so the last window that asks for it ends in front
        for (var i = 0; i < count; i++)
        {
            var (handle, plan) = placements[i];
            if (!plan.Focus || failures[i] != null)
                continue;

            cancellationToken.ThrowIfCancellationRequested();
            failures[i] = Focus(handle);
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
                Error: failure?.Error));
        }

        return outcomes;
    }

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
    /// The plan's geometry: snap or bounds (placing restores the window to normal first, without activating it), or a move
    /// to monitorIndex (maximized there for state maximized), or the state alone; then minimized for state minimized. A
    /// window that does not answer is NotResponding.
    /// </summary>
    private WindowChangeResult Apply(nint handle, LaunchPlacementPlan plan)
    {
        try
        {
            var result = plan switch
            {
                { Snap: { } snap } => _windows.SnapWindow(handle, snap, plan.MonitorIndex, bringToFront: true),
                { Bounds: { } b } => _windows.SetWindowFrame(handle, b.X, b.Y, b.Width, b.Height, bringToFront: true),
                { MonitorIndex: { } monitor } => _windows.MoveWindowToMonitor(handle, monitor,
                    plan.State == LaunchWindowState.Maximized ? MonitorPositioning.Maximize : MonitorPositioning.Center, bringToFront: true),
                { State: LaunchWindowState.Maximized } => _windows.SetWindowState(handle, WindowStateRequest.Maximize),
                { State: LaunchWindowState.Normal } => RestoreToNormal(handle),
                _ => WindowChangeResult.Done(handle)
            };

            return result.Success && plan.State == LaunchWindowState.Minimized
                ? _windows.SetWindowState(handle, WindowStateRequest.Minimize)
                : result;
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
    /// Brings the window to the front as focus_window does (<see cref="IForegroundService.EnsureForeground"/>); null when
    /// it is in front, else why not (FocusFailed for an unexpected failure, which never hides the launch).
    /// </summary>
    private WindowChangeResult? Focus(nint handle)
    {
        try
        {
            var outcome = _foreground.EnsureForeground(handle);
            return outcome.IsForeground
                ? null
                : WindowChangeResult.Failed(handle, outcome.ErrorCode == None ? FocusFailed : outcome.ErrorCode,
                    outcome.Error ?? "The window did not come to the front.");
        }
        catch (WindowNotRespondingException ex)
        {
            return WindowChangeResult.Failed(handle, NotResponding, ex.Message);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return WindowChangeResult.Failed(handle, FocusFailed, $"Bringing the window to the front failed: {ex.Message}");
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
