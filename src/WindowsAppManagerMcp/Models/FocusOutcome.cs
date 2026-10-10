namespace WindowsAppManagerMcp.Models;

/// <summary>
/// How <c>IForegroundService.EnsureForeground</c> got a window in front, in the order it tries them.
/// </summary>
public static class FocusMethod
{
    /// <summary>The window (or a window it owns) was already in front; nothing was changed.</summary>
    public const string Already = "already";

    /// <summary>SetForegroundWindow was allowed.</summary>
    public const string SetForeground = "setForeground";

    /// <summary>
    /// SetForegroundWindow was allowed after the activation thread attached its input to the foreground
    /// thread (AttachThreadInput), which the foreground lock treats as the foreground process.
    /// </summary>
    public const string AttachThreadInput = "attachThreadInput";

    /// <summary>
    /// SetForegroundWindow was allowed after the server injected a zero-distance mouse move, which makes
    /// it the process that received the last input. No key and no button is sent.
    /// </summary>
    public const string InputUnlock = "inputUnlock";
}

/// <summary>
/// What <c>IForegroundService.EnsureForeground</c> found and did: whether the target is in front, how
/// it got there, and which window is actually in front afterwards.
/// </summary>
/// <remarks>
/// The target counts as in front when the foreground window is the target's top-level window or a
/// window it owns, such as a dialog, directly or through other owned windows: the target is in the
/// foreground window's owner chain (GW_OWNER). GetAncestor(GA_ROOTOWNER) equal to the target is a fast
/// check for Win32 dialogs (WS_POPUP), but it does not follow WPF or WinForms owned windows, which are
/// not WS_POPUP, so the owner chain decides. A dialog in front is reported in
/// <see cref="BlockingPopup"/>, because keyboard input goes to the dialog, not to the target.
/// </remarks>
public sealed record FocusOutcome
{
    /// <summary>The window that was asked for.</summary>
    public required nint Target { get; init; }

    /// <summary>True when the target, or a window it owns, is the foreground window after the call.</summary>
    public bool IsForeground { get; init; }

    /// <summary>The <see cref="FocusMethod"/> that got the target in front; null when it is not in front.</summary>
    public string? Method { get; init; }

    /// <summary>The foreground window after the call, or 0 when no window is in the foreground.</summary>
    public nint ForegroundHandle { get; init; }

    /// <summary>The title of the foreground window; null when there is none.</summary>
    public string? ForegroundTitle { get; init; }

    /// <summary>The process of the foreground window (for a UWP app, the app's own process); null when there is none.</summary>
    public string? ForegroundProcess { get; init; }

    /// <summary>The ID of <see cref="ForegroundProcess"/>; null when there is no foreground window.</summary>
    public int? ForegroundProcessId { get; init; }

    /// <summary>
    /// A window the target owns that takes the input instead of the target: the dialog in front of it,
    /// or the enabled popup of a target that a modal dialog has disabled. Null when there is none.
    /// </summary>
    public nint? BlockingPopup { get; init; }

    /// <summary>The title of <see cref="BlockingPopup"/>.</summary>
    public string? BlockingPopupTitle { get; init; }

    /// <summary>
    /// True when the window in front was in menu mode after the activation and Esc was sent to close
    /// the menu, so that typed keys reach the window instead of selecting menu items.
    /// </summary>
    public bool MenuClosed { get; init; }

    /// <summary>
    /// <see cref="WindowErrorCode.None"/> when the target is in front; otherwise InvalidHandle,
    /// NotResponding, Cloaked or FocusFailed.
    /// </summary>
    public WindowErrorCode ErrorCode { get; init; }

    /// <summary>Why the target is not in front; null when it is.</summary>
    public string? Error { get; init; }
}
