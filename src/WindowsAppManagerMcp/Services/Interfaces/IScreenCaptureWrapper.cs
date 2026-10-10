namespace WindowsAppManagerMcp.Services.Interfaces;

/// <summary>
/// Raw pixels from the screen or a window, and where they came from.
/// </summary>
/// <param name="Bgra">Top-down 32-bit pixels, 4 bytes per pixel (blue, green, red, then an undefined byte).
/// The 4th byte is not alpha: GDI leaves it unset, so it must not be read as transparency.</param>
/// <param name="Width">Width in pixels.</param>
/// <param name="Height">Height in pixels.</param>
/// <param name="ScreenOriginX">Screen X (physical virtual-screen pixels) of the top-left pixel. Can be negative.</param>
/// <param name="ScreenOriginY">Screen Y (physical virtual-screen pixels) of the top-left pixel. Can be negative.</param>
/// <param name="Method">How the pixels were read: "BitBlt" (from the screen or window DC) or "PrintWindow".</param>
public sealed record CaptureResult(
    byte[] Bgra,
    int Width,
    int Height,
    int ScreenOriginX,
    int ScreenOriginY,
    string Method);

public interface IScreenCaptureWrapper
{
    /// <summary>
    /// Captures a region of the screen.
    /// </summary>
    /// <param name="x">X coordinate of the region</param>
    /// <param name="y">Y coordinate of the region</param>
    /// <param name="width">Width of the region</param>
    /// <param name="height">Height of the region</param>
    /// <returns>The pixels, with the screen origin (<paramref name="x"/>, <paramref name="y"/>).</returns>
    CaptureResult CaptureScreenRegion(int x, int y, int width, int height);

    /// <summary>
    /// Captures a window's content, even when other windows cover it.
    /// </summary>
    /// <param name="windowHandle">Handle to the window</param>
    /// <param name="includeFrame">Whether to include the window frame/decoration</param>
    /// <returns>
    /// The pixels and the screen position of the top-left pixel: the window rectangle's corner
    /// (GetWindowRect) with the frame, or the client area's corner (ClientToScreen of 0,0) without it.
    /// </returns>
    CaptureResult CaptureWindow(nint windowHandle, bool includeFrame);

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
