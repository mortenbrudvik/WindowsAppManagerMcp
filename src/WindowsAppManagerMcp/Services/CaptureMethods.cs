using WindowsAppManagerMcp.Services.Interfaces;

namespace WindowsAppManagerMcp.Services;

/// <summary>
/// The ways a capture reads pixels (<see cref="CaptureResult.Method"/>, a result's captureMethod), and which of them can show
/// windows that cover the captured window. The readers that are not capture tools (OCR, wait_for_visual) black out or leave
/// out the windows the policy denies (owner decision D8) wherever the pixels can show them; they decide it here, so they
/// decide it the same way.
/// </summary>
public static class CaptureMethods
{
    /// <summary>Copied from the screen: what is on screen there, covering windows included.</summary>
    public const string ScreenBitBlt = "ScreenBitBlt";

    /// <summary>The window drew itself (PrintWindow): covering windows do not show.</summary>
    public const string PrintWindow = "PrintWindow";

    /// <summary>The window refused PrintWindow, so its device context was copied: covering windows can show.</summary>
    public const string BitBltFallback = "BitBltFallback";

    /// <summary>
    /// The desktop compositor's frame of the window (Windows.Graphics.Capture, roadmap M7.1): the window's own content, made
    /// for one GraphicsCaptureItem of that window, so covering windows do not show.
    /// </summary>
    public const string WindowsGraphicsCapture = IGraphicsCaptureWrapper.Method;

    /// <summary>
    /// True when pixels read by <paramref name="method"/> show only the captured window's own content: PrintWindow and
    /// Windows.Graphics.Capture. Every other method, a screen copy, the device-context copy of BitBltFallback, and a method
    /// this server does not know, can show the windows that cover it, so the denied ones are masked (fail closed).
    /// </summary>
    public static bool ShowsOnlyTheWindow(string? method) => method is PrintWindow or WindowsGraphicsCapture;
}
