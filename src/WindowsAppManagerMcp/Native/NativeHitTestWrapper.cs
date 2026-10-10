using WindowsAppManagerMcp.Services.Interfaces;

namespace WindowsAppManagerMcp.Native;

/// <summary>
/// Production implementation of <see cref="INativeHitTestWrapper"/>. Both calls only read window
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
}
