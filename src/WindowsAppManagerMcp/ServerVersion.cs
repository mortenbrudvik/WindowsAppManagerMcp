using System.Reflection;

namespace WindowsAppManagerMcp;

/// <summary>
/// The version the MCP server reports as serverInfo.version in the initialize handshake.
/// It comes from &lt;Version&gt; in Directory.Build.props (the single source of truth).
/// When built from a git checkout the SDK appends the commit SHA
/// (for example "1.11.0+0e58ea9..."), so clients can tell exactly which build is running.
/// </summary>
public static class ServerVersion
{
    public static string Current { get; } = FromAssembly(typeof(ServerVersion).Assembly);

    public static string FromAssembly(Assembly assembly) =>
        Resolve(
            assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion,
            assembly.GetName().Version);

    /// <summary>
    /// Prefers the informational version (version + commit SHA), then the three-part
    /// assembly version, then "0.0.0".
    /// </summary>
    public static string Resolve(string? informationalVersion, Version? assemblyVersion)
    {
        if (!string.IsNullOrWhiteSpace(informationalVersion))
        {
            return informationalVersion.Trim();
        }

        return assemblyVersion is null
            ? "0.0.0"
            : $"{assemblyVersion.Major}.{assemblyVersion.Minor}.{Math.Max(assemblyVersion.Build, 0)}";
    }
}
