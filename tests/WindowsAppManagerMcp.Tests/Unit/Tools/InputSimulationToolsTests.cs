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

        mock.Setup(s => s.MouseClick(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<MouseButton>(), It.IsAny<int>(),
                It.IsAny<IReadOnlyList<MouseModifier>>(), It.IsAny<InputTarget>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .Returns((int x, int y, MouseButton button, int _, IReadOnlyList<MouseModifier> _, InputTarget _, bool _, CancellationToken _) =>
                new ClickResult(Success: success, X: x, Y: y, ClickType: button.ToString(), Error: error, ErrorCode: errorCode));

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

        mock.Setup(s => s.TypeTextAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<InputTarget>(), It.IsAny<bool>(), It.IsAny<TextControlChars?>(), It.IsAny<TextInputMethod>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string text, int delay, InputTarget _, bool _, TextControlChars? _, TextInputMethod _, CancellationToken _) => new TypeTextResult(
                Success: success,
                Length: text?.Length ?? 0,
                CharactersTyped: text?.Length ?? 0,
                Error: error,
                ErrorCode: errorCode));

        mock.Setup(s => s.SendKeysAsync(It.IsAny<string>(), It.IsAny<InputTarget>(), It.IsAny<bool>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string keys, InputTarget _, bool _, int _, CancellationToken _) => new SendKeysResult(
                Success: success,
                Keys: keys,
                Error: error,
                ErrorCode: errorCode));

        return mock;
    }

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
        _mockInputService.Verify(s => s.TypeTextAsync("Hello World", 0, InputTarget.None, false, It.IsAny<TextControlChars?>(), It.IsAny<TextInputMethod>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task TypeText_WithCustomDelay_PassesClampedDelay()
    {
        // Arrange
        var sut = CreateSut();

        // Act
        await sut.TypeText("Hello", delayMs: 50);

        // Assert
        _mockInputService.Verify(s => s.TypeTextAsync("Hello", 50, InputTarget.None, false, It.IsAny<TextControlChars?>(), It.IsAny<TextInputMethod>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task TypeText_WithDelayBelowMinimum_ClampsTo0()
    {
        // Arrange
        var sut = CreateSut();

        // Act
        await sut.TypeText("Hello", delayMs: -10);

        // Assert
        _mockInputService.Verify(s => s.TypeTextAsync("Hello", 0, InputTarget.None, false, It.IsAny<TextControlChars?>(), It.IsAny<TextInputMethod>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task TypeText_WithDelayAboveMaximum_ClampsTo100()
    {
        // Arrange
        var sut = CreateSut();

        // Act
        await sut.TypeText("Hello", delayMs: 500);

        // Assert
        _mockInputService.Verify(s => s.TypeTextAsync("Hello", 100, InputTarget.None, false, It.IsAny<TextControlChars?>(), It.IsAny<TextInputMethod>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task TypeText_ReturnsServiceResult()
    {
        // Arrange
        var expectedResult = new TypeTextResult(true, 5, 5);
        _mockInputService.Setup(s => s.TypeTextAsync("Hello", 0, InputTarget.None, false, It.IsAny<TextControlChars?>(), It.IsAny<TextInputMethod>(), It.IsAny<CancellationToken>())).ReturnsAsync(expectedResult);
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
            Length: 0,
            CharactersTyped: 0,
            Error: "Text cannot be empty",
            ErrorCode: InputErrorCode.EmptyText);
        _mockInputService.Setup(s => s.TypeTextAsync("", 0, InputTarget.None, false, It.IsAny<TextControlChars?>(), It.IsAny<TextInputMethod>(), It.IsAny<CancellationToken>())).ReturnsAsync(errorResult);
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
        _mockInputService.Verify(s => s.SendKeysAsync("{ENTER}", InputTarget.None, false, It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(40, 40)]
    [InlineData(-5, 0)]
    [InlineData(60_000, InputSimulationService.MaxSendKeysDelayMs)]
    public async Task SendKeys_PassesDelayMs_ClampedToTheMaximum(int delayMs, int expected)
    {
        // Act
        await CreateSut().SendKeys("{DOWN 3}", delayMs: delayMs);

        // Assert (roadmap M4.1)
        _mockInputService.Verify(s => s.SendKeysAsync("{DOWN 3}", InputTarget.None, false, expected, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(TextControlChars.Unicode)]
    [InlineData(TextControlChars.Keys)]
    [InlineData(TextControlChars.ShiftEnter)]
    public async Task TypeText_PassesControlChars_AsGiven(TextControlChars? controlChars)
    {
        // Act
        await CreateSut().TypeText("a\nb", controlChars: controlChars);

        // Assert (roadmap M4.1): the service refuses a line break without it
        _mockInputService.Verify(s => s.TypeTextAsync("a\nb", 0, InputTarget.None, false, controlChars, It.IsAny<TextInputMethod>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task TypeText_DefaultsToAuto()
    {
        // Act (roadmap M4.4): auto types short text, as type_text always did, and pastes long text
        await CreateSut().TypeText("Hello");

        // Assert
        _mockInputService.Verify(s => s.TypeTextAsync("Hello", 0, InputTarget.None, false, null, TextInputMethod.Auto, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Theory]
    [InlineData(TextInputMethod.Unicode)]
    [InlineData(TextInputMethod.Keys)]
    [InlineData(TextInputMethod.Paste)]
    public async Task TypeText_PassesTheMethod_AsGiven(TextInputMethod method)
    {
        await CreateSut().TypeText("Hello", method: method);

        _mockInputService.Verify(s => s.TypeTextAsync("Hello", 0, InputTarget.None, false, null, method, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task TypeText_TheToolResult_HasTheLengthButNeverTheText()
    {
        // Arrange
        _mockInputService.Setup(s => s.TypeTextAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<InputTarget>(), It.IsAny<bool>(),
                It.IsAny<TextControlChars?>(), It.IsAny<TextInputMethod>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new TypeTextResult(true, 15, 15) { Method = "paste", ClipboardRestored = true });

        // Act
        var result = await CreateSut().TypeText("my secret words", method: TextInputMethod.Paste);

        // Assert
        var json = string.Concat(result.Content.OfType<ModelContextProtocol.Protocol.TextContentBlock>().Select(b => b.Text));
        json.Should().Contain("\"length\":15").And.Contain("\"method\":\"paste\"").And.Contain("\"clipboardRestored\":true");
        json.Should().NotContain("secret").And.NotContain("\"text\"");
    }

    [Fact]
    public async Task SendKeys_WithModifierKeys_PassesToService()
    {
        // Arrange
        var sut = CreateSut();

        // Act
        await sut.SendKeys("^c");

        // Assert
        _mockInputService.Verify(s => s.SendKeysAsync("^c", InputTarget.None, false, It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task SendKeys_ReturnsServiceResult()
    {
        // Arrange
        var expectedResult = new SendKeysResult(true, "{ENTER}");
        _mockInputService.Setup(s => s.SendKeysAsync("{ENTER}", InputTarget.None, false, It.IsAny<int>(), It.IsAny<CancellationToken>())).ReturnsAsync(expectedResult);
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
        _mockInputService.Setup(s => s.SendKeysAsync("{UNKNOWNKEY}", InputTarget.None, false, It.IsAny<int>(), It.IsAny<CancellationToken>())).ReturnsAsync(errorResult);
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
        _mockInputService.Verify(s => s.SendKeysAsync(keys, InputTarget.None, false, It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Once);
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
        await sut.MouseClick(1, 2, releaseHeldModifiers: true, cancellationToken: request.Token);
        sut.MouseMove(1, 2, releaseHeldModifiers: true, cancellationToken: request.Token);

        // Assert
        _mockInputService.Verify(s => s.MouseClick(1, 2, MouseButton.Primary, 1, It.IsAny<IReadOnlyList<MouseModifier>>(), InputTarget.None, true,
            request.Token), Times.Once);
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
        _mockInputService.Verify(s => s.TypeTextAsync("x", 0, InputTarget.None, true, null, It.IsAny<TextInputMethod>(), request.Token), Times.Once);
        _mockInputService.Verify(s => s.SendKeysAsync("{ENTER}", InputTarget.None, true, 0, request.Token), Times.Once);
    }

    #endregion
}
