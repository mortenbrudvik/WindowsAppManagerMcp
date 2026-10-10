namespace WindowsAppManagerMcp.Models;

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
);

/// <summary>
/// Result of a mouse move operation.
/// </summary>
public record MouseMoveResult(
    bool Success,
    int X,
    int Y,
    string? Error = null,
    InputErrorCode ErrorCode = InputErrorCode.None
);

/// <summary>
/// Result of a text typing operation.
/// </summary>
public record TypeTextResult(
    bool Success,
    string Text,
    int CharactersTyped,
    string? Error = null,
    InputErrorCode ErrorCode = InputErrorCode.None
);

/// <summary>
/// Result of a send keys operation.
/// </summary>
public record SendKeysResult(
    bool Success,
    string Keys,
    string? Error = null,
    InputErrorCode ErrorCode = InputErrorCode.None
);

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
public readonly record struct InputEvent(InputEventKind Kind, ushort Code, uint Flags)
{
    public static InputEvent Mouse(uint flags) => new(InputEventKind.Mouse, 0, flags);

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
    UnknownKey = 6
}
