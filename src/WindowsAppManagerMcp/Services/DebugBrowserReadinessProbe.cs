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
/// free). An answer is the browser's only when the port's owner is the started browser or, when the owner cannot be
/// read, when the browser's own DevToolsActivePort names the port and the browser WebSocket URL that answered: a
/// browser-level URL gives full control of a browser, so an answer nothing ties to the started one is not reported as
/// its (as the targetId path of the reading tools fails closed). A port nothing listens on yet, or a request that timed
/// out, is asked again while the started browser runs; an answer that is not a browser's DevTools endpoint, or a
/// browser that has exited, is final. It reaches the network only through <see cref="ICdpService"/>, which the E2E
/// harness refuses.
/// </remarks>
public sealed class DebugBrowserReadinessProbe : IDebugBrowserReadiness
{
    private readonly ICdpService _cdp;
    private readonly ITcpPortOwnerResolver _portOwners;
    private readonly INativeProcessWrapper _processes;

    /// <param name="cdp">Asks the endpoint for /json/version.</param>
    /// <param name="portOwners">Tells which process listens on the port.</param>
    /// <param name="processes">Tells whether the started browser still runs (its start time).</param>
    public DebugBrowserReadinessProbe(ICdpService cdp, ITcpPortOwnerResolver portOwners, INativeProcessWrapper processes)
    {
        _cdp = cdp;
        _portOwners = portOwners;
        _processes = processes;
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
            if (OtherOwner(endpoint, _portOwners.GetListeningProcessId(endpoint.Port)) is { } held)
                return held;

            var left = timeout - stopwatch.Elapsed;
            if (left <= TimeSpan.Zero)
                return NotAnswered(endpoint, timeout, last);

            last = await _cdp.GetBrowserVersionAsync(endpoint.Port, left < AttemptTimeout ? left : AttemptTimeout, cancellationToken)
                .ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();

            if (last.Success)
                return Answered(endpoint, last.Value!);

            if (last.ErrorCode is not (BrowserErrorCode.CdpUnavailable or BrowserErrorCode.CdpTimeout))
            {
                return new DebugBrowserReadiness(
                    Ready: false,
                    Error: $"The browser's DevTools endpoint on {CdpEndpointValidator.Host}:{endpoint.Port} answered GET " +
                           $"/json/version, but not as a browser does ({last.DescribeError()}).",
                    Reason: DebugBrowserNotReadyReason.NotABrowsersAnswer);
            }

            // A browser that exited will never answer: its port refuses every request from now on
            if (!IsRunning(endpoint))
                return Exited(endpoint);

            left = timeout - stopwatch.Elapsed;
            if (left <= TimeSpan.Zero)
                return NotAnswered(endpoint, timeout, last);

            await Task.Delay(left < PollInterval ? left : PollInterval, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>Ready, when the answer can be tied to the started browser; not ready otherwise.</summary>
    private DebugBrowserReadiness Answered(DebugBrowserEndpoint endpoint, CdpBrowserVersion version)
    {
        // The answer came from whatever listens now; it must still be the browser's port
        var owner = _portOwners.GetListeningProcessId(endpoint.Port);
        if (OtherOwner(endpoint, owner) is { } taken)
            return taken;

        if (owner == null && !DevToolsActivePortFile.Names(endpoint.UserDataDir, endpoint.Port, version.WebSocketDebuggerUrl.AbsolutePath))
        {
            return new DebugBrowserReadiness(
                Ready: false,
                Error: $"The owner of port {endpoint.Port} on {CdpEndpointValidator.Host} could not be read, and the browser's " +
                       $"'{DevToolsActivePortFile.PathIn(endpoint.UserDataDir)}' does not name that port and the browser " +
                       $"WebSocket URL that answered, so nothing shows the endpoint is the {endpoint.Browser} that was started " +
                       $"(processId {endpoint.ProcessId.ToString(CultureInfo.InvariantCulture)}); it was not used. Launch again " +
                       "with port 0.",
                Reason: DebugBrowserNotReadyReason.PortOwnerUnknown);
        }

        return new DebugBrowserReadiness(
            Ready: true,
            BrowserVersion: version.Browser.Length > 0 ? version.Browser : null,
            CdpEndpoint: version.HttpEndpoint,
            BrowserWebSocketUrl: version.WebSocketDebuggerUrl.ToString());
    }

    /// <summary>Not ready when another process than the started browser listens on the port; null otherwise.</summary>
    private static DebugBrowserReadiness? OtherOwner(DebugBrowserEndpoint endpoint, int? owner)
    {
        if (owner is not int other || other == endpoint.ProcessId)
            return null;

        return new DebugBrowserReadiness(
            Ready: false,
            Error: $"Port {endpoint.Port} on {CdpEndpointValidator.Host} belongs to process {other.ToString(CultureInfo.InvariantCulture)}, " +
                   $"not to the {endpoint.Browser} that was started (processId {endpoint.ProcessId.ToString(CultureInfo.InvariantCulture)}), " +
                   "so its endpoint was not used: another program took the port, and the browser's debug port is not reachable " +
                   $"on {CdpEndpointValidator.Host}. Launch again with port 0, which lets the browser pick a free port.",
            Reason: DebugBrowserNotReadyReason.PortHeldByOtherProcess);
    }

    /// <summary>
    /// Whether the started browser still runs: its PID has the start time read after the start (any start time when that
    /// could not be read), as ProcessService tells a running browser from a reused PID.
    /// </summary>
    private bool IsRunning(DebugBrowserEndpoint endpoint) =>
        _processes.GetProcessStartTime(endpoint.ProcessId) is { } now && (endpoint.StartTime == null || now == endpoint.StartTime);

    private static DebugBrowserReadiness Exited(DebugBrowserEndpoint endpoint) =>
        new(
            Ready: false,
            Error: $"The {endpoint.Browser} that was started (processId {endpoint.ProcessId.ToString(CultureInfo.InvariantCulture)}) " +
                   $"exited before its DevTools endpoint on {CdpEndpointValidator.Host}:{endpoint.Port} answered GET /json/version.",
            Reason: DebugBrowserNotReadyReason.BrowserExited);

    private static DebugBrowserReadiness NotAnswered(DebugBrowserEndpoint endpoint, TimeSpan timeout, CdpResult<CdpBrowserVersion>? last) =>
        new(
            Ready: false,
            Error: $"The browser's DevTools endpoint on {CdpEndpointValidator.Host}:{endpoint.Port} did not answer GET /json/version " +
                   $"within {timeout.TotalSeconds.ToString("0.#", CultureInfo.InvariantCulture)} seconds" +
                   (last is { Success: false } ? $" (last: {last.DescribeError()})." : "."),
            Reason: DebugBrowserNotReadyReason.NotAnswered);
}
