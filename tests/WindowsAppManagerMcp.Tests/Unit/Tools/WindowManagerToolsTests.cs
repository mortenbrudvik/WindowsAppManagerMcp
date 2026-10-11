namespace WindowsAppManagerMcp.Tests.Unit.Tools;

using WindowsAppManagerMcp.Tools;

/// <summary>
/// Unit tests for WindowManagerTools.
/// Tests the MCP tool layer that exposes window management functionality.
/// </summary>
public class WindowManagerToolsTests
{
    private readonly Mock<IWindowService> _mockWindowService;
    private readonly Mock<IForegroundService> _mockForegroundService;

    public WindowManagerToolsTests()
    {
        _mockWindowService = new Mock<IWindowService>();
        _mockForegroundService = new Mock<IForegroundService>(MockBehavior.Strict);
    }

    private WindowManagerTools CreateSut() => new WindowManagerTools(_mockWindowService.Object, _mockForegroundService.Object);

    #region MoveWindow Tests

    [Fact]
    public async Task MoveWindow_WithInvalidHandle_ReturnsFailure()
    {
        // Arrange
        _mockWindowService.Setup(w => w.IsValidWindow(It.IsAny<nint>()))
            .Returns(false);
        var sut = CreateSut();

        // Act
        var result = await sut.MoveWindow(100, 100, handle: 99999);

        // Assert
        result.Success.Should().BeFalse();
        result.Error.Should().Be("Invalid window handle");
        result.ErrorCode.Should().Be("InvalidHandle");
    }

    [Fact]
    public async Task MoveWindow_WithValidHandle_CallsService()
    {
        // Arrange
        var handle = new nint(12345);
        _mockWindowService.Setup(w => w.IsValidWindow(handle)).Returns(true);
        _mockWindowService.Setup(w => w.MoveWindow(handle, 100, 200, It.IsAny<bool>())).Returns(WindowChangeResult.Done(handle));
        _mockWindowService.Setup(w => w.GetWindowInfo(handle))
            .Returns(TestDataFactory.CreateWindowInfo(handleValue: 12345, x: 100, y: 200));
        var sut = CreateSut();

        // Act
        var result = await sut.MoveWindow(100, 200, handle: 12345);

        // Assert
        result.Success.Should().BeTrue();
        _mockWindowService.Verify(w => w.MoveWindow(handle, 100, 200, It.IsAny<bool>()), Times.Once);
    }

    [Fact]
    public async Task MoveWindow_ReturnsTheBoundsAfterwards()
    {
        // Arrange
        var handle = new nint(12345);
        _mockWindowService.Setup(w => w.IsValidWindow(handle)).Returns(true);
        _mockWindowService.Setup(w => w.MoveWindow(handle, 100, 200, It.IsAny<bool>())).Returns(WindowChangeResult.Done(handle));
        _mockWindowService.Setup(w => w.GetWindowInfo(handle))
            .Returns(TestDataFactory.CreateWindowInfo(handleValue: 12345, x: 100, y: 200, width: 800, height: 600));
        var sut = CreateSut();

        // Act
        var result = await sut.MoveWindow(100, 200, handle: 12345);

        // Assert
        result.Bounds.Should().NotBeNull();
        result.Bounds!.X.Should().Be(100);
        result.Bounds.Y.Should().Be(200);
    }

    #endregion

    #region ResizeWindow Tests

    [Fact]
    public async Task ResizeWindow_WithInvalidHandle_ReturnsFailure()
    {
        // Arrange
        _mockWindowService.Setup(w => w.IsValidWindow(It.IsAny<nint>()))
            .Returns(false);
        var sut = CreateSut();

        // Act
        var result = await sut.ResizeWindow(800, 600, handle: 99999);

        // Assert
        result.Success.Should().BeFalse();
        result.Error.Should().Be("Invalid window handle");
    }

    [Fact]
    public async Task ResizeWindow_WithValidHandle_CallsService()
    {
        // Arrange
        var handle = new nint(12345);
        _mockWindowService.Setup(w => w.IsValidWindow(handle)).Returns(true);
        _mockWindowService.Setup(w => w.ResizeWindow(handle, 1024, 768)).Returns(WindowChangeResult.Done(handle));
        _mockWindowService.Setup(w => w.GetWindowInfo(handle))
            .Returns(TestDataFactory.CreateWindowInfo(width: 1024, height: 768));
        var sut = CreateSut();

        // Act
        var result = await sut.ResizeWindow(1024, 768, handle: 12345);

        // Assert
        result.Success.Should().BeTrue();
        _mockWindowService.Verify(w => w.ResizeWindow(handle, 1024, 768), Times.Once);
    }

    #endregion

    #region SetWindowBounds Tests

    [Fact]
    public async Task SetWindowBounds_WithInvalidHandle_ReturnsFailure()
    {
        // Arrange
        _mockWindowService.Setup(w => w.IsValidWindow(It.IsAny<nint>()))
            .Returns(false);
        var sut = CreateSut();

        // Act
        var result = await sut.SetWindowBounds(0, 0, 800, 600, handle: 99999);

        // Assert
        result.Success.Should().BeFalse();
        result.Error.Should().Be("Invalid window handle");
    }

    [Fact]
    public async Task SetWindowBounds_WithValidHandle_CallsService()
    {
        // Arrange
        var handle = new nint(12345);
        _mockWindowService.Setup(w => w.IsValidWindow(handle)).Returns(true);
        _mockWindowService.Setup(w => w.SetWindowFrame(handle, 50, 50, 1000, 800, It.IsAny<bool>())).Returns(WindowChangeResult.Done(handle));
        _mockWindowService.Setup(w => w.GetWindowInfo(handle))
            .Returns(TestDataFactory.CreateWindowInfo(x: 50, y: 50, width: 1000, height: 800));
        var sut = CreateSut();

        // Act
        var result = await sut.SetWindowBounds(50, 50, 1000, 800, handle: 12345);

        // Assert
        result.Success.Should().BeTrue();
        result.Bounds.Should().NotBeNull();
        result.Bounds!.X.Should().Be(50);
        result.Bounds.Y.Should().Be(50);
        result.Bounds.Width.Should().Be(1000);
        result.Bounds.Height.Should().Be(800);
    }

    #endregion

    #region SetWindowState Tests

    [Fact]
    public async Task SetWindowState_WithInvalidHandle_ReturnsFailure()
    {
        // Arrange
        _mockWindowService.Setup(w => w.IsValidWindow(It.IsAny<nint>()))
            .Returns(false);
        var sut = CreateSut();

        // Act
        var result = await sut.SetWindowState(WindowStateRequest.Minimize, handle: 99999);

        // Assert
        result.Success.Should().BeFalse();
        result.Error.Should().Be("Invalid window handle");
    }

    [Fact]
    public async Task SetWindowState_ReturnsTheStateAfterwards()
    {
        // Arrange
        var handle = new nint(12345);
        _mockWindowService.Setup(w => w.IsValidWindow(handle)).Returns(true);
        _mockWindowService.Setup(w => w.SetWindowState(handle, WindowStateRequest.Maximize)).Returns(WindowChangeResult.Done(handle));
        _mockWindowService.Setup(w => w.GetWindowInfo(handle))
            .Returns(TestDataFactory.CreateWindowInfo(state: WindowState.Maximized));
        var sut = CreateSut();

        // Act
        var result = await sut.SetWindowState(WindowStateRequest.Maximize, handle: 12345);

        // Assert
        result.State.Should().Be("maximized");
    }

    #endregion

    #region FocusWindow Tests

    [Fact]
    public async Task FocusWindow_WithInvalidHandle_ReturnsInvalidHandle()
    {
        // Arrange
        _mockForegroundService.Setup(f => f.EnsureForeground(new nint(99999), It.IsAny<int>())).Returns(new FocusOutcome
        {
            Target = new nint(99999),
            ForegroundHandle = new nint(0x200),
            ErrorCode = WindowErrorCode.InvalidHandle,
            Error = "Invalid window handle"
        });
        var sut = CreateSut();

        // Act
        var result = await sut.FocusWindow(handle: 99999);

        // Assert
        result.Success.Should().BeFalse();
        result.IsForeground.Should().BeFalse();
        result.Error.Should().Be("Invalid window handle");
        result.ErrorCode.Should().Be("InvalidHandle");
        result.ForegroundHandle.Should().Be(0x200);
    }

    [Fact]
    public async Task FocusWindow_ReturnsTheVerifiedOutcome_WithSuccessForCompatibility()
    {
        // Arrange - a dialog of the window is in front
        var handle = new nint(12345);
        _mockForegroundService.Setup(f => f.EnsureForeground(handle, It.IsAny<int>())).Returns(new FocusOutcome
        {
            Target = handle,
            IsForeground = true,
            Method = FocusMethod.AttachThreadInput,
            ForegroundHandle = new nint(0x300),
            ForegroundTitle = "Save As",
            ForegroundProcess = "notepad",
            BlockingPopup = new nint(0x300),
            BlockingPopupTitle = "Save As"
        });
        var sut = CreateSut();

        // Act
        var result = await sut.FocusWindow(handle: 12345);

        // Assert
        result.Should().BeEquivalentTo(new FocusResult(
            Success: true,
            Handle: 12345,
            IsForeground: true,
            Method: "attachThreadInput",
            ForegroundHandle: 0x300,
            ForegroundTitle: "Save As",
            ForegroundProcess: "notepad",
            BlockingPopup: new PopupWindowInfo(0x300, "Save As")));
        _mockForegroundService.Verify(f => f.EnsureForeground(handle, ForegroundService.DefaultTimeoutMs), Times.Once);
    }

    [Fact]
    public async Task FocusWindow_WhenAMenuIsStillActive_ReportsInMenuMode()
    {
        // Arrange - in front, but typed keys would pick menu items
        var handle = new nint(12345);
        _mockForegroundService.Setup(f => f.EnsureForeground(handle, It.IsAny<int>())).Returns(new FocusOutcome
        {
            Target = handle,
            IsForeground = true,
            Method = FocusMethod.Already,
            ForegroundHandle = handle,
            InMenuMode = true
        });
        var sut = CreateSut();

        // Act
        var result = await sut.FocusWindow(handle: 12345);

        // Assert
        result.Success.Should().BeTrue();
        result.InMenuMode.Should().BeTrue();
        result.MenuClosed.Should().BeNull();
    }

    #endregion

    #region CloseWindow Tests

    [Fact]
    public async Task CloseWindow_WithInvalidHandle_ReturnsFailure()
    {
        // Arrange
        _mockWindowService.Setup(w => w.IsValidWindow(It.IsAny<nint>()))
            .Returns(false);
        var sut = CreateSut();

        // Act
        var result = await sut.CloseWindow(99999);

        // Assert
        result.Success.Should().BeFalse();
        result.Error.Should().Be("Invalid window handle");
    }

    [Fact]
    public async Task CloseWindow_WithValidHandle_CallsService()
    {
        // Arrange
        var handle = new nint(12345);
        _mockWindowService.Setup(w => w.IsValidWindow(handle)).Returns(true);
        _mockWindowService.Setup(w => w.CloseWindow(handle, false)).Returns(WindowCloseResult.Sent(handle));
        var sut = CreateSut();

        // Act
        var result = await sut.CloseWindow(12345, waitMs: 0);

        // Assert
        result.Success.Should().BeTrue();
        _mockWindowService.Verify(w => w.CloseWindow(handle, false), Times.Once);
    }

    [Fact]
    public async Task CloseWindow_WhenServiceReturnsFalse_ReturnsFalse()
    {
        // Arrange
        var handle = new nint(12345);
        _mockWindowService.Setup(w => w.IsValidWindow(handle)).Returns(true);
        _mockWindowService.Setup(w => w.CloseWindow(handle, false))
            .Returns(WindowCloseResult.Failed(handle, WindowErrorCode.CloseFailed, "Windows did not accept the close message"));
        var sut = CreateSut();

        // Act
        var result = await sut.CloseWindow(12345, waitMs: 0);

        // Assert
        result.Success.Should().BeFalse();
    }

    #endregion

    #region SnapWindow Tests

    [Fact]
    public async Task SnapWindow_WithInvalidHandle_ReturnsFailure()
    {
        // Arrange
        _mockWindowService.Setup(w => w.IsValidWindow(It.IsAny<nint>()))
            .Returns(false);
        var sut = CreateSut();

        // Act
        var result = await sut.SnapWindow(SnapPosition.LeftHalf, handle: 99999);

        // Assert
        result.Success.Should().BeFalse();
        result.Error.Should().Be("Invalid window handle");
    }

    [Fact]
    public async Task SnapWindow_PassesMonitorIndex()
    {
        // Arrange
        var handle = new nint(12345);
        _mockWindowService.Setup(w => w.IsValidWindow(handle)).Returns(true);
        _mockWindowService.Setup(w => w.SnapWindow(handle, SnapPosition.LeftHalf, 1, It.IsAny<bool>())).Returns(WindowChangeResult.Done(handle));
        _mockWindowService.Setup(w => w.GetWindowInfo(handle))
            .Returns(TestDataFactory.CreateWindowInfo());
        var sut = CreateSut();

        // Act
        await sut.SnapWindow(SnapPosition.LeftHalf, handle: 12345, monitorIndex: 1);

        // Assert
        _mockWindowService.Verify(w => w.SnapWindow(handle, SnapPosition.LeftHalf, 1, It.IsAny<bool>()), Times.Once);
    }

    #endregion

    #region MoveWindowToMonitor Tests

    [Fact]
    public async Task MoveWindowToMonitor_WithInvalidHandle_ReturnsFailure()
    {
        // Arrange
        _mockWindowService.Setup(w => w.IsValidWindow(It.IsAny<nint>()))
            .Returns(false);
        var sut = CreateSut();

        // Act
        var result = await sut.MoveWindowToMonitor(1, handle: 99999);

        // Assert
        result.Success.Should().BeFalse();
        result.Error.Should().Be("Invalid window handle");
    }

    [Fact]
    public async Task MoveWindowToMonitor_WithValidHandle_CallsService()
    {
        // Arrange
        var handle = new nint(12345);
        _mockWindowService.Setup(w => w.IsValidWindow(handle)).Returns(true);
        _mockWindowService.Setup(w => w.MoveWindowToMonitor(handle, 1, MonitorPositioning.Center, It.IsAny<bool>())).Returns(WindowChangeResult.Done(handle));
        _mockWindowService.Setup(w => w.GetWindowInfo(handle))
            .Returns(TestDataFactory.CreateWindowInfo());
        var sut = CreateSut();

        // Act
        var result = await sut.MoveWindowToMonitor(1, handle: 12345);

        // Assert
        result.Success.Should().BeTrue();
        _mockWindowService.Verify(w => w.MoveWindowToMonitor(handle, 1, MonitorPositioning.Center, It.IsAny<bool>()), Times.Once);
    }

    [Fact]
    public async Task MoveWindowToMonitor_PassesPositioning()
    {
        // Arrange
        var handle = new nint(12345);
        _mockWindowService.Setup(w => w.IsValidWindow(handle)).Returns(true);
        _mockWindowService.Setup(w => w.MoveWindowToMonitor(handle, 0, MonitorPositioning.Maximize, It.IsAny<bool>())).Returns(WindowChangeResult.Done(handle));
        _mockWindowService.Setup(w => w.GetWindowInfo(handle))
            .Returns(TestDataFactory.CreateWindowInfo());
        var sut = CreateSut();

        // Act
        await sut.MoveWindowToMonitor(0, handle: 12345, positioning: MonitorPositioning.Maximize);

        // Assert
        _mockWindowService.Verify(w => w.MoveWindowToMonitor(handle, 0, MonitorPositioning.Maximize, It.IsAny<bool>()), Times.Once);
    }

    #endregion

    #region The window as it is afterwards (roadmap M2.3)

    [Fact]
    public async Task AGeometryResult_ReportsTheVisibleFrame_TheWindowRect_TheStateAndTheMonitor()
    {
        // Arrange
        var handle = new nint(12345);
        _mockWindowService.Setup(w => w.IsValidWindow(handle)).Returns(true);
        _mockWindowService.Setup(w => w.SnapWindow(handle, SnapPosition.LeftHalf, null, true)).Returns(WindowChangeResult.Done(handle));
        _mockWindowService.Setup(w => w.GetWindowInfo(handle)).Returns(
            TestDataFactory.CreateWindowInfo(x: -7, y: 0, width: 974, height: 1047, monitorIndex: 1) with
            {
                VisibleBounds = new WindowRect(0, 0, 960, 1040)
            });

        // Act
        var result = await CreateSut().SnapWindow(SnapPosition.LeftHalf, handle: 12345);

        // Assert
        result.Should().Be(new WindowOperationResult(
            Success: true,
            Bounds: new BoundsDto(0, 0, 960, 1040),
            OuterBounds: new BoundsDto(-7, 0, 974, 1047),
            State: "normal",
            MonitorIndex: 1,
            Handle: 12345));
    }

    [Fact]
    public async Task AGeometryResult_WithoutAVisibleFrame_ReportsTheWindowRectAsBounds()
    {
        var handle = new nint(12345);
        _mockWindowService.Setup(w => w.IsValidWindow(handle)).Returns(true);
        _mockWindowService.Setup(w => w.MoveWindow(handle, 1, 2, true)).Returns(WindowChangeResult.Done(handle));
        _mockWindowService.Setup(w => w.GetWindowInfo(handle)).Returns(TestDataFactory.CreateWindowInfo(x: 1, y: 2));

        var result = await CreateSut().MoveWindow(1, 2, handle: 12345);

        result.Bounds.Should().Be(new BoundsDto(1, 2, 800, 600));
        result.OuterBounds.Should().Be(result.Bounds);
    }

    [Fact]
    public async Task AMinimizedWindow_IsReportedWithoutBounds()
    {
        // Arrange - a minimized window's rect is its icon at -32000, which says nothing about where it will be
        var handle = new nint(12345);
        _mockWindowService.Setup(w => w.IsValidWindow(handle)).Returns(true);
        _mockWindowService.Setup(w => w.SetWindowState(handle, WindowStateRequest.Minimize)).Returns(WindowChangeResult.Done(handle));
        _mockWindowService.Setup(w => w.GetWindowInfo(handle)).Returns(
            TestDataFactory.CreateWindowInfo(x: -32000, y: -32000, width: 160, height: 28, state: WindowState.Minimized));

        // Act
        var result = await CreateSut().SetWindowState(WindowStateRequest.Minimize, handle: 12345);

        // Assert
        result.State.Should().Be("minimized");
        result.Bounds.Should().BeNull();
        result.OuterBounds.Should().BeNull();
    }

    [Fact]
    public async Task AFailedChange_StillReportsWhereTheWindowIs()
    {
        var handle = new nint(12345);
        _mockWindowService.Setup(w => w.IsValidWindow(handle)).Returns(true);
        _mockWindowService.Setup(w => w.MoveWindowToMonitor(handle, 1, MonitorPositioning.Center, true))
            .Returns(WindowChangeResult.Failed(handle, WindowErrorCode.MoveFailed, "The window is on monitor 0, not on monitor 1"));
        _mockWindowService.Setup(w => w.GetWindowInfo(handle)).Returns(TestDataFactory.CreateWindowInfo());

        var result = await CreateSut().MoveWindowToMonitor(1, handle: 12345);

        result.ErrorCode.Should().Be(nameof(WindowErrorCode.MoveFailed));
        result.MonitorIndex.Should().Be(0);
        result.Bounds.Should().NotBeNull();
    }

    [Fact]
    public async Task SetWindowBounds_PlacesTheVisibleFrame()
    {
        var handle = new nint(12345);
        _mockWindowService.Setup(w => w.IsValidWindow(handle)).Returns(true);
        _mockWindowService.Setup(w => w.SetWindowFrame(handle, 0, 0, 960, 1040, true)).Returns(WindowChangeResult.Done(handle));

        (await CreateSut().SetWindowBounds(0, 0, 960, 1040, handle: 12345)).Success.Should().BeTrue();

        _mockWindowService.Verify(w => w.SetWindowBounds(It.IsAny<nint>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<int>(),
            It.IsAny<int>(), It.IsAny<bool>()), Times.Never, "SetWindowBounds places the window rect, which layouts use");
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(250, 250)]
    [InlineData(-1, 0)]
    [InlineData(60_000, 5000)]
    public async Task SettleMs_WaitsForTheWindowToStopMoving_ForAtMostFiveSeconds(int settleMs, int expectedMs)
    {
        var handle = new nint(12345);
        _mockWindowService.Setup(w => w.IsValidWindow(handle)).Returns(true);
        _mockWindowService.Setup(w => w.ResizeWindow(handle, 640, 480)).Returns(WindowChangeResult.Done(handle));
        _mockWindowService.Setup(w => w.GetWindowInfo(handle)).Returns(TestDataFactory.CreateWindowInfo());

        await CreateSut().ResizeWindow(640, 480, handle: 12345, settleMs: settleMs);

        _mockWindowService.Verify(w => w.WaitForSettledFrame(handle, TimeSpan.FromMilliseconds(expectedMs)), Times.Once);
    }

    [Fact]
    public async Task AFailedChange_DoesNotWaitForTheWindowToSettle()
    {
        var handle = new nint(12345);
        _mockWindowService.Setup(w => w.IsValidWindow(handle)).Returns(true);
        _mockWindowService.Setup(w => w.ResizeWindow(handle, 640, 480))
            .Returns(WindowChangeResult.Failed(handle, WindowErrorCode.AccessDenied, "blocked"));

        await CreateSut().ResizeWindow(640, 480, handle: 12345, settleMs: 1000);

        _mockWindowService.Verify(w => w.WaitForSettledFrame(It.IsAny<nint>(), It.IsAny<TimeSpan>()), Times.Never);
    }

    #endregion

    #region A window that does not respond

    [Theory]
    [InlineData("move_window")]
    [InlineData("resize_window")]
    [InlineData("set_window_bounds")]
    [InlineData("set_window_state")]
    [InlineData("snap_window")]
    [InlineData("move_window_to_monitor")]
    public async Task AWindowThatDoesNotRespond_IsReportedAsNotResponding(string tool)
    {
        // Arrange - the service gave up on the window at its deadline (WindowCallRunner)
        var handle = new nint(12345);
        var notResponding = new WindowNotRespondingException(handle, "Window 12345 did not answer within 5 s.");
        _mockWindowService.Setup(w => w.IsValidWindow(handle)).Returns(true);
        _mockWindowService.Setup(w => w.MoveWindow(handle, It.IsAny<int>(), It.IsAny<int>(), It.IsAny<bool>())).Throws(notResponding);
        _mockWindowService.Setup(w => w.ResizeWindow(handle, It.IsAny<int>(), It.IsAny<int>())).Throws(notResponding);
        _mockWindowService.Setup(w => w.SetWindowFrame(handle, It.IsAny<int>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<bool>()))
            .Throws(notResponding);
        _mockWindowService.Setup(w => w.SetWindowState(handle, It.IsAny<WindowStateRequest>())).Throws(notResponding);
        _mockWindowService.Setup(w => w.SnapWindow(handle, It.IsAny<SnapPosition>(), It.IsAny<int?>(), It.IsAny<bool>())).Throws(notResponding);
        _mockWindowService.Setup(w => w.MoveWindowToMonitor(handle, It.IsAny<int>(), It.IsAny<MonitorPositioning>(), It.IsAny<bool>())).Throws(notResponding);
        var sut = CreateSut();

        // Act
        var result = tool switch
        {
            "move_window" => await sut.MoveWindow(0, 0, handle: 12345),
            "resize_window" => await sut.ResizeWindow(640, 480, handle: 12345),
            "set_window_bounds" => await sut.SetWindowBounds(0, 0, 640, 480, handle: 12345),
            "set_window_state" => await sut.SetWindowState(WindowStateRequest.Minimize, handle: 12345),
            "snap_window" => await sut.SnapWindow(SnapPosition.LeftHalf, handle: 12345),
            _ => await sut.MoveWindowToMonitor(0, handle: 12345)
        };

        // Assert
        result.Success.Should().BeFalse();
        result.ErrorCode.Should().Be(nameof(WindowErrorCode.NotResponding));
        result.Error.Should().Contain("did not answer");
    }

    #endregion
}
