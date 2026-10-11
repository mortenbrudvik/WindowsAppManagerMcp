using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using WindowsAppManagerMcp.Models;
using WindowsAppManagerMcp.Services.Interfaces;

namespace WindowsAppManagerMcp.Services;

/// <summary>
/// Logs once, when the server starts, what decides whether its answers mean what an agent expects: the server's and
/// the MCP SDK's versions and the DPI awareness in force. The awareness comes from the app manifest (per-monitor v2);
/// anything else means window, element and click coordinates can be scaled on monitors above 100%, so it is a warning.
/// get_server_info reports the same, on request.
/// </summary>
public sealed class ServerStartupLog(INativeSystemInfoWrapper system, ILogger<ServerStartupLog>? logger = null) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        var awareness = system.GetDpiAwareness();
        logger?.LogInformation(
            "Windows App Manager {Version} (MCP SDK {SdkVersion}), DPI awareness {DpiAwareness}",
            ServerVersion.Current, ServerVersion.SdkVersion, awareness);

        if (!DpiAwarenessNames.IsExpected(awareness))
        {
            logger?.LogWarning(
                "The DPI awareness in force is {DpiAwareness}, not the PerMonitorV2 the app manifest declares: window, element " +
                "and click coordinates can be scaled on monitors above 100%. Start the server exe itself, not through a host that " +
                "sets another awareness",
                awareness);
        }

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
