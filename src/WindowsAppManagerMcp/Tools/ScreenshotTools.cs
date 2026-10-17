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

    internal const string IncludeCursorDescription =
        "Draw the mouse pointer into the image (default: false). The result always says where it is (cursor)";

    internal const string GridSpacingDescription =
        "Draw a grid every this many screen pixels (20-10000), labeled with screen coordinates, to read points off the image";

    internal const string CaptureMethodDescription =
        "How the pixels are read: 'auto' (default: PrintWindow, falling back to the compositor), 'printwindow', 'wgc' " +
        "(Windows.Graphics.Capture, for windows that print black) or 'screen' (covering windows included)";

    internal const string RestoreIfMinimizedDescription =
        "Restore a minimized window (or the minimized window it is inside) for the capture without activating it, wait " +
        "until it has painted, capture, then minimize it again without activating another window. Default: false, which " +
        "refuses a minimized window with WindowMinimized. While the window is restored the call holds the input gate, so " +
        "it fails with PausedByUser while input is paused, and it is refused with TargetDeniedByPolicy when the policy " +
        "denies a window the restore would show. A hidden or cloaked window is not restored. A window that was maximized " +
        "before it was minimized is captured at its normal size and may restore un-maximized afterwards.";

    internal const string ActionableOnlyDescription =
        "Only mark elements an agent can act on: those with an Invoke, Toggle, Value, SelectionItem, ExpandCollapse or " +
        "RangeValue pattern, enabled or not (a disabled one is dimmed and flagged [disabled] in the legend). Labels and " +
        "containers are left out, but the elements inside them are still marked. Default: true when controlTypes is empty, " +
        "false with controlTypes.";

    /// <summary>capture_with_elements' default maxElements: enough marks for a dense window, few enough to stay legible.</summary>
    public const int DefaultMaxMarks = 80;

    /// <summary>capture_window's default padding around a ref's element, in screen pixels.</summary>
    public const int DefaultRefPadding = 8;

    /// <summary>The largest padding capture_window takes around a ref's element.</summary>
    public const int MaxRefPadding = 500;

    /// <summary>How long capture_window waits for a ref's element to be found again (its live bounds).</summary>
    public static readonly TimeSpan RefResolveTimeout = TimeSpan.FromSeconds(5);

    private readonly IScreenshotService _screenshotService;
    private readonly IUIElementService _uiElementService;
    private readonly IWindowService _windowService;
    private readonly IInputSession? _session;
    private readonly IElementRegistry? _elementRegistry;
    private readonly ICaptureTargetPolicy _capturePolicy;
    private readonly IMonitorService? _monitorService;

    /// <param name="screenshotService">The captures.</param>
    /// <param name="uiElementService">The UI Automation walk of capture_with_elements.</param>
    /// <param name="windowService">Window reads and the target policy of windows a restore would show.</param>
    /// <param name="targetPolicy">The user's target policy.</param>
    /// <param name="session">
    /// The input session: restoreIfMinimized takes the input gate through it, and refuses while input is paused.
    /// </param>
    /// <param name="elementRegistry">
    /// The element refs (roadmap M3.4): capture_window's ref is resolved through it to the element it crops to (roadmap
    /// M3.11). The server always passes it; without it capture_window refuses a ref.
    /// </param>
    /// <param name="capturePolicy">
    /// The target policy's check of a captured window, shared with the raw pixel reads (roadmap M6); default a
    /// <see cref="CaptureTargetPolicy"/> over <paramref name="windowService"/> and <paramref name="targetPolicy"/>.
    /// </param>
    /// <param name="monitorService">
    /// Resolves take_screenshot's `monitor` name (roadmap M6.7). The server always passes it; without it a monitor name fails.
    /// </param>
    public ScreenshotTools(
        IScreenshotService screenshotService,
        IUIElementService uiElementService,
        IWindowService windowService,
        ITargetPolicy targetPolicy,
        IInputSession? session = null,
        IElementRegistry? elementRegistry = null,
        ICaptureTargetPolicy? capturePolicy = null,
        IMonitorService? monitorService = null)
    {
        _screenshotService = screenshotService;
        _uiElementService = uiElementService;
        _windowService = windowService;
        ArgumentNullException.ThrowIfNull(targetPolicy);
        _session = session;
        _elementRegistry = elementRegistry;
        _capturePolicy = capturePolicy ?? new CaptureTargetPolicy(windowService, targetPolicy);
        _monitorService = monitorService;
    }

    [McpServerTool(Name = "take_screenshot", Title = "Capture Monitor", ReadOnly = false, Destructive = true, Idempotent = false, OpenWorld = false)]
    [Description("Capture a monitor (default: the primary), or with monitor 'all' every monitor in one image (the virtual screen, at " +
                 "most 64 megapixels; areas between monitors of different sizes are black). " +
                 ImageResultDescription + ImageToScreenDescription + " A monitor name that names none is MonitorNotFound.")]
    [ToolResultPayload(typeof(ScreenshotResult))]
    public CallToolResult TakeScreenshot(
        [Description(MonitorSelectorDescriptions.DeprecatedIndex)]
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
        int? maxWidth = null,
        [Description(MonitorSelectorDescriptions.MonitorOrAll)]
        MonitorSelector? monitor = null,
        [Description(IncludeCursorDescription)]
        bool includeCursor = false,
        [Description(GridSpacingDescription)]
        int? gridSpacing = null)
    {
        var options = Options(format, quality, maxDimension, output, outputPath, overwrite, saveToFile, maxWidth) with
        {
            IncludeCursor = includeCursor,
            GridSpacing = gridSpacing
        };
        var index = MonitorTargeting.Index(_monitorService, monitorIndex, monitor, allowAll: true, "Nothing was captured.") ??
                    _screenshotService.ListScreens().PrimaryIndex;
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
        int? maxWidth = null,
        [Description(IncludeCursorDescription)]
        bool includeCursor = false,
        [Description(GridSpacingDescription)]
        int? gridSpacing = null,
        [Description("Enlarge the image up to this many times (1-4, default 1) to read small text or controls; maxDimension still " +
                     "caps its long edge, and imageToScreen maps its points back")]
        double zoom = 1)
    {
        var options = Options(format, quality, maxDimension, output, outputPath, overwrite, saveToFile, maxWidth) with
        {
            IncludeCursor = includeCursor,
            GridSpacing = gridSpacing,
            Zoom = zoom
        };
        return ToolImages.ToCallToolResult(_screenshotService.CaptureRegion(x, y, width, height, options));
    }

    [McpServerTool(Name = "capture_window", Title = "Capture Window", ReadOnly = false, Destructive = true, Idempotent = false, OpenWorld = false)]
    [Description("Capture a specific window, even when other windows cover it. A minimized window fails with WindowMinimized " +
                 "(or pass restoreIfMinimized), a hidden one with WindowHidden, one on another virtual desktop with Cloaked, " +
                 "and a window of an app the user's policy denies with TargetDeniedByPolicy. A window that is not responding " +
                 "is captured as the compositor last showed it, or copied from the screen (warning WindowNotResponding). " +
                 ImageResultDescription + WarningsDescription +
                 "capturedRegion and imageToScreen use the window's real screen position: the visible frame's corner, or the client area's corner when includeFrame is false. " +
                 "With ref, the image is cropped to that element (plus padding) for a small, sharp view of one control; the crop's corner is its origin. " +
                 ImageToScreenDescription)]
    [ToolResultPayload(typeof(ScreenshotResult))]
    public async Task<CallToolResult> CaptureWindow(
        [Description("Window handle (as integer) from find_windows. Pass handle or window, not both; with ref both may be left out.")]
        long? handle = null,
        [Description(WindowTargeting.WindowDescription)]
        WindowSelector? window = null,
        [Description("The element to crop the image to: a ref from an earlier read, such as 'w4120035e17' (its bounds are read again now), " +
                     "plus padding, clipped to the window. The window is the ref's own; a handle or window that names another fails " +
                     "with InvalidArguments. ElementStale or AmbiguousElement when the element is gone, ElementOffscreen when it is offscreen or outside the window.")]
        string? @ref = null,
        [Description("Pixels of the window kept around the ref's element on every side (0-500). Default: 8. Only with ref.")]
        int? padding = null,
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
        [Description(CaptureMethodDescription)]
        WindowCaptureMethod captureMethod = WindowCaptureMethod.Auto,
        [Description(IncludeCursorDescription)]
        bool includeCursor = false,
        [Description(GridSpacingDescription)]
        int? gridSpacing = null,
        CancellationToken cancellationToken = default)
    {
        var element = ElementToCrop(@ref, padding);
        if (element?.Lookup is { Success: false } unknown)
        {
            return WindowFailure(unknown.ErrorCode.ToString(), (unknown.Error ?? $"ref '{element.Ref}' names no element.") +
                " Nothing was captured; capture_with_elements or find_ui_elements gives current refs.");
        }

        var windowHandle = WindowForCapture(handle, window, element);
        if (_capturePolicy.Check(windowHandle) is { } refusal)
        {
            return WindowFailure(refusal.Code.ToString(), refusal.Message);
        }

        var options = Options(format, quality, maxDimension, output, outputPath, overwrite, saveToFile, maxWidth) with
        {
            CaptureMethod = captureMethod,
            IncludeCursor = includeCursor,
            GridSpacing = gridSpacing
        };
        var restore = await RestoreIfMinimizedAsync(windowHandle, includeFrame, restoreIfMinimized, options, cancellationToken)
            .ConfigureAwait(false);
        if (restore.Refusal is { } refused)
        {
            return WindowFailure(refused.Code.ToString(), refused.Message);
        }

        ScreenCapture<ScreenshotResult> capture;
        using (restore)
        {
            if (element == null)
            {
                capture = _screenshotService.CaptureWindow(windowHandle, includeFrame, options);
            }
            else
            {
                // The window first: the ref's element is read only when the window can be captured at all
                if (_screenshotService.CheckWindow(windowHandle) is { } unavailable)
                {
                    return WindowFailure(unavailable.Code.ToString(), unavailable.Message);
                }

                var area = await ElementAreaAsync(element, cancellationToken).ConfigureAwait(false);
                if (area.Failure is { } failure)
                {
                    return failure;
                }

                capture = _screenshotService.CaptureWindowArea(windowHandle, includeFrame, area.Bounds!, options);
                if (capture.Metadata.Success)
                {
                    capture = capture with { Metadata = capture.Metadata with { Ref = area.Ref } };
                }

                // The crop shows the element the ref was moved to: say so
                if (capture.Metadata.Success && area.Moved)
                {
                    capture = capture with
                    {
                        Metadata = capture.Metadata with
                        {
                            RefMoved = true,
                            Warnings = Merge([CaptureWarnings.Format(CaptureWarnings.RefMoved, ElementRefs.MovedWarning(area.Ref!))],
                                capture.Metadata.Warnings)
                        }
                    };
                }
            }
        }

        // Read after the window was minimized again: a failed minimize adds a warning
        if (capture.Metadata.Success && restore.Warnings.Count > 0)
        {
            capture = capture with { Metadata = capture.Metadata with { Warnings = Merge(restore.Warnings, capture.Metadata.Warnings) } };
        }

        return ToolImages.ToCallToolResult(capture);
    }

    [McpServerTool(Name = "capture_with_elements", Title = "Capture Window with Element Boxes", ReadOnly = false, Destructive = true, Idempotent = false, OpenWorld = false)]
    [Description("Capture a window with its UI elements drawn on it as numbered marks (Set-of-Marks): a box per element and a filled tag with " +
                 "the element's ref, such as w4120035e17; when all marks' refs share their window part, the tags leave it out (e17) and markPrefix gives it (w4120035). Pick a target in the image, then act on it with element_action(ref) (no coordinates), " +
                 "or click the legend's center with this captureId. " + ImageResultDescription +
                 "The metadata's legend has one line per mark: '[ref] Type 'Name' #automationId center=(x,y)', center in pixels of the returned image. " +
                 "By default only elements with an action pattern (button, edit, check box, list item, menu item, ...) are marked, at most 80; " +
                 "detail 'full' adds the elements list with imageBounds and screenBounds. " +
                 "The image is returned even when the UI Automation walk fails or times out: " +
                 "then elementsError and elementsErrorCode say why, and truncated is true whenever the element list is incomplete. " +
                 "A window is refused as in capture_window (WindowMinimized unless restoreIfMinimized, WindowHidden, Cloaked, TargetDeniedByPolicy). " +
                 WarningsDescription + ImageToScreenDescription)]
    [ToolResultPayload(typeof(AnnotatedScreenshotResult))]
    public async Task<CallToolResult> CaptureWithElements(
        [Description("Window handle (as integer) from find_windows. Pass handle or window, not both.")]
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
        [Description(ActionableOnlyDescription)]
        bool? actionableOnly = null,
        [Description("Only include elements that are not offscreen. Default: true. " + UIElementTools.VisibleOnlyCaution)]
        bool visibleOnly = true,
        [Description("Use brighter colors for enabled elements. Default: true")]
        bool highlightInteractable = true,
        [Description("Timeout for UI element retrieval in ms (1000-30000). When it passes, the elements found so far are drawn (truncated: true); with none found, the image comes without elements and elementsErrorCode OperationTimedOut. Default: 5000")]
        int timeoutMs = 5000,
        [Description("Stop the walk once this many elements are found (default: 80, min: 1, max: 5000); then truncated=true and truncationReason MaxElements. " +
                     "An element without bounds or outside the image gets no mark, so there can be fewer marks. The walk reads breadth first, so the levels nearest the window are kept; narrow it with controlTypes or a smaller maxDepth rather than raising this.")]
        int maxElements = DefaultMaxMarks,
        [Description("Draw each mark's ref as a filled tag at its box. Default: true. false draws the boxes only.")]
        bool labels = true,
        [Description("Draw the marks on the image. Default: true. false returns the clean image, with the marks only in the legend.")]
        bool drawOverlays = true,
        [Description("What the metadata says about the marks: 'legend' (default: one compact line per mark) or 'full' (also the elements list with imageBounds, screenBounds and overlayColor).")]
        CaptureElementDetail detail = CaptureElementDetail.Legend,
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
        [Description(CaptureMethodDescription)]
        WindowCaptureMethod captureMethod = WindowCaptureMethod.Auto,
        IProgress<ProgressNotificationValue>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var options = Options(format, quality, maxDimension, output, outputPath, overwrite, saveToFile, maxWidth) with
        {
            CaptureMethod = captureMethod
        };
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

        // The title is informational: untitled windows (popups, menus) are still captured, with the walk's title
        var windowInfo = _windowService.GetWindowInfo(windowHandle);
        // Reported back only, so cut as the window tools cut it (roadmap M3.F31)
        var windowTitle = string.IsNullOrEmpty(windowInfo?.Title) ? null : AppText.Cut(windowInfo.Title);
        if (windowInfo == null && !_windowService.IsValidWindow(windowHandle))
        {
            return AnnotatedFailure(reported, null, ScreenshotErrorCode.InvalidHandle.ToString(), "Window not found or handle is invalid");
        }

        // Neither the pixels nor the element texts of a window the user's policy denies, or that it cannot check
        if (_capturePolicy.Verdict(windowInfo) is { Allowed: false } verdict)
        {
            return AnnotatedFailure(reported, windowTitle, nameof(ScreenshotErrorCode.TargetDeniedByPolicy),
                CaptureTargetPolicy.Message(verdict));
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

            // Where the window is while its elements are read: the capture compares it with where the window is when its
            // pixels are read, and gives no captureId when it moved meanwhile
            var walkedWindow = _screenshotService.ReadCapturedWindow(windowHandle, includeFrame);

            // The walk stops at timeoutMs with what it has, or when the client cancels the call (UIElementTools.ReadTreeAsync).
            // Without controlTypes only the elements an agent can act on are marked (roadmap M3.11)
            var types = UIElementTools.ParseControlTypes(controlTypes);
            var filter = new UIElementFilter(
                ControlTypes: types,
                InteractableOnly: interactableOnly,
                VisibleOnly: visibleOnly,
                MaxElements: Math.Clamp(maxElements, 1, UIElementTools.MaxElementsLimit),
                ActionableOnly: actionableOnly ?? types == null);
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
                new ElementOverlayOptions(Draw: drawOverlays, Labels: labels, HighlightInteractable: highlightInteractable),
                options,
                walkedWindow);

            if (capture.Metadata.Success)
            {
                capture = capture with { Metadata = WithMarks(WithWalk(capture.Metadata, elementsResult, walked), detail) };
            }
        }

        // Read after the window was minimized again: a failed minimize adds a warning
        if (capture.Metadata.Success && restore.Warnings.Count > 0)
        {
            capture = capture with { Metadata = capture.Metadata with { Warnings = Merge(restore.Warnings, capture.Metadata.Warnings) } };
        }

        return ToolImages.ToCallToolResult(capture);
    }

    /// <summary>capture_window's ref (roadmap M3.11): the ref normalized, what the registry knows of it, and the padding.</summary>
    private sealed record RefCrop(string Ref, ElementLookup Lookup, int Padding);

    /// <summary>The element capture_window crops to, or null without a ref.</summary>
    /// <exception cref="ToolErrorException">padding without ref, a padding out of range, or a ref that is not one (InvalidArguments).</exception>
    private RefCrop? ElementToCrop(string? elementRef, int? padding)
    {
        if (elementRef == null)
        {
            return padding == null ? null : throw new ToolErrorException(ToolErrorReporting.InvalidArgumentsErrorCode,
                "padding applies only with ref (the element to crop the image to). Nothing was captured.");
        }

        var pad = padding ?? DefaultRefPadding;
        if (pad is < 0 or > MaxRefPadding)
        {
            throw new ToolErrorException(ToolErrorReporting.InvalidArgumentsErrorCode,
                $"padding must be 0-{MaxRefPadding} pixels; it was {pad}. Nothing was captured.");
        }

        var normalized = ElementRefs.Normalize(elementRef) ?? throw new ToolErrorException(ToolErrorReporting.InvalidArgumentsErrorCode,
            $"ref '{elementRef}' is not an element ref: refs look like '{ElementRefs.Example}' and come from capture_with_elements, " +
            "find_ui_elements, get_ui_elements, get_focused_element or get_element_at_point. Nothing was captured.");
        var registry = _elementRegistry ?? throw new ToolErrorException(nameof(UIElementErrorCode.AutomationUnavailable),
            "Element refs are not available in this server. Nothing was captured.");
        return new RefCrop(normalized, registry.Lookup(normalized), pad);
    }

    /// <summary>
    /// The window capture_window captures: the one named, or with a ref and none named, the ref's own window. A named
    /// window that is not the ref's is refused: the element would not be in its image.
    /// </summary>
    /// <exception cref="ToolErrorException">No window named and no ref, or a named window that is not the ref's (InvalidArguments).</exception>
    private nint WindowForCapture(long? handle, WindowSelector? window, RefCrop? element)
    {
        if (element == null)
        {
            return WindowTargeting.Require(_windowService, handle, window);
        }

        if (WindowTargeting.Optional(_windowService, handle, window) is not { } named)
        {
            return element.Lookup.WindowHandle;
        }

        if (named != element.Lookup.WindowHandle)
        {
            throw new ToolErrorException(ToolErrorReporting.InvalidArgumentsErrorCode,
                $"ref '{element.Ref}' is an element of window {(long)element.Lookup.WindowHandle}, not of window {(long)named}, so it " +
                "is not in that window's image. Leave handle and window out to capture the ref's own window. Nothing was captured.");
        }

        return named;
    }

    /// <summary>
    /// The area capture_window crops to: the element's bounds, read now, plus padding; or why there is none.
    /// <paramref name="Moved"/>: the ref was moved to a re-created element (<see cref="ResolvedElement.Moved"/>).
    /// </summary>
    private sealed record ElementArea(BoundsDto? Bounds, string? Ref, CallToolResult? Failure, bool Moved = false);

    /// <summary>
    /// Resolves the ref (its live element, found again if it was re-created; otherwise the resolution's code: ElementStale,
    /// AmbiguousElement, WindowNotFound when UI Automation cannot read the window, NotResponding, OperationTimedOut or
    /// OperationException) and returns its bounds with the padding. An offscreen element, or one without bounds,
    /// is ElementOffscreen.
    /// </summary>
    private async Task<ElementArea> ElementAreaAsync(RefCrop element, CancellationToken cancellationToken)
    {
        var resolution = await _elementRegistry!.ResolveAsync(element.Ref, RefResolveTimeout, cancellationToken).ConfigureAwait(false);
        if (resolution.Element is not { } resolved)
        {
            return new ElementArea(null, null, WindowFailure(resolution.ErrorCode.ToString(),
                (resolution.Error ?? $"ref '{element.Ref}' could not be resolved.") + " Nothing was captured."));
        }

        var properties = resolved.Element.Properties;
        if (properties.IsOffscreen || properties.Bounds.ToBounds() is not { Width: > 0, Height: > 0 } bounds)
        {
            return new ElementArea(null, null, WindowFailure(nameof(ScreenshotErrorCode.ElementOffscreen),
                $"The element of ref '{resolved.Ref}' is offscreen (scrolled out of view, collapsed or hidden), so there is " +
                "nothing of it to capture. Scroll it into view first (element_action scroll_into_view), or capture the whole window."));
        }

        var pad = element.Padding;
        return new ElementArea(
            new BoundsDto(bounds.X - pad, bounds.Y - pad, bounds.Width + 2 * pad, bounds.Height + 2 * pad), resolved.Ref, null,
            resolved.Moved);
    }

    /// <summary>
    /// The legend of the marks (roadmap M3.11), and the elements list only for detail full: the legend names every mark
    /// with its ref and centre in a fraction of the list's tokens.
    /// </summary>
    private static AnnotatedScreenshotResult WithMarks(AnnotatedScreenshotResult metadata, CaptureElementDetail detail) =>
        metadata with
        {
            Legend = ElementMarkLegend.Build(metadata),
            Elements = detail == CaptureElementDetail.Full ? metadata.Elements : null
        };

    /// <summary>
    /// Adds what the UI Automation walk left out to a successful capture: the walk's error and code when it failed
    /// (the image then has no overlays), and truncated with its reason whenever the element list is incomplete. The walk's
    /// warnings (roadmap M3.F16: an elevated window, whose tree UI Automation cannot read; a minimized one is refused before
    /// the walk) go with the capture's own, which have the same "Code: text" form.
    /// </summary>
    private static AnnotatedScreenshotResult WithWalk(AnnotatedScreenshotResult metadata, UIElementResult walk, bool walked)
    {
        if (walk.Warnings is { Count: > 0 } warnings)
        {
            metadata = metadata with { Warnings = Merge(warnings, metadata.Warnings) };
        }

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

        // The windows the window owns are left out of its walk (roadmap M3.F9): named, so they can be captured on their own
        metadata = metadata with { AccessibilityWarmup = walk.AccessibilityWarmup, OwnedWindows = walk.OwnedWindows };
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
