using WindowsAppManagerMcp.Models;

namespace WindowsAppManagerMcp.Services;

/// <summary>
/// How a window's visible frame sits on its monitor (issue #12), for the window rows' zone and spansMonitors: the
/// snap_window position whose rectangle it fills, so a placement made by another program (a window manager) reads as one
/// field. Pure, so it is tested directly.
/// </summary>
/// <remarks>
/// The zones are SnapMath's rectangles of the monitor's work area, floor rounding included, so a frame snap_window placed
/// matches exactly. Each edge may be off by <see cref="Tolerance"/>, about 2 px times the monitor's scale, for a window
/// manager that rounds the other way; a frame with gaps or margins larger than that fills no zone.
/// </remarks>
public static class WindowZones
{
    /// <summary>The zone of a frame over the whole monitor, its taskbar included (a fullscreen video or game).</summary>
    public const string WholeMonitor = "monitor";

    /// <summary>How far an edge may be off a zone's edge at 100% scale.</summary>
    private const double TolerancePixels = 2;

    /// <summary>The snap positions in declaration order, with the names snap_window takes.</summary>
    private static readonly (SnapPosition Position, string Name)[] Positions =
        Enum.GetValues<SnapPosition>().Select(p => (p, ToolEnum<SnapPosition>.Name(p))).ToArray();

    /// <summary>How far, in pixels, an edge may be off a zone's edge on <paramref name="monitor"/>: 2 px times its scale, rounded up.</summary>
    public static int Tolerance(MonitorInfo monitor)
    {
        ArgumentNullException.ThrowIfNull(monitor);
        return (int)Math.Ceiling(TolerancePixels * monitor.ScaleFactor);
    }

    /// <summary>
    /// The zone <paramref name="frame"/> fills on <paramref name="monitor"/>'s work area: a snap_window position's name
    /// ("left_half", "fullscreen" for the whole work area, ...), or <see cref="WholeMonitor"/> for the monitor's bounds. A
    /// maximized window is "fullscreen" by its state. When the work area is the whole monitor (an auto-hide taskbar, or a
    /// monitor without one), a frame over it is "fullscreen". Null for a minimized window and a frame that fills no zone.
    /// </summary>
    public static string? Match(WindowRect frame, WindowState state, MonitorInfo monitor)
    {
        ArgumentNullException.ThrowIfNull(frame);
        ArgumentNullException.ThrowIfNull(monitor);

        if (state == WindowState.Minimized)
            return null;

        if (state == WindowState.Maximized)
            return ToolEnum<SnapPosition>.Name(SnapPosition.Fullscreen);

        // The closest zone within the tolerance; on a tie the snap position, in declaration order, before the monitor
        var tolerance = Tolerance(monitor);
        string? zone = null;
        var closest = int.MaxValue;
        foreach (var (position, name) in Positions)
        {
            Consider(SnapMath.Calculate(monitor.WorkArea, position), name);
        }

        Consider(monitor.Bounds, WholeMonitor);
        return zone;

        void Consider(MonitorRect rect, string name)
        {
            var off = EdgeDistance(frame, rect);
            if (off <= tolerance && off < closest)
            {
                zone = name;
                closest = off;
            }
        }
    }

    /// <summary>
    /// Whether <paramref name="frame"/> covers more than one monitor, each by more than the tolerance across and down, so an
    /// edge a window manager rounded onto the neighbour does not count. <paramref name="monitors"/> is read only for a frame
    /// that leaves <paramref name="monitor"/>, its own. False for a minimized window.
    /// </summary>
    public static bool Spans(WindowRect frame, WindowState state, MonitorInfo monitor, Func<IReadOnlyList<MonitorInfo>> monitors)
    {
        ArgumentNullException.ThrowIfNull(frame);
        ArgumentNullException.ThrowIfNull(monitor);
        ArgumentNullException.ThrowIfNull(monitors);

        if (state == WindowState.Minimized)
            return false;

        var tolerance = Tolerance(monitor);
        var own = monitor.Bounds;
        if (frame.Left >= own.X - tolerance && frame.Top >= own.Y - tolerance &&
            frame.Right <= own.X + own.Width + tolerance && frame.Bottom <= own.Y + own.Height + tolerance)
        {
            return false;
        }

        return monitors().Count(m => Covers(frame, m.Bounds, tolerance)) > 1;
    }

    /// <summary>The largest distance between an edge of <paramref name="frame"/> and the same edge of <paramref name="rect"/>.</summary>
    private static int EdgeDistance(WindowRect frame, MonitorRect rect) => Math.Max(
        Math.Max(Math.Abs(frame.Left - rect.X), Math.Abs(frame.Top - rect.Y)),
        Math.Max(Math.Abs(frame.Right - (rect.X + rect.Width)), Math.Abs(frame.Bottom - (rect.Y + rect.Height))));

    private static bool Covers(WindowRect frame, MonitorRect bounds, int tolerance)
    {
        var width = Math.Min(frame.Right, bounds.X + bounds.Width) - Math.Max(frame.Left, bounds.X);
        var height = Math.Min(frame.Bottom, bounds.Y + bounds.Height) - Math.Max(frame.Top, bounds.Y);
        return width > tolerance && height > tolerance;
    }
}
