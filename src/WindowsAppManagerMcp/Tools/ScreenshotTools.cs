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
        "Returns the image as image content you can see, followed by JSON metadata (size, capturedRegion, imageToScreen, filePath, captureId). " +
        "By default the image is JPEG (PNG when its long edge is at most 512 pixels) scaled to at most 1568 pixels on the long edge. ";

    internal const string ImageToScreenDescription =
        "Image pixels are not screen pixels: to click a point (imageX, imageY) seen in the image, pass it to the mouse tools with " +
        "this captureId (valid 10 minutes; refused with StaleCapture once a captured window moved), or convert it yourself: " +
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

    internal const string WarningsDescription =
        "A successful capture can carry warnings, each starting with a code: ImageBlank (one solid color), " +
        "WindowNotResponding (copied from the screen, covering windows show), DpiScaledWindow, RegionClipped and others. ";

    internal const string RestoreIfMinimizedDescription =
        "Restore a minimized window (or the minimized window it is inside) for the capture without activating it, wait " +
        "until it has painted, capture, then minimize it again without activating another window. Default: false, which " +
        "refuses a minimized window with WindowMinimized. While the window is restored the call holds the input gate, so " +
        "it fails with PausedByUser while input is paused, and it is refused with TargetDeniedByPolicy when the policy " +
        "denies a window the restore would show. A hidden or cloaked window is not restored. A window that was maximized " +
        "before it was minimized is captured at its normal size and may restore un-maximized afterwards.";

    private readonly IScreenshotService _screenshotService;
    private readonly IUIElementService _uiElementService;
    private readonly IWindowService _windowService;
    private readonly ITargetPolicy _targetPolicy;
    private readonly IInputSession? _session;

    /// <param name="screenshotService">The captures.</param>
    /// <param name="uiElementService">The UI Automation walk of capture_with_elements.</param>
    /// <param name="windowService">Window reads and the target policy of windows a restore would show.</param>
    /// <param name="targetPolicy">The user's target policy.</param>
    /// <param name="session">
    /// The input session: restoreIfMinimized takes the input gate through it, and refuses while input is paused.
    /// </param>
    public ScreenshotTools(
        IScreenshotService screenshotService,
        IUIElementService uiElementService,
        IWindowService windowService,
        ITargetPolicy targetPolicy,
        IInputSession? session = null)
    {
        _screenshotService = screenshotService;
        _uiElementService = uiElementService;
        _windowService = windowService;
        _targetPolicy = targetPolicy ?? throw new ArgumentNullException(nameof(targetPolicy));
        _session = session;
    }

    [McpServerTool(Name = "list_screens", Title = "List Screens", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("List all available screens/monitors for capture. Returns screen information including bounds, work area, scale factor, and virtual screen bounds (the combined area of all monitors).")]
    public ScreenListResult ListScreens()
    {
        return _screenshotService.ListScreens();
    }

    [McpServerTool(Name = "take_screenshot", Title = "Capture Monitor", ReadOnly = false, Destructive = true, Idempotent = false, OpenWorld = false)]
    [Description("Capture a specific monitor, or with monitorIndex -1 every monitor in one image (the virtual screen, at most 64 megapixels). " +
                 ImageResultDescription + ImageToScreenDescription + " Use list_screens to get available monitor indices.")]
    [ToolResultPayload(typeof(ScreenshotResult))]
    public CallToolResult TakeScreenshot(
        [Description("Monitor index to capture (0-based), or -1 for the whole virtual screen (all monitors; areas between monitors of different sizes are black). Use list_screens to see available monitors. Defaults to primary monitor.")]
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

    [McpServerTool(Name = "capture_region", Title = "Capture Screen Region", ReadOnly = false, Destructive = true, Idempotent = false, OpenWorld = false)]
    [Description("Capture an arbitrary screen region given in screen coordinates. Only the part on the screen is captured: " +
                 "a region partly off the screen is cut to it (warning RegionClipped, see capturedRegion), and a region " +
                 "entirely off it fails with RegionOffScreen. " + ImageResultDescription + ImageToScreenDescription)]
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

    [McpServerTool(Name = "capture_window", Title = "Capture Window", ReadOnly = false, Destructive = true, Idempotent = false, OpenWorld = false)]
    [Description("Capture a specific window, even when other windows cover it. A minimized window fails with WindowMinimized " +
                 "(or pass restoreIfMinimized), a hidden one with WindowHidden, one on another virtual desktop with Cloaked, " +
                 "and a window of an app the user's policy denies with TargetDeniedByPolicy. A window that is not responding " +
                 "is copied from the screen (captureMethod ScreenBitBlt, warning WindowNotResponding). " +
                 ImageResultDescription + WarningsDescription +
                 "capturedRegion and imageToScreen use the window's real screen position: the visible frame's corner, or the client area's corner when includeFrame is false. " +
                 ImageToScreenDescription)]
    [ToolResultPayload(typeof(ScreenshotResult))]
    public async Task<CallToolResult> CaptureWindow(
        [Description("Window handle (as integer) from find_windows or get_all_windows. Pass handle or window, not both.")]
        long? handle = null,
        [Description(WindowTargeting.WindowDescription)]
        WindowSelector? window = null,
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
        int? maxWidth = null,
        [Description(RestoreIfMinimizedDescription)]
        bool restoreIfMinimized = false,
        CancellationToken cancellationToken = default)
    {
        var windowHandle = WindowTargeting.Require(_windowService, handle, window);
        if (PolicyRefusal(windowHandle) is { } refusal)
        {
            return WindowFailure(nameof(ScreenshotErrorCode.TargetDeniedByPolicy), refusal);
        }

        var options = Options(format, quality, maxDimension, output, outputPath, overwrite, saveToFile, maxWidth);
        var restore = await RestoreIfMinimizedAsync(windowHandle, includeFrame, restoreIfMinimized, options, cancellationToken)
            .ConfigureAwait(false);
        if (restore.Refusal is { } refused)
        {
            return WindowFailure(refused.Code.ToString(), refused.Message);
        }

        ScreenCapture<ScreenshotResult> capture;
        using (restore)
        {
            capture = _screenshotService.CaptureWindow(windowHandle, includeFrame, options);
        }

        // Read after the window was minimized again: a failed minimize adds a warning
        if (capture.Metadata.Success && restore.Warnings.Count > 0)
        {
            capture = capture with { Metadata = capture.Metadata with { Warnings = Merge(restore.Warnings, capture.Metadata.Warnings) } };
        }

        return ToolImages.ToCallToolResult(capture);
    }

    [McpServerTool(Name = "capture_with_elements", Title = "Capture Window with Element Boxes", ReadOnly = false, Destructive = true, Idempotent = false, OpenWorld = false)]
    [Description("Capture a window with UI element bounding boxes drawn on it. " + ImageResultDescription +
                 "The metadata lists each element with imageBounds (pixels of the returned image) and screenBounds (screen pixels: use these to click). " +
                 "Elements are color-coded by type: green=buttons, blue=text inputs, orange=checkboxes, purple=lists, cyan=links, red=menus. " +
                 "The overlays line up with or without the frame. The image is returned even when the UI Automation walk fails or times out: " +
                 "then elementsError and elementsErrorCode say why, and truncated is true whenever the element list is incomplete. " +
                 "A window is refused as in capture_window (WindowMinimized unless restoreIfMinimized, WindowHidden, Cloaked, TargetDeniedByPolicy). " +
                 WarningsDescription + ImageToScreenDescription)]
    [ToolResultPayload(typeof(AnnotatedScreenshotResult))]
    public async Task<CallToolResult> CaptureWithElements(
        [Description("Window handle (as integer) from find_windows or get_all_windows. Pass handle or window, not both.")]
        long? handle = null,
        [Description(WindowTargeting.WindowDescription)]
        WindowSelector? window = null,
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
        [Description("Timeout for UI element retrieval in ms (1000-30000). When it passes, the elements found so far are drawn (truncated: true); with none found, the image comes without elements and elementsErrorCode OperationTimedOut. Default: 5000")]
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
        [Description(RestoreIfMinimizedDescription)]
        bool restoreIfMinimized = false,
        IProgress<ProgressNotificationValue>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var options = Options(format, quality, maxDimension, output, outputPath, overwrite, saveToFile, maxWidth);
        var windowHandle = WindowTargeting.Require(_windowService, handle, window);
        var reported = windowHandle.ToInt64();

        // Reject bad options and dead windows before the UI Automation walk, which can take seconds.
        if (CaptureRequest.Validate(options) is { } invalid)
        {
            return AnnotatedFailure(reported, null, invalid.Code.ToString(), invalid.Message);
        }

        // IsWindow only: untitled windows (popups, menus) are captured too
        if (!_windowService.IsValidWindow(windowHandle))
        {
            return AnnotatedFailure(reported, null, ScreenshotErrorCode.InvalidHandle.ToString(),
                "Window not found or handle is invalid");
        }

        // The title is informational: untitled windows (popups, menus) are still captured.
        var windowInfo = _windowService.GetWindowInfo(windowHandle);
        var windowTitle = windowInfo?.Title;

        // Neither the pixels nor the element texts of a window the user's policy denies
        if (windowInfo != null && _targetPolicy.Evaluate(windowInfo) is { Allowed: false } verdict)
        {
            return AnnotatedFailure(reported, windowTitle, nameof(ScreenshotErrorCode.TargetDeniedByPolicy), PolicyMessage(verdict));
        }

        var restore = await RestoreIfMinimizedAsync(windowHandle, includeFrame, restoreIfMinimized, options, cancellationToken)
            .ConfigureAwait(false);
        if (restore.Refusal is { } refused)
        {
            return AnnotatedFailure(reported, windowTitle, refused.Code.ToString(), refused.Message);
        }

        ScreenCapture<AnnotatedScreenshotResult> capture;
        using (restore)
        {
            // A minimized, hidden or cloaked window shows nothing and its elements are offscreen: refuse before the walk
            if (_screenshotService.CheckWindow(windowHandle) is { } unavailable)
            {
                return AnnotatedFailure(reported, windowTitle, unavailable.Code.ToString(), unavailable.Message);
            }

            // The walk stops at timeoutMs with what it has, or when the client cancels the call (UIElementTools.ReadTreeAsync)
            var filter = new UIElementFilter(
                ControlTypes: UIElementTools.ParseControlTypes(controlTypes),
                InteractableOnly: interactableOnly,
                VisibleOnly: visibleOnly);
            var elementsResult = await UIElementTools.ReadTreeAsync(
                _uiElementService, windowHandle, maxDepth, filter, timeoutMs, progress, cancellationToken).ConfigureAwait(false);

            // An unknown control type name is the agent's mistake, not a failed walk
            if (elementsResult.ErrorCode == UIElementErrorCode.InvalidControlType)
            {
                return AnnotatedFailure(reported, windowTitle, elementsResult.ErrorCode.ToString(), elementsResult.Error ?? "Unknown control type");
            }

            // The pixels do not depend on the walk: a failed walk still returns the image, without overlays
            var walked = elementsResult.Success && elementsResult.Elements != null;
            capture = _screenshotService.CaptureWindowWithElements(
                windowHandle,
                windowTitle ?? elementsResult.WindowTitle,
                walked ? elementsResult.Elements! : [],
                includeFrame,
                highlightInteractable,
                options);

            if (capture.Metadata.Success)
            {
                capture = capture with { Metadata = WithWalk(capture.Metadata, elementsResult, walked) };
            }
        }

        // Read after the window was minimized again: a failed minimize adds a warning
        if (capture.Metadata.Success && restore.Warnings.Count > 0)
        {
            capture = capture with { Metadata = capture.Metadata with { Warnings = Merge(restore.Warnings, capture.Metadata.Warnings) } };
        }

        return ToolImages.ToCallToolResult(capture);
    }

    /// <summary>
    /// Adds what the UI Automation walk left out to a successful capture: the walk's error and code when it failed
    /// (the image then has no overlays), and truncated with its reason whenever the element list is incomplete.
    /// </summary>
    private static AnnotatedScreenshotResult WithWalk(AnnotatedScreenshotResult metadata, UIElementResult walk, bool walked)
    {
        if (!walked)
        {
            var code = walk.ErrorCode == UIElementErrorCode.None ? nameof(UIElementErrorCode.OperationException) : walk.ErrorCode.ToString();
            return metadata with
            {
                ElementsError = $"The UI elements could not be read, so the image has no overlays: {walk.Error ?? "unknown error"}",
                ElementsErrorCode = code,
                AccessibilityWarmup = walk.AccessibilityWarmup,
                Truncated = true,
                TruncationReason = walk.ErrorCode == UIElementErrorCode.OperationTimedOut
                    ? nameof(UIElementTruncationReason.Timeout)
                    : walk.TruncationReason?.ToString()
            };
        }

        metadata = metadata with { AccessibilityWarmup = walk.AccessibilityWarmup };
        return walk.Truncated
            ? metadata with { Truncated = true, TruncationReason = walk.TruncationReason?.ToString() }
            : metadata;
    }

    /// <summary>
    /// The state of a restoreIfMinimized: nothing to restore, a refusal (the policy denies a window the restore would
    /// show, or the restore failed), or the restored window with the input gate, both released by Dispose.
    /// </summary>
    private sealed class RestoreScope : IDisposable
    {
        public static readonly RestoreScope None = new(null, null, null);

        private readonly IDisposable? _gate;
        private readonly MinimizedWindowRestore? _restore;

        public RestoreScope(IDisposable? gate, MinimizedWindowRestore? restore, CaptureRefusal? refusal)
        {
            _gate = gate;
            _restore = restore;
            Refusal = refusal;
        }

        public CaptureRefusal? Refusal { get; }

        public IReadOnlyList<string> Warnings => _restore?.Warnings ?? [];

        public void Dispose()
        {
            try
            {
                _restore?.Dispose();
            }
            finally
            {
                _gate?.Dispose();
            }
        }
    }

    /// <summary>
    /// For restoreIfMinimized: when the window, or the window it is inside, is minimized, checks the options and the
    /// target policy of every window the restore would show, takes the input gate (a pause is a PausedByUser tool error,
    /// a busy gate InputBusy) and restores the window without activating it. A failed restore is minimized again and
    /// returned as a refusal.
    /// </summary>
    /// <exception cref="ToolErrorException">Input is paused, or the gate is busy; nothing was restored.</exception>
    private async Task<RestoreScope> RestoreIfMinimizedAsync(
        nint windowHandle, bool includeFrame, bool restoreIfMinimized, CaptureOptions options, CancellationToken cancellationToken)
    {
        if (!restoreIfMinimized || _screenshotService.FindMinimizedWindow(windowHandle) is not { } minimized)
        {
            return RestoreScope.None;
        }

        // A window that is also hidden (its app minimized it to the notification area) or cloaked (another virtual
        // desktop) is refused as it is: a restore would show a window its app hid, or work on a desktop nobody sees
        if (_screenshotService.CheckWindow(windowHandle) is { Code: not ScreenshotErrorCode.WindowMinimized } unavailable)
        {
            return new RestoreScope(null, null, unavailable);
        }

        // Nothing is restored for a call that would fail anyway
        if (CaptureRequest.Validate(options) is { } invalid)
        {
            return new RestoreScope(null, null, new CaptureRefusal(invalid.Code, invalid.Message));
        }

        foreach (var shown in _screenshotService.WindowsShownByRestore(minimized))
        {
            if (_windowService.EvaluateTargetPolicy(shown) is { Allowed: false } verdict)
            {
                return new RestoreScope(null, null, new CaptureRefusal(ScreenshotErrorCode.TargetDeniedByPolicy,
                    $"The window is minimized, and restoring it would show window {shown}, which the server's target policy " +
                    $"refuses: {verdict.Reason}. Nothing was restored or captured. The policy is set by the user in the server's " +
                    "environment; no tool argument changes it."));
            }
        }

        var gate = await InputGateToolFilter.EnterAsync(_session, cancellationToken).ConfigureAwait(false);
        MinimizedWindowRestore restore;
        try
        {
            restore = _screenshotService.RestoreForCapture(minimized, windowHandle, includeFrame);
        }
        catch
        {
            gate.Dispose();
            throw;
        }

        if (restore.Failure is { } failure)
        {
            new RestoreScope(gate, restore, null).Dispose();
            return new RestoreScope(null, null, failure);
        }

        return new RestoreScope(gate, restore, null);
    }

    private static IReadOnlyList<string> Merge(IReadOnlyList<string> first, IReadOnlyList<string>? second) =>
        second == null ? first : [.. first, .. second];

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

    private static CallToolResult WindowFailure(string errorCode, string error) =>
        ToolImages.Create(
            new ScreenshotResult(false, null, null, 0, 0, null, null, null, null, Error: error, ErrorCode: errorCode),
            image: null,
            isError: true);

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
