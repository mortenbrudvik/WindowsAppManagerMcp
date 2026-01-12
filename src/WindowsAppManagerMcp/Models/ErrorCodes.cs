namespace WindowsAppManagerMcp.Models;

/// <summary>
/// Error codes for window operations.
/// </summary>
public enum WindowErrorCode
{
    /// <summary>No error occurred.</summary>
    None = 0,

    /// <summary>The window handle is invalid or the window no longer exists.</summary>
    InvalidHandle = 1,

    /// <summary>The specified snap position is not valid.</summary>
    InvalidSnapPosition = 2,

    /// <summary>The specified monitor index is out of range.</summary>
    InvalidMonitorIndex = 3,

    /// <summary>Failed to move the window.</summary>
    MoveFailed = 4,

    /// <summary>Failed to resize the window.</summary>
    ResizeFailed = 5,

    /// <summary>Failed to change window state.</summary>
    StateChangeFailed = 6,

    /// <summary>Failed to focus the window.</summary>
    FocusFailed = 7,

    /// <summary>Failed to close the window.</summary>
    CloseFailed = 8
}

/// <summary>
/// Error codes for application launch operations.
/// </summary>
public enum LaunchErrorCode
{
    /// <summary>No error occurred.</summary>
    None = 0,

    /// <summary>The executable path is empty or null.</summary>
    EmptyExecutablePath = 1,

    /// <summary>The executable path contains invalid characters (command injection attempt).</summary>
    InvalidCharacters = 2,

    /// <summary>Path traversal attempt detected (..).</summary>
    PathTraversal = 3,

    /// <summary>The protocol is blocked for security reasons.</summary>
    BlockedProtocol = 4,

    /// <summary>Unknown protocol (not in allowed list).</summary>
    UnknownProtocol = 5,

    /// <summary>Invalid URI format.</summary>
    InvalidUri = 6,

    /// <summary>Working directory contains invalid characters.</summary>
    InvalidWorkingDirectory = 7,

    /// <summary>Invalid path format.</summary>
    InvalidPath = 8,

    /// <summary>Failed to start the process.</summary>
    ProcessStartFailed = 9,

    /// <summary>An exception occurred during launch.</summary>
    LaunchException = 10
}

/// <summary>
/// Error codes for layout preset operations.
/// </summary>
public enum LayoutErrorCode
{
    /// <summary>No error occurred.</summary>
    None = 0,

    /// <summary>The layout preset was not found.</summary>
    PresetNotFound = 1,

    /// <summary>Failed to save the layout preset.</summary>
    SaveFailed = 2,

    /// <summary>Failed to delete the layout preset.</summary>
    DeleteFailed = 3,

    /// <summary>Failed to launch an application in the layout.</summary>
    LaunchFailed = 4,

    /// <summary>Failed to arrange a window in the layout.</summary>
    ArrangeFailed = 5,

    /// <summary>Invalid layout name.</summary>
    InvalidName = 6
}
