namespace WindowsAppManagerMcp.Models;

public record WindowPlacement(
    WindowMatcher Matcher,
    int MonitorIndex,
    RelativePosition Position,
    string? LaunchCommand = null,
    bool Focus = false,
    int Order = 0
);
