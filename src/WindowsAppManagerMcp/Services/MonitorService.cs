using WindowsAppManagerMcp.Models;
using WindowsAppManagerMcp.Native;
using WindowsAppManagerMcp.Services.Interfaces;

namespace WindowsAppManagerMcp.Services;

public class MonitorService : IMonitorService
{
    private readonly List<(nint Handle, MonitorInfo Info)> _monitors = [];

    public MonitorService()
    {
        RefreshMonitors();
    }

    private void RefreshMonitors()
    {
        _monitors.Clear();
        var index = 0;

        NativeMethods.User32.MonitorEnumProc callback = (nint hMonitor, nint hdcMonitor, ref RECT lprcMonitor, nint dwData) =>
        {
            var mi = MONITORINFOEX.Create();
            if (NativeMethods.User32.GetMonitorInfoW(hMonitor, ref mi))
            {
                var scaleFactor = GetScaleFactor(hMonitor);
                var info = new MonitorInfo(
                    Index: index++,
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
                _monitors.Add((hMonitor, info));
            }
            return true;
        };

        NativeMethods.User32.EnumDisplayMonitors(nint.Zero, nint.Zero, callback, nint.Zero);
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

    public IReadOnlyList<MonitorInfo> GetAllMonitors()
    {
        RefreshMonitors();
        return _monitors.Select(m => m.Info).ToList();
    }

    public MonitorInfo GetPrimaryMonitor()
    {
        RefreshMonitors();
        return _monitors.FirstOrDefault(m => m.Info.IsPrimary).Info
            ?? _monitors.FirstOrDefault().Info
            ?? throw new InvalidOperationException("No monitors found");
    }

    public MonitorInfo? GetMonitorAt(int x, int y)
    {
        RefreshMonitors();
        var pt = new POINT { X = x, Y = y };
        var hMonitor = NativeMethods.User32.MonitorFromPoint(pt, NativeEnums.MONITOR_DEFAULTTONEAREST);
        return _monitors.FirstOrDefault(m => m.Handle == hMonitor).Info;
    }

    public MonitorInfo? GetMonitorForWindow(nint windowHandle)
    {
        RefreshMonitors();
        var hMonitor = NativeMethods.User32.MonitorFromWindow(windowHandle, NativeEnums.MONITOR_DEFAULTTONEAREST);
        return _monitors.FirstOrDefault(m => m.Handle == hMonitor).Info;
    }

    public int GetMonitorIndex(nint windowHandle)
    {
        var monitor = GetMonitorForWindow(windowHandle);
        return monitor?.Index ?? 0;
    }

    public MonitorRect CalculateSnapBounds(int monitorIndex, SnapPosition position)
    {
        RefreshMonitors();
        var monitor = _monitors.ElementAtOrDefault(monitorIndex).Info
            ?? GetPrimaryMonitor();

        var workArea = monitor.WorkArea;

        return position switch
        {
            SnapPosition.LeftHalf => new MonitorRect(
                workArea.X, workArea.Y, workArea.Width / 2, workArea.Height),

            SnapPosition.RightHalf => new MonitorRect(
                workArea.X + workArea.Width / 2, workArea.Y, workArea.Width / 2, workArea.Height),

            SnapPosition.TopHalf => new MonitorRect(
                workArea.X, workArea.Y, workArea.Width, workArea.Height / 2),

            SnapPosition.BottomHalf => new MonitorRect(
                workArea.X, workArea.Y + workArea.Height / 2, workArea.Width, workArea.Height / 2),

            SnapPosition.TopLeftQuarter => new MonitorRect(
                workArea.X, workArea.Y, workArea.Width / 2, workArea.Height / 2),

            SnapPosition.TopRightQuarter => new MonitorRect(
                workArea.X + workArea.Width / 2, workArea.Y, workArea.Width / 2, workArea.Height / 2),

            SnapPosition.BottomLeftQuarter => new MonitorRect(
                workArea.X, workArea.Y + workArea.Height / 2, workArea.Width / 2, workArea.Height / 2),

            SnapPosition.BottomRightQuarter => new MonitorRect(
                workArea.X + workArea.Width / 2, workArea.Y + workArea.Height / 2, workArea.Width / 2, workArea.Height / 2),

            SnapPosition.LeftThird => new MonitorRect(
                workArea.X, workArea.Y, workArea.Width / 3, workArea.Height),

            SnapPosition.CenterThird => new MonitorRect(
                workArea.X + workArea.Width / 3, workArea.Y, workArea.Width / 3, workArea.Height),

            SnapPosition.RightThird => new MonitorRect(
                workArea.X + workArea.Width * 2 / 3, workArea.Y, workArea.Width / 3, workArea.Height),

            SnapPosition.LeftTwoThirds => new MonitorRect(
                workArea.X, workArea.Y, workArea.Width * 2 / 3, workArea.Height),

            SnapPosition.RightTwoThirds => new MonitorRect(
                workArea.X + workArea.Width / 3, workArea.Y, workArea.Width * 2 / 3, workArea.Height),

            SnapPosition.Fullscreen => workArea,

            _ => workArea
        };
    }
}
