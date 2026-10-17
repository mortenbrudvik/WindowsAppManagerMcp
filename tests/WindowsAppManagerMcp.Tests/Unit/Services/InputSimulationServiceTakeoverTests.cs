namespace WindowsAppManagerMcp.Tests.Unit.Services;

/// <summary>
/// Takeover detection in type_text and drag (roadmap M7.4): with mode stop the user's input stops the call between two
/// batches with UserInterrupted and what it did so far; with warn the call goes on and its result has a warning; a pause
/// wins over a takeover. The monitor is <see cref="StubTakeoverMonitor"/>, whose watch the test fires itself, and the native
/// input layer <see cref="FakeInputWrapper"/>, so nothing reaches the desktop.
/// </summary>
public class InputSimulationServiceTakeoverTests
{
    private readonly FakeInputWrapper _input = new();
    private readonly Mock<IInputTargetGuard> _guard = TestDataFactory.CreateAllowingInputGuard();
    private readonly Mock<IMonitorService> _monitors = TestDataFactory.CreateMockMonitorService(
    [
        TestDataFactory.CreateMonitorInfo(boundsX: 0, boundsY: 0, boundsWidth: 1920, boundsHeight: 1080)
    ]);

    private readonly InputSession _session;

    public InputSimulationServiceTakeoverTests()
    {
        _session = new InputSession(_input);
    }

    private InputSimulationService CreateSut(ITakeoverMonitor? takeover) =>
        new(_monitors.Object, _input, _guard.Object, _session, takeover: takeover);

    /// <summary>
    /// Fires the call's watch while batch number <paramref name="batches"/> goes in, as the user's input would: that batch is
    /// sent, the next one is not.
    /// </summary>
    private void UserActsAfter(int batches, StubTakeoverMonitor monitor, bool alsoPause = false)
    {
        var sent = 0;
        _input.BeforeSend = _ =>
        {
            if (++sent != batches)
            {
                return;
            }

            monitor.Last!.Signal();
            if (alsoPause)
            {
                _session.Pause(InputControlSources.Hotkey);
            }
        };
    }

    private void VerifyNothingIsDown()
    {
        _session.ServerHeld.Should().BeEmpty();
        foreach (var key in new ushort[] { 0x01, 0x02, 0x11 })
        {
            _input.IsKeyDown(key).Should().BeFalse($"key 0x{key:X2} must not stay down");
        }
    }

    [Fact]
    public async Task TypeText_ModeStop_TheUsersInput_StopsTypingBetweenBatches_WithWhatWasTyped()
    {
        // Arrange - the user starts typing while the second batch of 64 characters goes in
        var monitor = new StubTakeoverMonitor(TakeoverMode.Stop);
        UserActsAfter(2, monitor);

        // Act
        var result = await CreateSut(monitor).TypeTextAsync(new string('x', 200));

        // Assert
        result.Success.Should().BeFalse();
        result.ErrorCode.Should().Be(InputErrorCode.UserInterrupted);
        result.CharactersTyped.Should().Be(128);
        result.Error.Should().StartWith("Typing stopped after 128 of 200 characters").And.Contain("WAM_TAKEOVER_DETECTION=stop");
        _input.Batches.Should().HaveCount(2);
        monitor.Watches.Should().Be(1);
        monitor.Last!.Disposed.Should().BeTrue("the watch ends with the call");
        VerifyNothingIsDown();
    }

    [Fact]
    public async Task TypeText_ModeWarn_TheUsersInput_LetsTypingFinish_WithAWarning()
    {
        // Arrange
        var monitor = new StubTakeoverMonitor(TakeoverMode.Warn);
        UserActsAfter(1, monitor);

        // Act
        var result = await CreateSut(monitor).TypeTextAsync(new string('x', 200));

        // Assert
        result.Success.Should().BeTrue();
        result.CharactersTyped.Should().Be(200);
        result.Warning.Should().Contain("WAM_TAKEOVER_DETECTION=warn");
    }

    [Fact]
    public async Task TypeText_WithoutTheUsersInput_HasNoWarning()
    {
        var monitor = new StubTakeoverMonitor(TakeoverMode.Warn);

        var result = await CreateSut(monitor).TypeTextAsync(new string('x', 200));

        result.Success.Should().BeTrue();
        result.Warning.Should().BeNull();
        monitor.Watches.Should().Be(1);
    }

    [Fact]
    public async Task TypeText_ModeOff_StartsNoWatch()
    {
        var monitor = new StubTakeoverMonitor(TakeoverMode.Off);

        var result = await CreateSut(monitor).TypeTextAsync("hello");

        result.Success.Should().BeTrue();
        monitor.Watches.Should().Be(0);
    }

    [Fact]
    public async Task TypeText_APauseWithTheUsersInput_IsPausedByUser()
    {
        // Arrange - pressing the pause hotkey is the user's input too: the pause wins
        var monitor = new StubTakeoverMonitor(TakeoverMode.Stop);
        UserActsAfter(1, monitor, alsoPause: true);

        // Act
        var result = await CreateSut(monitor).TypeTextAsync(new string('x', 200));

        // Assert
        result.ErrorCode.Should().Be(InputErrorCode.PausedByUser);
        result.CharactersTyped.Should().Be(64);
    }

    [Fact]
    public async Task Drag_ModeStop_TheUsersInputAfterThePress_StopsIt_AndReleasesTheButtonBackAtTheFromPoint()
    {
        // Arrange - the user grabs the mouse right after the press; a release where the user moved the pointer would drop there
        // unchecked, so the drag ends as a refused drop does: back at the from point
        var monitor = new StubTakeoverMonitor(TakeoverMode.Stop);
        UserActsAfter(1, monitor);
        var request = new DragRequest(100, 100, 600, 100, MouseButton.Primary, [], 10, 0, 0);

        // Act
        var result = await CreateSut(monitor).DragAsync(request, InputTarget.None, InputTarget.None, false, CancellationToken.None);

        // Assert
        result.Success.Should().BeFalse();
        result.ErrorCode.Should().Be(InputErrorCode.UserInterrupted);
        result.Pressed.Should().BeTrue();
        result.Error.Should().Contain("went back to the from point (100, 100)").And.Contain("WAM_TAKEOVER_DETECTION=stop");
        _input.Batches.Count.Should().BeLessThan(10, "the moves stopped at the user's input");
        VerifyNothingIsDown();
    }

    [Fact]
    public async Task Drag_ModeWarn_TheUsersInput_LetsItDrop_WithAWarning()
    {
        // Arrange
        var monitor = new StubTakeoverMonitor(TakeoverMode.Warn);
        UserActsAfter(1, monitor);
        var request = new DragRequest(100, 100, 600, 100, MouseButton.Primary, [], 4, 0, 0);

        // Act
        var result = await CreateSut(monitor).DragAsync(request, InputTarget.None, InputTarget.None, false, CancellationToken.None);

        // Assert
        result.Success.Should().BeTrue();
        result.Warning.Should().Contain("WAM_TAKEOVER_DETECTION=warn").And.Contain("the drag ran");
        VerifyNothingIsDown();
    }
}
