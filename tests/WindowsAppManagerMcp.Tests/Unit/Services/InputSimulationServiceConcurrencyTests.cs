namespace WindowsAppManagerMcp.Tests.Unit.Services;

/// <summary>
/// Unit tests that concurrent input calls are serialized, so one call's input never
/// lands in the middle of another's. The native input wrapper is faked, so no real
/// mouse or keyboard input reaches the desktop.
/// </summary>
public class InputSimulationServiceConcurrencyTests
{
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(10);

    // Longer than one type_text batch (32 characters), so each call sends several batches
    private static readonly string As = new('a', 40);
    private static readonly string Bs = new('b', 40);

    private readonly Mock<IMonitorService> _mockMonitorService;

    public InputSimulationServiceConcurrencyTests()
    {
        _mockMonitorService = TestDataFactory.CreateMockMonitorService(new List<MonitorInfo>
        {
            TestDataFactory.CreateMonitorInfo(boundsX: 0, boundsY: 0, boundsWidth: 1920, boundsHeight: 1080)
        });
    }

    private InputSimulationService CreateSut(INativeInputWrapper wrapper) =>
        new(_mockMonitorService.Object, wrapper, TestDataFactory.CreateAllowingInputGuard().Object, new InputSession(wrapper));

    /// <summary>
    /// Starts the first call, waits until it is inside the native layer, then starts the second call.
    /// The fake holds the first call's first native operation open until the second call sends input
    /// (or a short timeout passes), so any interleaving shows up in the recorded operations.
    /// </summary>
    private static async Task RunOverlapping(HoldingInputWrapper wrapper, Func<Task> first, Func<Task> second)
    {
        var firstTask = Task.Run(first);
        wrapper.FirstOperationStarted.Wait(TestTimeout).Should().BeTrue("the first call should reach the native layer");

        var secondTask = Task.Run(second);
        await Task.WhenAll(firstTask, secondTask).WaitAsync(TestTimeout);
    }

    [Fact]
    public async Task TypeText_ConcurrentCalls_DoNotInterleaveCharacters()
    {
        // Arrange
        var wrapper = new HoldingInputWrapper();
        var sut = CreateSut(wrapper);

        // Act
        await RunOverlapping(wrapper, () => sut.TypeTextAsync(As), () => sut.TypeTextAsync(Bs));

        // Assert
        string.Concat(wrapper.Operations).Should().Be(As + Bs);
    }

    [Fact]
    public async Task SendKeys_DuringTypeText_WaitsUntilTypingFinishes()
    {
        // Arrange
        var wrapper = new HoldingInputWrapper();
        var sut = CreateSut(wrapper);

        // Act
        await RunOverlapping(wrapper, () => sut.TypeTextAsync(As), () => sut.SendKeysAsync("^c"));

        // Assert - the text goes in batches of up to 32 characters, and the keys come after all of them
        wrapper.Operations.Should().Equal(As[..32], As[32..], "keys");
    }

    [Fact]
    public async Task MoveMouse_DuringClick_DoesNotMoveCursorBetweenClickMoveAndPress()
    {
        // Arrange
        var wrapper = new HoldingInputWrapper();
        var sut = CreateSut(wrapper);

        // Act
        await RunOverlapping(wrapper, () => Task.FromResult(sut.Click(100, 100)), () => Task.FromResult(sut.MoveMouse(500, 500)));

        // Assert - the click lands where it was aimed
        wrapper.Operations.Should().Equal("move 100,100", "mouse", "move 500,500");
    }

    [Fact]
    public async Task FocusWindow_DuringTypeText_WaitsUntilTypingFinishes()
    {
        // Arrange - focus_window as the server runs it: through the input gate filter
        var wrapper = new HoldingInputWrapper();
        var sut = CreateSut(wrapper);
        var operationsWhenFocusRan = -1;
        var focusWindow = WindowsAppManagerMcp.Infrastructure.InputGateToolFilter.Filter((_, _) =>
        {
            operationsWhenFocusRan = wrapper.Operations.Count;
            return ValueTask.FromResult(new ModelContextProtocol.Protocol.CallToolResult());
        });
        var request = new ModelContextProtocol.Server.RequestContext<ModelContextProtocol.Protocol.CallToolRequestParams>(
            new Mock<ModelContextProtocol.Server.McpServer>().Object,
            new ModelContextProtocol.Protocol.JsonRpcRequest { Method = ModelContextProtocol.Protocol.RequestMethods.ToolsCall },
            new ModelContextProtocol.Protocol.CallToolRequestParams { Name = "focus_window" });

        // Act - the agent races focus_window (its own terminal) against type_text
        await RunOverlapping(wrapper, () => sut.TypeTextAsync(As), () => focusWindow(request, CancellationToken.None).AsTask());

        // Assert - it could not change the foreground between the guard's check and the typed text
        operationsWhenFocusRan.Should().Be(2, "both batches of the text were sent before focus_window ran");
    }

    [Fact]
    public async Task Calls_OnSeparateServiceInstances_AreSerializedProcessWide()
    {
        // Arrange
        var wrapper = new HoldingInputWrapper();
        var first = CreateSut(wrapper);
        var second = CreateSut(wrapper);

        // Act
        await RunOverlapping(wrapper, () => first.TypeTextAsync(As), () => second.TypeTextAsync(Bs));

        // Assert
        string.Concat(wrapper.Operations).Should().Be(As + Bs);
    }

    [Fact]
    public async Task TypeText_WhenNativeLayerThrows_ReleasesGateForLaterCalls()
    {
        // Arrange
        var failing = new Mock<INativeInputWrapper>();
        failing.Setup(w => w.SendInput(It.IsAny<IReadOnlyList<InputEvent>>()))
            .Throws(new InvalidOperationException("native failure"));
        var wrapper = new HoldingInputWrapper();

        // Act
        var failingCall = () => CreateSut(failing.Object).TypeTextAsync("x");
        await failingCall.Should().ThrowAsync<InvalidOperationException>();

        var laterCall = Task.Run(() => CreateSut(wrapper).TypeTextAsync("ok"));

        // Assert
        var result = await laterCall.WaitAsync(TestTimeout);
        result.Success.Should().BeTrue();
    }

    /// <summary>
    /// Records native input operations in order. The first operation is held open until
    /// another thread sends input or a short timeout passes.
    /// </summary>
    private sealed class HoldingInputWrapper : INativeInputWrapper
    {
        private static readonly TimeSpan HoldFirstOperation = TimeSpan.FromMilliseconds(300);

        private readonly object _lock = new();
        private readonly List<string> _operations = new();
        private readonly ManualResetEventSlim _otherThreadSentInput = new();
        private int _firstThreadId = -1;
        private (int X, int Y) _cursor;

        public ManualResetEventSlim FirstOperationStarted { get; } = new();

        public IReadOnlyList<string> Operations
        {
            get
            {
                lock (_lock)
                {
                    return _operations.ToList();
                }
            }
        }

        public bool SetCursorPos(int x, int y)
        {
            lock (_lock)
            {
                _cursor = (x, y);
            }

            Record($"move {x},{y}");
            return true;
        }

        public (int X, int Y)? GetCursorPos()
        {
            lock (_lock)
            {
                return _cursor;
            }
        }

        public uint SendInput(IReadOnlyList<InputEvent> events)
        {
            Record(Describe(events));
            return (uint)events.Count;
        }

        public bool IsKeyDown(ushort virtualKey) => false;

        private static string Describe(IReadOnlyList<InputEvent> events)
        {
            if (events.All(e => e.Kind == InputEventKind.Unicode))
            {
                // The characters of the batch, once each (their key-down events)
                return string.Concat(events.Where((_, i) => i % 2 == 0).Select(e => (char)e.Code));
            }

            return events.All(e => e.Kind == InputEventKind.Mouse) ? "mouse" : "keys";
        }

        private void Record(string operation)
        {
            bool isFirstOperation;
            lock (_lock)
            {
                _operations.Add(operation);
                isFirstOperation = _firstThreadId == -1;
                if (isFirstOperation)
                {
                    _firstThreadId = Environment.CurrentManagedThreadId;
                }
                else if (Environment.CurrentManagedThreadId != _firstThreadId)
                {
                    _otherThreadSentInput.Set();
                }
            }

            if (isFirstOperation)
            {
                FirstOperationStarted.Set();
                _otherThreadSentInput.Wait(HoldFirstOperation);
            }
        }
    }
}
