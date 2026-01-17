using System.Runtime.InteropServices;

namespace WindowsAppManagerMcp.Native;

[StructLayout(LayoutKind.Sequential)]
internal struct RECT
{
    public int Left;
    public int Top;
    public int Right;
    public int Bottom;

    public int Width => Right - Left;
    public int Height => Bottom - Top;
}

[StructLayout(LayoutKind.Sequential)]
internal struct BITMAPINFOHEADER
{
    public int biSize;
    public int biWidth;
    public int biHeight;
    public short biPlanes;
    public short biBitCount;
    public int biCompression;
    public int biSizeImage;
    public int biXPelsPerMeter;
    public int biYPelsPerMeter;
    public int biClrUsed;
    public int biClrImportant;

    public static BITMAPINFOHEADER Create(int width, int height)
    {
        return new BITMAPINFOHEADER
        {
            biSize = Marshal.SizeOf<BITMAPINFOHEADER>(),
            biWidth = width,
            biHeight = -height, // Negative for top-down bitmap
            biPlanes = 1,
            biBitCount = 32,
            biCompression = 0, // BI_RGB
            biSizeImage = 0,
            biXPelsPerMeter = 0,
            biYPelsPerMeter = 0,
            biClrUsed = 0,
            biClrImportant = 0
        };
    }
}

[StructLayout(LayoutKind.Sequential)]
internal struct BITMAPINFO
{
    public BITMAPINFOHEADER bmiHeader;
    // bmiColors is not needed for 32-bit bitmaps

    public static BITMAPINFO Create(int width, int height)
    {
        return new BITMAPINFO
        {
            bmiHeader = BITMAPINFOHEADER.Create(width, height)
        };
    }
}

[StructLayout(LayoutKind.Sequential)]
internal struct POINT
{
    public int X;
    public int Y;
}

[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
internal struct MONITORINFOEX
{
    public int cbSize;
    public RECT rcMonitor;
    public RECT rcWork;
    public uint dwFlags;
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
    public string szDevice;

    public static MONITORINFOEX Create()
    {
        var mi = new MONITORINFOEX();
        mi.cbSize = Marshal.SizeOf<MONITORINFOEX>();
        return mi;
    }
}

[StructLayout(LayoutKind.Sequential)]
internal struct WINDOWPLACEMENT
{
    public int length;
    public int flags;
    public int showCmd;
    public POINT ptMinPosition;
    public POINT ptMaxPosition;
    public RECT rcNormalPosition;

    public static WINDOWPLACEMENT Create()
    {
        var wp = new WINDOWPLACEMENT();
        wp.length = Marshal.SizeOf<WINDOWPLACEMENT>();
        return wp;
    }
}

[StructLayout(LayoutKind.Sequential)]
internal struct MOUSEINPUT
{
    public int dx;
    public int dy;
    public uint mouseData;
    public uint dwFlags;
    public uint time;
    public nint dwExtraInfo;
}

[StructLayout(LayoutKind.Sequential)]
internal struct KEYBDINPUT
{
    public ushort wVk;
    public ushort wScan;
    public uint dwFlags;
    public uint time;
    public nint dwExtraInfo;
}

[StructLayout(LayoutKind.Sequential)]
internal struct HARDWAREINPUT
{
    public uint uMsg;
    public ushort wParamL;
    public ushort wParamH;
}

[StructLayout(LayoutKind.Explicit)]
internal struct INPUT
{
    [FieldOffset(0)]
    public uint type;

    [FieldOffset(8)]
    public MOUSEINPUT mi;

    [FieldOffset(8)]
    public KEYBDINPUT ki;

    [FieldOffset(8)]
    public HARDWAREINPUT hi;

    public static INPUT MouseInput(int dx, int dy, uint flags, uint mouseData = 0)
    {
        return new INPUT
        {
            type = NativeEnums.INPUT_MOUSE,
            mi = new MOUSEINPUT
            {
                dx = dx,
                dy = dy,
                dwFlags = flags,
                mouseData = mouseData,
                time = 0,
                dwExtraInfo = nint.Zero
            }
        };
    }
}
