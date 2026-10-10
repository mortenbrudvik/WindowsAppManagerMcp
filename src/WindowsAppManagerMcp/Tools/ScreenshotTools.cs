using System.ComponentModel;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using WindowsAppManagerMcp.Infrastructure;
using WindowsAppManagerMcp.Models;
using WindowsAppManagerMcp.Services;
using WindowsAppManagerMcp.Services.Interfaces;

namespace WindowsAppManagerMcp.Tools;

[McpServerToolType]
public class ScreenshotTools
{
    internal const string ImageResultDescription =
        "Returns the image as image content you can see, followed by JSON metadata (size, capturedRegion, imageToScreen, filePath). " +
        "By default the image is JPEG (PNG when its long edge is at most 512 pixels) scaled to at most 1568 pixels on the long edge. ";

    internal const string ImageToScreenDescription =
        "Image pixels are not screen pixels: to click a point (imageX, imageY) seen in the image, use " +
        "screenX = imageToScreen.offsetX + imageX * imageToScreen.scaleX and screenY = imageToScreen.offsetY + imageY * imageToScreen.scaleY. " +
        "scaleFactor is the monitor's DPI scale, for information only; never multiply by it.";

    internal const string FormatDescription =
        "Image format: 'auto' (default: JPEG, or PNG when the image's long edge is at most 512 pixels), 'png' (lossless, larger) or 'jpeg'. " +
        "With outputPath, 'auto' follows the file extension. Any other value is rejected with InvalidFormat.";

    internal const string QualityDescription = "JPEG quality (1-100). Only used for JPEG. Default: 80";

    internal const string MaxDimensionDescription =
        "Longest edge of the image in pixels; larger captures are scaled down proportionally. " +
        "Default (null): 1568, the size vision models work at. 0 keeps full resolution. imageToScreen reports the resulting scale.";

    internal const string OutputDescription =
        "What to return: 'image' (default: the image and the metadata), 'file' (write the image to a file and return only the metadata with filePath), " +
        "'both' (image, file and metadata) or 'metadata' (only the metadata, no image).";

    internal const string OutputPathDescription =
        "Absolute path of the image file for output 'file' or 'both', ending in .png, .jpg or .jpeg to match the format. " +
        "The folder must exist; it is not created. Default: a new file in %TEMP%\\WindowsAppManagerMcp\\captures.";

    internal const string OverwriteDescription =
        "Allow outputPath to replace an existing file. Default: false, which reports an existing file as FileExists.";

    internal const string SaveToFileDescription =
        "Deprecated alias for output 'file': true writes the image to a file and returns only the metadata. Default: false";

    internal const string MaxWidthDescription =
        "Deprecated: maximum image width in pixels. Given without maxDimension it replaces the 1568 default and limits only the width; " +
        "with maxDimension both limits apply.";

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
    [Description("Capture a specific monitor. " + ImageResultDescription + ImageToScreenDescription +
                 " Use list_screens to get available monitor indices.")]
    [ToolResultPayload(typeof(ScreenshotResult))]
    public CallToolResult TakeScreenshot(
        [Description("Monitor index to capture (0-based). Use list_screens to see available monitors. Defaults to primary monitor.")]
        int? monitorIndex = null,
        [Description(FormatDescription)]
        string format = "auto",
        [Description(QualityDescription)]
        int quality = CaptureOptions.DefaultQuality,
        [Description(MaxDimensionDescription)]
        int? maxDimension = null,
        [Description(OutputDescription)]
        string output = "image",
        [Description(OutputPathDescription)]
        string? outputPath = null,
        [Description(OverwriteDescription)]
        bool overwrite = false,
        [Description(SaveToFileDescription)]
        bool saveToFile = false,
        [Description(MaxWidthDescription)]
        int? maxWidth = null)
    {
        var options = Options(format, quality, maxDimension, output, outputPath, overwrite, saveToFile, maxWidth);
        var index = monitorIndex ?? _screenshotService.ListScreens().PrimaryIndex;
        return ToolImages.ToCallToolResult(_screenshotService.CaptureMonitor(index, options));
    }

    [McpServerTool(Name = "capture_region")]
    [Description("Capture an arbitrary screen region given in screen coordinates. " + ImageResultDescription + ImageToScreenDescription)]
    [ToolResultPayload(typeof(ScreenshotResult))]
    public CallToolResult CaptureRegion(
        [Description("X coordinate of the region's left edge")]
        int x,
        [Description("Y coordinate of the region's top edge")]
        int y,
        [Description("Width of the region in pixels")]
        int width,
        [Description("Height of the region in pixels")]
        int height,
        [Description(FormatDescription)]
        string format = "auto",
        [Description(QualityDescription)]
        int quality = CaptureOptions.DefaultQuality,
        [Description(MaxDimensionDescription)]
        int? maxDimension = null,
        [Description(OutputDescription)]
        string output = "image",
        [Description(OutputPathDescription)]
        string? outputPath = null,
        [Description(OverwriteDescription)]
        bool overwrite = false,
        [Description(SaveToFileDescription)]
        bool saveToFile = false,
        [Description(MaxWidthDescription)]
        int? maxWidth = null)
    {
        var options = Options(format, quality, maxDimension, output, outputPath, overwrite, saveToFile, maxWidth);
        return ToolImages.ToCallToolResult(_screenshotService.CaptureRegion(x, y, width, height, options));
    }

    [McpServerTool(Name = "capture_window")]
    [Description("Capture a specific window. Works even if the window is partially occluded by other windows, but not when it is minimized. " +
                 ImageResultDescription +
                 "capturedRegion and imageToScreen use the window's real screen position: the window's corner, or the client area's corner when includeFrame is false. " +
                 ImageToScreenDescription)]
    [ToolResultPayload(typeof(ScreenshotResult))]
    public CallToolResult CaptureWindow(
        [Description("Window handle (as integer) from find_windows or get_all_windows")]
        long handle,
        [Description("Whether to include the window frame/decoration (title bar, borders). Default: true")]
        bool includeFrame = true,
        [Description(FormatDescription)]
        string format = "auto",
        [Description(QualityDescription)]
        int quality = CaptureOptions.DefaultQuality,
        [Description(MaxDimensionDescription)]
        int? maxDimension = null,
        [Description(OutputDescription)]
        string output = "image",
        [Description(OutputPathDescription)]
        string? outputPath = null,
        [Description(OverwriteDescription)]
        bool overwrite = false,
        [Description(SaveToFileDescription)]
        bool saveToFile = false,
        [Description(MaxWidthDescription)]
        int? maxWidth = null)
    {
        var options = Options(format, quality, maxDimension, output, outputPath, overwrite, saveToFile, maxWidth);
        return ToolImages.ToCallToolResult(_screenshotService.CaptureWindow((nint)handle, includeFrame, options));
    }

    [McpServerTool(Name = "capture_with_elements")]
    [Description("Capture a window with UI element bounding boxes drawn on it. " + ImageResultDescription +
                 "The metadata lists each element with imageBounds (pixels of the returned image) and screenBounds (screen pixels: use these to click). " +
                 "Elements are color-coded by type: green=buttons, blue=text inputs, orange=checkboxes, purple=lists, cyan=links, red=menus. " +
                 "The overlays line up with or without the frame. " + ImageToScreenDescription)]
    [ToolResultPayload(typeof(AnnotatedScreenshotResult))]
    public CallToolResult CaptureWithElements(
        [Description("Window handle (as integer) from find_windows or get_all_windows")]
        long handle,
        [Description("Whether to include the window frame/decoration. Default: true")]
        bool includeFrame = true,
        [Description(FormatDescription)]
        string format = "auto",
        [Description(QualityDescription)]
        int quality = CaptureOptions.DefaultQuality,
        [Description("Max depth to traverse UI tree (1-10). Default: 5")]
        int maxDepth = 5,
        [Description(UIElementTools.ControlTypesDescription)]
        string? controlTypes = null,
        [Description("Only include enabled elements. Default: false. " + UIElementTools.InteractableOnlyCaution)]
        bool interactableOnly = false,
        [Description("Only include elements that are not offscreen. Default: true. " + UIElementTools.VisibleOnlyCaution)]
        bool visibleOnly = true,
        [Description("Use brighter colors for enabled elements. Default: true")]
        bool highlightInteractable = true,
        [Description("Timeout for UI element retrieval in ms. Default: 5000")]
        int timeoutMs = 5000,
        [Description(MaxDimensionDescription + " imageBounds are scaled with the image; screenBounds stay in screen pixels.")]
        int? maxDimension = null,
        [Description(OutputDescription)]
        string output = "image",
        [Description(OutputPathDescription)]
        string? outputPath = null,
        [Description(OverwriteDescription)]
        bool overwrite = false,
        [Description(SaveToFileDescription)]
        bool saveToFile = false,
        [Description(MaxWidthDescription)]
        int? maxWidth = null)
    {
        var options = Options(format, quality, maxDimension, output, outputPath, overwrite, saveToFile, maxWidth);
        var windowHandle = (nint)handle;

        // Reject bad options and dead windows before the UI Automation walk, which can take seconds.
        if (CaptureRequest.Validate(options) is { } invalid)
        {
            return AnnotatedFailure(handle, null, invalid.Code.ToString(), invalid.Message);
        }

        if (!_windowService.IsValidWindow(windowHandle))
        {
            return AnnotatedFailure(handle, null, ScreenshotErrorCode.InvalidHandle.ToString(),
                "Window not found or handle is invalid");
        }

        // The title is informational: untitled windows (popups, menus) are still captured.
        var windowTitle = _windowService.GetWindowInfo(windowHandle)?.Title;

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
            // Pass the walk's own code on (for example OperationTimedOut), so the agent knows what to change.
            var code = elementsResult.ErrorCode == UIElementErrorCode.None
                ? ScreenshotErrorCode.CaptureFailed.ToString()
                : elementsResult.ErrorCode.ToString();
            return AnnotatedFailure(handle, windowTitle, code, $"Failed to get UI elements: {elementsResult.Error}");
        }

        var capture = _screenshotService.CaptureWindowWithElements(
            windowHandle,
            windowTitle,
            elementsResult.Elements,
            includeFrame,
            highlightInteractable,
            options);

        return ToolImages.ToCallToolResult(capture);
    }

    private static CaptureOptions Options(
        string? format,
        int quality,
        int? maxDimension,
        string? output,
        string? outputPath,
        bool overwrite,
        bool saveToFile,
        int? maxWidth) =>
        new()
        {
            Format = format,
            Quality = quality,
            MaxDimension = maxDimension,
            MaxWidth = maxWidth,
            Output = output,
            OutputPath = outputPath,
            Overwrite = overwrite,
            SaveToFile = saveToFile
        };

    private static CallToolResult AnnotatedFailure(long handle, string? windowTitle, string errorCode, string error) =>
        ToolImages.Create(
            new AnnotatedScreenshotResult(
                Success: false,
                ImageFormat: null,
                MimeType: null,
                Width: 0,
                Height: 0,
                WindowHandle: handle,
                WindowTitle: windowTitle,
                CapturedRegion: null,
                ImageToScreen: null,
                Elements: null,
                ElementCount: 0,
                CaptureElapsedMs: 0,
                AnnotationElapsedMs: 0,
                Error: error,
                ErrorCode: errorCode),
            image: null,
            isError: true);
}
