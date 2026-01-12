namespace WindowsAppManagerMcp.Models;

public record WindowMatcher(
    string? TitleContains = null,
    string? ProcessName = null,
    string? ClassName = null
);
