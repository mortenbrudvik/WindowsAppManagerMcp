using System.Runtime.InteropServices;
using WindowsAppManagerMcp.Services.Interfaces;

namespace WindowsAppManagerMcp.Native;

public class ScreenCaptureWrapper : IScreenCaptureWrapper
{
    private const int MaxCaptureSize = 16384;

    internal const string BitBltMethod = "BitBlt";
    internal const string PrintWindowMethod = "PrintWindow";

    public CaptureResult CaptureScreenRegion(int x, int y, int width, int height)
    {
        ValidateDimensions(width, height);

        nint screenDc = nint.Zero;
        nint memoryDc = nint.Zero;
        nint bitmap = nint.Zero;
        nint oldBitmap = nint.Zero;

        try
        {
            // Get screen DC (null window handle = entire screen)
            screenDc = NativeMethods.User32.GetDC(nint.Zero);
            if (screenDc == nint.Zero)
                throw new InvalidOperationException("Failed to get screen device context");

            // Create compatible memory DC
            memoryDc = NativeMethods.Gdi32.CreateCompatibleDC(screenDc);
            if (memoryDc == nint.Zero)
                throw new InvalidOperationException("Failed to create compatible DC");

            // Create compatible bitmap
            bitmap = NativeMethods.Gdi32.CreateCompatibleBitmap(screenDc, width, height);
            if (bitmap == nint.Zero)
                throw new InvalidOperationException("Failed to create compatible bitmap");

            // Select bitmap into memory DC
            oldBitmap = NativeMethods.Gdi32.SelectObject(memoryDc, bitmap);

            // BitBlt from screen to memory DC
            bool success = NativeMethods.Gdi32.BitBlt(
                memoryDc, 0, 0, width, height,
                screenDc, x, y,
                NativeEnums.SRCCOPY | NativeEnums.CAPTUREBLT);

            if (!success)
                throw new InvalidOperationException("BitBlt failed");

            // Get pixel data from bitmap
            var data = GetBitmapData(memoryDc, bitmap, width, height);
            return new CaptureResult(data, width, height, x, y, BitBltMethod);
        }
        finally
        {
            if (oldBitmap != nint.Zero && memoryDc != nint.Zero)
                NativeMethods.Gdi32.SelectObject(memoryDc, oldBitmap);

            if (bitmap != nint.Zero)
                NativeMethods.Gdi32.DeleteObject(bitmap);

            if (memoryDc != nint.Zero)
                NativeMethods.Gdi32.DeleteDC(memoryDc);

            if (screenDc != nint.Zero)
                NativeMethods.User32.ReleaseDC(nint.Zero, screenDc);
        }
    }

    public CaptureResult CaptureWindow(nint windowHandle, bool includeFrame)
    {
        if (!IsValidWindow(windowHandle))
            throw new ArgumentException("Invalid window handle", nameof(windowHandle));

        // Size and screen origin of what is captured. GetWindowDC and PrintWindow without
        // PW_CLIENTONLY start at the window rectangle's corner; GetDC and PW_CLIENTONLY start at the
        // client area's top-left corner on screen. All values are physical pixels because the server
        // is per-monitor DPI aware.
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

        nint windowDc = nint.Zero;
        nint memoryDc = nint.Zero;
        nint bitmap = nint.Zero;
        nint oldBitmap = nint.Zero;

        try
        {
            // Get window DC
            windowDc = includeFrame
                ? NativeMethods.User32.GetWindowDC(windowHandle)
                : NativeMethods.User32.GetDC(windowHandle);

            if (windowDc == nint.Zero)
                throw new InvalidOperationException("Failed to get window device context");

            // Create compatible memory DC
            memoryDc = NativeMethods.Gdi32.CreateCompatibleDC(windowDc);
            if (memoryDc == nint.Zero)
                throw new InvalidOperationException("Failed to create compatible DC");

            // Create compatible bitmap
            bitmap = NativeMethods.Gdi32.CreateCompatibleBitmap(windowDc, width, height);
            if (bitmap == nint.Zero)
                throw new InvalidOperationException("Failed to create compatible bitmap");

            // Select bitmap into memory DC
            oldBitmap = NativeMethods.Gdi32.SelectObject(memoryDc, bitmap);

            // Use PrintWindow for better capture (handles occluded windows)
            uint flags = includeFrame ? 0u : NativeEnums.PW_CLIENTONLY;
            flags |= NativeEnums.PW_RENDERFULLCONTENT;

            bool success = NativeMethods.User32.PrintWindow(windowHandle, memoryDc, flags);
            var method = PrintWindowMethod;

            // Fallback to BitBlt if PrintWindow fails
            if (!success)
            {
                method = BitBltMethod;
                success = NativeMethods.Gdi32.BitBlt(
                    memoryDc, 0, 0, width, height,
                    windowDc, 0, 0,
                    NativeEnums.SRCCOPY);

                if (!success)
                    throw new InvalidOperationException("Failed to capture window content");
            }

            // Get pixel data from bitmap
            var data = GetBitmapData(memoryDc, bitmap, width, height);
            return new CaptureResult(data, width, height, originX, originY, method);
        }
        finally
        {
            if (oldBitmap != nint.Zero && memoryDc != nint.Zero)
                NativeMethods.Gdi32.SelectObject(memoryDc, oldBitmap);

            if (bitmap != nint.Zero)
                NativeMethods.Gdi32.DeleteObject(bitmap);

            if (memoryDc != nint.Zero)
                NativeMethods.Gdi32.DeleteDC(memoryDc);

            if (windowDc != nint.Zero)
                NativeMethods.User32.ReleaseDC(windowHandle, windowDc);
        }
    }

    public (int X, int Y, int Width, int Height) GetVirtualScreenBounds()
    {
        int x = NativeMethods.User32.GetSystemMetrics(NativeEnums.SM_XVIRTUALSCREEN);
        int y = NativeMethods.User32.GetSystemMetrics(NativeEnums.SM_YVIRTUALSCREEN);
        int width = NativeMethods.User32.GetSystemMetrics(NativeEnums.SM_CXVIRTUALSCREEN);
        int height = NativeMethods.User32.GetSystemMetrics(NativeEnums.SM_CYVIRTUALSCREEN);

        return (x, y, width, height);
    }

    public bool IsValidWindow(nint windowHandle)
    {
        return windowHandle != nint.Zero && NativeMethods.User32.IsWindow(windowHandle);
    }

    private static void ValidateDimensions(int width, int height)
    {
        if (width <= 0 || height <= 0)
            throw new ArgumentException("Width and height must be positive");

        if (width > MaxCaptureSize || height > MaxCaptureSize)
            throw new ArgumentException($"Capture dimensions exceed maximum of {MaxCaptureSize}x{MaxCaptureSize}");
    }

    private static byte[] GetBitmapData(nint hdc, nint hBitmap, int width, int height)
    {
        var bmi = BITMAPINFO.Create(width, height);
        int dataSize = width * height * 4; // 32-bit BGRA
        byte[] pixelData = new byte[dataSize];

        nint pixelBuffer = Marshal.AllocHGlobal(dataSize);
        try
        {
            int result = NativeMethods.Gdi32.GetDIBits(
                hdc, hBitmap,
                0, (uint)height,
                pixelBuffer, ref bmi,
                NativeEnums.DIB_RGB_COLORS);

            if (result == 0)
                throw new InvalidOperationException("GetDIBits failed");

            Marshal.Copy(pixelBuffer, pixelData, 0, dataSize);
        }
        finally
        {
            Marshal.FreeHGlobal(pixelBuffer);
        }

        return pixelData;
    }
}
