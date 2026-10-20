using System.Text.Json.Serialization;
using WindowsAppManagerMcp.Native;

namespace WindowsAppManagerMcp.Models;

/// <summary>
/// What a window's app asked Windows to do with it in captures (SetWindowDisplayAffinity, issue #17), as the capture results'
/// excludedFromCapture and get_window_info's displayAffinity report it. A window that asked nothing has none, and the field is
/// left out. Black or empty content that no affinity declares (DRM, exclusive fullscreen) is not reported.
/// </summary>
public enum DisplayAffinity
{
    /// <summary>WDA_MONITOR: the window is shown on the monitor only; a capture shows it black, hiding what is behind it.</summary>
    [JsonStringEnumMemberName("monitor")]
    Monitor,

    /// <summary>
    /// WDA_EXCLUDEFROMCAPTURE (Windows 10 2004 and later): the window is shown on the monitor only; a copy of the screen leaves
    /// it out and shows what is behind it, as if it were not there.
    /// </summary>
    [JsonStringEnumMemberName("excludeFromCapture")]
    ExcludeFromCapture
}

/// <summary>Reads <see cref="DisplayAffinity"/> from GetWindowDisplayAffinity's value.</summary>
public static class DisplayAffinities
{
    /// <summary>
    /// The affinity of a GetWindowDisplayAffinity value, or null for WDA_NONE. A value Windows does not document counts by its
    /// bits: with the exclusion bit (0x10) as excluded, else as monitor, so an unknown value is still reported.
    /// </summary>
    public static DisplayAffinity? Of(uint value) =>
        value == NativeEnums.WDA_NONE ? null
        : (value & (NativeEnums.WDA_EXCLUDEFROMCAPTURE & ~NativeEnums.WDA_MONITOR)) != 0 ? DisplayAffinity.ExcludeFromCapture
        : DisplayAffinity.Monitor;

    /// <summary>The name the results use: excludeFromCapture or monitor.</summary>
    public static string Name(DisplayAffinity affinity) =>
        affinity == DisplayAffinity.ExcludeFromCapture ? "excludeFromCapture" : "monitor";
}
