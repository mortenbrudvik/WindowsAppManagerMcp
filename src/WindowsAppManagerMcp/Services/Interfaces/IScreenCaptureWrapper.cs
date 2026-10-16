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
/// <param name="Method">How the pixels were read: "ScreenBitBlt" (copied from the screen, covering windows included),
/// "PrintWindow" (the window drew itself) or "BitBltFallback" (the window refused PrintWindow, so its device context
/// was copied).</param>
public sealed record CaptureResult(
    byte[] Bgra,
    int Width,
    int Height,
    int ScreenOriginX,
    int ScreenOriginY,
    string Method);

/// <summary>
/// What <see cref="IScreenCaptureWrapper.WithPixels{T}"/> reads: a screen rectangle, a window that draws itself, or a
/// window's rectangle copied from the screen. Each reads the same pixels, with the same origin and method, as
/// <see cref="IScreenCaptureWrapper.CaptureScreenRegion"/>, <see cref="IScreenCaptureWrapper.CaptureWindow"/> and
/// <see cref="IScreenCaptureWrapper.CaptureWindowFromScreen"/>.
/// </summary>
public abstract record PixelSource
{
    private PixelSource()
    {
    }

    /// <summary>A rectangle of the screen in physical virtual-screen pixels (method "ScreenBitBlt").</summary>
    public sealed record Screen(int X, int Y, int Width, int Height) : PixelSource;

    /// <summary>A window that draws itself with PrintWindow, even when other windows cover it.</summary>
    public sealed record Window(nint Handle, bool IncludeFrame) : PixelSource;

    /// <summary>A window's rectangle copied from the screen without asking the window to draw (a window that does not answer).</summary>
    public sealed record WindowFromScreen(nint Handle, bool IncludeFrame) : PixelSource;
}

/// <summary>
/// Pixels lent for the length of one <see cref="PixelReader{T}"/> call, without a copy: top-down rows of 4 bytes per pixel
/// (blue, green, red, then an undefined byte that is not alpha), <see cref="Stride"/> bytes apart. The memory belongs to
/// whoever lent it and is gone once the reader returns, so a reader keeps what it computed, never the span; it calls
/// <see cref="ToArray"/> or <see cref="ToCaptureResult"/> when it does need the pixels afterwards.
/// </summary>
public readonly ref struct PixelView
{
    /// <exception cref="ArgumentOutOfRangeException">A negative size, a stride shorter than a row, or a buffer shorter than the rows.</exception>
    public PixelView(ReadOnlySpan<byte> bgra, int width, int height, int stride, int screenOriginX, int screenOriginY, string method)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(width);
        ArgumentOutOfRangeException.ThrowIfNegative(height);
        if (stride < width * 4L)
        {
            throw new ArgumentOutOfRangeException(nameof(stride), stride, $"A row of {width} pixels needs {width * 4L} bytes.");
        }

        if (width > 0 && height > 0 && bgra.Length < (long)(height - 1) * stride + width * 4L)
        {
            throw new ArgumentOutOfRangeException(nameof(bgra), bgra.Length,
                $"{width}x{height} pixels {stride} bytes apart need {(long)(height - 1) * stride + width * 4L} bytes.");
        }

        Bgra = bgra;
        Width = width;
        Height = height;
        Stride = stride;
        ScreenOriginX = screenOriginX;
        ScreenOriginY = screenOriginY;
        Method = method ?? throw new ArgumentNullException(nameof(method));
    }

    /// <summary>The rows, <see cref="Stride"/> bytes apart; the last row may end without padding.</summary>
    public ReadOnlySpan<byte> Bgra { get; }

    /// <summary>Width in pixels.</summary>
    public int Width { get; }

    /// <summary>Height in pixels.</summary>
    public int Height { get; }

    /// <summary>Bytes from the start of one row to the start of the next: at least <see cref="Width"/> * 4.</summary>
    public int Stride { get; }

    /// <summary>Screen X (physical virtual-screen pixels) of the top-left pixel.</summary>
    public int ScreenOriginX { get; }

    /// <summary>Screen Y (physical virtual-screen pixels) of the top-left pixel.</summary>
    public int ScreenOriginY { get; }

    /// <summary>How the pixels were read, as <see cref="CaptureResult.Method"/>.</summary>
    public string Method { get; }

    /// <summary>The pixels of a <see cref="CaptureResult"/>, lent without a copy (rows are <c>Width * 4</c> bytes apart).</summary>
    public static PixelView Of(CaptureResult capture)
    {
        ArgumentNullException.ThrowIfNull(capture);
        return new PixelView(capture.Bgra, capture.Width, capture.Height, capture.Width * 4, capture.ScreenOriginX,
            capture.ScreenOriginY, capture.Method);
    }

    /// <summary>Row <paramref name="y"/>: <see cref="Width"/> * 4 bytes.</summary>
    public ReadOnlySpan<byte> Row(int y)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(y);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(y, Height);
        return Bgra.Slice(y * Stride, Width * 4);
    }

    /// <summary>
    /// The part of the view at screen rectangle (<paramref name="screenX"/>, <paramref name="screenY"/>,
    /// <paramref name="width"/> x <paramref name="height"/>), which must lie inside it, as a view of the same memory: no
    /// pixel is copied, and the stride stays the same.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">The rectangle is not inside the view.</exception>
    public PixelView Crop(int screenX, int screenY, int width, int height)
    {
        var left = (long)screenX - ScreenOriginX;
        var top = (long)screenY - ScreenOriginY;
        if (width < 0 || height < 0 || left < 0 || top < 0 || left + width > Width || top + height > Height)
        {
            throw new ArgumentOutOfRangeException(nameof(screenX),
                $"({screenX}, {screenY}, {width}x{height}) is not inside the pixels at ({ScreenOriginX}, {ScreenOriginY}, {Width}x{Height}).");
        }

        if (width == 0 || height == 0)
        {
            return new PixelView(ReadOnlySpan<byte>.Empty, width, height, Stride, screenX, screenY, Method);
        }

        var start = (int)(top * Stride + left * 4);
        var length = (height - 1) * Stride + width * 4;
        return new PixelView(Bgra.Slice(start, length), width, height, Stride, screenX, screenY, Method);
    }

    /// <summary>A copy of the pixels with rows <see cref="Width"/> * 4 bytes apart, as <see cref="CaptureResult.Bgra"/>.</summary>
    public byte[] ToArray()
    {
        // An empty crop keeps its parent's stride over no memory, so its rows cannot be read
        if (Width == 0 || Height == 0)
        {
            return [];
        }

        var rowBytes = Width * 4;
        var pixels = GC.AllocateUninitializedArray<byte>(rowBytes * Height);
        if (Stride == rowBytes)
        {
            Bgra[..pixels.Length].CopyTo(pixels);
            return pixels;
        }

        for (var y = 0; y < Height; y++)
        {
            Row(y).CopyTo(pixels.AsSpan(y * rowBytes, rowBytes));
        }

        return pixels;
    }

    /// <summary>A copy of the pixels with their origin and method.</summary>
    public CaptureResult ToCaptureResult() => new(ToArray(), Width, Height, ScreenOriginX, ScreenOriginY, Method);
}

/// <summary>
/// Reads lent pixels and returns what it computed from them. A delegate of its own rather than <c>Func&lt;PixelView, T&gt;</c>:
/// a ref struct cannot be a type argument before C# 13.
/// </summary>
public delegate T PixelReader<out T>(PixelView pixels);

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
    /// (GetWindowRect) with the frame, or the client area's top-left corner on screen without it
    /// (MapWindowPoints of the client rectangle, which is also right for mirrored right-to-left windows).
    /// </returns>
    CaptureResult CaptureWindow(nint windowHandle, bool includeFrame);

    /// <summary>
    /// Copies a window's rectangle from the screen (method "ScreenBitBlt") without asking the window to draw: for a
    /// window whose thread does not answer, where PrintWindow would block. Windows that cover it show in the image.
    /// </summary>
    /// <param name="windowHandle">Handle to the window</param>
    /// <param name="includeFrame">Whether to include the window frame/decoration</param>
    /// <returns>The pixels, with the same screen origin as <see cref="CaptureWindow"/>.</returns>
    CaptureResult CaptureWindowFromScreen(nint windowHandle, bool includeFrame);

    /// <summary>
    /// Gets the bounds of the virtual screen (all monitors combined).
    /// </summary>
    /// <returns>X, Y, Width, Height of the virtual screen</returns>
    (int X, int Y, int Width, int Height) GetVirtualScreenBounds();

    /// <summary>
    /// Validates that a window handle is valid and the window exists.
    /// </summary>
    bool IsValidWindow(nint windowHandle);

    /// <summary>
    /// Reads <paramref name="source"/>'s pixels and lends them to <paramref name="read"/> without copying them (roadmap M6,
    /// for polling): the reader runs on the calling thread, once, while the pixels exist, and its result is returned.
    /// The pixels, origin and method are those the matching Capture member returns, and the same sizes are refused
    /// (<see cref="ArgumentException"/>) before anything is allocated. Only the copy is left out, so a reader that needs
    /// the pixels afterwards copies them itself (<see cref="PixelView.ToArray"/>). Nothing here checks the target policy
    /// or whether a window can be captured: a caller checks first (ICaptureTargetPolicy, IScreenshotService.CheckWindow),
    /// and runs a window source, which waits for the window's thread, with a deadline.
    /// </summary>
    /// <param name="source">What to read.</param>
    /// <param name="read">What to compute from the pixels; it must not keep the span.</param>
    T WithPixels<T>(PixelSource source, PixelReader<T> read);
}
