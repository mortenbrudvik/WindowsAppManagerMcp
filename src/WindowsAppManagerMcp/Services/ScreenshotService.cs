using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Globalization;
using System.Runtime.InteropServices;
using WindowsAppManagerMcp.Models;
using WindowsAppManagerMcp.Services.Interfaces;

namespace WindowsAppManagerMcp.Services;

public class ScreenshotService : IScreenshotService
{
    /// <summary>Where captures go when output writes a file and no outputPath is given.</summary>
    public static readonly string DefaultCaptureFolder =
        Path.Combine(Path.GetTempPath(), "WindowsAppManagerMcp", "captures");

    private readonly IScreenCaptureWrapper _captureWrapper;
    private readonly IMonitorService _monitorService;

    public ScreenshotService(IScreenCaptureWrapper captureWrapper, IMonitorService monitorService)
    {
        _captureWrapper = captureWrapper;
        _monitorService = monitorService;
    }

    public ScreenListResult ListScreens()
    {
        var monitors = _monitorService.GetAllMonitors();
        var virtualBounds = _captureWrapper.GetVirtualScreenBounds();

        var screens = monitors.Select(m => new ScreenInfo(
            Index: m.Index,
            Name: m.DeviceName,
            IsPrimary: m.IsPrimary,
            X: m.Bounds.X,
            Y: m.Bounds.Y,
            Width: m.Bounds.Width,
            Height: m.Bounds.Height,
            WorkAreaX: m.WorkArea.X,
            WorkAreaY: m.WorkArea.Y,
            WorkAreaWidth: m.WorkArea.Width,
            WorkAreaHeight: m.WorkArea.Height,
            ScaleFactor: m.ScaleFactor
        )).ToList();

        return new ScreenListResult(
            Screens: screens,
            PrimaryIndex: monitors.FirstOrDefault(m => m.IsPrimary)?.Index ?? 0,
            VirtualScreen: new VirtualScreenBounds(
                virtualBounds.X,
                virtualBounds.Y,
                virtualBounds.Width,
                virtualBounds.Height
            )
        );
    }

    public ScreenCapture<ScreenshotResult> CaptureMonitor(int monitorIndex, CaptureOptions options) =>
        Run("monitor", options, request =>
        {
            var monitors = _monitorService.GetAllMonitors();
            if (monitorIndex < 0 || monitorIndex >= monitors.Count)
            {
                throw new CaptureException(ScreenshotErrorCode.InvalidMonitorIndex,
                    $"Monitor index {monitorIndex} is out of range. Available monitors: 0-{monitors.Count - 1}");
            }

            var monitor = monitors[monitorIndex];
            var bounds = monitor.Bounds;
            var capture = _captureWrapper.CaptureScreenRegion(bounds.X, bounds.Y, bounds.Width, bounds.Height);
            return Complete(capture, request, monitorIndex, monitor.ScaleFactor);
        });

    public ScreenCapture<ScreenshotResult> CaptureRegion(int x, int y, int width, int height, CaptureOptions options) =>
        Run("region", options, request =>
        {
            if (width <= 0 || height <= 0)
            {
                throw new CaptureException(ScreenshotErrorCode.InvalidRegion, "Width and height must be positive values");
            }

            var capture = _captureWrapper.CaptureScreenRegion(x, y, width, height);

            // Determine which monitor contains the center of the region
            var monitor = _monitorService.GetMonitorAt(x + width / 2, y + height / 2);
            return Complete(capture, request, monitor?.Index, monitor?.ScaleFactor ?? 1.0);
        });

    public ScreenCapture<ScreenshotResult> CaptureWindow(nint windowHandle, bool includeFrame, CaptureOptions options) =>
        Run("window", options, request =>
        {
            if (!_captureWrapper.IsValidWindow(windowHandle))
            {
                throw new CaptureException(ScreenshotErrorCode.InvalidHandle, "Invalid window handle or window no longer exists");
            }

            var capture = _captureWrapper.CaptureWindow(windowHandle, includeFrame);
            var monitor = _monitorService.GetMonitorForWindow(windowHandle);
            return Complete(capture, request, monitor?.Index, monitor?.ScaleFactor ?? 1.0);
        });

    public ScreenCapture<AnnotatedScreenshotResult> CaptureWindowWithElements(
        nint windowHandle,
        string? windowTitle,
        IReadOnlyList<UIElementInfo> elements,
        bool includeFrame,
        bool highlightInteractable,
        CaptureOptions options)
    {
        try
        {
            var request = CaptureRequest.Resolve(options);

            if (!_captureWrapper.IsValidWindow(windowHandle))
            {
                throw new CaptureException(ScreenshotErrorCode.InvalidHandle, "Invalid window handle or window no longer exists");
            }

            var captureStopwatch = Stopwatch.StartNew();
            var capture = CaptureOrFail("window", () => _captureWrapper.CaptureWindow(windowHandle, includeFrame));
            var captureMs = captureStopwatch.Elapsed.TotalMilliseconds;

            var annotationStopwatch = Stopwatch.StartNew();
            var annotations = new List<AnnotatedElement>();
            var rendered = Render(capture, request, bitmap =>
            {
                using var graphics = Graphics.FromImage(bitmap);
                graphics.SmoothingMode = SmoothingMode.AntiAlias;

                // Element bounds are screen pixels; the image starts at the capture's screen origin,
                // which is the client area's corner when the frame is left out.
                DrawElementOverlays(graphics, elements, capture.ScreenOriginX, capture.ScreenOriginY,
                    capture.Width, capture.Height, highlightInteractable, annotations);
            });

            IReadOnlyList<AnnotatedElement> finalAnnotations = rendered.IsScaled
                ? ScaleAnnotations(annotations, rendered.Width, rendered.Height, capture.Width, capture.Height)
                : annotations;
            var annotationMs = annotationStopwatch.Elapsed.TotalMilliseconds;

            var monitor = _monitorService.GetMonitorForWindow(windowHandle);

            var metadata = new AnnotatedScreenshotResult(
                Success: true,
                ImageFormat: CaptureRequest.FormatName(rendered.Encoding),
                MimeType: rendered.Image.MimeType,
                Width: rendered.Width,
                Height: rendered.Height,
                WindowHandle: (long)windowHandle,
                WindowTitle: windowTitle,
                CapturedRegion: CapturedRegionOf(capture),
                ImageToScreen: TransformOf(capture, rendered),
                Elements: finalAnnotations,
                ElementCount: finalAnnotations.Count,
                CaptureElapsedMs: Math.Round(captureMs, 2),
                AnnotationElapsedMs: Math.Round(annotationMs, 2),
                MonitorScaleFactor: monitor?.ScaleFactor,
                FilePath: rendered.FilePath,
                OriginalWidth: rendered.IsScaled ? capture.Width : null,
                OriginalHeight: rendered.IsScaled ? capture.Height : null,
                ByteSize: rendered.Image.Data.LongLength,
                CaptureMethod: capture.Method,
                CoordinateSpace: CoordinateSpaces.PhysicalVirtualScreen);

            return new ScreenCapture<AnnotatedScreenshotResult>(metadata, request.ReturnsImage ? rendered.Image : null);
        }
        catch (CaptureException ex)
        {
            return new ScreenCapture<AnnotatedScreenshotResult>(
                AnnotatedFailure(windowHandle, windowTitle, ex.Code, ex.Message), null);
        }
        catch (Exception ex)
        {
            return new ScreenCapture<AnnotatedScreenshotResult>(
                AnnotatedFailure(windowHandle, windowTitle, ScreenshotErrorCode.CaptureFailed,
                    $"Failed to capture window with elements: {ex.Message}"),
                null);
        }
    }

    /// <summary>
    /// Resolves the options, then runs <paramref name="capture"/>. A <see cref="CaptureException"/>
    /// becomes a failure with its code; any other exception (the native capture or the encoder
    /// failed) becomes <see cref="ScreenshotErrorCode.CaptureFailed"/>.
    /// </summary>
    private static ScreenCapture<ScreenshotResult> Run(
        string what,
        CaptureOptions options,
        Func<CaptureRequest, ScreenCapture<ScreenshotResult>> capture)
    {
        try
        {
            return capture(CaptureRequest.Resolve(options));
        }
        catch (CaptureException ex)
        {
            return new ScreenCapture<ScreenshotResult>(Failure(ex.Code, ex.Message), null);
        }
        catch (Exception ex)
        {
            return new ScreenCapture<ScreenshotResult>(
                Failure(ScreenshotErrorCode.CaptureFailed, $"Failed to capture {what}: {ex.Message}"), null);
        }
    }

    private static CaptureResult CaptureOrFail(string what, Func<CaptureResult> capture)
    {
        try
        {
            return capture();
        }
        catch (Exception ex) when (ex is not CaptureException)
        {
            throw new CaptureException(ScreenshotErrorCode.CaptureFailed, $"Failed to capture {what}: {ex.Message}");
        }
    }

    private static ScreenCapture<ScreenshotResult> Complete(
        CaptureResult capture,
        CaptureRequest request,
        int? monitorIndex,
        double scaleFactor)
    {
        var rendered = Render(capture, request, annotate: null);

        var metadata = new ScreenshotResult(
            Success: true,
            ImageFormat: CaptureRequest.FormatName(rendered.Encoding),
            MimeType: rendered.Image.MimeType,
            Width: rendered.Width,
            Height: rendered.Height,
            CapturedRegion: CapturedRegionOf(capture),
            ImageToScreen: TransformOf(capture, rendered),
            MonitorIndex: monitorIndex,
            ScaleFactor: scaleFactor,
            FilePath: rendered.FilePath,
            OriginalWidth: rendered.IsScaled ? capture.Width : null,
            OriginalHeight: rendered.IsScaled ? capture.Height : null,
            ByteSize: rendered.Image.Data.LongLength,
            CaptureMethod: capture.Method,
            CoordinateSpace: CoordinateSpaces.PhysicalVirtualScreen);

        return new ScreenCapture<ScreenshotResult>(metadata, request.ReturnsImage ? rendered.Image : null);
    }

    /// <summary>The encoded output image and where it was written.</summary>
    private sealed record RenderedImage(
        EncodedImage Image,
        ImageEncoding Encoding,
        int Width,
        int Height,
        bool IsScaled,
        string? FilePath);

    /// <summary>
    /// Turns raw pixels into the output image: optional overlays at full resolution, scaling to the
    /// size limit, encoding, and the file write when the output includes a file.
    /// </summary>
    private static RenderedImage Render(CaptureResult capture, CaptureRequest request, Action<Bitmap>? annotate)
    {
        var (width, height) = request.OutputSize(capture.Width, capture.Height);
        var isScaled = width != capture.Width || height != capture.Height;
        var encoding = request.EncodingFor(width, height);

        using var bitmap = CreateBitmap(capture);
        annotate?.Invoke(bitmap);

        using var scaled = isScaled ? ResizeImage(bitmap, width, height) : null;
        var data = Encode(scaled ?? bitmap, encoding, request.Quality);
        var filePath = request.WritesFile ? WriteFile(data, encoding, request) : null;

        return new RenderedImage(
            new EncodedImage(data, CaptureRequest.MimeType(encoding)),
            encoding,
            width,
            height,
            isScaled,
            filePath);
    }

    private static CapturedRegion CapturedRegionOf(CaptureResult capture) =>
        new(capture.ScreenOriginX, capture.ScreenOriginY, capture.Width, capture.Height);

    private static ImageToScreenTransform TransformOf(CaptureResult capture, RenderedImage rendered) =>
        new(
            OffsetX: capture.ScreenOriginX,
            OffsetY: capture.ScreenOriginY,
            ScaleX: Math.Round((double)capture.Width / rendered.Width, 6),
            ScaleY: Math.Round((double)capture.Height / rendered.Height, 6));

    /// <summary>
    /// Copies the captured pixels into a bitmap that ignores the 4th byte. GDI leaves that byte
    /// undefined (often 0), so reading it as alpha would make parts of a PNG transparent.
    /// </summary>
    private static Bitmap CreateBitmap(CaptureResult capture)
    {
        var expected = (long)capture.Width * capture.Height * 4;
        if (capture.Bgra.LongLength < expected)
        {
            throw new InvalidOperationException(
                $"The capture returned {capture.Bgra.LongLength} bytes for {capture.Width}x{capture.Height} pixels.");
        }

        var bitmap = new Bitmap(capture.Width, capture.Height, PixelFormat.Format32bppRgb);
        try
        {
            var bitmapData = bitmap.LockBits(
                new Rectangle(0, 0, capture.Width, capture.Height),
                ImageLockMode.WriteOnly,
                PixelFormat.Format32bppRgb);

            try
            {
                Marshal.Copy(capture.Bgra, 0, bitmapData.Scan0, (int)expected);
            }
            finally
            {
                bitmap.UnlockBits(bitmapData);
            }

            return bitmap;
        }
        catch
        {
            bitmap.Dispose();
            throw;
        }
    }

    private static Bitmap ResizeImage(Bitmap original, int width, int height)
    {
        var resized = new Bitmap(width, height, PixelFormat.Format32bppRgb);
        try
        {
            using var graphics = Graphics.FromImage(resized);
            graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
            graphics.CompositingQuality = CompositingQuality.HighQuality;
            graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
            graphics.SmoothingMode = SmoothingMode.HighQuality;

            // Without TileFlipXY the bicubic filter samples outside the source and darkens the edges.
            using var attributes = new ImageAttributes();
            attributes.SetWrapMode(WrapMode.TileFlipXY);
            graphics.DrawImage(
                original,
                new Rectangle(0, 0, width, height),
                0, 0, original.Width, original.Height,
                GraphicsUnit.Pixel,
                attributes);
            return resized;
        }
        catch
        {
            resized.Dispose();
            throw;
        }
    }

    private static byte[] Encode(Bitmap bitmap, ImageEncoding encoding, int quality)
    {
        using var stream = new MemoryStream();

        if (encoding == ImageEncoding.Png)
        {
            bitmap.Save(stream, ImageFormat.Png);
        }
        else
        {
            var encoder = ImageCodecInfo.GetImageEncoders().First(c => c.FormatID == ImageFormat.Jpeg.Guid);
            using var encoderParameters = new EncoderParameters(1);
            encoderParameters.Param[0] = new EncoderParameter(Encoder.Quality, (long)Math.Clamp(quality, 1, 100));
            bitmap.Save(stream, encoder, encoderParameters);
        }

        return stream.ToArray();
    }

    /// <summary>
    /// Writes the encoded image to the requested path, or to a new file in
    /// <see cref="DefaultCaptureFolder"/>. An existing file is replaced only with overwrite; the
    /// folder of an explicit path must already exist.
    /// </summary>
    private static string WriteFile(byte[] data, ImageEncoding encoding, CaptureRequest request)
    {
        var path = request.OutputPath;
        if (path == null)
        {
            var timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture);
            path = Path.Combine(DefaultCaptureFolder, $"screenshot_{timestamp}_{Guid.NewGuid():N}{CaptureRequest.Extension(encoding)}");
        }

        var mode = request.Overwrite && request.OutputPath != null ? FileMode.Create : FileMode.CreateNew;
        try
        {
            if (request.OutputPath == null)
            {
                // The server's own folder; explicit paths must name an existing folder.
                Directory.CreateDirectory(DefaultCaptureFolder);
            }

            using var stream = new FileStream(path, mode, FileAccess.Write, FileShare.None);
            stream.Write(data);
        }
        catch (IOException) when (mode == FileMode.CreateNew && File.Exists(path))
        {
            // Created between the check in CaptureRequest and this write.
            throw CaptureRequest.FileExists(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new CaptureException(ScreenshotErrorCode.FileWriteFailed, $"Could not write '{path}': {ex.Message}");
        }

        return path;
    }

    /// <summary>
    /// Scales ImageBounds from the full-resolution capture to the output image. ScreenBounds stay in
    /// screen pixels.
    /// </summary>
    private static List<AnnotatedElement> ScaleAnnotations(
        IReadOnlyList<AnnotatedElement> annotations,
        int finalWidth,
        int finalHeight,
        int originalWidth,
        int originalHeight)
    {
        var scaleX = (double)finalWidth / originalWidth;
        var scaleY = (double)finalHeight / originalHeight;

        return annotations.Select(annotation =>
        {
            var x = ClampToImage(RoundScaled(annotation.ImageBounds.X, scaleX), finalWidth);
            var y = ClampToImage(RoundScaled(annotation.ImageBounds.Y, scaleY), finalHeight);
            var width = ScaleLength(annotation.ImageBounds.Width, scaleX, finalWidth - x);
            var height = ScaleLength(annotation.ImageBounds.Height, scaleY, finalHeight - y);
            return annotation with { ImageBounds = new BoundsDto(x, y, width, height) };
        }).ToList();
    }

    private static int RoundScaled(int value, double scale) =>
        (int)Math.Round(value * scale, MidpointRounding.AwayFromZero);

    private static int ClampToImage(int value, int limit) =>
        Math.Clamp(value, 0, Math.Max(0, limit - 1));

    private static int ScaleLength(int value, double scale, int room)
    {
        if (value <= 0 || room <= 0)
        {
            return 0;
        }

        var scaled = Math.Max(1, RoundScaled(value, scale));
        return Math.Min(scaled, room);
    }

    private static void DrawElementOverlays(
        Graphics graphics,
        IReadOnlyList<UIElementInfo> elements,
        int originX,
        int originY,
        int imageWidth,
        int imageHeight,
        bool highlightInteractable,
        List<AnnotatedElement> annotations)
    {
        foreach (var element in elements)
        {
            DrawElementOverlay(graphics, element, originX, originY, imageWidth, imageHeight, highlightInteractable, annotations);
        }
    }

    private static void DrawElementOverlay(
        Graphics graphics,
        UIElementInfo element,
        int originX,
        int originY,
        int imageWidth,
        int imageHeight,
        bool highlightInteractable,
        List<AnnotatedElement> annotations)
    {
        // Elements smaller than 2 pixels, or without bounds, get no overlay; their children still can.
        if (element.Bounds is { Width: >= 2, Height: >= 2 } screenBounds)
        {
            // The element's position in the image, clipped to the image
            var visible = Rectangle.Intersect(
                new Rectangle(screenBounds.X - originX, screenBounds.Y - originY, screenBounds.Width, screenBounds.Height),
                new Rectangle(0, 0, imageWidth, imageHeight));

            if (visible.Width > 0 && visible.Height > 0)
            {
                var (color, colorName) = GetOverlayColor(element.ControlType, element.IsEnabled, highlightInteractable);
                using var pen = new Pen(color, 2);
                graphics.DrawRectangle(pen, visible);

                annotations.Add(new AnnotatedElement(
                    Name: element.Name,
                    ControlType: element.ControlType,
                    AutomationId: element.AutomationId,
                    ImageBounds: new BoundsDto(visible.X, visible.Y, visible.Width, visible.Height),
                    ScreenBounds: screenBounds,
                    IsEnabled: element.IsEnabled,
                    OverlayColor: colorName
                ));
            }
        }

        if (element.Children != null)
        {
            DrawElementOverlays(graphics, element.Children, originX, originY, imageWidth, imageHeight, highlightInteractable, annotations);
        }
    }

    private static (Color color, string name) GetOverlayColor(string controlType, bool isEnabled, bool highlightInteractable)
    {
        // Dim colors for disabled elements when highlighting interactable
        var alpha = (highlightInteractable && !isEnabled) ? 100 : 200;

        return controlType switch
        {
            "Button" or "SplitButton" => (Color.FromArgb(alpha, 0, 200, 0), "green"),      // Green for buttons
            "Edit" or "Document" => (Color.FromArgb(alpha, 0, 150, 255), "blue"),          // Blue for text input
            "CheckBox" or "RadioButton" => (Color.FromArgb(alpha, 255, 165, 0), "orange"), // Orange for toggles
            "ComboBox" or "List" or "ListItem" => (Color.FromArgb(alpha, 200, 0, 200), "purple"), // Purple for lists
            "Hyperlink" => (Color.FromArgb(alpha, 0, 200, 200), "cyan"),                   // Cyan for links
            "MenuItem" or "Menu" or "MenuBar" => (Color.FromArgb(alpha, 255, 100, 100), "red"), // Red for menus
            "Tab" or "TabItem" => (Color.FromArgb(alpha, 255, 200, 0), "yellow"),          // Yellow for tabs
            "Image" => (Color.FromArgb(alpha, 150, 150, 150), "gray"),                     // Gray for images
            "Text" => (Color.FromArgb(alpha, 180, 180, 180), "lightgray"),                 // Light gray for text
            _ => (Color.FromArgb(alpha, 100, 100, 255), "lightblue")                       // Light blue for others
        };
    }

    private static ScreenshotResult Failure(ScreenshotErrorCode code, string error) =>
        new(
            Success: false,
            ImageFormat: null,
            MimeType: null,
            Width: 0,
            Height: 0,
            CapturedRegion: null,
            ImageToScreen: null,
            MonitorIndex: null,
            ScaleFactor: null,
            Error: error,
            ErrorCode: code.ToString());

    private static AnnotatedScreenshotResult AnnotatedFailure(
        nint windowHandle,
        string? windowTitle,
        ScreenshotErrorCode code,
        string error) =>
        new(
            Success: false,
            ImageFormat: null,
            MimeType: null,
            Width: 0,
            Height: 0,
            WindowHandle: (long)windowHandle,
            WindowTitle: windowTitle,
            CapturedRegion: null,
            ImageToScreen: null,
            Elements: null,
            ElementCount: 0,
            CaptureElapsedMs: 0,
            AnnotationElapsedMs: 0,
            Error: error,
            ErrorCode: code.ToString());
}
