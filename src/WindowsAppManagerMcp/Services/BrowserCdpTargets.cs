using WindowsAppManagerMcp.Infrastructure;
using WindowsAppManagerMcp.Models;
using WindowsAppManagerMcp.Services.Interfaces;

namespace WindowsAppManagerMcp.Services;

/// <summary>
/// The browser and the tab a CDP action works on (roadmap M5.5, shared with the DOM bridge of M5.8): a window's process and
/// its own debug port (found as the reading tools find it since M5.3, or cdpPort), or the process that listens on cdpPort;
/// a recognized Chromium browser that the user's target policy allows; then the tab by id or the one the window shows.
/// </summary>
/// <remarks>
/// The target policy applies to every CDP action, also with an explicit cdpPort (the reads exempt an explicit port; an
/// action does not): for a window, the window's process (fail closed, as the UI Automation reads decide), and for a port
/// alone, the process that listens on it. Finding the tab then checks that the port's listener is that process
/// (<see cref="ICdpService.FindTargetByIdAsync"/>, <see cref="ICdpService.FindTargetForWindowAsync"/>; fail closed).
/// </remarks>
internal sealed class BrowserCdpTargets
{
    /// <summary>How long finding the browser's tab may take, apart from the call's own time limit.</summary>
    public static readonly TimeSpan LookupTimeout = TimeSpan.FromMilliseconds(CdpOptions.DefaultTimeoutMs);

    private readonly INativeWindowWrapper _windows;
    private readonly IBrowserDetectionService _detection;
    private readonly IBrowserReadingService _reading;
    private readonly ICdpService _cdp;
    private readonly ITcpPortOwnerResolver _portOwners;
    private readonly ITargetPolicy? _policy;
    private readonly IDebugPortLocator? _portLocator;

    public BrowserCdpTargets(
        INativeWindowWrapper windows,
        IBrowserDetectionService detection,
        IBrowserReadingService reading,
        ICdpService cdp,
        ITcpPortOwnerResolver portOwners,
        ITargetPolicy? policy,
        IDebugPortLocator? portLocator)
    {
        _windows = windows;
        _detection = detection;
        _reading = reading;
        _cdp = cdp;
        _portOwners = portOwners;
        _policy = policy;
        _portLocator = portLocator;
    }

    /// <summary>The tab with <paramref name="targetId"/>, or the one the window shows.</summary>
    public Task<CdpResult<CdpTarget>> FindTabAsync(ResolvedBrowser browser, string? targetId, CancellationToken cancellationToken) =>
        targetId != null
            ? _cdp.FindTargetByIdAsync(browser.Port, browser.ProcessId, targetId, LookupTimeout, cancellationToken)
            : _cdp.FindTargetForWindowAsync(browser.Port, browser.Window!, LookupTimeout, cancellationToken);

    /// <summary>
    /// The browser to work on, or why not: a window's process and its own debug port (or cdpPort), or the process that
    /// listens on cdpPort; a recognized Chromium browser the target policy allows.
    /// </summary>
    /// <param name="toolsName">Names the tools in messages, such as "The tab tools".</param>
    public ResolvedBrowser Resolve(BrowserTabTarget target, string toolsName = "The tab tools")
    {
        if (target.WindowHandle is not { } handle)
        {
            return target.CdpPort is int port
                ? ResolvePort(port)
                : ResolvedBrowser.Refused(ToolErrorReporting.InvalidArgumentsErrorCode, "Pass the browser window (handle or window) or cdpPort. Nothing was done.");
        }

        if (handle == nint.Zero || !_windows.IsWindow(handle))
            return ResolvedBrowser.Refused(nameof(BrowserErrorCode.InvalidHandle), "Invalid window handle. Nothing was done.");

        var root = _windows.GetAncestor(handle, WindowAncestor.Root);
        handle = root != nint.Zero ? root : handle;
        var processId = _windows.GetWindowThreadProcessId(handle);
        var processName = _windows.GetProcessName(processId) ?? "Unknown";
        if (!_detection.IsBrowser(processName))
        {
            return ResolvedBrowser.Refused(nameof(BrowserErrorCode.NotABrowser),
                $"Window process '{processName}' is not a recognized browser. Nothing was done.");
        }

        if (UiaTargets.ReadVerdict(_windows, _policy, handle) is { Allowed: false } verdict)
            return ResolvedBrowser.Refused(nameof(BrowserErrorCode.TargetDeniedByPolicy), PolicyRefusal($"Window {(long)handle}", verdict));

        if (_reading.GetBrowserType(handle, processName) == "firefox")
            return NoCdp(toolsName);

        int found;
        if (target.CdpPort is int given)
        {
            found = given;
        }
        else
        {
            var lookup = _portLocator?.Find((int)processId)
                         ?? new DebugPortLookup(DebugPortLookupStatus.NoDebugPort, null, "No debug port is looked for.");
            if (lookup.Port is not int located)
            {
                return ResolvedBrowser.Refused(
                    lookup.Status == DebugPortLookupStatus.HeldByOtherProcess
                        ? nameof(BrowserErrorCode.CdpPortOwnerMismatch)
                        : nameof(BrowserErrorCode.CdpUnavailable),
                    $"{toolsName} work through CDP only and need the DevTools port of the browser that owns the window, and none " +
                    $"was found. {lookup.Detail} Start the browser with launch_browser_with_debug, or pass cdpPort. Nothing was done.");
            }

            found = located;
        }

        var window = CdpWindowBinder.Describe(_windows, handle, processId, _windows.GetWindowText(handle));
        return new ResolvedBrowser(found, (int)processId, window, handle, null);
    }

    /// <summary>
    /// The browser that listens on <paramref name="port"/>, named by no window: its process must be readable, a browser
    /// and allowed by the target policy (the port's owner is the target).
    /// </summary>
    private ResolvedBrowser ResolvePort(int port)
    {
        if (!_portOwners.TryGetListeningProcessId(port, out var owner))
        {
            return ResolvedBrowser.Refused(nameof(BrowserErrorCode.CdpPortOwnerMismatch),
                $"Could not read which process owns CDP port {port}, so it cannot be checked against the target policy. Nothing " +
                "was done. Pass the browser window instead.");
        }

        if (owner is not int processId)
        {
            return ResolvedBrowser.Refused(nameof(BrowserErrorCode.CdpUnavailable),
                $"Nothing listens on {CdpEndpointValidator.Host}:{port}: the browser may have exited, or the port is wrong. Nothing was done.");
        }

        var processName = _windows.GetProcessName((uint)processId);
        if (processName == null || !_detection.IsBrowser(processName))
        {
            return ResolvedBrowser.Refused(nameof(BrowserErrorCode.NotABrowser),
                $"CDP port {port} belongs to process {processId} ('{processName ?? "unknown"}'), which is not a recognized browser. " +
                "Nothing was done.");
        }

        if (_policy != null && _policy.EvaluateFailClosed([processName]) is { Allowed: false } verdict)
            return ResolvedBrowser.Refused(nameof(BrowserErrorCode.TargetDeniedByPolicy), PolicyRefusal($"The browser on port {port}", verdict));

        if (_reading.GetBrowserType(nint.Zero, processName) == "firefox")
            return NoCdp("The tab tools");

        return new ResolvedBrowser(port, processId, null, null, null);
    }

    private static ResolvedBrowser NoCdp(string toolsName) => ResolvedBrowser.Refused(nameof(BrowserErrorCode.CdpUnavailable),
        $"Firefox has no CDP (its debug port speaks WebDriver BiDi); {toolsName.ToLowerInvariant()} need a Chromium browser started " +
        "with a debug port (launch_browser_with_debug). Nothing was done.");

    private static string PolicyRefusal(string what, TargetPolicyVerdict verdict) =>
        $"{what} is refused by the server's target policy: {verdict.Reason}. Nothing was done: the policy covers acting on a " +
        "browser through CDP as it covers input. It is set by the user in the server's environment and no tool argument changes " +
        "it; ask the user to do this step.";
}

/// <summary>
/// The browser a CDP action works on: its port, its process, the window named (with its handle), or why it was refused.
/// </summary>
internal sealed record ResolvedBrowser(int Port, int ProcessId, CdpWindow? Window, nint? WindowHandle, (string Code, string Error)? Refusal)
{
    public static ResolvedBrowser Refused(string code, string error) => new(0, 0, null, null, (code, error));
}
