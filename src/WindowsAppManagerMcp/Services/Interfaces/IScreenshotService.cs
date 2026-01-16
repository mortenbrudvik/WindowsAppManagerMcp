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
    ScreenshotResult CaptureMonitor(int monitorIndex, string format = "png", int quality = 85);

    /// <summary>
    /// Captures a region of the screen.
    /// </summary>
    /// <param name="x">X coordinate of the region</param>
    /// <param name="y">Y coordinate of the region</param>
    /// <param name="width">Width of the region</param>
    /// <param name="height">Height of the region</param>
    /// <param name="format">Image format: "png" or "jpeg"</param>
    /// <param name="quality">JPEG quality (1-100, ignored for PNG)</param>
    ScreenshotResult CaptureRegion(int x, int y, int width, int height, string format = "png", int quality = 85);

    /// <summary>
    /// Captures a specific window.
    /// </summary>
    /// <param name="windowHandle">Handle to the window</param>
    /// <param name="includeFrame">Whether to include the window frame/decoration</param>
    /// <param name="format">Image format: "png" or "jpeg"</param>
    /// <param name="quality">JPEG quality (1-100, ignored for PNG)</param>
    ScreenshotResult CaptureWindow(nint windowHandle, bool includeFrame = true, string format = "png", int quality = 85);
}
