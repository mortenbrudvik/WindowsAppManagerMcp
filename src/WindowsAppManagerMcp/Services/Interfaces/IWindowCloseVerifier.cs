using WindowsAppManagerMcp.Models;

namespace WindowsAppManagerMcp.Services.Interfaces;

/// <summary>
/// Finds out what a close request led to (roadmap M2.5): whether the window closed, waits behind a prompt, was hidden,
/// or is still open. It only reads windows, except <see cref="CloseWithUiAutomationAsync"/>. The waits run outside the
/// input gate: the close tools post their close messages inside it and release it before they wait.
/// </summary>
public interface IWindowCloseVerifier
{
    /// <summary>
    /// Waits up to <paramref name="wait"/> for the windows to close, polling IsWindow, then reports for each handle, in
    /// order: Closed; PromptShown with the prompt (<see cref="FindPromptAsync"/>); Hidden (it exists but is hidden or
    /// cloaked); or StillOpen. The wait ends early once every window is closed or shows a Win32 prompt that stayed for
    /// two checks in a row. Cancelling the call ends the wait.
    /// </summary>
    /// <exception cref="OperationCanceledException">The call was cancelled.</exception>
    Task<IReadOnlyList<CloseObservation>> WaitForCloseAsync(IReadOnlyList<nint> handles, TimeSpan wait, CancellationToken cancellationToken);

    /// <summary>
    /// The prompt that keeps <paramref name="window"/> from closing: a Win32 dialog (<see cref="IWindowService.FindBlockingDialog"/>),
    /// whose text and buttons are read through UI Automation when its controls have none (a task dialog), or else a
    /// modal dialog drawn inside the window (UI Automation WindowPattern.IsModal, such as a WinUI ContentDialog). The UI
    /// Automation reads run under a deadline; one that fails or times out finds nothing. Null when there is no prompt.
    /// </summary>
    Task<ClosePrompt?> FindPromptAsync(nint window, CancellationToken cancellationToken);

    /// <summary>
    /// Asks the window to close through UI Automation (WindowPattern.Close), under a deadline: close_application's
    /// fallback for a packaged app's frame window when Windows does not accept the close message. It never forces. False
    /// when it could not ask (no UI Automation, no Window pattern, the window is gone, or the call did not return in time).
    /// </summary>
    Task<bool> CloseWithUiAutomationAsync(nint window, CancellationToken cancellationToken);

    /// <summary>
    /// Waits up to <paramref name="wait"/> for the processes to exit and returns the ones still running then (empty when
    /// all have exited). Cancelling the call ends the wait.
    /// </summary>
    /// <exception cref="OperationCanceledException">The call was cancelled.</exception>
    Task<IReadOnlyList<int>> WaitForProcessesToExitAsync(IReadOnlyList<int> processIds, TimeSpan wait, CancellationToken cancellationToken);
}
