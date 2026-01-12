namespace WindowsAppManagerMcp.Models.Results;

public record LaunchResult(
    bool Success,
    int? ProcessId = null,
    nint? WindowHandle = null,
    string? ErrorMessage = null,
    string? Warning = null
);
