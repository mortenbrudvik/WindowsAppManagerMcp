using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;

namespace WindowsAppManagerMcp.Tests.Unit.Services;

/// <summary>
/// Tests for TcpPortOwnerResolver, which tells which process owns a CDP debug port.
/// The parsing tests use hand-built MIB tables; the lookup test only opens a loopback
/// listener owned by the test process.
/// </summary>
public class TcpPortOwnerResolverTests
{
    private const uint Listen = 2;

    private static byte[] Ipv4Table(params (byte[] Address, int Port, int Pid, uint State)[] rows)
    {
        var table = new byte[4 + rows.Length * 24];
        BinaryPrimitives.WriteUInt32LittleEndian(table, (uint)rows.Length);
        for (var i = 0; i < rows.Length; i++)
        {
            var row = table.AsSpan(4 + i * 24, 24);
            BinaryPrimitives.WriteUInt32LittleEndian(row, rows[i].State);
            rows[i].Address.CopyTo(row.Slice(4, 4));
            BinaryPrimitives.WriteUInt16BigEndian(row.Slice(8, 2), (ushort)rows[i].Port);
            BinaryPrimitives.WriteUInt32LittleEndian(row.Slice(20, 4), (uint)rows[i].Pid);
        }

        return table;
    }

    private static byte[] Ipv6Table(params (byte[] Address, int Port, int Pid)[] rows)
    {
        var table = new byte[4 + rows.Length * 56];
        BinaryPrimitives.WriteUInt32LittleEndian(table, (uint)rows.Length);
        for (var i = 0; i < rows.Length; i++)
        {
            var row = table.AsSpan(4 + i * 56, 56);
            rows[i].Address.CopyTo(row.Slice(0, 16));
            BinaryPrimitives.WriteUInt16BigEndian(row.Slice(20, 2), (ushort)rows[i].Port);
            BinaryPrimitives.WriteUInt32LittleEndian(row.Slice(48, 4), Listen);
            BinaryPrimitives.WriteUInt32LittleEndian(row.Slice(52, 4), (uint)rows[i].Pid);
        }

        return table;
    }

    private static readonly byte[] Loopback = [127, 0, 0, 1];
    private static readonly byte[] Any = [0, 0, 0, 0];
    private static readonly byte[] OtherAddress = [192, 168, 1, 10];

    [Fact]
    public void FindIpv4ListenerProcessId_ReturnsOwnerOfLoopbackListener()
    {
        var table = Ipv4Table(
            (Any, 80, 4, Listen),
            (Loopback, 9222, 1234, Listen));

        TcpPortOwnerResolver.FindIpv4ListenerProcessId(table, 9222).Should().Be(1234);
    }

    [Fact]
    public void FindIpv4ListenerProcessId_PrefersLoopbackOverAnyAddress()
    {
        var table = Ipv4Table(
            (Any, 9222, 999, Listen),
            (Loopback, 9222, 1234, Listen));

        TcpPortOwnerResolver.FindIpv4ListenerProcessId(table, 9222).Should().Be(1234);
    }

    [Fact]
    public void FindIpv4ListenerProcessId_FallsBackToAnyAddressListener()
    {
        var table = Ipv4Table((Any, 9222, 999, Listen));

        TcpPortOwnerResolver.FindIpv4ListenerProcessId(table, 9222).Should().Be(999);
    }

    [Fact]
    public void FindIpv4ListenerProcessId_IgnoresOtherPortsAddressesAndStates()
    {
        var table = Ipv4Table(
            (Loopback, 9223, 1, Listen),
            (OtherAddress, 9222, 2, Listen),
            (Loopback, 9222, 3, State: 5));

        TcpPortOwnerResolver.FindIpv4ListenerProcessId(table, 9222).Should().BeNull();
    }

    [Fact]
    public void FindIpv4ListenerProcessId_DoesNotReadPastTruncatedTable()
    {
        var table = Ipv4Table((Loopback, 9222, 1234, Listen));
        BinaryPrimitives.WriteUInt32LittleEndian(table, 1000); // claims more rows than the buffer holds

        TcpPortOwnerResolver.FindIpv4ListenerProcessId(table, 9222).Should().Be(1234);
        TcpPortOwnerResolver.FindIpv4ListenerProcessId(table.AsSpan(0, 10), 9222).Should().BeNull();
        TcpPortOwnerResolver.FindIpv4ListenerProcessId(ReadOnlySpan<byte>.Empty, 9222).Should().BeNull();
    }

    [Fact]
    public void FindIpv6ListenerProcessId_ReturnsOwnerOfAnyAddressListenerOnly()
    {
        var ipv6Loopback = new byte[16];
        ipv6Loopback[15] = 1;

        var table = Ipv6Table(
            (ipv6Loopback, 9222, 1),
            (new byte[16], 9222, 2));

        TcpPortOwnerResolver.FindIpv6ListenerProcessId(table, 9222).Should().Be(2);
        TcpPortOwnerResolver.FindIpv6ListenerProcessId(table, 9333).Should().BeNull();
    }

    [Fact]
    public void GetListeningProcessId_ReturnsCurrentProcess_ForLoopbackListenerOwnedByTest()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;

            var owner = new TcpPortOwnerResolver().GetListeningProcessId(port);

            owner.Should().Be(Environment.ProcessId);
        }
        finally
        {
            listener.Stop();
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(70000)]
    public void GetListeningProcessId_ReturnsNull_ForInvalidPort(int port)
    {
        new TcpPortOwnerResolver().GetListeningProcessId(port).Should().BeNull();
    }
}
