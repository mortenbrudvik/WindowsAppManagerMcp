namespace WindowsAppManagerMcp.Tests.Unit.Services;

/// <summary>
/// The outcome of a window change (roadmap M2.2): a refused SetWindowPos reports its Win32 error (5 is AccessDenied),
/// a state change succeeds only when the window is seen in the new state, an out-of-range monitor index is
/// InvalidMonitorIndex with the valid range, and maximize on another monitor moves the window there first. Every
/// native call is a mock.
/// </summary>
public class WindowServiceOutcomeTests
{
    private const int SwMaximize = 3, SwRestore = 9;
    private static readonly nint Window = 0x1001;

    private readonly Mock<INativeWindowWrapper> _native = new();
    private readonly Mock<IMonitorService> _monitors = TestDataFactory.CreateMockMonitorService(
    [
        TestDataFactory.CreateMonitorInfo(index: 0),
        TestDataFactory.CreateMonitorInfo(index: 1, deviceName: @"\\.\DISPLAY2", isPrimary: false, boundsX: 1920, workAreaX: 1920)
    ]);

    public WindowServiceOutcomeTests()
    {
        _native.Setup(n => n.IsWindow(Window)).Returns(true);
        _native.Setup(n => n.IsWindowVisible(Window)).Returns(true);
        _native.Setup(n => n.GetWindowRect(Window)).Returns((100, 100, 900, 700));
        _native.Setup(n => n.SetWindowPos(Window, It.IsAny<int>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<uint>()))
            .Returns(Win32CallResult.Ok);
        _monitors.Setup(m => m.CalculateSnapBounds(It.IsAny<int>(), It.IsAny<SnapPosition>())).Returns(new MonitorRect(0, 0, 960, 1040));
    }

    private WindowService CreateSut() =>
        new(_monitors.Object, _native.Object, Mock.Of<IHostGuard>(), stateChangeTimeout: TimeSpan.Zero);

    public static TheoryData<string, WindowErrorCode> Placements => new()
    {
        { "move", WindowErrorCode.MoveFailed },
        { "resize", WindowErrorCode.ResizeFailed },
        { "bounds", WindowErrorCode.MoveFailed },
        { "snap", WindowErrorCode.MoveFailed },
        { "monitor", WindowErrorCode.MoveFailed },
    };

    private WindowChangeResult Place(string operation) => operation switch
    {
        "move" => CreateSut().MoveWindow(Window, 10, 20),
        "resize" => CreateSut().ResizeWindow(Window, 640, 480),
        "bounds" => CreateSut().SetWindowBounds(Window, 10, 20, 640, 480),
        "snap" => CreateSut().SnapWindow(Window, SnapPosition.LeftHalf, monitorIndex: 0),
        _ => CreateSut().MoveWindowToMonitor(Window, 1, MonitorPositioning.Center)
    };

    #region A refused SetWindowPos reports its Win32 error

    [Theory]
    [MemberData(nameof(Placements))]
    public void APlacementWindowsDeniesAccessTo_IsAccessDenied(string operation, WindowErrorCode _)
    {
        // Arrange - User Interface Privilege Isolation: the window belongs to an app running as administrator
        _native.Setup(n => n.SetWindowPos(Window, It.IsAny<int>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<uint>()))
            .Returns(Win32CallResult.Failure(5));

        // Act
        var result = Place(operation);

        // Assert
        result.Success.Should().BeFalse();
        result.ErrorCode.Should().Be(WindowErrorCode.AccessDenied);
        result.Error.Should().Contain("Win32 error 5").And.Contain("administrator");
    }

    [Theory]
    [MemberData(nameof(Placements))]
    public void APlacementThatFailsOtherwise_ReportsTheOperationsCode_AndTheWin32Error(string operation, WindowErrorCode expected)
    {
        // Arrange - ERROR_INVALID_PARAMETER
        _native.Setup(n => n.SetWindowPos(Window, It.IsAny<int>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<uint>()))
            .Returns(Win32CallResult.Failure(87));

        // Act
        var result = Place(operation);

        // Assert
        result.ErrorCode.Should().Be(expected);
        result.Error.Should().Contain("Win32 error 87");
    }

    [Fact]
    public void AWindowThatClosedDuringThePlacement_IsInvalidHandle()
    {
        _native.Setup(n => n.SetWindowPos(Window, It.IsAny<int>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<uint>()))
            .Returns(Win32CallResult.Failure(1400));

        CreateSut().MoveWindow(Window, 10, 20).ErrorCode.Should().Be(WindowErrorCode.InvalidHandle);
    }

    [Theory]
    [MemberData(nameof(Placements))]
    public void APlacementWindowsMade_IsDone(string operation, WindowErrorCode _)
    {
        Place(operation).Should().Be(WindowChangeResult.Done(Window));
    }

    #endregion

    #region A state change is reported from the state the window is in afterwards

    [Theory]
    [InlineData(WindowState.Normal, WindowStateRequest.Minimize, WindowState.Minimized)]
    [InlineData(WindowState.Normal, WindowStateRequest.Maximize, WindowState.Maximized)]
    [InlineData(WindowState.Maximized, WindowStateRequest.Restore, WindowState.Normal)]
    [InlineData(WindowState.Minimized, WindowStateRequest.Restore, WindowState.Normal)]
    [InlineData(WindowState.Normal, WindowStateRequest.Restore, WindowState.Normal)]
    [InlineData(WindowState.Minimized, WindowStateRequest.Minimize, WindowState.Minimized)]
    public void SetWindowState_WhenTheWindowReachesTheState_Succeeds(WindowState initial, WindowStateRequest request, WindowState expected)
    {
        // Arrange
        var state = TestDataFactory.SimulateShowStates(_native, Window, initial);

        // Act
        var result = CreateSut().SetWindowState(Window, request);

        // Assert
        result.Success.Should().BeTrue(result.Error);
        state().Should().Be(expected);
    }

    [Fact]
    public void Restore_OfAWindowMinimizedFromMaximized_ReturnsItToMaximized_AndSucceeds()
    {
        // Arrange - the Windows Restore command: back to the state before it was minimized
        var state = TestDataFactory.SimulateShowStates(_native, Window, WindowState.Maximized);
        CreateSut().SetWindowState(Window, WindowStateRequest.Minimize);

        // Act
        var result = CreateSut().SetWindowState(Window, WindowStateRequest.Restore);

        // Assert
        result.Success.Should().BeTrue(result.Error);
        state().Should().Be(WindowState.Maximized);
    }

    [Theory]
    [InlineData(WindowStateRequest.Minimize, "minimized", "normal")]
    [InlineData(WindowStateRequest.Maximize, "maximized", "normal")]
    public void SetWindowState_WhenTheWindowDoesNotReachTheState_IsStateChangeFailed(WindowStateRequest request, string wanted, string actual)
    {
        // Arrange - ShowWindow returns true (the window was visible before), but nothing changes: the app refused
        _native.Setup(n => n.ShowWindow(Window, It.IsAny<int>())).Returns(true);

        // Act
        var result = CreateSut().SetWindowState(Window, request);

        // Assert - it used to report ShowWindow's "was visible" as success
        result.Success.Should().BeFalse();
        result.ErrorCode.Should().Be(WindowErrorCode.StateChangeFailed);
        result.Error.Should().Contain($"did not become {wanted}").And.Contain($"it is {actual}");
    }

    [Fact]
    public void SetWindowState_IgnoresShowWindowsReturnValue_WhenTheStateWasReached()
    {
        // Arrange - restoring a hidden window: ShowWindow returns false (it was not visible), yet it worked
        var minimized = true;
        _native.Setup(n => n.IsIconic(Window)).Returns(() => minimized);
        _native.Setup(n => n.ShowWindow(Window, SwRestore)).Callback(() => minimized = false).Returns(false);

        // Act & Assert
        CreateSut().SetWindowState(Window, WindowStateRequest.Restore).Success.Should().BeTrue();
    }

    [Fact]
    public void SetWindowState_WaitsForAWindowThatAnimates()
    {
        // Arrange - the window is minimized only on the third read after the command
        var reads = 0;
        _native.Setup(n => n.ShowWindow(Window, It.IsAny<int>())).Returns(true);
        _native.Setup(n => n.IsIconic(Window)).Returns(() => ++reads > 3);
        var sut = new WindowService(_monitors.Object, _native.Object, Mock.Of<IHostGuard>(), stateChangeTimeout: TimeSpan.FromSeconds(5));

        // Act & Assert
        sut.SetWindowState(Window, WindowStateRequest.Minimize).Success.Should().BeTrue();
    }

    #endregion

    #region Monitor indexes

    [Theory]
    [InlineData(2)]
    [InlineData(-1)]
    public void SnapWindow_OnAMonitorThatDoesNotExist_IsInvalidMonitorIndex_AndMovesNothing(int monitorIndex)
    {
        // Act
        var result = CreateSut().SnapWindow(Window, SnapPosition.LeftHalf, monitorIndex);

        // Assert - it used to snap on the primary monitor and report success
        result.ErrorCode.Should().Be(WindowErrorCode.InvalidMonitorIndex);
        result.Error.Should().Contain($"Monitor index {monitorIndex} does not exist").And.Contain("valid indexes are 0 to 1");
        _native.Verify(n => n.SetWindowPos(It.IsAny<nint>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<uint>()), Times.Never);
        _monitors.Verify(m => m.CalculateSnapBounds(It.IsAny<int>(), It.IsAny<SnapPosition>()), Times.Never);
    }

    [Fact]
    public void MoveWindowToMonitor_OnAMonitorThatDoesNotExist_IsInvalidMonitorIndex()
    {
        var result = CreateSut().MoveWindowToMonitor(Window, 7, MonitorPositioning.Center);

        result.ErrorCode.Should().Be(WindowErrorCode.InvalidMonitorIndex);
        result.Error.Should().Contain("valid indexes are 0 to 1 (2 monitors)");
    }

    [Fact]
    public void MoveWindowToMonitor_Maximize_OfAWindowMaximizedOnAnotherMonitor_RestoresMovesThenMaximizesIt()
    {
        // Arrange - maximized on monitor 0; SW_MAXIMIZE alone would maximize it there again
        var state = TestDataFactory.SimulateShowStates(_native, Window, WindowState.Maximized);
        var onMonitor = 0;
        _monitors.Setup(m => m.GetMonitorIndex(Window)).Returns(() => onMonitor);
        var calls = new List<string>();
        _native.Setup(n => n.SetWindowPos(Window, It.IsAny<int>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<uint>()))
            .Callback<nint, int, int, int, int, uint>((_, x, _, _, _, _) =>
            {
                calls.Add($"move to {x} while {state()}");
                onMonitor = x >= 1920 ? 1 : 0;
            })
            .Returns(Win32CallResult.Ok);

        // Act
        var result = CreateSut().MoveWindowToMonitor(Window, 1, MonitorPositioning.Maximize);

        // Assert
        result.Success.Should().BeTrue(result.Error);
        calls.Should().Equal("move to 1920 while Normal");
        state().Should().Be(WindowState.Maximized);
        _native.Verify(n => n.ShowWindow(Window, SwRestore), Times.Once);
        _native.Verify(n => n.ShowWindow(Window, SwMaximize), Times.Once);
    }

    [Fact]
    public void MoveWindowToMonitor_Maximize_ThatEndsOnAnotherMonitor_IsMoveFailed()
    {
        // Arrange - the window does not leave monitor 0 (an app that keeps its position)
        TestDataFactory.SimulateShowStates(_native, Window);
        _monitors.Setup(m => m.GetMonitorIndex(Window)).Returns(0);

        // Act
        var result = CreateSut().MoveWindowToMonitor(Window, 1, MonitorPositioning.Maximize);

        // Assert - it used to maximize the window where it was and report success
        result.ErrorCode.Should().Be(WindowErrorCode.MoveFailed);
        result.Error.Should().Contain("maximized on monitor 0, not on monitor 1");
    }

    [Fact]
    public void CalculateSnapBounds_OfAMonitorThatDoesNotExist_Throws_InsteadOfUsingThePrimaryMonitor()
    {
        // Arrange
        var monitors = new Mock<INativeMonitorWrapper>();
        monitors.Setup(m => m.EnumerateMonitors()).Returns([(new nint(0x10001), TestDataFactory.CreateMonitorInfo())]);
        var sut = new MonitorService(monitors.Object);

        // Act
        var act = () => sut.CalculateSnapBounds(3, SnapPosition.LeftHalf);

        // Assert
        act.Should().Throw<ArgumentOutOfRangeException>().WithParameterName("monitorIndex");
    }

    #endregion

    #region Batches

    [Fact]
    public async Task ABatch_GivesEachWindowItsOwnOutcome_AndGoesOn()
    {
        // Arrange - the second window does not answer, the third one throws
        const nint Hung = 0x2002, Broken = 0x3003;
        foreach (var handle in new[] { Hung, Broken })
        {
            _native.Setup(n => n.IsWindow(handle)).Returns(true);
        }

        var runner = new Mock<IWindowCallRunner>();
        runner.Setup(r => r.Run(Window, It.IsAny<Func<Win32CallResult>>())).Returns(Win32CallResult.Ok);
        runner.Setup(r => r.Run(Hung, It.IsAny<Func<Win32CallResult>>())).Throws(new WindowNotRespondingException(Hung, "Window did not answer."));
        runner.Setup(r => r.Run(Broken, It.IsAny<Func<Win32CallResult>>())).Throws(new InvalidOperationException("Something broke."));
        _native.Setup(n => n.GetWindowRect(It.IsAny<nint>())).Returns((0, 0, 100, 100));
        var sut = new WindowService(_monitors.Object, _native.Object, Mock.Of<IHostGuard>(), windowCalls: runner.Object);

        // Act
        var results = await sut.SetWindowBoundsBatchAsync(
            [(Window, 0, 0, 100, 100, true), (Hung, 0, 0, 100, 100, true), (Broken, 0, 0, 100, 100, true), (0x9999, 0, 0, 100, 100, true)],
            delayBetweenMs: 0);

        // Assert
        results.Select(r => r.ErrorCode).Should().Equal(
            WindowErrorCode.None, WindowErrorCode.NotResponding, WindowErrorCode.MoveFailed, WindowErrorCode.InvalidHandle);
        results[2].Error.Should().Be("Something broke.");
    }

    #endregion
}
