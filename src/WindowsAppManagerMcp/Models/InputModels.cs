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
