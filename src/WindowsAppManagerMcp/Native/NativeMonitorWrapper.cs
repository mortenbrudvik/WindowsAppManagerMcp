using System.Runtime.InteropServices;
using WindowsAppManagerMcp.Models;
using WindowsAppManagerMcp.Services.Interfaces;

namespace WindowsAppManagerMcp.Native;

/// <summary>
/// Production implementation of INativeMonitorWrapper that calls real Windows API functions.
/// </summary>
public class NativeMonitorWrapper : INativeMonitorWrapper
{
    public IReadOnlyList<(nint Handle, MonitorInfo Info)> EnumerateMonitors()
    {
        // Built locally on every call, so concurrent callers never share a list that is being filled
        var monitors = new List<(nint Handle, MonitorInfo Info)>();

        NativeMethods.User32.MonitorEnumProc callback = (nint hMonitor, nint hdcMonitor, ref RECT lprcMonitor, nint dwData) =>
        {
            var mi = MONITORINFOEX.Create();
            if (NativeMethods.User32.GetMonitorInfoW(hMonitor, ref mi))
            {
                var dpi = GetDpi(hMonitor);
                var info = new MonitorInfo(
                    Index: monitors.Count,
                    DeviceName: mi.szDevice,
                    IsPrimary: (mi.dwFlags & NativeEnums.MONITORINFOF_PRIMARY) != 0,
                    Bounds: new MonitorRect(
                        mi.rcMonitor.Left,
                        mi.rcMonitor.Top,
                        mi.rcMonitor.Right - mi.rcMonitor.Left,
                        mi.rcMonitor.Bottom - mi.rcMonitor.Top),
                    WorkArea: new MonitorRect(
                        mi.rcWork.Left,
                        mi.rcWork.Top,
                        mi.rcWork.Right - mi.rcWork.Left,
                        mi.rcWork.Bottom - mi.rcWork.Top),
                    ScaleFactor: dpi / 96.0
                )
                {
                    Dpi = (int)dpi
                };
                monitors.Add((hMonitor, info));
            }
            return true;
        };

        NativeMethods.User32.EnumDisplayMonitors(nint.Zero, nint.Zero, callback, nint.Zero);
        GC.KeepAlive(callback);

        return monitors;
    }

    public nint MonitorFromWindow(nint windowHandle)
    {
        return NativeMethods.User32.MonitorFromWindow(windowHandle, NativeEnums.MONITOR_DEFAULTTONEAREST);
    }

    public nint MonitorFromPoint(int x, int y)
    {
        var pt = new POINT { X = x, Y = y };
        return NativeMethods.User32.MonitorFromPoint(pt, NativeEnums.MONITOR_DEFAULTTONEAREST);
    }

    /// <summary>The effective DPI of a monitor; 96 (no scaling) when it cannot be read.</summary>
    private static uint GetDpi(nint hMonitor)
    {
        try
        {
            int result = NativeMethods.Shcore.GetDpiForMonitor(
                hMonitor,
                MONITOR_DPI_TYPE.MDT_EFFECTIVE_DPI,
                out uint dpiX,
                out uint _);

            if (result == 0 && dpiX > 0) // S_OK
            {
                // Standard DPI is 96; the scale factor is dpi / 96
                return dpiX;
            }
        }
        catch
        {
            // Fallback for older Windows versions (pre-8.1)
        }

        return 96; // Default to no scaling
    }

    /// <summary>How often <see cref="GetDisplayTargets"/> queries when the topology keeps changing under it.</summary>
    public const int DisplayConfigAttempts = 3;

    public IReadOnlyList<DisplayTarget>? GetDisplayTargets()
    {
        try
        {
            return ReadTargets(QueryActiveTargets);
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            // The display configuration API is Windows 7 and later; the identity is then unknown, never an error
            return null;
        }
    }

    /// <summary>
    /// Runs the query until it succeeds, retrying while the topology changes between sizing the arrays and filling them
    /// (ERROR_INSUFFICIENT_BUFFER), at most <see cref="DisplayConfigAttempts"/> times. Null on any other error, or when the
    /// topology never settles.
    /// </summary>
    public static IReadOnlyList<DisplayTarget>? ReadTargets(Func<(int Status, IReadOnlyList<DisplayTarget> Targets)> query)
    {
        for (var attempt = 0; attempt < DisplayConfigAttempts; attempt++)
        {
            var (status, targets) = query();
            if (status == NativeMethods.DisplayConfig.ERROR_SUCCESS)
            {
                return targets;
            }

            if (status != NativeMethods.DisplayConfig.ERROR_INSUFFICIENT_BUFFER)
            {
                return null;
            }
        }

        return null;
    }

    /// <summary>
    /// A target from what DisplayConfigGetDeviceInfo reported for one path: names and paths trimmed, empty ones null (the
    /// native buffers are fixed-size, and a monitor without an EDID has no name). Null without a source name, which is
    /// what joins the target with a monitor.
    /// </summary>
    public static DisplayTarget? ToDisplayTarget(string? gdiDeviceName, string? friendlyName, string? devicePath, uint outputTechnology)
    {
        if (string.IsNullOrWhiteSpace(gdiDeviceName))
        {
            return null;
        }

        return new DisplayTarget(
            gdiDeviceName.Trim(),
            NullIfEmpty(friendlyName),
            NullIfEmpty(devicePath),
            (DisplayOutputTechnology)outputTechnology);
    }

    /// <summary>
    /// A target for a path whose target name could not be read: the source and the path's connector, no name, no device
    /// path, and marked as not read so its monitor gets no id or name rather than a partial one. Null without a source name.
    /// </summary>
    public static DisplayTarget? ToUnreadTarget(string? gdiDeviceName, uint outputTechnology) =>
        ToDisplayTarget(gdiDeviceName, null, null, outputTechnology) is { } target ? target with { TargetRead = false } : null;

    private static string? NullIfEmpty(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    /// <summary>
    /// The targets of the active paths, each joined with its source's GDI name. Each distinct source (adapter and source
    /// id) is read once, and its name is used for every path on it. In clone mode several paths share one source, so its
    /// cloned paths are either all joined or, when the source name cannot be read, all left out: a monitor's identity is
    /// then unknown, never hashed from only some of its cloned paths. A path with no target is skipped.
    /// </summary>
    public static IReadOnlyList<DisplayTarget> ReadPathTargets<TPath, TSource>(
        IEnumerable<TPath> paths,
        Func<TPath, TSource> sourceOf,
        Func<TPath, string?> readSourceName,
        Func<TPath, string, DisplayTarget?> readTarget)
        where TSource : notnull
    {
        var sourceNames = new Dictionary<TSource, string?>();
        var targets = new List<DisplayTarget>();
        foreach (var path in paths)
        {
            var source = sourceOf(path);
            if (!sourceNames.TryGetValue(source, out var gdiDeviceName))
            {
                gdiDeviceName = readSourceName(path);
                sourceNames[source] = gdiDeviceName;
            }

            if (string.IsNullOrWhiteSpace(gdiDeviceName))
            {
                continue;
            }

            if (readTarget(path, gdiDeviceName) is { } target)
            {
                targets.Add(target);
            }
        }

        return targets;
    }

    /// <summary>
    /// One QueryDisplayConfig of the active paths, and the source and target names of each (<see cref="ReadPathTargets"/>).
    /// The paths of a source whose name cannot be read are left out (they cannot be joined with a monitor); a path whose
    /// target name cannot be read keeps the connector from the path, with no name and no device path, and is marked as not
    /// read (<see cref="DisplayTarget.TargetRead"/>).
    /// </summary>
    private static (int Status, IReadOnlyList<DisplayTarget> Targets) QueryActiveTargets()
    {
        var flags = NativeMethods.DisplayConfig.QDC_ONLY_ACTIVE_PATHS;
        var status = NativeMethods.DisplayConfig.GetDisplayConfigBufferSizes(flags, out var pathCount, out var modeCount);
        if (status != NativeMethods.DisplayConfig.ERROR_SUCCESS)
        {
            return (status, []);
        }

        var paths = new DISPLAYCONFIG_PATH_INFO[pathCount];
        var modes = new DISPLAYCONFIG_MODE_INFO[modeCount];
        status = NativeMethods.DisplayConfig.QueryDisplayConfig(flags, ref pathCount, paths, ref modeCount, modes, nint.Zero);
        if (status != NativeMethods.DisplayConfig.ERROR_SUCCESS)
        {
            return (status, []);
        }

        var active = paths.AsSpan(0, (int)Math.Min(pathCount, (uint)paths.Length)).ToArray();
        var targets = ReadPathTargets(
            active,
            path => (path.sourceInfo.adapterId.LowPart, path.sourceInfo.adapterId.HighPart, path.sourceInfo.id),
            ReadSourceName,
            ReadTarget);

        return (NativeMethods.DisplayConfig.ERROR_SUCCESS, targets);
    }

    /// <summary>The GDI device name of a path's source (DisplayConfigGetDeviceInfo GET_SOURCE_NAME); null when it cannot be read.</summary>
    private static string? ReadSourceName(DISPLAYCONFIG_PATH_INFO path)
    {
        var source = new DISPLAYCONFIG_SOURCE_DEVICE_NAME
        {
            header = new DISPLAYCONFIG_DEVICE_INFO_HEADER
            {
                type = NativeMethods.DisplayConfig.DISPLAYCONFIG_DEVICE_INFO_GET_SOURCE_NAME,
                size = (uint)Marshal.SizeOf<DISPLAYCONFIG_SOURCE_DEVICE_NAME>(),
                adapterId = path.sourceInfo.adapterId,
                id = path.sourceInfo.id
            }
        };
        return NativeMethods.DisplayConfig.DisplayConfigGetDeviceInfo(ref source) == NativeMethods.DisplayConfig.ERROR_SUCCESS
            ? source.viewGdiDeviceName
            : null;
    }

    /// <summary>
    /// The target of a path on the source with this GDI name (DisplayConfigGetDeviceInfo GET_TARGET_NAME); when that read
    /// fails, an unread target with the path's connector.
    /// </summary>
    private static DisplayTarget? ReadTarget(DISPLAYCONFIG_PATH_INFO path, string gdiDeviceName)
    {
        var target = new DISPLAYCONFIG_TARGET_DEVICE_NAME
        {
            header = new DISPLAYCONFIG_DEVICE_INFO_HEADER
            {
                type = NativeMethods.DisplayConfig.DISPLAYCONFIG_DEVICE_INFO_GET_TARGET_NAME,
                size = (uint)Marshal.SizeOf<DISPLAYCONFIG_TARGET_DEVICE_NAME>(),
                adapterId = path.targetInfo.adapterId,
                id = path.targetInfo.id
            }
        };
        return NativeMethods.DisplayConfig.DisplayConfigGetDeviceInfo(ref target) == NativeMethods.DisplayConfig.ERROR_SUCCESS
            ? ToDisplayTarget(gdiDeviceName, target.monitorFriendlyDeviceName, target.monitorDevicePath, target.outputTechnology)
            : ToUnreadTarget(gdiDeviceName, path.targetInfo.outputTechnology);
    }
}
