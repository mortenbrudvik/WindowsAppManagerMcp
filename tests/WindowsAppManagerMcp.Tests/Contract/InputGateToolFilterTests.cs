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
                InputGateToolFilter.ReadOnlyTools.Contains(tool)
            };
            lists.Count(inList => inList).Should().Be(1,
                $"'{tool}' must be classified as an input tool, a window-changing tool or a read-only tool");
        }
    }

    [Fact]
    public void TheLists_NameOnlyServedTools()
    {
        InputGateToolFilter.InputTools
            .Concat(InputGateToolFilter.WindowChangingTools)
            .Concat(InputGateToolFilter.ReadOnlyTools)
            .Should().BeSubsetOf(ServerToolCatalog.ServedToolNames);
    }

    [Theory]
    [InlineData("focus_window")]
    [InlineData("close_window")]
    [InlineData("launch_application")]
    [InlineData("apply_layout")]
    [InlineData("set_window_state")]
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
        var gate = InputGate.Enter();

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
    [InlineData("click")]
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
        var heldDuringTheCall = false;
        var handler = InputGateToolFilter.Filter((_, _) =>
        {
            heldDuringTheCall = InputGate.IsHeld;
            return ValueTask.FromResult(new CallToolResult());
        });

        // Act
        await handler(CreateRequest("focus_window"), CancellationToken.None);

        // Assert - and the gate was released again: an input call can take it
        heldDuringTheCall.Should().BeTrue();
        var next = Task.Run(() => InputGate.Enter().Dispose());
        (await Task.WhenAny(next, Task.Delay(Timeout)) == next).Should().BeTrue();
    }

    [Fact]
    public async Task WindowChangingTool_ThatThrows_ReleasesTheGate()
    {
        var handler = InputGateToolFilter.Filter((_, _) => throw new InvalidOperationException("tool failed"));

        var call = async () => await handler(CreateRequest("close_window"), CancellationToken.None);

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
            var call = handler(CreateRequest("focus_window"), cancellation.Token).AsTask();
            cancellation.Cancel();

            // Assert
            var act = async () => await call.WaitAsync(Timeout);
            await act.Should().ThrowAsync<OperationCanceledException>();
        }

        ran.Should().BeFalse();
    }

    [Fact]
    public void AddInputGateSerialization_RegistersTheFilter()
    {
        var services = new ServiceCollection();

        services.AddMcpServer().AddInputGateSerialization();

        using var provider = services.BuildServiceProvider();
        provider.GetRequiredService<IOptions<McpServerOptions>>().Value.Filters.CallToolFilters
            .Should().ContainSingle();
    }

    private static RequestContext<CallToolRequestParams> CreateRequest(string tool) =>
        new(new Mock<McpServer>().Object, new JsonRpcRequest { Method = RequestMethods.ToolsCall })
        {
            Params = new CallToolRequestParams { Name = tool }
        };
}
