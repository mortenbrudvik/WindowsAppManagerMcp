using WindowsAppManagerMcp.Models;
using WindowsAppManagerMcp.Services.Interfaces;

namespace WindowsAppManagerMcp.Services;

/// <summary>
/// Opens a tab's own DevTools connection, the one way every caller does (the reads, which tab is shown, the tab tools and
/// the DOM bridge): at the WebSocket URL the tab's port listed, on the browser whose process listed it
/// (<see cref="CdpTarget.OwnerProcessId"/>). The transport reads the port's listener again once the connection is open and
/// closes it unsent when that is no longer the browser (<see cref="ICdpTransport.ConnectAsync"/>, review F4). A tab whose
/// list no listener read tied to a browser is not connected to at all (fails closed).
/// </summary>
internal static class CdpTargetConnection
{
    /// <summary>Tab <paramref name="target"/>'s own connection, which the caller disposes; or why none was opened.</summary>
    public static async Task<CdpResult<ICdpConnection>> OpenAsync(
        ICdpTransport transport, CdpTarget target, TimeSpan timeout, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(transport);
        ArgumentNullException.ThrowIfNull(target);
        if (target.OwnerProcessId is not int owner)
            return Unowned<ICdpConnection>(target);

        var label = $"tab {target.Id}";
        var webSocketUrl = CdpEndpointValidator.ValidateWebSocketUrl(target.WebSocketDebuggerUrl, target.Port, label);
        if (!webSocketUrl.Success)
            return webSocketUrl.FailAs<ICdpConnection>();

        return await transport.ConnectAsync(webSocketUrl.Value!, target.Port, owner, label, timeout, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// CdpPortOwnerMismatch for a tab that no read of its port's listener tied to a browser
    /// (<see cref="CdpTarget.OwnerProcessId"/> null): nothing is connected to it or sent.
    /// </summary>
    public static CdpResult<T> Unowned<T>(CdpTarget target)
    {
        ArgumentNullException.ThrowIfNull(target);
        return CdpResult<T>.Fail(
            BrowserErrorCode.CdpPortOwnerMismatch,
            $"Tab {target.Id} was not listed by a browser whose process was read as the listener of CDP port {target.Port}, so " +
            "it cannot be tied to that browser. Nothing was sent to it. Find the tab again (list_tabs, or pass the window).");
    }
}
