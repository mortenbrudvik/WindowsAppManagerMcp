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

        mock.Setup(s => s.Click(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<InputTarget>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .Returns((int x, int y, InputTarget _, bool _, CancellationToken _) => new ClickResult(
                Success: success,
                X: x,
                Y: y,
                ClickType: "left",
                Error: error,
                ErrorCode: errorCode));

        mock.Setup(s => s.RightClick(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<InputTarget>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .Returns((int x, int y, InputTarget _, bool _, CancellationToken _) => new ClickResult(
                Success: success,
                X: x,
                Y: y,
                ClickType: "right",
                Error: error,
                ErrorCode: errorCode));

        mock.Setup(s => s.DoubleClick(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<InputTarget>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .Returns((int x, int y, InputTarget _, bool _, CancellationToken _) => new ClickResult(
                Success: success,
                X: x,
                Y: y,
                ClickType: "double",
                Error: error,
                ErrorCode: errorCode));

        mock.Setup(s => s.MoveMouse(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<InputTarget>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .Returns((int x, int y, InputTarget _, bool _, CancellationToken _) => new MouseMoveResult(
                Success: success,
                X: x,
                Y: y,
                Error: error,
                ErrorCode: errorCode));

        mock.Setup(s => s.IsValidScreenCoordinate(It.IsAny<int>(), It.IsAny<int>()))
            .Returns(true);

        mock.Setup(s => s.GetCursorPosition())
            .Returns((500, 500));

        mock.Setup(s => s.TypeTextAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<InputTarget>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string text, int delay, InputTarget _, bool _, CancellationToken _) => new TypeTextResult(
                Success: success,
                Text: text,
                CharactersTyped: text?.Length ?? 0,
                Error: error,
                ErrorCode: errorCode));

        mock.Setup(s => s.SendKeysAsync(It.IsAny<string>(), It.IsAny<InputTarget>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string keys, InputTarget _, bool _, CancellationToken _) => new SendKeysResult(
                Success: success,
                Keys: keys,
                Error: error,
                ErrorCode: errorCode));

        return mock;
    }

    #region Click Tests

    [Fact]
    public async Task Click_CallsServiceWithCorrectCoordinates()
    {
        // Arrange
        var sut = CreateSut();

        // Act
        await sut.Click(100, 200);

        // Assert
        _mockInputService.Verify(s => s.Click(100, 200, InputTarget.None, false, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Click_ReturnsServiceResult()
    {
        // Arrange
        var expectedResult = new ClickResult(true, 100, 200, "left");
        _mockInputService.Setup(s => s.Click(100, 200, InputTarget.None, false, It.IsAny<CancellationToken>())).Returns(expectedResult);
        var sut = CreateSut();

        // Act
        var result = await sut.Click(100, 200).Payload<ClickResult>();

        // Assert
        result.Should().Be(expectedResult);
    }

    [Fact]
    public async Task Click_WithNegativeCoordinates_PassesToService()
    {
        // Arrange
        var sut = CreateSut();

        // Act
        await sut.Click(-100, -200);

        // Assert
        _mockInputService.Verify(s => s.Click(-100, -200, InputTarget.None, false, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Click_WhenServiceReturnsError_ReturnsError()
    {
        // Arrange
        var errorResult = new ClickResult(
            Success: false,
            X: 5000,
            Y: 5000,
            ClickType: "left",
            Error: "Coordinates are outside screen bounds",
            ErrorCode: InputErrorCode.CoordinatesOutOfBounds);
        _mockInputService.Setup(s => s.Click(5000, 5000, InputTarget.None, false, It.IsAny<CancellationToken>())).Returns(errorResult);
        var sut = CreateSut();

        // Act
        var result = await sut.Click(5000, 5000).Payload<ClickResult>();

        // Assert
        result.Success.Should().BeFalse();
        result.ErrorCode.Should().Be(InputErrorCode.CoordinatesOutOfBounds);
    }

    #endregion

    #region RightClick Tests

    [Fact]
    public async Task RightClick_CallsServiceWithCorrectCoordinates()
    {
        // Arrange
        var sut = CreateSut();

        // Act
        await sut.RightClick(300, 400);

        // Assert
        _mockInputService.Verify(s => s.RightClick(300, 400, InputTarget.None, false, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task RightClick_ReturnsServiceResult()
    {
        // Arrange
        var expectedResult = new ClickResult(true, 300, 400, "right");
        _mockInputService.Setup(s => s.RightClick(300, 400, InputTarget.None, false, It.IsAny<CancellationToken>())).Returns(expectedResult);
        var sut = CreateSut();

        // Act
        var result = await sut.RightClick(300, 400).Payload<ClickResult>();

        // Assert
        result.Should().Be(expectedResult);
    }

    [Fact]
    public async Task RightClick_WhenServiceReturnsError_ReturnsError()
    {
        // Arrange
        var errorResult = new ClickResult(
            Success: false,
            X: 300,
            Y: 400,
            ClickType: "right",
            Error: "SendInput failed",
            ErrorCode: InputErrorCode.SendInputFailed);
        _mockInputService.Setup(s => s.RightClick(300, 400, InputTarget.None, false, It.IsAny<CancellationToken>())).Returns(errorResult);
        var sut = CreateSut();

        // Act
        var result = await sut.RightClick(300, 400).Payload<ClickResult>();

        // Assert
        result.Success.Should().BeFalse();
        result.ErrorCode.Should().Be(InputErrorCode.SendInputFailed);
    }

    #endregion

    #region DoubleClick Tests

    [Fact]
    public async Task DoubleClick_CallsServiceWithCorrectCoordinates()
    {
        // Arrange
        var sut = CreateSut();

        // Act
        await sut.DoubleClick(500, 600);

        // Assert
        _mockInputService.Verify(s => s.DoubleClick(500, 600, InputTarget.None, false, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(100)]
    [InlineData(1000)]
    public async Task DoubleClick_DelayMs_IsAcceptedAndIgnored(int delayMs)
    {
        // Arrange - roadmap M4.2: both clicks go in one batch, so there is no delay to pass; the parameter stays declared, so
        // a call that passes it is not refused as an undeclared argument
        var sut = CreateSut();

        // Act
        await sut.DoubleClick(500, 600, delayMs: delayMs);

        // Assert
        _mockInputService.Verify(s => s.DoubleClick(500, 600, InputTarget.None, false, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task DoubleClick_ReturnsServiceResult()
    {
        // Arrange
        var expectedResult = new ClickResult(true, 500, 600, "double");
        _mockInputService.Setup(s => s.DoubleClick(500, 600, InputTarget.None, false, It.IsAny<CancellationToken>())).Returns(expectedResult);
        var sut = CreateSut();

        // Act
        var result = await sut.DoubleClick(500, 600).Payload<ClickResult>();

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
        _mockInputService.Verify(s => s.MoveMouse(700, 800, InputTarget.None, false, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public void MouseMove_ReturnsServiceResult()
    {
        // Arrange
        var expectedResult = new MouseMoveResult(true, 700, 800);
        _mockInputService.Setup(s => s.MoveMouse(700, 800, InputTarget.None, false, It.IsAny<CancellationToken>())).Returns(expectedResult);
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
        _mockInputService.Setup(s => s.MoveMouse(10000, 10000, InputTarget.None, false, It.IsAny<CancellationToken>())).Returns(errorResult);
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
        _mockInputService.Verify(s => s.MoveMouse(0, 0, InputTarget.None, false, It.IsAny<CancellationToken>()), Times.Once);
    }

    #endregion

    #region Result Coordinate Verification Tests

    [Theory]
    [InlineData(0, 0)]
    [InlineData(100, 200)]
    [InlineData(1920, 1080)]
    [InlineData(-100, -200)]
    public async Task Click_ResultContainsCorrectCoordinates(int x, int y)
    {
        // Arrange
        var sut = CreateSut();

        // Act
        var result = await sut.Click(x, y).Payload<ClickResult>();

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
    public async Task TypeText_CallsServiceWithCorrectParameters()
    {
        // Arrange
        var sut = CreateSut();

        // Act
        await sut.TypeText("Hello World");

        // Assert
        _mockInputService.Verify(s => s.TypeTextAsync("Hello World", 0, InputTarget.None, false, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task TypeText_WithCustomDelay_PassesClampedDelay()
    {
        // Arrange
        var sut = CreateSut();

        // Act
        await sut.TypeText("Hello", delayMs: 50);

        // Assert
        _mockInputService.Verify(s => s.TypeTextAsync("Hello", 50, InputTarget.None, false, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task TypeText_WithDelayBelowMinimum_ClampsTo0()
    {
        // Arrange
        var sut = CreateSut();

        // Act
        await sut.TypeText("Hello", delayMs: -10);

        // Assert
        _mockInputService.Verify(s => s.TypeTextAsync("Hello", 0, InputTarget.None, false, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task TypeText_WithDelayAboveMaximum_ClampsTo100()
    {
        // Arrange
        var sut = CreateSut();

        // Act
        await sut.TypeText("Hello", delayMs: 500);

        // Assert
        _mockInputService.Verify(s => s.TypeTextAsync("Hello", 100, InputTarget.None, false, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task TypeText_ReturnsServiceResult()
    {
        // Arrange
        var expectedResult = new TypeTextResult(true, "Hello", 5);
        _mockInputService.Setup(s => s.TypeTextAsync("Hello", 0, InputTarget.None, false, It.IsAny<CancellationToken>())).ReturnsAsync(expectedResult);
        var sut = CreateSut();

        // Act
        var result = await sut.TypeText("Hello").Payload<TypeTextResult>();

        // Assert
        result.Should().Be(expectedResult);
    }

    [Fact]
    public async Task TypeText_WhenServiceReturnsError_ReturnsError()
    {
        // Arrange
        var errorResult = new TypeTextResult(
            Success: false,
            Text: "",
            CharactersTyped: 0,
            Error: "Text cannot be empty",
            ErrorCode: InputErrorCode.EmptyText);
        _mockInputService.Setup(s => s.TypeTextAsync("", 0, InputTarget.None, false, It.IsAny<CancellationToken>())).ReturnsAsync(errorResult);
        var sut = CreateSut();

        // Act
        var result = await sut.TypeText("").Payload<TypeTextResult>();

        // Assert
        result.Success.Should().BeFalse();
        result.ErrorCode.Should().Be(InputErrorCode.EmptyText);
    }

    #endregion

    #region SendKeys Tests

    [Fact]
    public async Task SendKeys_CallsServiceWithCorrectKeys()
    {
        // Arrange
        var sut = CreateSut();

        // Act
        await sut.SendKeys("{ENTER}");

        // Assert
        _mockInputService.Verify(s => s.SendKeysAsync("{ENTER}", InputTarget.None, false, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task SendKeys_WithModifierKeys_PassesToService()
    {
        // Arrange
        var sut = CreateSut();

        // Act
        await sut.SendKeys("^c");

        // Assert
        _mockInputService.Verify(s => s.SendKeysAsync("^c", InputTarget.None, false, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task SendKeys_ReturnsServiceResult()
    {
        // Arrange
        var expectedResult = new SendKeysResult(true, "{ENTER}");
        _mockInputService.Setup(s => s.SendKeysAsync("{ENTER}", InputTarget.None, false, It.IsAny<CancellationToken>())).ReturnsAsync(expectedResult);
        var sut = CreateSut();

        // Act
        var result = await sut.SendKeys("{ENTER}").Payload<SendKeysResult>();

        // Assert
        result.Should().Be(expectedResult);
    }

    [Fact]
    public async Task SendKeys_WhenServiceReturnsError_ReturnsError()
    {
        // Arrange
        var errorResult = new SendKeysResult(
            Success: false,
            Keys: "{UNKNOWNKEY}",
            Error: "Unknown key: UNKNOWNKEY",
            ErrorCode: InputErrorCode.UnknownKey);
        _mockInputService.Setup(s => s.SendKeysAsync("{UNKNOWNKEY}", InputTarget.None, false, It.IsAny<CancellationToken>())).ReturnsAsync(errorResult);
        var sut = CreateSut();

        // Act
        var result = await sut.SendKeys("{UNKNOWNKEY}").Payload<SendKeysResult>();

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
    public async Task SendKeys_WithVariousKeyFormats_PassesToService(string keys)
    {
        // Arrange
        var sut = CreateSut();

        // Act
        await sut.SendKeys(keys);

        // Assert
        _mockInputService.Verify(s => s.SendKeysAsync(keys, InputTarget.None, false, It.IsAny<CancellationToken>()), Times.Once);
    }

    #endregion

    #region Session safety parameters (roadmap M1.10)

    [Fact]
    public async Task MouseTools_PassReleaseHeldModifiersAndTheRequestToken()
    {
        // Arrange
        using var request = new CancellationTokenSource();
        var sut = CreateSut();

        // Act
        await sut.Click(1, 2, releaseHeldModifiers: true, cancellationToken: request.Token);
        await sut.RightClick(1, 2, releaseHeldModifiers: true, cancellationToken: request.Token);
        await sut.DoubleClick(1, 2, releaseHeldModifiers: true, cancellationToken: request.Token);
        sut.MouseMove(1, 2, releaseHeldModifiers: true, cancellationToken: request.Token);

        // Assert
        _mockInputService.Verify(s => s.Click(1, 2, InputTarget.None, true, request.Token), Times.Once);
        _mockInputService.Verify(s => s.RightClick(1, 2, InputTarget.None, true, request.Token), Times.Once);
        _mockInputService.Verify(s => s.DoubleClick(1, 2, InputTarget.None, true, request.Token), Times.Once);
        _mockInputService.Verify(s => s.MoveMouse(1, 2, InputTarget.None, true, request.Token), Times.Once);
    }

    [Fact]
    public async Task KeyboardTools_PassReleaseHeldModifiersAndTheRequestToken()
    {
        // Arrange - cancelling the MCP request must reach the typing loop
        using var request = new CancellationTokenSource();
        var sut = CreateSut();

        // Act
        await sut.TypeText("x", releaseHeldModifiers: true, cancellationToken: request.Token);
        await sut.SendKeys("{ENTER}", releaseHeldModifiers: true, cancellationToken: request.Token);

        // Assert
        _mockInputService.Verify(s => s.TypeTextAsync("x", 0, InputTarget.None, true, request.Token), Times.Once);
        _mockInputService.Verify(s => s.SendKeysAsync("{ENTER}", InputTarget.None, true, request.Token), Times.Once);
    }

    #endregion
}
