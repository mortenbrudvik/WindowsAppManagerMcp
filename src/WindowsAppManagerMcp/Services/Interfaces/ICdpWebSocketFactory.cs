using System.Net.WebSockets;

namespace WindowsAppManagerMcp.Services.Interfaces;

/// <summary>
/// Opens WebSocket connections to CDP targets.
/// This seam lets tests replace the browser with a scripted fake.
/// </summary>
public interface ICdpWebSocketFactory
{
    /// <summary>
    /// Connects to a CDP WebSocket endpoint.
    /// </summary>
    /// <param name="uri">The target's webSocketDebuggerUrl.</param>
    /// <param name="cancellationToken">Cancels the connection attempt.</param>
    /// <returns>An open WebSocket. The caller disposes it.</returns>
    Task<WebSocket> ConnectAsync(Uri uri, CancellationToken cancellationToken);
}
