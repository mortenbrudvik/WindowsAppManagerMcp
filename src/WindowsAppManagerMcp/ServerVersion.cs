using System.Reflection;
using ModelContextProtocol.Server;

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

    /// <summary>The commit the server was built from (the build metadata of <see cref="Current"/>), or null.</summary>
    public static string? GitSha => GitShaOf(Current);

    /// <summary>The version of the MCP C# SDK (ModelContextProtocol) the server runs, without its commit.</summary>
    public static string SdkVersion { get; } = WithoutBuildMetadata(FromAssembly(typeof(McpServer).Assembly));

    /// <summary>The build metadata of a version ("1.13.0+abc" gives "abc"), or null when it has none.</summary>
    public static string? GitShaOf(string version)
    {
        var plus = version.IndexOf('+', StringComparison.Ordinal);
        return plus < 0 || plus == version.Length - 1 ? null : version[(plus + 1)..];
    }

    /// <summary>A version without its build metadata ("2.2.0+abc" gives "2.2.0").</summary>
    public static string WithoutBuildMetadata(string version)
    {
        var plus = version.IndexOf('+', StringComparison.Ordinal);
        return plus < 0 ? version : version[..plus];
    }

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
