using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using WindowsAppManagerMcp.Infrastructure;

namespace WindowsAppManagerMcp.Tests.Contract;

/// <summary>
/// The call-tool filter that runs window-changing tools inside the input gate, so that a parallel
/// focus_window, close_window or launch_application cannot change which window is in front between an
/// input call's guard check and its input. The tools are fake handlers; nothing touches the desktop.
/// </summary>
public class InputGateToolFilterTests
{
    private static readonly TimeSpan Blocked = TimeSpan.FromMilliseconds(150);
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    [Fact]
    public void EveryServedTool_IsInExactlyOneList()
    {
        foreach (var tool in ServerToolCatalog.ServedToolNames)
        {
            var lists = new[]
            {
                InputGateToolFilter.InputTools.Contains(tool),
                InputGateToolFilter.WindowChangingTools.Contains(tool),
                InputGateToolFilter.ReadOnlyTools.Contains(tool),
                InputGateToolFilter.ControlTools.Contains(tool),
                InputGateToolFilter.SelfGatedTools.Contains(tool)
            };
            lists.Count(inList => inList).Should().Be(1,
                $"'{tool}' must be classified as an input tool, a window-changing tool, a read-only tool, a control tool or a " +
                "tool that takes the gate itself after asking the user");
        }
    }

    [Fact]
    public void TheLists_NameOnlyServedTools()
    {
        InputGateToolFilter.InputTools
            .Concat(InputGateToolFilter.WindowChangingTools)
            .Concat(InputGateToolFilter.ReadOnlyTools)
            .Concat(InputGateToolFilter.ControlTools)
            .Concat(InputGateToolFilter.SelfGatedTools)
            .Should().BeSubsetOf(ServerToolCatalog.ServedToolNames);
    }

    [Theory]
    [InlineData("kill_process")]
    [InlineData("close_windows_batch")]
    [InlineData("close_application")]
    [InlineData("close_debug_browser")] // asks the user, then takes the gate for Browser.close and an end (roadmap M5.4)
    [InlineData("close_window")] // sends inside the gate it takes itself, and waits for the window outside it (roadmap M2.5)
    [InlineData("focus_window")] // resolves its window selector before it takes the gate itself (roadmap M2.4)
    [InlineData("set_window_bounds")]
    [InlineData("set_window_state")]
    [InlineData("set_windows_bounds_batch")]
    [InlineData("launch_application")] // asks about an elevation Windows would not ask about, then takes the gate (roadmap M6.5)
    public async Task AToolThatAsksTheUser_IsNotHeldInTheGateByTheFilter(string tool)
    {
        // Arrange - it takes the gate itself, after its question (UserConfirmationContractTests)
        var ran = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var handler = InputGateToolFilter.Filter((_, _) =>
        {
            ran.TrySetResult();
            return ValueTask.FromResult(new CallToolResult());
        });
        using var gate = InputGate.Enter();

        // Act
        await handler(CreateRequest(tool), CancellationToken.None).AsTask().WaitAsync(Timeout);

        // Assert
        ran.Task.IsCompleted.Should().BeTrue($"{tool} runs at once: the filter does not wait for the gate for it");
    }

    [Theory]
    [InlineData("launch_browser_with_debug")]
    [InlineData("set_clipboard")]
    [InlineData("launch_applications_batch")]
    [InlineData("a_tool_nobody_classified")] // fails safe
    public async Task WindowChangingTool_WaitsWhileAnInputCallHoldsTheGate(string tool)
    {
        // Arrange - an input call is between its guard check and its input
        var ran = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var handler = InputGateToolFilter.Filter((_, _) =>
        {
            ran.TrySetResult();
            return ValueTask.FromResult(new CallToolResult());
        });
        // using: the process-wide gate is released even when the test fails before the Dispose below (a second Dispose
        // releases nothing), so a failure here cannot leave every other class's input and window calls waiting for it
        using var gate = InputGate.Enter();

        // Act
        var call = handler(CreateRequest(tool), CancellationToken.None).AsTask();
        var ranWhileHeld = await Task.WhenAny(ran.Task, Task.Delay(Blocked)) == ran.Task;
        gate.Dispose();
        await call.WaitAsync(Timeout);

        // Assert
        ranWhileHeld.Should().BeFalse($"{tool} must not run while the input gate is held");
        ran.Task.IsCompleted.Should().BeTrue();
    }

    [Theory]
    [InlineData("find_windows")]
    [InlineData("take_screenshot")]
    [InlineData("get_ui_elements")]
    [InlineData("type_text")] // takes the gate itself; taking it here too would deadlock
    [InlineData("mouse_click")]
    [InlineData("get_control_status")]
    [InlineData("resume_input")] // waits for the user's answer, and must not hold up other tools meanwhile
    [InlineData("release_all_input")] // a release must never be queued behind a long input call
    public async Task ReadOnlyAndInputTools_DoNotWaitForTheGate(string tool)
    {
        // Arrange
        var handler = InputGateToolFilter.Filter((_, _) => ValueTask.FromResult(new CallToolResult()));
        using var gate = InputGate.Enter();

        // Act
        var call = handler(CreateRequest(tool), CancellationToken.None).AsTask();

        // Assert
        (await Task.WhenAny(call, Task.Delay(Timeout)) == call).Should().BeTrue($"{tool} must not wait for the gate");
    }

    [Fact]
    public async Task WindowChangingTool_HoldsTheGateUntilItReturns()
    {
        // Arrange
        var flow = InputGate.TagFlow();
        var heldDuringTheCall = false;
        var handler = InputGateToolFilter.Filter((_, _) =>
        {
            heldDuringTheCall = InputGate.IsHeldBy(flow);
            return ValueTask.FromResult(new CallToolResult());
        });

        // Act
        await handler(CreateRequest("launch_applications_batch"), CancellationToken.None);

        // Assert - and the gate was released again: an input call can take it
        heldDuringTheCall.Should().BeTrue();
        var next = Task.Run(() => InputGate.Enter().Dispose());
        (await Task.WhenAny(next, Task.Delay(Timeout)) == next).Should().BeTrue();
    }

    [Fact]
    public async Task WindowChangingTool_ThatThrows_ReleasesTheGate()
    {
        var handler = InputGateToolFilter.Filter((_, _) => throw new InvalidOperationException("tool failed"));

        var call = async () => await handler(CreateRequest("launch_applications_batch"), CancellationToken.None);

        await call.Should().ThrowAsync<InvalidOperationException>();
        var next = Task.Run(() => InputGate.Enter().Dispose());
        (await Task.WhenAny(next, Task.Delay(Timeout)) == next).Should().BeTrue();
    }

    [Fact]
    public async Task WindowChangingTool_CancelledWhileWaiting_IsCancelled()
    {
        // Arrange
        var ran = false;
        var handler = InputGateToolFilter.Filter((_, _) =>
        {
            ran = true;
            return ValueTask.FromResult(new CallToolResult());
        });
        using var cancellation = new CancellationTokenSource();
        using (InputGate.Enter())
        {
            // Act
            var call = handler(CreateRequest("launch_applications_batch"), cancellation.Token).AsTask();
            cancellation.Cancel();

            // Assert
            var act = async () => await call.WaitAsync(Timeout);
            await act.Should().ThrowAsync<OperationCanceledException>();
        }

        ran.Should().BeFalse();
    }

    [Theory]
    [InlineData("set_clipboard")]
    [InlineData("launch_browser_with_debug")]
    [InlineData("launch_applications_batch")]
    [InlineData("a_tool_nobody_classified")] // fails safe
    public async Task WhilePaused_AWindowChangingTool_ReturnsPausedByUser_AndDoesNotRun(string tool)
    {
        // Arrange - the user pressed the pause hotkey
        var session = new InputSession(new FakeInputWrapper());
        session.Pause(InputControlSources.Hotkey);
        var ran = false;
        var handler = InputGateToolFilter.Filter((_, _) =>
        {
            ran = true;
            return ValueTask.FromResult(new CallToolResult());
        });

        // Act
        var result = await handler(CreateRequest(tool, session), CancellationToken.None);

        // Assert
        ran.Should().BeFalse();
        result.IsError.Should().BeTrue();
        var payload = System.Text.Json.JsonDocument.Parse(result.Content.OfType<TextContentBlock>().Single().Text).RootElement;
        payload.GetProperty("errorCode").GetString().Should().Be("PausedByUser");
        payload.GetProperty("error").GetString().Should().Contain("Ctrl+Alt+Shift+Pause").And.Contain("Do not try to resume");
    }

    [Theory]
    [InlineData("take_screenshot")]
    [InlineData("find_windows")]
    [InlineData("get_control_status")]
    public async Task WhilePaused_AReadOnlyTool_StillRuns(string tool)
    {
        var session = new InputSession(new FakeInputWrapper());
        session.Pause(InputControlSources.Hotkey);
        var ran = false;
        var handler = InputGateToolFilter.Filter((_, _) =>
        {
            ran = true;
            return ValueTask.FromResult(new CallToolResult());
        });

        await handler(CreateRequest(tool, session), CancellationToken.None);

        ran.Should().BeTrue("the agent can still look while input is paused");
    }

    [Fact]
    public async Task APauseWhileWaitingForTheGate_StopsTheWindowChangingTool()
    {
        // Arrange - an input call holds the gate, so launch_applications_batch (not paused yet) waits for it
        var session = new InputSession(new FakeInputWrapper());
        var ran = false;
        var handler = InputGateToolFilter.Filter((_, _) =>
        {
            ran = true;
            return ValueTask.FromResult(new CallToolResult());
        });
        using var gate = InputGate.Enter(); // released even when the test fails first; a second Dispose releases nothing
        var call = handler(CreateRequest("launch_applications_batch", session), CancellationToken.None).AsTask();

        // Act - the user pauses meanwhile, then the input call ends
        session.Pause(InputControlSources.Hotkey);
        gate.Dispose();
        var result = await call.WaitAsync(Timeout);

        // Assert
        ran.Should().BeFalse();
        result.IsError.Should().BeTrue();
    }

    [Fact]
    public async Task AWindowChangingTool_GivesUpWaitingForTheGate_WithInputBusy()
    {
        // Arrange - another call holds the gate and does not let go
        var ran = false;
        var handler = InputGateToolFilter.CreateFilter(TimeSpan.FromMilliseconds(100))((_, _) =>
        {
            ran = true;
            return ValueTask.FromResult(new CallToolResult());
        });
        using var gate = InputGate.Enter();

        // Act
        var result = await handler(CreateRequest("launch_applications_batch"), CancellationToken.None).AsTask().WaitAsync(Timeout);

        // Assert
        ran.Should().BeFalse();
        result.IsError.Should().BeTrue();
        var payload = System.Text.Json.JsonDocument.Parse(result.Content.OfType<TextContentBlock>().Single().Text).RootElement;
        payload.GetProperty("errorCode").GetString().Should().Be("InputBusy");
        payload.GetProperty("error").GetString().Should().Contain("Nothing was done");
    }

    [Fact]
    public async Task APauseWhileWaitingForTheGate_ReturnsPausedByUser_WithoutWaitingForTheGate()
    {
        // Arrange - an input call holds the gate for longer than this test runs
        var session = new InputSession(new FakeInputWrapper());
        var handler = InputGateToolFilter.Filter((_, _) => ValueTask.FromResult(new CallToolResult()));
        using var gate = InputGate.Enter();
        var call = handler(CreateRequest("launch_applications_batch", session), CancellationToken.None).AsTask();

        // Act
        session.Pause(InputControlSources.Hotkey);

        // Assert
        var result = await call.WaitAsync(Timeout);
        InputGate.IsHeld.Should().BeTrue("the call returned while the gate was still held");
        System.Text.Json.JsonDocument.Parse(result.Content.OfType<TextContentBlock>().Single().Text).RootElement
            .GetProperty("errorCode").GetString().Should().Be("PausedByUser");
    }

    [Fact]
    public void AWindowChangingTool_WaitsForTheGateAsLongAsAnInputCallDoes()
    {
        InputGateToolFilter.GateWaitTimeout.Should().Be(InputGate.InputWaitTimeout);
        InputGate.InputWaitTimeout.Should().Be(TimeSpan.FromSeconds(30));
    }

    [Fact]
    public async Task ANameTheServerDoesNotServe_PassesStraightThrough_EvenWhilePaused()
    {
        // Arrange - the server serves focus_window only; the user has paused input and an input call holds the gate
        var session = new InputSession(new FakeInputWrapper());
        session.Pause(InputControlSources.Hotkey);
        var reached = false;
        var handler = InputGateToolFilter.Filter((_, _) =>
        {
            reached = true; // the SDK's handler, which reports the unknown name as a protocol error
            return ValueTask.FromResult(new CallToolResult());
        });
        using var gate = InputGate.Enter();

        // Act
        var call = handler(CreateRequest("focus_windwo", session, servedTools: ["focus_window"]), CancellationToken.None).AsTask();

        // Assert
        (await Task.WhenAny(call, Task.Delay(Timeout)) == call).Should().BeTrue("an unknown name must not wait for the gate");
        (await call).IsError.Should().NotBe(true);
        reached.Should().BeTrue();
    }

    [Fact]
    public async Task AServedToolOnNoList_StillRunsInsideTheGate()
    {
        var session = new InputSession(new FakeInputWrapper());
        session.Pause(InputControlSources.Hotkey);
        var handler = InputGateToolFilter.Filter((_, _) => ValueTask.FromResult(new CallToolResult()));

        var result = await handler(CreateRequest("a_tool_nobody_classified", session, servedTools: ["a_tool_nobody_classified"]), CancellationToken.None);

        result.IsError.Should().BeTrue("a served tool nobody classified fails safe");
    }

    [Fact]
    public void AddInputGateSerialization_RegistersTheFilter()
    {
        var services = new ServiceCollection();

        services.AddMcpServer().AddInputGateSerialization();

        using var provider = services.BuildServiceProvider();
        provider.GetRequiredService<IOptions<McpServerOptions>>().Value.Filters.Request.CallToolFilters
            .Should().ContainSingle();
    }

    /// <summary>
    /// A call of <paramref name="tool"/>. With <paramref name="servedTools"/> the server serves those tools; without,
    /// its tool collection is unknown (null), as for a server the filter cannot ask.
    /// </summary>
    private static RequestContext<CallToolRequestParams> CreateRequest(
        string tool, IInputSession? session = null, IReadOnlyList<string>? servedTools = null)
    {
        var server = new Mock<McpServer>();
        if (servedTools != null)
        {
            var tools = new McpServerPrimitiveCollection<McpServerTool>();
            foreach (var name in servedTools)
            {
                tools.Add(McpServerTool.Create(() => "ok", new McpServerToolCreateOptions { Name = name }));
            }

            server.Setup(s => s.ServerOptions).Returns(new McpServerOptions { ToolCollection = tools });
        }

        return new(server.Object, new JsonRpcRequest { Method = RequestMethods.ToolsCall }, new CallToolRequestParams { Name = tool })
        {
            Services = session == null ? null : new ServiceCollection().AddSingleton(session).BuildServiceProvider()
        };
    }
}
