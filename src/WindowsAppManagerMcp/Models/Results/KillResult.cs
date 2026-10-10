namespace WindowsAppManagerMcp.Models.Results;

/// <summary>
/// Result of kill_process. Success is true only when the process is confirmed to have exited.
/// </summary>
/// <param name="CloseRequested">A close request (WM_CLOSE) was posted to the main window (graceful mode).</param>
/// <param name="Forced">A forced termination of the process and its child processes was issued (forceKill=true).</param>
/// <param name="Exited">The process is confirmed to have exited.</param>
/// <param name="Warning">Additional information, for example child processes that could not be terminated.</param>
public record KillResult(
    bool Success,
    int ProcessId,
    string? ProcessName = null,
    string? ErrorMessage = null,
    string? ErrorCode = null,
    bool CloseRequested = false,
    bool Forced = false,
    bool Exited = false,
    string? Warning = null
);
