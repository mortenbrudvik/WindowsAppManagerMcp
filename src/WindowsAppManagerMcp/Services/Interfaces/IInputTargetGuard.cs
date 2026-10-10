using WindowsAppManagerMcp.Models;

namespace WindowsAppManagerMcp.Services.Interfaces;

/// <summary>What keyboard input a call sends.</summary>
public enum KeyboardInputKind
{
    /// <summary>type_text: characters.</summary>
    Text,

    /// <summary>send_keys: keys and key chords.</summary>
    Keys
}

/// <summary>
/// Decides, inside the input gate and before every SendInput batch, whether input may be sent and where it
/// would land (roadmap M1.1, M1.5, M1.12). Every input tool goes through it, with or without a target. The
/// checks run in this order, and the first one that fails is reported (nothing is sent):
/// <list type="number">
/// <item>The request: send_keys with a destructive chord and no target (DestructiveChordNeedsTarget).</item>
/// <item>Desktop probes: SessionLocked, DesktopUnavailable.</item>
/// <item>An explicit target: InvalidHandle; then its probes, policy and host checks (TargetElevated,
/// TargetDeniedByPolicy, TargetIsAgentHost unless allowHostTarget); then BlockedByModal when a modal
/// dialog disables it.</item>
/// <item>The receiving window (keys: the foreground window, after EnsureForeground brought the target to
/// the front; mouse: the top-level window at the point; no window there is TargetOccluded). With a target it
/// must belong to the target first (TargetNotForeground, TargetOccluded). Then the same probes, policy and
/// host checks as for a target; allowHostTarget lifts the host check only for the target's top-level window
/// and the windows it owns. Keys never go to a modal dialog of the target (BlockedByModal).</item>
/// <item>type_text only: TargetInMenuMode when a menu is active in the receiving window.</item>
/// <item>Mouse only: MouseInUse when another window has captured the mouse (on the foreground thread, or on the
/// thread of the window at the point), so the input would go there; a capture by the window at the point, or by a
/// window of its thread that passes the same rules (a menu's owner), is fine.</item>
/// </list>
/// The re-checks before later batches are cheap while the receiving window stays the same.
/// </summary>
/// <remarks>
/// Owned dialogs count as part of their owner by the owner chain (<c>WindowOwnerChain</c>), as in
/// <see cref="IForegroundService.IsInFront"/>. The activation input of
/// <see cref="IForegroundService.EnsureForeground"/> (one zero-distance mouse move, at most one Esc while the
/// target is verified in front) follows that service's own rules and is not checked here.
/// </remarks>
public interface IInputTargetGuard
{
    /// <summary>
    /// Checks keyboard input before its first batch. With a target window, brings it to the front first
    /// (EnsureForeground); this is the only check that changes anything.
    /// </summary>
    /// <param name="target">The target window, or none.</param>
    /// <param name="kind">Text (type_text) or keys (send_keys).</param>
    /// <param name="keys">For send_keys, the parsed events, checked for destructive chords; otherwise null.</param>
    InputGuardResult CheckKeyboard(InputTarget target, KeyboardInputKind kind, IReadOnlyList<InputEvent>? keys);

    /// <summary>
    /// Checks again right before every batch of the same call. Allowed while the foreground window is the one
    /// <paramref name="approved"/> named, or another window of the anchor (a modeless dialog it opened) that
    /// passes the window checks. A modal dialog that opened over the anchor or over the window that got the
    /// keys is BlockedByModal; any other window is TargetLostFocus. Reads only.
    /// </summary>
    InputGuardResult RecheckKeyboard(InputGuardResult approved);

    /// <summary>Checks mouse input at a screen point before the cursor moves. Reads only.</summary>
    InputGuardResult CheckMouse(int x, int y, InputTarget target);

    /// <summary>
    /// Checks again before a later batch at the same point (the click after the cursor move, the second
    /// click of a double click). Allowed while the same window is at the point; otherwise the new window is
    /// checked in full. Reads only.
    /// </summary>
    InputGuardResult RecheckMouse(int x, int y, InputGuardResult approved);

    /// <summary>The foreground window now, for the results' ForegroundAfter; null when there is none.</summary>
    WindowSummary? DescribeForeground();
}

/// <summary>
/// The decision of <see cref="IInputTargetGuard"/>: allowed, or the error code and message to report, with
/// the window the input goes to (or would have gone to).
/// </summary>
public sealed record InputGuardResult
{
    /// <summary>True when the input may be sent.</summary>
    public bool Allowed => ErrorCode == InputErrorCode.None;

    /// <summary><see cref="InputErrorCode.None"/> when allowed; otherwise why not.</summary>
    public InputErrorCode ErrorCode { get; init; }

    /// <summary>What was refused and how to recover; null when allowed.</summary>
    public string? Error { get; init; }

    /// <summary>The call's target.</summary>
    public InputTarget Target { get; init; }

    /// <summary>
    /// The window the input goes to: the foreground window (keys) or the top-level window at the point
    /// (mouse). Zero when there is none.
    /// </summary>
    public nint Receiving { get; init; }

    /// <summary><see cref="Receiving"/> described for the result; null when there is none.</summary>
    public WindowSummary? ReceivingWindow { get; init; }

    /// <summary>With BlockedByModal: the dialog that disables the target.</summary>
    public WindowSummary? BlockingWindow { get; init; }

    /// <summary>
    /// The window later batches must stay with: the target's top-level window, or without a target the
    /// window the first batch went to.
    /// </summary>
    public nint Anchor { get; init; }

    /// <summary>The input may be sent to <paramref name="receiving"/>.</summary>
    public static InputGuardResult Allow(InputTarget target, nint receiving, WindowSummary? receivingWindow, nint anchor) =>
        new() { Target = target, Receiving = receiving, ReceivingWindow = receivingWindow, Anchor = anchor };

    /// <summary>The input must not be sent.</summary>
    public static InputGuardResult Refuse(
        InputErrorCode errorCode, string error, InputTarget target, WindowSummary? receivingWindow = null,
        WindowSummary? blockingWindow = null) =>
        new()
        {
            ErrorCode = errorCode,
            Error = error,
            Target = target,
            Receiving = receivingWindow is { } window ? (nint)window.Handle : nint.Zero,
            ReceivingWindow = receivingWindow,
            BlockingWindow = blockingWindow
        };
}
