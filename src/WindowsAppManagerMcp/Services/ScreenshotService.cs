using System.Drawing;
using System.Drawing.Imaging;
using WindowsAppManagerMcp.Models;
using WindowsAppManagerMcp.Services.Interfaces;

namespace WindowsAppManagerMcp.Services;

public class ScreenshotService : IScreenshotService
{
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

    public ScreenshotResult CaptureMonitor(int monitorIndex, string format = "png", int quality = 85)
    {
        try
        {
            var monitors = _monitorService.GetAllMonitors();
            if (monitorIndex < 0 || monitorIndex >= monitors.Count)
            {
                return CreateErrorResult($"Monitor index {monitorIndex} is out of range. Available monitors: 0-{monitors.Count - 1}");
            }

            var monitor = monitors[monitorIndex];
            var bounds = monitor.Bounds;

            var normalizedFormat = NormalizeFormat(format);
            var pixelData = _captureWrapper.CaptureScreenRegion(bounds.X, bounds.Y, bounds.Width, bounds.Height);
            var imageData = EncodeImage(pixelData, bounds.Width, bounds.Height, normalizedFormat, quality);

            return new ScreenshotResult(
                Success: true,
                ImageData: imageData,
                ImageFormat: normalizedFormat,
                Width: bounds.Width,
                Height: bounds.Height,
                CapturedRegion: new CapturedRegion(bounds.X, bounds.Y, bounds.Width, bounds.Height),
                MonitorIndex: monitorIndex,
                ScaleFactor: monitor.ScaleFactor
            );
        }
        catch (Exception ex)
        {
            return CreateErrorResult($"Failed to capture monitor: {ex.Message}");
        }
    }

    public ScreenshotResult CaptureRegion(int x, int y, int width, int height, string format = "png", int quality = 85)
    {
        try
        {
            if (width <= 0 || height <= 0)
            {
                return CreateErrorResult("Width and height must be positive values");
            }

            var normalizedFormat = NormalizeFormat(format);
            var pixelData = _captureWrapper.CaptureScreenRegion(x, y, width, height);
            var imageData = EncodeImage(pixelData, width, height, normalizedFormat, quality);

            // Determine which monitor contains the center of the region
            var centerX = x + width / 2;
            var centerY = y + height / 2;
            var monitor = _monitorService.GetMonitorAt(centerX, centerY);

            return new ScreenshotResult(
                Success: true,
                ImageData: imageData,
                ImageFormat: normalizedFormat,
                Width: width,
                Height: height,
                CapturedRegion: new CapturedRegion(x, y, width, height),
                MonitorIndex: monitor?.Index,
                ScaleFactor: monitor?.ScaleFactor ?? 1.0
            );
        }
        catch (Exception ex)
        {
            return CreateErrorResult($"Failed to capture region: {ex.Message}");
        }
    }

    public ScreenshotResult CaptureWindow(nint windowHandle, bool includeFrame = true, string format = "png", int quality = 85)
    {
        try
        {
            if (!_captureWrapper.IsValidWindow(windowHandle))
            {
                return CreateErrorResult("Invalid window handle or window no longer exists");
            }

            var normalizedFormat = NormalizeFormat(format);
            var (pixelData, width, height) = _captureWrapper.CaptureWindow(windowHandle, includeFrame);
            var imageData = EncodeImage(pixelData, width, height, normalizedFormat, quality);

            // Get the monitor for this window
            var monitor = _monitorService.GetMonitorForWindow(windowHandle);

            return new ScreenshotResult(
                Success: true,
                ImageData: imageData,
                ImageFormat: normalizedFormat,
                Width: width,
                Height: height,
                CapturedRegion: new CapturedRegion(0, 0, width, height),
                MonitorIndex: monitor?.Index,
                ScaleFactor: monitor?.ScaleFactor ?? 1.0
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
}
