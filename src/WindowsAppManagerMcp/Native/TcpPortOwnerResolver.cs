using System.Buffers.Binary;
using System.Runtime.InteropServices;
using WindowsAppManagerMcp.Services.Interfaces;

namespace WindowsAppManagerMcp.Native;

/// <summary>
/// Finds the process listening on a TCP port using GetExtendedTcpTable (TCP_TABLE_OWNER_PID_LISTENER).
/// This works without elevation, like "netstat -ano".
/// </summary>
public class TcpPortOwnerResolver : ITcpPortOwnerResolver
{
    // MIB_TCPROW_OWNER_PID: dwState, dwLocalAddr, dwLocalPort, dwRemoteAddr, dwRemotePort, dwOwningPid
    private const int Ipv4RowSize = 24;

    // MIB_TCP6ROW_OWNER_PID: ucLocalAddr[16], dwLocalScopeId, dwLocalPort, ucRemoteAddr[16],
    // dwRemoteScopeId, dwRemotePort, dwState, dwOwningPid
    private const int Ipv6RowSize = 56;

    private const uint MibTcpStateListen = 2;
    private const int MaxQueryAttempts = 5;

    private static readonly byte[] Ipv4Loopback = [127, 0, 0, 1];
    private static readonly byte[] Ipv4Any = [0, 0, 0, 0];

    /// <inheritdoc />
    public int? GetListeningProcessId(int port)
    {
        if (port is < 1 or > 65535)
            return null;

        try
        {
            var ipv4Table = QueryListenerTable(NativeMethods.Iphlpapi.AF_INET);
            var pid = ipv4Table == null ? null : FindIpv4ListenerProcessId(ipv4Table, port);
            if (pid != null)
                return pid;

            // A dual-mode socket bound to [::] also accepts IPv4 connections to 127.0.0.1.
            var ipv6Table = QueryListenerTable(NativeMethods.Iphlpapi.AF_INET6);
            return ipv6Table == null ? null : FindIpv6ListenerProcessId(ipv6Table, port);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Finds the owner of an IPv4 listener on the port in a raw MIB_TCPTABLE_OWNER_PID buffer.
    /// A listener on 127.0.0.1 wins over one on 0.0.0.0, because Windows routes a connection
    /// to 127.0.0.1 to the more specific binding.
    /// </summary>
    public static int? FindIpv4ListenerProcessId(ReadOnlySpan<byte> table, int port)
    {
        int? anyAddressOwner = null;

        foreach (var row in Rows(table, Ipv4RowSize))
        {
            var span = table.Slice(row, Ipv4RowSize);
            var state = BinaryPrimitives.ReadUInt32LittleEndian(span);
            var localAddress = span.Slice(4, 4);
            var localPort = BinaryPrimitives.ReadUInt16BigEndian(span.Slice(8, 2));
            var owner = (int)BinaryPrimitives.ReadUInt32LittleEndian(span.Slice(20, 4));

            if (state != MibTcpStateListen || localPort != port)
                continue;

            if (localAddress.SequenceEqual(Ipv4Loopback))
                return owner;

            if (localAddress.SequenceEqual(Ipv4Any))
                anyAddressOwner ??= owner;
        }

        return anyAddressOwner;
    }

    /// <summary>
    /// Finds the owner of an IPv6 listener bound to [::] on the port in a raw MIB_TCP6TABLE_OWNER_PID buffer.
    /// </summary>
    public static int? FindIpv6ListenerProcessId(ReadOnlySpan<byte> table, int port)
    {
        foreach (var row in Rows(table, Ipv6RowSize))
        {
            var span = table.Slice(row, Ipv6RowSize);
            var localAddress = span.Slice(0, 16);
            var localPort = BinaryPrimitives.ReadUInt16BigEndian(span.Slice(20, 2));
            var state = BinaryPrimitives.ReadUInt32LittleEndian(span.Slice(48, 4));
            var owner = (int)BinaryPrimitives.ReadUInt32LittleEndian(span.Slice(52, 4));

            if (state == MibTcpStateListen && localPort == port && localAddress.IndexOfAnyExcept((byte)0) < 0)
                return owner;
        }

        return null;
    }

    /// <summary>Returns the byte offsets of the rows that fit in the buffer.</summary>
    private static List<int> Rows(ReadOnlySpan<byte> table, int rowSize)
    {
        var offsets = new List<int>();
        if (table.Length < 4)
            return offsets;

        var count = BinaryPrimitives.ReadUInt32LittleEndian(table);
        for (long i = 0, offset = 4; i < count && offset + rowSize <= table.Length; i++, offset += rowSize)
        {
            offsets.Add((int)offset);
        }

        return offsets;
    }

    private static byte[]? QueryListenerTable(uint addressFamily)
    {
        uint size = 0;

        for (var attempt = 0; attempt < MaxQueryAttempts; attempt++)
        {
            var buffer = size == 0 ? nint.Zero : Marshal.AllocHGlobal((int)size);
            try
            {
                var result = NativeMethods.Iphlpapi.GetExtendedTcpTable(
                    buffer, ref size, false, addressFamily, NativeMethods.Iphlpapi.TCP_TABLE_OWNER_PID_LISTENER, 0);

                if (result == NativeMethods.Iphlpapi.NO_ERROR && buffer != nint.Zero)
                {
                    var bytes = new byte[size];
                    Marshal.Copy(buffer, bytes, 0, (int)size);
                    return bytes;
                }

                // The table can grow between the size query and the copy; retry with the new size.
                if (result != NativeMethods.Iphlpapi.ERROR_INSUFFICIENT_BUFFER)
                    return null;
            }
            finally
            {
                if (buffer != nint.Zero)
                    Marshal.FreeHGlobal(buffer);
            }
        }

        return null;
    }
}
