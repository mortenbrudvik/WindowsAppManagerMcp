namespace WindowsAppManagerMcp.Models;

/// <summary>
/// The DPI awareness of the server's threads. Only <see cref="PerMonitorV2"/> (and <see cref="PerMonitor"/> for a
/// windowless process like this one) gives the server physical pixels on every monitor; below that, Windows scales
/// what it reports and receives on displays above 100%.
/// </summary>
public enum DpiAwareness
{
    /// <summary>The awareness could not be read (Windows before 10 1607, or the call failed).</summary>
    Unknown = 0,

    /// <summary>DPI unaware: Windows scales everything as if every monitor were at 96 DPI.</summary>
    Unaware,

    /// <summary>DPI unaware with GDI scaling.</summary>
    UnawareGdiScaled,

    /// <summary>System aware: physical pixels at the primary monitor's DPI at sign-in, scaled elsewhere.</summary>
    System,

    /// <summary>Per-monitor aware (v1): physical pixels on every monitor.</summary>
    PerMonitor,

    /// <summary>Per-monitor aware v2, which the server's app manifest declares.</summary>
    PerMonitorV2
}

/// <summary>Names a DPI awareness context.</summary>
public static class DpiAwarenessNames
{
    /// <summary>
    /// The awareness of a context: <paramref name="awareness"/> is GetAwarenessFromDpiAwarenessContext (0 unaware,
    /// 1 system, 2 per monitor, -1 invalid), which does not tell per-monitor v2 from v1 or GDI-scaled unawareness from
    /// plain unawareness; <paramref name="perMonitorV2"/> and <paramref name="gdiScaled"/> are AreDpiAwarenessContextsEqual
    /// with those contexts.
    /// </summary>
    public static DpiAwareness FromContext(int awareness, bool perMonitorV2, bool gdiScaled) => awareness switch
    {
        2 => perMonitorV2 ? DpiAwareness.PerMonitorV2 : DpiAwareness.PerMonitor,
        1 => DpiAwareness.System,
        0 => gdiScaled ? DpiAwareness.UnawareGdiScaled : DpiAwareness.Unaware,
        _ => DpiAwareness.Unknown
    };

    /// <summary>True for the awareness the app manifest declares (<see cref="DpiAwareness.PerMonitorV2"/>).</summary>
    public static bool IsExpected(DpiAwareness awareness) => awareness == DpiAwareness.PerMonitorV2;
}
