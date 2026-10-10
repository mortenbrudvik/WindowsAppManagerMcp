using System.Runtime.InteropServices;

namespace WindowsAppManagerMcp.Native;

internal static partial class NativeMethods
{
    internal static partial class Iphlpapi
    {
        public const uint AF_INET = 2;
        public const uint AF_INET6 = 23;

        /// <summary>TCP_TABLE_CLASS value: listening sockets with their owning process ID.</summary>
        public const int TCP_TABLE_OWNER_PID_LISTENER = 3;

        public const uint NO_ERROR = 0;
        public const uint ERROR_INSUFFICIENT_BUFFER = 122;

        [LibraryImport("iphlpapi.dll")]
        public static partial uint GetExtendedTcpTable(
            nint pTcpTable,
            ref uint pdwSize,
            [MarshalAs(UnmanagedType.Bool)] bool bOrder,
            uint ulAf,
            int tableClass,
            uint reserved);
    }
}
