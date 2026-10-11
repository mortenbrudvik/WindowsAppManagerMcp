using System.ComponentModel;
using ModelContextProtocol.Server;
using WindowsAppManagerMcp.Models;
using WindowsAppManagerMcp.Services.Interfaces;

namespace WindowsAppManagerMcp.Tools;

/// <summary>
/// get_server_info (roadmap M2.11): one read-only call that tells the agent which build is running and what will make
/// its tools fail or its coordinates wrong, before it acts.
/// </summary>
[McpServerToolType]
public class ServerInfoTools
{
    private readonly IServerInfoService _info;

    public ServerInfoTools(IServerInfoService info)
    {
        _info = info ?? throw new ArgumentNullException(nameof(info));
    }

    [McpServerTool(Name = "get_server_info", Title = "Server info", ReadOnly = true, Destructive = false, Idempotent = true,
        OpenWorld = false)]
    [Description("The server's build and environment in one read-only call: version and commit, MCP SDK and protocol version, " +
                 "integrity level and elevation (Windows drops input to windows above it: TargetElevated), the DPI awareness in " +
                 "force (PerMonitorV2 means every coordinate is a physical pixel), whether input can reach the desktop (locked " +
                 "session, UAC prompt), the foreground lock timeout, the monitors with their DPI, the agent host windows and " +
                 "processes input never reaches (TargetIsAgentHost), whether the user paused input and the pause hotkey, the " +
                 "target policy and destructive chords, the layouts folder and the audit log folder, and the toolsets, excluded tools and read-only mode " +
                 "in force (a tool you do not see may be turned off there). warnings lists what will make tools fail or " +
                 "coordinates wrong. Call it first, and whenever tools behave unexpectedly. Changes nothing.")]
    public ServerInfoResult GetServerInfo(McpServer server)
    {
        ArgumentNullException.ThrowIfNull(server);

        var connection = new ServerConnectionInfo(server.NegotiatedProtocolVersion, server.ClientInfo?.Name, server.ClientInfo?.Version);
        return _info.Read(connection, server.ServerOptions.ToolCollection?.Count ?? 0);
    }
}
