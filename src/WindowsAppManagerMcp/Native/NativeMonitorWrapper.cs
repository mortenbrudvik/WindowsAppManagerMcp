using WindowsAppManagerMcp.Models;
using WindowsAppManagerMcp.Services.Interfaces;

namespace WindowsAppManagerMcp.Native;

/// <summary>
/// Production implementation of INativeMonitorWrapper that calls real Windows API functions.
/// </summary>
public class NativeMonitorWrapper : INativeMonitorWrapper
{
    public IReadOnlyList<(nint Handle, MonitorInfo Info)> EnumerateMonitors()
    {
        // Built locally on every call, so concurrent callers never share a list that is being filled
        var monitors = new List<(nint Handle, MonitorInfo Info)>();

        NativeMethods.User32.MonitorEnumProc callback = (nint hMonitor, nint hdcMonitor, ref RECT lprcMonitor, nint dwData) =>
        {
            var mi = MONITORINFOEX.Create();
            if (NativeMethods.User32.GetMonitorInfoW(hMonitor, ref mi))
            {
                var scaleFactor = GetScaleFactor(hMonitor);
                var info = new MonitorInfo(
                    Index: monitors.Count,
                    DeviceName: mi.szDevice,
                    IsPrimary: (mi.dwFlags & NativeEnums.MONITORINFOF_PRIMARY) != 0,
                    Bounds: new MonitorRect(
                        mi.rcMonitor.Left,
                        mi.rcMonitor.Top,
                        mi.rcMonitor.Right - mi.rcMonitor.Left,
                        mi.rcMonitor.Bottom - mi.rcMonitor.Top),
                    WorkArea: new MonitorRect(
                        mi.rcWork.Left,
                        mi.rcWork.Top,
                        mi.rcWork.Right - mi.rcWork.Left,
                        mi.rcWork.Bottom - mi.rcWork.Top),
                    ScaleFactor: scaleFactor
                );
                monitors.Add((hMonitor, info));
            }
            return true;
        };

        NativeMethods.User32.EnumDisplayMonitors(nint.Zero, nint.Zero, callback, nint.Zero);
        GC.KeepAlive(callback);

        return monitors;
    }

    public nint MonitorFromWindow(nint windowHandle)
    {
        return NativeMethods.User32.MonitorFromWindow(windowHandle, NativeEnums.MONITOR_DEFAULTTONEAREST);
    }

    public nint MonitorFromPoint(int x, int y)
    {
        var pt = new POINT { X = x, Y = y };
        return NativeMethods.User32.MonitorFromPoint(pt, NativeEnums.MONITOR_DEFAULTTONEAREST);
    }

    private static double GetScaleFactor(nint hMonitor)
    {
        try
        {
            int result = NativeMethods.Shcore.GetDpiForMonitor(
                hMonitor,
                MONITOR_DPI_TYPE.MDT_EFFECTIVE_DPI,
                out uint dpiX,
                out uint _);

            if (result == 0) // S_OK
            {
                // Standard DPI is 96, scale factor = actual DPI / 96
                return dpiX / 96.0;
            }
        }
        catch
        {
            // Fallback for older Windows versions (pre-8.1)
        }

        return 1.0; // Default to no scaling
    }
}
