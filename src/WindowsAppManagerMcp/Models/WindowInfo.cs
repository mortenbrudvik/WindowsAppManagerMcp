namespace WindowsAppManagerMcp.Models;

public record WindowInfo(
    nint Handle,
    string Title,
    string ProcessName,
    int ProcessId,
    WindowRect Bounds,
    WindowState State,
    bool IsVisible,
    int MonitorIndex
);
