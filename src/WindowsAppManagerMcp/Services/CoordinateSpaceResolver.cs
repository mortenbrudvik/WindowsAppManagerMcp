using WindowsAppManagerMcp.Models;
using WindowsAppManagerMcp.Services.Interfaces;

namespace WindowsAppManagerMcp.Services;

/// <summary>
/// Converts the mouse tools' image, window and client points to screen points (roadmap M3.11, see
/// <see cref="ICoordinateSpaceResolver"/>). An image point goes through the capture's own imageToScreen (the M1.2
/// transform the capture reported), and only while what the image showed is still where it was: the captured window
/// within <see cref="Tolerance"/> pixels of its rectangle at capture time (and of its client area, for a client
/// capture), not closed, minimized or replaced by another process's window; for a screen capture, the same monitors.
/// Every conversion comes with a recheck the input guard runs inside the input gate, so a window that moves while
/// the call waits for the gate is caught too. Reads only.
/// </summary>
public sealed class CoordinateSpaceResolver : ICoordinateSpaceResolver
{
    /// <summary>How far, in pixels, an edge of the window may have moved before its capture counts as stale.</summary>
    public const int Tolerance = 2;

    private readonly ICaptureRegistry _captures;
    private readonly INativeWindowWrapper _windows;
    private readonly INativeHitTestWrapper _hitTest;
    private readonly IScreenCaptureWrapper _screen;

    /// <param name="captures">The registered captures.</param>
    /// <param name="windows">Window reads: whether it exists, its process, rectangle and minimized state.</param>
    /// <param name="hitTest">The window's client area on screen.</param>
    /// <param name="screen">The virtual screen's bounds, which tell whether the monitors changed.</param>
    public CoordinateSpaceResolver(
        ICaptureRegistry captures, INativeWindowWrapper windows, INativeHitTestWrapper hitTest, IScreenCaptureWrapper screen)
    {
        _captures = captures ?? throw new ArgumentNullException(nameof(captures));
        _windows = windows ?? throw new ArgumentNullException(nameof(windows));
        _hitTest = hitTest ?? throw new ArgumentNullException(nameof(hitTest));
        _screen = screen ?? throw new ArgumentNullException(nameof(screen));
    }

    /// <inheritdoc />
    public PointResolution FromImage(string captureId, int x, int y)
    {
        if (_captures.Find(captureId) is not { } capture)
        {
            return PointResolution.Fail(InputErrorCode.CaptureNotFound,
                $"No capture has captureId '{captureId}': it is mistyped, from an earlier run of the server, older than " +
                $"{(int)_captures.TimeToLive.TotalMinutes} minutes, or {_captures.Capacity} newer captures pushed it out. " +
                "Nothing was sent: capture again and use the new captureId.");
        }

        var geometry = capture.Geometry;
        if (x < 0 || y < 0 || x >= geometry.ImageWidth || y >= geometry.ImageHeight)
        {
            return PointResolution.Fail(InputErrorCode.CoordinatesOutOfBounds,
                $"Point ({x}, {y}) is outside the image of capture {capture.CaptureId}, which is " +
                $"{geometry.ImageWidth}x{geometry.ImageHeight} pixels (x 0-{geometry.ImageWidth - 1}, y 0-{geometry.ImageHeight - 1}). " +
                "Nothing was sent. Measure the point on that image, as returned (not at full resolution).");
        }

        if (Staleness(capture) is { } stale)
        {
            return PointResolution.Fail(InputErrorCode.StaleCapture, stale);
        }

        var (screenX, screenY) = geometry.ImageToScreen.ToScreen(x, y);
        return PointResolution.At(screenX, screenY,
            () => Staleness(capture) is { } changed ? new SelectorRefusal(nameof(InputErrorCode.StaleCapture), changed) : null,
            geometry.Window is { } window ? (nint)window.Handle : null);
    }

    /// <inheritdoc />
    public PointResolution FromWindow(CoordinateSpace space, nint window, int x, int y)
    {
        if (space is not (CoordinateSpace.Window or CoordinateSpace.Client))
        {
            throw new ArgumentOutOfRangeException(nameof(space), space, "Only window and client points are relative to a window.");
        }

        var name = space == CoordinateSpace.Window ? "rectangle" : "client area";
        if (Origin(space, window) is not { } origin)
        {
            return PointResolution.Fail(InputErrorCode.InvalidHandle,
                $"Window {(long)window} is not a window, or its {name} cannot be read, so there is no origin for the " +
                $"{SpaceName(space)} point ({x}, {y}). Nothing was sent. Get a current handle from find_windows.");
        }

        if (_windows.IsIconic(window))
        {
            return PointResolution.Fail(InputErrorCode.CoordinatesOutOfBounds,
                $"Window {(long)window} is minimized, so its {name} is not on screen and the {SpaceName(space)} point " +
                $"({x}, {y}) is nowhere. Nothing was sent. Restore it with set_window_state or focus_window first.");
        }

        return PointResolution.At(origin.X + x, origin.Y + y, () =>
        {
            var now = Origin(space, window);
            return now != null && Math.Abs(now.X - origin.X) <= Tolerance && Math.Abs(now.Y - origin.Y) <= Tolerance &&
                   Math.Abs(now.Width - origin.Width) <= Tolerance && Math.Abs(now.Height - origin.Height) <= Tolerance
                ? null
                : new SelectorRefusal(nameof(InputErrorCode.StaleCapture),
                    $"Window {(long)window} moved, resized or closed while the call waited for the input gate (its {name} was " +
                    $"{Describe(origin)}, now {(now == null ? "unreadable" : Describe(now))}), so the {SpaceName(space)} point " +
                    $"({x}, {y}) would land elsewhere. Nothing was sent. Check the window, then send the point again.");
        });
    }

    /// <summary>The window's rectangle (window) or client area on screen (client); null when it cannot be read.</summary>
    private WindowRect? Origin(CoordinateSpace space, nint window)
    {
        if (space == CoordinateSpace.Client)
        {
            return _hitTest.GetClientAreaOnScreen(window);
        }

        return _windows.GetWindowRect(window) is { } rect ? WindowRect.FromLTRB(rect.Left, rect.Top, rect.Right, rect.Bottom) : null;
    }

    /// <summary>Why <paramref name="capture"/> no longer shows what is on screen, or null when it still does.</summary>
    private string? Staleness(CaptureRecord capture)
    {
        var geometry = capture.Geometry;
        var again = "Nothing was sent: capture it again and use the new captureId.";
        if (geometry.Window is not { } window)
        {
            var screen = _screen.GetVirtualScreenBounds();
            var now = new VirtualScreenBounds(screen.X, screen.Y, screen.Width, screen.Height);
            return now == geometry.VirtualScreen
                ? null
                : $"The monitors changed since capture {capture.CaptureId} (the virtual screen was {Describe(geometry.VirtualScreen)}, " +
                  $"now {Describe(now)}), so its image points may map to other places on screen. {again}";
        }

        var handle = (nint)window.Handle;
        var of = $"Window {window.Handle} of capture {capture.CaptureId}";
        if (!_windows.IsWindow(handle))
        {
            return $"{of} has closed. {again}";
        }

        if ((int)_windows.GetWindowThreadProcessId(handle) != window.ProcessId)
        {
            return $"{of} closed and its handle now belongs to a window of another process. {again}";
        }

        if (_windows.IsIconic(handle))
        {
            return $"{of} is minimized now, so the image is not on screen. Restore it, then capture it again.";
        }

        if (_windows.GetWindowRect(handle) is not { } rect)
        {
            return $"{of} cannot be read now. {again}";
        }

        var current = WindowRect.FromLTRB(rect.Left, rect.Top, rect.Right, rect.Bottom);
        if (!WithinTolerance(current, window.WindowRect))
        {
            return $"{of} moved or resized since the capture (its rectangle was {Describe(window.WindowRect)}, now " +
                   $"{Describe(current)}), so the image no longer shows what is at its points. {again}";
        }

        if (!window.IncludeFrame)
        {
            // The client area as captured: a ref crop's region is only part of it
            var region = geometry.CapturedRegion;
            var captured = window.ClientArea ?? new WindowRect(region.X, region.Y, region.Width, region.Height);
            if (_hitTest.GetClientAreaOnScreen(handle) is not { } client || !WithinTolerance(client, captured))
            {
                return $"{of} has its client area somewhere else than at the capture (it was {Describe(captured)}, now " +
                       $"{(_hitTest.GetClientAreaOnScreen(handle) is { } area ? Describe(area) : "unreadable")}; a menu bar or " +
                       $"toolbar changed?), so the image no longer shows what is at its points. {again}";
            }
        }

        return null;
    }

    /// <summary>True when no edge of the two rectangles is more than <see cref="Tolerance"/> pixels apart.</summary>
    internal static bool WithinTolerance(WindowRect a, WindowRect b) =>
        Math.Abs(a.Left - b.Left) <= Tolerance && Math.Abs(a.Top - b.Top) <= Tolerance &&
        Math.Abs(a.Right - b.Right) <= Tolerance && Math.Abs(a.Bottom - b.Bottom) <= Tolerance;

    private static string SpaceName(CoordinateSpace space) => ToolEnum<CoordinateSpace>.Name(space);

    private static string Describe(WindowRect rect) => $"({rect.X}, {rect.Y}, {rect.Width}x{rect.Height})";

    private static string Describe(VirtualScreenBounds? bounds) =>
        bounds == null ? "unknown" : $"({bounds.X}, {bounds.Y}, {bounds.Width}x{bounds.Height})";
}
