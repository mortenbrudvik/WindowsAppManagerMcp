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
/// part in any coordinate conversion. <see cref="Warnings"/> say why a successful image may not show what the
/// agent expects (see <see cref="CaptureWarnings"/>); null when there is nothing to say. <see cref="CaptureId"/> names
/// the capture for the mouse tools' image space (roadmap M3.11): they convert a point of this image to the screen
/// themselves, and refuse when the captured window moved since; null when the capture failed or was not registered.
/// <see cref="Ref"/> is set for capture_window with a ref (roadmap M3.11): the element the image is cropped to, under the
/// ref it resolved to (the one asked for, or the element's own ref when it was found again under another).
/// <see cref="RefMoved"/> is true when that ref was moved to a re-created element that alone fits its description (the
/// element it was given for is gone), with a RefMoved warning; null otherwise.
/// <see cref="MarkPrefix"/> is set for get_desktop_state's screenshot with marks (roadmap M3.F32, M3.F38): the window part
/// its tags leave out, as <see cref="AnnotatedScreenshotResult.MarkPrefix"/>; null otherwise.
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
    string? CoordinateSpace = null,
    IReadOnlyList<string>? Warnings = null,
    string? CaptureId = null,
    string? Ref = null,
    bool? RefMoved = null,
    string? MarkPrefix = null
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
/// screen pixels. The pixels are captured even when the UI Automation walk fails: the image then comes with
/// <see cref="ElementsError"/> and <see cref="ElementsErrorCode"/> (the get_ui_elements code, for example
/// OperationTimedOut) and no elements. <see cref="Truncated"/> is true when the element list is incomplete
/// (the walk failed, timed out or skipped elements), and <see cref="TruncationReason"/> says why.
/// <see cref="AccessibilityWarmup"/> is set when the window shows web content and the walk waited for it first (roadmap
/// M3.3), as in get_ui_elements.
/// <see cref="CaptureId"/> is as for <see cref="ScreenshotResult.CaptureId"/>.
/// <see cref="Legend"/> (roadmap M3.11) has one line per mark, in the order of <see cref="Elements"/>, for example
/// <c>[w4120035e17] Button 'Save' #btnSave center=(392,196)</c>: the ref drawn on the mark, the control type, the name,
/// the automation id and the mark's centre in pixels of the returned image (a click there with <see cref="CaptureId"/>
/// lands on it); see Tools.ElementMarkLegend. Null when there are no marks. capture_with_elements leaves
/// <see cref="Elements"/> out unless detail is full.
/// <see cref="MarkPrefix"/> (roadmap M3.F38) is the window part of the refs, such as <c>w4120035</c>, when every mark's ref
/// has it: the tags then leave it out and show only the element part (<c>e17</c>), so a tag is the ref
/// <c>w4120035e17</c> with the prefix before it. Null when no tags were drawn or the refs are of more than one window (the
/// tags then show whole refs). The legend always gives whole refs.
/// <see cref="OwnedWindows"/> names the windows the window owns (an open dialog or popup) that the walk left out, as
/// <see cref="UIElementResult.OwnedWindows"/> (roadmap M3.F9): they get no marks, and are captured and marked by their
/// own handle. Null when there were none.
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
    string? CoordinateSpace = null,
    IReadOnlyList<string>? Warnings = null,
    string? ElementsError = null,
    string? ElementsErrorCode = null,
    bool? Truncated = null,
    string? TruncationReason = null,
    AccessibilityWarmupInfo? AccessibilityWarmup = null,
    string? CaptureId = null,
    string? Legend = null,
    IReadOnlyList<long>? OwnedWindows = null,
    string? MarkPrefix = null
) : ICaptureMetadata;

/// <summary>
/// How capture_with_elements draws its elements (roadmap M3.11). The overlays are drawn on the returned image after it was
/// scaled, so a 2-pixel box and an 11-pixel tag stay legible at any maxDimension.
/// </summary>
/// <param name="Draw">Draw the boxes and tags; false returns the image as captured, with the marks only in the metadata.</param>
/// <param name="Labels">Draw each mark's ref as a filled tag at its box (an element without a ref gets only its box).</param>
/// <param name="HighlightInteractable">Dim the boxes of disabled elements.</param>
public sealed record ElementOverlayOptions(bool Draw = true, bool Labels = true, bool HighlightInteractable = true)
{
    /// <summary>
    /// Mark the elements also when the window moved or resized while they were read, with a WindowMoved warning that the
    /// marks are offset (capture_with_elements, whose legend still names each mark's element). False leaves every mark out
    /// then, with a WindowMoved warning that says so (get_desktop_state's marks, roadmap M3.F32, which are only a picture of
    /// its element lines). Default: true.
    /// </summary>
    public bool MarkMovedWindow { get; init; } = true;

    /// <summary>Boxes and labelled tags, disabled elements dimmed.</summary>
    public static ElementOverlayOptions Default { get; } = new();
}

/// <summary>
/// An element annotation.
/// <see cref="ImageBounds"/> are pixels of the output image, scaled when the capture is scaled down.
/// <see cref="ScreenBounds"/> stay in screen pixels and are not scaled.
/// </summary>
/// <param name="Ref">The element's ref (roadmap M3.4), as get_ui_elements reports it; null when it has none. Serialized
/// first.</param>
public record AnnotatedElement(
    string? Name,
    string ControlType,
    string? AutomationId,
    BoundsDto ImageBounds,
    BoundsDto ScreenBounds,
    bool IsEnabled,
    string OverlayColor,
    [property: System.Text.Json.Serialization.JsonPropertyOrder(-1)] string? Ref = null
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

/// <summary>
/// The warnings a successful capture can carry (roadmap M2.9). Each one starts with a stable code and a colon,
/// so an agent can match on the code; the rest of the sentence says what it means and what to do. capture_with_elements
/// also carries its walk's warnings (<see cref="UIElementWarnings"/>, roadmap M3.F16), in the same form.
/// </summary>
public static class CaptureWarnings
{
    /// <summary>Every sampled pixel has one color.</summary>
    public const string ImageBlank = "ImageBlank";

    /// <summary>The window did not answer, so its pixels were copied from the screen.</summary>
    public const string WindowNotResponding = "WindowNotResponding";

    /// <summary>The requested region was cut to the virtual screen.</summary>
    public const string RegionClipped = "RegionClipped";

    /// <summary>Part of the region is on no monitor (a gap between monitors of different sizes) and is black.</summary>
    public const string RegionPartlyOffMonitors = "RegionPartlyOffMonitors";

    /// <summary>The window is not per-monitor DPI aware, so Windows scales it.</summary>
    public const string DpiScaledWindow = "DpiScaledWindow";

    /// <summary>restoreIfMinimized restored the window for the capture and minimized it again.</summary>
    public const string WindowRestored = "WindowRestored";

    /// <summary>The restored window did not show two equal frames in time.</summary>
    public const string WindowStillPainting = "WindowStillPainting";

    /// <summary>The window could not be minimized again after the capture.</summary>
    public const string ReminimizeFailed = "ReminimizeFailed";

    /// <summary>capture_window's ref was moved to a re-created element: the crop shows that element.</summary>
    public const string RefMoved = "RefMoved";

    /// <summary>
    /// capture_with_elements: the window moved or resized while its elements were read, so the marks are offset from the
    /// image, which then has no captureId.
    /// </summary>
    public const string WindowMoved = "WindowMoved";

    /// <summary>"Code: text".</summary>
    public static string Format(string code, string text) => $"{code}: {text}";
}
