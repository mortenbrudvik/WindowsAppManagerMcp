using WindowsAppManagerMcp.Models;

namespace WindowsAppManagerMcp.Services.Interfaces;

/// <summary>
/// Captures monitors, regions and windows. Every capture returns its metadata (size, the captured
/// screen rectangle with its real origin, the image-to-screen transform, file path) and, when the
/// output includes it, the encoded image. Options are checked first (see <see cref="CaptureRequest"/>);
/// an invalid option or a failed capture returns metadata with success false and an error code.
/// </summary>
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
    /// <param name="options">Format, size limit, output mode and file options.</param>
    ScreenCapture<ScreenshotResult> CaptureMonitor(int monitorIndex, CaptureOptions options);

    /// <summary>
    /// Captures a region of the screen.
    /// </summary>
    /// <param name="x">X coordinate of the region</param>
    /// <param name="y">Y coordinate of the region</param>
    /// <param name="width">Width of the region</param>
    /// <param name="height">Height of the region</param>
    /// <param name="options">Format, size limit, output mode and file options.</param>
    ScreenCapture<ScreenshotResult> CaptureRegion(int x, int y, int width, int height, CaptureOptions options);

    /// <summary>
    /// Captures a specific window. The captured region's origin is the visible frame's corner with the frame
    /// (DWMWA_EXTENDED_FRAME_BOUNDS, without the invisible resize borders), or the client area's corner without it.
    /// A window that cannot be captured right now (see <see cref="CheckWindow"/>) is refused with its code; a window
    /// that does not answer is copied from the screen with a warning.
    /// </summary>
    /// <param name="windowHandle">Handle to the window</param>
    /// <param name="includeFrame">Whether to include the window frame/decoration</param>
    /// <param name="options">Format, size limit, output mode and file options.</param>
    ScreenCapture<ScreenshotResult> CaptureWindow(nint windowHandle, bool includeFrame, CaptureOptions options);

    /// <summary>
    /// Captures part of a window (capture_window with a ref, roadmap M3.11): the window is captured as by
    /// <see cref="CaptureWindow"/> (so covering windows do not show), then cut to <paramref name="area"/> clipped to the
    /// captured window. The capture is registered with the crop's origin and the window, so an image point of the crop
    /// converts to the screen and is refused once the window moved. An area outside the captured window (or its client
    /// area without the frame) fails with ElementOffscreen.
    /// </summary>
    /// <param name="windowHandle">Handle to the window.</param>
    /// <param name="includeFrame">Whether the frame is part of what the area is clipped to.</param>
    /// <param name="area">The area in screen pixels: an element's bounds and padding.</param>
    /// <param name="options">Format, size limit, output mode and file options.</param>
    ScreenCapture<ScreenshotResult> CaptureWindowArea(nint windowHandle, bool includeFrame, BoundsDto area, CaptureOptions options);

    /// <summary>
    /// Captures a window and draws its UI elements on it as marks (roadmap M3.11): a box and a filled tag with the element's
    /// ref, drawn on the returned image after it was scaled. Marks are placed relative to the capture's own screen origin, so
    /// they line up with or without the frame. The tags leave out the window part the refs share, which the result reports
    /// as <see cref="AnnotatedScreenshotResult.MarkPrefix"/> (roadmap M3.F38).
    /// </summary>
    /// <param name="windowHandle">Handle to the window</param>
    /// <param name="windowTitle">The window title to report, or null</param>
    /// <param name="elements">UI elements to draw overlays for, with screen bounds</param>
    /// <param name="includeFrame">Whether to include the window frame/decoration</param>
    /// <param name="overlays">Whether to draw the marks, their tags, and dimmed boxes for disabled elements.</param>
    /// <param name="options">Format, size limit, output mode and file options. ImageBounds follow the output image.</param>
    /// <param name="walkedWindow">
    /// The window as <see cref="ReadCapturedWindow"/> read it before <paramref name="elements"/> were read, or null. When the
    /// window has moved or resized since, the elements' screen bounds are no longer where the image shows them: the
    /// capture then has no captureId (a click at a mark would land beside its element) and a WindowMoved warning, and no
    /// marks unless <see cref="ElementOverlayOptions.MarkMovedWindow"/>.
    /// </param>
    ScreenCapture<AnnotatedScreenshotResult> CaptureWindowWithElements(
        nint windowHandle,
        string? windowTitle,
        IReadOnlyList<UIElementInfo> elements,
        bool includeFrame,
        ElementOverlayOptions overlays,
        CaptureOptions options,
        CapturedWindow? walkedWindow = null);

    /// <summary>
    /// The raw pixels of <paramref name="target"/> (roadmap M6, for OCR and visual change detection), captured as
    /// <see cref="CaptureMonitor"/>, <see cref="CaptureRegion"/> and <see cref="CaptureWindow"/> capture them: the same
    /// refusals, warnings, crop to the visible frame, screen-copy fallback for a window that does not answer, and origin,
    /// but at full resolution and never scaled, encoded, written to a file or registered. A window is first checked
    /// against the user's target policy (<see cref="ICaptureTargetPolicy"/>), as capture_window checks it; a service built
    /// without that check refuses every window. A minimized window is refused (WindowMinimized): nothing is restored.
    /// </summary>
    /// <param name="target">The monitor, region or window.</param>
    /// <exception cref="CaptureException">
    /// The capture was refused or failed, with the capture tools' code: TargetDeniedByPolicy, InvalidHandle, WindowHidden,
    /// Cloaked, WindowMinimized, InvalidMonitorIndex, InvalidRegion, RegionOffScreen or CaptureFailed.
    /// </exception>
    RawCapture CapturePixels(CaptureTarget target);

    /// <summary>
    /// Registers a capture of <see cref="CapturePixels"/> under a captureId (roadmap M3.11), with its full-resolution size
    /// and a 1:1 image-to-screen transform from its origin: an image point (x, y) of its pixels is screen point
    /// (origin x + x, origin y + y), and the mouse tools refuse it with StaleCapture once a captured window moved, or
    /// once the monitors changed since the pixels were read (<see cref="RawCapture.Screen"/>, read with the pixels, so
    /// slow work between the capture and this call does not hide a change made during it). Null when captures are not
    /// registered, for a window that could not be read, or for a screen capture without its <see cref="RawCapture.Screen"/>.
    /// </summary>
    string? RegisterRaw(RawCapture capture);

    /// <summary>
    /// The window's process and rectangle now, as a capture of it registers them (roadmap M3.11); null when the rectangle
    /// cannot be read, or when captures are not registered. capture_with_elements reads it before the UI element walk.
    /// </summary>
    /// <param name="windowHandle">Handle to the window.</param>
    /// <param name="includeFrame">Whether the capture includes the frame.</param>
    CapturedWindow? ReadCapturedWindow(nint windowHandle, bool includeFrame);

    /// <summary>
    /// Why the window cannot be captured right now, or null when it can: the handle is not a window
    /// (InvalidHandle), the window or its top-level window is minimized (WindowMinimized), the window or a parent
    /// of it is hidden (WindowHidden), or DWM cloaks it (Cloaked). Only reads the window; captures run it first.
    /// </summary>
    CaptureRefusal? CheckWindow(nint windowHandle);

    /// <summary>
    /// The window that restoreIfMinimized would restore: the top-level window of <paramref name="windowHandle"/>
    /// when that is minimized, or <paramref name="windowHandle"/> itself when it is; null when neither is minimized.
    /// </summary>
    nint? FindMinimizedWindow(nint windowHandle);

    /// <summary>
    /// The windows that restoring <paramref name="minimizedWindow"/> puts on screen: the window itself and every
    /// top-level window it owns (Windows hides those while their owner is minimized). The caller checks the target
    /// policy for each before it restores anything.
    /// </summary>
    IReadOnlyList<nint> WindowsShownByRestore(nint minimizedWindow);

    /// <summary>
    /// Restores <paramref name="minimizedWindow"/> without activating it (SW_SHOWNOACTIVATE) and waits until
    /// <paramref name="captureWindow"/> shows two equal frames, so the capture does not get a half-painted window.
    /// Disposing the result minimizes the window again without activating another one (SW_SHOWMINNOACTIVE). The
    /// caller holds the input gate for the whole time: the restored window can cover a point another call clicks.
    /// </summary>
    MinimizedWindowRestore RestoreForCapture(nint minimizedWindow, nint captureWindow, bool includeFrame);
}

/// <summary>Why a window cannot be captured: the error code and a message that says what to do.</summary>
public sealed record CaptureRefusal(ScreenshotErrorCode Code, string Message);
