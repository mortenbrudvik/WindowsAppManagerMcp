namespace WindowsAppManagerMcp.Models;

/// <summary>
/// One active display path as the display configuration (QueryDisplayConfig and DisplayConfigGetDeviceInfo) reports it:
/// the GDI source a monitor is enumerated under, and the physical monitor on the other end of the path. In clone mode
/// several targets share one source, so one monitor can have more than one target.
/// </summary>
/// <param name="GdiDeviceName">The source's GDI device name (\\.\DISPLAYn), the same name EnumDisplayMonitors reports as
/// <see cref="MonitorInfo.DeviceName"/>. It renumbers on docking and on driver or adapter changes, so it is the join key,
/// never the identity.</param>
/// <param name="FriendlyName">The monitor's name from its EDID (for example "DELL U2720Q"); null when the monitor has none
/// (no EDID, a virtual or remote display) or the target could not be read.</param>
/// <param name="DevicePath">The monitor's device interface path (monitorDevicePath). It stays the same for a monitor on
/// the same port across reboots and docking, so the monitor id is derived from it; null when it could not be read.</param>
/// <param name="OutputTechnology">The connector type of the target.</param>
public sealed record DisplayTarget(
    string GdiDeviceName,
    string? FriendlyName,
    string? DevicePath,
    DisplayOutputTechnology OutputTechnology);

/// <summary>
/// DISPLAYCONFIG_VIDEO_OUTPUT_TECHNOLOGY. Only the values the server interprets are named; any other value the display
/// configuration reports is kept as is.
/// </summary>
public enum DisplayOutputTechnology : uint
{
    Hdmi = 5,
    Lvds = 6,
    DisplayPortExternal = 10,
    DisplayPortEmbedded = 11,
    UdiExternal = 12,
    UdiEmbedded = 13,
    Miracast = 15,
    IndirectWired = 16,
    IndirectVirtual = 17,

    /// <summary>An internal panel that reports no more specific connector.</summary>
    Internal = 0x80000000,

    /// <summary>DISPLAYCONFIG_OUTPUT_TECHNOLOGY_OTHER (-1).</summary>
    Other = 0xFFFFFFFF
}
