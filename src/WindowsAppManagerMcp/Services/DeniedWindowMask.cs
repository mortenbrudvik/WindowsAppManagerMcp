using System.Drawing;
using WindowsAppManagerMcp.Native;
using WindowsAppManagerMcp.Services.Interfaces;

namespace WindowsAppManagerMcp.Services;

/// <summary>
/// The parts of the screen that show windows the user's target policy denies (roadmap M6.3, M6 decision D8): OCR turns pixels
/// into text an agent can log and search, so a monitor or region read blacks them out before OCR, as a window target is
/// refused. Read from the drawn top-level windows (visible, not minimized, not cloaked) in z-order: a denied window's
/// rectangle, less what opaque windows above it cover. Over-masking is the safe side: a denied window's rectangle includes
/// its invisible resize borders; only a window that is neither layered nor click-through counts as opaque, and only by its
/// visible frame; and a window the policy cannot check is denied (fail-closed). Masking by rectangle cannot hide denied
/// content another app draws (a remote desktop, a screen share).
/// </summary>
public sealed class DeniedWindowMask
{
    private readonly INativeWindowWrapper _windows;
    private readonly IWindowService _windowService;

    /// <param name="windows">Lists the top-level windows in z-order and reads their state and rectangles.</param>
    /// <param name="windowService">The target policy's fail-closed verdict on each window.</param>
    public DeniedWindowMask(INativeWindowWrapper windows, IWindowService windowService)
    {
        _windows = windows ?? throw new ArgumentNullException(nameof(windows));
        _windowService = windowService ?? throw new ArgumentNullException(nameof(windowService));
    }

    /// <summary>The screen areas of the denied windows as the screen shows them now, per window, in screen pixels.</summary>
    public IReadOnlyList<DeniedWindowArea> Read()
    {
        var topLevel = new List<nint>();
        _windows.EnumWindows(handle =>
        {
            topLevel.Add(handle);
            return true;
        });

        var denied = new List<DeniedWindowArea>();
        var covered = new List<Rectangle>();
        foreach (var handle in topLevel)
        {
            if (!_windows.IsWindowVisible(handle) || _windows.IsIconic(handle) || _windows.IsCloaked(handle) ||
                _windows.GetWindowRect(handle) is not { } rect)
            {
                continue;
            }

            var bounds = Rectangle.FromLTRB(rect.Left, rect.Top, rect.Right, rect.Bottom);
            if (bounds.Width <= 0 || bounds.Height <= 0)
            {
                continue;
            }

            var isDenied = !_windowService.EvaluateTargetPolicy(handle).Allowed;
            if (isDenied && Subtract(bounds, covered) is { Count: > 0 } visible)
            {
                denied.Add(new DeniedWindowArea(handle, visible));
            }

            // A denied window is blacked out where it shows, so it hides what is below it as well as an opaque one
            if (isDenied || IsOpaque(handle))
            {
                covered.Add(isDenied ? bounds : VisibleFrame(handle, bounds));
            }
        }

        return denied;
    }

    /// <summary>
    /// Blacks out the areas of <paramref name="reads"/> that fall on <paramref name="pixels"/> (top-down BGRA, in place) and
    /// returns how many windows that were: each window counts once, however many reads named it.
    /// </summary>
    public static int Apply(CaptureResult pixels, params IReadOnlyList<DeniedWindowArea>[] reads)
    {
        ArgumentNullException.ThrowIfNull(pixels);
        var image = new Rectangle(pixels.ScreenOriginX, pixels.ScreenOriginY, pixels.Width, pixels.Height);
        var masked = new HashSet<nint>();
        foreach (var area in reads.SelectMany(read => read))
        {
            foreach (var part in area.Areas)
            {
                var onImage = Rectangle.Intersect(part, image);
                if (onImage.Width <= 0 || onImage.Height <= 0)
                {
                    continue;
                }

                masked.Add(area.Window);
                var rowBytes = onImage.Width * 4;
                for (var y = onImage.Top; y < onImage.Bottom; y++)
                {
                    var start = ((y - image.Top) * pixels.Width + (onImage.Left - image.Left)) * 4;
                    Array.Clear(pixels.Bgra, start, rowBytes);
                }
            }
        }

        return masked.Count;
    }

    /// <summary>
    /// <paramref name="rectangle"/> without <paramref name="holes"/>, as disjoint rectangles (each hole splits what is left
    /// into at most four).
    /// </summary>
    public static IReadOnlyList<Rectangle> Subtract(Rectangle rectangle, IReadOnlyList<Rectangle> holes)
    {
        var parts = new List<Rectangle> { rectangle };
        foreach (var hole in holes)
        {
            var next = new List<Rectangle>(parts.Count);
            foreach (var part in parts)
            {
                var overlap = Rectangle.Intersect(part, hole);
                if (overlap.Width <= 0 || overlap.Height <= 0)
                {
                    next.Add(part);
                    continue;
                }

                AddIfNotEmpty(next, Rectangle.FromLTRB(part.Left, part.Top, part.Right, overlap.Top));
                AddIfNotEmpty(next, Rectangle.FromLTRB(part.Left, overlap.Bottom, part.Right, part.Bottom));
                AddIfNotEmpty(next, Rectangle.FromLTRB(part.Left, overlap.Top, overlap.Left, overlap.Bottom));
                AddIfNotEmpty(next, Rectangle.FromLTRB(overlap.Right, overlap.Top, part.Right, overlap.Bottom));
            }

            parts = next;
        }

        return parts;
    }

    private static void AddIfNotEmpty(List<Rectangle> parts, Rectangle part)
    {
        if (part.Width > 0 && part.Height > 0)
        {
            parts.Add(part);
        }
    }

    /// <summary>
    /// Whether the window paints all of its visible frame: a layered window can be partly or wholly transparent, and a
    /// click-through (WS_EX_TRANSPARENT) one is usually an overlay, so neither hides what is below it.
    /// </summary>
    private bool IsOpaque(nint handle) =>
        ((long)_windows.GetWindowLongPtr(handle, NativeEnums.GWL_EXSTYLE) & (NativeEnums.WS_EX_LAYERED | NativeEnums.WS_EX_TRANSPARENT)) == 0;

    /// <summary>The visible frame (without the invisible resize borders), or the window rectangle when DWM has none.</summary>
    private Rectangle VisibleFrame(nint handle, Rectangle bounds) =>
        _windows.GetExtendedFrameBounds(handle) is { } frame
            ? Rectangle.Intersect(Rectangle.FromLTRB(frame.Left, frame.Top, frame.Right, frame.Bottom), bounds)
            : bounds;
}

/// <summary>The parts of the screen a denied window shows on, in screen pixels.</summary>
public sealed record DeniedWindowArea(nint Window, IReadOnlyList<Rectangle> Areas);
