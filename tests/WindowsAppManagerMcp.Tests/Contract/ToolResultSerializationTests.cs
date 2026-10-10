using System.Reflection;
using System.Text.Json;
using System.Text.Json.Schema;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using WindowsAppManagerMcp.Tools;

namespace WindowsAppManagerMcp.Tests.Contract;

/// <summary>
/// Contract: every [McpServerTool] return type must serialize with the options the MCP SDK uses
/// for tool results (<see cref="McpJsonUtilities.DefaultOptions"/>, the default of
/// McpServerToolCreateOptions.SerializerOptions). A result that cannot be serialized fails
/// after the tool's side effects already happened (window moved, browser launched), and the
/// agent loses the whole result.
/// </summary>
public class ToolResultSerializationTests
{
    private static readonly Dictionary<string, MethodInfo> ToolMethods = DiscoverToolMethods();

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
    public void Discovery_FindsTheServerTools()
    {
        // Guards against the theories below silently running over an empty set.
        ToolMethods.Should().ContainKey("launch_browser_with_debug");
        ToolMethods.Should().ContainKey("move_window");
        ToolMethods.Count.Should().BeGreaterThan(30);
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

    private static Type? GetResultType(MethodInfo method)
    {
        var type = method.ReturnType;
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

    private static Dictionary<string, MethodInfo> DiscoverToolMethods()
    {
        // Mirrors WithTools<T>(): public and non-public, instance and static methods on [McpServerToolType] types.
        const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic |
                                   BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;

        return typeof(BrowserReadingTools).Assembly.GetTypes()
            .Where(t => t.GetCustomAttribute<McpServerToolTypeAttribute>() != null)
            .SelectMany(t => t.GetMethods(flags))
            .Select(m => (Method: m, Attribute: m.GetCustomAttribute<McpServerToolAttribute>()))
            .Where(x => x.Attribute != null)
            .ToDictionary(x => x.Attribute!.Name ?? x.Method.Name, x => x.Method);
    }
}
