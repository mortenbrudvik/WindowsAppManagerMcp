using System.Diagnostics;
using WindowsAppManagerMcp.Models;
using WindowsAppManagerMcp.Services.Interfaces;

namespace WindowsAppManagerMcp.Native;

/// <summary>
/// Production implementation of <see cref="INativeSystemInfoWrapper"/>. Every call only reads; NativeCallGuardTests
/// holds it to the read-only API list, and lets it alone call SystemParametersInfoW, with the GET action only.
/// </summary>
public sealed class NativeSystemInfoWrapper : INativeSystemInfoWrapper
{
    public DpiAwareness GetDpiAwareness()
    {
        try
        {
            var context = NativeMethods.User32.GetThreadDpiAwarenessContext();
            if (context == nint.Zero)
            {
                return DpiAwareness.Unknown;
            }

            return DpiAwarenessNames.FromContext(
                NativeMethods.User32.GetAwarenessFromDpiAwarenessContext(context),
                NativeMethods.User32.AreDpiAwarenessContextsEqual(context, NativeMethods.User32.DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2),
                NativeMethods.User32.AreDpiAwarenessContextsEqual(context, NativeMethods.User32.DPI_AWARENESS_CONTEXT_UNAWARE_GDISCALED));
        }
        catch (EntryPointNotFoundException)
        {
            // Windows before 10 1607 has no awareness contexts
            return DpiAwareness.Unknown;
        }
    }

    public int? GetForegroundLockTimeout() =>
        NativeMethods.User32.SystemParametersInfoW(NativeMethods.User32.SPI_GETFOREGROUNDLOCKTIMEOUT, 0, out var milliseconds, 0)
            ? (int)Math.Min(milliseconds, int.MaxValue)
            : null;

    public MouseWheelRouting? GetMouseWheelRouting() =>
        NativeMethods.User32.SystemParametersInfoW(NativeMethods.User32.SPI_GETMOUSEWHEELROUTING, 0, out var routing, 0) &&
        Enum.IsDefined(typeof(MouseWheelRouting), (int)Math.Min(routing, int.MaxValue))
            ? (MouseWheelRouting)routing
            : null;

    public VirtualScreenBounds? GetVirtualScreen()
    {
        var width = NativeMethods.User32.GetSystemMetrics(NativeEnums.SM_CXVIRTUALSCREEN);
        var height = NativeMethods.User32.GetSystemMetrics(NativeEnums.SM_CYVIRTUALSCREEN);
        return width > 0 && height > 0
            ? new VirtualScreenBounds(NativeMethods.User32.GetSystemMetrics(NativeEnums.SM_XVIRTUALSCREEN),
                NativeMethods.User32.GetSystemMetrics(NativeEnums.SM_YVIRTUALSCREEN), width, height)
            : null;
    }

    public bool? AreMouseButtonsSwapped() => NativeMethods.User32.GetSystemMetrics(NativeEnums.SM_SWAPBUTTON) != 0;

    public int? GetSessionId()
    {
        try
        {
            using var current = Process.GetCurrentProcess();
            return current.SessionId;
        }
        catch (Exception)
        {
            return null;
        }
    }
}
