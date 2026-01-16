namespace WindowsAppManagerMcp.Models;

public record ScreenshotResult(
    bool Success,
    string? ImageData,
    string ImageFormat,
    int Width,
    int Height,
    CapturedRegion CapturedRegion,
    int? MonitorIndex,
    double ScaleFactor,
    string? Error = null
);

public record CapturedRegion(
    int X,
    int Y,
    int Width,
    int Height
);

public record ScreenInfo(
    int Index,
    string Name,
    bool IsPrimary,
    int X,
    int Y,
    int Width,
    int Height,
    int WorkAreaX,
    int WorkAreaY,
    int WorkAreaWidth,
    int WorkAreaHeight,
    double ScaleFactor
);

public record ScreenListResult(
    IReadOnlyList<ScreenInfo> Screens,
    int PrimaryIndex,
    VirtualScreenBounds VirtualScreen
);

public record VirtualScreenBounds(
    int X,
    int Y,
    int Width,
    int Height
);
