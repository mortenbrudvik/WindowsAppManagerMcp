namespace WindowsAppManagerMcp.Services.Interfaces;

public interface IScreenCaptureWrapper
{
    /// <summary>
    /// Captures a region of the screen.
    /// </summary>
    /// <param name="x">X coordinate of the region</param>
    /// <param name="y">Y coordinate of the region</param>
    /// <param name="width">Width of the region</param>
    /// <param name="height">Height of the region</param>
    /// <returns>Raw pixel data in BGRA format</returns>
    byte[] CaptureScreenRegion(int x, int y, int width, int height);

    /// <summary>
    /// Captures a window's content.
    /// </summary>
    /// <param name="windowHandle">Handle to the window</param>
    /// <param name="includeFrame">Whether to include the window frame/decoration</param>
    /// <returns>Raw pixel data in BGRA format, width, and height</returns>
    (byte[] Data, int Width, int Height) CaptureWindow(nint windowHandle, bool includeFrame);

    /// <summary>
    /// Gets the bounds of the virtual screen (all monitors combined).
    /// </summary>
    /// <returns>X, Y, Width, Height of the virtual screen</returns>
    (int X, int Y, int Width, int Height) GetVirtualScreenBounds();

    /// <summary>
    /// Validates that a window handle is valid and the window exists.
    /// </summary>
    bool IsValidWindow(nint windowHandle);
}
