namespace WindowsAppManagerMcp.Models.Results;

/// <summary>
/// The outcome of a launch.
/// </summary>
/// <param name="Success">True when the launch was handed to Windows (whatever <paramref name="Status"/> says about its window).</param>
/// <param name="ProcessId">The process that was started; null when Windows started none (a handoff) or the start is still pending.</param>
/// <param name="WindowHandle">The handle of <paramref name="Window"/>, kept for callers that need only the handle.</param>
/// <param name="ErrorMessage">Why the launch failed.</param>
/// <param name="ErrorCode">A LaunchErrorCode name when the launch failed.</param>
/// <param name="Warning">Validation warnings, and what to do when no window was found.</param>
/// <param name="Status">What the launch led to; null when it failed.</param>
/// <param name="Window">The window of the launch, read when it was found: it can belong to another process than <paramref name="ProcessId"/>.</param>
/// <param name="LauncherExited">After a window wait: whether the started process had exited (launchers and stubs exit after a handoff).</param>
public record LaunchResult(
    bool Success,
    int? ProcessId = null,
    nint? WindowHandle = null,
    string? ErrorMessage = null,
    string? ErrorCode = null,
    string? Warning = null,
    LaunchStatus? Status = null,
    WindowInfo? Window = null,
    bool? LauncherExited = null
);
