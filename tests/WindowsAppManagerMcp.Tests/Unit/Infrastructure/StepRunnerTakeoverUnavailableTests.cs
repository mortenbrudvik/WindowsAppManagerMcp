using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol.Protocol;
using WindowsAppManagerMcp.Infrastructure;

namespace WindowsAppManagerMcp.Tests.Unit.Infrastructure;

/// <summary>
/// run_steps while takeover detection (roadmap M7.4) does not work (review F1): the run's steps still run, as with mode off,
/// and its result has a warning that the user's input was not watched, as type_text's and drag's have. Whether the run's
/// watch worked is read from the watch itself (<see cref="TakeoverWatch.Unwatched"/>), so a status that still shows an
/// earlier failure blames no run. With a fake dispatcher and <see cref="StubTakeoverMonitor"/>: nothing reaches the desktop.
/// </summary>
public class StepRunnerTakeoverUnavailableTests
{
    private const long Window = 0x1A0B2C;
    private const string ThreadStopped = "The takeover detection thread stopped, so the user's input is no longer detected.";

    private readonly Mock<IInputSession> _session = new();
    private readonly Mock<IWindowService> _windows = new();
    private readonly Mock<IElementRegistry> _elements = new();
    private readonly ClickDispatcher _dispatcher = new();
    private readonly CancellationTokenSource _pause = new();

    public StepRunnerTakeoverUnavailableTests()
    {
        _session.Setup(s => s.IsPaused).Returns(false);
        _session.Setup(s => s.PauseToken).Returns(() => _pause.Token);
        _session.Setup(s => s.ReleaseHeldInput(It.IsAny<string>())).Returns(InputReleaseOutcome.Nothing);
        _windows.Setup(w => w.GetWindowInfo(It.IsAny<nint>())).Returns((nint h) => Info(h));
        _windows.Setup(w => w.GetForegroundWindow()).Returns(() => Info((nint)Window));
    }

    private static WindowInfo? Info(nint handle) => (long)handle == Window
        ? new WindowInfo(handle, "Form", "app", 4_100, new WindowRect(0, 0, 800, 600), WindowState.Normal, true, 0)
        : null;

    private static RunStep Click() => new(StepTool.MouseClick, JsonNode.Parse("""{ "x": 10, "y": 10 }""")!.AsObject(), null, false, false);

    private Task<StepRunOutcome> RunAsync(StubTakeoverMonitor monitor)
    {
        var runner = new StepRunner(_session.Object, _windows.Object, _elements.Object, takeover: monitor);
        return runner.RunAsync((nint)Window, runner.Check([Click(), Click()], _dispatcher), _dispatcher, TimeSpan.FromSeconds(30),
            TimeSpan.Zero, null, CancellationToken.None);
    }

    [Theory]
    [InlineData(TakeoverMode.Stop, "stop")]
    [InlineData(TakeoverMode.Warn, "warn")]
    public async Task ARun_WhileDetectionIsUnavailable_RunsItsSteps_WithAWarningThatSaysSo(TakeoverMode mode, string name)
    {
        // Arrange - the monitor thread is not started again, so no hook covers the run
        var monitor = new StubTakeoverMonitor(mode) { State = TakeoverState.Unavailable, Error = ThreadStopped, UnwatchedBecause = ThreadStopped };

        // Act
        var run = await RunAsync(monitor);

        // Assert
        run.Result.Success.Should().BeTrue("detection that does not work runs the steps as with off");
        _dispatcher.Calls.Should().Be(2);
        run.Result.Warning.Should().Contain($"WAM_TAKEOVER_DETECTION={name}").And.Contain("did not work while the steps ran")
            .And.Contain("not watched").And.Contain(ThreadStopped);
        monitor.Last!.Disposed.Should().BeTrue("the watch ends with the run");
    }

    [Fact]
    public async Task ARun_DetectionThatStopsWorkingDuringIt_IsWarnedAbout()
    {
        // Arrange - the monitor thread dies while step 1 runs
        var monitor = new StubTakeoverMonitor(TakeoverMode.Stop);
        _dispatcher.OnCall = number =>
        {
            if (number == 1)
            {
                monitor.StopWorking(ThreadStopped);
            }
        };

        // Act
        var run = await RunAsync(monitor);

        // Assert
        run.Result.Success.Should().BeTrue();
        run.Result.Warning.Should().Contain("did not work while the steps ran").And.Contain(ThreadStopped);
    }

    [Fact]
    public async Task ARun_AStatusStillUnavailableFromAnEarlierOperation_WhileTheRunIsWatched_HasNoWarning()
    {
        var monitor = new StubTakeoverMonitor(TakeoverMode.Stop) { State = TakeoverState.Unavailable, Error = ThreadStopped };

        var run = await RunAsync(monitor);

        run.Result.Success.Should().BeTrue();
        run.Result.Warning.Should().BeNull();
    }

    [Fact]
    public async Task ARun_ModeWarn_TheUsersInputSeen_AfterDetectionFailedForPartOfIt_HasOnlyTheTakeoverWarning()
    {
        // Arrange - detection fails in step 1, and still sees the user in step 2
        var monitor = new StubTakeoverMonitor(TakeoverMode.Warn);
        _dispatcher.OnCall = number =>
        {
            if (number == 1)
            {
                monitor.StopWorking(ThreadStopped);
            }
            else
            {
                monitor.Last!.Signal();
            }
        };

        // Act
        var run = await RunAsync(monitor);

        // Assert
        run.Result.Success.Should().BeTrue();
        run.Result.Warning.Should().Contain("WAM_TAKEOVER_DETECTION=warn").And.Contain("the steps ran").And.NotContain("did not work");
    }

    /// <summary>Serves mouse_click with the parameters its real schema declares, and answers every call with success.</summary>
    private sealed class ClickDispatcher : IStepDispatcher
    {
        private int _calls;

        public int Calls => Volatile.Read(ref _calls);

        public Action<int>? OnCall { get; set; }

        public JsonElement? InputSchemaOf(string tool)
        {
            if (tool != "mouse_click")
            {
                return null;
            }

            var properties = new JsonObject();
            foreach (var name in new[] { "x", "y", "windowHandle", "window", "captureId", "allowHostTarget" })
            {
                properties[name] = new JsonObject
                {
                    ["type"] = name is "window" ? "object" : name is "x" or "y" or "windowHandle" ? "integer" : "string"
                };
            }

            return JsonSerializer.SerializeToElement(new JsonObject { ["type"] = "object", ["properties"] = properties });
        }

        public Task<CallToolResult> InvokeAsync(string tool, JsonObject arguments, int stepNumber, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _calls);
            OnCall?.Invoke(stepNumber);
            return Task.FromResult(new CallToolResult { Content = [new TextContentBlock { Text = """{ "success": true }""" }] });
        }
    }
}
