using System.Runtime.InteropServices;

namespace WindowsAppManagerMcp.Native;

internal static partial class NativeMethods
{
    // The mouse pointer in captures (ScreenCaptureWrapper, roadmap M7.1): where it is, and drawing its current shape into a
    // device context of the server's own image. Nothing here moves the pointer or changes its shape.
    internal static partial class User32
    {
        /// <summary>The pointer's shape, whether it is shown, and its screen position (physical pixels for a per-monitor aware process).</summary>
        [LibraryImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static partial bool GetCursorInfo(ref CURSORINFO pci);

        /// <summary>A pointer shape's hot spot and bitmaps; the caller deletes both bitmaps.</summary>
        [LibraryImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static partial bool GetIconInfo(nint hIcon, out ICONINFO piconinfo);

        /// <summary>Draws an icon or pointer shape into a device context.</summary>
        [LibraryImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static partial bool DrawIconEx(nint hdc, int xLeft, int yTop, nint hIcon, int cxWidth, int cyWidth, uint istepIfAniCur,
            nint hbrFlickerFreeDraw, uint diFlags);
    }
}

[StructLayout(LayoutKind.Sequential)]
internal struct CURSORINFO
{
    public int cbSize;
    public uint flags;
    public nint hCursor;
    public POINT ptScreenPos;
}

[StructLayout(LayoutKind.Sequential)]
internal struct ICONINFO
{
    public int fIcon;
    public int xHotspot;
    public int yHotspot;
    public nint hbmMask;
    public nint hbmColor;
}
