namespace WindowsAppManagerMcp.Models.Results;

/// <summary>
/// What a launch placement (roadmap M3.13) did to the launch's window: the window as it is afterwards, whether it is in
/// front, whether it was placed again because the app moved it back, and why the placement failed, if it did.
/// </summary>
/// <param name="Handle">The window that was placed.</param>
/// <param name="Window">The window read after the placement; null when it is gone.</param>
/// <param name="Focused">True when the window, or a dialog it owns, is the foreground window afterwards.</param>
/// <param name="Reapplied">
/// True when the window had moved, resized or changed state on its own while it was watched after the placement (an app
/// that restores its saved bounds after it shows its window), and was placed again.
/// </param>
/// <param name="ErrorCode">Why the placement failed; <see cref="WindowErrorCode.None"/> when it did everything asked.</param>
/// <param name="Error">What went wrong and what the agent can do; null on success.</param>
/// <param name="Warning">
/// What the agent should know although the placement did not fail: the app moved its window back more often than it is
/// placed again (roadmap M3.F27), so it was left where the app put it. Null otherwise.
/// </param>
/// <param name="FailedStep">
/// The step that failed, so a caller can tell a window that was never placed from one that was placed and then not
/// minimized, kept in place or focused (apply_layout reports them apart); <see cref="LaunchPlacementStep.None"/> on success.
/// </param>
/// <param name="Activation">
/// The verified activation's outcome, as focus_window's, when the plan asked for focus and the window got that far (an
/// unexpected failure is reported as FocusFailed); null otherwise.
/// </param>
public sealed record LaunchPlacementOutcome(
    nint Handle,
    WindowInfo? Window,
    bool Focused,
    bool Reapplied,
    WindowErrorCode ErrorCode = WindowErrorCode.None,
    string? Error = null,
    string? Warning = null,
    LaunchPlacementStep FailedStep = LaunchPlacementStep.None,
    FocusOutcome? Activation = null)
{
    /// <summary>True when the placement did everything it was asked to.</summary>
    public bool Success => ErrorCode == WindowErrorCode.None;
}

/// <summary>The step of a launch placement that failed (<see cref="LaunchPlacementOutcome.FailedStep"/>, roadmap M3.F27).</summary>
public enum LaunchPlacementStep
{
    /// <summary>Nothing failed.</summary>
    None,

    /// <summary>
    /// Before anything was sent to the window: it is gone, input is paused, it belongs to the agent's own host, or the
    /// user's policy denies it. The window was not touched.
    /// </summary>
    Check,

    /// <summary>The geometry (snap, bounds, monitor or state): the window was not placed.</summary>
    Place,

    /// <summary>Minimizing the window after it was placed (state minimized): the window was placed.</summary>
    Minimize,

    /// <summary>
    /// While the placed window was watched: it closed, or the app moved it back and it was not placed again (input was
    /// paused, or placing it again failed).
    /// </summary>
    Settle,

    /// <summary>Bringing the placed window to the front.</summary>
    Focus
}
