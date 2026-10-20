using WindowsAppManagerMcp.Models;

namespace WindowsAppManagerMcp.Services;

/// <summary>
/// Converts a restore rectangle between workspace and screen coordinates (roadmap M6.8). GetWindowPlacement and
/// SetWindowPlacement give a top-level window's rectangles in workspace coordinates, which start at the top-left corner of
/// the work area of the monitor the rectangle is on: with the taskbar on the left or at the top, workspace (0, 0) is the
/// screen point beside or below it. A tool window (WS_EX_TOOLWINDOW) uses screen coordinates and needs no conversion. Pure.
/// </summary>
public static class WorkspaceCoordinates
{
    /// <summary>The rectangle in screen coordinates: shifted by the offset of its monitor's work area from the monitor's corner.</summary>
    public static WindowRect ToScreen(WindowRect workspace, IReadOnlyList<MonitorInfo> monitors)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        var (dx, dy) = Offset(workspace, monitors);
        return workspace with { X = workspace.X + dx, Y = workspace.Y + dy };
    }

    /// <summary>The rectangle in workspace coordinates: the inverse of <see cref="ToScreen"/>.</summary>
    public static WindowRect ToWorkspace(WindowRect screen, IReadOnlyList<MonitorInfo> monitors)
    {
        ArgumentNullException.ThrowIfNull(screen);
        var (dx, dy) = Offset(screen, monitors);
        return screen with { X = screen.X - dx, Y = screen.Y - dy };
    }

    /// <summary>
    /// The monitor of the rectangle as MonitorFromRect with MONITOR_DEFAULTTONEAREST picks it: the one with the largest share
    /// of the rectangle, else the nearest one. Null when there are no monitors.
    /// </summary>
    public static MonitorInfo? MonitorOf(WindowRect rect, IReadOnlyList<MonitorInfo> monitors)
    {
        ArgumentNullException.ThrowIfNull(rect);
        ArgumentNullException.ThrowIfNull(monitors);
        return monitors
            .OrderByDescending(m => Overlap(rect, m.Bounds))
            .ThenBy(m => Distance(rect, m.Bounds))
            .FirstOrDefault();
    }

    /// <summary>
    /// How far the work area of the rectangle's monitor (<see cref="MonitorOf"/>) starts from the monitor's corner. The offsets
    /// are a taskbar's size, so the monitor is the same whether the rectangle is read in workspace or screen coordinates
    /// except within a taskbar's width of a monitor's edge.
    /// </summary>
    private static (int X, int Y) Offset(WindowRect rect, IReadOnlyList<MonitorInfo> monitors) =>
        MonitorOf(rect, monitors) is { } monitor
            ? (monitor.WorkArea.X - monitor.Bounds.X, monitor.WorkArea.Y - monitor.Bounds.Y)
            : (0, 0);

    private static long Overlap(WindowRect rect, MonitorRect bounds)
    {
        long width = Math.Min((long)rect.X + rect.Width, (long)bounds.X + bounds.Width) - Math.Max(rect.X, bounds.X);
        long height = Math.Min((long)rect.Y + rect.Height, (long)bounds.Y + bounds.Height) - Math.Max(rect.Y, bounds.Y);
        return width > 0 && height > 0 ? width * height : 0;
    }

    /// <summary>The gap between the rectangle and the monitor, edge to edge (0 when they touch or overlap).</summary>
    private static long Distance(WindowRect rect, MonitorRect bounds)
    {
        long dx = Math.Max(0, Math.Max((long)bounds.X - (rect.X + (long)rect.Width), (long)rect.X - (bounds.X + (long)bounds.Width)));
        long dy = Math.Max(0, Math.Max((long)bounds.Y - (rect.Y + (long)rect.Height), (long)rect.Y - (bounds.Y + (long)bounds.Height)));
        return dx * dx + dy * dy;
    }
}
