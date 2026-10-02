using WindowsAppManagerMcp.Models;

namespace WindowsAppManagerMcp.Services.Interfaces;

public interface IScreenshotService
{
    /// <summary>
    /// Lists all available screens/monitors for capture.
    /// </summary>
    ScreenListResult ListScreens();

    /// <summary>
    /// Captures a specific monitor.
    /// </summary>
    /// <param name="monitorIndex">Index of the monitor to capture (0-based)</param>
    /// <param name="format">Image format: "png" or "jpeg"</param>
    /// <param name="quality">JPEG quality (1-100, ignored for PNG)</param>
    /// <param name="saveToFile">When true, writes the output image after any maxWidth resize, sets FilePath, and leaves ImageData null. When false, ImageData is base64 and FilePath is null.</param>
    /// <param name="outputPath">Used only when saveToFile is true. If null, the image is written under the temp directory with a generated name (screenshot_{timestamp}_{guid}.png or .jpg). Ignored when saveToFile is false. Parent directories are created.</param>
    /// <param name="maxWidth">Maximum width in pixels. Images wider than this are scaled down proportionally. Narrower images are unchanged.</param>
    ScreenshotResult CaptureMonitor(int monitorIndex, string format = "png", int quality = 85, bool saveToFile = false, string? outputPath = null, int? maxWidth = null);

    /// <summary>
    /// Captures a region of the screen.
    /// </summary>
    /// <param name="x">X coordinate of the region</param>
    /// <param name="y">Y coordinate of the region</param>
    /// <param name="width">Width of the region</param>
    /// <param name="height">Height of the region</param>
    /// <param name="format">Image format: "png" or "jpeg"</param>
    /// <param name="quality">JPEG quality (1-100, ignored for PNG)</param>
    /// <param name="saveToFile">When true, writes the output image after any maxWidth resize, sets FilePath, and leaves ImageData null. When false, ImageData is base64 and FilePath is null.</param>
    /// <param name="outputPath">Used only when saveToFile is true. If null, the image is written under the temp directory with a generated name (screenshot_{timestamp}_{guid}.png or .jpg). Ignored when saveToFile is false. Parent directories are created.</param>
    /// <param name="maxWidth">Maximum width in pixels. Images wider than this are scaled down proportionally. Narrower images are unchanged.</param>
    ScreenshotResult CaptureRegion(int x, int y, int width, int height, string format = "png", int quality = 85, bool saveToFile = false, string? outputPath = null, int? maxWidth = null);

    /// <summary>
    /// Captures a specific window.
    /// </summary>
    /// <param name="windowHandle">Handle to the window</param>
    /// <param name="includeFrame">Whether to include the window frame/decoration</param>
    /// <param name="format">Image format: "png" or "jpeg"</param>
    /// <param name="quality">JPEG quality (1-100, ignored for PNG)</param>
    /// <param name="saveToFile">When true, writes the output image after any maxWidth resize, sets FilePath, and leaves ImageData null. When false, ImageData is base64 and FilePath is null.</param>
    /// <param name="outputPath">Used only when saveToFile is true. If null, the image is written under the temp directory with a generated name (screenshot_{timestamp}_{guid}.png or .jpg). Ignored when saveToFile is false. Parent directories are created.</param>
    /// <param name="maxWidth">Maximum width in pixels. Images wider than this are scaled down proportionally. Narrower images are unchanged.</param>
    ScreenshotResult CaptureWindow(nint windowHandle, bool includeFrame = true, string format = "png", int quality = 85, bool saveToFile = false, string? outputPath = null, int? maxWidth = null);

    /// <summary>
    /// Captures a window with UI element bounding box overlays drawn on the image.
    /// </summary>
    /// <param name="windowHandle">Handle to the window</param>
    /// <param name="elements">UI elements to draw overlays for</param>
    /// <param name="windowBounds">Window bounds for coordinate translation</param>
    /// <param name="includeFrame">Whether to include the window frame/decoration</param>
    /// <param name="format">Image format: "png" or "jpeg"</param>
    /// <param name="quality">JPEG quality (1-100, ignored for PNG)</param>
    /// <param name="highlightInteractable">Use brighter colors for enabled/interactable elements</param>
    /// <param name="saveToFile">When true, writes the output image after any maxWidth resize, sets FilePath, and leaves ImageData null. When false, ImageData is base64 and FilePath is null.</param>
    /// <param name="outputPath">Used only when saveToFile is true. If null, the image is written under the temp directory with a generated name (screenshot_{timestamp}_{guid}.png or .jpg). Ignored when saveToFile is false. Parent directories are created.</param>
    /// <param name="maxWidth">Maximum width in pixels. Images wider than this are scaled down proportionally. Narrower images are unchanged. ImageBounds are scaled to the output image. ScreenBounds stay in screen pixels.</param>
    AnnotatedScreenshotResult CaptureWindowWithElements(
        nint windowHandle,
        IReadOnlyList<UIElementInfo> elements,
        (int X, int Y, int Width, int Height) windowBounds,
        bool includeFrame = true,
        string format = "png",
        int quality = 85,
        bool highlightInteractable = true,
        bool saveToFile = false,
        string? outputPath = null,
        int? maxWidth = null);
}
