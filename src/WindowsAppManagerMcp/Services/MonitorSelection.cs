using System.Globalization;
using WindowsAppManagerMcp.Models;

namespace WindowsAppManagerMcp.Services;

/// <summary>
/// Resolves a `monitor` argument (roadmap M6.7, owner decision D13) against a list of monitors: a pure function, tested
/// directly; <see cref="MonitorService.ResolveMonitor"/> runs it on its snapshot. It never guesses: a name that fits no
/// monitor is MonitorNotFound, and 'left' or 'right' where two monitors are stacked is AmbiguousMonitor with the candidates.
/// </summary>
public static class MonitorSelection
{
    /// <summary>The index of the whole virtual screen, every monitor in one image: 'all', or -1, where a capture takes it.</summary>
    public const int AllMonitors = -1;

    public const string Primary = "primary";
    public const string Left = "left";
    public const string Right = "right";
    public const string Current = "current";
    public const string All = "all";

    /// <summary>
    /// The monitor <paramref name="selector"/> names among <paramref name="monitors"/>. An index is passed on as given, with
    /// its monitor when there is one, so each tool keeps its own InvalidMonitorIndex and -1. 'primary' is the primary
    /// monitor; 'left' and 'right' (or 'leftmost' and 'rightmost') the monitor with the leftmost or rightmost centre;
    /// 'current' <paramref name="current"/>'s monitor (the foreground window's); 'all' <see cref="AllMonitors"/>; and an id or
    /// a device name ('\\.\DISPLAY2' or 'DISPLAY2') the monitor that has it, in any case.
    /// </summary>
    public static MonitorResolution Resolve(IReadOnlyList<MonitorInfo> monitors, MonitorSelector selector, Func<MonitorInfo?> current)
    {
        ArgumentNullException.ThrowIfNull(monitors);
        ArgumentNullException.ThrowIfNull(selector);
        ArgumentNullException.ThrowIfNull(current);

        if (selector.Index is { } index)
        {
            return MonitorResolution.Found(index, index >= 0 && index < monitors.Count ? monitors[index] : null);
        }

        var name = selector.Name!;
        if (Is(name, All))
        {
            return MonitorResolution.Found(AllMonitors, null);
        }

        if (monitors.Count == 0)
        {
            return NotFound($"No monitor was found, so monitor '{name}' names none.");
        }

        if (Is(name, Primary))
        {
            var primary = monitors.FirstOrDefault(m => m.IsPrimary) ?? monitors[0];
            return MonitorResolution.Found(primary.Index, primary);
        }

        if (Is(name, Left) || Is(name, "leftmost"))
        {
            return Outermost(monitors, rightmost: false, name);
        }

        if (Is(name, Right) || Is(name, "rightmost"))
        {
            return Outermost(monitors, rightmost: true, name);
        }

        if (Is(name, Current))
        {
            return current() is { } monitor
                ? MonitorResolution.Found(monitor.Index, monitor)
                : NotFound("monitor 'current' is the foreground window's monitor, and no window is in the foreground right now " +
                           "(windows may be switching, or a UAC prompt or the lock screen is shown). Try again shortly, or name the monitor.");
        }

        if (name.StartsWith(MonitorIdentity.IdPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return monitors.FirstOrDefault(m => Is(name, m.Id)) is { } withId
                ? MonitorResolution.Found(withId.Index, withId)
                : NotFound($"No monitor has the id '{name}' now ({Ids(monitors)}). An id follows a monitor on its port; on another " +
                           "port or dock it gets a new one. Call get_monitors.");
        }

        if (monitors.FirstOrDefault(m => IsDeviceName(name, m.DeviceName)) is { } named)
        {
            return MonitorResolution.Found(named.Index, named);
        }

        return NotFound($"monitor '{name}' names no monitor. It takes an index (0 to {monitors.Count - 1}), 'primary', 'left', " +
                        "'right', 'current' (the foreground window's monitor), or an id or name from get_monitors.");
    }

    /// <summary>
    /// Where <paramref name="monitor"/> is from <paramref name="primary"/>: 'above' or 'below' when it shares more than half of
    /// the narrower one's columns with the primary (stacked, as 'left' and 'right' tell them); otherwise 'left' or 'right' when
    /// its centre is beyond the primary's left or right edge, and 'above' or 'below' when it is not; null for the primary.
    /// </summary>
    public static string? RelationToPrimary(MonitorInfo monitor, MonitorInfo primary)
    {
        ArgumentNullException.ThrowIfNull(monitor);
        ArgumentNullException.ThrowIfNull(primary);
        if (monitor.Index == primary.Index && monitor.DeviceName == primary.DeviceName)
        {
            return null;
        }

        var (x, y) = Centre(monitor.Bounds);
        var p = primary.Bounds;
        if (Stacked(monitor.Bounds, p))
        {
            return y < Centre(p).Y ? "above" : "below";
        }

        return x < p.X ? Left
            : x >= (long)p.X + p.Width ? Right
            : y < Centre(p).Y ? "above"
            : "below";
    }

    /// <summary>
    /// The monitor whose centre is furthest left or right. A monitor that shares more than half of the narrower one's columns
    /// with it is stacked above or below it, so neither is further left or right: AmbiguousMonitor with both. With one
    /// monitor none is left or right of another: MonitorNotFound.
    /// </summary>
    private static MonitorResolution Outermost(IReadOnlyList<MonitorInfo> monitors, bool rightmost, string name)
    {
        if (monitors.Count == 1)
        {
            return NotFound($"There is one monitor, so monitor '{name}' names none: no monitor is left or right of another. Use " +
                            "'primary' or 0.");
        }

        var ordered = rightmost
            ? monitors.OrderByDescending(m => Centre(m.Bounds).X).ToList()
            : monitors.OrderBy(m => Centre(m.Bounds).X).ToList();
        var outermost = ordered[0];
        var stacked = monitors.Where(m => m == outermost || Stacked(m.Bounds, outermost.Bounds)).OrderBy(m => m.Index).ToList();
        if (stacked.Count > 1)
        {
            return MonitorResolution.Failed(nameof(WindowErrorCode.AmbiguousMonitor),
                $"monitor '{name}' is ambiguous: {string.Join(" and ", stacked.Select(Describe))} are stacked, so neither is " +
                $"further {(rightmost ? "right" : "left")}. Pass the index or id of the one you mean. Nothing was done.");
        }

        return MonitorResolution.Found(outermost.Index, outermost);
    }

    /// <summary>Whether two monitors share more than half of the narrower one's columns.</summary>
    private static bool Stacked(MonitorRect a, MonitorRect b)
    {
        var overlap = Math.Min((long)a.X + a.Width, (long)b.X + b.Width) - Math.Max(a.X, b.X);
        return overlap * 2 > Math.Min(a.Width, b.Width);
    }

    private static (double X, double Y) Centre(MonitorRect rect) => (rect.X + rect.Width / 2.0, rect.Y + rect.Height / 2.0);

    private static bool Is(string name, string? value) => string.Equals(name, value, StringComparison.OrdinalIgnoreCase);

    /// <summary>Whether <paramref name="name"/> is the device name (\\.\DISPLAY2), or the device name without its \\.\ prefix.</summary>
    private static bool IsDeviceName(string name, string deviceName) =>
        Is(name, deviceName) || deviceName.EndsWith(@"\" + name, StringComparison.OrdinalIgnoreCase) && !name.Contains('\\');

    private static string Ids(IReadOnlyList<MonitorInfo> monitors)
    {
        var ids = monitors.Where(m => m.Id != null).Select(m => $"{m.Id} is monitor {m.Index}").ToList();
        return ids.Count == 0 ? "no monitor has an id: the display configuration could not be read" : string.Join(", ", ids);
    }

    private static string Describe(MonitorInfo monitor) =>
        string.Create(CultureInfo.InvariantCulture,
            $"{monitor.Index} ({monitor.DeviceName}{(monitor.Id is { } id ? ", " + id : string.Empty)}, at {monitor.Bounds.X},{monitor.Bounds.Y} " +
            $"{monitor.Bounds.Width}x{monitor.Bounds.Height})");

    private static MonitorResolution NotFound(string error) =>
        MonitorResolution.Failed(nameof(WindowErrorCode.MonitorNotFound), error + " Nothing was done.");
}
