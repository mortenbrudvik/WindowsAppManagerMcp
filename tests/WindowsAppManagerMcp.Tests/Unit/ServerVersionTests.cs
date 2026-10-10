namespace WindowsAppManagerMcp.Tests.Unit;

/// <summary>
/// Tests for the version the MCP server reports as serverInfo.version.
/// The version is single-sourced from Directory.Build.props, so the MCP handshake
/// identifies which build is actually running.
/// </summary>
public class ServerVersionTests
{
    [Fact]
    public void Resolve_WithInformationalVersion_ReturnsItIncludingCommitSha()
    {
        var result = ServerVersion.Resolve("1.12.0+0123456789abcdef", new Version(1, 12, 0, 0));

        result.Should().Be("1.12.0+0123456789abcdef");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Resolve_WithoutInformationalVersion_FallsBackToThreePartAssemblyVersion(string? informational)
    {
        var result = ServerVersion.Resolve(informational, new Version(1, 12, 0, 0));

        result.Should().Be("1.12.0");
    }

    [Fact]
    public void Resolve_WithTwoPartAssemblyVersion_PadsPatchWithZero()
    {
        var result = ServerVersion.Resolve(null, new Version(2, 1));

        result.Should().Be("2.1.0");
    }

    [Fact]
    public void Resolve_WithNoVersionInformation_ReturnsZeroVersion()
    {
        var result = ServerVersion.Resolve(null, null);

        result.Should().Be("0.0.0");
    }

    [Fact]
    public void Current_ReportsVersionFromDirectoryBuildProps()
    {
        var propsVersion = RepositoryFiles.ReadPropsVersion();

        var current = ServerVersion.Current;

        // The SDK appends "+<commit sha>" when building from a git checkout.
        current.Split('+')[0].Should().Be(propsVersion);
    }
}
