namespace WindowsAppManagerMcp.Models;

/// <summary>
/// A window as the input results report it: where input went, what is in front afterwards, or the
/// dialog that blocks a target.
/// </summary>
/// <param name="Handle">The window handle.</param>
/// <param name="Title">The window title (empty for untitled windows such as menus and popups).</param>
/// <param name="ProcessName">The process name; for a UWP window the app's own process.</param>
/// <param name="ProcessId">The ID of <paramref name="ProcessName"/>.</param>
public sealed record WindowSummary(long Handle, string Title, string ProcessName, int ProcessId);

/// <summary>
/// The window an input call is meant for (roadmap M1.5), or none. With a window the input guard brings it
/// to the front (keys) or checks that the point is on it (mouse), and refuses to send input that would
/// land elsewhere.
/// </summary>
/// <param name="WindowHandle">The target window, or null for no target.</param>
/// <param name="AllowHostTarget">
/// Lets input reach the agent's own host window (its terminal, editor or chat app), but only when that
/// window is the explicit target. It has no effect without <paramref name="WindowHandle"/>.
/// </param>
public readonly record struct InputTarget(nint? WindowHandle, bool AllowHostTarget = false)
{
    /// <summary>No target: input goes to the foreground window (keys) or the window at the point (mouse).</summary>
    public static InputTarget None => default;

    /// <summary>A target window.</summary>
    public static InputTarget Window(nint handle, bool allowHostTarget = false) => new(handle, allowHostTarget);

    /// <summary>True when a target window was given (even an invalid one).</summary>
    public bool HasWindow => WindowHandle.HasValue;
}

/// <summary>
/// Result of a mouse click operation.
/// </summary>
public record ClickResult(
    bool Success,
    int X,
    int Y,
    string? ClickType = null,
    string? Error = null,
    InputErrorCode ErrorCode = InputErrorCode.None
)
{
    /// <summary>
    /// The top-level window at the point: where the click went, or would have gone when it was refused.
    /// Null when nothing was checked (invalid coordinates).
    /// </summary>
    public WindowSummary? ReceivingWindow { get; init; }

    /// <summary>The foreground window right after the call; the application may still be handling the input.</summary>
    public WindowSummary? ForegroundAfter { get; init; }

    /// <summary>Where the cursor is after the call (GetCursorPos), in screen pixels.</summary>
    public int? ActualX { get; init; }

    /// <inheritdoc cref="ActualX"/>
    public int? ActualY { get; init; }

    /// <summary>With BlockedByModal: the dialog that disables the target window.</summary>
    public WindowSummary? BlockingWindow { get; init; }
}

/// <summary>
/// Result of a mouse move operation.
/// </summary>
public record MouseMoveResult(
    bool Success,
    int X,
    int Y,
    string? Error = null,
    InputErrorCode ErrorCode = InputErrorCode.None
)
{
    /// <inheritdoc cref="ClickResult.ReceivingWindow"/>
    public WindowSummary? ReceivingWindow { get; init; }

    /// <inheritdoc cref="ClickResult.ForegroundAfter"/>
    public WindowSummary? ForegroundAfter { get; init; }

    /// <inheritdoc cref="ClickResult.ActualX"/>
    public int? ActualX { get; init; }

    /// <inheritdoc cref="ClickResult.ActualX"/>
    public int? ActualY { get; init; }

    /// <inheritdoc cref="ClickResult.BlockingWindow"/>
    public WindowSummary? BlockingWindow { get; init; }
}

/// <summary>
/// Result of a text typing operation.
/// </summary>
public record TypeTextResult(
    bool Success,
    string Text,
    int CharactersTyped,
    string? Error = null,
    InputErrorCode ErrorCode = InputErrorCode.None
)
{
    /// <summary>
    /// The window the keys went to (the foreground window, which can be a dialog of the target), or would
    /// have gone to when the call was refused. Null when nothing was checked (empty text).
    /// </summary>
    public WindowSummary? ReceivingWindow { get; init; }

    /// <inheritdoc cref="ClickResult.ForegroundAfter"/>
    public WindowSummary? ForegroundAfter { get; init; }

    /// <inheritdoc cref="ClickResult.BlockingWindow"/>
    public WindowSummary? BlockingWindow { get; init; }
}

/// <summary>
/// Result of a send keys operation.
/// </summary>
public record SendKeysResult(
    bool Success,
    string Keys,
    string? Error = null,
    InputErrorCode ErrorCode = InputErrorCode.None
)
{
    /// <inheritdoc cref="TypeTextResult.ReceivingWindow"/>
    public WindowSummary? ReceivingWindow { get; init; }

    /// <inheritdoc cref="ClickResult.ForegroundAfter"/>
    public WindowSummary? ForegroundAfter { get; init; }

    /// <inheritdoc cref="ClickResult.BlockingWindow"/>
    public WindowSummary? BlockingWindow { get; init; }
}

/// <summary>
/// Kind of event sent through SendInput.
/// </summary>
public enum InputEventKind
{
    Mouse,
    VirtualKey,
    Unicode
}

/// <summary>
/// A single mouse or keyboard event sent through SendInput.
/// </summary>
/// <param name="Kind">The kind of event.</param>
/// <param name="Code">The virtual-key code for VirtualKey events, the character for Unicode events, unused for Mouse events.</param>
/// <param name="Flags">The raw MOUSEEVENTF_* or KEYEVENTF_* flags.</param>
/// <param name="Dx">Mouse events: MOUSEINPUT.dx (with MOUSEEVENTF_ABSOLUTE, 0..65535 across the screen); otherwise 0.</param>
/// <param name="Dy">Mouse events: MOUSEINPUT.dy; otherwise 0.</param>
public readonly record struct InputEvent(InputEventKind Kind, ushort Code, uint Flags, int Dx = 0, int Dy = 0)
{
    public static InputEvent Mouse(uint flags) => new(InputEventKind.Mouse, 0, flags);

    /// <summary>
    /// Moves the pointer to normalized absolute coordinates (0..65535) across the whole virtual desktop
    /// (MOUSEEVENTF_MOVE | MOUSEEVENTF_ABSOLUTE | MOUSEEVENTF_VIRTUALDESK).
    /// </summary>
    public static InputEvent MouseMoveAbsolute(int normalizedX, int normalizedY) =>
        new(InputEventKind.Mouse, 0, MouseMoveAbsoluteFlags, normalizedX, normalizedY);

    /// <summary>MOUSEEVENTF_MOVE | MOUSEEVENTF_VIRTUALDESK | MOUSEEVENTF_ABSOLUTE.</summary>
    public const uint MouseMoveAbsoluteFlags =
        Native.NativeEnums.MOUSEEVENTF_MOVE | Native.NativeEnums.MOUSEEVENTF_VIRTUALDESK | Native.NativeEnums.MOUSEEVENTF_ABSOLUTE;

    public static InputEvent Key(ushort virtualKey, uint flags) => new(InputEventKind.VirtualKey, virtualKey, flags);

    public static InputEvent Unicode(char character, uint flags) => new(InputEventKind.Unicode, character, flags);
}

/// <summary>
/// Error codes for input simulation operations.
/// </summary>
public enum InputErrorCode
{
    None = 0,
    InvalidCoordinates = 1,
    SendInputFailed = 2,
    CoordinatesOutOfBounds = 3,
    EmptyText = 4,
    InvalidKeySpecification = 5,
    UnknownKey = 6,

    // Input guard codes (roadmap M1.1, M1.5 and M1.12) are numbered from 20, so that earlier input codes
    // can be added after UnknownKey without renumbering. In every case nothing was sent.

    /// <summary>
    /// The input would reach the agent's own host (its terminal, editor or chat app, or a window it owns),
    /// and the call did not target that window explicitly with allowHostTarget=true.
    /// </summary>
    TargetIsAgentHost = 20,

    /// <summary>
    /// Keyboard input: the target window could not be brought to the foreground, or no window is in the
    /// foreground.
    /// </summary>
    TargetNotForeground = 21,

    /// <summary>Mouse input: another window (not the target or a popup of it) is at the point.</summary>
    TargetOccluded = 22,

    /// <summary>The target window is disabled by a modal dialog; BlockingWindow names the dialog.</summary>
    BlockedByModal = 23,

    /// <summary>
    /// type_text: the foreground window changed to a window outside the target while typing, so typing
    /// stopped; CharactersTyped says how much was typed before.
    /// </summary>
    TargetLostFocus = 24,

    /// <summary>send_keys: the keys contain a chord that closes, quits or locks, and no windowHandle was given.</summary>
    DestructiveChordNeedsTarget = 25,

    /// <summary>
    /// The receiving window's process has a higher integrity level than the server (for example it runs as
    /// administrator): Windows (UIPI) would drop the input without an error.
    /// </summary>
    TargetElevated = 26,

    /// <summary>
    /// The input desktop cannot be reached: a UAC prompt or Ctrl+Alt+Del screen is shown, or the session
    /// is disconnected.
    /// </summary>
    DesktopUnavailable = 27,

    /// <summary>The session is locked.</summary>
    SessionLocked = 28,

    /// <summary>The server's target policy (WAM_DENY_PROCESSES, WAM_ALLOW_PROCESSES) refuses the window's process.</summary>
    TargetDeniedByPolicy = 29,

    /// <summary>windowHandle is not a window, or the window has closed.</summary>
    InvalidHandle = 30,

    /// <summary>type_text: a menu is active in the receiving window, so typed letters would select menu items.</summary>
    TargetInMenuMode = 31,

    /// <summary>
    /// Mouse input: the pointer did not stay at the point, so the click would land somewhere the guard did
    /// not check (it is confined to a window, for example by a game or a remote session, or the user is
    /// moving the mouse).
    /// </summary>
    CursorNotAtPoint = 32
}
