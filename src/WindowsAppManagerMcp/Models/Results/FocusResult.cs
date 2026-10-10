namespace WindowsAppManagerMcp.Models.Results;

/// <summary>
/// What focus_window returns, and apply_layout for each placement it focuses: whether the window is in
/// front after the call, how it got there, and which window is actually in front.
/// </summary>
/// <param name="Success">Same as <paramref name="IsForeground"/>; kept so existing callers keep working.</param>
/// <param name="Handle">The window that was asked for.</param>
/// <param name="IsForeground">True when the window, or a dialog it owns, is the foreground window after the call.</param>
/// <param name="Method">How it got in front: already, setForeground, attachThreadInput or inputUnlock. Null when it is not in front.</param>
/// <param name="ForegroundHandle">The foreground window after the call; null when no window is in the foreground.</param>
/// <param name="ForegroundTitle">The title of the foreground window.</param>
/// <param name="ForegroundProcess">The process name of the foreground window.</param>
/// <param name="BlockingPopup">A dialog of the window that takes keyboard input instead of it; null when there is none.</param>
/// <param name="MenuClosed">True when the window was in menu mode and Esc was sent to close the menu; null otherwise.</param>
/// <param name="Error">Why the window is not in front.</param>
/// <param name="ErrorCode">InvalidHandle, NotResponding, Cloaked or FocusFailed; null on success.</param>
public sealed record FocusResult(
    bool Success,
    long Handle,
    bool IsForeground,
    string? Method = null,
    long? ForegroundHandle = null,
    string? ForegroundTitle = null,
    string? ForegroundProcess = null,
    PopupWindowInfo? BlockingPopup = null,
    bool? MenuClosed = null,
    string? Error = null,
    string? ErrorCode = null)
{
    /// <summary>Converts a service outcome to the tool result.</summary>
    public static FocusResult From(FocusOutcome outcome)
    {
        ArgumentNullException.ThrowIfNull(outcome);

        return new FocusResult(
            Success: outcome.IsForeground,
            Handle: outcome.Target.ToInt64(),
            IsForeground: outcome.IsForeground,
            Method: outcome.Method,
            ForegroundHandle: outcome.ForegroundHandle == nint.Zero ? null : outcome.ForegroundHandle.ToInt64(),
            ForegroundTitle: outcome.ForegroundTitle,
            ForegroundProcess: outcome.ForegroundProcess,
            BlockingPopup: outcome.BlockingPopup is { } popup
                ? new PopupWindowInfo(popup.ToInt64(), outcome.BlockingPopupTitle ?? string.Empty)
                : null,
            MenuClosed: outcome.MenuClosed ? true : null,
            Error: outcome.Error,
            ErrorCode: outcome.ErrorCode == WindowErrorCode.None ? null : outcome.ErrorCode.ToString());
    }
}

/// <summary>A popup window (usually a dialog): its handle and title.</summary>
public sealed record PopupWindowInfo(long Handle, string Title);
