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

        // No Origin header is set, on purpose: Chrome 111+ refuses a DevTools WebSocket whose Origin header it was not
        // told to allow (--remote-allow-origins), and a client without one needs no such switch (roadmap M5.2).

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
