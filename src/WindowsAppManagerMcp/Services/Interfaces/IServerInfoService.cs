using WindowsAppManagerMcp.Models;

namespace WindowsAppManagerMcp.Services.Interfaces;

/// <summary>
/// Reads what get_server_info reports (roadmap M2.11): the build, the process, the desktop, the monitors, the host,
/// the pause, the safety settings, the paths and the toolsets. Only reads; cheap enough to call before a task.
/// </summary>
public interface IServerInfoService
{
    /// <summary>The server's state now.</summary>
    /// <param name="connection">The MCP session the call came in on (the tool reads it from the request).</param>
    /// <param name="servedTools">How many tools the server serves (the tool reads it from the server's options).</param>
    ServerInfoResult Read(ServerConnectionInfo connection, int servedTools);
}
