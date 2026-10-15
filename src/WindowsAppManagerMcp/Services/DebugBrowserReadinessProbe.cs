using System.Diagnostics;
using System.Globalization;
using WindowsAppManagerMcp.Models;
using WindowsAppManagerMcp.Services.Interfaces;

namespace WindowsAppManagerMcp.Services;

/// <summary>
/// launch_browser_with_debug's readiness check (roadmap M5.1): polls the started browser's /json/version through
/// <see cref="ICdpService.GetBrowserVersionAsync"/> until it answers, for at most the caller's timeout.
/// </summary>
/// <remarks>
/// Only 127.0.0.1 on the launched port is asked, and only over HTTP: nothing is connected. The port must not belong to
/// another process than the started browser (an explicit port another program took after the server checked it was
/// free); an owner that cannot be read does not stop the check, as for the reading tools' title match. A port nothing
/// listens on yet, or a request that timed out, is asked again; an answer that is not a browser's DevTools endpoint is
/// final. It reaches the network only through <see cref="ICdpService"/>, which the E2E harness refuses.
/// </remarks>
public sealed class DebugBrowserReadinessProbe : IDebugBrowserReadiness
{
    private readonly ICdpService _cdp;
    private readonly ITcpPortOwnerResolver _portOwners;

    /// <param name="cdp">Asks the endpoint for /json/version.</param>
    /// <param name="portOwners">Tells which process listens on the port.</param>
    public DebugBrowserReadinessProbe(ICdpService cdp, ITcpPortOwnerResolver portOwners)
    {
        _cdp = cdp;
        _portOwners = portOwners;
    }

    /// <summary>The pause between two requests; tests shorten it.</summary>
    public TimeSpan PollInterval { get; init; } = TimeSpan.FromMilliseconds(250);

    /// <summary>The longest one request may take, so that a stalled one leaves time to ask again; tests shorten it.</summary>
    public TimeSpan AttemptTimeout { get; init; } = TimeSpan.FromSeconds(2);

    /// <inheritdoc />
    public async Task<DebugBrowserReadiness> WaitUntilReadyAsync(
        DebugBrowserEndpoint endpoint,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        var stopwatch = Stopwatch.StartNew();
        CdpResult<CdpBrowserVersion>? last = null;

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (OtherOwner(endpoint) is { } held)
                return held;

            var left = timeout - stopwatch.Elapsed;
            if (left <= TimeSpan.Zero)
                return NotAnswered(endpoint, timeout, last);

            last = await _cdp.GetBrowserVersionAsync(endpoint.Port, left < AttemptTimeout ? left : AttemptTimeout, cancellationToken)
                .ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();

            if (last.Success)
            {
                // The answer came from whatever listens now; it must still be the browser's port
                if (OtherOwner(endpoint) is { } taken)
                    return taken;

                var version = last.Value!;
                return new DebugBrowserReadiness(
                    Ready: true,
                    BrowserVersion: version.Browser.Length > 0 ? version.Browser : null,
                    CdpEndpoint: version.HttpEndpoint,
                    BrowserWebSocketUrl: version.WebSocketDebuggerUrl.ToString());
            }

            if (last.ErrorCode is not (BrowserErrorCode.CdpUnavailable or BrowserErrorCode.CdpTimeout))
            {
                return new DebugBrowserReadiness(
                    Ready: false,
                    Error: $"The browser's DevTools endpoint on {CdpEndpointValidator.Host}:{endpoint.Port} answered GET " +
                           $"/json/version, but not as a browser does ({last.DescribeError()}).");
            }

            left = timeout - stopwatch.Elapsed;
            if (left <= TimeSpan.Zero)
                return NotAnswered(endpoint, timeout, last);

            await Task.Delay(left < PollInterval ? left : PollInterval, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>Not ready when another process than the started browser listens on the port; null otherwise.</summary>
    private DebugBrowserReadiness? OtherOwner(DebugBrowserEndpoint endpoint)
    {
        if (_portOwners.GetListeningProcessId(endpoint.Port) is not int owner || owner == endpoint.ProcessId)
            return null;

        return new DebugBrowserReadiness(
            Ready: false,
            Error: $"Port {endpoint.Port} on {CdpEndpointValidator.Host} belongs to process {owner.ToString(CultureInfo.InvariantCulture)}, " +
                   $"not to the {endpoint.Browser} that was started (processId {endpoint.ProcessId.ToString(CultureInfo.InvariantCulture)}), " +
                   "so its endpoint was not used: another program took the port, and the browser could not open its debug " +
                   "port there. Launch again with port 0, which lets the browser pick a free port.");
    }

    private static DebugBrowserReadiness NotAnswered(DebugBrowserEndpoint endpoint, TimeSpan timeout, CdpResult<CdpBrowserVersion>? last) =>
        new(
            Ready: false,
            Error: $"The browser's DevTools endpoint on {CdpEndpointValidator.Host}:{endpoint.Port} did not answer GET /json/version " +
                   $"within {timeout.TotalSeconds.ToString("0.#", CultureInfo.InvariantCulture)} seconds" +
                   (last is { Success: false } ? $" (last: {last.DescribeError()})." : "."));
}
