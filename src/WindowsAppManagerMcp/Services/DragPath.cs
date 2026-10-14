namespace WindowsAppManagerMcp.Services;

/// <summary>
/// The points a drag moves the pointer through (roadmap M4.3), from the press to the drop. Windows and applications start a
/// drag only once the pointer leaves a rectangle around the press (SM_CXDRAG x SM_CYDRAG, which DragDetect and OLE
/// drag-and-drop wait for), and some read only the first move after the press, so the first point is beyond twice the
/// threshold whenever the drag is that long: the system metric is for the system DPI, and an application on a monitor at
/// twice that scale measures in its own pixels. The rest follow evenly to the drop, which is always the last point.
/// </summary>
public static class DragPath
{
    /// <summary>The drag threshold when it cannot be read: Windows' default SM_CXDRAG and SM_CYDRAG.</summary>
    public const int DefaultThreshold = 4;

    /// <summary>How many times the threshold the first move goes beyond, on either axis.</summary>
    public const int StartMargin = 2;

    /// <summary>
    /// <paramref name="steps"/> points from (<paramref name="fromX"/>, <paramref name="fromY"/>), not included, to
    /// (<paramref name="toX"/>, <paramref name="toY"/>), the last one.
    /// </summary>
    /// <param name="threshold">SM_CXDRAG and SM_CYDRAG; null for <see cref="DefaultThreshold"/>.</param>
    public static IReadOnlyList<(int X, int Y)> Points(int fromX, int fromY, int toX, int toY, int steps, (int Width, int Height)? threshold)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(steps, 1);
        var (width, height) = threshold is { Width: > 0, Height: > 0 } read ? read : (DefaultThreshold, DefaultThreshold);
        long dx = (long)toX - fromX, dy = (long)toY - fromY;

        // The first point's share of the way: an even step, or more when an even step stays inside the start margin
        var first = 1.0 / steps;
        double limitX = StartMargin * width, limitY = StartMargin * height;
        if (Math.Abs(first * dx) <= limitX && Math.Abs(first * dy) <= limitY)
        {
            var beyondX = dx == 0 ? double.MaxValue : (limitX + 1) / Math.Abs(dx);
            var beyondY = dy == 0 ? double.MaxValue : (limitY + 1) / Math.Abs(dy);
            first = Math.Max(first, Math.Min(1.0, Math.Min(beyondX, beyondY)));
        }

        var points = new List<(int X, int Y)>(steps);
        for (var i = 0; i < steps; i++)
        {
            var t = steps == 1 ? 1.0 : first + (1.0 - first) * i / (steps - 1);
            points.Add(i == steps - 1
                ? (toX, toY)
                : ((int)(fromX + Math.Round(t * dx, MidpointRounding.AwayFromZero)), (int)(fromY + Math.Round(t * dy, MidpointRounding.AwayFromZero))));
        }

        return points;
    }
}
