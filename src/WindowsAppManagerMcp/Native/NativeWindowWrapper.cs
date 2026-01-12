using WindowsAppManagerMcp.Services.Interfaces;

namespace WindowsAppManagerMcp.Native;

/// <summary>
/// Production implementation of INativeWindowWrapper that calls real Windows API functions.
/// </summary>
public class NativeWindowWrapper : INativeWindowWrapper
{
    public void EnumWindows(Func<nint, bool> callback)
    {
        NativeMethods.User32.EnumWindowsProc nativeCallback = (hWnd, lParam) => callback(hWnd);
        NativeMethods.User32.EnumWindows(nativeCallback, nint.Zero);
    }

    public bool IsWindowVisible(nint hWnd)
    {
        return NativeMethods.User32.IsWindowVisible(hWnd);
    }

    public bool IsWindow(nint hWnd)
    {
        return NativeMethods.User32.IsWindow(hWnd);
    }

    public bool IsIconic(nint hWnd)
    {
        return NativeMethods.User32.IsIconic(hWnd);
    }

    public bool IsZoomed(nint hWnd)
    {
        return NativeMethods.User32.IsZoomed(hWnd);
    }

    public string GetWindowText(nint hWnd)
    {
        var titleLength = NativeMethods.User32.GetWindowTextLengthW(hWnd);
        if (titleLength <= 0)
            return string.Empty;

        var titleBuffer = new char[titleLength + 1];
        NativeMethods.User32.GetWindowTextW(hWnd, titleBuffer, titleBuffer.Length);
        return new string(titleBuffer, 0, titleLength);
    }

    public uint GetWindowThreadProcessId(nint hWnd)
    {
        NativeMethods.User32.GetWindowThreadProcessId(hWnd, out var processId);
        return processId;
    }

    public (int Left, int Top, int Right, int Bottom)? GetWindowRect(nint hWnd)
    {
        if (!NativeMethods.User32.GetWindowRect(hWnd, out var rect))
            return null;

        return (rect.Left, rect.Top, rect.Right, rect.Bottom);
    }

    public bool SetWindowPos(nint hWnd, int x, int y, int width, int height, uint flags)
    {
        return NativeMethods.User32.SetWindowPos(hWnd, nint.Zero, x, y, width, height, flags);
    }

    public bool ShowWindow(nint hWnd, int cmdShow)
    {
        return NativeMethods.User32.ShowWindow(hWnd, cmdShow);
    }

    public bool SetForegroundWindow(nint hWnd)
    {
        return NativeMethods.User32.SetForegroundWindow(hWnd);
    }

    public bool BringWindowToTop(nint hWnd)
    {
        return NativeMethods.User32.BringWindowToTop(hWnd);
    }

    public nint GetForegroundWindow()
    {
        return NativeMethods.User32.GetForegroundWindow();
    }

    public bool PostMessage(nint hWnd, uint msg, nint wParam, nint lParam)
    {
        return NativeMethods.User32.PostMessageW(hWnd, msg, wParam, lParam);
    }

    public uint GetCurrentProcessId()
    {
        return NativeMethods.Kernel32.GetCurrentProcessId();
    }
}
