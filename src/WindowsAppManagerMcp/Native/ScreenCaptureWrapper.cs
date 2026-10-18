using System.Runtime.InteropServices;
using WindowsAppManagerMcp.Services;
using WindowsAppManagerMcp.Services.Interfaces;

namespace WindowsAppManagerMcp.Native;

/// <summary>
/// Reads pixels from the screen or a window into a DIB section of the server (roadmap M2.9): BitBlt or PrintWindow
/// draws straight into the section's memory, and the Capture members copy the pixels out of it once, while
/// <see cref="WithPixels{T}"/> lends the section's memory itself (roadmap M6). The section is a top-down 32-bit bitmap,
/// so its memory already has the layout of <see cref="CaptureResult.Bgra"/>; nothing calls GetDIBits on a bitmap that is
/// still selected into a device context.
/// </summary>
public class ScreenCaptureWrapper : IScreenCaptureWrapper
{
    /// <summary>The longest edge GDI captures, in pixels.</summary>
    public const int MaxCaptureSize = 16384;

    /// <summary>
    /// The pixel budget of one capture: 64 megapixels (for example 8192 x 8192), 256 MB of pixels. A larger capture
    /// is refused before any memory is allocated, so a capture can never take gigabytes of the user's memory.
    /// </summary>
    public const long MaxCapturePixels = 64L * 1024 * 1024;

    /// <summary>The pixels were copied from the screen: what is on screen there, covering windows included.</summary>
    internal const string ScreenBitBltMethod = CaptureMethods.ScreenBitBlt;

    /// <summary>The window drew itself (PrintWindow): covering windows do not show.</summary>
    internal const string PrintWindowMethod = CaptureMethods.PrintWindow;

    /// <summary>The window refused PrintWindow, so its device context was copied: covering windows can show.</summary>
    internal const string BitBltFallbackMethod = CaptureMethods.BitBltFallback;

    /// <summary>The Capture members' reader: one copy of the lent pixels.</summary>
    private static readonly PixelReader<CaptureResult> Copy = static pixels => pixels.ToCaptureResult();

    public CaptureResult CaptureScreenRegion(int x, int y, int width, int height) =>
        WithScreenPixels(x, y, width, height, Copy);

    public CaptureResult CaptureWindow(nint windowHandle, bool includeFrame) =>
        WithWindowPixels(windowHandle, includeFrame, Copy);

    public CaptureResult CaptureWindowFromScreen(nint windowHandle, bool includeFrame) =>
        WithWindowPixelsFromScreen(windowHandle, includeFrame, Copy);

    public T WithPixels<T>(PixelSource source, PixelReader<T> read)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(read);
        return source switch
        {
            PixelSource.Screen screen => WithScreenPixels(screen.X, screen.Y, screen.Width, screen.Height, read),
            PixelSource.Window window => WithWindowPixels(window.Handle, window.IncludeFrame, read),
            PixelSource.WindowFromScreen window => WithWindowPixelsFromScreen(window.Handle, window.IncludeFrame, read),
            _ => throw new ArgumentException($"Unknown pixel source {source.GetType().Name}", nameof(source))
        };
    }

    private static T WithScreenPixels<T>(int x, int y, int width, int height, PixelReader<T> read)
    {
        ValidateDimensions(width, height);

        nint screenDc = nint.Zero;
        try
        {
            // Get screen DC (null window handle = entire screen)
            screenDc = NativeMethods.User32.GetDC(nint.Zero);
            if (screenDc == nint.Zero)
                throw new InvalidOperationException("Failed to get screen device context");

            using var section = new DibSection(screenDc, width, height);
            if (!NativeMethods.Gdi32.BitBlt(
                    section.Dc, 0, 0, width, height,
                    screenDc, x, y,
                    NativeEnums.SRCCOPY | NativeEnums.CAPTUREBLT))
            {
                throw new InvalidOperationException("BitBlt failed");
            }

            // The pixels are in the section now: the screen's device context is not held while the reader runs
            NativeMethods.User32.ReleaseDC(nint.Zero, screenDc);
            screenDc = nint.Zero;
            return read(section.View(x, y, ScreenBitBltMethod));
        }
        finally
        {
            if (screenDc != nint.Zero)
                NativeMethods.User32.ReleaseDC(nint.Zero, screenDc);
        }
    }

    private T WithWindowPixels<T>(nint windowHandle, bool includeFrame, PixelReader<T> read)
    {
        var (originX, originY, width, height) = WindowCaptureRectangle(windowHandle, includeFrame);

        nint windowDc = nint.Zero;
        try
        {
            windowDc = includeFrame
                ? NativeMethods.User32.GetWindowDC(windowHandle)
                : NativeMethods.User32.GetDC(windowHandle);

            if (windowDc == nint.Zero)
                throw new InvalidOperationException("Failed to get window device context");

            using var section = new DibSection(windowDc, width, height);

            // PrintWindow has the window draw itself, so covering windows do not show
            uint flags = includeFrame ? 0u : NativeEnums.PW_CLIENTONLY;
            flags |= NativeEnums.PW_RENDERFULLCONTENT;

            var method = PrintWindowMethod;
            if (!NativeMethods.User32.PrintWindow(windowHandle, section.Dc, flags))
            {
                // The window refused: copy its device context, which shows what is on screen there
                method = BitBltFallbackMethod;
                if (!NativeMethods.Gdi32.BitBlt(
                        section.Dc, 0, 0, width, height,
                        windowDc, 0, 0,
                        NativeEnums.SRCCOPY))
                {
                    throw new InvalidOperationException("Failed to capture window content");
                }
            }

            // The pixels are in the section now: the window's device context is not held while the reader runs, so a
            // slow reader (OCR) does not count against a deadline the caller sets for the window's own answer
            NativeMethods.User32.ReleaseDC(windowHandle, windowDc);
            windowDc = nint.Zero;
            return read(section.View(originX, originY, method));
        }
        finally
        {
            if (windowDc != nint.Zero)
                NativeMethods.User32.ReleaseDC(windowHandle, windowDc);
        }
    }

    private T WithWindowPixelsFromScreen<T>(nint windowHandle, bool includeFrame, PixelReader<T> read)
    {
        // Only rectangle reads, which Windows answers without the window's thread, then a copy of the screen
        var (originX, originY, width, height) = WindowCaptureRectangle(windowHandle, includeFrame);
        return WithScreenPixels(originX, originY, width, height, read);
    }

    public (int X, int Y, int Width, int Height) GetVirtualScreenBounds()
    {
        int x = NativeMethods.User32.GetSystemMetrics(NativeEnums.SM_XVIRTUALSCREEN);
        int y = NativeMethods.User32.GetSystemMetrics(NativeEnums.SM_YVIRTUALSCREEN);
        int width = NativeMethods.User32.GetSystemMetrics(NativeEnums.SM_CXVIRTUALSCREEN);
        int height = NativeMethods.User32.GetSystemMetrics(NativeEnums.SM_CYVIRTUALSCREEN);

        return (x, y, width, height);
    }

    /// <summary>CURSORINFO.flags: the pointer is shown.</summary>
    private const uint CursorShowing = 0x1;

    /// <summary>DrawIconEx's DI_NORMAL: the shape with its mask.</summary>
    private const uint DiNormal = 0x3;

    /// <inheritdoc />
    public PointerState? GetPointer()
    {
        var info = new CURSORINFO { cbSize = Marshal.SizeOf<CURSORINFO>() };
        return NativeMethods.User32.GetCursorInfo(ref info)
            ? new PointerState(info.ptScreenPos.X, info.ptScreenPos.Y, (info.flags & CursorShowing) != 0)
            : null;
    }

    /// <inheritdoc />
    public bool DrawPointer(nint hdc, int originX, int originY)
    {
        var info = new CURSORINFO { cbSize = Marshal.SizeOf<CURSORINFO>() };
        if (!NativeMethods.User32.GetCursorInfo(ref info) || (info.flags & CursorShowing) == 0 || info.hCursor == nint.Zero ||
            !NativeMethods.User32.GetIconInfo(info.hCursor, out var icon))
        {
            return false;
        }

        try
        {
            return NativeMethods.User32.DrawIconEx(hdc, info.ptScreenPos.X - icon.xHotspot - originX,
                info.ptScreenPos.Y - icon.yHotspot - originY, info.hCursor, 0, 0, 0, nint.Zero, DiNormal);
        }
        finally
        {
            // GetIconInfo hands out copies of the shape's bitmaps
            if (icon.hbmMask != nint.Zero)
            {
                NativeMethods.Gdi32.DeleteObject(icon.hbmMask);
            }

            if (icon.hbmColor != nint.Zero)
            {
                NativeMethods.Gdi32.DeleteObject(icon.hbmColor);
            }
        }
    }

    public bool IsValidWindow(nint windowHandle)
    {
        return windowHandle != nint.Zero && NativeMethods.User32.IsWindow(windowHandle);
    }

    /// <summary>
    /// The screen rectangle a window capture covers. GetWindowDC and PrintWindow without PW_CLIENTONLY start at the
    /// window rectangle's corner; GetDC and PW_CLIENTONLY start at the client area's top-left corner on screen. All
    /// values are physical pixels because the server is per-monitor DPI aware.
    /// </summary>
    private (int X, int Y, int Width, int Height) WindowCaptureRectangle(nint windowHandle, bool includeFrame)
    {
        if (!IsValidWindow(windowHandle))
            throw new ArgumentException("Invalid window handle", nameof(windowHandle));

        RECT rect;
        int width, height, originX, originY;

        if (includeFrame)
        {
            if (!NativeMethods.User32.GetWindowRect(windowHandle, out rect))
                throw new InvalidOperationException("Failed to get window rect");

            width = rect.Width;
            height = rect.Height;
            originX = rect.Left;
            originY = rect.Top;
        }
        else
        {
            if (!NativeMethods.User32.GetClientRect(windowHandle, out rect))
                throw new InvalidOperationException("Failed to get client rect");

            // The whole client rectangle on screen. In a right-to-left mirrored window
            // (WS_EX_LAYOUTRTL, Arabic or Hebrew UI) client x = 0 is the right edge, so
            // ClientToScreen(0, 0) would give the top-right corner; MapWindowPoints with a RECT keeps
            // left < right, so Left is the left edge in every layout.
            var clientOnScreen = rect;
            if (NativeMethods.User32.MapWindowPoints(windowHandle, nint.Zero, ref clientOnScreen, 2) == 0 &&
                Marshal.GetLastPInvokeError() != 0)
                throw new InvalidOperationException("Failed to get the screen position of the client area");

            width = rect.Width;
            height = rect.Height;
            originX = Math.Min(clientOnScreen.Left, clientOnScreen.Right);
            originY = Math.Min(clientOnScreen.Top, clientOnScreen.Bottom);
        }

        if (width <= 0 || height <= 0)
            throw new InvalidOperationException("Window has invalid dimensions (possibly minimized)");

        ValidateDimensions(width, height);
        return (originX, originY, width, height);
    }

    /// <summary>
    /// Refuses sizes GDI cannot capture and captures over the pixel budget, before any memory is allocated.
    /// </summary>
    internal static void ValidateDimensions(int width, int height)
    {
        if (width <= 0 || height <= 0)
            throw new ArgumentException("Width and height must be positive");

        if (width > MaxCaptureSize || height > MaxCaptureSize)
            throw new ArgumentException($"Capture dimensions exceed maximum of {MaxCaptureSize}x{MaxCaptureSize}");

        if ((long)width * height > MaxCapturePixels)
        {
            throw new ArgumentException(
                $"The capture of {width}x{height} pixels is over the budget of {MaxCapturePixels / (1024 * 1024)} megapixels");
        }
    }

    /// <summary>
    /// A top-down 32-bit DIB section selected into a memory device context of the server. BitBlt and PrintWindow draw
    /// into its memory; <see cref="View"/> lends that memory as it is, and the Capture members copy it out once. Dispose
    /// selects the old bitmap back before the section is deleted, so a view must not outlive the section.
    /// </summary>
    private sealed class DibSection : IDisposable
    {
        private readonly nint _bitmap;
        private readonly nint _oldBitmap;
        private readonly nint _bits;
        private readonly int _byteCount;
        private readonly int _width;
        private readonly int _height;

        public DibSection(nint compatibleWith, int width, int height)
        {
            _byteCount = checked(width * height * 4);
            _width = width;
            _height = height;

            Dc = NativeMethods.Gdi32.CreateCompatibleDC(compatibleWith);
            if (Dc == nint.Zero)
                throw new InvalidOperationException("Failed to create compatible DC");

            var info = BITMAPINFO.Create(width, height);
            _bitmap = NativeMethods.Gdi32.CreateDIBSection(Dc, ref info, NativeEnums.DIB_RGB_COLORS, out _bits, nint.Zero, 0);
            if (_bitmap == nint.Zero || _bits == nint.Zero)
            {
                if (_bitmap != nint.Zero)
                    NativeMethods.Gdi32.DeleteObject(_bitmap);
                NativeMethods.Gdi32.DeleteDC(Dc);
                throw new InvalidOperationException($"Failed to create a {width}x{height} bitmap (out of memory?)");
            }

            _oldBitmap = NativeMethods.Gdi32.SelectObject(Dc, _bitmap);
        }

        /// <summary>The memory device context to draw into.</summary>
        public nint Dc { get; }

        /// <summary>
        /// The pixels drawn so far, in the section's own memory: top-down rows of 4 bytes per pixel (blue, green, red,
        /// undefined), with no padding between rows.
        /// </summary>
        public unsafe PixelView View(int screenOriginX, int screenOriginY, string method)
        {
            // BitBlt and PrintWindow can be batched; the bits are only complete once the batch has run
            NativeMethods.Gdi32.GdiFlush();
            return new PixelView(new ReadOnlySpan<byte>((void*)_bits, _byteCount), _width, _height, _width * 4,
                screenOriginX, screenOriginY, method);
        }

        public void Dispose()
        {
            if (_oldBitmap != nint.Zero)
                NativeMethods.Gdi32.SelectObject(Dc, _oldBitmap);

            NativeMethods.Gdi32.DeleteObject(_bitmap);
            NativeMethods.Gdi32.DeleteDC(Dc);
        }
    }
}
