namespace WindowsAppManagerMcp.Models;

/// <summary>
/// The outcome of one close request (IWindowService.CloseWindow). Success means WM_CLOSE was posted to
/// the window, not that it closed: the application can still show a save prompt or ignore the request.
/// </summary>
/// <param name="Handle">The window the request was for.</param>
/// <param name="Success">True when the close message was posted.</param>
/// <param name="ErrorCode">Why nothing was posted; <see cref="WindowErrorCode.None"/> on success.</param>
/// <param name="Error">What went wrong and what the agent can do about it; null on success.</param>
public sealed record WindowCloseResult(nint Handle, bool Success, WindowErrorCode ErrorCode = WindowErrorCode.None, string? Error = null)
{
    /// <summary>The close message was posted.</summary>
    public static WindowCloseResult Sent(nint handle) => new(handle, true);

    /// <summary>Nothing was posted, because of <paramref name="errorCode"/>.</summary>
    public static WindowCloseResult Failed(nint handle, WindowErrorCode errorCode, string error) =>
        new(handle, false, errorCode, error);
}

/// <summary>What became of a window after a close request (roadmap M2.5).</summary>
public enum CloseStatus
{
    /// <summary>The close message was sent and nothing was checked (waitMs 0).</summary>
    Requested,

    /// <summary>The window no longer exists.</summary>
    Closed,

    /// <summary>The window still exists and is visible, and no prompt was found.</summary>
    StillOpen,

    /// <summary>The window still exists because a prompt (a "Save changes?" dialog) waits for an answer.</summary>
    PromptShown,

    /// <summary>
    /// The window still exists but is hidden or cloaked: the application hid it instead of closing it (an app that keeps
    /// running in the notification area). The application still runs.
    /// </summary>
    Hidden
}

/// <summary>How a prompt that blocks a close was found.</summary>
public enum ClosePromptKind
{
    /// <summary>The window is disabled and GetWindow(GW_ENABLEDPOPUP) names the enabled popup that took over: a modal dialog.</summary>
    ModalPopup,

    /// <summary>A visible dialog box (#32770) owned by the window, directly or through other owned windows, while the window or its owner is disabled.</summary>
    OwnedDialog,

    /// <summary>A modal dialog inside the window itself (UI Automation WindowPattern.IsModal), such as a WinUI ContentDialog; it has no window of its own.</summary>
    InWindowDialog
}

/// <summary>
/// A prompt that keeps a window from closing, such as "Do you want to save changes?". The agent asks the user which button
/// to choose, then answers it with respond_to_dialog by its <see cref="Handle"/> (an in-window dialog with element_action).
/// </summary>
/// <param name="Handle">The prompt's window; for an <see cref="ClosePromptKind.InWindowDialog"/> without a window of its own, the window that was closed.</param>
/// <param name="Title">Its title (UI Automation name for an in-window dialog).</param>
/// <param name="ClassName">Its window class, "#32770" for a standard dialog box.</param>
/// <param name="Text">Its message text, or null when none could be read.</param>
/// <param name="Buttons">The captions of its buttons, without accelerator ampersands, in tree order.</param>
/// <param name="Kind">How it was found.</param>
public sealed record ClosePrompt(nint Handle, string Title, string ClassName, string? Text, IReadOnlyList<string> Buttons, ClosePromptKind Kind);

/// <summary>What a close request led to for one window.</summary>
public sealed record CloseObservation(nint Handle, CloseStatus Status, ClosePrompt? Prompt = null)
{
    /// <summary>
    /// What the verifier could not find out, as a sentence for the agent: the window's prompt could not be inspected through
    /// UI Automation (it failed, timed out, or the app does not answer), so StillOpen does not prove there is no prompt; or
    /// a Win32 prompt's text and buttons could not be read. Null when everything was read.
    /// </summary>
    public string? Warning { get; init; }
}

/// <summary>A window owned by another window (get_owned_windows).</summary>
/// <param name="Window">The owned window.</param>
/// <param name="IsDialog">Its class is a standard dialog box (#32770).</param>
/// <param name="IsBlocking">It is the enabled popup of the owner while the owner is disabled: a modal dialog that takes the owner's input.</param>
public sealed record OwnedWindowInfo(WindowInfo Window, bool IsDialog, bool IsBlocking);
