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
    /// Which of the windows the user can see now: visible and not cloaked. The close tools read this before they send the
    /// close messages, so that <see cref="WaitForCloseAsync"/> reports Hidden only for a window the close hid.
    /// </summary>
    IReadOnlySet<nint> ShownWindows(IReadOnlyList<nint> handles);

    /// <summary>
    /// Waits up to <paramref name="wait"/> for the windows to close, polling IsWindow, then reports for each handle, in
    /// order: Closed; PromptShown with the prompt (<see cref="FindPromptAsync"/>); Hidden (it was in
    /// <paramref name="shownBefore"/> and is now hidden or cloaked: the app hid it instead of closing it); or StillOpen
    /// (also a window that was already hidden or cloaked before the close and still exists). The wait ends early once
    /// every window is closed or shows a Win32 prompt that stayed for two checks in a row. The windows still open at the
    /// end are looked at once more, all at the same time, each UI Automation read under its own deadline. Cancelling the
    /// call ends the wait.
    /// </summary>
    /// <param name="handles">The windows the close messages were sent to.</param>
    /// <param name="shownBefore">The ones the user could see before the close (<see cref="ShownWindows"/>).</param>
    /// <param name="wait">How long to wait for them to close.</param>
    /// <param name="cancellationToken">Ends the wait.</param>
    /// <exception cref="OperationCanceledException">The call was cancelled.</exception>
    Task<IReadOnlyList<CloseObservation>> WaitForCloseAsync(
        IReadOnlyList<nint> handles, IReadOnlySet<nint> shownBefore, TimeSpan wait, CancellationToken cancellationToken);

    /// <summary>
    /// The prompt that keeps <paramref name="window"/> from closing: a Win32 dialog (<see cref="IWindowService.FindBlockingDialog"/>),
    /// whose text and buttons are read through UI Automation when its controls have none (a task dialog), or else a
    /// modal dialog drawn inside the window (UI Automation WindowPattern.IsModal, such as a WinUI ContentDialog). The UI
    /// Automation reads run under a deadline; one that fails or times out finds nothing. Null when there is no prompt.
    /// </summary>
    Task<ClosePrompt?> FindPromptAsync(nint window, CancellationToken cancellationToken);

    /// <summary>
    /// Asks the window to close through UI Automation (WindowPattern.Close), under a deadline: close_application's
    /// fallback for a packaged app's frame window when Windows does not accept the close message. It never forces. True
    /// when the close was requested; false when it could not ask (no UI Automation, no Window pattern, the window is gone,
    /// or the app is not responding); null when the call did not return in time. The deadline is checked right before the
    /// close is requested, so a slow lookup requests nothing, but a request already under way may still close the window.
    /// </summary>
    Task<bool?> CloseWithUiAutomationAsync(nint window, CancellationToken cancellationToken);

    /// <summary>
    /// Waits up to <paramref name="wait"/> for the processes to exit and returns the ones still running then (empty when
    /// all have exited). Cancelling the call ends the wait.
    /// </summary>
    /// <exception cref="OperationCanceledException">The call was cancelled.</exception>
    Task<IReadOnlyList<int>> WaitForProcessesToExitAsync(IReadOnlyList<int> processIds, TimeSpan wait, CancellationToken cancellationToken);
}
