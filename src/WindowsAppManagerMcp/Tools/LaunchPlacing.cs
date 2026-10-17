using WindowsAppManagerMcp.Models;
using WindowsAppManagerMcp.Models.Results;
using WindowsAppManagerMcp.Services;
using WindowsAppManagerMcp.Services.Interfaces;

namespace WindowsAppManagerMcp.Tools;

/// <summary>A launch whose window may be placed: whether it succeeded, the window it found, its status and its checked plan.</summary>
internal sealed record LaunchToPlace(bool Success, nint? Window, LaunchStatus? Status, LaunchPlacementPlan? Plan);

/// <summary>
/// The launch tools' placement (roadmap M3.13): launch_application's, launch_applications_batch's and, since roadmap
/// M5.4, launch_browser_with_debug's. A placement is checked before anything is launched and applied to the launch's
/// window through <see cref="ILaunchPlacementService"/>; a failure never hides a successful launch.
/// </summary>
internal static class LaunchPlacing
{
    /// <summary>
    /// Checks a placement: its monitor resolved to an index (roadmap M6.7: MonitorNotFound, AmbiguousMonitor), the placement
    /// itself (<see cref="LaunchPlacementPlan.TryCreate"/>), and the index
    /// against the monitors there are when <paramref name="monitors"/> is known. A name's monitor is kept by its id (or
    /// device name) in <see cref="LaunchPlacementPlan.MonitorKey"/>, so the placement finds it again once the window is there.
    /// </summary>
    public static bool TryPlan(LaunchPlacement placement, IMonitorService? monitors, out LaunchPlacementPlan? plan, out string? errorCode,
        out string? error)
    {
        plan = null;
        if (!MonitorTargeting.TryChoose(monitors, placement.Monitor, allowAll: false, "placement.", out var choice,
                out errorCode, out error))
        {
            error = MonitorTargeting.WithoutNothingDone(error!);
            return false;
        }

        if (!LaunchPlacementPlan.TryCreate(placement, choice.Index, out plan, out errorCode, out error))
        {
            return false;
        }

        if (choice.Named is { } named)
        {
            plan = plan! with { MonitorKey = named.Id ?? named.DeviceName };
        }

        if (plan!.MonitorIndex is { } index && monitors?.GetAllMonitors().Count is { } count && index >= count)
        {
            plan = null;
            errorCode = nameof(WindowErrorCode.InvalidMonitorIndex);
            error = $"placement monitor: {WindowService.MonitorIndexMessage(index, count)}";
            return false;
        }

        return true;
    }

    /// <summary>
    /// Places the windows of the launches that have a plan, all in one go (<see cref="ILaunchPlacementService"/>), and
    /// returns each launch's placement result, in order: null without a plan or for a launch that failed, WindowNotFound
    /// for a launch that found no window.
    /// </summary>
    public static async Task<IReadOnlyList<LaunchPlacementResultDto?>> PlaceAsync(
        ILaunchPlacementService? placementService, IReadOnlyList<LaunchToPlace> launches, CancellationToken cancellationToken)
    {
        var placed = new LaunchPlacementResultDto?[launches.Count];
        var placements = new List<(nint Handle, LaunchPlacementPlan Plan)>();
        var positions = new List<int>();
        for (var i = 0; i < launches.Count; i++)
        {
            var launch = launches[i];
            if (launch.Plan is not { } plan || !launch.Success)
                continue;

            if (launch.Window is { } window)
            {
                placements.Add((window, plan));
                positions.Add(i);
            }
            else
            {
                placed[i] = new LaunchPlacementResultDto(null, null, null, Focused: false, Reapplied: false,
                    ErrorCode: nameof(WindowErrorCode.WindowNotFound),
                    Error: $"The launch succeeded (launchStatus {launch.Status?.ToWireName()}) but found no window, so nothing was placed. " +
                           "Look for its window with find_windows and arrange it with snap_window, set_window_bounds or focus_window; " +
                           "do not launch it again.");
            }
        }

        if (placements.Count > 0)
        {
            try
            {
                var service = placementService ?? throw new InvalidOperationException("No launch placement service is registered.");
                var outcomes = await service.PlaceAsync(placements, cancellationToken).ConfigureAwait(false);
                for (var k = 0; k < outcomes.Count; k++)
                {
                    placed[positions[k]] = ToDto(outcomes[k]);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // A placement failure never hides a successful launch: the launch result stays, the placement reports it
                foreach (var position in positions)
                {
                    placed[position] = new LaunchPlacementResultDto(null, null, null, Focused: false, Reapplied: false,
                        ErrorCode: nameof(WindowErrorCode.MoveFailed),
                        Error: $"The launch succeeded, but placing its window failed: {ex.Message} Arrange it with snap_window, " +
                               "set_window_bounds or focus_window; do not launch it again.");
                }
            }
        }

        return placed;
    }

    private static LaunchPlacementResultDto ToDto(LaunchPlacementOutcome outcome)
    {
        var window = outcome.Window;
        var visible = window is null || window.State == WindowState.Minimized ? null : window.VisibleBounds ?? window.Bounds;
        return new LaunchPlacementResultDto(
            FinalBounds: visible is null ? null : new BoundsDto(visible.X, visible.Y, visible.Width, visible.Height),
            FinalState: window?.State.ToString().ToLowerInvariant(),
            MonitorIndex: window?.MonitorIndex,
            Focused: outcome.Focused,
            Reapplied: outcome.Reapplied,
            ErrorCode: outcome.Success ? null : outcome.ErrorCode.ToString(),
            Error: outcome.Error,
            Warning: outcome.Warning);
    }
}
