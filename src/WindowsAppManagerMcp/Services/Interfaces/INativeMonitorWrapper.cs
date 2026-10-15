using WindowsAppManagerMcp.Models;

namespace WindowsAppManagerMcp.Services.Interfaces;

/// <summary>
/// Abstraction over the native Windows monitor APIs (EnumDisplayMonitors, GetMonitorInfo,
/// GetDpiForMonitor, MonitorFromWindow and MonitorFromPoint) and the display configuration reads
/// (QueryDisplayConfig and DisplayConfigGetDeviceInfo). Every member only reads.
/// This interface enables unit testing of MonitorService with fake monitor topologies.
/// </summary>
public interface INativeMonitorWrapper
{
    /// <summary>
    /// Enumerates the connected monitors in EnumDisplayMonitors order.
    /// Each call returns a new list whose monitor indices run from 0 in list order.
    /// </summary>
    IReadOnlyList<(nint Handle, MonitorInfo Info)> EnumerateMonitors();

    /// <summary>
    /// Gets the handle of the monitor that contains the window, or the nearest monitor.
    /// </summary>
    nint MonitorFromWindow(nint windowHandle);

    /// <summary>
    /// Gets the handle of the monitor that contains the point, or the nearest monitor.
    /// </summary>
    nint MonitorFromPoint(int x, int y);

    /// <summary>
    /// Reads the active display paths (QueryDisplayConfig with QDC_ONLY_ACTIVE_PATHS), with each path's GDI source name
    /// and its monitor's friendly name, device path and connector (DisplayConfigGetDeviceInfo). In clone mode several
    /// targets share a source. Slower than <see cref="EnumerateMonitors"/>, so callers cache the result per topology.
    /// Never throws: null when the display configuration cannot be read.
    /// </summary>
    IReadOnlyList<DisplayTarget>? GetDisplayTargets();
}
