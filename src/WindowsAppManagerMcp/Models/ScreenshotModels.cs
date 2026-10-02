namespace WindowsAppManagerMcp.Models;

/// <summary>
/// Result of a screenshot capture.
/// On success, exactly one of <see cref="ImageData"/> (base64) or <see cref="FilePath"/> is set.
/// <see cref="Width"/> and <see cref="Height"/> are the output image size in pixels.
/// <see cref="OriginalWidth"/> and <see cref="OriginalHeight"/> are the pre-resize size.
/// Both are set only when the image was wider than maxWidth; otherwise both are null.
/// <see cref="CapturedRegion"/> is the pre-scale capture rectangle. Window captures use origin (0, 0).
/// </summary>
public record ScreenshotResult(
    bool Success,
    string? ImageData,
    string ImageFormat,
    int Width,
    int Height,
    CapturedRegion CapturedRegion,
    int? MonitorIndex,
    double ScaleFactor,
    string? Error = null,
    string? FilePath = null,
    int? OriginalWidth = null,
    int? OriginalHeight = null
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
/// On success, exactly one of <see cref="ImageData"/> (base64) or <see cref="FilePath"/> is set.
/// <see cref="Width"/> and <see cref="Height"/> are the output image size.
/// <see cref="OriginalWidth"/> and <see cref="OriginalHeight"/> are both set only when the image was scaled down.
/// <see cref="AnnotatedElement.ImageBounds"/> are output-image pixels.
/// <see cref="AnnotatedElement.ScreenBounds"/> stay in screen pixels.
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
    string? Error = null,
    string? FilePath = null,
    int? OriginalWidth = null,
    int? OriginalHeight = null
);

/// <summary>
/// An element annotation.
/// <see cref="ImageBounds"/> are pixels of the output image, scaled when maxWidth shrinks the capture.
/// <see cref="ScreenBounds"/> stay in screen pixels and are not scaled.
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
