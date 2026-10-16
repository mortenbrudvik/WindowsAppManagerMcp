using WindowsAppManagerMcp.Models;

namespace WindowsAppManagerMcp.Services.Interfaces;

/// <summary>
/// What <see cref="IScreenshotService.CapturePixels"/> captures (roadmap M6): a monitor, a screen region or a window, as
/// take_screenshot, capture_region and capture_window name them.
/// </summary>
public abstract record CaptureTarget
{
    private CaptureTarget()
    {
    }

    /// <summary>A monitor by its index, or <see cref="ScreenshotService.VirtualScreenIndex"/> for every monitor in one image.</summary>
    public sealed record Monitor(int Index) : CaptureTarget;

    /// <summary>A rectangle of the screen in physical virtual-screen pixels; the part off the screen is cut off.</summary>
    public sealed record Region(int X, int Y, int Width, int Height) : CaptureTarget;

    /// <summary>A window, with its visible frame or only its client area.</summary>
    public sealed record Window(nint Handle, bool IncludeFrame) : CaptureTarget;
}

/// <summary>
/// The raw pixels of a capture (roadmap M6, for OCR and visual change detection): what a capture tool would have encoded,
/// with the same refusals, warnings, crop and origin, but never scaled, encoded, written or registered.
/// <see cref="IScreenshotService.RegisterRaw"/> registers it when the caller hands out image points.
/// </summary>
/// <param name="Target">What was captured.</param>
/// <param name="Pixels">The pixels at full resolution, with their screen origin and capture method.</param>
/// <param name="MonitorIndex">The monitor of the capture, as the capture tools report it; null when there is none.</param>
/// <param name="ScaleFactor">That monitor's DPI scale, for information only, as the capture tools report it.</param>
/// <param name="Warnings">The capture tools' warnings, each starting with its code (ImageBlank, WindowNotResponding, ...).</param>
/// <param name="Window">
/// For a window, the window as it was read before its pixels (process and rectangle, and the client area without the
/// frame), which <see cref="IScreenshotService.RegisterRaw"/> registers; null for a screen capture, and when the window could
/// not be read or captures are not registered.
/// </param>
/// <param name="Screen">
/// For a monitor, virtual-screen or region capture, the virtual screen and the monitors as they were read right after
/// the pixels, which <see cref="IScreenshotService.RegisterRaw"/> registers: a monitor change during the work between
/// the capture and its registration (an OCR pass) is then still caught as StaleCapture. Null for a window capture, and
/// when captures are not registered.
/// </param>
public sealed record RawCapture(
    CaptureTarget Target,
    CaptureResult Pixels,
    int? MonitorIndex,
    double? ScaleFactor,
    IReadOnlyList<string> Warnings,
    CapturedWindow? Window,
    CapturedScreen? Screen = null)
{
    /// <summary>The screen rectangle the pixels show.</summary>
    public CapturedRegion CapturedRegion => new(Pixels.ScreenOriginX, Pixels.ScreenOriginY, Pixels.Width, Pixels.Height);
}
