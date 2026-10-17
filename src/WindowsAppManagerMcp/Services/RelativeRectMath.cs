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
        return CheckPercentPosition(x, y) ?? CheckPercentSize(width, height)
            ?? End(x, width, "x + width") ?? End(y, height, "y + height");

        static string? End(double start, double size, string name) =>
            start + size > 100 + PercentTolerance
                ? $"{name} must be at most 100, so the rectangle ends inside the area, not {Format(start + size)}."
                : null;
    }

    /// <summary>
    /// <see cref="CheckPercent"/> for a position alone (set_window_bounds with x and y only): each a finite number from 0 to
    /// 100. Null when they are.
    /// </summary>
    public static string? CheckPercentPosition(double x, double y) => Position(x, nameof(x)) ?? Position(y, nameof(y));

    /// <summary>
    /// <see cref="CheckPercent"/> for a size alone (set_window_bounds with width and height only): each a finite number above 0
    /// and at most 100. Null when they are.
    /// </summary>
    public static string? CheckPercentSize(double width, double height) => Size(width, nameof(width)) ?? Size(height, nameof(height));

    private static string? Position(double value, string name) =>
        !double.IsFinite(value) ? $"{name} must be a number from 0 to 100, not {Format(value)}."
        : value is < 0 or > 100 ? $"{name} must be from 0 to 100 (percent of the area), not {Format(value)}."
        : null;

    private static string? Size(double value, string name) =>
        !double.IsFinite(value) ? $"{name} must be a number above 0 and at most 100, not {Format(value)}."
        : value is <= 0 or > 100 ? $"{name} must be above 0 and at most 100 (percent of the area), not {Format(value)}."
        : null;

    private static string Format(double value) => value.ToString("R", CultureInfo.InvariantCulture);

    /// <summary>
    /// The point at <paramref name="x"/>, <paramref name="y"/> percent of <paramref name="area"/>: the top-left corner
    /// <see cref="FromPercent(MonitorRect, double, double, double, double)"/> gives for them, for a window moved without
    /// resizing it.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">The percentages fail <see cref="CheckPercentPosition"/> (the message is its message).</exception>
    public static (int X, int Y) PointFromPercent(MonitorRect area, double x, double y)
    {
        ArgumentNullException.ThrowIfNull(area);
        if (CheckPercentPosition(x, y) is { } error)
            throw new ArgumentOutOfRangeException(error, (Exception?)null);

        return (SnapMath.Edge(area.X, area.Width, x / 100), SnapMath.Edge(area.Y, area.Height, y / 100));
    }

    /// <summary>
    /// The size of <paramref name="width"/> x <paramref name="height"/> percent of <paramref name="area"/>, as
    /// <see cref="FromPercent(MonitorRect, double, double, double, double)"/> gives it from the area's corner, for a window
    /// resized without moving it.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">
    /// The percentages fail <see cref="CheckPercentSize"/> (the message is its message), or the width or height is less than
    /// one pixel on <paramref name="area"/>.
    /// </exception>
    public static (int Width, int Height) SizeFromPercent(MonitorRect area, double width, double height)
    {
        ArgumentNullException.ThrowIfNull(area);
        if (CheckPercentSize(width, height) is { } error)
            throw new ArgumentOutOfRangeException(error, (Exception?)null);

        var rect = Rect(area, 0, 0, width / 100, height / 100);
        return (rect.Width, rect.Height);
    }

    /// <summary>
    /// The rectangle at <paramref name="x"/>, <paramref name="y"/> of size <paramref name="width"/> x
    /// <paramref name="height"/>, all in percent of <paramref name="area"/>, in virtual-screen pixels.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">
    /// The percentages fail <see cref="CheckPercent"/> (the message is its message), or the width or height is less
    /// than one pixel on <paramref name="area"/> (valid percentages whose two edges floor to the same pixel).
    /// </exception>
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
    /// The percentages fail <see cref="CheckPercent"/>, the width or height is less than one pixel on the area, or
    /// <paramref name="relativeTo"/> is not a <see cref="RelativeArea"/>.
    /// </exception>
    public static MonitorRect FromPercent(MonitorInfo monitor, RelativeArea relativeTo, double x, double y, double width, double height) =>
        FromPercent(AreaOf(monitor, relativeTo), x, y, width, height);

    /// <summary>
    /// The rectangle at fractions <paramref name="x"/>, <paramref name="y"/> of size <paramref name="width"/> x
    /// <paramref name="height"/> (1 is the whole area) of <paramref name="area"/>, in virtual-screen pixels. Unlike
    /// percentages, fractions are not limited to the area: a saved window that hung off its work area keeps doing so.
    /// The width and height must still be above 0, as fractions and in pixels: a size of 0 or less is not a window,
    /// wherever it hangs, and neither is one that floors to 0 px.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">
    /// A value is not finite, the width or height is not above 0 or is less than one pixel on the area, or an edge or
    /// the pixel width or height is outside the int range.
    /// </exception>
    public static MonitorRect FromFractions(MonitorRect area, double x, double y, double width, double height)
    {
        ArgumentNullException.ThrowIfNull(area);
        if (!(width > 0))
            throw new ArgumentOutOfRangeException(nameof(width), width, "The width must be above 0.");
        if (!(height > 0))
            throw new ArgumentOutOfRangeException(nameof(height), height, "The height must be above 0.");

        return Rect(area, x, y, x + width, y + height);
    }

    /// <summary>The rectangle of a layout placement's <paramref name="position"/> on <paramref name="area"/>.</summary>
    /// <exception cref="ArgumentOutOfRangeException">
    /// A value is not finite, the width or height is not above 0 or is less than one pixel on the area, or an edge or the pixel width or height is outside
    /// the int range.
    /// </exception>
    public static MonitorRect FromFractions(MonitorRect area, RelativePosition position)
    {
        ArgumentNullException.ThrowIfNull(position);
        return FromFractions(area, position.X, position.Y, position.Width, position.Height);
    }

    /// <summary>The rectangle between the edges at the given fractions of the area's width and height.</summary>
    /// <exception cref="ArgumentOutOfRangeException">
    /// An edge is outside the int range, or the edges are further apart than an int can hold (two edges that each fit
    /// can be up to twice that far apart), or they are on the same pixel or the wrong way round.
    /// </exception>
    private static MonitorRect Rect(MonitorRect area, double left, double top, double right, double bottom)
    {
        var leftEdge = SnapMath.Edge(area.X, area.Width, left);
        var topEdge = SnapMath.Edge(area.Y, area.Height, top);
        var rightEdge = SnapMath.Edge(area.X, area.Width, right);
        var bottomEdge = SnapMath.Edge(area.Y, area.Height, bottom);
        return new MonitorRect(leftEdge, topEdge, Size(leftEdge, rightEdge, "width"), Size(topEdge, bottomEdge, "height"));

        // Subtracted as long: in int, edges 3.84e9 px apart would wrap to a negative size without an error. A size above
        // 0 as a share can still floor to 0 px (1e-4 of 1920 is 0.192 px): a 0-px rectangle is not a window either, and
        // SetWindowBounds would report success for it, so it is refused here rather than left to each caller.
        static int Size(int start, int end, string name)
        {
            var size = (long)end - start;
            return size switch
            {
                <= 0 => throw new ArgumentOutOfRangeException(name, size, $"The {name} is less than one pixel on this area."),
                > int.MaxValue => throw new ArgumentOutOfRangeException(name, size, $"The {name} in pixels is outside the virtual screen's coordinates."),
                _ => (int)size
            };
        }
    }
}
