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

/// <summary>
/// Result of a screenshot capture with UI element overlays.
/// </summary>
public record AnnotatedScreenshotResult(
    bool Success,
    string? ImageData,
    string ImageFormat,
    int Width,
    int Height,
    long WindowHandle,
    string? WindowTitle,
    IReadOnlyList<AnnotatedElement>? Elements,
    int ElementCount,
    double CaptureElapsedMs,
    double AnnotationElapsedMs,
    double? MonitorScaleFactor = null,
    string? Error = null
);

/// <summary>
/// An element annotation with its bounding box relative to the captured image.
/// </summary>
public record AnnotatedElement(
    string? Name,
    string ControlType,
    string? AutomationId,
    BoundsDto ImageBounds,
    BoundsDto ScreenBounds,
    bool IsEnabled,
    string OverlayColor
);
