using System.ComponentModel;
using ModelContextProtocol;
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
        "Image format: 'auto' (default: JPEG, or PNG when the image's long edge is at most 512 pixels), 'png' (lossless, larger) or 'jpeg' ('jpg' is accepted too). " +
        "With outputPath, 'auto' follows the file extension. Any other value is rejected with InvalidFormat.";

    internal const string QualityDescription = "JPEG quality (1-100). Only used for JPEG. Default: 80";

    internal const string MaxDimensionDescription =
        "Longest edge of the image in pixels; larger captures are scaled down proportionally. " +
        "Default (null): 1568, the size vision models work at. 0 keeps full resolution. imageToScreen reports the resulting scale.";

    internal const string OutputDescription =
        "What to return: 'image' (default: the image and the metadata), 'file' (write the image to a file and return only the metadata with filePath), " +
        "'both' (image, file and metadata) or 'metadata' (only the metadata, no image). Any other value is rejected with InvalidOutputMode.";

    internal const string OutputPathDescription =
        "Absolute path on a drive letter (no network or device paths) of the image file for output 'file' or 'both', ending in .png, .jpg or .jpeg to match the format. " +
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
    private readonly ITargetPolicy _targetPolicy;

    public ScreenshotTools(
        IScreenshotService screenshotService,
        IUIElementService uiElementService,
        IWindowService windowService,
        ITargetPolicy targetPolicy)
    {
        _screenshotService = screenshotService;
        _uiElementService = uiElementService;
        _windowService = windowService;
        _targetPolicy = targetPolicy ?? throw new ArgumentNullException(nameof(targetPolicy));
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
        CaptureFormat format = CaptureFormat.Auto,
        [Description(QualityDescription)]
        int quality = CaptureOptions.DefaultQuality,
        [Description(MaxDimensionDescription)]
        int? maxDimension = null,
        [Description(OutputDescription)]
        CaptureOutputMode output = CaptureOutputMode.Image,
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
        CaptureFormat format = CaptureFormat.Auto,
        [Description(QualityDescription)]
        int quality = CaptureOptions.DefaultQuality,
        [Description(MaxDimensionDescription)]
        int? maxDimension = null,
        [Description(OutputDescription)]
        CaptureOutputMode output = CaptureOutputMode.Image,
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
    [Description("Capture a specific window. Works even if the window is partially occluded by other windows, but not when it is minimized (CaptureFailed). " +
                 "A window of an app the user's policy denies is refused (TargetDeniedByPolicy). " +
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
        CaptureFormat format = CaptureFormat.Auto,
        [Description(QualityDescription)]
        int quality = CaptureOptions.DefaultQuality,
        [Description(MaxDimensionDescription)]
        int? maxDimension = null,
        [Description(OutputDescription)]
        CaptureOutputMode output = CaptureOutputMode.Image,
        [Description(OutputPathDescription)]
        string? outputPath = null,
        [Description(OverwriteDescription)]
        bool overwrite = false,
        [Description(SaveToFileDescription)]
        bool saveToFile = false,
        [Description(MaxWidthDescription)]
        int? maxWidth = null)
    {
        if (PolicyRefusal((nint)handle) is { } refusal)
        {
            return ToolImages.Create(
                new ScreenshotResult(false, null, null, 0, 0, null, null, null, null,
                    Error: refusal, ErrorCode: nameof(ScreenshotErrorCode.TargetDeniedByPolicy)),
                image: null,
                isError: true);
        }

        var options = Options(format, quality, maxDimension, output, outputPath, overwrite, saveToFile, maxWidth);
        return ToolImages.ToCallToolResult(_screenshotService.CaptureWindow((nint)handle, includeFrame, options));
    }

    [McpServerTool(Name = "capture_with_elements")]
    [Description("Capture a window with UI element bounding boxes drawn on it. " + ImageResultDescription +
                 "The metadata lists each element with imageBounds (pixels of the returned image) and screenBounds (screen pixels: use these to click). " +
                 "Elements are color-coded by type: green=buttons, blue=text inputs, orange=checkboxes, purple=lists, cyan=links, red=menus. " +
                 "The overlays line up with or without the frame. A minimized window is refused (CaptureFailed), and so is a window of an app the user's policy denies (TargetDeniedByPolicy). " +
                 ImageToScreenDescription)]
    [ToolResultPayload(typeof(AnnotatedScreenshotResult))]
    public async Task<CallToolResult> CaptureWithElements(
        [Description("Window handle (as integer) from find_windows or get_all_windows")]
        long handle,
        [Description("Whether to include the window frame/decoration. Default: true")]
        bool includeFrame = true,
        [Description(FormatDescription)]
        CaptureFormat format = CaptureFormat.Auto,
        [Description(QualityDescription)]
        int quality = CaptureOptions.DefaultQuality,
        [Description("How many levels below the window to read (1-10). 1 reads only the window's direct children. Default: 5")]
        int maxDepth = 5,
        [Description(UIElementTools.ControlTypesDescription)]
        string? controlTypes = null,
        [Description("Only include interactable elements. Default: false. " + UIElementTools.InteractableOnlyCaution)]
        bool interactableOnly = false,
        [Description("Only include elements that are not offscreen. Default: true. " + UIElementTools.VisibleOnlyCaution)]
        bool visibleOnly = true,
        [Description("Use brighter colors for enabled elements. Default: true")]
        bool highlightInteractable = true,
        [Description("Timeout for UI element retrieval in ms (1000-30000). When it passes, the elements found so far are drawn; with none found, the call fails with OperationTimedOut. Default: 5000")]
        int timeoutMs = 5000,
        [Description(MaxDimensionDescription + " imageBounds are scaled with the image; screenBounds stay in screen pixels.")]
        int? maxDimension = null,
        [Description(OutputDescription)]
        CaptureOutputMode output = CaptureOutputMode.Image,
        [Description(OutputPathDescription)]
        string? outputPath = null,
        [Description(OverwriteDescription)]
        bool overwrite = false,
        [Description(SaveToFileDescription)]
        bool saveToFile = false,
        [Description(MaxWidthDescription)]
        int? maxWidth = null,
        IProgress<ProgressNotificationValue>? progress = null,
        CancellationToken cancellationToken = default)
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
        var windowInfo = _windowService.GetWindowInfo(windowHandle);
        var windowTitle = windowInfo?.Title;

        // Neither the pixels nor the element texts of a window the user's policy denies
        if (windowInfo != null && _targetPolicy.Evaluate(windowInfo) is { Allowed: false } verdict)
        {
            return AnnotatedFailure(handle, windowTitle, nameof(ScreenshotErrorCode.TargetDeniedByPolicy), PolicyMessage(verdict));
        }

        // A minimized window cannot be captured, and its elements are offscreen: refuse before the walk
        if (windowInfo?.State == WindowState.Minimized)
        {
            return AnnotatedFailure(handle, windowTitle, nameof(ScreenshotErrorCode.CaptureFailed), ScreenshotService.MinimizedMessage);
        }

        // The walk stops at timeoutMs with what it has, or when the client cancels the call (UIElementTools.ReadTreeAsync)
        var filter = new UIElementFilter(
            ControlTypes: UIElementTools.ParseControlTypes(controlTypes),
            InteractableOnly: interactableOnly,
            VisibleOnly: visibleOnly);
        var elementsResult = await UIElementTools.ReadTreeAsync(
            _uiElementService, windowHandle, maxDepth, filter, timeoutMs, progress, cancellationToken).ConfigureAwait(false);

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

    /// <summary>
    /// Why the target policy refuses capturing the window, or null. A window that cannot be read is left to
    /// the capture, which reports InvalidHandle.
    /// </summary>
    private string? PolicyRefusal(nint handle) =>
        _windowService.GetWindowInfo(handle) is { } window && _targetPolicy.Evaluate(window) is { Allowed: false } verdict
            ? PolicyMessage(verdict)
            : null;

    private static string PolicyMessage(TargetPolicyVerdict verdict) =>
        $"The window is refused by the server's target policy: {verdict.Reason}. Nothing was captured. The policy is set by " +
        "the user in the server's environment; no tool argument changes it.";

    private static CaptureOptions Options(
        CaptureFormat format,
        int quality,
        int? maxDimension,
        CaptureOutputMode output,
        string? outputPath,
        bool overwrite,
        bool saveToFile,
        int? maxWidth) =>
        new()
        {
            // The service takes the names; the enums were checked when the arguments were bound
            Format = ToolEnum<CaptureFormat>.Name(format),
            Quality = quality,
            MaxDimension = maxDimension,
            MaxWidth = maxWidth,
            Output = ToolEnum<CaptureOutputMode>.Name(output),
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
