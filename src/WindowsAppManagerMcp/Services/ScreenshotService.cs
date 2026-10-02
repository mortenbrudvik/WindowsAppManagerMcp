using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using WindowsAppManagerMcp.Models;
using WindowsAppManagerMcp.Services.Interfaces;

namespace WindowsAppManagerMcp.Services;

public class ScreenshotService : IScreenshotService
{
    internal const string MaxWidthMustBePositive = "maxWidth must be a positive number of pixels.";

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

    public ScreenshotResult CaptureMonitor(int monitorIndex, string format = "png", int quality = 85, bool saveToFile = false, string? outputPath = null, int? maxWidth = null)
    {
        if (maxWidth is <= 0)
        {
            return CreateErrorResult(MaxWidthMustBePositive);
        }

        try
        {
            var monitors = _monitorService.GetAllMonitors();
            if (monitorIndex < 0 || monitorIndex >= monitors.Count)
            {
                return CreateErrorResult($"Monitor index {monitorIndex} is out of range. Available monitors: 0-{monitors.Count - 1}");
            }

            var monitor = monitors[monitorIndex];
            var bounds = monitor.Bounds;

            if (CollapsedHeightError(bounds.Width, bounds.Height, maxWidth) is { } heightError)
            {
                return CreateErrorResult(heightError);
            }

            var normalizedFormat = NormalizeFormat(format);
            var pixelData = _captureWrapper.CaptureScreenRegion(bounds.X, bounds.Y, bounds.Width, bounds.Height);
            using var bitmap = CreateBitmapFromPixelData(pixelData, bounds.Width, bounds.Height);
            var stored = StoreImage(bitmap, bounds.Width, bounds.Height, normalizedFormat, quality, saveToFile, outputPath, maxWidth);

            return new ScreenshotResult(
                Success: true,
                ImageData: stored.ImageData,
                ImageFormat: normalizedFormat,
                Width: stored.Width,
                Height: stored.Height,
                CapturedRegion: new CapturedRegion(bounds.X, bounds.Y, bounds.Width, bounds.Height),
                MonitorIndex: monitorIndex,
                ScaleFactor: monitor.ScaleFactor,
                FilePath: stored.FilePath,
                OriginalWidth: stored.OriginalWidth,
                OriginalHeight: stored.OriginalHeight
            );
        }
        catch (Exception ex)
        {
            return CreateErrorResult($"Failed to capture monitor: {ex.Message}");
        }
    }

    public ScreenshotResult CaptureRegion(int x, int y, int width, int height, string format = "png", int quality = 85, bool saveToFile = false, string? outputPath = null, int? maxWidth = null)
    {
        if (maxWidth is <= 0)
        {
            return CreateErrorResult(MaxWidthMustBePositive);
        }

        try
        {
            if (width <= 0 || height <= 0)
            {
                return CreateErrorResult("Width and height must be positive values");
            }

            if (CollapsedHeightError(width, height, maxWidth) is { } heightError)
            {
                return CreateErrorResult(heightError);
            }

            var normalizedFormat = NormalizeFormat(format);
            var pixelData = _captureWrapper.CaptureScreenRegion(x, y, width, height);
            using var bitmap = CreateBitmapFromPixelData(pixelData, width, height);
            var stored = StoreImage(bitmap, width, height, normalizedFormat, quality, saveToFile, outputPath, maxWidth);

            // Determine which monitor contains the center of the region
            var centerX = x + width / 2;
            var centerY = y + height / 2;
            var monitor = _monitorService.GetMonitorAt(centerX, centerY);

            return new ScreenshotResult(
                Success: true,
                ImageData: stored.ImageData,
                ImageFormat: normalizedFormat,
                Width: stored.Width,
                Height: stored.Height,
                CapturedRegion: new CapturedRegion(x, y, width, height),
                MonitorIndex: monitor?.Index,
                ScaleFactor: monitor?.ScaleFactor ?? 1.0,
                FilePath: stored.FilePath,
                OriginalWidth: stored.OriginalWidth,
                OriginalHeight: stored.OriginalHeight
            );
        }
        catch (Exception ex)
        {
            return CreateErrorResult($"Failed to capture region: {ex.Message}");
        }
    }

    public ScreenshotResult CaptureWindow(nint windowHandle, bool includeFrame = true, string format = "png", int quality = 85, bool saveToFile = false, string? outputPath = null, int? maxWidth = null)
    {
        if (maxWidth is <= 0)
        {
            return CreateErrorResult(MaxWidthMustBePositive);
        }

        try
        {
            if (!_captureWrapper.IsValidWindow(windowHandle))
            {
                return CreateErrorResult("Invalid window handle or window no longer exists");
            }

            var normalizedFormat = NormalizeFormat(format);
            var (pixelData, width, height) = _captureWrapper.CaptureWindow(windowHandle, includeFrame);

            if (CollapsedHeightError(width, height, maxWidth) is { } heightError)
            {
                return CreateErrorResult(heightError);
            }

            using var bitmap = CreateBitmapFromPixelData(pixelData, width, height);
            var stored = StoreImage(bitmap, width, height, normalizedFormat, quality, saveToFile, outputPath, maxWidth);

            // Get the monitor for this window
            var monitor = _monitorService.GetMonitorForWindow(windowHandle);

            return new ScreenshotResult(
                Success: true,
                ImageData: stored.ImageData,
                ImageFormat: normalizedFormat,
                Width: stored.Width,
                Height: stored.Height,
                CapturedRegion: new CapturedRegion(0, 0, width, height),
                MonitorIndex: monitor?.Index,
                ScaleFactor: monitor?.ScaleFactor ?? 1.0,
                FilePath: stored.FilePath,
                OriginalWidth: stored.OriginalWidth,
                OriginalHeight: stored.OriginalHeight
            );
        }
        catch (Exception ex)
        {
            return CreateErrorResult($"Failed to capture window: {ex.Message}");
        }
    }

    private static string NormalizeFormat(string format)
    {
        format = format.ToLowerInvariant();
        return format is "png" or "jpeg" or "jpg" ? format : "png";
    }

    private readonly record struct StoredImage(
        string? ImageData,
        string? FilePath,
        int Width,
        int Height,
        int? OriginalWidth,
        int? OriginalHeight);

    private static StoredImage StoreImage(
        Bitmap source,
        int width,
        int height,
        string format,
        int quality,
        bool saveToFile,
        string? outputPath,
        int? maxWidth)
    {
        Bitmap? resized = null;
        try
        {
            var output = source;
            var finalWidth = width;
            var finalHeight = height;
            int? originalWidth = null;
            int? originalHeight = null;

            if (maxWidth is int limit && width > limit)
            {
                originalWidth = width;
                originalHeight = height;
                resized = ResizeImage(source, limit);
                output = resized;
                finalWidth = resized.Width;
                finalHeight = resized.Height;
            }

            string? imageData = null;
            string? filePath = null;
            if (saveToFile)
            {
                filePath = SaveBitmapToFile(output, format, quality, outputPath);
            }
            else
            {
                imageData = EncodeBitmap(output, format, quality);
            }

            return new StoredImage(imageData, filePath, finalWidth, finalHeight, originalWidth, originalHeight);
        }
        finally
        {
            if (resized != null && !ReferenceEquals(resized, source))
            {
                resized.Dispose();
            }
        }
    }

    private static string? CollapsedHeightError(int width, int height, int? maxWidth)
    {
        if (maxWidth is not int limit || width <= limit)
        {
            return null;
        }

        var newHeight = (int)(height * ((double)limit / width));
        if (newHeight >= 1)
        {
            return null;
        }

        return $"maxWidth {limit} would scale a {width}x{height} image to height 0.";
    }

    private static Bitmap ResizeImage(Bitmap original, int maxWidth)
    {
        if (maxWidth <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maxWidth), MaxWidthMustBePositive);
        }

        // Returning the source is an alias. Callers must not dispose it.
        if (original.Width <= maxWidth)
        {
            return original;
        }

        double scale = (double)maxWidth / original.Width;
        int newHeight = (int)(original.Height * scale);
        if (newHeight < 1)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maxWidth),
                $"maxWidth {maxWidth} would scale a {original.Width}x{original.Height} image to height 0.");
        }

        var resized = new Bitmap(maxWidth, newHeight);
        try
        {
            using var g = Graphics.FromImage(resized);
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.CompositingQuality = CompositingQuality.HighQuality;
            g.SmoothingMode = SmoothingMode.HighQuality;
            g.DrawImage(original, 0, 0, maxWidth, newHeight);
            return resized;
        }
        catch
        {
            resized.Dispose();
            throw;
        }
    }

    private static List<AnnotatedElement> ScaleAnnotations(
        IReadOnlyList<AnnotatedElement> annotations,
        double scale,
        int finalWidth,
        int finalHeight)
    {
        return annotations.Select(annotation =>
        {
            var x = ClampToImage(RoundScaled(annotation.ImageBounds.X, scale), finalWidth);
            var y = ClampToImage(RoundScaled(annotation.ImageBounds.Y, scale), finalHeight);
            var width = ScaleLength(annotation.ImageBounds.Width, scale, finalWidth - x);
            var height = ScaleLength(annotation.ImageBounds.Height, scale, finalHeight - y);
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

    private static string GenerateTempFilePath(string format)
    {
        var extension = format == "jpeg" || format == "jpg" ? "jpg" : "png";
        var timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
        var fileName = $"screenshot_{timestamp}_{Guid.NewGuid():N}.{extension}";
        return Path.Combine(Path.GetTempPath(), fileName);
    }

    private static string SaveImageToFile(byte[] pixelData, int width, int height, string format, int quality, string? outputPath)
    {
        var filePath = outputPath ?? GenerateTempFilePath(format);
        quality = Math.Clamp(quality, 1, 100);

        // Ensure directory exists
        var directory = Path.GetDirectoryName(filePath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        // Create bitmap from raw BGRA pixel data
        using var bitmap = new Bitmap(width, height, PixelFormat.Format32bppArgb);

        var bitmapData = bitmap.LockBits(
            new Rectangle(0, 0, width, height),
            ImageLockMode.WriteOnly,
            PixelFormat.Format32bppArgb);

        try
        {
            System.Runtime.InteropServices.Marshal.Copy(pixelData, 0, bitmapData.Scan0, pixelData.Length);
        }
        finally
        {
            bitmap.UnlockBits(bitmapData);
        }

        if (format == "png")
        {
            bitmap.Save(filePath, ImageFormat.Png);
        }
        else
        {
            var encoder = ImageCodecInfo.GetImageEncoders()
                .First(c => c.MimeType == "image/jpeg");
            var encoderParams = new EncoderParameters(1);
            encoderParams.Param[0] = new EncoderParameter(Encoder.Quality, quality);
            bitmap.Save(filePath, encoder, encoderParams);
        }

        return filePath;
    }

    private static string SaveBitmapToFile(Bitmap bitmap, string format, int quality, string? outputPath)
    {
        var filePath = outputPath ?? GenerateTempFilePath(format);
        quality = Math.Clamp(quality, 1, 100);

        // Ensure directory exists
        var directory = Path.GetDirectoryName(filePath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        if (format == "png")
        {
            bitmap.Save(filePath, ImageFormat.Png);
        }
        else
        {
            var encoder = ImageCodecInfo.GetImageEncoders()
                .First(c => c.MimeType == "image/jpeg");
            var encoderParams = new EncoderParameters(1);
            encoderParams.Param[0] = new EncoderParameter(Encoder.Quality, quality);
            bitmap.Save(filePath, encoder, encoderParams);
        }

        return filePath;
    }

    private static string EncodeImage(byte[] pixelData, int width, int height, string format, int quality)
    {
        quality = Math.Clamp(quality, 1, 100);

        // Create bitmap from raw BGRA pixel data
        using var bitmap = new Bitmap(width, height, PixelFormat.Format32bppArgb);

        var bitmapData = bitmap.LockBits(
            new Rectangle(0, 0, width, height),
            ImageLockMode.WriteOnly,
            PixelFormat.Format32bppArgb);

        try
        {
            System.Runtime.InteropServices.Marshal.Copy(pixelData, 0, bitmapData.Scan0, pixelData.Length);
        }
        finally
        {
            bitmap.UnlockBits(bitmapData);
        }

        using var memoryStream = new MemoryStream();

        if (format == "png")
        {
            bitmap.Save(memoryStream, ImageFormat.Png);
        }
        else
        {
            // JPEG with quality setting
            var encoder = ImageCodecInfo.GetImageEncoders()
                .First(c => c.MimeType == "image/jpeg");
            var encoderParams = new EncoderParameters(1);
            encoderParams.Param[0] = new EncoderParameter(Encoder.Quality, quality);
            bitmap.Save(memoryStream, encoder, encoderParams);
        }

        return Convert.ToBase64String(memoryStream.ToArray());
    }

    public AnnotatedScreenshotResult CaptureWindowWithElements(
        nint windowHandle,
        IReadOnlyList<UIElementInfo> elements,
        (int X, int Y, int Width, int Height) windowBounds,
        bool includeFrame = true,
        string format = "png",
        int quality = 85,
        bool highlightInteractable = true,
        bool saveToFile = false,
        string? outputPath = null,
        int? maxWidth = null)
    {
        if (maxWidth is <= 0)
        {
            return CreateAnnotatedErrorResult(windowHandle, MaxWidthMustBePositive);
        }

        var captureStopwatch = System.Diagnostics.Stopwatch.StartNew();

        try
        {
            if (!_captureWrapper.IsValidWindow(windowHandle))
            {
                return CreateAnnotatedErrorResult(windowHandle, "Invalid window handle or window no longer exists");
            }

            var normalizedFormat = NormalizeFormat(format);
            var (pixelData, width, height) = _captureWrapper.CaptureWindow(windowHandle, includeFrame);

            captureStopwatch.Stop();
            var captureMs = captureStopwatch.Elapsed.TotalMilliseconds;

            if (CollapsedHeightError(width, height, maxWidth) is { } heightError)
            {
                return CreateAnnotatedErrorResult(windowHandle, heightError);
            }

            var annotationStopwatch = System.Diagnostics.Stopwatch.StartNew();

            using var bitmap = CreateBitmapFromPixelData(pixelData, width, height);

            var annotations = new List<AnnotatedElement>();
            using (var graphics = Graphics.FromImage(bitmap))
            {
                graphics.SmoothingMode = SmoothingMode.AntiAlias;
                DrawElementOverlays(graphics, elements, windowBounds, width, height, highlightInteractable, annotations);
            }

            var stored = StoreImage(bitmap, width, height, normalizedFormat, quality, saveToFile, outputPath, maxWidth);
            IReadOnlyList<AnnotatedElement> finalAnnotations = annotations;
            if (stored.OriginalWidth is int originalWidth && originalWidth > 0)
            {
                // ScreenBounds stay in screen pixels. Only ImageBounds follow the output image.
                var scale = (double)stored.Width / originalWidth;
                finalAnnotations = ScaleAnnotations(annotations, scale, stored.Width, stored.Height);
            }

            annotationStopwatch.Stop();

            var monitor = _monitorService.GetMonitorForWindow(windowHandle);

            return new AnnotatedScreenshotResult(
                Success: true,
                ImageData: stored.ImageData,
                ImageFormat: normalizedFormat,
                Width: stored.Width,
                Height: stored.Height,
                WindowHandle: (long)windowHandle,
                WindowTitle: null, // Will be filled by the tool layer
                Elements: finalAnnotations,
                ElementCount: finalAnnotations.Count,
                CaptureElapsedMs: Math.Round(captureMs, 2),
                AnnotationElapsedMs: Math.Round(annotationStopwatch.Elapsed.TotalMilliseconds, 2),
                MonitorScaleFactor: monitor?.ScaleFactor,
                FilePath: stored.FilePath,
                OriginalWidth: stored.OriginalWidth,
                OriginalHeight: stored.OriginalHeight
            );
        }
        catch (Exception ex)
        {
            return CreateAnnotatedErrorResult(windowHandle, $"Failed to capture window with elements: {ex.Message}");
        }
    }

    private static Bitmap CreateBitmapFromPixelData(byte[] pixelData, int width, int height)
    {
        var bitmap = new Bitmap(width, height, PixelFormat.Format32bppArgb);

        var bitmapData = bitmap.LockBits(
            new Rectangle(0, 0, width, height),
            ImageLockMode.WriteOnly,
            PixelFormat.Format32bppArgb);

        try
        {
            System.Runtime.InteropServices.Marshal.Copy(pixelData, 0, bitmapData.Scan0, pixelData.Length);
        }
        finally
        {
            bitmap.UnlockBits(bitmapData);
        }

        return bitmap;
    }

    private static void DrawElementOverlays(
        Graphics graphics,
        IReadOnlyList<UIElementInfo> elements,
        (int X, int Y, int Width, int Height) windowBounds,
        int imageWidth,
        int imageHeight,
        bool highlightInteractable,
        List<AnnotatedElement> annotations)
    {
        foreach (var element in elements)
        {
            DrawElementOverlay(graphics, element, windowBounds, imageWidth, imageHeight, highlightInteractable, annotations);
        }
    }

    private static void DrawElementOverlay(
        Graphics graphics,
        UIElementInfo element,
        (int X, int Y, int Width, int Height) windowBounds,
        int imageWidth,
        int imageHeight,
        bool highlightInteractable,
        List<AnnotatedElement> annotations)
    {
        // Calculate image-relative coordinates
        var screenBounds = element.Bounds;
        var imageBounds = new BoundsDto(
            screenBounds.X - windowBounds.X,
            screenBounds.Y - windowBounds.Y,
            screenBounds.Width,
            screenBounds.Height
        );

        // Skip elements outside the image bounds
        if (imageBounds.X + imageBounds.Width <= 0 || imageBounds.X >= imageWidth ||
            imageBounds.Y + imageBounds.Height <= 0 || imageBounds.Y >= imageHeight)
        {
            // Still process children
            if (element.Children != null)
            {
                DrawElementOverlays(graphics, element.Children, windowBounds, imageWidth, imageHeight, highlightInteractable, annotations);
            }
            return;
        }

        // Skip very small or invalid elements
        if (imageBounds.Width < 2 || imageBounds.Height < 2)
        {
            if (element.Children != null)
            {
                DrawElementOverlays(graphics, element.Children, windowBounds, imageWidth, imageHeight, highlightInteractable, annotations);
            }
            return;
        }

        // Determine overlay color based on control type and enabled state
        var (color, colorName) = GetOverlayColor(element.ControlType, element.IsEnabled, highlightInteractable);

        // Draw the overlay rectangle
        using var pen = new Pen(color, 2);
        var rect = new Rectangle(
            Math.Max(0, imageBounds.X),
            Math.Max(0, imageBounds.Y),
            Math.Min(imageBounds.Width, imageWidth - Math.Max(0, imageBounds.X)),
            Math.Min(imageBounds.Height, imageHeight - Math.Max(0, imageBounds.Y))
        );

        if (rect.Width > 0 && rect.Height > 0)
        {
            graphics.DrawRectangle(pen, rect);

            // Add annotation
            annotations.Add(new AnnotatedElement(
                Name: element.Name,
                ControlType: element.ControlType,
                AutomationId: element.AutomationId,
                ImageBounds: new BoundsDto(rect.X, rect.Y, rect.Width, rect.Height),
                ScreenBounds: screenBounds,
                IsEnabled: element.IsEnabled,
                OverlayColor: colorName
            ));
        }

        // Process children
        if (element.Children != null)
        {
            DrawElementOverlays(graphics, element.Children, windowBounds, imageWidth, imageHeight, highlightInteractable, annotations);
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

    private static string EncodeBitmap(Bitmap bitmap, string format, int quality)
    {
        quality = Math.Clamp(quality, 1, 100);

        using var memoryStream = new MemoryStream();

        if (format == "png")
        {
            bitmap.Save(memoryStream, ImageFormat.Png);
        }
        else
        {
            var encoder = ImageCodecInfo.GetImageEncoders()
                .First(c => c.MimeType == "image/jpeg");
            var encoderParams = new EncoderParameters(1);
            encoderParams.Param[0] = new EncoderParameter(Encoder.Quality, quality);
            bitmap.Save(memoryStream, encoder, encoderParams);
        }

        return Convert.ToBase64String(memoryStream.ToArray());
    }

    private static ScreenshotResult CreateErrorResult(string error)
    {
        return new ScreenshotResult(
            Success: false,
            ImageData: null,
            ImageFormat: "png",
            Width: 0,
            Height: 0,
            CapturedRegion: new CapturedRegion(0, 0, 0, 0),
            MonitorIndex: null,
            ScaleFactor: 1.0,
            Error: error
        );
    }

    private static AnnotatedScreenshotResult CreateAnnotatedErrorResult(nint windowHandle, string error)
    {
        return new AnnotatedScreenshotResult(
            Success: false,
            ImageData: null,
            ImageFormat: "png",
            Width: 0,
            Height: 0,
            WindowHandle: (long)windowHandle,
            WindowTitle: null,
            Elements: null,
            ElementCount: 0,
            CaptureElapsedMs: 0,
            AnnotationElapsedMs: 0,
            Error: error
        );
    }
}
