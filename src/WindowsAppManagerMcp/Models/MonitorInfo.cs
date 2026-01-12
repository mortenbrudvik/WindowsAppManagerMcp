namespace WindowsAppManagerMcp.Models;

public record MonitorInfo(
    int Index,
    string DeviceName,
    bool IsPrimary,
    MonitorRect Bounds,
    MonitorRect WorkArea,
    double ScaleFactor
);
