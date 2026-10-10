namespace WindowsAppManagerMcp.Tests.Unit;

/// <summary>
/// Guards against version drift between Directory.Build.props (the single source of truth)
/// and the Claude Code plugin manifests. scripts/Set-Version.ps1 updates all three together.
/// These tests only read files in the repository.
/// </summary>
public class VersionConsistencyTests
{
    private const string PluginName = "windows-app-manager";

    [Fact]
    public void PluginJson_Version_MatchesDirectoryBuildProps()
    {
        var propsVersion = RepositoryFiles.ReadPropsVersion();
        using var plugin = RepositoryFiles.ReadJson(".claude-plugin", "plugin.json");

        plugin.RootElement.GetProperty("version").GetString().Should().Be(propsVersion,
            "run scripts/Set-Version.ps1 to change the version in every manifest at once");
    }

    [Fact]
    public void MarketplaceJson_PluginEntryVersion_MatchesDirectoryBuildProps()
    {
        var propsVersion = RepositoryFiles.ReadPropsVersion();
        using var marketplace = RepositoryFiles.ReadJson(".claude-plugin", "marketplace.json");

        var entry = marketplace.RootElement.GetProperty("plugins").EnumerateArray()
            .Single(p => p.GetProperty("name").GetString() == PluginName);

        entry.GetProperty("version").GetString().Should().Be(propsVersion,
            "run scripts/Set-Version.ps1 to change the version in every manifest at once");
    }

    [Fact]
    public void PluginJson_RunsTheExeInRepositoryBin()
    {
        // bin/WindowsAppManagerMcp.exe is the only binary the plugin runs, and the only one
        // scripts/Publish-Plugin.ps1 refreshes.
        using var plugin = RepositoryFiles.ReadJson(".claude-plugin", "plugin.json");

        var command = plugin.RootElement.GetProperty("mcpServers").GetProperty(PluginName)
            .GetProperty("command").GetString();

        command.Should().Be("${CLAUDE_PLUGIN_ROOT}/bin/WindowsAppManagerMcp.exe");
    }
}
