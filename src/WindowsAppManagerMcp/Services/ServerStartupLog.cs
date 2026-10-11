using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Server;
using WindowsAppManagerMcp.Infrastructure;
using WindowsAppManagerMcp.Models;
using WindowsAppManagerMcp.Services.Interfaces;

namespace WindowsAppManagerMcp.Services;

/// <summary>
/// Logs once, when the server starts, what decides whether its answers mean what an agent expects: the server's and
/// the MCP SDK's versions, the DPI awareness in force, and which tools it serves. The awareness comes from the app
/// manifest (per-monitor v2); anything else means window, element and click coordinates can be scaled on monitors above
/// 100%, so it is a warning. So is every toolset entry that was ignored (a typo in WAM_TOOLSETS would otherwise only
/// show as missing tools). get_server_info reports the same, on request.
/// </summary>
public sealed class ServerStartupLog(
    INativeSystemInfoWrapper system,
    ToolsetSelection? toolsets = null,
    IOptions<McpServerOptions>? serverOptions = null,
    ILogger<ServerStartupLog>? logger = null,
    ClipboardOptions? clipboard = null,
    ProtocolAllowlist? protocols = null) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        var awareness = system.GetDpiAwareness();
        logger?.LogInformation(
            "Windows App Manager {Version} (MCP SDK {SdkVersion}), DPI awareness {DpiAwareness}",
            ServerVersion.Current, ServerVersion.SdkVersion, awareness);

        if (!DpiAwarenessNames.GivesPhysicalPixels(awareness))
        {
            logger?.LogWarning(
                "The DPI awareness in force is {DpiAwareness}, not the PerMonitorV2 the app manifest declares: window, element " +
                "and click coordinates can be scaled on monitors above 100%. Start the server exe itself, not through a host that " +
                "sets another awareness",
                awareness);
        }

        if (toolsets != null)
        {
            LogToolsets(toolsets, serverOptions?.Value.ToolCollection?.Count ?? 0);
        }

        if (clipboard != null || protocols != null)
        {
            LogClipboardAndProtocols(clipboard ?? ClipboardOptions.Default, protocols ?? ProtocolAllowlist.BuiltIn);
        }

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    private void LogClipboardAndProtocols(ClipboardOptions clipboard, ProtocolAllowlist protocols)
    {
        logger?.LogInformation(
            "Reading the user's clipboard without asking ({ClipboardVariable}): {UserClipboard}; launch_application opens the URI " +
            "schemes {Schemes}",
            ClipboardOptions.EnvironmentVariable,
            clipboard.AllowUserClipboard ? "on" : "off",
            string.Join(", ", protocols.AllowedSchemes));

        if (clipboard.Warning is { } clipboardWarning)
        {
            logger?.LogWarning("{ClipboardWarning}", clipboardWarning);
        }

        if (protocols.IgnoredWarning is { } protocolWarning)
        {
            logger?.LogWarning("{ProtocolWarning}", protocolWarning);
        }
    }

    private void LogToolsets(ToolsetSelection selection, int served)
    {
        logger?.LogInformation(
            "Serving {Served} tools: toolsets {Enabled} (from {Source}); not served: {Disabled}; read-only mode {ReadOnly}; " +
            "excluded tools: {Excluded}",
            served,
            string.Join(", ", selection.Enabled),
            selection.Source,
            selection.Disabled.Count == 0 ? "none" : string.Join(", ", selection.Disabled),
            selection.ReadOnly ? $"on ({ToolsetSelection.ReadOnlyVariable}: only tools annotated read-only)" : "off",
            selection.ExcludedTools.Count == 0 ? "none" : string.Join(", ", selection.ExcludedTools));

        foreach (var warning in selection.Warnings)
        {
            logger?.LogWarning("{ToolsetWarning}", warning);
        }

        if (served == 0)
        {
            logger?.LogWarning(
                "No tool is served: the toolsets ({Toolsets}), {ExcludeVariable} and {ReadOnlyVariable} leave out every tool",
                ToolsetSelection.ToolsetsVariable, ToolsetSelection.ExcludeToolsVariable, ToolsetSelection.ReadOnlyVariable);
        }
    }
}
