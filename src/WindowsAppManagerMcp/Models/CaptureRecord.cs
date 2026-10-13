namespace WindowsAppManagerMcp.Models;

/// <summary>
/// The window a capture was of, as it was right before its pixels were read (roadmap M3.11): what an image-space click
/// checks again before it sends anything, so a click never lands on a window that moved, resized, closed or was
/// replaced since the capture.
/// </summary>
/// <param name="Handle">The captured window.</param>
/// <param name="ProcessId">The window's process: a handle that now belongs to another process was reused.</param>
/// <param name="WindowRect">The window rectangle (GetWindowRect) right before the capture.</param>
/// <param name="IncludeFrame">
/// True for a frame capture; false for a client-area capture, whose image starts at the client area's corner, so the
/// client area on screen is checked too (a menu bar or toolbar can move it without moving the window).
/// </param>
/// <param name="ClientArea">
/// For a client-area capture: the client area on screen as it was captured, before any crop. A ref crop of it
/// (capture_window with ref) shows only part of the client area, so this, not the crop, is what the client area is
/// checked against. Null for a frame capture.
/// </param>
public sealed record CapturedWindow(long Handle, int ProcessId, WindowRect WindowRect, bool IncludeFrame, WindowRect? ClientArea = null);

/// <summary>
/// What a capture's image shows and how its pixels map to the screen: the facts the capture registry keeps under a
/// captureId so a mouse tool can convert image points itself (roadmap M3.11).
/// </summary>
/// <param name="ImageWidth">Width of the returned image in pixels (after any scaling).</param>
/// <param name="ImageHeight">Height of the returned image in pixels.</param>
/// <param name="ImageToScreen">The capture's transform: <c>screen = offset + image * scale</c>.</param>
/// <param name="CapturedRegion">The captured screen rectangle before any scaling.</param>
/// <param name="Window">The captured window; null for a monitor, region or virtual-screen capture.</param>
/// <param name="VirtualScreen">
/// For a capture without a window: the virtual screen (all monitors) at capture time. A change means the monitors
/// were moved, added, removed or rescaled, so a screen point may no longer show what the image showed.
/// </param>
/// <param name="Monitors">
/// For a capture without a window: each monitor at capture time (<see cref="CapturedMonitors.Of"/>). Monitors can move,
/// or swap places, inside the same virtual screen; any change means a screen point may no longer show what the image
/// showed. Null when they were not read.
/// </param>
public sealed record CaptureGeometry(
    int ImageWidth,
    int ImageHeight,
    ImageToScreenTransform ImageToScreen,
    CapturedRegion CapturedRegion,
    CapturedWindow? Window,
    VirtualScreenBounds? VirtualScreen,
    IReadOnlyList<CapturedMonitor>? Monitors = null);

/// <summary>A monitor as a screen capture saw it: its device name and its bounds on the virtual screen.</summary>
public sealed record CapturedMonitor(string DeviceName, MonitorRect Bounds);

/// <summary>The monitors a screen capture registers (roadmap M3.11).</summary>
public static class CapturedMonitors
{
    /// <summary>Each monitor's device name and bounds, ordered by device name, so two reads of the same monitors are equal.</summary>
    public static IReadOnlyList<CapturedMonitor> Of(IEnumerable<MonitorInfo> monitors) =>
        monitors.Select(m => new CapturedMonitor(m.DeviceName, m.Bounds)).OrderBy(m => m.DeviceName, StringComparer.Ordinal).ToList();
}

/// <summary>A capture the registry keeps: its id, when it was taken, and its geometry.</summary>
/// <param name="CaptureId">The id the capture result reported (for example <c>c7-3fa91c</c>).</param>
/// <param name="CapturedAt">When the capture was registered.</param>
/// <param name="Geometry">How its image maps to the screen, and the window it was of.</param>
public sealed record CaptureRecord(string CaptureId, DateTimeOffset CapturedAt, CaptureGeometry Geometry);
