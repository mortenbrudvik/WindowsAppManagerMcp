namespace WindowsAppManagerMcp.Tests.Unit.Tools;

using WindowsAppManagerMcp.Tools;

/// <summary>
/// Unit tests for WindowManagerTools.
/// Tests the MCP tool layer that exposes window management functionality.
/// </summary>
public class WindowManagerToolsTests
{
    private readonly Mock<IWindowService> _mockWindowService;

    public WindowManagerToolsTests()
    {
        _mockWindowService = new Mock<IWindowService>();
    }

    private WindowManagerTools CreateSut() => new WindowManagerTools(_mockWindowService.Object);

    #region MoveWindow Tests

    [Fact]
    public void MoveWindow_WithInvalidHandle_ReturnsFailure()
    {
        // Arrange
        _mockWindowService.Setup(w => w.IsValidWindow(It.IsAny<nint>()))
            .Returns(false);
        var sut = CreateSut();

        // Act
        var result = sut.MoveWindow(99999, 100, 100);

        // Assert
        result.Success.Should().BeFalse();
        result.Error.Should().Be("Invalid window handle");
        result.ErrorCode.Should().Be("InvalidHandle");
    }

    [Fact]
    public void MoveWindow_WithValidHandle_CallsService()
    {
        // Arrange
        var handle = new nint(12345);
        _mockWindowService.Setup(w => w.IsValidWindow(handle)).Returns(true);
        _mockWindowService.Setup(w => w.MoveWindow(handle, 100, 200, It.IsAny<bool>())).Returns(true);
        _mockWindowService.Setup(w => w.GetWindowInfo(handle))
            .Returns(TestDataFactory.CreateWindowInfo(handleValue: 12345, x: 100, y: 200));
        var sut = CreateSut();

        // Act
        var result = sut.MoveWindow(12345, 100, 200);

        // Assert
        result.Success.Should().BeTrue();
        _mockWindowService.Verify(w => w.MoveWindow(handle, 100, 200, It.IsAny<bool>()), Times.Once);
    }

    [Fact]
    public void MoveWindow_ReturnsNewBounds()
    {
        // Arrange
        var handle = new nint(12345);
        _mockWindowService.Setup(w => w.IsValidWindow(handle)).Returns(true);
        _mockWindowService.Setup(w => w.MoveWindow(handle, 100, 200, It.IsAny<bool>())).Returns(true);
        _mockWindowService.Setup(w => w.GetWindowInfo(handle))
            .Returns(TestDataFactory.CreateWindowInfo(handleValue: 12345, x: 100, y: 200, width: 800, height: 600));
        var sut = CreateSut();

        // Act
        var result = sut.MoveWindow(12345, 100, 200);

        // Assert
        result.NewBounds.Should().NotBeNull();
        result.NewBounds!.X.Should().Be(100);
        result.NewBounds.Y.Should().Be(200);
    }

    #endregion

    #region ResizeWindow Tests

    [Fact]
    public void ResizeWindow_WithInvalidHandle_ReturnsFailure()
    {
        // Arrange
        _mockWindowService.Setup(w => w.IsValidWindow(It.IsAny<nint>()))
            .Returns(false);
        var sut = CreateSut();

        // Act
        var result = sut.ResizeWindow(99999, 800, 600);

        // Assert
        result.Success.Should().BeFalse();
        result.Error.Should().Be("Invalid window handle");
    }

    [Fact]
    public void ResizeWindow_WithValidHandle_CallsService()
    {
        // Arrange
        var handle = new nint(12345);
        _mockWindowService.Setup(w => w.IsValidWindow(handle)).Returns(true);
        _mockWindowService.Setup(w => w.ResizeWindow(handle, 1024, 768)).Returns(true);
        _mockWindowService.Setup(w => w.GetWindowInfo(handle))
            .Returns(TestDataFactory.CreateWindowInfo(width: 1024, height: 768));
        var sut = CreateSut();

        // Act
        var result = sut.ResizeWindow(12345, 1024, 768);

        // Assert
        result.Success.Should().BeTrue();
        _mockWindowService.Verify(w => w.ResizeWindow(handle, 1024, 768), Times.Once);
    }

    #endregion

    #region SetWindowBounds Tests

    [Fact]
    public void SetWindowBounds_WithInvalidHandle_ReturnsFailure()
    {
        // Arrange
        _mockWindowService.Setup(w => w.IsValidWindow(It.IsAny<nint>()))
            .Returns(false);
        var sut = CreateSut();

        // Act
        var result = sut.SetWindowBounds(99999, 0, 0, 800, 600);

        // Assert
        result.Success.Should().BeFalse();
        result.Error.Should().Be("Invalid window handle");
    }

    [Fact]
    public void SetWindowBounds_WithValidHandle_CallsService()
    {
        // Arrange
        var handle = new nint(12345);
        _mockWindowService.Setup(w => w.IsValidWindow(handle)).Returns(true);
        _mockWindowService.Setup(w => w.SetWindowBounds(handle, 50, 50, 1000, 800, It.IsAny<bool>())).Returns(true);
        _mockWindowService.Setup(w => w.GetWindowInfo(handle))
            .Returns(TestDataFactory.CreateWindowInfo(x: 50, y: 50, width: 1000, height: 800));
        var sut = CreateSut();

        // Act
        var result = sut.SetWindowBounds(12345, 50, 50, 1000, 800);

        // Assert
        result.Success.Should().BeTrue();
        result.NewBounds.Should().NotBeNull();
        result.NewBounds!.X.Should().Be(50);
        result.NewBounds.Y.Should().Be(50);
        result.NewBounds.Width.Should().Be(1000);
        result.NewBounds.Height.Should().Be(800);
    }

    #endregion

    #region SetWindowState Tests

    [Fact]
    public void SetWindowState_WithInvalidHandle_ReturnsFailure()
    {
        // Arrange
        _mockWindowService.Setup(w => w.IsValidWindow(It.IsAny<nint>()))
            .Returns(false);
        var sut = CreateSut();

        // Act
        var result = sut.SetWindowState(99999, "minimize");

        // Assert
        result.Success.Should().BeFalse();
        result.Error.Should().Be("Invalid window handle");
    }

    [Theory]
    [InlineData("minimize", WindowState.Minimized)]
    [InlineData("minimized", WindowState.Minimized)]
    [InlineData("maximize", WindowState.Maximized)]
    [InlineData("maximized", WindowState.Maximized)]
    [InlineData("restore", WindowState.Normal)]
    [InlineData("normal", WindowState.Normal)]
    public void SetWindowState_ParsesStateStrings(string stateString, WindowState expectedState)
    {
        // Arrange
        var handle = new nint(12345);
        _mockWindowService.Setup(w => w.IsValidWindow(handle)).Returns(true);
        _mockWindowService.Setup(w => w.SetWindowState(handle, expectedState)).Returns(true);
        _mockWindowService.Setup(w => w.GetWindowInfo(handle))
            .Returns(TestDataFactory.CreateWindowInfo(state: expectedState));
        var sut = CreateSut();

        // Act
        var result = sut.SetWindowState(12345, stateString);

        // Assert
        _mockWindowService.Verify(w => w.SetWindowState(handle, expectedState), Times.Once);
    }

    [Fact]
    public void SetWindowState_ReturnsNewState()
    {
        // Arrange
        var handle = new nint(12345);
        _mockWindowService.Setup(w => w.IsValidWindow(handle)).Returns(true);
        _mockWindowService.Setup(w => w.SetWindowState(handle, WindowState.Maximized)).Returns(true);
        _mockWindowService.Setup(w => w.GetWindowInfo(handle))
            .Returns(TestDataFactory.CreateWindowInfo(state: WindowState.Maximized));
        var sut = CreateSut();

        // Act
        var result = sut.SetWindowState(12345, "maximize");

        // Assert
        result.NewState.Should().Be("maximized");
    }

    #endregion

    #region FocusWindow Tests

    [Fact]
    public void FocusWindow_WithInvalidHandle_ReturnsFailure()
    {
        // Arrange
        _mockWindowService.Setup(w => w.IsValidWindow(It.IsAny<nint>()))
            .Returns(false);
        var sut = CreateSut();

        // Act
        var result = sut.FocusWindow(99999);

        // Assert
        result.Success.Should().BeFalse();
        result.Error.Should().Be("Invalid window handle");
    }

    [Fact]
    public void FocusWindow_WithValidHandle_CallsService()
    {
        // Arrange
        var handle = new nint(12345);
        _mockWindowService.Setup(w => w.IsValidWindow(handle)).Returns(true);
        _mockWindowService.Setup(w => w.FocusWindow(handle)).Returns(true);
        var sut = CreateSut();

        // Act
        var result = sut.FocusWindow(12345);

        // Assert
        result.Success.Should().BeTrue();
        _mockWindowService.Verify(w => w.FocusWindow(handle), Times.Once);
    }

    #endregion

    #region CloseWindow Tests

    [Fact]
    public void CloseWindow_WithInvalidHandle_ReturnsFailure()
    {
        // Arrange
        _mockWindowService.Setup(w => w.IsValidWindow(It.IsAny<nint>()))
            .Returns(false);
        var sut = CreateSut();

        // Act
        var result = sut.CloseWindow(99999);

        // Assert
        result.Success.Should().BeFalse();
        result.Error.Should().Be("Invalid window handle");
    }

    [Fact]
    public void CloseWindow_WithValidHandle_CallsService()
    {
        // Arrange
        var handle = new nint(12345);
        _mockWindowService.Setup(w => w.IsValidWindow(handle)).Returns(true);
        _mockWindowService.Setup(w => w.CloseWindow(handle)).Returns(true);
        var sut = CreateSut();

        // Act
        var result = sut.CloseWindow(12345);

        // Assert
        result.Success.Should().BeTrue();
        _mockWindowService.Verify(w => w.CloseWindow(handle), Times.Once);
    }

    [Fact]
    public void CloseWindow_WhenServiceReturnsFalse_ReturnsFalse()
    {
        // Arrange
        var handle = new nint(12345);
        _mockWindowService.Setup(w => w.IsValidWindow(handle)).Returns(true);
        _mockWindowService.Setup(w => w.CloseWindow(handle)).Returns(false);
        var sut = CreateSut();

        // Act
        var result = sut.CloseWindow(12345);

        // Assert
        result.Success.Should().BeFalse();
    }

    #endregion

    #region SnapWindow Tests

    [Fact]
    public void SnapWindow_WithInvalidHandle_ReturnsFailure()
    {
        // Arrange
        _mockWindowService.Setup(w => w.IsValidWindow(It.IsAny<nint>()))
            .Returns(false);
        var sut = CreateSut();

        // Act
        var result = sut.SnapWindow(99999, "left_half");

        // Assert
        result.Success.Should().BeFalse();
        result.Error.Should().Be("Invalid window handle");
    }

    [Fact]
    public void SnapWindow_WithInvalidPosition_ReturnsFailure()
    {
        // Arrange
        var handle = new nint(12345);
        _mockWindowService.Setup(w => w.IsValidWindow(handle)).Returns(true);
        var sut = CreateSut();

        // Act
        var result = sut.SnapWindow(12345, "invalid_position");

        // Assert
        result.Success.Should().BeFalse();
        result.Error.Should().Contain("Invalid snap position");
        result.ErrorCode.Should().Be("InvalidSnapPosition");
    }

    [Theory]
    [InlineData("left_half", SnapPosition.LeftHalf)]
    [InlineData("right_half", SnapPosition.RightHalf)]
    [InlineData("top_half", SnapPosition.TopHalf)]
    [InlineData("bottom_half", SnapPosition.BottomHalf)]
    [InlineData("top_left_quarter", SnapPosition.TopLeftQuarter)]
    [InlineData("top_right_quarter", SnapPosition.TopRightQuarter)]
    [InlineData("bottom_left_quarter", SnapPosition.BottomLeftQuarter)]
    [InlineData("bottom_right_quarter", SnapPosition.BottomRightQuarter)]
    [InlineData("left_third", SnapPosition.LeftThird)]
    [InlineData("center_third", SnapPosition.CenterThird)]
    [InlineData("right_third", SnapPosition.RightThird)]
    [InlineData("left_two_thirds", SnapPosition.LeftTwoThirds)]
    [InlineData("right_two_thirds", SnapPosition.RightTwoThirds)]
    [InlineData("fullscreen", SnapPosition.Fullscreen)]
    public void SnapWindow_ParsesPositionStrings(string positionString, SnapPosition expectedPosition)
    {
        // Arrange
        var handle = new nint(12345);
        _mockWindowService.Setup(w => w.IsValidWindow(handle)).Returns(true);
        _mockWindowService.Setup(w => w.SnapWindow(handle, expectedPosition, null, It.IsAny<bool>())).Returns(true);
        _mockWindowService.Setup(w => w.GetWindowInfo(handle))
            .Returns(TestDataFactory.CreateWindowInfo());
        var sut = CreateSut();

        // Act
        sut.SnapWindow(12345, positionString);

        // Assert
        _mockWindowService.Verify(w => w.SnapWindow(handle, expectedPosition, null, It.IsAny<bool>()), Times.Once);
    }

    [Fact]
    public void SnapWindow_PassesMonitorIndex()
    {
        // Arrange
        var handle = new nint(12345);
        _mockWindowService.Setup(w => w.IsValidWindow(handle)).Returns(true);
        _mockWindowService.Setup(w => w.SnapWindow(handle, SnapPosition.LeftHalf, 1, It.IsAny<bool>())).Returns(true);
        _mockWindowService.Setup(w => w.GetWindowInfo(handle))
            .Returns(TestDataFactory.CreateWindowInfo());
        var sut = CreateSut();

        // Act
        sut.SnapWindow(12345, "left_half", monitorIndex: 1);

        // Assert
        _mockWindowService.Verify(w => w.SnapWindow(handle, SnapPosition.LeftHalf, 1, It.IsAny<bool>()), Times.Once);
    }

    #endregion

    #region MoveWindowToMonitor Tests

    [Fact]
    public void MoveWindowToMonitor_WithInvalidHandle_ReturnsFailure()
    {
        // Arrange
        _mockWindowService.Setup(w => w.IsValidWindow(It.IsAny<nint>()))
            .Returns(false);
        var sut = CreateSut();

        // Act
        var result = sut.MoveWindowToMonitor(99999, 1);

        // Assert
        result.Success.Should().BeFalse();
        result.Error.Should().Be("Invalid window handle");
    }

    [Fact]
    public void MoveWindowToMonitor_WithValidHandle_CallsService()
    {
        // Arrange
        var handle = new nint(12345);
        _mockWindowService.Setup(w => w.IsValidWindow(handle)).Returns(true);
        _mockWindowService.Setup(w => w.MoveWindowToMonitor(handle, 1, "center", It.IsAny<bool>())).Returns(true);
        _mockWindowService.Setup(w => w.GetWindowInfo(handle))
            .Returns(TestDataFactory.CreateWindowInfo());
        var sut = CreateSut();

        // Act
        var result = sut.MoveWindowToMonitor(12345, 1);

        // Assert
        result.Success.Should().BeTrue();
        _mockWindowService.Verify(w => w.MoveWindowToMonitor(handle, 1, "center", It.IsAny<bool>()), Times.Once);
    }

    [Fact]
    public void MoveWindowToMonitor_PassesPositioning()
    {
        // Arrange
        var handle = new nint(12345);
        _mockWindowService.Setup(w => w.IsValidWindow(handle)).Returns(true);
        _mockWindowService.Setup(w => w.MoveWindowToMonitor(handle, 0, "maximize", It.IsAny<bool>())).Returns(true);
        _mockWindowService.Setup(w => w.GetWindowInfo(handle))
            .Returns(TestDataFactory.CreateWindowInfo());
        var sut = CreateSut();

        // Act
        sut.MoveWindowToMonitor(12345, 0, "maximize");

        // Assert
        _mockWindowService.Verify(w => w.MoveWindowToMonitor(handle, 0, "maximize", It.IsAny<bool>()), Times.Once);
    }

    #endregion
}
