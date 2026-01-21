using System.ComponentModel;
using ModelContextProtocol.Server;
using WindowsAppManagerMcp.Models;
using WindowsAppManagerMcp.Services.Interfaces;

namespace WindowsAppManagerMcp.Tools;

[McpServerToolType]
public class ScreenshotTools
{
    private readonly IScreenshotService _screenshotService;
    private readonly IUIElementService _uiElementService;
    private readonly IWindowService _windowService;

    public ScreenshotTools(
        IScreenshotService screenshotService,
        IUIElementService uiElementService,
        IWindowService windowService)
    {
        _screenshotService = screenshotService;
        _uiElementService = uiElementService;
        _windowService = windowService;
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
        int quality = 85,
        [Description("Save to file instead of returning base64 (reduces token usage for large screenshots). Default: false")]
        bool saveToFile = false,
        [Description("Output file path when saveToFile is true. If not specified, saves to temp directory with auto-generated name.")]
        string? outputPath = null,
        [Description("Maximum image width in pixels. Images wider than this are scaled down proportionally. Combine with format='jpeg' for smallest files.")]
        int? maxWidth = null)
    {
        var monitors = _screenshotService.ListScreens();
        var index = monitorIndex ?? monitors.PrimaryIndex;
        return _screenshotService.CaptureMonitor(index, format, quality, saveToFile, outputPath, maxWidth);
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
        int quality = 85,
        [Description("Save to file instead of returning base64 (reduces token usage for large screenshots). Default: false")]
        bool saveToFile = false,
        [Description("Output file path when saveToFile is true. If not specified, saves to temp directory with auto-generated name.")]
        string? outputPath = null,
        [Description("Maximum image width in pixels. Images wider than this are scaled down proportionally. Combine with format='jpeg' for smallest files.")]
        int? maxWidth = null)
    {
        return _screenshotService.CaptureRegion(x, y, width, height, format, quality, saveToFile, outputPath, maxWidth);
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
        int quality = 85,
        [Description("Save to file instead of returning base64 (reduces token usage for large screenshots). Default: false")]
        bool saveToFile = false,
        [Description("Output file path when saveToFile is true. If not specified, saves to temp directory with auto-generated name.")]
        string? outputPath = null,
        [Description("Maximum image width in pixels. Images wider than this are scaled down proportionally. Combine with format='jpeg' for smallest files.")]
        int? maxWidth = null)
    {
        return _screenshotService.CaptureWindow((nint)handle, includeFrame, format, quality, saveToFile, outputPath, maxWidth);
    }

    [McpServerTool(Name = "capture_with_elements")]
    [Description("Capture a window screenshot with UI element bounding box overlays. Returns annotated image with element coordinates for click targeting. Elements are color-coded by type: green=buttons, blue=text inputs, orange=checkboxes, purple=lists, cyan=links, red=menus.")]
    public AnnotatedScreenshotResult CaptureWithElements(
        [Description("Window handle (as integer) from find_windows or get_all_windows")]
        long handle,
        [Description("Whether to include the window frame/decoration. Default: true")]
        bool includeFrame = true,
        [Description("Image format: 'png' or 'jpeg'. Default: 'png'")]
        string format = "png",
        [Description("JPEG quality (1-100). Default: 85")]
        int quality = 85,
        [Description("Max depth to traverse UI tree (1-10). Default: 5")]
        int maxDepth = 5,
        [Description("Comma-separated control types to include (e.g., 'Button,Edit'). Empty for all.")]
        string? controlTypes = null,
        [Description("Only include enabled/interactable elements. Default: false")]
        bool interactableOnly = false,
        [Description("Only include visible elements. Default: true")]
        bool visibleOnly = true,
        [Description("Use brighter colors for enabled elements. Default: true")]
        bool highlightInteractable = true,
        [Description("Timeout for UI element retrieval in ms. Default: 5000")]
        int timeoutMs = 5000,
        [Description("Save to file instead of returning base64 (reduces token usage for large screenshots). Default: false")]
        bool saveToFile = false,
        [Description("Output file path when saveToFile is true. If not specified, saves to temp directory with auto-generated name.")]
        string? outputPath = null,
        [Description("Maximum image width in pixels. Images wider than this are scaled down proportionally. Element coordinates in result are scaled to match. Combine with format='jpeg' for smallest files.")]
        int? maxWidth = null)
    {
        var windowHandle = (nint)handle;

        // Get window info for bounds and title
        var windowInfo = _windowService.GetWindowInfo(windowHandle);
        if (windowInfo == null)
        {
            return new AnnotatedScreenshotResult(
                Success: false,
                ImageData: null,
                ImageFormat: "png",
                Width: 0,
                Height: 0,
                WindowHandle: handle,
                WindowTitle: null,
                Elements: null,
                ElementCount: 0,
                CaptureElapsedMs: 0,
                AnnotationElapsedMs: 0,
                Error: "Window not found or handle is invalid"
            );
        }

        // Parse filter parameters
        string[]? controlTypeArray = null;
        if (!string.IsNullOrWhiteSpace(controlTypes))
        {
            controlTypeArray = controlTypes
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Where(s => !string.IsNullOrEmpty(s))
                .ToArray();

            if (controlTypeArray.Length == 0)
            {
                controlTypeArray = null;
            }
        }

        // Get UI elements
        var filter = new UIElementFilter(
            ControlTypes: controlTypeArray,
            InteractableOnly: interactableOnly,
            VisibleOnly: visibleOnly
        );

        using var cts = new CancellationTokenSource(Math.Clamp(timeoutMs, 1000, 30000));
        var elementsResult = _uiElementService.GetUIElements(
            windowHandle,
            Math.Clamp(maxDepth, 1, 10),
            filter,
            cts.Token);

        if (!elementsResult.Success || elementsResult.Elements == null)
        {
            return new AnnotatedScreenshotResult(
                Success: false,
                ImageData: null,
                ImageFormat: "png",
                Width: 0,
                Height: 0,
                WindowHandle: handle,
                WindowTitle: windowInfo.Title,
                Elements: null,
                ElementCount: 0,
                CaptureElapsedMs: 0,
                AnnotationElapsedMs: elementsResult.ElapsedMilliseconds,
                Error: $"Failed to get UI elements: {elementsResult.Error}"
            );
        }

        // Capture with elements
        var windowBounds = (
            windowInfo.Bounds.X,
            windowInfo.Bounds.Y,
            windowInfo.Bounds.Width,
            windowInfo.Bounds.Height
        );

        var result = _screenshotService.CaptureWindowWithElements(
            windowHandle,
            elementsResult.Elements,
            windowBounds,
            includeFrame,
            format,
            quality,
            highlightInteractable,
            saveToFile,
            outputPath,
            maxWidth);

        // Add window title to result
        return result with { WindowTitle = windowInfo.Title };
    }
}
