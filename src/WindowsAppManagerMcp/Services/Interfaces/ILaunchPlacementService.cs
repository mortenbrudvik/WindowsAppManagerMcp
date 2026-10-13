using WindowsAppManagerMcp.Models;
using WindowsAppManagerMcp.Models.Results;

namespace WindowsAppManagerMcp.Services.Interfaces;

/// <summary>
/// Places the windows of launches (roadmap M3.13): launch_application's and launch_applications_batch's placement. The
/// geometry goes through <see cref="IWindowService"/> (the M2.3 placements: restore without activating, the visible frame,
/// the DPI second pass, hang-safe calls) and the focus through <see cref="IForegroundService.EnsureForeground"/>, as
/// focus_window does. The caller holds the input gate.
/// </summary>
public interface ILaunchPlacementService
{
    /// <summary>
    /// Places each window as its plan asks, in order, then waits <see cref="LaunchPlacementService.DefaultRecheckDelay"/> once
    /// and reads every placed window again: one that moved, resized or changed state on its own meanwhile (an app that
    /// restores its saved bounds after it shows its window) is placed once more. Then each window whose plan asks for focus
    /// is brought to the front, in order (the last one ends in front), and every window is read for the result.
    /// <para>
    /// A window is not touched at all when it is gone (InvalidHandle), belongs to the agent's own host (ProtectedWindow),
    /// is denied by the target policy (TargetDeniedByPolicy), or while the user has paused input (PausedByUser). The first
    /// step that fails ends that window's placement; the others go on.
    /// </para>
    /// </summary>
    /// <param name="placements">The windows and their checked plans.</param>
    /// <param name="cancellationToken">Ends the placement between windows and during the wait; what was placed stays placed.</param>
    /// <returns>One outcome per placement, in order.</returns>
    Task<IReadOnlyList<LaunchPlacementOutcome>> PlaceAsync(
        IReadOnlyList<(nint Handle, LaunchPlacementPlan Plan)> placements,
        CancellationToken cancellationToken = default);
}
