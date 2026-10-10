using WindowsAppManagerMcp.Models;
using WindowsAppManagerMcp.Native;
using WindowsAppManagerMcp.Services.Interfaces;

namespace WindowsAppManagerMcp.Services;

public class MonitorService : IMonitorService
{
    private readonly List<(nint Handle, MonitorInfo Info)> _monitors = [];
    private readonly INativeMonitorWrapper _nativeMonitors;

    public MonitorService()
        : this(new NativeMonitorWrapper())
    {
    }

    public MonitorService(INativeMonitorWrapper nativeMonitors)
    {
        _nativeMonitors = nativeMonitors;
        RefreshMonitors();
    }

    private void RefreshMonitors()
    {
        _monitors.Clear();
        foreach (var monitor in _nativeMonitors.EnumerateMonitors())
        {
            _monitors.Add(monitor);
        }
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
        var hMonitor = _nativeMonitors.MonitorFromPoint(x, y);
        return _monitors.FirstOrDefault(m => m.Handle == hMonitor).Info;
    }

    public MonitorInfo? GetMonitorForWindow(nint windowHandle)
    {
        RefreshMonitors();
        var hMonitor = _nativeMonitors.MonitorFromWindow(windowHandle);
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
