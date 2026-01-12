namespace WindowsAppManagerMcp.Models.Results;

public record KillResult(
    bool Success,
    int ProcessId,
    string? ProcessName = null,
    string? ErrorMessage = null,
    string? ErrorCode = null
);
