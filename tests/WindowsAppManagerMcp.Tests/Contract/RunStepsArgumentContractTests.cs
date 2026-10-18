using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol.Protocol;
using WindowsAppManagerMcp.Infrastructure;

namespace WindowsAppManagerMcp.Tests.Contract;

/// <summary>
/// Contract: run_steps' one-window rule (roadmap M6.2) sees a step's window as the step's tool binds it, through the
/// production tool and filter registrations with mocked services (<see cref="ContractServices"/>). The tools read a number
/// from a JSON string (the SDK's serializer options), so a handle written as a string is the window it names; a step's own
/// members are refused when unknown, and a step names its tool. The runner's own rules are in StepRunnerTests.
/// </summary>
public class RunStepsArgumentContractTests
{
    private const long Notepad = TypicalDesktop.NotepadHandle;
    private const long Browser = TypicalDesktop.BrowserHandle;

    private static string Quoted(long handle) => $"\"{handle.ToString(CultureInfo.InvariantCulture)}\"";

    private static async Task<(CallToolResult Result, ContractServices Services)> RunAsync(string steps)
    {
        var services = new ContractServices(ContractMode.Typical);
        await using var server = await InProcessMcpServer.StartAsync(
            [], builder => builder.AddWindowsAppManagerTools(), configureServices: services.Register);
        var result = await server.CallToolAsync("run_steps", new Dictionary<string, object?>
        {
            ["window"] = new Dictionary<string, object?> { ["handle"] = Notepad },
            ["steps"] = JsonSerializer.Deserialize<JsonElement>(steps)
        });
        return (result, services);
    }

    private static string Text(CallToolResult result) => result.Content.OfType<TextContentBlock>().Single().Text;

    private static JsonObject Body(CallToolResult result) => JsonNode.Parse(Text(result))!.AsObject();

    private static void VerifyNothingTyped(ContractServices services) =>
        services.Input.Verify(i => i.TypeTextAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<InputTarget>(), It.IsAny<bool>(),
            It.IsAny<TextControlChars?>(), It.IsAny<TextInputMethod>(), It.IsAny<CancellationToken>()), Times.Never);

    [Fact]
    public async Task AStepThatNamesAnotherWindowsHandleAsAString_IsNotRun_AndTypesNothing()
    {
        // Arrange - the browser's handle as a JSON string, which type_text would bind as that window
        var (result, services) = await RunAsync($$"""
            [ { "tool": "type_text", "args": { "text": "secret", "windowHandle": {{Quoted(Browser)}} } } ]
            """);

        // Assert
        using (services)
        {
            result.IsError.Should().BeTrue();
            var step = Body(result)["steps"]![0]!;
            step["status"]!.GetValue<string>().Should().Be("failed");
            step["errorCode"]!.GetValue<string>().Should().Be("InvalidArguments");
            step["result"]!["error"]!.GetValue<string>().Should().Contain("not the run's window");
            VerifyNothingTyped(services);
        }
    }

    [Fact]
    public async Task AStepThatNamesTheRunsWindowAsAString_TypesIntoIt()
    {
        var (result, services) = await RunAsync($$"""
            [ { "tool": "type_text", "args": { "text": "notes", "windowHandle": {{Quoted(Notepad)}} } } ]
            """);

        using (services)
        {
            result.IsError.Should().NotBe(true, Text(result));
            services.Input.Verify(i => i.TypeTextAsync("notes", It.IsAny<int>(), It.Is<InputTarget>(t => t.WindowHandle == (nint)Notepad),
                It.IsAny<bool>(), It.IsAny<TextControlChars?>(), TextInputMethod.Unicode, It.IsAny<CancellationToken>()), Times.Once);
        }
    }

    [Theory]
    [InlineData("""{ "Handle": BROWSER }""")]
    [InlineData("""{ "handle": "BROWSER" }""")]
    [InlineData("""{ "HANDLE": "BROWSER" }""")]
    public async Task AStepWhoseSelectorNamesAnotherWindow_InAnyLetterCaseOrAsAString_IsNotRun(string selector)
    {
        var (result, services) = await RunAsync($$"""
            [ { "tool": "focus_window", "args": { "window": {{selector.Replace("BROWSER", Browser.ToString(CultureInfo.InvariantCulture))}} } } ]
            """);

        using (services)
        {
            result.IsError.Should().BeTrue();
            Body(result)["steps"]![0]!["result"]!["error"]!.GetValue<string>().Should().Contain("not the run's window");
            services.Foreground.Verify(f => f.EnsureForeground(It.IsAny<nint>(), It.IsAny<int>()), Times.Never);
        }
    }

    [Fact]
    public async Task AStepsHandleArgumentInAnotherLetterCase_RefusesTheRun()
    {
        // The SDK would not bind WindowHandle to windowHandle, and type_text without a window types into the one in front
        var (result, services) = await RunAsync($$"""
            [ { "tool": "type_text", "args": { "text": "secret", "WindowHandle": {{Browser}} } } ]
            """);

        using (services)
        {
            Body(result)["errorCode"]!.GetValue<string>().Should().Be("InvalidArguments");
            Text(result).Should().Contain("'WindowHandle' (did you mean 'windowHandle'?)").And.Contain("Nothing was done");
            VerifyNothingTyped(services);
        }
    }

    [Fact]
    public async Task AStepsHandleThatIsNoWholeNumber_RefusesTheRun()
    {
        var (result, services) = await RunAsync("""
            [ { "tool": "focus_window" }, { "tool": "type_text", "args": { "text": "secret", "windowHandle": "the editor" } } ]
            """);

        using (services)
        {
            Body(result)["errorCode"]!.GetValue<string>().Should().Be("InvalidArguments");
            Text(result).Should().Contain("Step 2 (type_text)").And.Contain("windowHandle").And.Contain("Nothing was done");
            services.Foreground.Verify(f => f.EnsureForeground(It.IsAny<nint>(), It.IsAny<int>()), Times.Never);
            VerifyNothingTyped(services);
        }
    }

    [Theory]
    [InlineData("""{ "tool": "focus_window", "arguments": { "handle": BROWSER } }""", "arguments")]
    [InlineData("""{ "tool": "type_text", "args": { "text": "a" }, "optinal": true }""", "optinal")]
    public async Task AStepMemberRunStepsDoesNotTake_RefusesTheRun_InsteadOfBeingIgnored(string step, string member)
    {
        // Ignored, "arguments" would leave the step without its args, and the run would give it the run's window instead
        var (result, services) = await RunAsync(
            $"[ {{ \"tool\": \"get_focused_element\" }}, {step.Replace("BROWSER", Browser.ToString(CultureInfo.InvariantCulture))} ]");

        using (services)
        {
            result.IsError.Should().BeTrue();
            Body(result)["errorCode"]!.GetValue<string>().Should().Be("InvalidArguments");

            // The binder refuses it: the error names the step by its place in the array and the member, never a .NET type
            Body(result)["error"]!.GetValue<string>().Should().Contain($"steps[1] has a member '{member}' that it does not take")
                .And.NotContain("WindowsAppManagerMcp").And.NotContain(".NET").And.EndWith("Nothing was done.");
            services.Foreground.Verify(f => f.EnsureForeground(It.IsAny<nint>(), It.IsAny<int>()), Times.Never);
            VerifyNothingTyped(services);
        }
    }

    [Theory]
    [InlineData("""{ "args": { "handle": NOTEPAD } }""")]
    [InlineData("""{ "tool": null, "args": { "handle": NOTEPAD } }""")]
    public async Task AStepThatNamesNoTool_RefusesTheRun_InsteadOfRunningFocusWindow(string step)
    {
        var (result, services) = await RunAsync(
            $"[ {{ \"tool\": \"get_focused_element\" }}, {step.Replace("NOTEPAD", Notepad.ToString(CultureInfo.InvariantCulture))} ]");

        using (services)
        {
            Body(result)["errorCode"]!.GetValue<string>().Should().Be("InvalidArguments");
            Text(result).Should().Contain("Step 2").And.Contain("no tool").And.Contain("Nothing was done");
            services.Foreground.Verify(f => f.EnsureForeground(It.IsAny<nint>(), It.IsAny<int>()), Times.Never);
        }
    }
}
