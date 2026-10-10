using System.Net.WebSockets;
using WindowsAppManagerMcp.Services.Interfaces;

namespace WindowsAppManagerMcp.Services;

/// <summary>
/// Opens CDP WebSocket connections with <see cref="ClientWebSocket"/>.
/// </summary>
public sealed class ClientWebSocketFactory : ICdpWebSocketFactory
{
    /// <inheritdoc />
    public async Task<WebSocket> ConnectAsync(Uri uri, CancellationToken cancellationToken)
    {
        var socket = new ClientWebSocket();

        // CDP endpoints are on the loopback interface; never route them through a proxy.
        socket.Options.Proxy = null;

        try
        {
            await socket.ConnectAsync(uri, cancellationToken);
            return socket;
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }
}
