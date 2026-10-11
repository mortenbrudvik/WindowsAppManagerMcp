using System.Text.Json;
using ModelContextProtocol.Protocol;
using WindowsAppManagerMcp.Infrastructure;

namespace WindowsAppManagerMcp.Tests.Contract;

/// <summary>
/// Contract: wait_for_element (roadmap M3.8) through the production tool and filter registrations, with mocked services
/// (<see cref="ContractServices"/>). The arguments bind from their JSON names (ref included), a timeout reaches the client
/// as isError with the result body (timedOut, lastObservedState, errorCode WaitTimedOut), an unknown condition is refused
/// before anything waits, and a wait in flight does not hold the input gate.
/// </summary>
public class WaitForElementContractTests
{
    private static Task<InProcessMcpServer> StartAsync(ContractServices services) =>
        InProcessMcpServer.StartAsync([], builder => builder.AddWindowsAppManagerTools(), configureServices: services.Register);

    private static JsonElement Payload(CallToolResult result) =>
        JsonDocument.Parse(result.Content.OfType<TextContentBlock>().Single().Text).RootElement;

    private static void ReturnsRequest(ContractServices services, Action<ElementWaitRequest> seen) =>
        services.ElementWait.Setup(w => w.WaitAsync(It.IsAny<ElementWaitRequest>(), It.IsAny<IProgress<OperationProgress>?>(), It.IsAny<CancellationToken>()))
            .Callback((ElementWaitRequest r, IProgress<OperationProgress>? _, CancellationToken _) => seen(r))
            .ReturnsAsync(new WaitForElementResult(true, true, false, 10, 1));

    [Fact]
    public async Task TheCriteria_BindFromTheirJsonNames()
    {
        // Arrange
        using var services = new ContractServices(ContractMode.Typical);
        ElementWaitRequest? request = null;
        ReturnsRequest(services, r => request = r);
        await using var server = await StartAsync(services);

        // Act
        var result = await server.CallToolAsync("wait_for_element", new Dictionary<string, object?>
        {
            ["window"] = new Dictionary<string, object?> { ["processName"] = "notepad" },
            ["automationId"] = "StatusText",
            ["controlType"] = "Text",
            ["condition"] = "name_equals",
            ["expected"] = "Saved",
            ["timeoutMs"] = 5000
        });

        // Assert
        result.IsError.Should().NotBe(true);
        request.Should().BeEquivalentTo(new ElementWaitRequest
        {
            Window = new WindowSelector { ProcessName = "notepad" },
            Criteria = new ElementQuery(nint.Zero, AutomationId: "StatusText", ControlTypes: ["Text"]),
            Condition = ElementWaitCondition.NameEquals,
            Expected = "Saved",
            TimeoutMs = 5000
        });
    }

    [Fact]
    public async Task ARef_BindsFromRef()
    {
        using var services = new ContractServices(ContractMode.Typical);
        ElementWaitRequest? request = null;
        ReturnsRequest(services, r => request = r);
        await using var server = await StartAsync(services);

        var result = await server.CallToolAsync("wait_for_element", new Dictionary<string, object?>
        {
            ["ref"] = "w4120035e9",
            ["condition"] = "toggle_state",
            ["expected"] = "on"
        });

        result.IsError.Should().NotBe(true);
        request!.Ref.Should().Be("w4120035e9");
        request.Condition.Should().Be(ElementWaitCondition.ToggleState);
        request.Criteria.Should().BeNull();
    }

    [Fact]
    public async Task ATimeout_IsAnError_WithTheResultBody()
    {
        // Arrange - the failing contract mode's wait times out
        using var services = new ContractServices(ContractMode.Failing);
        await using var server = await StartAsync(services);

        // Act
        var result = await server.CallToolAsync("wait_for_element", new Dictionary<string, object?>
        {
            ["window"] = new Dictionary<string, object?> { ["processName"] = "notepad" },
            ["name"] = "Save",
            ["condition"] = "enabled"
        });

        // Assert
        result.IsError.Should().BeTrue();
        var payload = Payload(result);
        payload.GetProperty("success").GetBoolean().Should().BeFalse();
        payload.GetProperty("satisfied").GetBoolean().Should().BeFalse();
        payload.GetProperty("timedOut").GetBoolean().Should().BeTrue();
        payload.GetProperty("polls").GetInt32().Should().Be(24);
        payload.GetProperty("lastObservedState").GetString().Should().Contain("is disabled");
        payload.GetProperty("element").GetProperty("ref").GetString().Should().Be("w4120035e9");
        payload.GetProperty("errorCode").GetString().Should().Be("WaitTimedOut");
    }

    [Fact]
    public async Task AnUnknownCondition_IsInvalidArguments_AndNothingWaits()
    {
        using var services = new ContractServices(ContractMode.Typical);
        await using var server = await StartAsync(services);

        var result = await server.CallToolAsync("wait_for_element", new Dictionary<string, object?>
        {
            ["window"] = new Dictionary<string, object?> { ["processName"] = "notepad" },
            ["name"] = "Save",
            ["condition"] = "clickable"
        });

        result.IsError.Should().BeTrue();
        Payload(result).GetProperty("errorCode").GetString().Should().Be(ToolErrorReporting.InvalidArgumentsErrorCode);
        services.ElementWait.Verify(w => w.WaitAsync(It.IsAny<ElementWaitRequest>(), It.IsAny<IProgress<OperationProgress>?>(),
            It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task AWaitInFlight_DoesNotHoldTheInputGate()
    {
        // Arrange - a wait that runs until the test releases it
        using var services = new ContractServices(ContractMode.Typical);
        var running = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        services.ElementWait.Setup(w => w.WaitAsync(It.IsAny<ElementWaitRequest>(), It.IsAny<IProgress<OperationProgress>?>(), It.IsAny<CancellationToken>()))
            .Returns(async (ElementWaitRequest _, IProgress<OperationProgress>? _, CancellationToken token) =>
            {
                running.TrySetResult();
                await release.Task.WaitAsync(token);
                return new WaitForElementResult(true, true, false, 10, 1);
            });
        await using var server = await StartAsync(services);
        var wait = server.CallToolAsync("wait_for_element", new Dictionary<string, object?>
        {
            ["window"] = new Dictionary<string, object?> { ["processName"] = "notepad" },
            ["name"] = "Save"
        });
        await running.Task.WaitAsync(TimeSpan.FromSeconds(10));

        // Act - a window call that takes the gate (it may first wait for another test's window call to free it)
        var focus = await server.CallToolAsync("focus_window", new Dictionary<string, object?> { ["handle"] = 1706796L }, TimeSpan.FromSeconds(40));

        // Assert
        focus.IsError.Should().NotBe(true);
        wait.IsCompleted.Should().BeFalse("the wait is still running while focus_window ran");
        release.TrySetResult();
        (await wait).IsError.Should().NotBe(true);
    }
}
