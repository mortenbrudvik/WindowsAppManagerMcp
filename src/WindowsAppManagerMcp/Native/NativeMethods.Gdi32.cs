using System.Runtime.InteropServices;

namespace WindowsAppManagerMcp.Native;

internal static partial class NativeMethods
{
    internal static partial class Gdi32
    {
        [LibraryImport("gdi32.dll")]
        public static partial nint CreateCompatibleDC(nint hdc);

        [LibraryImport("gdi32.dll")]
        public static partial nint CreateCompatibleBitmap(nint hdc, int nWidth, int nHeight);

        [LibraryImport("gdi32.dll")]
        public static partial nint SelectObject(nint hdc, nint hObject);

        [LibraryImport("gdi32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static partial bool BitBlt(
            nint hdcDest,
            int nXDest,
            int nYDest,
            int nWidth,
            int nHeight,
            nint hdcSrc,
            int nXSrc,
            int nYSrc,
            uint dwRop);

        [LibraryImport("gdi32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static partial bool DeleteDC(nint hdc);

        [LibraryImport("gdi32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static partial bool DeleteObject(nint hObject);

        [LibraryImport("gdi32.dll")]
        public static partial int GetDIBits(
            nint hdc,
            nint hbmp,
            uint uStartScan,
            uint cScanLines,
            nint lpvBits,
            ref BITMAPINFO lpbmi,
            uint uUsage);

        [LibraryImport("gdi32.dll")]
        public static partial int GetObjectW(nint hObject, int nCount, nint lpObject);

        [LibraryImport("gdi32.dll")]
        public static partial nint CreateDIBSection(
            nint hdc,
            ref BITMAPINFO pbmi,
            uint iUsage,
            out nint ppvBits,
            nint hSection,
            uint dwOffset);
    }
}
