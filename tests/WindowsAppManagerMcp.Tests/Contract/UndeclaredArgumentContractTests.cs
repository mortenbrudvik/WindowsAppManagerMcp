using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using WindowsAppManagerMcp.Infrastructure;

namespace WindowsAppManagerMcp.Tests.Contract;

/// <summary>
/// Contract (M3 review, roadmap M3.F42): every tool refuses an argument its input schema does not declare, before
/// anything is searched, read or done, with InvalidArguments naming it, listing the tool's parameters and, for a near
/// miss, the declared name it most likely stands for. Through the production tool and filter registrations, with mocked
/// services (<see cref="ContractServices"/>).
/// </summary>
public class UndeclaredArgumentContractTests
{
    private static async Task<(CallToolResult Result, ContractServices Services)> CallAsync(
        string tool, IReadOnlyDictionary<string, object?> arguments, IInputSession? session = null)
    {
        var services = new ContractServices(ContractMode.Typical);
        await using var server = await InProcessMcpServer.StartAsync(
            [], builder => builder.AddWindowsAppManagerTools(), configureServices: collection =>
            {
                services.Register(collection);
                if (session != null)
                {
                    collection.AddSingleton(session);
                }
            });
        return (await server.CallToolAsync(tool, arguments), services);
    }

    private static Dictionary<string, object?> With(string tool, string name, object? value) =>
        new(EveryToolContractTests.ArgumentsFor(tool)) { [name] = value };

    private static JsonElement Payload(CallToolResult result) =>
        JsonDocument.Parse(result.Content.OfType<TextContentBlock>().Single().Text).RootElement;

    [Fact]
    public async Task ElementAction_WithControlTypes_IsRefused_NamingTheArgumentAndTheNearestDeclaredOne()
    {
        // Act - controlTypes is get_ui_elements' name; element_action's criterion is controlType
        var (result, services) = await CallAsync("element_action", new Dictionary<string, object?>
        {
            ["handle"] = 1706796L, ["name"] = "Save", ["controlTypes"] = "Button", ["action"] = "invoke"
        });

        // Assert - nothing was searched for or done: the wider search could have found another element
        using (services)
        {
            result.IsError.Should().BeTrue();
            var payload = Payload(result);
            payload.GetProperty("errorCode").GetString().Should().Be(ToolErrorReporting.InvalidArgumentsErrorCode);
            payload.GetProperty("error").GetString().Should().Contain("'controlTypes'").And.Contain("'controlType'")
                .And.Contain("Nothing was done");
            services.ElementActions.Verify(a => a.ActAsync(It.IsAny<ElementActionRequest>(), It.IsAny<CancellationToken>()), Times.Never);
            services.ElementSearch.Verify(s => s.FindAsync(It.IsAny<ElementQuery>(), It.IsAny<CancellationToken>()), Times.Never);
        }
    }

    [Fact]
    public async Task ElementAction_WithWithinRef_IsRefused_PointingToFindUiElements()
    {
        var (result, services) = await CallAsync("element_action", With("element_action", "withinRef", "w4120035e17"));

        using (services)
        {
            var error = Payload(result).GetProperty("error").GetString();
            error.Should().Contain("'withinRef'").And.Contain("find_ui_elements").And.Contain("ref");
            services.ElementActions.Verify(a => a.ActAsync(It.IsAny<ElementActionRequest>(), It.IsAny<CancellationToken>()), Times.Never);
        }
    }

    [Theory]
    [MemberData(nameof(EveryToolContractTests.ServedTools), MemberType = typeof(EveryToolContractTests))]
    public async Task EveryTool_RefusesAnUndeclaredArgument_ListingItsParameters(string tool)
    {
        // Roadmap M3.F42: one check for every tool, keyed on the input schema it serves
        var (result, services) = await CallAsync(tool, With(tool, "bogusParam", 1));

        using (services)
        {
            result.IsError.Should().BeTrue();
            result.Content.OfType<ImageContentBlock>().Should().BeEmpty("nothing was captured");
            var payload = Payload(result);
            payload.GetProperty("errorCode").GetString().Should().Be(ToolErrorReporting.InvalidArgumentsErrorCode);
            var parameters = ToolSurface.Tool(await ToolSurface.ListToolsAsync(), tool)["inputSchema"]!["properties"]!
                .AsObject().Select(p => p.Key).ToList();
            payload.GetProperty("error").GetString().Should().StartWith($"Invalid arguments for tool '{tool}'.")
                .And.Contain("'bogusParam'")
                .And.Contain(parameters.Count == 0 ? "It takes no arguments." : $"Its parameters are {string.Join(", ", parameters)}.");
        }
    }

    [Fact]
    public async Task TakeScreenshot_WithHandle_IsRefused_InsteadOfCapturingTheMonitor()
    {
        // Measured on a real desktop: take_screenshot has no window parameter, and with handle it captured the primary monitor
        var (result, services) = await CallAsync("take_screenshot", new Dictionary<string, object?> { ["handle"] = 987156L });

        using (services)
        {
            result.IsError.Should().BeTrue();
            result.Content.OfType<ImageContentBlock>().Should().BeEmpty();
            var payload = Payload(result);
            payload.GetProperty("errorCode").GetString().Should().Be(ToolErrorReporting.InvalidArgumentsErrorCode);
            payload.GetProperty("error").GetString().Should().Contain("'handle'").And.Contain("capture_window");
        }
    }

    [Fact]
    public async Task GetWindowInfo_WithAnUndeclaredArgument_IsRefused_BeforeTheWindowIsRead()
    {
        // Measured on a real desktop: get_window_info with bogusParam succeeded
        var (result, services) = await CallAsync("get_window_info", new Dictionary<string, object?>
        {
            ["handle"] = 1706796L, ["bogusParam"] = 1
        });

        using (services)
        {
            Payload(result).GetProperty("error").GetString().Should().Be(
                "Invalid arguments for tool 'get_window_info'. It does not take 'bogusParam'. Its parameters are handle, " +
                "window, detail. Nothing was done: an argument a tool does not declare would be ignored, so the call " +
                "could act on, capture or read something other than what was meant.");
            services.Windows.Invocations.Should().BeEmpty();
        }
    }

    [Fact]
    public async Task Click_WithHandle_SuggestsWindowHandle_AndSendsNothing()
    {
        var (result, services) = await CallAsync("click", new Dictionary<string, object?> { ["x"] = 10, ["y"] = 10, ["handle"] = 1706796L });

        using (services)
        {
            Payload(result).GetProperty("error").GetString().Should().Contain("'handle' (did you mean 'windowHandle'?)");
            services.Input.Invocations.Should().BeEmpty();
        }
    }

    [Fact]
    public async Task AnArgumentInAnotherLetterCase_IsRefused_SuggestingTheDeclaredSpelling()
    {
        // The SDK would not bind Handle to handle (TheSdk_BindsAnArgumentOnlyByItsExactName)
        var (result, services) = await CallAsync("get_window_info", new Dictionary<string, object?> { ["Handle"] = 1706796L });

        using (services)
        {
            Payload(result).GetProperty("error").GetString().Should().Contain("'Handle' (did you mean 'handle'?)");
        }
    }

    [Fact]
    public async Task TheSdk_BindsAnArgumentOnlyByItsExactName()
    {
        // The check matches names as the SDK binds them: exactly, letter case included. Without the check, a parameter
        // named in another case is left at its default; an SDK that matched it would make the refusal wrong
        await using var server = await InProcessMcpServer.StartAsync([], builder => builder.WithServerTools(new BindingProbe()));

        var exact = await server.CallToolAsync("binding_probe", new Dictionary<string, object?> { ["windowHandle"] = 5 });
        var otherCase = await server.CallToolAsync("binding_probe", new Dictionary<string, object?> { ["WindowHandle"] = 5 });

        exact.Content.OfType<TextContentBlock>().Single().Text.Should().Be("5");
        otherCase.Content.OfType<TextContentBlock>().Single().Text.Should().Be("unbound");
    }

    [Fact]
    public async Task TheRequestsMeta_IsNoArgument_AndIsNotRefused()
    {
        // _meta (a progress token, or anything a client adds) sits beside the arguments, not among them
        using var services = new ContractServices(ContractMode.Typical);
        await using var connection = await RawMcpConnection.StartAsync(builder =>
        {
            builder.AddWindowsAppManagerTools();
            services.Register(builder.Services);
        });
        await connection.SendAsync($$"""
            { "jsonrpc": "2.0", "id": 1, "method": "initialize",
              "params": { "protocolVersion": "{{InProcessMcpServer.NewestInitializeRevision}}", "capabilities": {},
                          "clientInfo": { "name": "test", "version": "1.0.0" } } }
            """);
        await connection.ReadResponseAsync(1);
        await connection.SendAsync("""{ "jsonrpc": "2.0", "method": "notifications/initialized" }""");

        await connection.SendAsync("""
            { "jsonrpc": "2.0", "id": 2, "method": "tools/call",
              "params": { "name": "get_window_info", "arguments": { "handle": 1706796 },
                          "_meta": { "progressToken": "p1", "com.example/trace": "abc" } } }
            """);
        var result = (await connection.ReadResponseAsync(2)).GetProperty("result");

        (result.TryGetProperty("isError", out var isError) && isError.GetBoolean()).Should().BeFalse(
            "the call succeeds: {0}", result.GetRawText());
    }

    [Fact]
    public async Task AnUndeclaredArgument_IsRefusedWithoutWaitingForThePausedInput()
    {
        // The pause would refuse the call with PausedByUser; the argument is the agent's mistake and is reported first
        var session = new InputSession(new FakeInputWrapper());
        session.Pause(InputControlSources.Hotkey);

        var (result, services) = await CallAsync("close_window", With("close_window", "forse", true), session);

        using (services)
        {
            Payload(result).GetProperty("errorCode").GetString().Should().Be(ToolErrorReporting.InvalidArgumentsErrorCode);
        }
    }

    [Theory]
    [InlineData("element_action", false)]
    [InlineData("read_element_text", false)]
    [InlineData("wait_for_element", true)]
    public async Task TheScopeDescription_NamesWithinRef_OnlyForAToolThatTakesIt(string name, bool takesWithinRef)
    {
        var properties = ToolSurface.Tool(await ToolSurface.ListToolsAsync(), name)["inputSchema"]!["properties"]!.AsObject();

        properties.ContainsKey("withinRef").Should().Be(takesWithinRef);
        properties["scope"]!["description"]!.GetValue<string>().Contains("withinRef", StringComparison.Ordinal).Should().Be(takesWithinRef);
    }

    [Fact]
    public async Task GetDesktopStatesScope_OffersOnlyTheValuesItTakes()
    {
        var tool = ToolSurface.Tool(await ToolSurface.ListToolsAsync(), "get_desktop_state");
        var values = tool["inputSchema"]!["properties"]!["scope"]!["enum"]!.AsArray().Select(v => v?.GetValue<string>());

        values.Should().NotContain("desktop").And.Contain("window").And.Contain("windowAndPopups");
    }

    [Fact]
    public async Task GetDesktopState_WithScopeDesktop_IsInvalidArguments_PointingToFindUiElements()
    {
        var (result, services) = await CallAsync("get_desktop_state", With("get_desktop_state", "scope", "desktop"));

        using (services)
        {
            result.IsError.Should().BeTrue();
            var payload = Payload(result);
            payload.GetProperty("errorCode").GetString().Should().Be(ToolErrorReporting.InvalidArgumentsErrorCode);
            payload.GetProperty("error").GetString().Should().Contain("find_ui_elements");
        }
    }

    /// <summary>A tool that says whether the SDK bound its one parameter.</summary>
    private sealed class BindingProbe
    {
        [McpServerTool(Name = "binding_probe")]
        public static string Probe(long? windowHandle = null) =>
            windowHandle?.ToString(CultureInfo.InvariantCulture) ?? "unbound";
    }
}
