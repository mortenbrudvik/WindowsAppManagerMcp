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
    /// Whether a window can be captured this way now: <see cref="GraphicsCaptureAvailability.Unsupported"/> when this Windows
    /// cannot (Windows 10 1903 and later, with a Direct3D 11 device, can), and <see cref="GraphicsCaptureAvailability.Busy"/>
    /// or <see cref="GraphicsCaptureAvailability.Unknown"/> when it cannot be asked right now, which a later call may change.
    /// </summary>
    GraphicsCaptureAvailability Availability { get; }

    /// <summary>
    /// One frame of <paramref name="windowHandle"/>, a top-level window: its visible frame (DWMWA_EXTENDED_FRAME_BOUNDS), or
    /// with <paramref name="includeFrame"/> false its client area, with the screen origin of the top-left pixel and method
    /// <see cref="Method"/>. A minimized window has no frames.
    /// </summary>
    /// <exception cref="GraphicsCaptureException">The window could not be captured this way; the message says why.</exception>
    CaptureResult CaptureWindow(nint windowHandle, bool includeFrame);
}

/// <summary>
/// Whether Windows.Graphics.Capture can capture a window now (<see cref="IGraphicsCaptureWrapper.Availability"/>). Only
/// <see cref="Unsupported"/> says this computer lacks it; <see cref="Busy"/> and <see cref="Unknown"/> pass, so an agent is
/// told to try again rather than that it needs another Windows.
/// </summary>
public enum GraphicsCaptureAvailability
{
    /// <summary>A window can be captured this way.</summary>
    Available,

    /// <summary>This Windows cannot capture windows this way (before Windows 10 1903, or no Direct3D 11 device); asking again does not help.</summary>
    Unsupported,

    /// <summary>
    /// An earlier Windows.Graphics.Capture call missed its deadline and has not returned (a stuck graphics driver), so no new
    /// one is started until it does.
    /// </summary>
    Busy,

    /// <summary>Whether this Windows can do it is not known yet: the check missed its deadline or failed (a busy machine).</summary>
    Unknown
}

/// <summary>What the capture tools say about a <see cref="GraphicsCaptureAvailability"/> other than Available.</summary>
public static class GraphicsCaptureAvailabilityText
{
    /// <summary>Why a window was not captured with Windows.Graphics.Capture, and whether trying again can help; empty for Available.</summary>
    public static string Why(GraphicsCaptureAvailability availability) => availability switch
    {
        GraphicsCaptureAvailability.Available => string.Empty,
        GraphicsCaptureAvailability.Busy =>
            "An earlier Windows.Graphics.Capture call has not returned (the graphics driver may be stuck or busy), so no new one is " +
            "started until it does; try again in a few seconds.",
        GraphicsCaptureAvailability.Unknown =>
            "Whether this computer supports Windows.Graphics.Capture could not be checked in time (the machine may be busy); try " +
            "again in a few seconds.",
        _ => "Windows.Graphics.Capture is not available on this computer (it needs Windows 10 1903 or later and Direct3D 11)."
    };
}

/// <summary>A window could not be captured with Windows.Graphics.Capture (<see cref="IGraphicsCaptureWrapper"/>).</summary>
public sealed class GraphicsCaptureException(string message, Exception? inner = null) : Exception(message, inner);
