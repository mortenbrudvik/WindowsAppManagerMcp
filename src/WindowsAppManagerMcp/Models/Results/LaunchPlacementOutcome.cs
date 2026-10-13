namespace WindowsAppManagerMcp.Models.Results;

/// <summary>
/// What a launch placement (roadmap M3.13) did to the launch's window: the window as it is afterwards, whether it is in
/// front, whether it was placed a second time because the app moved it back, and why the placement failed, if it did.
/// </summary>
/// <param name="Handle">The window that was placed.</param>
/// <param name="Window">The window read after the placement; null when it is gone.</param>
/// <param name="Focused">True when the window, or a dialog it owns, is the foreground window afterwards.</param>
/// <param name="Reapplied">
/// True when the window had moved, resized or changed state on its own shortly after the placement (an app that restores
/// its saved bounds after it shows its window), and was placed once more.
/// </param>
/// <param name="ErrorCode">Why the placement failed; <see cref="WindowErrorCode.None"/> when it did everything asked.</param>
/// <param name="Error">What went wrong and what the agent can do; null on success.</param>
public sealed record LaunchPlacementOutcome(
    nint Handle,
    WindowInfo? Window,
    bool Focused,
    bool Reapplied,
    WindowErrorCode ErrorCode = WindowErrorCode.None,
    string? Error = null)
{
    /// <summary>True when the placement did everything it was asked to.</summary>
    public bool Success => ErrorCode == WindowErrorCode.None;
}
