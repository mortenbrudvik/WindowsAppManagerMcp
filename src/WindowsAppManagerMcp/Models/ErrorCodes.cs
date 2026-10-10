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

    /// <summary>
    /// The window could not be brought to the foreground: Windows kept another window in front
    /// (the foreground lock, an open menu, or an elevated foreground window).
    /// </summary>
    FocusFailed = 7,

    /// <summary>
    /// The close message could not be sent (a Win32 error other than access denied, or an unexpected
    /// error in a batch); the error text has the reason.
    /// </summary>
    CloseFailed = 8,

    // Codes added in M1 are numbered from 20, so that earlier window codes can be added after
    // CloseFailed without renumbering. Activation codes (EnsureForeground, roadmap M1.3):

    /// <summary>The window's thread is not responding (IsHungAppWindow), or a call to it did not return in time.</summary>
    NotResponding = 20,

    /// <summary>The window is cloaked: it is on another virtual desktop, or its app has hidden it.</summary>
    Cloaked = 21,

    // Close guard codes (roadmap M1.7):

    /// <summary>
    /// The window is part of the desktop shell (the desktop or a taskbar) or of the agent's own host (the
    /// terminal, editor or chat app the server was started from), and force was not true. Nothing was sent.
    /// </summary>
    ProtectedWindow = 22,

    /// <summary>
    /// Windows refused the message with ERROR_ACCESS_DENIED (5): User Interface Privilege Isolation blocks
    /// messages to windows of more privileged processes, such as an app running as administrator.
    /// </summary>
    AccessDenied = 23,

    /// <summary>A batch above the confirmation threshold was sent without confirm=true. Nothing was done.</summary>
    ConfirmationRequired = 24,

    /// <summary>
    /// The server's target policy (WAM_DENY_PROCESSES, WAM_ALLOW_PROCESSES) refuses the window's process
    /// (roadmap M1.12). force does not override it. Nothing was sent.
    /// </summary>
    TargetDeniedByPolicy = 25,

    /// <summary>
    /// The user paused the server's input with the pause hotkey (roadmap M1.10), so the window was not
    /// activated: activating takes the keyboard focus from the user and can send input. Nothing was done.
    /// </summary>
    PausedByUser = 26
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

    /// <summary>
    /// The executable contains characters that are not valid in a Windows path (", &lt;, &gt;, |, *, ?, control characters),
    /// or a bare name / relative path contains shell metacharacters.
    /// </summary>
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
    LaunchException = 10,

    /// <summary>
    /// An argument contains characters that cmd.exe would interpret (&amp; | &lt; &gt; ^ % ! " or control characters),
    /// and the target is a batch file (.bat/.cmd) or could not be verified to be a native executable.
    /// </summary>
    UnsafeBatchArguments = 11,

    /// <summary>
    /// An element of the arguments array is null or contains a NUL character. Not to be confused with the
    /// InvalidArguments tool error (ToolErrorReporting), which means the call's arguments could not be bound.
    /// </summary>
    InvalidArgumentValue = 12,

    /// <summary>
    /// A bare executable name was found only in the working directory. Bare names are never resolved
    /// from the working directory, to avoid running a planted program.
    /// </summary>
    ExecutableInWorkingDirectory = 13
}

/// <summary>
/// Error codes for process termination operations.
/// </summary>
public enum ProcessErrorCode
{
    /// <summary>No error occurred.</summary>
    None = 0,

    /// <summary>The process ID is invalid (zero or negative).</summary>
    InvalidProcessId = 1,

    /// <summary>The process was not found or has already exited.</summary>
    ProcessNotFound = 2,

    /// <summary>The process is protected and cannot be terminated.</summary>
    ProtectedProcess = 3,

    /// <summary>Cannot terminate the current process.</summary>
    CannotTerminateSelf = 4,

    /// <summary>
    /// Access denied - insufficient permissions to terminate the process
    /// (typically it runs elevated or as another user).
    /// </summary>
    AccessDenied = 5,

    /// <summary>The confirmation parameter was not set to true.</summary>
    ConfirmationRequired = 6,

    /// <summary>An exception occurred during termination.</summary>
    TerminationException = 7,

    /// <summary>
    /// Graceful mode: the process has no main window that can receive a close request (windowless,
    /// tray-only, or main window disabled by a modal dialog). The process was left running.
    /// </summary>
    NoClosableWindow = 8,

    /// <summary>
    /// Graceful mode: a close request was sent but the process was still running when the timeout
    /// elapsed (for example it is showing a "save changes?" prompt). The process was left running.
    /// </summary>
    GracefulCloseTimedOut = 9,

    /// <summary>The process name did not match expectedProcessName (the PID may have been reused). Nothing was done.</summary>
    ProcessNameMismatch = 10,

    /// <summary>
    /// The process belongs to the agent's host (IHostGuard.IsHostProcess: an ancestor of this server such as the
    /// terminal or editor hosting the agent, the console host's process or the console server behind it, or the
    /// startup foreground window's process), or, with forceKill, one of the child processes the forced kill would end does. Nothing was done.
    /// </summary>
    CannotTerminateHost = 11,

    /// <summary>Forced termination was requested, but the process had not exited when the wait ended.</summary>
    TerminationNotConfirmed = 12,

    /// <summary>The graceful close timeout is outside the allowed range.</summary>
    InvalidTimeout = 13,

    /// <summary>
    /// The server's target policy (WAM_DENY_PROCESSES, WAM_ALLOW_PROCESSES) refuses the process (roadmap
    /// M1.12). Nothing was done.
    /// </summary>
    TargetDeniedByPolicy = 14,

    /// <summary>
    /// forceKill: the process list could not be read, so the child processes the forced kill would also end
    /// could not be checked against the target policy and the host guard. Nothing was terminated.
    /// </summary>
    ProcessTreeUnreadable = 15
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
    InvalidName = 6,

    /// <summary>The layout storage folder could not be created or used.</summary>
    StorageUnavailable = 7,

    /// <summary>No open window matched one or more placements in the layout.</summary>
    WindowNotFound = 8,

    /// <summary>No windows matched the capture filters, so nothing was saved.</summary>
    NoWindowsCaptured = 9
}

/// <summary>
/// Error codes for the capture tools (take_screenshot, capture_region, capture_window, capture_with_elements).
/// </summary>
public enum ScreenshotErrorCode
{
    /// <summary>No error occurred.</summary>
    None = 0,

    /// <summary>The format is not auto, png, jpeg or jpg.</summary>
    InvalidFormat = 1,

    /// <summary>maxDimension or maxWidth is out of range, or would scale the image to zero pixels.</summary>
    InvalidImageSize = 2,

    /// <summary>The output mode is unknown, or outputPath was given without a file output.</summary>
    InvalidOutputMode = 3,

    /// <summary>outputPath is not absolute, has the wrong extension, or its folder does not exist.</summary>
    InvalidOutputPath = 4,

    /// <summary>outputPath names an existing file and overwrite was not true.</summary>
    FileExists = 5,

    /// <summary>The image file could not be written.</summary>
    FileWriteFailed = 6,

    /// <summary>The monitor index is out of range.</summary>
    InvalidMonitorIndex = 7,

    /// <summary>The region's width or height is not positive.</summary>
    InvalidRegion = 8,

    /// <summary>The window handle is invalid or the window no longer exists.</summary>
    InvalidHandle = 9,

    /// <summary>Windows could not capture the pixels (for example the window is minimized).</summary>
    CaptureFailed = 10,

    /// <summary>
    /// capture_window and capture_with_elements: the server's target policy (WAM_DENY_PROCESSES,
    /// WAM_ALLOW_PROCESSES) refuses the window's process (roadmap M1.12). Nothing was captured or read.
    /// </summary>
    TargetDeniedByPolicy = 11
}

/// <summary>
/// Error codes for browser reading operations.
/// </summary>
public enum BrowserErrorCode
{
    /// <summary>No error occurred.</summary>
    None = 0,

    /// <summary>The window handle is invalid or the window no longer exists.</summary>
    InvalidHandle = 1,

    /// <summary>The window is not a recognized browser.</summary>
    NotABrowser = 2,

    /// <summary>Failed to find the URL bar element in the browser.</summary>
    UrlBarNotFound = 3,

    /// <summary>Failed to get the URL value from the browser.</summary>
    UrlExtractionFailed = 4,

    /// <summary>Failed to find the content area in the browser.</summary>
    ContentAreaNotFound = 5,

    /// <summary>Failed to extract content from the browser.</summary>
    ContentExtractionFailed = 6,

    /// <summary>UI Automation is not available or failed to initialize.</summary>
    AutomationUnavailable = 7,

    /// <summary>No foreground browser window was found.</summary>
    NoBrowserInForeground = 8,

    /// <summary>An exception occurred during the operation.</summary>
    OperationException = 9,

    /// <summary>The browser executable was not found.</summary>
    BrowserNotFound = 10,

    /// <summary>The specified browser type is not supported.</summary>
    UnsupportedBrowser = 11,

    /// <summary>The CDP port is already in use.</summary>
    PortInUse = 12,

    /// <summary>Failed to launch browser with debug mode.</summary>
    DebugLaunchFailed = 13,

    // CDP (Chrome DevTools Protocol) errors are numbered from 30 so that other
    // browser error codes can be added above without renumbering.

    /// <summary>The CDP endpoint could not be reached or did not answer like a DevTools endpoint.</summary>
    CdpUnavailable = 30,

    /// <summary>The browser or page did not answer a CDP request within the timeout.</summary>
    CdpTimeout = 31,

    /// <summary>No CDP tab could be matched to the window.</summary>
    CdpTargetNotFound = 32,

    /// <summary>More than one CDP tab matches the window, so the right one cannot be chosen.</summary>
    CdpTargetAmbiguous = 33,

    /// <summary>The CDP port is owned by a different process than the window.</summary>
    CdpPortOwnerMismatch = 34,

    /// <summary>The page has a JavaScript dialog (alert/confirm/prompt/beforeunload) open.</summary>
    CdpDialogOpen = 35,

    /// <summary>The page threw an exception or the browser rejected the evaluation.</summary>
    CdpEvaluationFailed = 36,

    /// <summary>The CDP connection closed early or returned a response that could not be read.</summary>
    CdpProtocolError = 37,

    /// <summary>HTML content (textOnly=false) was requested, but it is only available via CDP.</summary>
    HtmlRequiresCdp = 38
}
