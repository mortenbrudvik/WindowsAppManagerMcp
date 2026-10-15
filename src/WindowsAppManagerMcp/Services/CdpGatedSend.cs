using WindowsAppManagerMcp.Models;
using WindowsAppManagerMcp.Services.Interfaces;

namespace WindowsAppManagerMcp.Services;

/// <summary>
/// Sends a CDP command that changes the browser inside the input gate (roadmap M5.5, shared with the DOM bridge of M5.8):
/// the gate is held until the browser answers or <see cref="GateHold"/> has passed, so the command is ordered with the input
/// calls (and refused while the user has paused input) but a slow page never holds them up.
/// </summary>
internal static class CdpGatedSend
{
    /// <summary>
    /// How long the input gate is held for a command that changes the browser: until the browser answers it, at most this
    /// long. The command is on its way by then; the gate orders it with the input calls, it does not wait for the page.
    /// </summary>
    public static readonly TimeSpan GateHold = TimeSpan.FromSeconds(1);

    /// <summary>Sends the command inside the gate and waits for its reply (or <paramref name="timeout"/>).</summary>
    public static async Task<CdpResult<CdpReply>> SendAsync(
        ICdpConnection connection, CdpGate gate, string method, object? parameters, TimeSpan timeout, CancellationToken cancellationToken,
        CdpSendOptions options = CdpSendOptions.None)
    {
        if (timeout <= TimeSpan.Zero)
        {
            return CdpResult<CdpReply>.Fail(
                BrowserErrorCode.CdpTimeout, $"The time limit was used up before {method} could be sent. Nothing was changed.");
        }

        var sent = await StartAsync(connection, gate, method, parameters, timeout, cancellationToken, options).ConfigureAwait(false);
        return await sent.ConfigureAwait(false);
    }

    /// <summary>
    /// Sends the command inside the gate as <see cref="SendAsync"/> does, and returns once the gate is let go, with the
    /// reply still to come when the browser has not answered within <see cref="GateHold"/>.
    /// </summary>
    public static async Task<Task<CdpResult<CdpReply>>> StartAsync(
        ICdpConnection connection, CdpGate gate, string method, object? parameters, TimeSpan timeout, CancellationToken cancellationToken,
        CdpSendOptions options = CdpSendOptions.None)
    {
        Task<CdpResult<CdpReply>> sent;
        using (await gate(cancellationToken).ConfigureAwait(false))
        {
            sent = connection.SendAsync(method, parameters, timeout, cancellationToken, options);
            using var hold = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            var held = Task.Delay(GateHold, hold.Token);
            await Task.WhenAny(sent, held).ConfigureAwait(false);
            hold.Cancel();
        }

        return sent;
    }
}
