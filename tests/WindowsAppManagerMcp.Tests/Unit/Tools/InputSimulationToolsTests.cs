namespace WindowsAppManagerMcp.Tests.Unit.Tools;

using WindowsAppManagerMcp.Tools;

/// <summary>
/// Unit tests for InputSimulationTools.
/// Tests tool parameter passing and result forwarding.
/// </summary>
public class InputSimulationToolsTests
{
    private readonly Mock<IInputSimulationService> _mockInputService;

    public InputSimulationToolsTests()
    {
        _mockInputService = CreateMockInputSimulationService();
    }

    private InputSimulationTools CreateSut() =>
        new InputSimulationTools(_mockInputService.Object);

    private static Mock<IInputSimulationService> CreateMockInputSimulationService(
        bool success = true,
        string? error = null,
        InputErrorCode errorCode = InputErrorCode.None)
    {
        var mock = new Mock<IInputSimulationService>();

        mock.Setup(s => s.Click(It.IsAny<int>(), It.IsAny<int>()))
            .Returns((int x, int y) => new ClickResult(
                Success: success,
                X: x,
                Y: y,
                ClickType: "left",
                Error: error,
                ErrorCode: errorCode));

        mock.Setup(s => s.RightClick(It.IsAny<int>(), It.IsAny<int>()))
            .Returns((int x, int y) => new ClickResult(
                Success: success,
                X: x,
                Y: y,
                ClickType: "right",
                Error: error,
                ErrorCode: errorCode));

        mock.Setup(s => s.DoubleClick(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<int>()))
            .Returns((int x, int y, int delay) => new ClickResult(
                Success: success,
                X: x,
                Y: y,
                ClickType: "double",
                Error: error,
                ErrorCode: errorCode));

        mock.Setup(s => s.MoveMouse(It.IsAny<int>(), It.IsAny<int>()))
            .Returns((int x, int y) => new MouseMoveResult(
                Success: success,
                X: x,
                Y: y,
                Error: error,
                ErrorCode: errorCode));

        mock.Setup(s => s.IsValidScreenCoordinate(It.IsAny<int>(), It.IsAny<int>()))
            .Returns(true);

        mock.Setup(s => s.GetCursorPosition())
            .Returns((500, 500));

        mock.Setup(s => s.TypeText(It.IsAny<string>(), It.IsAny<int>()))
            .Returns((string text, int delay) => new TypeTextResult(
                Success: success,
                Text: text,
                CharactersTyped: text?.Length ?? 0,
                Error: error,
                ErrorCode: errorCode));

        mock.Setup(s => s.SendKeys(It.IsAny<string>()))
            .Returns((string keys) => new SendKeysResult(
                Success: success,
                Keys: keys,
                Error: error,
                ErrorCode: errorCode));

        return mock;
    }

    #region Click Tests

    [Fact]
    public void Click_CallsServiceWithCorrectCoordinates()
    {
        // Arrange
        var sut = CreateSut();

        // Act
        sut.Click(100, 200);

        // Assert
        _mockInputService.Verify(s => s.Click(100, 200), Times.Once);
    }

    [Fact]
    public void Click_ReturnsServiceResult()
    {
        // Arrange
        var expectedResult = new ClickResult(true, 100, 200, "left");
        _mockInputService.Setup(s => s.Click(100, 200)).Returns(expectedResult);
        var sut = CreateSut();

        // Act
        var result = sut.Click(100, 200);

        // Assert
        result.Should().Be(expectedResult);
    }

    [Fact]
    public void Click_WithNegativeCoordinates_PassesToService()
    {
        // Arrange
        var sut = CreateSut();

        // Act
        sut.Click(-100, -200);

        // Assert
        _mockInputService.Verify(s => s.Click(-100, -200), Times.Once);
    }

    [Fact]
    public void Click_WhenServiceReturnsError_ReturnsError()
    {
        // Arrange
        var errorResult = new ClickResult(
            Success: false,
            X: 5000,
            Y: 5000,
            ClickType: "left",
            Error: "Coordinates are outside screen bounds",
            ErrorCode: InputErrorCode.CoordinatesOutOfBounds);
        _mockInputService.Setup(s => s.Click(5000, 5000)).Returns(errorResult);
        var sut = CreateSut();

        // Act
        var result = sut.Click(5000, 5000);

        // Assert
        result.Success.Should().BeFalse();
        result.ErrorCode.Should().Be(InputErrorCode.CoordinatesOutOfBounds);
    }

    #endregion

    #region RightClick Tests

    [Fact]
    public void RightClick_CallsServiceWithCorrectCoordinates()
    {
        // Arrange
        var sut = CreateSut();

        // Act
        sut.RightClick(300, 400);

        // Assert
        _mockInputService.Verify(s => s.RightClick(300, 400), Times.Once);
    }

    [Fact]
    public void RightClick_ReturnsServiceResult()
    {
        // Arrange
        var expectedResult = new ClickResult(true, 300, 400, "right");
        _mockInputService.Setup(s => s.RightClick(300, 400)).Returns(expectedResult);
        var sut = CreateSut();

        // Act
        var result = sut.RightClick(300, 400);

        // Assert
        result.Should().Be(expectedResult);
    }

    [Fact]
    public void RightClick_WhenServiceReturnsError_ReturnsError()
    {
        // Arrange
        var errorResult = new ClickResult(
            Success: false,
            X: 300,
            Y: 400,
            ClickType: "right",
            Error: "SendInput failed",
            ErrorCode: InputErrorCode.SendInputFailed);
        _mockInputService.Setup(s => s.RightClick(300, 400)).Returns(errorResult);
        var sut = CreateSut();

        // Act
        var result = sut.RightClick(300, 400);

        // Assert
        result.Success.Should().BeFalse();
        result.ErrorCode.Should().Be(InputErrorCode.SendInputFailed);
    }

    #endregion

    #region DoubleClick Tests

    [Fact]
    public void DoubleClick_CallsServiceWithCorrectCoordinates()
    {
        // Arrange
        var sut = CreateSut();

        // Act
        sut.DoubleClick(500, 600);

        // Assert
        _mockInputService.Verify(s => s.DoubleClick(500, 600, 50), Times.Once);
    }

    [Fact]
    public void DoubleClick_WithCustomDelay_PassesClampedDelay()
    {
        // Arrange
        var sut = CreateSut();

        // Act
        sut.DoubleClick(500, 600, delayMs: 100);

        // Assert
        _mockInputService.Verify(s => s.DoubleClick(500, 600, 100), Times.Once);
    }

    [Fact]
    public void DoubleClick_WithDelayBelowMinimum_ClampsTo10()
    {
        // Arrange
        var sut = CreateSut();

        // Act
        sut.DoubleClick(500, 600, delayMs: 1);

        // Assert
        _mockInputService.Verify(s => s.DoubleClick(500, 600, 10), Times.Once);
    }

    [Fact]
    public void DoubleClick_WithDelayAboveMaximum_ClampsTo500()
    {
        // Arrange
        var sut = CreateSut();

        // Act
        sut.DoubleClick(500, 600, delayMs: 1000);

        // Assert
        _mockInputService.Verify(s => s.DoubleClick(500, 600, 500), Times.Once);
    }

    [Fact]
    public void DoubleClick_WithNegativeDelay_ClampsTo10()
    {
        // Arrange
        var sut = CreateSut();

        // Act
        sut.DoubleClick(500, 600, delayMs: -50);

        // Assert
        _mockInputService.Verify(s => s.DoubleClick(500, 600, 10), Times.Once);
    }

    [Fact]
    public void DoubleClick_ReturnsServiceResult()
    {
        // Arrange
        var expectedResult = new ClickResult(true, 500, 600, "double");
        _mockInputService.Setup(s => s.DoubleClick(500, 600, 50)).Returns(expectedResult);
        var sut = CreateSut();

        // Act
        var result = sut.DoubleClick(500, 600);

        // Assert
        result.Should().Be(expectedResult);
    }

    #endregion

    #region MouseMove Tests

    [Fact]
    public void MouseMove_CallsServiceWithCorrectCoordinates()
    {
        // Arrange
        var sut = CreateSut();

        // Act
        sut.MouseMove(700, 800);

        // Assert
        _mockInputService.Verify(s => s.MoveMouse(700, 800), Times.Once);
    }

    [Fact]
    public void MouseMove_ReturnsServiceResult()
    {
        // Arrange
        var expectedResult = new MouseMoveResult(true, 700, 800);
        _mockInputService.Setup(s => s.MoveMouse(700, 800)).Returns(expectedResult);
        var sut = CreateSut();

        // Act
        var result = sut.MouseMove(700, 800);

        // Assert
        result.Should().Be(expectedResult);
    }

    [Fact]
    public void MouseMove_WhenServiceReturnsError_ReturnsError()
    {
        // Arrange
        var errorResult = new MouseMoveResult(
            Success: false,
            X: 10000,
            Y: 10000,
            Error: "Coordinates are outside screen bounds",
            ErrorCode: InputErrorCode.CoordinatesOutOfBounds);
        _mockInputService.Setup(s => s.MoveMouse(10000, 10000)).Returns(errorResult);
        var sut = CreateSut();

        // Act
        var result = sut.MouseMove(10000, 10000);

        // Assert
        result.Success.Should().BeFalse();
        result.ErrorCode.Should().Be(InputErrorCode.CoordinatesOutOfBounds);
    }

    [Fact]
    public void MouseMove_WithZeroCoordinates_PassesToService()
    {
        // Arrange
        var sut = CreateSut();

        // Act
        sut.MouseMove(0, 0);

        // Assert
        _mockInputService.Verify(s => s.MoveMouse(0, 0), Times.Once);
    }

    #endregion

    #region Result Coordinate Verification Tests

    [Theory]
    [InlineData(0, 0)]
    [InlineData(100, 200)]
    [InlineData(1920, 1080)]
    [InlineData(-100, -200)]
    public void Click_ResultContainsCorrectCoordinates(int x, int y)
    {
        // Arrange
        var sut = CreateSut();

        // Act
        var result = sut.Click(x, y);

        // Assert
        result.X.Should().Be(x);
        result.Y.Should().Be(y);
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(100, 200)]
    [InlineData(1920, 1080)]
    public void MouseMove_ResultContainsCorrectCoordinates(int x, int y)
    {
        // Arrange
        var sut = CreateSut();

        // Act
        var result = sut.MouseMove(x, y);

        // Assert
        result.X.Should().Be(x);
        result.Y.Should().Be(y);
    }

    #endregion

    #region TypeText Tests

    [Fact]
    public void TypeText_CallsServiceWithCorrectParameters()
    {
        // Arrange
        var sut = CreateSut();

        // Act
        sut.TypeText("Hello World");

        // Assert
        _mockInputService.Verify(s => s.TypeText("Hello World", 0), Times.Once);
    }

    [Fact]
    public void TypeText_WithCustomDelay_PassesClampedDelay()
    {
        // Arrange
        var sut = CreateSut();

        // Act
        sut.TypeText("Hello", delayMs: 50);

        // Assert
        _mockInputService.Verify(s => s.TypeText("Hello", 50), Times.Once);
    }

    [Fact]
    public void TypeText_WithDelayBelowMinimum_ClampsTo0()
    {
        // Arrange
        var sut = CreateSut();

        // Act
        sut.TypeText("Hello", delayMs: -10);

        // Assert
        _mockInputService.Verify(s => s.TypeText("Hello", 0), Times.Once);
    }

    [Fact]
    public void TypeText_WithDelayAboveMaximum_ClampsTo100()
    {
        // Arrange
        var sut = CreateSut();

        // Act
        sut.TypeText("Hello", delayMs: 500);

        // Assert
        _mockInputService.Verify(s => s.TypeText("Hello", 100), Times.Once);
    }

    [Fact]
    public void TypeText_ReturnsServiceResult()
    {
        // Arrange
        var expectedResult = new TypeTextResult(true, "Hello", 5);
        _mockInputService.Setup(s => s.TypeText("Hello", 0)).Returns(expectedResult);
        var sut = CreateSut();

        // Act
        var result = sut.TypeText("Hello");

        // Assert
        result.Should().Be(expectedResult);
    }

    [Fact]
    public void TypeText_WhenServiceReturnsError_ReturnsError()
    {
        // Arrange
        var errorResult = new TypeTextResult(
            Success: false,
            Text: "",
            CharactersTyped: 0,
            Error: "Text cannot be empty",
            ErrorCode: InputErrorCode.EmptyText);
        _mockInputService.Setup(s => s.TypeText("", 0)).Returns(errorResult);
        var sut = CreateSut();

        // Act
        var result = sut.TypeText("");

        // Assert
        result.Success.Should().BeFalse();
        result.ErrorCode.Should().Be(InputErrorCode.EmptyText);
    }

    #endregion

    #region SendKeys Tests

    [Fact]
    public void SendKeys_CallsServiceWithCorrectKeys()
    {
        // Arrange
        var sut = CreateSut();

        // Act
        sut.SendKeys("{ENTER}");

        // Assert
        _mockInputService.Verify(s => s.SendKeys("{ENTER}"), Times.Once);
    }

    [Fact]
    public void SendKeys_WithModifierKeys_PassesToService()
    {
        // Arrange
        var sut = CreateSut();

        // Act
        sut.SendKeys("^c");

        // Assert
        _mockInputService.Verify(s => s.SendKeys("^c"), Times.Once);
    }

    [Fact]
    public void SendKeys_ReturnsServiceResult()
    {
        // Arrange
        var expectedResult = new SendKeysResult(true, "{ENTER}");
        _mockInputService.Setup(s => s.SendKeys("{ENTER}")).Returns(expectedResult);
        var sut = CreateSut();

        // Act
        var result = sut.SendKeys("{ENTER}");

        // Assert
        result.Should().Be(expectedResult);
    }

    [Fact]
    public void SendKeys_WhenServiceReturnsError_ReturnsError()
    {
        // Arrange
        var errorResult = new SendKeysResult(
            Success: false,
            Keys: "{UNKNOWNKEY}",
            Error: "Unknown key: UNKNOWNKEY",
            ErrorCode: InputErrorCode.UnknownKey);
        _mockInputService.Setup(s => s.SendKeys("{UNKNOWNKEY}")).Returns(errorResult);
        var sut = CreateSut();

        // Act
        var result = sut.SendKeys("{UNKNOWNKEY}");

        // Assert
        result.Success.Should().BeFalse();
        result.ErrorCode.Should().Be(InputErrorCode.UnknownKey);
    }

    [Theory]
    [InlineData("^a")]
    [InlineData("%{F4}")]
    [InlineData("+{HOME}")]
    [InlineData("{TAB}")]
    [InlineData("{F1}")]
    public void SendKeys_WithVariousKeyFormats_PassesToService(string keys)
    {
        // Arrange
        var sut = CreateSut();

        // Act
        sut.SendKeys(keys);

        // Assert
        _mockInputService.Verify(s => s.SendKeys(keys), Times.Once);
    }

    #endregion
}
