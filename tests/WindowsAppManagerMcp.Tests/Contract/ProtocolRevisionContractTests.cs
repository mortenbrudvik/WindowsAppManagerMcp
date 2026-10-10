using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using WindowsAppManagerMcp.Infrastructure;

namespace WindowsAppManagerMcp.Tests.Contract;

/// <summary>
/// Contract: the server behaves the same for every MCP revision a client may negotiate. SDK 2.x speaks the 2026-07-28
/// revision (server/discover, no initialize handshake, elicitation as a multi-round-trip request), which its own client
/// prefers, and the earlier revisions with the initialize handshake, which stdio clients such as Claude Code and the CI
/// guard use. Most contract tests run on the SDK client's default; these run the surface and a typical call on both
/// paths. The elicitation, cancellation and stdin EOF tests of <see cref="InputControlToolContractTests"/> run on both.
/// </summary>
public class ProtocolRevisionContractTests
{
    public static TheoryData<string?> Revisions => new() { null, InProcessMcpServer.InitializeRevision, "2025-11-25" };

    [Theory]
    [InlineData(null, InProcessMcpServer.LatestRevision)]
    [InlineData(InProcessMcpServer.InitializeRevision, InProcessMcpServer.InitializeRevision)]
    [InlineData("2025-11-25", "2025-11-25")]
    public async Task TheClient_NegotiatesTheRevisionItAsksFor(string? requested, string negotiated)
    {
        await using var server = await InProcessMcpServer.StartAsync(
            [], builder => builder.AddWindowsAppManagerTools(), clientOptions: InProcessMcpServer.WithRevision(null, requested));

        server.Client.NegotiatedProtocolVersion.Should().Be(negotiated);
    }

    [Fact]
    public async Task ToolsList_IsTheSameOnTheInitializeHandshake_AsInTheSnapshot()
    {
        // The snapshot is listed on the SDK client's default revision; the CI guard lists the built exe's tools with
        // the initialize handshake (2025-06-18) and compares them with the same file
        var latest = await ToolSurface.ListToolsAsync();

        var initialize = await ToolSurface.ListToolsFromServerAsync(InProcessMcpServer.InitializeRevision);

        JsonNode.DeepEquals(initialize, latest).Should().BeTrue("a client on either revision must see the same tools");
    }

    [Theory]
    [MemberData(nameof(Revisions))]
    public async Task ACapture_ReturnsItsImageAsImageContent(string? revision)
    {
        // Arrange
        using var services = new ContractServices(ContractMode.Typical);
        await using var server = await InProcessMcpServer.StartAsync(
            [],
            builder => builder.AddWindowsAppManagerTools(),
            clientOptions: InProcessMcpServer.WithRevision(null, revision),
            configureServices: services.Register);

        // Act
        var result = await server.CallToolAsync("take_screenshot");

        // Assert - the bytes arrive once, base64-encoded on the wire, and decode to an image
        result.IsError.Should().NotBe(true);
        var image = result.Content[0].Should().BeOfType<ImageContentBlock>().Subject;
        image.MimeType.Should().Be("image/jpeg");
        image.DecodedData.ToArray().Should().StartWith(new byte[] { 0xFF, 0xD8 }, "a JPEG file starts with its SOI marker");
        result.Content[1].Should().BeOfType<TextContentBlock>();
    }

    [Theory]
    [MemberData(nameof(Revisions))]
    public async Task AFailingCall_IsAStructuredToolError(string? revision)
    {
        // Arrange
        using var services = new ContractServices(ContractMode.Typical);
        await using var server = await InProcessMcpServer.StartAsync(
            [],
            builder => builder.AddWindowsAppManagerTools(),
            clientOptions: InProcessMcpServer.WithRevision(null, revision),
            configureServices: services.Register);

        // Act - a required argument is missing
        var result = await server.CallToolAsync("get_layout");

        // Assert
        result.IsError.Should().BeTrue();
        var payload = JsonDocument.Parse(result.Content.OfType<TextContentBlock>().Single().Text).RootElement;
        payload.GetProperty("errorCode").GetString().Should().Be(ToolErrorReporting.InvalidArgumentsErrorCode);
    }

    [Fact]
    public void ABlankElicitationCapability_DeclaresForms()
    {
        // resume_input asks only a client whose capabilities name form elicitation. Clients of 2025-06-18 declare
        // "elicitation": {} (forms were the only mode then); the SDK reads that as form support, which this pins.
        var capabilities = JsonSerializer.Deserialize<ClientCapabilities>("""{ "elicitation": {} }""", McpJsonUtilities.DefaultOptions);

        capabilities!.Elicitation!.Form.Should().NotBeNull();
    }
}
