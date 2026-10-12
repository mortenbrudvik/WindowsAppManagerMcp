using WindowsAppManagerMcp.Models;
using WindowsAppManagerMcp.Services.Interfaces;

namespace WindowsAppManagerMcp.Services;

// The mouse path's click point, read on the worker inside the gate and hit-tested with UI Automation before the click. The
// input guard checks only the top-level window at the point, so an element of the same window that covers this one (a
// sticky header, a cookie banner, an in-page overlay, an open dropdown) would get the click without the hit test.
public sealed partial class ElementActionService
{
    /// <summary>How many levels a hit test walks up, from the element found at a point and from the element, to relate them.</summary>
    private const int MaxHitTestLevels = 32;

    /// <summary>
    /// The click point now, on the worker: UI Automation's clickable point, else the centre of the element's bounds (as far as
    /// they are inside its window), then the centres of the bounds' quarters, each hit-tested with UI Automation
    /// (<see cref="IUiaTree.FromPoint"/>). The first point whose element is the element, or an element inside it, is the
    /// click point. Without one, the first point where the hit test finds an element that holds the element (some providers
    /// do not hit-test their inner elements) or cannot tell is clicked, with a warning; when another element is at every
    /// point, the element is covered. The hit test runs here, before the gate is released, and never inside the input
    /// service's rechecks (<see cref="InputTarget.PointRecheck"/>): a cross-process UI Automation call there could hang the
    /// gate. Something that moves over the point between this check and the click is not caught.
    /// </summary>
    private ReadOutcome<ClickPoint> ClickPointOf(ActionCall call, PreparedElement element)
    {
        var node = element.Node;
        try
        {
            var fresh = _tree.ReadElement(node);
            if (fresh.Properties.IsOffscreen || fresh.Properties.Bounds.ToBounds() is not { Width: > 0, Height: > 0 } bounds)
            {
                return ReadOutcome<ClickPoint>.Of(ClickPoint.None);
            }

            var points = PointsToTry(TryState(() => _tree.GetClickablePoint(node)), InsideWindow(bounds, call.Window));
            IReadOnlyList<IUiaNode>? ancestors = null;
            ClickPoint? unverified = null;
            foreach (var point in points)
            {
                switch (HitTest(point, node, () => ancestors ??= AncestorsOf(node)))
                {
                    case Hit.Element:
                        return ReadOutcome<ClickPoint>.Of(new ClickPoint(point));
                    case Hit.Ancestor:
                        unverified ??= new ClickPoint(point,
                            $"The click point ({point.X}, {point.Y}) could not be checked: UI Automation finds an element there that " +
                            "holds this one, not the element itself (some applications do not report their inner elements to a hit " +
                            "test), so the click may have reached another part of it.");
                        break;
                    case Hit.Unknown:
                        unverified ??= new ClickPoint(point,
                            $"The click point ({point.X}, {point.Y}) could not be checked: UI Automation could not tell which element " +
                            "is there.");
                        break;
                }
            }

            return ReadOutcome<ClickPoint>.Of(unverified ?? new ClickPoint(points[0], Covered: true, Tried: points.Count));
        }
        catch (UiaElementUnavailableException)
        {
            return ReadOutcome<ClickPoint>.Of(ClickPoint.None);
        }
    }

    /// <summary>The refusal of an element that another element covers at every point tried.</summary>
    private static ElementActionResult Covered(ActionCall call, PreparedElement element, ClickPoint covered)
    {
        var point = covered.Point!.Value;
        var others = covered.Tried switch
        {
            1 => string.Empty,
            2 => " and at another point of its bounds",
            _ => $" and at {covered.Tried - 1} other points of its bounds"
        };
        return call.Fail(nameof(InputErrorCode.TargetOccluded),
            $"{Name(element.Element)} is covered: at its click point ({point.X}, {point.Y}){others} UI Automation finds another " +
            "element (an overlay, a sticky header, a banner or an open dropdown), so the click would reach that one. Nothing was " +
            "clicked. Scroll the element out from under it (action scroll_into_view, or scroll with amount line), or close what " +
            "covers it (get_element_at_point shows what is there), then try again.");
    }

    /// <summary>
    /// The points to try, in order: UI Automation's clickable point, the centre of <paramref name="bounds"/>, then the centres
    /// of its quarters; each once.
    /// </summary>
    private static List<UiaPoint> PointsToTry(UiaPoint? clickable, BoundsDto bounds)
    {
        var (x, y, width, height) = (bounds.X, bounds.Y, bounds.Width, bounds.Height);
        var (left, middle, right) = (x + width / 4, x + width / 2, x + width - width / 4);
        var (top, centre, bottom) = (y + height / 4, y + height / 2, y + height - height / 4);
        var points = new List<UiaPoint>();
        foreach (var point in new UiaPoint?[]
                 {
                     clickable, new UiaPoint(middle, centre), new UiaPoint(left, top), new UiaPoint(right, top),
                     new UiaPoint(left, bottom), new UiaPoint(right, bottom)
                 })
        {
            if (point is { } candidate && !points.Contains(candidate))
            {
                points.Add(candidate);
            }
        }

        return points;
    }

    /// <summary>The part of <paramref name="bounds"/> inside the window's rectangle; the bounds as they are when there is none.</summary>
    private BoundsDto InsideWindow(BoundsDto bounds, nint window)
    {
        if (TryState(() => _windows.GetWindowRect(window)) is not { } rect)
        {
            return bounds;
        }

        var left = Math.Max(bounds.X, rect.Left);
        var top = Math.Max(bounds.Y, rect.Top);
        var right = Math.Min((long)bounds.X + bounds.Width, rect.Right);
        var bottom = Math.Min((long)bounds.Y + bounds.Height, rect.Bottom);
        return right > left && bottom > top ? new BoundsDto(left, top, (int)(right - left), (int)(bottom - top)) : bounds;
    }

    /// <summary>
    /// What UI Automation finds at <paramref name="point"/>: the element or one inside it, an element that holds it (one of
    /// <paramref name="ancestors"/>), another element, or nothing it can tell (no element, or the hit test failed).
    /// </summary>
    private Hit HitTest(UiaPoint point, IUiaNode node, Func<IReadOnlyList<IUiaNode>> ancestors)
    {
        try
        {
            if (_tree.FromPoint(point.X, point.Y) is not { } hit)
            {
                return Hit.Unknown;
            }

            var current = hit;
            for (var level = 0; level <= MaxHitTestLevels && current != null; level++)
            {
                if (_tree.AreSameElement(current, node))
                {
                    return Hit.Element;
                }

                current = _tree.GetParent(current);
            }

            return ancestors().Any(ancestor => _tree.AreSameElement(ancestor, hit)) ? Hit.Ancestor : Hit.Other;
        }
        catch (Exception ex) when (ex is not (TimeoutException or OperationCanceledException))
        {
            return Hit.Unknown;
        }
    }

    /// <summary>The element's ancestors, nearest first, up to <see cref="MaxHitTestLevels"/>; those read before a failure.</summary>
    private List<IUiaNode> AncestorsOf(IUiaNode node)
    {
        var ancestors = new List<IUiaNode>();
        try
        {
            for (var current = _tree.GetParent(node); current != null && ancestors.Count < MaxHitTestLevels; current = _tree.GetParent(current))
            {
                ancestors.Add(current);
            }
        }
        catch (Exception ex) when (ex is not (TimeoutException or OperationCanceledException))
        {
            // The ancestors read so far
        }

        return ancestors;
    }

    /// <summary>What a hit test finds at a point.</summary>
    private enum Hit
    {
        /// <summary>The element, or an element inside it.</summary>
        Element,

        /// <summary>An element that holds the element.</summary>
        Ancestor,

        /// <summary>No element, or the hit test failed: it cannot be told.</summary>
        Unknown,

        /// <summary>Another element.</summary>
        Other
    }

    /// <summary>Where the mouse path clicks.</summary>
    /// <param name="Point">The point; null when the element has no point on screen.</param>
    /// <param name="Warning">Why the point could not be checked, when it could not.</param>
    /// <param name="Covered">Another element is at every point tried (<paramref name="Point"/> is the first of them).</param>
    /// <param name="Tried">How many points were tried.</param>
    private sealed record ClickPoint(UiaPoint? Point, string? Warning = null, bool Covered = false, int Tried = 1)
    {
        public static ClickPoint None { get; } = new((UiaPoint?)null);
    }
}
