namespace WindowsAppManagerMcp.Models;

/// <summary>
/// A prompt that keeps a window from closing, as the close tools report it (roadmap M2.5). Kind is modalPopup (the
/// enabled popup of the disabled window), ownedDialog (a #32770 dialog the window owns) or inWindowDialog (a modal dialog
/// drawn inside the window, such as Win11 Notepad's; its handle is the window's own).
/// </summary>
public record ClosePromptDto(long Handle, string Title, string ClassName, string? Text, IReadOnlyList<string> Buttons, string Kind)
{
    public static ClosePromptDto? From(ClosePrompt? prompt) => prompt == null
        ? null
        : new ClosePromptDto(prompt.Handle.ToInt64(), prompt.Title, prompt.ClassName, prompt.Text, prompt.Buttons, CloseNames.Of(prompt.Kind));
}

/// <summary>
/// close_window's result. Status is requested (waitMs 0: sent, not checked), closed, hidden (the app hid the window
/// instead of closing it; it still runs), stillOpen or promptShown (with Prompt). Success is true for requested, closed
/// and hidden. Handle is the window the call was for (roadmap M2.4: the one a window selector resolved to).
/// </summary>
public record CloseWindowResult(
    bool Success,
    string? Status = null,
    ClosePromptDto? Prompt = null,
    string? Error = null,
    string? ErrorCode = null,
    long? Handle = null
);

/// <summary>A window close_application asked to close, and what became of it (closed, hidden, stillOpen, promptShown or notSent).</summary>
public record ClosedWindowItem(long Handle, string Title, string Status, string? Error = null, string? ErrorCode = null);

/// <summary>An application window still open after close_application waited.</summary>
public record RemainingWindowDto(long Handle, string Title, int ProcessId, string ProcessName);

/// <summary>
/// close_application's result (roadmap M2.5). Status is closed (every application window is gone), promptShown (a prompt
/// waits for an answer: PromptDetected and Prompt) or stillOpen (windows remain without a prompt); it is left out when
/// nothing was sent. Exited says whether every target process ended once its windows were gone (null while windows
/// remain); RunningProcessIds lists the ones still running then, for example an app that stays in the notification area.
/// OtherDesktopWindows lists the application's windows on other virtual desktops, which are not closed (left out when
/// there are none): status closed covers the windows on the current desktop only.
/// </summary>
public record CloseApplicationResult(
    bool Success,
    string? Status,
    IReadOnlyList<int> ProcessIds,
    IReadOnlyList<ClosedWindowItem> Windows,
    IReadOnlyList<RemainingWindowDto> RemainingWindows,
    bool PromptDetected,
    ClosePromptDto? Prompt = null,
    bool? Exited = null,
    IReadOnlyList<int>? RunningProcessIds = null,
    string? Error = null,
    string? ErrorCode = null,
    IReadOnlyList<RemainingWindowDto>? OtherDesktopWindows = null
);

/// <summary>A window another window owns (get_owned_windows). IsBlocking is true for the modal dialog that takes the owner's input, and left out otherwise.</summary>
public record OwnedWindowDto(
    long Handle,
    string Title,
    string ClassName,
    string ProcessName,
    int ProcessId,
    long? OwnerHandle,
    BoundsDto Bounds,
    string State,
    bool IsVisible,
    bool IsDialog,
    bool? IsBlocking = null
);

/// <summary>get_owned_windows's result: the owner, whether a modal dialog blocks it, and its owned windows in z-order.</summary>
public record OwnedWindowsResult(
    bool Success,
    long Handle,
    bool IsBlocked = false,
    IReadOnlyList<OwnedWindowDto>? Windows = null,
    string? Error = null,
    string? ErrorCode = null
);

/// <summary>The names the close results use for the close enums: camelCase, as JSON values are.</summary>
public static class CloseNames
{
    public static string Of(CloseStatus status) => status switch
    {
        CloseStatus.Requested => "requested",
        CloseStatus.Closed => "closed",
        CloseStatus.StillOpen => "stillOpen",
        CloseStatus.PromptShown => "promptShown",
        CloseStatus.Hidden => "hidden",
        _ => throw new ArgumentOutOfRangeException(nameof(status), status, null)
    };

    public static string Of(ClosePromptKind kind) => kind switch
    {
        ClosePromptKind.ModalPopup => "modalPopup",
        ClosePromptKind.OwnedDialog => "ownedDialog",
        ClosePromptKind.InWindowDialog => "inWindowDialog",
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null)
    };
}
