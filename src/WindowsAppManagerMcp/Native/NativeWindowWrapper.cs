using System.Diagnostics;
using System.Runtime.InteropServices;
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

    public uint GetWindowThreadId(nint hWnd)
    {
        return hWnd == nint.Zero ? 0 : NativeMethods.User32.GetWindowThreadProcessId(hWnd, out _);
    }

    public (int Left, int Top, int Right, int Bottom)? GetWindowRect(nint hWnd)
    {
        if (!NativeMethods.User32.GetWindowRect(hWnd, out var rect))
            return null;

        return (rect.Left, rect.Top, rect.Right, rect.Bottom);
    }

    public Win32CallResult SetWindowPos(nint hWnd, int x, int y, int width, int height, uint flags)
    {
        // The error must be read here, right after the call: nothing else may run in between
        return NativeMethods.User32.SetWindowPos(hWnd, nint.Zero, x, y, width, height, flags)
            ? Win32CallResult.Ok
            : Win32CallResult.Failure(Marshal.GetLastPInvokeError());
    }

    public Win32CallResult SetWindowZOrder(nint hWnd, nint insertAfter, uint flags)
    {
        // The error must be read here, right after the call: nothing else may run in between
        return NativeMethods.User32.SetWindowPos(hWnd, insertAfter, 0, 0, 0, 0, flags | NativeEnums.SWP_NOMOVE | NativeEnums.SWP_NOSIZE)
            ? Win32CallResult.Ok
            : Win32CallResult.Failure(Marshal.GetLastPInvokeError());
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

    public nint GetAncestor(nint hWnd, WindowAncestor ancestor)
    {
        return NativeMethods.User32.GetAncestor(hWnd, (uint)ancestor);
    }

    public nint GetShellWindow()
    {
        return NativeMethods.User32.GetShellWindow();
    }

    public Win32CallResult PostMessage(nint hWnd, uint msg, nint wParam, nint lParam)
    {
        // The error must be read here, right after the call: nothing else may run in between
        return NativeMethods.User32.PostMessageW(hWnd, msg, wParam, lParam)
            ? Win32CallResult.Ok
            : Win32CallResult.Failure(Marshal.GetLastPInvokeError());
    }

    public uint GetCurrentProcessId()
    {
        return NativeMethods.Kernel32.GetCurrentProcessId();
    }

    public string? GetProcessName(uint processId)
    {
        try
        {
            using var process = Process.GetProcessById((int)processId);
            return process.ProcessName;
        }
        catch (Exception)
        {
            // The process has exited (ArgumentException, InvalidOperationException) or cannot be
            // queried (Win32Exception); callers report it as "Unknown".
            return null;
        }
    }

    public void EnumChildWindows(nint hWndParent, Func<nint, bool> callback)
    {
        // EnumChildWindows(NULL) would list every top-level window instead, like EnumWindows.
        if (hWndParent == nint.Zero)
            return;

        NativeMethods.User32.EnumWindowsProc nativeCallback = (hWnd, lParam) => callback(hWnd);
        NativeMethods.User32.EnumChildWindows(hWndParent, nativeCallback, nint.Zero);
    }

    public string GetClassName(nint hWnd)
    {
        // Window class names are at most 256 characters.
        var buffer = new char[257];
        var length = NativeMethods.User32.GetClassNameW(hWnd, buffer, buffer.Length);
        return length > 0 ? new string(buffer, 0, length) : string.Empty;
    }

    public nint GetWindowLongPtr(nint hWnd, int index)
    {
        return nint.Size == 8
            ? NativeMethods.User32.GetWindowLongPtrW(hWnd, index)
            : NativeMethods.User32.GetWindowLongW(hWnd, index);
    }

    public nint GetWindow(nint hWnd, uint command)
    {
        return NativeMethods.User32.GetWindow(hWnd, command);
    }

    public bool IsCloaked(nint hWnd)
    {
        return GetCloakReason(hWnd) != 0;
    }

    public bool HasWindowRegion(nint hWnd) => NativeMethods.User32.GetWindowRgnBox(hWnd, out _) != 0;

    public int GetCloakReason(nint hWnd)
    {
        var hr = NativeMethods.Dwmapi.DwmGetWindowAttribute(hWnd, NativeEnums.DWMWA_CLOAKED, out int cloaked, sizeof(int));
        return hr == 0 ? cloaked : 0;
    }

    public (int Left, int Top, int Right, int Bottom)? GetExtendedFrameBounds(nint hWnd)
    {
        var hr = NativeMethods.Dwmapi.DwmGetWindowAttribute(
            hWnd, NativeEnums.DWMWA_EXTENDED_FRAME_BOUNDS, out RECT rect, Marshal.SizeOf<RECT>());
        return hr == 0 ? (rect.Left, rect.Top, rect.Right, rect.Bottom) : null;
    }

    public bool IsHungAppWindow(nint hWnd)
    {
        return NativeMethods.User32.IsHungAppWindow(hWnd);
    }

    public bool? RespondsWithin(nint hWnd, uint timeoutMs)
    {
        // WM_NULL only: the window does nothing with it, so this reads whether its thread pumps messages
        if (NativeMethods.User32.SendMessageTimeoutW(
                hWnd, NativeEnums.WM_NULL, 0, 0, NativeEnums.SMTO_ABORTIFHUNG, timeoutMs, out _) != 0)
        {
            return true;
        }

        // Only a timeout says the thread did not answer; any other failure says nothing about it
        return Marshal.GetLastPInvokeError() == NativeEnums.ERROR_TIMEOUT ? false : null;
    }

    public uint GetDpiForWindow(nint hWnd)
    {
        return NativeMethods.User32.GetDpiForWindow(hWnd);
    }
}
