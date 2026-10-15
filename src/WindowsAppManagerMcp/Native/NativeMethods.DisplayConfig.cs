using System.Runtime.InteropServices;

namespace WindowsAppManagerMcp.Native;

internal static partial class NativeMethods
{
    /// <summary>
    /// The display configuration reads behind monitor identity (NativeMonitorWrapper.GetDisplayTargets, roadmap M6.7):
    /// the active paths, each source's GDI name and each target monitor's EDID name, device path and connector. Read-only;
    /// SetDisplayConfig, which changes the topology, is deliberately not declared.
    /// </summary>
    internal static class DisplayConfig
    {
        /// <summary>QueryDisplayConfig flag: only the paths that are active now.</summary>
        public const uint QDC_ONLY_ACTIVE_PATHS = 0x00000002;

        public const int ERROR_SUCCESS = 0;

        /// <summary>The topology changed after GetDisplayConfigBufferSizes, so the arrays are too small.</summary>
        public const int ERROR_INSUFFICIENT_BUFFER = 122;

        /// <summary>DISPLAYCONFIG_DEVICE_INFO_TYPE values.</summary>
        public const uint DISPLAYCONFIG_DEVICE_INFO_GET_SOURCE_NAME = 1;

        public const uint DISPLAYCONFIG_DEVICE_INFO_GET_TARGET_NAME = 2;

        [DllImport("user32.dll")]
        public static extern int GetDisplayConfigBufferSizes(
            uint flags,
            out uint numPathArrayElements,
            out uint numModeInfoArrayElements);

        [DllImport("user32.dll")]
        public static extern int QueryDisplayConfig(
            uint flags,
            ref uint numPathArrayElements,
            [Out] DISPLAYCONFIG_PATH_INFO[] pathArray,
            ref uint numModeInfoArrayElements,
            [Out] DISPLAYCONFIG_MODE_INFO[] modeInfoArray,
            nint currentTopologyId);

        [DllImport("user32.dll")]
        public static extern int DisplayConfigGetDeviceInfo(ref DISPLAYCONFIG_SOURCE_DEVICE_NAME requestPacket);

        [DllImport("user32.dll")]
        public static extern int DisplayConfigGetDeviceInfo(ref DISPLAYCONFIG_TARGET_DEVICE_NAME requestPacket);
    }
}

[StructLayout(LayoutKind.Sequential)]
internal struct DISPLAYCONFIG_LUID
{
    public uint LowPart;
    public int HighPart;
}

[StructLayout(LayoutKind.Sequential)]
internal struct DISPLAYCONFIG_RATIONAL
{
    public uint Numerator;
    public uint Denominator;
}

[StructLayout(LayoutKind.Sequential)]
internal struct DISPLAYCONFIG_PATH_SOURCE_INFO
{
    public DISPLAYCONFIG_LUID adapterId;
    public uint id;
    public uint modeInfoIdx;
    public uint statusFlags;
}

[StructLayout(LayoutKind.Sequential)]
internal struct DISPLAYCONFIG_PATH_TARGET_INFO
{
    public DISPLAYCONFIG_LUID adapterId;
    public uint id;
    public uint modeInfoIdx;
    public uint outputTechnology;
    public uint rotation;
    public uint scaling;
    public DISPLAYCONFIG_RATIONAL refreshRate;
    public uint scanLineOrdering;
    public int targetAvailable;
    public uint statusFlags;
}

[StructLayout(LayoutKind.Sequential)]
internal struct DISPLAYCONFIG_PATH_INFO
{
    public DISPLAYCONFIG_PATH_SOURCE_INFO sourceInfo;
    public DISPLAYCONFIG_PATH_TARGET_INFO targetInfo;
    public uint flags;
}

/// <summary>
/// A mode entry. Only its size matters: QueryDisplayConfig requires the mode array, but monitor identity reads nothing from
/// it, so the 48-byte union of target, source and desktop image modes is left as padding.
/// </summary>
[StructLayout(LayoutKind.Sequential, Size = 64)]
internal struct DISPLAYCONFIG_MODE_INFO
{
    public uint infoType;
    public uint id;
    public DISPLAYCONFIG_LUID adapterId;
}

[StructLayout(LayoutKind.Sequential)]
internal struct DISPLAYCONFIG_DEVICE_INFO_HEADER
{
    public uint type;
    public uint size;
    public DISPLAYCONFIG_LUID adapterId;
    public uint id;
}

[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
internal struct DISPLAYCONFIG_SOURCE_DEVICE_NAME
{
    public DISPLAYCONFIG_DEVICE_INFO_HEADER header;

    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
    public string viewGdiDeviceName;
}

[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
internal struct DISPLAYCONFIG_TARGET_DEVICE_NAME
{
    public DISPLAYCONFIG_DEVICE_INFO_HEADER header;
    public uint flags;
    public uint outputTechnology;
    public ushort edidManufactureId;
    public ushort edidProductCodeId;
    public uint connectorInstance;

    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
    public string monitorFriendlyDeviceName;

    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
    public string monitorDevicePath;
}
