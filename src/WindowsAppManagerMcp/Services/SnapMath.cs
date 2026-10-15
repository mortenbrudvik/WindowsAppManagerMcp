using WindowsAppManagerMcp.Models;

namespace WindowsAppManagerMcp.Services;

/// <summary>
/// The snap positions as rectangles of a work area (roadmap M2.3): a pure function, so it is tested directly. Every edge
/// is at X + W*k/n (and Y + H*k/n), and each part ends where the next one starts, so the parts of a split tile the work
/// area exactly: no pixel is lost on odd sizes (thirds of 2560 are 853, 853 and 854 wide) and none is covered twice.
/// The rectangles are the visible frames the windows get; WindowService adds the invisible resize borders.
/// </summary>
public static class SnapMath
{
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="position"/> is not a snap position.</exception>
    public static MonitorRect Calculate(MonitorRect workArea, SnapPosition position)
    {
        ArgumentNullException.ThrowIfNull(workArea);

        return position switch
        {
            SnapPosition.LeftHalf => Part(workArea, columns: (0, 1, 2), rows: (0, 1, 1)),
            SnapPosition.RightHalf => Part(workArea, columns: (1, 2, 2), rows: (0, 1, 1)),
            SnapPosition.TopHalf => Part(workArea, columns: (0, 1, 1), rows: (0, 1, 2)),
            SnapPosition.BottomHalf => Part(workArea, columns: (0, 1, 1), rows: (1, 2, 2)),
            SnapPosition.TopLeftQuarter => Part(workArea, columns: (0, 1, 2), rows: (0, 1, 2)),
            SnapPosition.TopRightQuarter => Part(workArea, columns: (1, 2, 2), rows: (0, 1, 2)),
            SnapPosition.BottomLeftQuarter => Part(workArea, columns: (0, 1, 2), rows: (1, 2, 2)),
            SnapPosition.BottomRightQuarter => Part(workArea, columns: (1, 2, 2), rows: (1, 2, 2)),
            SnapPosition.LeftThird => Part(workArea, columns: (0, 1, 3), rows: (0, 1, 1)),
            SnapPosition.CenterThird => Part(workArea, columns: (1, 2, 3), rows: (0, 1, 1)),
            SnapPosition.RightThird => Part(workArea, columns: (2, 3, 3), rows: (0, 1, 1)),
            SnapPosition.LeftTwoThirds => Part(workArea, columns: (0, 2, 3), rows: (0, 1, 1)),
            SnapPosition.RightTwoThirds => Part(workArea, columns: (1, 3, 3), rows: (0, 1, 1)),
            SnapPosition.Fullscreen => workArea,
            _ => throw new ArgumentOutOfRangeException(nameof(position), position, "Not a snap position.")
        };
    }

    /// <summary>
    /// The part of the work area from edge <c>From</c> to edge <c>To</c> of <c>Of</c> equal columns, and likewise for the
    /// rows.
    /// </summary>
    private static MonitorRect Part(MonitorRect workArea, (int From, int To, int Of) columns, (int From, int To, int Of) rows)
    {
        var left = Edge(workArea.X, workArea.Width, columns.From, columns.Of);
        var right = Edge(workArea.X, workArea.Width, columns.To, columns.Of);
        var top = Edge(workArea.Y, workArea.Height, rows.From, rows.Of);
        var bottom = Edge(workArea.Y, workArea.Height, rows.To, rows.Of);
        return new MonitorRect(left, top, right - left, bottom - top);
    }

    /// <summary>Edge <paramref name="k"/> of <paramref name="n"/> equal parts: origin + length*k/n, computed without overflow.</summary>
    internal static int Edge(int origin, int length, int k, int n) => origin + (int)((long)length * k / n);

    /// <summary>
    /// How far below a whole pixel a computed edge may land and still count as that pixel: far more than the error of a
    /// double product of screen sizes (about 1e-11 px), far less than the smallest fraction of a pixel an edge k/n of a
    /// screen can really have.
    /// </summary>
    private const double EdgeTolerance = 1e-6;

    /// <summary>
    /// The edge at <paramref name="fraction"/> of the way along: origin + floor(length*fraction), the same rule as
    /// <see cref="Edge(int, int, int, int)"/>, so fraction k/n gives exactly the snap edge k of n. The floor forgives
    /// floating-point error (0.29 * 100 is 28.999999999999996, which is edge 29), so an edge given as a percentage that
    /// is whole in decimal is not a pixel short. Rectangles that share an edge fraction share the pixel edge, which is
    /// what makes them tile (RelativeRectMath).
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">The fraction is not finite, or the edge is outside the int range.</exception>
    internal static int Edge(int origin, int length, double fraction)
    {
        if (!double.IsFinite(fraction))
            throw new ArgumentOutOfRangeException(nameof(fraction), fraction, "Not a finite number.");

        var edge = origin + Math.Floor(length * fraction + EdgeTolerance);
        if (edge < int.MinValue || edge > int.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(fraction), fraction, "The edge is outside the virtual screen's coordinates.");

        return (int)edge;
    }
}
