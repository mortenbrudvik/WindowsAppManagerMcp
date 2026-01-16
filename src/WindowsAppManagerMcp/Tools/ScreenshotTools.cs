using System.ComponentModel;
using ModelContextProtocol.Server;
using WindowsAppManagerMcp.Models;
using WindowsAppManagerMcp.Services.Interfaces;

namespace WindowsAppManagerMcp.Tools;

[McpServerToolType]
public class ScreenshotTools
{
    private readonly IScreenshotService _screenshotService;

    public ScreenshotTools(IScreenshotService screenshotService)
    {
        _screenshotService = screenshotService;
    }

    [McpServerTool(Name = "list_screens")]
    [Description("List all available screens/monitors for capture. Returns screen information including bounds, work area, scale factor, and virtual screen bounds (the combined area of all monitors).")]
    public ScreenListResult ListScreens()
    {
        return _screenshotService.ListScreens();
    }

    [McpServerTool(Name = "take_screenshot")]
    [Description("Capture a screenshot of a specific monitor. Returns base64-encoded image data. Use list_screens to get available monitor indices.")]
    public ScreenshotResult TakeScreenshot(
        [Description("Monitor index to capture (0-based). Use list_screens to see available monitors. Defaults to primary monitor.")]
        int? monitorIndex = null,
        [Description("Image format: 'png' (lossless, larger) or 'jpeg' (smaller, lossy). Default: 'png'")]
        string format = "png",
        [Description("JPEG quality (1-100). Only used when format is 'jpeg'. Default: 85")]
        int quality = 85)
    {
        var monitors = _screenshotService.ListScreens();
        var index = monitorIndex ?? monitors.PrimaryIndex;
        return _screenshotService.CaptureMonitor(index, format, quality);
    }

    [McpServerTool(Name = "capture_region")]
    [Description("Capture a screenshot of an arbitrary screen region. Returns base64-encoded image data.")]
    public ScreenshotResult CaptureRegion(
        [Description("X coordinate of the region's left edge")]
        int x,
        [Description("Y coordinate of the region's top edge")]
        int y,
        [Description("Width of the region in pixels")]
        int width,
        [Description("Height of the region in pixels")]
        int height,
        [Description("Image format: 'png' (lossless, larger) or 'jpeg' (smaller, lossy). Default: 'png'")]
        string format = "png",
        [Description("JPEG quality (1-100). Only used when format is 'jpeg'. Default: 85")]
        int quality = 85)
    {
        return _screenshotService.CaptureRegion(x, y, width, height, format, quality);
    }

    [McpServerTool(Name = "capture_window")]
    [Description("Capture a screenshot of a specific window. Works even if the window is partially occluded by other windows. Returns base64-encoded image data.")]
    public ScreenshotResult CaptureWindow(
        [Description("Window handle (as integer) from find_windows or get_all_windows")]
        long handle,
        [Description("Whether to include the window frame/decoration (title bar, borders). Default: true")]
        bool includeFrame = true,
        [Description("Image format: 'png' (lossless, larger) or 'jpeg' (smaller, lossy). Default: 'png'")]
        string format = "png",
        [Description("JPEG quality (1-100). Only used when format is 'jpeg'. Default: 85")]
        int quality = 85)
    {
        return _screenshotService.CaptureWindow((nint)handle, includeFrame, format, quality);
    }
}
