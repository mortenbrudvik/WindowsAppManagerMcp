using System.Reflection;
using System.Text.Json;
using System.Text.Json.Schema;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using WindowsAppManagerMcp.Infrastructure;
using WindowsAppManagerMcp.Tools;

namespace WindowsAppManagerMcp.Tests.Contract;

/// <summary>
/// Contract: every [McpServerTool] return type must serialize with the options the MCP SDK uses
/// for tool results (<see cref="McpJsonUtilities.DefaultOptions"/>, the default of
/// McpServerToolCreateOptions.SerializerOptions). A result that cannot be serialized fails
/// after the tool's side effects already happened (window moved, browser launched), and the
/// agent loses the whole result. A tool that builds its own <see cref="CallToolResult"/> (the
/// capture tools, which return image content) names the DTO of its text block with
/// <see cref="ToolResultPayloadAttribute"/>, and that DTO is checked instead.
/// </summary>
public class ToolResultSerializationTests
{
    private static IReadOnlyDictionary<string, MethodInfo> ToolMethods => ServerToolCatalog.Tools;

    public static TheoryData<string> ToolNames()
    {
        var data = new TheoryData<string>();
        foreach (var name in ToolMethods.Keys.OrderBy(n => n, StringComparer.Ordinal))
        {
            data.Add(name);
        }

        return data;
    }

    [Fact]
    public void Discovery_FindsExactlyTheToolsTheServerServes()
    {
        // The theories below run over the catalog, so it must be exactly what the production
        // registration serves: a served tool missing from it would never be checked.
        ServerToolCatalog.ServedToolNames.Should().Contain("launch_browser_with_debug").And.Contain("move_window");
        ToolMethods.Keys.Should().BeEquivalentTo(ServerToolCatalog.ServedToolNames,
            "every tool the server serves must have its result type checked");
    }

    [Fact]
    public void ToolsReturningCallToolResult_NameTheirTextBlockPayload()
    {
        // Without the attribute, the theories below would have nothing to check for these tools.
        var undeclared = ToolMethods
            .Where(t => UnwrapTask(t.Value.ReturnType) == typeof(CallToolResult))
            .Where(t => t.Value.GetCustomAttribute<ToolResultPayloadAttribute>() == null)
            .Select(t => t.Key);

        undeclared.Should().BeEmpty(
            "a tool that returns CallToolResult must name the DTO it serializes into its text block with [ToolResultPayload]");
        ToolMethods.Keys.Where(name => GetResultType(ToolMethods[name]) == typeof(ScreenshotResult))
            .Should().Contain("take_screenshot", "the capture tools are checked through their payload type");
    }

    [Theory]
    [MemberData(nameof(ToolNames))]
    public void ToolResult_WithAllMembersPopulated_SerializesWithMcpDefaultOptions(string toolName)
    {
        // Arrange
        var resultType = GetResultType(ToolMethods[toolName]);
        if (resultType == null)
        {
            return; // void / Task: nothing to serialize
        }

        var sample = SampleValueFactory.Create(resultType);

        // Act
        var act = () => JsonSerializer.Serialize(sample, resultType, McpJsonUtilities.DefaultOptions);

        // Assert
        act.Should().NotThrow(
            "the result of '{0}' ({1}) is serialized by the MCP SDK with McpJsonUtilities.DefaultOptions",
            toolName,
            resultType);
    }

    [Theory]
    [MemberData(nameof(ToolNames))]
    public void ToolResult_JsonSchema_HasNoUnsupportedTypes(string toolName)
    {
        // Arrange
        var resultType = GetResultType(ToolMethods[toolName]);
        if (resultType == null)
        {
            return;
        }

        // Act
        var schema = McpJsonUtilities.DefaultOptions.GetJsonSchemaAsNode(resultType).ToJsonString();

        // Assert - System.Text.Json marks types it cannot serialize (e.g. IntPtr) in the schema.
        schema.Should().NotContain(
            "Unsupported .NET type",
            "every member of the result of '{0}' ({1}) must be serializable",
            toolName,
            resultType);
    }

    [Fact]
    public async Task LaunchBrowserWithDebug_ThroughMcpServer_ReturnsWindowHandleAndLaunchDetails()
    {
        // Arrange - the normal success path finds the browser window, so the handle is non-null.
        const string userDataDir = @"C:\Temp\browser-debug";
        var processService = new Mock<IProcessService>();
        processService.Setup(s => s.LaunchBrowserWithDebug(
                It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<int>(), It.IsAny<string?>(), It.IsAny<bool>(), It.IsAny<int>()))
            .Returns(new DebugBrowserLaunchResult(
                Success: true,
                ProcessId: 4321,
                WindowHandle: new nint(5678),
                CdpPort: 9222,
                UserDataDir: userDataDir,
                Error: null,
                ErrorCode: null));
        var tools = new BrowserReadingTools(Mock.Of<IBrowserReadingService>(), processService.Object);
        await using var server = await InProcessMcpServer.StartAsync([], builder => builder.WithTools(tools));

        // Act - goes through the SDK's own argument binding and result serialization.
        var result = await server.CallToolAsync("launch_browser_with_debug");

        // Assert
        var text = result.Content.OfType<TextContentBlock>().Single().Text;
        result.IsError.Should().NotBe(true, "the call failed with: {0}", text);
        var json = JsonDocument.Parse(text).RootElement;
        json.GetProperty("success").GetBoolean().Should().BeTrue();
        json.GetProperty("processId").GetInt32().Should().Be(4321);
        json.GetProperty("windowHandle").GetInt64().Should().Be(5678);
        json.GetProperty("cdpPort").GetInt32().Should().Be(9222);
        json.GetProperty("userDataDir").GetString().Should().Be(userDataDir);
    }

    [Fact]
    public async Task ApplyLayout_ThroughMcpServer_ReturnsTheFocusOutcomeOfEachFocusedPlacement()
    {
        // Arrange - the placement with focus was arranged, but Windows kept the terminal in front
        var layoutService = new Mock<ILayoutService>();
        layoutService.Setup(l => l.ApplyPresetAsync("work-setup", It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new LayoutApplyResult(
                Success: false,
                PresetName: "work-setup",
                WindowsArranged: 1,
                Errors: ["Arranged window 'main.cs - Visual Studio Code' for placement 1 (process 'Code'), but could not bring it to the foreground (FocusFailed): ..."],
                ErrorCode: nameof(LayoutErrorCode.ArrangeFailed),
                Focus:
                [
                    new FocusResult(false, 2884384, false, ForegroundHandle: 1312934, ForegroundTitle: "pwsh",
                        ForegroundProcess: "WindowsTerminal", Error: "SetForegroundWindow was refused (foreground lock)",
                        ErrorCode: nameof(WindowErrorCode.FocusFailed))
                ]));
        await using var server = await InProcessMcpServer.StartAsync(
            [], builder => builder.WithTools(new LayoutPresetTools(layoutService.Object)));

        // Act - goes through the SDK's own argument binding and result serialization
        var result = await server.CallToolAsync("apply_layout", new Dictionary<string, object?> { ["name"] = "work-setup" });

        // Assert
        var text = result.Content.OfType<TextContentBlock>().Single().Text;
        var json = JsonDocument.Parse(text).RootElement;
        json.GetProperty("errorCode").GetString().Should().Be(nameof(LayoutErrorCode.ArrangeFailed));
        var focus = json.GetProperty("focus").EnumerateArray().Should().ContainSingle().Subject;
        focus.GetProperty("handle").GetInt64().Should().Be(2884384);
        focus.GetProperty("isForeground").GetBoolean().Should().BeFalse();
        focus.GetProperty("foregroundProcess").GetString().Should().Be("WindowsTerminal");
        focus.GetProperty("errorCode").GetString().Should().Be(nameof(WindowErrorCode.FocusFailed));
        focus.GetProperty("error").GetString().Should().Contain("foreground lock");
    }

    /// <summary>
    /// The type the SDK serializes for the tool: its return type, unwrapped from Task, or for a tool
    /// that returns <see cref="CallToolResult"/> the payload type of its text block. Null when there
    /// is nothing to serialize.
    /// </summary>
    private static Type? GetResultType(MethodInfo method)
    {
        var type = UnwrapTask(method.ReturnType);
        if (type == typeof(CallToolResult))
        {
            return method.GetCustomAttribute<ToolResultPayloadAttribute>()?.PayloadType;
        }

        return type;
    }

    private static Type? UnwrapTask(Type type)
    {
        if (type == typeof(void) || type == typeof(Task) || type == typeof(ValueTask))
        {
            return null;
        }

        if (type.IsGenericType &&
            (type.GetGenericTypeDefinition() == typeof(Task<>) || type.GetGenericTypeDefinition() == typeof(ValueTask<>)))
        {
            return type.GetGenericArguments()[0];
        }

        return type;
    }

}
