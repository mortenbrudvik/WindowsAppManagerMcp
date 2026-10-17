namespace WindowsAppManagerMcp.Services.Interfaces;

/// <summary>
/// Windows.Graphics.Capture for one window (roadmap M7.1): the window as the desktop compositor (DWM) shows it, read from
/// one captured frame. It sees content PrintWindow returns black (DirectComposition, WinUI, UWP and GPU-drawn windows), and
/// it needs nothing from the window's own thread, so a window that does not answer is still captured as it was last drawn,
/// without the windows that cover it. Behind a seam the tests replace with a fake; only reads.
/// </summary>
public interface IGraphicsCaptureWrapper
{
    /// <summary>Method name of its captures (<see cref="CaptureResult.Method"/>).</summary>
    public const string Method = "WindowsGraphicsCapture";

    /// <summary>
    /// Whether this Windows can capture windows this way (Windows 10 1903 and later, with a Direct3D 11 device); false also
    /// once a capture missed its deadline and has not returned.
    /// </summary>
    bool IsSupported { get; }

    /// <summary>
    /// One frame of <paramref name="windowHandle"/>, a top-level window: its visible frame (DWMWA_EXTENDED_FRAME_BOUNDS), or
    /// with <paramref name="includeFrame"/> false its client area, with the screen origin of the top-left pixel and method
    /// <see cref="Method"/>. A minimized window has no frames.
    /// </summary>
    /// <exception cref="GraphicsCaptureException">The window could not be captured this way; the message says why.</exception>
    CaptureResult CaptureWindow(nint windowHandle, bool includeFrame);
}

/// <summary>A window could not be captured with Windows.Graphics.Capture (<see cref="IGraphicsCaptureWrapper"/>).</summary>
public sealed class GraphicsCaptureException(string message, Exception? inner = null) : Exception(message, inner);
