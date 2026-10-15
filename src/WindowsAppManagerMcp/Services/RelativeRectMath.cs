using System.Globalization;
using WindowsAppManagerMcp.Models;

namespace WindowsAppManagerMcp.Services;

/// <summary>
/// Rectangles given relative to a monitor, as percentages (M6.7's set_window_bounds unit=percent) or fractions (layout
/// presets, M6.8), in virtual-screen pixels: a pure function, tested directly. Each edge is computed on its own through
/// SnapMath's edge rule (origin + floor(length * share)), never as origin plus a separately rounded size, so:
/// <list type="bullet">
/// <item>rectangles that share an edge in percent share it in pixels: no gap and no overlap between tiles;</item>
/// <item>the percentages of a snap position (50, 100/3, ...) give exactly snap_window's rectangle;</item>
/// <item>a monitor left of or above the primary (negative origin) behaves exactly like one at 0,0.</item>
/// </list>
/// The monitor's scale factor does not enter: virtual-screen coordinates are physical pixels, so 50 % of a 150 % 4K
/// monitor is 1920 px and 50 % of a 100 % 1080p monitor is 960 px. A caller converts once, against the target monitor,
/// before the window moves, and must not convert again when the placement is repeated after a DPI change.
/// </summary>
public static class RelativeRectMath
{
    /// <summary>
    /// How far over 100 a sum of percentages may be and still count as 100: floating-point error only
    /// (70.00000000000001 + 30 is 100.00000000000001).
    /// </summary>
    private const double PercentTolerance = 1e-9;

    /// <summary>The rectangle percentages and fractions of <paramref name="monitor"/> are measured against.</summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="relativeTo"/> is not a <see cref="RelativeArea"/>.</exception>
    public static MonitorRect AreaOf(MonitorInfo monitor, RelativeArea relativeTo)
    {
        ArgumentNullException.ThrowIfNull(monitor);

        return relativeTo switch
        {
            RelativeArea.WorkArea => monitor.WorkArea,
            RelativeArea.Monitor => monitor.Bounds,
            _ => throw new ArgumentOutOfRangeException(nameof(relativeTo), relativeTo, "Not a relative area.")
        };
    }

    /// <summary>
    /// Why the percentages are not a rectangle inside the area, or null when they are: each must be a finite number,
    /// x and y from 0 to 100, width and height above 0 and at most 100, and the rectangle must end inside the area
    /// (x + width and y + height at most 100, give or take floating-point error). The message starts with the value's
    /// name, so a tool can return it as is.
    /// </summary>
    public static string? CheckPercent(double x, double y, double width, double height)
    {
        return Position(x, nameof(x)) ?? Position(y, nameof(y)) ?? Size(width, nameof(width)) ?? Size(height, nameof(height))
            ?? End(x, width, "x + width") ?? End(y, height, "y + height");

        static string? Position(double value, string name) =>
            !double.IsFinite(value) ? $"{name} must be a number from 0 to 100, not {Format(value)}."
            : value is < 0 or > 100 ? $"{name} must be from 0 to 100 (percent of the area), not {Format(value)}."
            : null;

        static string? Size(double value, string name) =>
            !double.IsFinite(value) ? $"{name} must be a number above 0 and at most 100, not {Format(value)}."
            : value is <= 0 or > 100 ? $"{name} must be above 0 and at most 100 (percent of the area), not {Format(value)}."
            : null;

        static string? End(double start, double size, string name) =>
            start + size > 100 + PercentTolerance
                ? $"{name} must be at most 100, so the rectangle ends inside the area, not {Format(start + size)}."
                : null;

        static string Format(double value) => value.ToString("R", CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// The rectangle at <paramref name="x"/>, <paramref name="y"/> of size <paramref name="width"/> x
    /// <paramref name="height"/>, all in percent of <paramref name="area"/>, in virtual-screen pixels.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">The percentages fail <see cref="CheckPercent"/>; the message is its message.</exception>
    public static MonitorRect FromPercent(MonitorRect area, double x, double y, double width, double height)
    {
        ArgumentNullException.ThrowIfNull(area);
        if (CheckPercent(x, y, width, height) is { } error)
            throw new ArgumentOutOfRangeException(error, (Exception?)null);

        // An end within the tolerance over 100 is the area's far edge
        return Rect(area, x / 100, y / 100, Math.Min(1, (x + width) / 100), Math.Min(1, (y + height) / 100));
    }

    /// <summary>The percent rectangle on <paramref name="monitor"/>'s work area or full bounds.</summary>
    /// <exception cref="ArgumentOutOfRangeException">
    /// The percentages fail <see cref="CheckPercent"/>, or <paramref name="relativeTo"/> is not a <see cref="RelativeArea"/>.
    /// </exception>
    public static MonitorRect FromPercent(MonitorInfo monitor, RelativeArea relativeTo, double x, double y, double width, double height) =>
        FromPercent(AreaOf(monitor, relativeTo), x, y, width, height);

    /// <summary>
    /// The rectangle at fractions <paramref name="x"/>, <paramref name="y"/> of size <paramref name="width"/> x
    /// <paramref name="height"/> (1 is the whole area) of <paramref name="area"/>, in virtual-screen pixels. Unlike
    /// percentages, fractions are not limited to the area: a saved window that hung off its work area keeps doing so.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">A value is not finite, or an edge is outside the int range.</exception>
    public static MonitorRect FromFractions(MonitorRect area, double x, double y, double width, double height)
    {
        ArgumentNullException.ThrowIfNull(area);
        return Rect(area, x, y, x + width, y + height);
    }

    /// <summary>The rectangle of a layout placement's <paramref name="position"/> on <paramref name="area"/>.</summary>
    /// <exception cref="ArgumentOutOfRangeException">A value is not finite, or an edge is outside the int range.</exception>
    public static MonitorRect FromFractions(MonitorRect area, RelativePosition position)
    {
        ArgumentNullException.ThrowIfNull(position);
        return FromFractions(area, position.X, position.Y, position.Width, position.Height);
    }

    /// <summary>The rectangle between the edges at the given fractions of the area's width and height.</summary>
    private static MonitorRect Rect(MonitorRect area, double left, double top, double right, double bottom)
    {
        var leftEdge = SnapMath.Edge(area.X, area.Width, left);
        var topEdge = SnapMath.Edge(area.Y, area.Height, top);
        var rightEdge = SnapMath.Edge(area.X, area.Width, right);
        var bottomEdge = SnapMath.Edge(area.Y, area.Height, bottom);
        return new MonitorRect(leftEdge, topEdge, rightEdge - leftEdge, bottomEdge - topEdge);
    }
}
