using System.Text.Json;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using WindowsAppManagerMcp.Infrastructure;

namespace WindowsAppManagerMcp.Tests.Contract;

/// <summary>
/// Contract: get_server_info over MCP, through the production tool and filter registrations with the contract services
/// (a real report over mocked sources). It reports the revision and client of the session it is called on, on every
/// revision a client may negotiate, and how many tools the server serves; read-only mode serves it.
/// </summary>
public class ServerInfoToolContractTests
{
    public static TheoryData<string?> Revisions => InProcessMcpServer.Revisions();

    [Theory]
    [MemberData(nameof(Revisions))]
    public async Task GetServerInfo_ReportsTheSessionItIsCalledOn(string? revision)
    {
        // Arrange
        using var services = new ContractServices(ContractMode.Typical);
        var client = new McpClientOptions { ClientInfo = new Implementation { Name = "contract-test", Version = "3.1.4" } };
        await using var server = await InProcessMcpServer.StartAsync(
            [],
            builder => builder.AddWindowsAppManagerTools(),
            clientOptions: InProcessMcpServer.WithRevision(client, revision),
            configureServices: services.Register);

        // Act
        var info = Json(await server.CallToolAsync("get_server_info"));

        // Assert
        var connection = info.GetProperty("connection");
        connection.GetProperty("protocolVersion").GetString().Should().Be(revision ?? InProcessMcpServer.LatestRevision);
        connection.GetProperty("clientName").GetString().Should().Be("contract-test");
        connection.GetProperty("clientVersion").GetString().Should().Be("3.1.4");
        info.GetProperty("toolsets").GetProperty("servedTools").GetInt32().Should().Be(ServerToolCatalog.ServedToolNames.Count);
    }

    [Fact]
    public async Task GetServerInfo_ReportsTheEnvironment()
    {
        using var services = new ContractServices(ContractMode.Typical);
        await using var server = await InProcessMcpServer.StartAsync(
            [], builder => builder.AddWindowsAppManagerTools(), configureServices: services.Register);

        var info = Json(await server.CallToolAsync("get_server_info"));

        info.GetProperty("server").GetProperty("name").GetString().Should().Be("windows-app-manager");
        info.GetProperty("server").GetProperty("sdkVersion").GetString().Should().Be(ServerVersion.SdkVersion);
        info.GetProperty("process").GetProperty("integrityLevel").GetString().Should().Be("Medium");
        info.GetProperty("process").GetProperty("dpiAwareness").GetString().Should().Be("PerMonitorV2");
        info.GetProperty("desktop").GetProperty("inputDesktopAvailable").GetBoolean().Should().BeTrue();
        info.GetProperty("monitors").GetArrayLength().Should().Be(TypicalDesktop.Monitors.Count);
        info.GetProperty("host").GetProperty("windows")[0].GetProperty("processName").GetString().Should().Be("WindowsTerminal");
        info.GetProperty("input").GetProperty("hotkey").GetProperty("state").GetString().Should().Be("Registered");
        info.GetProperty("toolsets").GetProperty("source").GetString().Should().Be(ToolsetSelection.AllKeyword);
        info.GetProperty("paths").TryGetProperty("auditLog", out _).Should().BeFalse("there is no audit log until roadmap M2.17");
        info.TryGetProperty("warnings", out _).Should().BeFalse("nothing is wrong on the typical desktop");
    }

    [Fact]
    public async Task GetServerInfo_OnALockedDesktop_ReportsWarnings()
    {
        using var services = new ContractServices(ContractMode.Failing);
        await using var server = await InProcessMcpServer.StartAsync(
            [], builder => builder.AddWindowsAppManagerTools(), configureServices: services.Register);

        var result = await server.CallToolAsync("get_server_info");

        result.IsError.Should().NotBe(true, "the report itself succeeds; the warnings say what will fail");
        var warnings = Json(result).GetProperty("warnings").EnumerateArray().Select(w => w.GetString()).ToList();
        warnings.Should().Contain(w => w!.Contains("locked")).And.Contain(w => w!.Contains("DPI awareness"));
    }

    [Fact]
    public async Task ReadOnlyMode_ServesGetServerInfo_AndItReportsTheMode()
    {
        // Arrange
        using var services = new ContractServices(ContractMode.Typical);
        var readOnly = ToolsetSelection.FromEnvironment(name => name == ToolsetSelection.ReadOnlyVariable ? "1" : null, Toolsets.Server);
        await using var server = await InProcessMcpServer.StartAsync(
            [], builder => builder.AddWindowsAppManagerTools(readOnly), configureServices: services.Register);

        // Act
        var listed = await server.Client.ListToolsAsync();
        var info = Json(await server.CallToolAsync("get_server_info"));

        // Assert
        listed.Select(t => t.Name).Should().Contain("get_server_info");
        info.GetProperty("toolsets").GetProperty("readOnly").GetBoolean().Should().BeTrue();
        info.GetProperty("toolsets").GetProperty("servedTools").GetInt32().Should().Be(listed.Count);
    }

    private static JsonElement Json(CallToolResult result)
    {
        var text = result.Content.OfType<TextContentBlock>().Single().Text;
        result.IsError.Should().NotBe(true, "the call failed with: {0}", text);
        return JsonDocument.Parse(text).RootElement;
    }
}
