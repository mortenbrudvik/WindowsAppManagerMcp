namespace WindowsAppManagerMcp.Models;

/// <summary>
/// What a capture result has in common: whether it succeeded. The tool layer uses it to set isError.
/// </summary>
public interface ICaptureMetadata
{
    bool Success { get; }
}

/// <summary>
/// Metadata of a monitor, region or window capture. The capture tools send it as JSON text next to
/// the image content block; the image itself is never part of it.
/// <see cref="Width"/> and <see cref="Height"/> are the output image size in pixels.
/// <see cref="CapturedRegion"/> is the captured screen rectangle before any scaling, with its real
/// screen origin, also for window captures. <see cref="ImageToScreen"/> maps an image pixel to a screen
/// pixel. <see cref="OriginalWidth"/> and <see cref="OriginalHeight"/> are set only when the image was
/// scaled down. <see cref="ScaleFactor"/> is the monitor's DPI scale, for information only: it plays no
/// part in any coordinate conversion.
/// </summary>
public record ScreenshotResult(
    bool Success,
    string? ImageFormat,
    string? MimeType,
    int Width,
    int Height,
    CapturedRegion? CapturedRegion,
    ImageToScreenTransform? ImageToScreen,
    int? MonitorIndex,
    double? ScaleFactor,
    string? Error = null,
    string? ErrorCode = null,
    string? FilePath = null,
    int? OriginalWidth = null,
    int? OriginalHeight = null,
    long? ByteSize = null,
    string? CaptureMethod = null,
    string? CoordinateSpace = null
) : ICaptureMetadata;

/// <summary>
/// The captured screen rectangle, in physical virtual-screen pixels, before any scaling.
/// X and Y can be negative on monitors left of or above the primary monitor.
/// </summary>
public record CapturedRegion(
    int X,
    int Y,
    int Width,
    int Height
);

/// <summary>
/// Converts a pixel of the output image to a screen point:
/// <c>screenX = OffsetX + imageX * ScaleX</c>, <c>screenY = OffsetY + imageY * ScaleY</c>.
/// The offset is the screen position of the image's top-left pixel; the scale is screen pixels per
/// image pixel (1 unless the image was scaled down).
/// </summary>
public record ImageToScreenTransform(
    int OffsetX,
    int OffsetY,
    double ScaleX,
    double ScaleY
)
{
    /// <summary>The screen point of an image point, rounded to the nearest pixel.</summary>
    public (int X, int Y) ToScreen(double imageX, double imageY) =>
        ((int)Math.Round(OffsetX + imageX * ScaleX, MidpointRounding.AwayFromZero),
         (int)Math.Round(OffsetY + imageY * ScaleY, MidpointRounding.AwayFromZero));
}

/// <summary>Names of the coordinate spaces that capture results report.</summary>
public static class CoordinateSpaces
{
    /// <summary>Physical pixels of the virtual desktop: the space of every screen coordinate the server uses.</summary>
    public const string PhysicalVirtualScreen = "physical-virtual-screen";
}

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
/// Metadata of a window capture with UI element overlays (see <see cref="ScreenshotResult"/> for the
/// shared fields). <see cref="AnnotatedElement.ImageBounds"/> are output-image pixels, measured from
/// the same origin as <see cref="CapturedRegion"/>; <see cref="AnnotatedElement.ScreenBounds"/> stay in
/// screen pixels.
/// </summary>
public record AnnotatedScreenshotResult(
    bool Success,
    string? ImageFormat,
    string? MimeType,
    int Width,
    int Height,
    long WindowHandle,
    string? WindowTitle,
    CapturedRegion? CapturedRegion,
    ImageToScreenTransform? ImageToScreen,
    IReadOnlyList<AnnotatedElement>? Elements,
    int ElementCount,
    double CaptureElapsedMs,
    double AnnotationElapsedMs,
    double? MonitorScaleFactor = null,
    string? Error = null,
    string? ErrorCode = null,
    string? FilePath = null,
    int? OriginalWidth = null,
    int? OriginalHeight = null,
    long? ByteSize = null,
    string? CaptureMethod = null,
    string? CoordinateSpace = null
) : ICaptureMetadata;

/// <summary>
/// An element annotation.
/// <see cref="ImageBounds"/> are pixels of the output image, scaled when the capture is scaled down.
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

/// <summary>An encoded image: PNG or JPEG bytes and their MIME type.</summary>
public sealed record EncodedImage(byte[] Data, string MimeType);

/// <summary>
/// A capture as the screenshot service returns it: the metadata the agent reads, and the encoded
/// image when the requested output includes it (null for output "file" or "metadata", and on failure).
/// </summary>
public sealed record ScreenCapture<TMetadata>(TMetadata Metadata, EncodedImage? Image)
    where TMetadata : ICaptureMetadata;

/// <summary>
/// Capture options as the capture tools receive them. <see cref="Services.CaptureRequest"/> checks
/// and resolves them, so an invalid value is reported with an error code instead of being guessed.
/// </summary>
public sealed record CaptureOptions
{
    /// <summary>Default long edge of the output image: the size vision models work at.</summary>
    public const int DefaultMaxDimension = 1568;

    /// <summary>Default JPEG quality.</summary>
    public const int DefaultQuality = 80;

    /// <summary>With format "auto", images whose long edge is at most this many pixels are PNG, larger ones JPEG.</summary>
    public const int AutoPngMaxEdge = 512;

    /// <summary>"auto" (default), "png", "jpeg" or "jpg".</summary>
    public string? Format { get; init; } = "auto";

    /// <summary>JPEG quality, clamped to 1-100.</summary>
    public int Quality { get; init; } = DefaultQuality;

    /// <summary>
    /// Longest edge of the output image in pixels; 0 keeps full resolution. Null means
    /// <see cref="DefaultMaxDimension"/>, unless <see cref="MaxWidth"/> is set: then only the width is limited.
    /// </summary>
    public int? MaxDimension { get; init; }

    /// <summary>Legacy width limit in pixels. Applies together with <see cref="MaxDimension"/>.</summary>
    public int? MaxWidth { get; init; }

    /// <summary>"image" (default), "file", "both" or "metadata".</summary>
    public string? Output { get; init; } = "image";

    /// <summary>Absolute path of the image file for output "file" or "both". Null writes a new temp file.</summary>
    public string? OutputPath { get; init; }

    /// <summary>Allow <see cref="OutputPath"/> to replace an existing file.</summary>
    public bool Overwrite { get; init; }

    /// <summary>Legacy alias: true turns output "image" or "metadata" into "file".</summary>
    public bool SaveToFile { get; init; }
}
