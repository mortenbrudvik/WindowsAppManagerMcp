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
    /// Captures a specific window. The captured region's origin is the window rectangle's corner with
    /// the frame, or the client area's corner without it.
    /// </summary>
    /// <param name="windowHandle">Handle to the window</param>
    /// <param name="includeFrame">Whether to include the window frame/decoration</param>
    /// <param name="options">Format, size limit, output mode and file options.</param>
    ScreenCapture<ScreenshotResult> CaptureWindow(nint windowHandle, bool includeFrame, CaptureOptions options);

    /// <summary>
    /// Captures a window and draws UI element bounding boxes on it. Overlays are placed relative to the
    /// capture's own screen origin, so they line up with or without the frame.
    /// </summary>
    /// <param name="windowHandle">Handle to the window</param>
    /// <param name="windowTitle">The window title to report, or null</param>
    /// <param name="elements">UI elements to draw overlays for, with screen bounds</param>
    /// <param name="includeFrame">Whether to include the window frame/decoration</param>
    /// <param name="highlightInteractable">Use brighter colors for enabled/interactable elements</param>
    /// <param name="options">Format, size limit, output mode and file options. ImageBounds follow the output image.</param>
    ScreenCapture<AnnotatedScreenshotResult> CaptureWindowWithElements(
        nint windowHandle,
        string? windowTitle,
        IReadOnlyList<UIElementInfo> elements,
        bool includeFrame,
        bool highlightInteractable,
        CaptureOptions options);
}
