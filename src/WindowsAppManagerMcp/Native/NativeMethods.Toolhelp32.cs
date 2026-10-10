using System.Runtime.InteropServices;

namespace WindowsAppManagerMcp.Native;

internal static partial class NativeMethods
{
    internal static partial class Kernel32
    {
        public const uint TH32CS_SNAPPROCESS = 0x00000002;

        public static readonly nint INVALID_HANDLE_VALUE = -1;

        [LibraryImport("kernel32.dll", SetLastError = true)]
        public static partial nint CreateToolhelp32Snapshot(uint dwFlags, uint th32ProcessID);

        [LibraryImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static partial bool Process32FirstW(nint hSnapshot, ref PROCESSENTRY32W lppe);

        [LibraryImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static partial bool Process32NextW(nint hSnapshot, ref PROCESSENTRY32W lppe);
    }
}

/// <summary>
/// PROCESSENTRY32W. The executable name is declared as UTF-16 code units (ushort) to keep the
/// struct blittable for source-generated P/Invoke.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
internal unsafe struct PROCESSENTRY32W
{
    public uint dwSize;
    public uint cntUsage;
    public uint th32ProcessID;
    public nuint th32DefaultHeapID;
    public uint th32ModuleID;
    public uint cntThreads;
    public uint th32ParentProcessID;
    public int pcPriClassBase;
    public uint dwFlags;
    public fixed ushort szExeFile[260];
}
