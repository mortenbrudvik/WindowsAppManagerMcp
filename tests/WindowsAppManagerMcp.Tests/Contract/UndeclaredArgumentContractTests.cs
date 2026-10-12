using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Protocol;
using WindowsAppManagerMcp.Infrastructure;

namespace WindowsAppManagerMcp.Tests.Contract;

/// <summary>
/// Contract (M3 review): an acting tool refuses an argument its input schema does not declare, before anything is
/// searched or done, with InvalidArguments naming it and the nearest declared name; a read tool still ignores it.
/// Through the production tool and filter registrations, with mocked services (<see cref="ContractServices"/>).
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
    [InlineData("click")]
    [InlineData("type_text")]
    [InlineData("respond_to_dialog")]
    [InlineData("file_dialog")]
    [InlineData("close_window")]
    [InlineData("kill_process")]
    public async Task EveryActingTool_RefusesAnUndeclaredArgument(string tool)
    {
        var (result, services) = await CallAsync(tool, With(tool, "windowHandel", 1706796L));

        using (services)
        {
            result.IsError.Should().BeTrue();
            var payload = Payload(result);
            payload.GetProperty("errorCode").GetString().Should().Be(ToolErrorReporting.InvalidArgumentsErrorCode);
            payload.GetProperty("error").GetString().Should().Contain("'windowHandel'");
        }
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
    [InlineData("read_element_text", "withinRef")]
    [InlineData("get_ui_elements", "controlType")]
    [InlineData("find_windows", "titleContainz")]
    public async Task AReadTool_StillIgnoresAnUndeclaredArgument(string tool, string argument)
    {
        var (result, services) = await CallAsync(tool, With(tool, argument, "x"));

        using (services)
        {
            result.IsError.Should().NotBe(true, result.Content.OfType<TextContentBlock>().FirstOrDefault()?.Text);
        }
    }

    [Fact]
    public void TheActingTools_AreTheElementDialogInputCloseAndKillTools()
    {
        UndeclaredArgumentToolFilter.ActingTools.Should().BeEquivalentTo(
            "element_action", "respond_to_dialog", "file_dialog",
            "click", "right_click", "double_click", "mouse_move", "scroll", "type_text", "send_keys",
            "close_window", "close_windows_batch", "close_application", "kill_process");
    }

    [Fact]
    public async Task EveryActingTool_IsServed_AndActs()
    {
        // The annotations do not give this list (every capture, launch and window tool is not read-only too), but every tool
        // on it must be one that acts
        var tools = await ToolSurface.ListToolsAsync();

        foreach (var name in UndeclaredArgumentToolFilter.ActingTools)
        {
            var tool = tools.Single(t => t!["name"]!.GetValue<string>() == name)!;
            tool["annotations"]!["readOnlyHint"]!.GetValue<bool>().Should().BeFalse("{0} acts", name);
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
}
