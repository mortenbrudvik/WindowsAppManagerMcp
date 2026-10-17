using System.Collections.Concurrent;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Globalization;
using System.Runtime.InteropServices;
using WindowsAppManagerMcp.Models;
using WindowsAppManagerMcp.Native;
using WindowsAppManagerMcp.Services.Interfaces;

namespace WindowsAppManagerMcp.Services;

/// <summary>
/// Captures monitors, regions and windows (see <see cref="IScreenshotService"/>), and says when a capture cannot show
/// what the agent expects (roadmap M2.9): a minimized, hidden or cloaked window is refused with its own code before
/// any pixel is read, and a successful capture carries warnings for a window that does not answer (copied from the
/// screen), a region partly off the screen (cut to it), a DPI-scaled window and an image of one solid color.
/// </summary>
public class ScreenshotService : IScreenshotService
{
    /// <summary>Where captures go when output writes a file and no outputPath is given.</summary>
    public static readonly string DefaultCaptureFolder =
        Path.Combine(Path.GetTempPath(), "WindowsAppManagerMcp", "captures");

    /// <summary>
    /// The WindowMinimized message: Windows does not draw a minimized window, and its rectangle is the icon's at
    /// (-32000, -32000), so a capture would succeed with a tiny or black image.
    /// </summary>
    public const string MinimizedMessage =
        "The window is minimized, so it cannot be captured: Windows does not draw a minimized window, and its elements are " +
        "offscreen. Pass restoreIfMinimized: true to restore it without activation for the capture and minimize it again, " +
        "or restore it first with set_window_state (state 'restore') or focus_window.";

    /// <summary>The WindowHidden message.</summary>
    public const string HiddenMessage =
        "The window is hidden (it, or a window it is inside, does not have WS_VISIBLE), so it draws nothing and was not " +
        "captured. An app that hid its window in the notification area has to show it first; ask the user, or find the " +
        "visible window with find_windows.";

    /// <summary>The Cloaked message when the virtual desktop manager cannot say why the window is cloaked.</summary>
    public const string CloakedMessage =
        "The window is cloaked: it is on another virtual desktop, it is a suspended Store app, or its app hid it. Windows " +
        "does not draw it, so it was not captured. Ask the user to switch to its desktop, or find the visible window with " +
        "find_windows.";

    /// <summary>The Cloaked message for a window on another virtual desktop (roadmap M7.3).</summary>
    public const string OtherDesktopMessage =
        "The window is on another virtual desktop, so Windows does not draw it and it was not captured. The server does not " +
        "switch desktops: ask the user to switch to its desktop, or capture a window on this desktop.";

    /// <summary>The Cloaked message for a cloaked window on no virtual desktop (roadmap M7.3).</summary>
    public const string NoDesktopMessage =
        "The window is cloaked and on no virtual desktop: it is the frame of a suspended or prelaunched Store app, or its app " +
        "hid it. Windows does not draw it, so it was not captured. Find the visible window with find_windows.";

    /// <summary>How long a window restored for a capture may take to stop painting (two equal frames).</summary>
    public static readonly TimeSpan DefaultRestoreSettleTimeout = TimeSpan.FromMilliseconds(1500);

    /// <summary>How long restoring a window, or minimizing it again, may take before it counts as failed.</summary>
    public static readonly TimeSpan DefaultRestoreStateTimeout = TimeSpan.FromMilliseconds(1000);

    /// <summary>
    /// How many pixels the blank check samples along each axis (32 x 32 = 1024 pixels). A sampled pixel counts as the
    /// same color when no channel differs by more than <see cref="BlankTolerance"/>.
    /// </summary>
    private const int BlankSamplesPerAxis = 32;

    private const int BlankTolerance = 2;

    /// <summary>How long the frame comparison waits between two frames of a restored window.</summary>
    private static readonly TimeSpan SettleFrameInterval = TimeSpan.FromMilliseconds(50);

    private readonly IScreenCaptureWrapper _captureWrapper;
    private readonly IMonitorService _monitorService;
    private readonly INativeWindowWrapper _windows;
    private readonly IWindowCallRunner _windowCalls;
    private readonly TimeSpan _restoreSettleTimeout;
    private readonly TimeSpan _restoreStateTimeout;
    private readonly ICaptureRegistry? _captureRegistry;
    private readonly ICaptureTargetPolicy? _capturePolicy;
    private readonly ITargetPolicy? _targetPolicy;
    private readonly INativeVirtualDesktopWrapper? _virtualDesktops;
    private readonly IGraphicsCaptureWrapper? _graphicsCapture;

    // The windows restoreIfMinimized has restored for a capture, by how many restores: they are minimized again afterwards,
    // so their captures keep no visual signature (roadmap M6.4)
    private readonly ConcurrentDictionary<nint, int> _restoredForCapture = new();

    /// <summary>take_screenshot's monitorIndex for the whole virtual screen: every monitor in one image (roadmap M3.11).</summary>
    public const int VirtualScreenIndex = -1;

    /// <param name="captureWrapper">The pixel capture.</param>
    /// <param name="monitorService">Monitor lookups.</param>
    /// <param name="windows">
    /// Window reads: a minimized, hidden or cloaked window is refused before its pixels are read, a frame capture is
    /// cut to the visible frame, and a DPI-scaled window gets a warning. restoreIfMinimized shows and minimizes the
    /// window through it.
    /// </param>
    /// <param name="windowCalls">
    /// Runs PrintWindow, which waits for the window's thread, and the restore with a deadline; default a
    /// <see cref="WindowCallRunner"/> over <paramref name="windows"/>. A window that does not answer is copied from
    /// the screen instead.
    /// </param>
    /// <param name="restoreSettleTimeout">How long a restored window may paint; default <see cref="DefaultRestoreSettleTimeout"/>.</param>
    /// <param name="restoreStateTimeout">How long a restore or minimize may take; default <see cref="DefaultRestoreStateTimeout"/>.</param>
    /// <param name="captureRegistry">
    /// Where successful captures are registered under their captureId (roadmap M3.11), for the mouse tools' image space.
    /// Without it captures have no captureId. The server always passes it.
    /// </param>
    /// <param name="capturePolicy">
    /// The target policy's check of a window whose pixels <see cref="CapturePixels"/> reads (roadmap M6). Without it
    /// <see cref="CapturePixels"/> refuses every window. The capture tools check the policy themselves, so the capture
    /// members that return images do not ask it. The server always passes it.
    /// </param>
    /// <param name="targetPolicy">
    /// The user's target policy, so the visual signature of a screen capture lists the denied windows over it only when the
    /// policy denies anything (roadmap M6.4, owner decision D8). Null: every window over it is checked.
    /// </param>
    /// <param name="virtualDesktops">
    /// The shell's virtual desktop manager (roadmap M7.3): a Cloaked refusal says whether the window is on another virtual
    /// desktop. The server always passes it; without it the refusal names every cause.
    /// </param>
    /// <param name="graphicsCapture">
    /// Windows.Graphics.Capture (roadmap M7.1): window captures fall back to it (method auto) or use it (method wgc). The server
    /// always passes it; without it a window capture reads with PrintWindow or from the screen, as before.
    /// </param>
    public ScreenshotService(
        IScreenCaptureWrapper captureWrapper,
        IMonitorService monitorService,
        INativeWindowWrapper windows,
        IWindowCallRunner? windowCalls = null,
        TimeSpan? restoreSettleTimeout = null,
        TimeSpan? restoreStateTimeout = null,
        ICaptureRegistry? captureRegistry = null,
        ICaptureTargetPolicy? capturePolicy = null,
        ITargetPolicy? targetPolicy = null,
        INativeVirtualDesktopWrapper? virtualDesktops = null,
        IGraphicsCaptureWrapper? graphicsCapture = null)
    {
        _virtualDesktops = virtualDesktops;
        _graphicsCapture = graphicsCapture;
        _capturePolicy = capturePolicy;
        _targetPolicy = targetPolicy;
        _captureRegistry = captureRegistry;
        _captureWrapper = captureWrapper;
        _monitorService = monitorService;
        _windows = windows ?? throw new ArgumentNullException(nameof(windows));
        _windowCalls = windowCalls ?? new WindowCallRunner(windows);
        _restoreSettleTimeout = restoreSettleTimeout ?? DefaultRestoreSettleTimeout;
        _restoreStateTimeout = restoreStateTimeout ?? DefaultRestoreStateTimeout;
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

    public ScreenCapture<ScreenshotResult> CaptureMonitor(int monitorIndex, CaptureOptions options) =>
        Run("monitor", options, request => Complete(Acquire(new CaptureTarget.Monitor(monitorIndex)), request));

    public ScreenCapture<ScreenshotResult> CaptureRegion(int x, int y, int width, int height, CaptureOptions options) =>
        Run("region", options, request => Complete(Acquire(new CaptureTarget.Region(x, y, width, height)), request));

    public ScreenCapture<ScreenshotResult> CaptureWindow(nint windowHandle, bool includeFrame, CaptureOptions options) =>
        Run("window", options, request => Complete(Acquire(new CaptureTarget.Window(windowHandle, includeFrame, options.CaptureMethod)), request));

    /// <inheritdoc />
    public RawCapture CapturePixels(CaptureTarget target)
    {
        ArgumentNullException.ThrowIfNull(target);

        Acquired acquired;
        try
        {
            // The capture tools check the policy themselves, before restoreIfMinimized shows anything; a pixel read has no
            // tool in front of it, so it checks here, before anything of the window is read
            if (target is CaptureTarget.Window window)
            {
                ThrowIfRefused(_capturePolicy != null
                    ? _capturePolicy.Check(window.Handle)
                    : new CaptureRefusal(ScreenshotErrorCode.TargetDeniedByPolicy,
                        "The window's pixels cannot be read: this server has no target policy check for captures. Nothing was captured."));
            }

            acquired = Acquire(target);
        }
        catch (Exception ex) when (ex is not CaptureException)
        {
            // As Run reports it for the capture tools
            throw new CaptureException(ScreenshotErrorCode.CaptureFailed, $"Failed to capture {What(target)}: {ex.Message}");
        }

        ThrowIfIncomplete(acquired.Capture);
        AddBlankWarning(acquired.Capture, acquired.Warnings);
        return new RawCapture(target, acquired.Capture, acquired.MonitorIndex, acquired.ScaleFactor, acquired.Warnings.ToArray(),
            acquired.Window, acquired.Screen);
    }

    /// <inheritdoc />
    public string? RegisterRaw(RawCapture capture)
    {
        ArgumentNullException.ThrowIfNull(capture);
        var pixels = capture.Pixels;
        return Register(pixels.Width, pixels.Height, new ImageToScreenTransform(pixels.ScreenOriginX, pixels.ScreenOriginY, 1, 1),
            CapturedRegionOf(pixels), windowCapture: capture.Target is CaptureTarget.Window, capture.Window, capture.Screen,
            SignatureOf(pixels, capture.Target is CaptureTarget.Window, capture.Window));
    }

    /// <summary>
    /// The pixels of a capture before they become an image: what a target resolves to, its warnings so far, and for a
    /// window the window as it was read before its pixels (with its client area), for a screen capture the screen layout as
    /// it was read right after them. <see cref="Complete"/> renders it; <see cref="CapturePixels"/> returns it as it is.
    /// </summary>
    private sealed record Acquired(
        CaptureResult Capture,
        int? MonitorIndex,
        double? ScaleFactor,
        List<string> Warnings,
        bool WindowCapture,
        CapturedWindow? Window,
        CapturedScreen? Screen = null);

    /// <summary>
    /// Resolves <paramref name="target"/> and reads its pixels, with every refusal and warning of the capture tools: an
    /// unknown monitor, a region off the screen or over the budget, a window that cannot be captured right now
    /// (<see cref="CheckWindow"/>), the screen copy of a window that does not answer, the crop to the visible frame and the
    /// DPI warning. A screen capture also reads the screen layout right after its pixels (<see cref="ReadScreen"/>), so
    /// that its registration keeps the layout the pixels were read in, however long after them it is registered. The
    /// target policy is not checked here (see <see cref="CapturePixels"/>), and no image is made.
    /// </summary>
    /// <exception cref="CaptureException">The target was refused.</exception>
    private Acquired Acquire(CaptureTarget target)
    {
        var acquired = target switch
        {
            CaptureTarget.Monitor monitor => AcquireMonitor(monitor.Index),
            CaptureTarget.Region region => AcquireRegion(region.X, region.Y, region.Width, region.Height),
            CaptureTarget.Window window => AcquireWindow(window.Handle, window.IncludeFrame, window.Method),
            _ => throw new ArgumentException($"Unknown capture target {target.GetType().Name}", nameof(target))
        };

        return acquired.WindowCapture ? acquired : acquired with { Screen = ReadScreen() };
    }

    /// <summary>What a failure message calls the target, as the capture tools name it.</summary>
    private static string What(CaptureTarget target) => target switch
    {
        CaptureTarget.Monitor => "monitor",
        CaptureTarget.Region => "region",
        _ => "window"
    };

    private Acquired AcquireMonitor(int monitorIndex)
    {
        var monitors = _monitorService.GetAllMonitors();
        if (monitorIndex == VirtualScreenIndex)
        {
            return AcquireVirtualScreen(monitors);
        }

        if (monitorIndex < 0 || monitorIndex >= monitors.Count)
        {
            throw new CaptureException(ScreenshotErrorCode.InvalidMonitorIndex,
                $"Monitor index {monitorIndex} is out of range. Available monitors: 0-{monitors.Count - 1}, or " +
                $"{VirtualScreenIndex} for all of them in one image");
        }

        var monitor = monitors[monitorIndex];
        var bounds = monitor.Bounds;
        var capture = _captureWrapper.CaptureScreenRegion(bounds.X, bounds.Y, bounds.Width, bounds.Height);
        return new Acquired(capture, monitorIndex, monitor.ScaleFactor, [], WindowCapture: false, Window: null);
    }

    /// <summary>
    /// The whole virtual screen, every monitor in one image (roadmap M3.11), within the pixel budget: a larger desktop is
    /// refused before anything is allocated (CaptureFailed). It has no monitor index or scale factor, and warns when
    /// monitors of different sizes leave parts of the bounding rectangle on no screen (black in the image).
    /// </summary>
    private Acquired AcquireVirtualScreen(IReadOnlyList<MonitorInfo> monitors)
    {
        var screen = _captureWrapper.GetVirtualScreenBounds();
        if ((long)screen.Width * screen.Height > ScreenCaptureWrapper.MaxCapturePixels ||
            screen.Width > ScreenCaptureWrapper.MaxCaptureSize || screen.Height > ScreenCaptureWrapper.MaxCaptureSize)
        {
            throw new CaptureException(ScreenshotErrorCode.CaptureFailed,
                $"The virtual screen is {screen.Width}x{screen.Height} pixels, more than one capture may hold " +
                $"({ScreenCaptureWrapper.MaxCapturePixels / (1024 * 1024)} megapixels, {ScreenCaptureWrapper.MaxCaptureSize} " +
                "pixels on an edge). Nothing was captured. Capture one monitor at a time with take_screenshot, or parts with capture_region.");
        }

        var warnings = new List<string>();
        var region = new Rectangle(screen.X, screen.Y, screen.Width, screen.Height);
        var capture = _captureWrapper.CaptureScreenRegion(screen.X, screen.Y, screen.Width, screen.Height);
        if (monitors.Count > 0 && CoveredArea(region, monitors) < (long)region.Width * region.Height)
        {
            warnings.Add(CaptureWarnings.Format(CaptureWarnings.RegionPartlyOffMonitors,
                "the monitors do not fill the virtual screen's bounding rectangle; the parts on no monitor are black in the image."));
        }

        return new Acquired(capture, MonitorIndex: null, ScaleFactor: null, warnings, WindowCapture: false, Window: null);
    }

    private Acquired AcquireRegion(int x, int y, int width, int height)
    {
        if (width <= 0 || height <= 0)
        {
            throw new CaptureException(ScreenshotErrorCode.InvalidRegion, "Width and height must be positive values");
        }

        // Only the part on the virtual screen can be captured: the rest would be black
        var requested = new Rectangle(x, y, width, height);
        var screen = _captureWrapper.GetVirtualScreenBounds();
        var virtualScreen = new Rectangle(screen.X, screen.Y, screen.Width, screen.Height);
        var region = Rectangle.Intersect(requested, virtualScreen);
        if (region.Width <= 0 || region.Height <= 0)
        {
            throw new CaptureException(ScreenshotErrorCode.RegionOffScreen,
                $"No part of the region ({x}, {y}, {width}x{height}) is on the screen: the monitors cover ({virtualScreen.X}, " +
                $"{virtualScreen.Y}, {virtualScreen.Width}x{virtualScreen.Height}). Nothing was captured. Coordinates are " +
                "physical pixels of the virtual screen; get_monitors shows where each monitor is.");
        }

        if ((long)region.Width * region.Height > ScreenCaptureWrapper.MaxCapturePixels)
        {
            throw new CaptureException(ScreenshotErrorCode.InvalidRegion,
                $"The region on screen is {region.Width}x{region.Height} pixels, more than the budget of " +
                $"{ScreenCaptureWrapper.MaxCapturePixels / (1024 * 1024)} megapixels per capture. Capture it in parts, " +
                "or one monitor at a time with take_screenshot.");
        }

        if (region.Width > ScreenCaptureWrapper.MaxCaptureSize || region.Height > ScreenCaptureWrapper.MaxCaptureSize)
        {
            throw new CaptureException(ScreenshotErrorCode.InvalidRegion,
                $"The region on screen is {region.Width}x{region.Height} pixels, more than {ScreenCaptureWrapper.MaxCaptureSize} " +
                "pixels on an edge. Capture it in parts, or one monitor at a time with take_screenshot.");
        }

        var warnings = new List<string>();
        if (region != requested)
        {
            warnings.Add(CaptureWarnings.Format(CaptureWarnings.RegionClipped,
                $"the region is partly off the screen; only ({region.X}, {region.Y}, {region.Width}x{region.Height}) " +
                "was captured (capturedRegion)."));
        }

        var capture = _captureWrapper.CaptureScreenRegion(region.X, region.Y, region.Width, region.Height);

        var monitors = _monitorService.GetAllMonitors();
        if (monitors.Count > 0 && CoveredArea(region, monitors) < (long)region.Width * region.Height)
        {
            warnings.Add(CaptureWarnings.Format(CaptureWarnings.RegionPartlyOffMonitors,
                "part of the region lies between monitors, on no screen, and is black in the image."));
        }

        // The monitor that contains the region's center, or none (MONITOR_DEFAULTTONULL, not the nearest one)
        var monitor = MonitorContaining(monitors, region.X + region.Width / 2, region.Y + region.Height / 2);
        return new Acquired(capture, monitor?.Index, monitor?.ScaleFactor, warnings, WindowCapture: false, Window: null);
    }

    /// <summary>
    /// A window: refused as <see cref="CheckWindow"/> says before any pixel is read, then read as it was before its pixels
    /// (<see cref="ReadCapturedWindow"/>, with the client area the capture shows), its pixels (<see cref="CaptureWindowPixels"/>)
    /// and the DPI warning of its monitor. A window without a monitor reports scale 1.
    /// </summary>
    private Acquired AcquireWindow(nint windowHandle, bool includeFrame, WindowCaptureMethod method)
    {
        ThrowIfRefused(CheckWindow(windowHandle));
        var window = ReadCapturedWindow(windowHandle, includeFrame);
        var warnings = new List<string>();
        var capture = CaptureWindowPixels(windowHandle, includeFrame, warnings, method);
        var monitor = _monitorService.GetMonitorForWindow(windowHandle);
        AddDpiWarning(windowHandle, monitor, warnings);
        return new Acquired(capture, monitor?.Index, monitor?.ScaleFactor ?? 1.0, warnings, WindowCapture: true,
            WithClientArea(window, capture));
    }

    public ScreenCapture<ScreenshotResult> CaptureWindowArea(nint windowHandle, bool includeFrame, BoundsDto area, CaptureOptions options)
    {
        ArgumentNullException.ThrowIfNull(area);
        return Run("window area", options, request =>
        {
            // The client area is the one before the crop: a click on the crop checks that the client area is still there
            var acquired = Acquire(new CaptureTarget.Window(windowHandle, includeFrame, options.CaptureMethod));
            var capture = acquired.Capture;

            // Only what the window capture shows can be cropped to: the visible frame, or the client area without it
            var crop = Rectangle.Intersect(
                new Rectangle(area.X, area.Y, area.Width, area.Height),
                new Rectangle(capture.ScreenOriginX, capture.ScreenOriginY, capture.Width, capture.Height));
            if (crop.Width <= 0 || crop.Height <= 0)
            {
                throw new CaptureException(ScreenshotErrorCode.ElementOffscreen,
                    $"The element's area ({area.X}, {area.Y}, {area.Width}x{area.Height}) is outside the captured " +
                    $"{(includeFrame ? "window" : "client area")} ({capture.ScreenOriginX}, {capture.ScreenOriginY}, " +
                    $"{capture.Width}x{capture.Height}), so nothing was captured. Scroll it into view (element_action " +
                    "scroll_into_view), or capture the whole window" + (includeFrame ? "." : ", or pass includeFrame: true."));
            }

            if (crop.Width != capture.Width || crop.Height != capture.Height)
            {
                acquired = acquired with { Capture = Crop(capture, crop) };
            }

            // A crop to a ref keeps no signature: wait_for_visual's since watches a whole window, region or monitor
            return Complete(acquired, request, signed: false);
        });
    }

    public ScreenCapture<AnnotatedScreenshotResult> CaptureWindowWithElements(
        nint windowHandle,
        string? windowTitle,
        IReadOnlyList<UIElementInfo> elements,
        bool includeFrame,
        ElementOverlayOptions overlays,
        CaptureOptions options,
        CapturedWindow? walkedWindow = null)
    {
        ArgumentNullException.ThrowIfNull(overlays);
        try
        {
            var request = CaptureRequest.Resolve(options);
            ThrowIfRefused(CheckWindow(windowHandle));
            var window = ReadCapturedWindow(windowHandle, includeFrame);

            var warnings = new List<string>();
            var captureStopwatch = Stopwatch.StartNew();
            var capture = CaptureWindowPixels(windowHandle, includeFrame, warnings, options.CaptureMethod);
            var captureMs = captureStopwatch.Elapsed.TotalMilliseconds;
            window = WithClientArea(window, capture);

            // The elements were read before the pixels: a window that moved or resized meanwhile shows them elsewhere than
            // their bounds say, so a click at a mark would land beside its element. No captureId then (roadmap M3.11), and
            // no marks at all when the caller would rather have none than offset ones (roadmap M3.F32)
            if (walkedWindow != null && window != null &&
                (walkedWindow.ProcessId != window.ProcessId || !CoordinateSpaceResolver.WithinTolerance(walkedWindow.WindowRect, window.WindowRect)))
            {
                warnings.Add(CaptureWarnings.Format(CaptureWarnings.WindowMoved,
                    $"the window moved or resized while its elements were read (its rectangle was {Describe(walkedWindow.WindowRect)}, " +
                    $"then {Describe(window.WindowRect)}), so " + (overlays.MarkMovedWindow
                        ? "the marks and the legend are offset from the controls in the image, and the capture has no captureId. " +
                          "Capture it again, or act by ref with element_action."
                        : "no marks were drawn and the capture has no captureId. Read it again, or act by ref with element_action.")));
                window = null;
                if (!overlays.MarkMovedWindow)
                {
                    elements = [];
                }
            }
            var monitor = _monitorService.GetMonitorForWindow(windowHandle);
            AddDpiWarning(windowHandle, monitor, warnings);
            AddBlankWarning(capture, warnings);

            // Element bounds are screen pixels; the image starts at the capture's screen origin, which is the client
            // area's corner when the frame is left out. The marks are placed in the returned image's pixels and drawn on
            // it after it was scaled (roadmap M3.11), so their lines and tags keep their size.
            var annotationStopwatch = Stopwatch.StartNew();
            var (outputWidth, outputHeight) = request.OutputSize(capture.Width, capture.Height);
            var colored = new List<(AnnotatedElement Element, Color Color)>();
            CollectAnnotations(elements, capture.ScreenOriginX, capture.ScreenOriginY, capture.Width, capture.Height,
                overlays.HighlightInteractable, colored);
            var finalAnnotations = outputWidth != capture.Width || outputHeight != capture.Height
                ? ScaleAnnotations(colored.Select(c => c.Element).ToList(), outputWidth, outputHeight, capture.Width, capture.Height)
                : colored.Select(c => c.Element).ToList();

            // The tags leave out the window part every mark's ref shares, which the result reports (roadmap M3.F38): 79 tags
            // of w1399829e<n> covered File Explorer's toolbar and each other
            var markPrefix = overlays.Draw && overlays.Labels
                ? ElementMarkRenderer.SharedWindowPart(finalAnnotations.Select(annotation => annotation.Ref))
                : null;
            var marks = finalAnnotations
                .Select((annotation, i) => new ElementMark(
                    new Rectangle(annotation.ImageBounds.X, annotation.ImageBounds.Y, annotation.ImageBounds.Width, annotation.ImageBounds.Height),
                    colored[i].Color,
                    annotation.Ref == null ? null : ElementMarkRenderer.TagText(annotation.Ref, markPrefix)))
                .ToList();

            // Before the marks: at full size they are drawn on the captured pixels themselves
            var signature = window != null ? SignatureOf(capture, windowCapture: true, window) : null;
            var rendered = Render(capture, request, overlays.Draw
                ? bitmap => ElementMarkRenderer.Draw(bitmap, marks, overlays.Labels)
                : null);
            var annotationMs = annotationStopwatch.Elapsed.TotalMilliseconds;

            var metadata = new AnnotatedScreenshotResult(
                Success: true,
                ImageFormat: CaptureRequest.FormatName(rendered.Encoding),
                MimeType: rendered.Image.MimeType,
                Width: rendered.Width,
                Height: rendered.Height,
                WindowHandle: (long)windowHandle,
                WindowTitle: windowTitle,
                CapturedRegion: CapturedRegionOf(capture),
                ImageToScreen: TransformOf(capture, rendered),
                Elements: finalAnnotations,
                ElementCount: finalAnnotations.Count,
                CaptureElapsedMs: Math.Round(captureMs, 2),
                AnnotationElapsedMs: Math.Round(annotationMs, 2),
                MonitorScaleFactor: monitor?.ScaleFactor,
                FilePath: rendered.FilePath,
                OriginalWidth: rendered.IsScaled ? capture.Width : null,
                OriginalHeight: rendered.IsScaled ? capture.Height : null,
                ByteSize: rendered.Image.Data.LongLength,
                CaptureMethod: capture.Method,
                CoordinateSpace: CoordinateSpaces.PhysicalVirtualScreen,
                Warnings: warnings.Count > 0 ? warnings : null,
                MarkPrefix: markPrefix);
            metadata = metadata with
            {
                CaptureId = Register(metadata.Width, metadata.Height, metadata.ImageToScreen!, metadata.CapturedRegion!, windowCapture: true, window,
                    screen: null, signature)
            };

            return new ScreenCapture<AnnotatedScreenshotResult>(metadata, request.ReturnsImage ? rendered.Image : null);
        }
        catch (CaptureException ex)
        {
            return new ScreenCapture<AnnotatedScreenshotResult>(
                AnnotatedFailure(windowHandle, windowTitle, ex.Code, ex.Message), null);
        }
        catch (Exception ex)
        {
            return new ScreenCapture<AnnotatedScreenshotResult>(
                AnnotatedFailure(windowHandle, windowTitle, ScreenshotErrorCode.CaptureFailed,
                    $"Failed to capture window with elements: {ex.Message}"),
                null);
        }
    }

    /// <inheritdoc />
    public CaptureRefusal? CheckWindow(nint windowHandle)
    {
        if (!_captureWrapper.IsValidWindow(windowHandle))
        {
            return new CaptureRefusal(ScreenshotErrorCode.InvalidHandle, "Invalid window handle or window no longer exists");
        }

        // Hidden and cloaked come before minimized: WindowMinimized advises restoreIfMinimized, and restoring a window
        // its app hid (minimized to the notification area) would show it, while one on another desktop stays unseen.
        // IsWindowVisible is false when the window or any window it is inside lacks WS_VISIBLE.
        if (!_windows.IsWindowVisible(windowHandle))
        {
            return new CaptureRefusal(ScreenshotErrorCode.WindowHidden, HiddenMessage);
        }

        var root = Root(windowHandle);
        if (_windows.IsCloaked(windowHandle) || (root != windowHandle && _windows.IsCloaked(root)))
        {
            return new CaptureRefusal(ScreenshotErrorCode.Cloaked, VirtualDesktops.PlacementOf(_virtualDesktops, root) switch
            {
                VirtualDesktopPlacement.OtherDesktop => OtherDesktopMessage,
                VirtualDesktopPlacement.NoDesktop => NoDesktopMessage,
                _ => CloakedMessage
            });
        }

        if (FindMinimizedWindow(windowHandle) != null)
        {
            return new CaptureRefusal(ScreenshotErrorCode.WindowMinimized, MinimizedMessage);
        }

        return null;
    }

    /// <inheritdoc />
    public nint? FindMinimizedWindow(nint windowHandle)
    {
        // A control inside a minimized window is offscreen with it, though it is not iconic itself
        var root = Root(windowHandle);
        if (root != windowHandle && _windows.IsIconic(root))
        {
            return root;
        }

        return _windows.IsIconic(windowHandle) ? windowHandle : null;
    }

    /// <inheritdoc />
    public IReadOnlyList<nint> WindowsShownByRestore(nint minimizedWindow)
    {
        var windows = new List<nint> { minimizedWindow };
        _windows.EnumWindows(window =>
        {
            if (window != minimizedWindow && WindowOwnerChain.IsOwnedBy(_windows, window, minimizedWindow))
            {
                windows.Add(window);
            }

            return true;
        });

        return windows;
    }

    /// <inheritdoc />
    public MinimizedWindowRestore RestoreForCapture(nint minimizedWindow, nint captureWindow, bool includeFrame)
    {
        var restore = new MinimizedWindowRestore(minimizedWindow, _windows, _windowCalls, _restoreStateTimeout);
        _restoredForCapture.AddOrUpdate(captureWindow, 1, (_, restores) => restores + 1);
        var released = 0;
        restore.Released = () =>
        {
            if (Interlocked.Exchange(ref released, 1) == 0 &&
                _restoredForCapture.AddOrUpdate(captureWindow, 0, (_, restores) => restores - 1) <= 0)
            {
                _restoredForCapture.TryRemove(KeyValuePair.Create(captureWindow, 0));
            }
        };

        try
        {
            restore.Restore();
        }
        catch
        {
            restore.Released();
            throw;
        }

        if (restore.Failure == null && !WaitForTwoEqualFrames(captureWindow, includeFrame))
        {
            restore.StillPainting(_restoreSettleTimeout);
        }

        return restore;
    }

    /// <summary>
    /// Captures frames of a window that was just restored until two in a row are equal: the restore animation and
    /// the application's repaint are over. False when that did not happen within the settle time. A frame that
    /// cannot be captured ends the wait; the capture itself then reports why.
    /// </summary>
    private bool WaitForTwoEqualFrames(nint window, bool includeFrame)
    {
        var waited = Stopwatch.StartNew();
        byte[]? previous = null;
        while (true)
        {
            byte[] frame;
            try
            {
                frame = _windowCalls.Run(window, () => _captureWrapper.CaptureWindow(window, includeFrame)).Bgra;
            }
            catch (Exception)
            {
                return true;
            }

            if (previous != null && previous.AsSpan().SequenceEqual(frame))
            {
                return true;
            }

            if (waited.Elapsed >= _restoreSettleTimeout)
            {
                return false;
            }

            previous = frame;
            Thread.Sleep(SettleFrameInterval);
        }
    }

    private nint Root(nint windowHandle)
    {
        var root = _windows.GetAncestor(windowHandle, WindowAncestor.Root);
        return root == nint.Zero ? windowHandle : root;
    }

    private static void ThrowIfRefused(CaptureRefusal? refusal)
    {
        if (refusal != null)
        {
            throw new CaptureException(refusal.Code, refusal.Message);
        }
    }

    /// <summary>
    /// The window's pixels. PrintWindow waits for the window's thread, so it runs with a deadline; a window that
    /// Windows reports hung, or that does not answer in time, is copied from the screen with a warning instead. A
    /// frame capture is then cut to the visible frame.
    /// </summary>
    private CaptureResult CaptureWindowPixels(nint windowHandle, bool includeFrame, List<string> warnings,
        WindowCaptureMethod method = WindowCaptureMethod.Auto)
    {
        switch (method)
        {
            case WindowCaptureMethod.Screen:
            {
                var copied = _captureWrapper.CaptureWindowFromScreen(windowHandle, includeFrame);
                ThrowIfIncomplete(copied);
                return includeFrame ? CropToVisibleFrame(windowHandle, copied) : copied;
            }

            case WindowCaptureMethod.Wgc:
            {
                // Already the visible frame, or the client area
                var composed = GraphicsCapture(windowHandle, includeFrame, out var why) ?? throw new CaptureException(
                    ScreenshotErrorCode.CaptureFailed,
                    $"captureMethod wgc could not capture the window: {why} Nothing was captured; leave captureMethod out (auto) " +
                    "or use printwindow or screen.");
                ThrowIfIncomplete(composed);
                return composed;
            }
        }

        CaptureResult capture;
        try
        {
            capture = _windowCalls.Run(windowHandle, () => _captureWrapper.CaptureWindow(windowHandle, includeFrame));
        }
        catch (WindowNotRespondingException ex)
        {
            // The compositor still has the window as it was last drawn, without the windows that cover it (roadmap M7.1)
            if (method == WindowCaptureMethod.Auto && GraphicsCapture(windowHandle, includeFrame, out _) is { } composed)
            {
                ThrowIfIncomplete(composed);
                warnings.Add(CaptureWarnings.Format(CaptureWarnings.WindowNotResponding,
                    "the window could not be asked to draw itself, so the image is the window as the desktop compositor last " +
                    $"showed it (captureMethod WindowsGraphicsCapture); it may be out of date. Reason: {ex.Message}"));
                return composed;
            }

            // The runner also refuses at once while too many calls to other applications are stuck, so the warning
            // gives its reason rather than blaming this window's application
            capture = _captureWrapper.CaptureWindowFromScreen(windowHandle, includeFrame);
            warnings.Add(CaptureWarnings.Format(CaptureWarnings.WindowNotResponding,
                "the window could not be asked to draw itself, so the image is a copy of the screen there and shows any " +
                $"window that covers it. Reason: {ex.Message}"));
        }

        ThrowIfIncomplete(capture);
        capture = includeFrame ? CropToVisibleFrame(windowHandle, capture) : capture;

        // A window drawn by the GPU, DirectComposition, WinUI or UWP often prints as one solid color (black): the compositor
        // has its real pixels (roadmap M7.1). A window that really is one color stays as PrintWindow read it
        if (method == WindowCaptureMethod.Auto && BlankColor(capture) != null &&
            GraphicsCapture(windowHandle, includeFrame, out _) is { } recaptured &&
            recaptured.Bgra.LongLength >= (long)recaptured.Width * recaptured.Height * 4 && BlankColor(recaptured) == null)
        {
            return recaptured;
        }

        return capture;
    }

    /// <summary>
    /// The window through Windows.Graphics.Capture (roadmap M7.1), or null with <paramref name="why"/> when there is no
    /// such capture here or it failed.
    /// </summary>
    private CaptureResult? GraphicsCapture(nint windowHandle, bool includeFrame, out string why)
    {
        if (_graphicsCapture is not { IsSupported: true } graphics)
        {
            why = "Windows.Graphics.Capture is not available on this computer (it needs Windows 10 1903 or later and Direct3D 11).";
            return null;
        }

        try
        {
            why = string.Empty;
            return graphics.CaptureWindow(windowHandle, includeFrame);
        }
        catch (GraphicsCaptureException ex)
        {
            why = ex.Message;
            return null;
        }
    }

    /// <summary>
    /// Cuts a frame capture to DWMWA_EXTENDED_FRAME_BOUNDS. GetWindowRect includes invisible resize borders (about
    /// 7 pixels left, right and bottom at 100%), which PrintWindow leaves black, and a maximized window's rectangle
    /// overhangs its monitor by them. The origin moves to the visible frame's corner. A window without a DWM frame
    /// attribute (a child window) is left as it is.
    /// </summary>
    private CaptureResult CropToVisibleFrame(nint windowHandle, CaptureResult capture)
    {
        if (_windows.GetExtendedFrameBounds(windowHandle) is not { } frame)
        {
            return capture;
        }

        var visible = Rectangle.Intersect(
            Rectangle.FromLTRB(frame.Left, frame.Top, frame.Right, frame.Bottom),
            new Rectangle(capture.ScreenOriginX, capture.ScreenOriginY, capture.Width, capture.Height));
        if (visible.Width <= 0 || visible.Height <= 0 ||
            (visible.Width == capture.Width && visible.Height == capture.Height))
        {
            return capture;
        }

        return Crop(capture, visible);
    }

    /// <summary>The part of <paramref name="capture"/> inside <paramref name="screenRectangle"/>, which must lie inside it.</summary>
    private static CaptureResult Crop(CaptureResult capture, Rectangle screenRectangle)
    {
        var left = screenRectangle.X - capture.ScreenOriginX;
        var top = screenRectangle.Y - capture.ScreenOriginY;
        var rowBytes = screenRectangle.Width * 4;
        var pixels = GC.AllocateUninitializedArray<byte>(rowBytes * screenRectangle.Height);
        for (var row = 0; row < screenRectangle.Height; row++)
        {
            Buffer.BlockCopy(capture.Bgra, ((top + row) * capture.Width + left) * 4, pixels, row * rowBytes, rowBytes);
        }

        return capture with
        {
            Bgra = pixels,
            Width = screenRectangle.Width,
            Height = screenRectangle.Height,
            ScreenOriginX = screenRectangle.X,
            ScreenOriginY = screenRectangle.Y
        };
    }

    /// <summary>
    /// Warns when the window is not per-monitor DPI aware on its monitor: Windows lays it out at another DPI and
    /// stretches it, and PrintWindow can then draw it at its unscaled size, leaving part of the image empty or the
    /// content offset from the screen.
    /// </summary>
    private void AddDpiWarning(nint windowHandle, MonitorInfo? monitor, List<string> warnings)
    {
        if (monitor == null)
        {
            return;
        }

        var windowDpi = (int)_windows.GetDpiForWindow(windowHandle);
        var monitorDpi = (int)Math.Round(monitor.ScaleFactor * 96);
        if (windowDpi == 0 || Math.Abs(windowDpi - monitorDpi) <= 1)
        {
            return;
        }

        warnings.Add(CaptureWarnings.Format(CaptureWarnings.DpiScaledWindow,
            $"the window works at {windowDpi} DPI on a {monitorDpi} DPI monitor, so Windows scales it. The image can show " +
            "it at its unscaled size, partly empty or offset from the screen; if it looks wrong, capture_region over " +
            "capturedRegion copies the screen as shown."));
    }

    /// <summary>
    /// Warns when the image is one solid color: about 1024 pixels on a grid all have the same color. That is right for
    /// an empty area, but a black image usually means GPU or protected content this capture cannot read, or a window
    /// that has not painted yet.
    /// </summary>
    private static void AddBlankWarning(CaptureResult capture, List<string> warnings)
    {
        if (BlankColor(capture) is { } color)
        {
            warnings.Add(CaptureWarnings.Format(CaptureWarnings.ImageBlank,
                $"every sampled pixel is #{color}: GPU or protected content, or not painted yet?"));
        }
    }

    /// <summary>The color as RRGGBB when every sampled pixel has it (within a small tolerance), otherwise null.</summary>
    private static string? BlankColor(CaptureResult capture)
    {
        if (capture.Width <= 0 || capture.Height <= 0 || capture.Bgra.LongLength < (long)capture.Width * capture.Height * 4)
        {
            return null;
        }

        var pixels = capture.Bgra;
        int b0 = pixels[0], g0 = pixels[1], r0 = pixels[2];
        var columns = Math.Min(BlankSamplesPerAxis, capture.Width);
        var rows = Math.Min(BlankSamplesPerAxis, capture.Height);
        for (var row = 0; row < rows; row++)
        {
            var y = rows == 1 ? 0 : (int)((long)row * (capture.Height - 1) / (rows - 1));
            for (var column = 0; column < columns; column++)
            {
                var x = columns == 1 ? 0 : (int)((long)column * (capture.Width - 1) / (columns - 1));
                var i = ((long)y * capture.Width + x) * 4;
                if (Math.Abs(pixels[i] - b0) > BlankTolerance ||
                    Math.Abs(pixels[i + 1] - g0) > BlankTolerance ||
                    Math.Abs(pixels[i + 2] - r0) > BlankTolerance)
                {
                    return null;
                }
            }
        }

        return string.Create(CultureInfo.InvariantCulture, $"{r0:X2}{g0:X2}{b0:X2}");
    }

    /// <summary>How much of <paramref name="region"/> the monitors cover, in pixels (monitors do not overlap).</summary>
    private static long CoveredArea(Rectangle region, IReadOnlyList<MonitorInfo> monitors) =>
        monitors
            .Select(m => Rectangle.Intersect(region, new Rectangle(m.Bounds.X, m.Bounds.Y, m.Bounds.Width, m.Bounds.Height)))
            .Where(r => r.Width > 0 && r.Height > 0)
            .Sum(r => (long)r.Width * r.Height);

    private static MonitorInfo? MonitorContaining(IReadOnlyList<MonitorInfo> monitors, int x, int y) =>
        monitors.FirstOrDefault(m =>
            x >= m.Bounds.X && x < m.Bounds.X + m.Bounds.Width && y >= m.Bounds.Y && y < m.Bounds.Y + m.Bounds.Height);

    /// <summary>
    /// Resolves the options, then runs <paramref name="capture"/>. A <see cref="CaptureException"/>
    /// becomes a failure with its code; any other exception (the native capture failed) becomes
    /// <see cref="ScreenshotErrorCode.CaptureFailed"/>.
    /// </summary>
    private static ScreenCapture<ScreenshotResult> Run(
        string what,
        CaptureOptions options,
        Func<CaptureRequest, ScreenCapture<ScreenshotResult>> capture)
    {
        try
        {
            return capture(CaptureRequest.Resolve(options));
        }
        catch (CaptureException ex)
        {
            return new ScreenCapture<ScreenshotResult>(Failure(ex.Code, ex.Message), null);
        }
        catch (Exception ex)
        {
            return new ScreenCapture<ScreenshotResult>(
                Failure(ScreenshotErrorCode.CaptureFailed, $"Failed to capture {what}: {ex.Message}"), null);
        }
    }

    /// <summary>Refuses a capture whose buffer is shorter than its size says (CaptureFailed).</summary>
    private static void ThrowIfIncomplete(CaptureResult capture)
    {
        if (capture.Bgra.LongLength < (long)capture.Width * capture.Height * 4)
        {
            throw new CaptureException(ScreenshotErrorCode.CaptureFailed,
                $"The capture returned {capture.Bgra.LongLength} bytes for {capture.Width}x{capture.Height} pixels.");
        }
    }

    /// <summary>
    /// The metadata and image of a successful capture, registered under its captureId. The window is the captured window
    /// as it was before the capture (<see cref="ReadCapturedWindow"/>) for a window capture. With <paramref name="signed"/>,
    /// the registration keeps the capture's visual signature while the registry keeps signatures (roadmap M6.4).
    /// </summary>
    private ScreenCapture<ScreenshotResult> Complete(Acquired acquired, CaptureRequest request, bool signed = true)
    {
        var (capture, monitorIndex, scaleFactor, warnings, windowCapture, window, screen) = acquired;
        ThrowIfIncomplete(capture);
        AddBlankWarning(capture, warnings);
        var signature = signed && (windowCapture ? window != null : screen != null) ? SignatureOf(capture, windowCapture, window) : null;
        var rendered = Render(capture, request, annotate: null);

        var metadata = new ScreenshotResult(
            Success: true,
            ImageFormat: CaptureRequest.FormatName(rendered.Encoding),
            MimeType: rendered.Image.MimeType,
            Width: rendered.Width,
            Height: rendered.Height,
            CapturedRegion: CapturedRegionOf(capture),
            ImageToScreen: TransformOf(capture, rendered),
            MonitorIndex: monitorIndex,
            ScaleFactor: scaleFactor,
            FilePath: rendered.FilePath,
            OriginalWidth: rendered.IsScaled ? capture.Width : null,
            OriginalHeight: rendered.IsScaled ? capture.Height : null,
            ByteSize: rendered.Image.Data.LongLength,
            CaptureMethod: capture.Method,
            CoordinateSpace: CoordinateSpaces.PhysicalVirtualScreen,
            Warnings: warnings.Count > 0 ? warnings : null);
        metadata = metadata with
        {
            CaptureId = Register(metadata.Width, metadata.Height, metadata.ImageToScreen!, metadata.CapturedRegion!, windowCapture, window, screen,
                signature)
        };

        return new ScreenCapture<ScreenshotResult>(metadata, request.ReturnsImage ? rendered.Image : null);
    }

    /// <summary>
    /// The window a capture is of, read right before its pixels (roadmap M3.11), as the wrapper reads the capture's
    /// origin then: its process and rectangle. Null when the rectangle cannot be read; the capture then gets no
    /// captureId, because a click could not tell whether the window moved since.
    /// </summary>
    public CapturedWindow? ReadCapturedWindow(nint windowHandle, bool includeFrame)
    {
        if (_captureRegistry == null || _windows.GetWindowRect(windowHandle) is not { } rect)
        {
            return null;
        }

        return new CapturedWindow((long)windowHandle, (int)_windows.GetWindowThreadProcessId(windowHandle),
            WindowRect.FromLTRB(rect.Left, rect.Top, rect.Right, rect.Bottom), includeFrame);
    }

    /// <summary>
    /// <paramref name="window"/> with the client area on screen that a client-area capture shows (its origin and size, read
    /// with the pixels, before any crop), which a click with its captureId checks; a frame capture is left as it is.
    /// </summary>
    private static CapturedWindow? WithClientArea(CapturedWindow? window, CaptureResult capture) =>
        window is { IncludeFrame: false }
            ? window with { ClientArea = new WindowRect(capture.ScreenOriginX, capture.ScreenOriginY, capture.Width, capture.Height) }
            : window;

    /// <summary>
    /// Registers a successful capture (roadmap M3.11) and returns its captureId: a screen capture with
    /// <paramref name="screen"/>, the virtual screen and the monitors as they were read right after its pixels (not as they
    /// are now, which may be seconds later for <see cref="RegisterRaw"/>), a window capture with <paramref name="window"/>.
    /// Null without a registry, for a window capture whose window could not be read, and for a screen capture without its
    /// screen layout: a click could not tell whether the monitors changed since its pixels. <paramref name="signature"/>, the
    /// capture's visual signature (<see cref="SignatureOf"/>), is registered with it.
    /// </summary>
    private string? Register(int width, int height, ImageToScreenTransform transform, CapturedRegion region, bool windowCapture,
        CapturedWindow? window, CapturedScreen? screen, VisualSignature? signature = null)
    {
        if (_captureRegistry == null || (windowCapture ? window == null : screen == null))
        {
            return null;
        }

        var geometry = new CaptureGeometry(width, height, transform, region, window,
            windowCapture ? null : screen!.VirtualScreen, windowCapture ? null : screen!.Monitors);
        return (signature == null ? _captureRegistry.Register(geometry) : _captureRegistry.Register(geometry, signature)).CaptureId;
    }

    /// <summary>
    /// The visual signature of a capture's full-resolution pixels, which wait_for_visual's since compares with (roadmap M6.4);
    /// null unless the registry keeps signatures, which it does only while wait_for_visual is served (owner decision D9), so
    /// the captures cost nothing more otherwise. Null too for a window restoreIfMinimized restored for the capture: it is
    /// minimized again afterwards, so a since on it would read a minimized window. A screen capture's signature leaves out
    /// the windows the policy denies over it (owner decision D8), so a since on it leaves them out even after they closed.
    /// </summary>
    private VisualSignature? SignatureOf(CaptureResult capture, bool windowCapture, CapturedWindow? window)
    {
        if (_captureRegistry is not { KeepsSignatures: true } ||
            (windowCapture && (window == null || _restoredForCapture.ContainsKey((nint)window.Handle))))
        {
            return null;
        }

        var signature = VisualSignature.Of(PixelView.Of(capture));
        if (windowCapture || _capturePolicy == null)
        {
            return signature;
        }

        var masked = new DeniedWindowMask(_windows, _capturePolicy, _targetPolicy)
            .Over(new Rectangle(capture.ScreenOriginX, capture.ScreenOriginY, capture.Width, capture.Height));
        return masked.Count == 0 ? signature : signature.WithExcluded(masked.Select(w => new Rectangle(w.X, w.Y, w.Width, w.Height)).ToList());
    }

    /// <summary>
    /// The virtual screen and the monitors now, which a screen capture reads right after its pixels and registers; null
    /// when captures are not registered.
    /// </summary>
    private CapturedScreen? ReadScreen()
    {
        if (_captureRegistry == null)
        {
            return null;
        }

        var screen = _captureWrapper.GetVirtualScreenBounds();
        return new CapturedScreen(new VirtualScreenBounds(screen.X, screen.Y, screen.Width, screen.Height), ReadMonitors());
    }

    /// <summary>The encoded output image and where it was written.</summary>
    private sealed record RenderedImage(
        EncodedImage Image,
        ImageEncoding Encoding,
        int Width,
        int Height,
        bool IsScaled,
        string? FilePath);

    /// <summary>
    /// Turns raw pixels into the output image: scaling to the size limit, optional overlays drawn on the scaled image (so
    /// they keep their size in pixels of the returned image), encoding, and the file write when the output includes a
    /// file. A failure to build, scale or encode the image is <see cref="ScreenshotErrorCode.EncodeFailed"/>; the file
    /// write reports its own codes.
    /// </summary>
    private static RenderedImage Render(CaptureResult capture, CaptureRequest request, Action<Bitmap>? annotate)
    {
        var (width, height) = request.OutputSize(capture.Width, capture.Height);
        var isScaled = width != capture.Width || height != capture.Height;
        var encoding = request.EncodingFor(width, height);

        byte[] data;
        try
        {
            data = WithBitmap(capture, bitmap =>
            {
                using var scaled = isScaled ? ResizeImage(bitmap, width, height) : null;
                var output = scaled ?? bitmap;
                annotate?.Invoke(output);
                return Encode(output, encoding, request.Quality);
            });
        }
        catch (Exception ex) when (ex is not CaptureException)
        {
            throw new CaptureException(ScreenshotErrorCode.EncodeFailed,
                $"The pixels were captured, but the {CaptureRequest.FormatName(encoding)} image could not be built: {ex.Message}");
        }

        var filePath = request.WritesFile ? WriteFile(data, encoding, request) : null;

        return new RenderedImage(
            new EncodedImage(data, CaptureRequest.MimeType(encoding)),
            encoding,
            width,
            height,
            isScaled,
            filePath);
    }

    /// <summary>The monitors a screen capture registers, or null when they cannot be read (only the virtual screen is compared then).</summary>
    private IReadOnlyList<CapturedMonitor>? ReadMonitors()
    {
        try
        {
            return CapturedMonitors.Of(_monitorService.GetAllMonitors());
        }
        catch (Exception)
        {
            // The capture itself does not depend on them
            return null;
        }
    }

    private static string Describe(WindowRect rect) => $"({rect.X}, {rect.Y}, {rect.Width}x{rect.Height})";

    private static CapturedRegion CapturedRegionOf(CaptureResult capture) =>
        new(capture.ScreenOriginX, capture.ScreenOriginY, capture.Width, capture.Height);

    private static ImageToScreenTransform TransformOf(CaptureResult capture, RenderedImage rendered) =>
        new(
            OffsetX: capture.ScreenOriginX,
            OffsetY: capture.ScreenOriginY,
            ScaleX: Math.Round((double)capture.Width / rendered.Width, 6),
            ScaleY: Math.Round((double)capture.Height / rendered.Height, 6));

    /// <summary>
    /// Runs <paramref name="use"/> on a bitmap over the captured pixels themselves, without copying them: the pixel
    /// array is pinned while the bitmap exists. The bitmap ignores the 4th byte (Format32bppRgb): GDI leaves that byte
    /// undefined (often 0), so reading it as alpha would make parts of a PNG transparent. Overlays drawn on the bitmap
    /// change <see cref="CaptureResult.Bgra"/>.
    /// </summary>
    private static T WithBitmap<T>(CaptureResult capture, Func<Bitmap, T> use)
    {
        var pinned = GCHandle.Alloc(capture.Bgra, GCHandleType.Pinned);
        try
        {
            using var bitmap = new Bitmap(capture.Width, capture.Height, capture.Width * 4, PixelFormat.Format32bppRgb,
                pinned.AddrOfPinnedObject());
            return use(bitmap);
        }
        finally
        {
            pinned.Free();
        }
    }

    internal static Bitmap ResizeImage(Bitmap original, int width, int height)
    {
        var resized = new Bitmap(width, height, PixelFormat.Format32bppRgb);
        try
        {
            using var graphics = Graphics.FromImage(resized);
            graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
            graphics.CompositingQuality = CompositingQuality.HighQuality;
            graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
            graphics.SmoothingMode = SmoothingMode.HighQuality;

            // Without TileFlipXY the bicubic filter samples outside the source and darkens the edges.
            using var attributes = new ImageAttributes();
            attributes.SetWrapMode(WrapMode.TileFlipXY);
            graphics.DrawImage(
                original,
                new Rectangle(0, 0, width, height),
                0, 0, original.Width, original.Height,
                GraphicsUnit.Pixel,
                attributes);
            return resized;
        }
        catch
        {
            resized.Dispose();
            throw;
        }
    }

    internal static byte[] Encode(Bitmap bitmap, ImageEncoding encoding, int quality)
    {
        using var stream = new MemoryStream();

        if (encoding == ImageEncoding.Png)
        {
            bitmap.Save(stream, ImageFormat.Png);
        }
        else
        {
            var encoder = ImageCodecInfo.GetImageEncoders().First(c => c.FormatID == ImageFormat.Jpeg.Guid);
            using var encoderParameters = new EncoderParameters(1);
            encoderParameters.Param[0] = new EncoderParameter(Encoder.Quality, (long)Math.Clamp(quality, 1, 100));
            bitmap.Save(stream, encoder, encoderParameters);
        }

        return stream.ToArray();
    }

    /// <summary>
    /// Writes the encoded image to the requested path, or to a new file in
    /// <see cref="DefaultCaptureFolder"/>. An existing file is replaced only with overwrite; the
    /// folder of an explicit path must already exist.
    /// </summary>
    private static string WriteFile(byte[] data, ImageEncoding encoding, CaptureRequest request)
    {
        var path = request.OutputPath;
        if (path == null)
        {
            var timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture);
            path = Path.Combine(DefaultCaptureFolder, $"screenshot_{timestamp}_{Guid.NewGuid():N}{CaptureRequest.Extension(encoding)}");
        }

        var mode = request.Overwrite && request.OutputPath != null ? FileMode.Create : FileMode.CreateNew;
        try
        {
            if (request.OutputPath == null)
            {
                // The server's own folder; explicit paths must name an existing folder.
                Directory.CreateDirectory(DefaultCaptureFolder);
            }

            using var stream = new FileStream(path, mode, FileAccess.Write, FileShare.None);
            stream.Write(data);
        }
        catch (IOException) when (mode == FileMode.CreateNew && File.Exists(path))
        {
            // Created between the check in CaptureRequest and this write.
            throw CaptureRequest.FileExists(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new CaptureException(ScreenshotErrorCode.FileWriteFailed, $"Could not write '{path}': {ex.Message}");
        }

        return path;
    }

    /// <summary>
    /// Scales ImageBounds from the full-resolution capture to the output image. ScreenBounds stay in
    /// screen pixels.
    /// </summary>
    private static List<AnnotatedElement> ScaleAnnotations(
        IReadOnlyList<AnnotatedElement> annotations,
        int finalWidth,
        int finalHeight,
        int originalWidth,
        int originalHeight)
    {
        var scaleX = (double)finalWidth / originalWidth;
        var scaleY = (double)finalHeight / originalHeight;

        return annotations.Select(annotation =>
        {
            var x = ClampToImage(RoundScaled(annotation.ImageBounds.X, scaleX), finalWidth);
            var y = ClampToImage(RoundScaled(annotation.ImageBounds.Y, scaleY), finalHeight);
            var width = ScaleLength(annotation.ImageBounds.Width, scaleX, finalWidth - x);
            var height = ScaleLength(annotation.ImageBounds.Height, scaleY, finalHeight - y);
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

    /// <summary>
    /// The marks of <paramref name="elements"/> and their children, in reading order (an element, then the elements inside
    /// it), with their positions in the full-resolution capture (clipped to it) and their colors. Elements smaller than 2
    /// pixels, without bounds or outside the image get no mark; their children still can.
    /// </summary>
    private static void CollectAnnotations(
        IReadOnlyList<UIElementInfo> elements,
        int originX,
        int originY,
        int imageWidth,
        int imageHeight,
        bool highlightInteractable,
        List<(AnnotatedElement Element, Color Color)> annotations)
    {
        foreach (var element in elements)
        {
            if (element.Bounds is { Width: >= 2, Height: >= 2 } screenBounds)
            {
                var visible = Rectangle.Intersect(
                    new Rectangle(screenBounds.X - originX, screenBounds.Y - originY, screenBounds.Width, screenBounds.Height),
                    new Rectangle(0, 0, imageWidth, imageHeight));

                if (visible.Width > 0 && visible.Height > 0)
                {
                    var (color, colorName) = GetOverlayColor(element.ControlType, element.IsEnabled, highlightInteractable);
                    annotations.Add((new AnnotatedElement(
                        Name: element.Name,
                        ControlType: element.ControlType,
                        AutomationId: element.AutomationId,
                        ImageBounds: new BoundsDto(visible.X, visible.Y, visible.Width, visible.Height),
                        ScreenBounds: screenBounds,
                        IsEnabled: element.IsEnabled,
                        OverlayColor: colorName,
                        Ref: element.Ref), color));
                }
            }

            if (element.Children != null)
            {
                CollectAnnotations(element.Children, originX, originY, imageWidth, imageHeight, highlightInteractable, annotations);
            }
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

    private static ScreenshotResult Failure(ScreenshotErrorCode code, string error) =>
        new(
            Success: false,
            ImageFormat: null,
            MimeType: null,
            Width: 0,
            Height: 0,
            CapturedRegion: null,
            ImageToScreen: null,
            MonitorIndex: null,
            ScaleFactor: null,
            Error: error,
            ErrorCode: code.ToString());

    private static AnnotatedScreenshotResult AnnotatedFailure(
        nint windowHandle,
        string? windowTitle,
        ScreenshotErrorCode code,
        string error) =>
        new(
            Success: false,
            ImageFormat: null,
            MimeType: null,
            Width: 0,
            Height: 0,
            WindowHandle: (long)windowHandle,
            WindowTitle: windowTitle,
            CapturedRegion: null,
            ImageToScreen: null,
            Elements: null,
            ElementCount: 0,
            CaptureElapsedMs: 0,
            AnnotationElapsedMs: 0,
            Error: error,
            ErrorCode: code.ToString());
}
