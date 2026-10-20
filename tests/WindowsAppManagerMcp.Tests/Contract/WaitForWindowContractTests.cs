using System.Text.Json;
using ModelContextProtocol.Protocol;
using WindowsAppManagerMcp.Infrastructure;

namespace WindowsAppManagerMcp.Tests.Contract;

/// <summary>
/// Contract: wait_for_window (roadmap M3.7) through the production tool and filter registrations, with mocked services
/// (<see cref="ContractServices"/>). The selector and condition bind from their JSON names, a timeout reaches the client as
/// isError with the result body (timedOut, elapsedMs, errorCode WaitTimedOut), and a wait in flight does not hold the input
/// gate, so window and input calls go ahead meanwhile.
/// </summary>
public class WaitForWindowContractTests
{
    private static Task<InProcessMcpServer> StartAsync(ContractServices services) =>
        InProcessMcpServer.StartAsync([], builder => builder.AddWindowsAppManagerTools(), configureServices: services.Register);

    private static JsonElement Payload(CallToolResult result) =>
        JsonDocument.Parse(result.Content.OfType<TextContentBlock>().Single().Text).RootElement;

    [Fact]
    public async Task TheArguments_BindFromTheirJsonNames()
    {
        // Arrange
        using var services = new ContractServices(ContractMode.Typical);
        WindowConditionRequest? request = null;
        services.WindowWait.Setup(w => w.WaitForConditionAsync(It.IsAny<WindowConditionRequest>(), It.IsAny<IProgress<OperationProgress>?>(), It.IsAny<CancellationToken>()))
            .Callback((WindowConditionRequest r, IProgress<OperationProgress>? _, CancellationToken _) => request = r)
            .ReturnsAsync(new WindowConditionResult { Satisfied = true, Window = TypicalDesktop.Notepad, MatchCount = 1 });
        await using var server = await StartAsync(services);

        // Act
        var result = await server.CallToolAsync("wait_for_window", new Dictionary<string, object?>
        {
            ["window"] = new Dictionary<string, object?> { ["handle"] = 1706796, ["processId"] = 10244 },
            ["condition"] = "title_changed",
            ["previousTitle"] = "*notes.txt - Notepad",
            ["timeoutMs"] = 5000,
            ["stableMs"] = 0
        });

        // Assert
        result.IsError.Should().NotBe(true);
        Payload(result).GetProperty("satisfied").GetBoolean().Should().BeTrue();
        request.Should().BeEquivalentTo(new WindowConditionRequest
        {
            Window = new WindowSelector { Handle = 1706796, ProcessId = 10244 },
            Condition = WindowWaitCondition.TitleChanged,
            PreviousTitle = "*notes.txt - Notepad",
            TimeoutMs = 5000,
            StableMs = 0
        });
    }

    [Fact]
    public async Task TheSetArguments_BindFromTheirJsonNames()
    {
        // Arrange
        using var services = new ContractServices(ContractMode.Typical);
        var requests = new List<WindowConditionRequest>();
        services.WindowWait.Setup(w => w.WaitForConditionAsync(It.IsAny<WindowConditionRequest>(), It.IsAny<IProgress<OperationProgress>?>(), It.IsAny<CancellationToken>()))
            .Callback((WindowConditionRequest r, IProgress<OperationProgress>? _, CancellationToken _) => requests.Add(r))
            .ReturnsAsync(new WindowConditionResult { Satisfied = true, IsSet = true });
        await using var server = await StartAsync(services);
        var edge = new Dictionary<string, object?> { ["processName"] = "msedge" };

        // Act
        var all = await server.CallToolAsync("wait_for_window", new Dictionary<string, object?>
        {
            ["window"] = edge, ["condition"] = "minimized", ["all"] = true
        });
        var count = await server.CallToolAsync("wait_for_window", new Dictionary<string, object?>
        {
            ["window"] = edge, ["count"] = new Dictionary<string, object?> { ["atLeast"] = 2, ["atMost"] = 4 }
        });

        // Assert
        all.IsError.Should().NotBe(true, Payload(all).GetRawText());
        count.IsError.Should().NotBe(true, Payload(count).GetRawText());
        requests.Should().HaveCount(2);
        requests[0].Condition.Should().Be(WindowWaitCondition.Minimized);
        requests[0].All.Should().BeTrue();
        requests[1].Count.Should().Be(new WindowCount { AtLeast = 2, AtMost = 4 });
        Payload(count).GetProperty("matchCount").GetInt32().Should().Be(0);
    }

    [Fact]
    public async Task AMemberCountDoesNotTake_IsInvalidArguments_AndNothingWaits()
    {
        using var services = new ContractServices(ContractMode.Typical);
        await using var server = await StartAsync(services);

        var result = await server.CallToolAsync("wait_for_window", new Dictionary<string, object?>
        {
            ["window"] = new Dictionary<string, object?> { ["processName"] = "msedge" },
            ["count"] = new Dictionary<string, object?> { ["min"] = 2 }
        });

        result.IsError.Should().BeTrue();
        var payload = Payload(result);
        payload.GetProperty("errorCode").GetString().Should().Be(ToolErrorReporting.InvalidArgumentsErrorCode);
        payload.GetProperty("error").GetString().Should().Contain("'min'");
        services.WindowWait.Verify(w => w.WaitForConditionAsync(It.IsAny<WindowConditionRequest>(), It.IsAny<IProgress<OperationProgress>?>(),
            It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ASetResult_ReachesTheClient_WithEachWindowsStateAndPlace()
    {
        // Arrange - the typical desktop's Notepad window (normal) and its Explorer window (minimized)
        using var services = new ContractServices(ContractMode.Typical);
        var notepad = TypicalDesktop.Notepad;
        var explorer = TypicalDesktop.Windows.Single(w => w.ProcessName == "explorer");
        services.WindowWait.Setup(w => w.WaitForConditionAsync(It.IsAny<WindowConditionRequest>(), It.IsAny<IProgress<OperationProgress>?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new WindowConditionResult { Satisfied = true, IsSet = true, MatchCount = 2, Candidates = [notepad, explorer] });
        await using var server = await StartAsync(services);

        // Act
        var result = await server.CallToolAsync("wait_for_window", new Dictionary<string, object?>
        {
            ["window"] = new Dictionary<string, object?> { ["titleRegex"] = "Notepad|Explorer" },
            ["count"] = new Dictionary<string, object?> { ["exactly"] = 2 }
        });

        // Assert
        var payload = Payload(result);
        payload.TryGetProperty("window", out _).Should().BeFalse();
        payload.GetProperty("matchCount").GetInt32().Should().Be(2);
        var candidates = payload.GetProperty("candidates").EnumerateArray().ToList();
        candidates[0].GetProperty("state").GetString().Should().Be("normal");
        candidates[0].GetProperty("visibleBounds").GetProperty("width").GetInt32().Should().Be(notepad.VisibleBounds!.Width);
        candidates[0].GetProperty("monitorIndex").GetInt32().Should().Be(0);
        candidates[0].GetProperty("monitorId").GetString().Should().Be(notepad.MonitorId);
        candidates[1].GetProperty("state").GetString().Should().Be("minimized");
        candidates[1].TryGetProperty("visibleBounds", out _).Should().BeFalse("a minimized window is parked off screen");
        candidates[1].TryGetProperty("zone", out _).Should().BeFalse();
        candidates[1].GetProperty("monitorIndex").GetInt32().Should().Be(explorer.MonitorIndex!.Value);
    }

    [Fact]
    public async Task ATimeout_IsAnError_WithTheResultBody()
    {
        // Arrange - the failing contract mode's wait times out
        using var services = new ContractServices(ContractMode.Failing);
        await using var server = await StartAsync(services);

        // Act
        var result = await server.CallToolAsync("wait_for_window", new Dictionary<string, object?>
        {
            ["window"] = new Dictionary<string, object?> { ["titleEquals"] = "Save As" }
        });

        // Assert
        result.IsError.Should().BeTrue();
        var payload = Payload(result);
        payload.GetProperty("success").GetBoolean().Should().BeFalse();
        payload.GetProperty("satisfied").GetBoolean().Should().BeFalse();
        payload.GetProperty("timedOut").GetBoolean().Should().BeTrue();
        payload.GetProperty("elapsedMs").GetInt64().Should().Be(10_000);
        payload.GetProperty("errorCode").GetString().Should().Be("WaitTimedOut");
    }

    [Fact]
    public async Task AnUnknownCondition_IsInvalidArguments_AndNothingWaits()
    {
        using var services = new ContractServices(ContractMode.Typical);
        await using var server = await StartAsync(services);

        var result = await server.CallToolAsync("wait_for_window", new Dictionary<string, object?>
        {
            ["window"] = new Dictionary<string, object?> { ["titleEquals"] = "Save As" },
            ["condition"] = "appears"
        });

        result.IsError.Should().BeTrue();
        Payload(result).GetProperty("errorCode").GetString().Should().Be(ToolErrorReporting.InvalidArgumentsErrorCode);
        services.WindowWait.Verify(w => w.WaitForConditionAsync(It.IsAny<WindowConditionRequest>(), It.IsAny<IProgress<OperationProgress>?>(),
            It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task AWaitInFlight_DoesNotHoldTheInputGate()
    {
        // Arrange - a wait that runs until the test releases it
        using var services = new ContractServices(ContractMode.Typical);
        var running = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        services.WindowWait.Setup(w => w.WaitForConditionAsync(It.IsAny<WindowConditionRequest>(), It.IsAny<IProgress<OperationProgress>?>(), It.IsAny<CancellationToken>()))
            .Returns(async (WindowConditionRequest _, IProgress<OperationProgress>? _, CancellationToken token) =>
            {
                running.TrySetResult();
                await release.Task.WaitAsync(token);
                return new WindowConditionResult { Satisfied = true, Window = TypicalDesktop.Notepad, MatchCount = 1 };
            });
        await using var server = await StartAsync(services);
        var wait = server.CallToolAsync("wait_for_window", new Dictionary<string, object?>
        {
            ["window"] = new Dictionary<string, object?> { ["processName"] = "notepad" }
        });
        await running.Task.WaitAsync(TimeSpan.FromSeconds(10));

        // Act - a window call that takes the gate (it may first wait for another test's window call to free it)
        var focus = await server.CallToolAsync("focus_window", new Dictionary<string, object?> { ["handle"] = 1706796L }, TimeSpan.FromSeconds(40));

        // Assert
        focus.IsError.Should().NotBe(true);
        Payload(focus).GetProperty("success").GetBoolean().Should().BeTrue();
        wait.IsCompleted.Should().BeFalse("the wait is still running while focus_window ran");
        release.TrySetResult();
        (await wait).IsError.Should().NotBe(true);
    }
}
