using System.Runtime.InteropServices;

namespace WindowsAppManagerMcp.Native;

internal static partial class NativeMethods
{
    internal static partial class User32
    {
        public delegate bool EnumWindowsProc(nint hWnd, nint lParam);
        public delegate bool MonitorEnumProc(nint hMonitor, nint hdcMonitor, ref RECT lprcMonitor, nint dwData);

        [LibraryImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static partial bool EnumWindows(EnumWindowsProc lpEnumFunc, nint lParam);

        [LibraryImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static partial bool EnumChildWindows(nint hWndParent, EnumWindowsProc lpEnumFunc, nint lParam);

        [LibraryImport("user32.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
        public static partial int GetWindowTextW(nint hWnd, [Out] char[] lpString, int nMaxCount);

        [LibraryImport("user32.dll", SetLastError = true)]
        public static partial int GetWindowTextLengthW(nint hWnd);

        [LibraryImport("user32.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
        public static partial int GetClassNameW(nint hWnd, [Out] char[] lpClassName, int nMaxCount);

        [LibraryImport("user32.dll", SetLastError = true)]
        public static partial uint GetWindowThreadProcessId(nint hWnd, out uint lpdwProcessId);

        [LibraryImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static partial bool IsWindowVisible(nint hWnd);

        [LibraryImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static partial bool IsIconic(nint hWnd);

        [LibraryImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static partial bool IsZoomed(nint hWnd);

        [LibraryImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static partial bool IsWindow(nint hWnd);

        [LibraryImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static partial bool GetWindowRect(nint hWnd, out RECT lpRect);

        [LibraryImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static partial bool SetWindowPos(
            nint hWnd,
            nint hWndInsertAfter,
            int X,
            int Y,
            int cx,
            int cy,
            uint uFlags);

        [LibraryImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static partial bool MoveWindow(
            nint hWnd,
            int X,
            int Y,
            int nWidth,
            int nHeight,
            [MarshalAs(UnmanagedType.Bool)] bool bRepaint);

        [LibraryImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static partial bool ShowWindow(nint hWnd, int nCmdShow);

        [LibraryImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static partial bool SetForegroundWindow(nint hWnd);

        [LibraryImport("user32.dll")]
        public static partial nint GetForegroundWindow();

        [LibraryImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static partial bool BringWindowToTop(nint hWnd);

        [LibraryImport("user32.dll")]
        public static partial nint GetWindow(nint hWnd, uint uCmd);

        [LibraryImport("user32.dll")]
        public static partial nint GetParent(nint hWnd);

        [LibraryImport("user32.dll")]
        public static partial nint GetAncestor(nint hWnd, uint gaFlags);

        [LibraryImport("user32.dll", SetLastError = true)]
        public static partial int GetWindowLongW(nint hWnd, int nIndex);

        [LibraryImport("user32.dll", SetLastError = true)]
        public static partial int SetWindowLongW(nint hWnd, int nIndex, int dwNewLong);

        [LibraryImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static partial bool GetWindowPlacement(nint hWnd, ref WINDOWPLACEMENT lpwndpl);

        [LibraryImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static partial bool SetWindowPlacement(nint hWnd, ref WINDOWPLACEMENT lpwndpl);

        [LibraryImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static partial bool EnumDisplayMonitors(
            nint hdc,
            nint lprcClip,
            MonitorEnumProc lpfnEnum,
            nint dwData);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool GetMonitorInfoW(nint hMonitor, ref MONITORINFOEX lpmi);

        [LibraryImport("user32.dll")]
        public static partial nint MonitorFromWindow(nint hwnd, uint dwFlags);

        [LibraryImport("user32.dll")]
        public static partial nint MonitorFromPoint(POINT pt, uint dwFlags);

        [LibraryImport("user32.dll")]
        public static partial nint MonitorFromRect(ref RECT lprc, uint dwFlags);

        [LibraryImport("user32.dll")]
        public static partial uint GetDpiForWindow(nint hwnd);

        [LibraryImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static partial bool AllowSetForegroundWindow(uint dwProcessId);

        [LibraryImport("user32.dll")]
        public static partial uint GetWindowThreadProcessId(nint hWnd, nint lpdwProcessId);

        [LibraryImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static partial bool PostMessageW(nint hWnd, uint Msg, nint wParam, nint lParam);

        [LibraryImport("user32.dll")]
        public static partial nint GetDC(nint hWnd);

        [LibraryImport("user32.dll")]
        public static partial nint GetWindowDC(nint hWnd);

        [LibraryImport("user32.dll")]
        public static partial int ReleaseDC(nint hWnd, nint hDC);

        [LibraryImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static partial bool GetClientRect(nint hWnd, out RECT lpRect);

        [LibraryImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static partial bool PrintWindow(nint hWnd, nint hdcBlt, uint nFlags);

        [LibraryImport("user32.dll")]
        public static partial int GetSystemMetrics(int nIndex);
    }
}
