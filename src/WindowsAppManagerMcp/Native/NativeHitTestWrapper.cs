using System.Runtime.InteropServices;
using WindowsAppManagerMcp.Models;
using WindowsAppManagerMcp.Services.Interfaces;

namespace WindowsAppManagerMcp.Native;

/// <summary>
/// Production implementation of <see cref="INativeHitTestWrapper"/>. Every call only reads window
/// state.
/// </summary>
public class NativeHitTestWrapper : INativeHitTestWrapper
{
    private const uint GA_ROOT = 2;

    /// <inheritdoc />
    public nint WindowFromPoint(int x, int y) =>
        NativeMethods.User32.WindowFromPoint(new POINT { X = x, Y = y });

    /// <inheritdoc />
    public nint GetRootWindow(nint windowHandle) =>
        windowHandle == nint.Zero ? nint.Zero : NativeMethods.User32.GetAncestor(windowHandle, GA_ROOT);

    /// <inheritdoc />
    public WindowRect? GetClientAreaOnScreen(nint windowHandle)
    {
        if (windowHandle == nint.Zero || !NativeMethods.User32.GetClientRect(windowHandle, out var client))
        {
            return null;
        }

        // MapWindowPoints with a RECT keeps left < right in a mirrored right-to-left window too (as the capture does);
        // it returns 0 both for a failure and for a window at the screen's origin, so only the last error tells them apart
        var onScreen = client;
        if (NativeMethods.User32.MapWindowPoints(windowHandle, nint.Zero, ref onScreen, 2) == 0 && Marshal.GetLastPInvokeError() != 0)
        {
            return null;
        }

        return WindowRect.FromLTRB(
            Math.Min(onScreen.Left, onScreen.Right), Math.Min(onScreen.Top, onScreen.Bottom),
            Math.Max(onScreen.Left, onScreen.Right), Math.Max(onScreen.Top, onScreen.Bottom));
    }
}
