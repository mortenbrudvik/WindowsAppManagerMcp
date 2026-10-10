namespace WindowsAppManagerMcp.Tests.Unit.Services;

/// <summary>
/// Unit tests for InputSimulationService.
/// Tests coordinate validation, key parsing, and the input sent to the native layer.
/// The native input wrapper is mocked so no real mouse or keyboard input reaches the desktop.
/// </summary>
public class InputSimulationServiceTests
{
    // Win32 input constants (NativeEnums is internal to the server assembly)
    private const uint KeyDown = 0x0000;
    private const uint ExtendedKey = 0x0001;
    private const uint KeyUp = 0x0002;
    private const uint LeftDown = 0x0002;
    private const uint LeftUp = 0x0004;
    private const uint RightDown = 0x0008;
    private const uint RightUp = 0x0010;
    private const ushort VkReturn = 0x0D;
    private const ushort VkControl = 0x11;
    private const ushort VkMenu = 0x12;
    private const ushort VkUp = 0x26;
    private const ushort VkC = 0x43;
    private const ushort VkF4 = 0x73;

    private readonly Mock<IMonitorService> _mockMonitorService;
    private readonly Mock<INativeInputWrapper> _mockInputWrapper;
    private readonly Mock<IInputTargetGuard> _guard = TestDataFactory.CreateAllowingInputGuard();
    private readonly List<InputEvent> _sentEvents = new();

    public InputSimulationServiceTests()
    {
        _mockMonitorService = TestDataFactory.CreateMockMonitorService();
        _mockInputWrapper = new Mock<INativeInputWrapper>();
        _mockInputWrapper.Setup(w => w.SetCursorPos(It.IsAny<int>(), It.IsAny<int>())).Returns(true);
        _mockInputWrapper.Setup(w => w.SendInput(It.IsAny<IReadOnlyList<InputEvent>>()))
            .Callback((IReadOnlyList<InputEvent> events) => _sentEvents.AddRange(events))
            .Returns((IReadOnlyList<InputEvent> events) => (uint)events.Count);
    }

    private InputSimulationService CreateSut() =>
        new InputSimulationService(_mockMonitorService.Object, _mockInputWrapper.Object, _guard.Object);

    private void SetupSingleMonitor()
    {
        var monitor = TestDataFactory.CreateMonitorInfo(
            boundsX: 0, boundsY: 0, boundsWidth: 1920, boundsHeight: 1080);
        _mockMonitorService.Setup(m => m.GetAllMonitors())
            .Returns(new List<MonitorInfo> { monitor });
    }

    private void SetupSendInputFailure()
    {
        _mockInputWrapper.Setup(w => w.SendInput(It.IsAny<IReadOnlyList<InputEvent>>())).Returns(0u);
    }

    private void VerifyNoInputSent()
    {
        _mockInputWrapper.Verify(w => w.SetCursorPos(It.IsAny<int>(), It.IsAny<int>()), Times.Never);
        _mockInputWrapper.Verify(w => w.SendInput(It.IsAny<IReadOnlyList<InputEvent>>()), Times.Never);
    }

    #region IsValidScreenCoordinate Tests

    [Fact]
    public void IsValidScreenCoordinate_WithCoordinatesInsideMonitor_ReturnsTrue()
    {
        // Arrange
        var monitor = TestDataFactory.CreateMonitorInfo(
            boundsX: 0, boundsY: 0, boundsWidth: 1920, boundsHeight: 1080);
        _mockMonitorService.Setup(m => m.GetAllMonitors())
            .Returns(new List<MonitorInfo> { monitor });
        var sut = CreateSut();

        // Act
        var result = sut.IsValidScreenCoordinate(960, 540);

        // Assert
        result.Should().BeTrue();
    }

    [Fact]
    public void IsValidScreenCoordinate_WithCoordinatesAtTopLeftCorner_ReturnsTrue()
    {
        // Arrange
        var monitor = TestDataFactory.CreateMonitorInfo(
            boundsX: 0, boundsY: 0, boundsWidth: 1920, boundsHeight: 1080);
        _mockMonitorService.Setup(m => m.GetAllMonitors())
            .Returns(new List<MonitorInfo> { monitor });
        var sut = CreateSut();

        // Act
        var result = sut.IsValidScreenCoordinate(0, 0);

        // Assert
        result.Should().BeTrue();
    }

    [Fact]
    public void IsValidScreenCoordinate_WithCoordinatesAtBottomRightEdge_ReturnsFalse()
    {
        // Arrange - coordinate at exact edge should be outside
        var monitor = TestDataFactory.CreateMonitorInfo(
            boundsX: 0, boundsY: 0, boundsWidth: 1920, boundsHeight: 1080);
        _mockMonitorService.Setup(m => m.GetAllMonitors())
            .Returns(new List<MonitorInfo> { monitor });
        var sut = CreateSut();

        // Act - x=1920 is outside since valid range is 0-1919
        var result = sut.IsValidScreenCoordinate(1920, 1080);

        // Assert
        result.Should().BeFalse();
    }

    [Fact]
    public void IsValidScreenCoordinate_WithCoordinatesJustInsideEdge_ReturnsTrue()
    {
        // Arrange
        var monitor = TestDataFactory.CreateMonitorInfo(
            boundsX: 0, boundsY: 0, boundsWidth: 1920, boundsHeight: 1080);
        _mockMonitorService.Setup(m => m.GetAllMonitors())
            .Returns(new List<MonitorInfo> { monitor });
        var sut = CreateSut();

        // Act - x=1919 is inside since valid range is 0-1919
        var result = sut.IsValidScreenCoordinate(1919, 1079);

        // Assert
        result.Should().BeTrue();
    }

    [Fact]
    public void IsValidScreenCoordinate_WithNegativeCoordinates_ReturnsFalseForStandardMonitor()
    {
        // Arrange
        var monitor = TestDataFactory.CreateMonitorInfo(
            boundsX: 0, boundsY: 0, boundsWidth: 1920, boundsHeight: 1080);
        _mockMonitorService.Setup(m => m.GetAllMonitors())
            .Returns(new List<MonitorInfo> { monitor });
        var sut = CreateSut();

        // Act
        var result = sut.IsValidScreenCoordinate(-100, -100);

        // Assert
        result.Should().BeFalse();
    }

    [Fact]
    public void IsValidScreenCoordinate_WithNegativeCoordinates_ReturnsTrueForLeftOfPrimaryMonitor()
    {
        // Arrange - monitor positioned to the left of primary has negative X bounds
        var leftMonitor = TestDataFactory.CreateMonitorInfo(
            index: 1, deviceName: @"\\.\DISPLAY2", isPrimary: false,
            boundsX: -1920, boundsY: 0, boundsWidth: 1920, boundsHeight: 1080);
        var primaryMonitor = TestDataFactory.CreateMonitorInfo(
            index: 0, deviceName: @"\\.\DISPLAY1", isPrimary: true,
            boundsX: 0, boundsY: 0, boundsWidth: 1920, boundsHeight: 1080);
        _mockMonitorService.Setup(m => m.GetAllMonitors())
            .Returns(new List<MonitorInfo> { primaryMonitor, leftMonitor });
        var sut = CreateSut();

        // Act
        var result = sut.IsValidScreenCoordinate(-960, 540);

        // Assert
        result.Should().BeTrue();
    }

    [Fact]
    public void IsValidScreenCoordinate_WithCoordinatesOnSecondMonitor_ReturnsTrue()
    {
        // Arrange
        var monitors = TestDataFactory.CreateDualMonitorSetup();
        _mockMonitorService.Setup(m => m.GetAllMonitors()).Returns(monitors);
        var sut = CreateSut();

        // Act - coordinate on second monitor (starts at x=1920)
        var result = sut.IsValidScreenCoordinate(2500, 500);

        // Assert
        result.Should().BeTrue();
    }

    [Fact]
    public void IsValidScreenCoordinate_WithCoordinatesBetweenMonitors_ReturnsFalse()
    {
        // Arrange - gap between monitors
        var monitor1 = TestDataFactory.CreateMonitorInfo(
            boundsX: 0, boundsY: 0, boundsWidth: 1920, boundsHeight: 1080);
        var monitor2 = TestDataFactory.CreateMonitorInfo(
            index: 1, boundsX: 2000, boundsY: 0, boundsWidth: 1920, boundsHeight: 1080);
        _mockMonitorService.Setup(m => m.GetAllMonitors())
            .Returns(new List<MonitorInfo> { monitor1, monitor2 });
        var sut = CreateSut();

        // Act - coordinate in the gap
        var result = sut.IsValidScreenCoordinate(1950, 500);

        // Assert
        result.Should().BeFalse();
    }

    #endregion

    #region Click Tests

    [Fact]
    public void Click_WithInvalidCoordinates_ReturnsError()
    {
        // Arrange
        SetupSingleMonitor();
        var sut = CreateSut();

        // Act
        var result = sut.Click(5000, 5000);

        // Assert
        result.Success.Should().BeFalse();
        result.ErrorCode.Should().Be(InputErrorCode.CoordinatesOutOfBounds);
        result.Error.Should().Contain("outside screen bounds");
        result.ClickType.Should().Be("left");
        VerifyNoInputSent();
    }

    [Fact]
    public void Click_WithValidCoordinates_MovesCursorAndSendsLeftClick()
    {
        // Arrange
        SetupSingleMonitor();
        var sut = CreateSut();

        // Act
        var result = sut.Click(500, 300);

        // Assert
        result.Success.Should().BeTrue();
        result.X.Should().Be(500);
        result.Y.Should().Be(300);
        result.ClickType.Should().Be("left");
        _mockInputWrapper.Verify(w => w.SetCursorPos(500, 300), Times.Once);
        _sentEvents.Should().Equal(InputEvent.Mouse(LeftDown), InputEvent.Mouse(LeftUp));
    }

    [Fact]
    public void Click_WhenSetCursorPosFails_ReturnsSendInputFailed()
    {
        // Arrange
        SetupSingleMonitor();
        _mockInputWrapper.Setup(w => w.SetCursorPos(It.IsAny<int>(), It.IsAny<int>())).Returns(false);
        var sut = CreateSut();

        // Act
        var result = sut.Click(500, 300);

        // Assert
        result.Success.Should().BeFalse();
        result.ErrorCode.Should().Be(InputErrorCode.SendInputFailed);
        _sentEvents.Should().BeEmpty();
    }

    [Fact]
    public void Click_WhenSendInputFails_ReturnsSendInputFailed()
    {
        // Arrange
        SetupSingleMonitor();
        SetupSendInputFailure();
        var sut = CreateSut();

        // Act
        var result = sut.Click(500, 300);

        // Assert
        result.Success.Should().BeFalse();
        result.ErrorCode.Should().Be(InputErrorCode.SendInputFailed);
    }

    #endregion

    #region RightClick Tests

    [Fact]
    public void RightClick_WithInvalidCoordinates_ReturnsError()
    {
        // Arrange
        SetupSingleMonitor();
        var sut = CreateSut();

        // Act
        var result = sut.RightClick(-100, -100);

        // Assert
        result.Success.Should().BeFalse();
        result.ErrorCode.Should().Be(InputErrorCode.CoordinatesOutOfBounds);
        result.ClickType.Should().Be("right");
        VerifyNoInputSent();
    }

    [Fact]
    public void RightClick_WithValidCoordinates_MovesCursorAndSendsRightClick()
    {
        // Arrange
        SetupSingleMonitor();
        var sut = CreateSut();

        // Act
        var result = sut.RightClick(750, 400);

        // Assert
        result.Success.Should().BeTrue();
        result.X.Should().Be(750);
        result.Y.Should().Be(400);
        result.ClickType.Should().Be("right");
        _mockInputWrapper.Verify(w => w.SetCursorPos(750, 400), Times.Once);
        _sentEvents.Should().Equal(InputEvent.Mouse(RightDown), InputEvent.Mouse(RightUp));
    }

    #endregion

    #region DoubleClick Tests

    [Fact]
    public void DoubleClick_WithInvalidCoordinates_ReturnsError()
    {
        // Arrange
        SetupSingleMonitor();
        var sut = CreateSut();

        // Act
        var result = sut.DoubleClick(10000, 10000);

        // Assert
        result.Success.Should().BeFalse();
        result.ErrorCode.Should().Be(InputErrorCode.CoordinatesOutOfBounds);
        result.ClickType.Should().Be("double");
        VerifyNoInputSent();
    }

    [Fact]
    public void DoubleClick_WithValidCoordinates_SendsTwoLeftClicks()
    {
        // Arrange
        SetupSingleMonitor();
        var sut = CreateSut();

        // Act
        var result = sut.DoubleClick(600, 350);

        // Assert
        result.Success.Should().BeTrue();
        result.X.Should().Be(600);
        result.Y.Should().Be(350);
        result.ClickType.Should().Be("double");
        _mockInputWrapper.Verify(w => w.SetCursorPos(600, 350), Times.Once);
        _sentEvents.Should().Equal(
            InputEvent.Mouse(LeftDown), InputEvent.Mouse(LeftUp),
            InputEvent.Mouse(LeftDown), InputEvent.Mouse(LeftUp));
    }

    [Fact]
    public void DoubleClick_WhenFirstClickFails_ReturnsErrorWithoutSecondClick()
    {
        // Arrange
        SetupSingleMonitor();
        SetupSendInputFailure();
        var sut = CreateSut();

        // Act
        var result = sut.DoubleClick(500, 500);

        // Assert
        result.Success.Should().BeFalse();
        result.ErrorCode.Should().Be(InputErrorCode.SendInputFailed);
        result.Error.Should().Contain("First click");
        _mockInputWrapper.Verify(w => w.SendInput(It.IsAny<IReadOnlyList<InputEvent>>()), Times.Once);
    }

    #endregion

    #region MoveMouse Tests

    [Fact]
    public void MoveMouse_WithInvalidCoordinates_ReturnsError()
    {
        // Arrange
        SetupSingleMonitor();
        var sut = CreateSut();

        // Act
        var result = sut.MoveMouse(3000, 3000);

        // Assert
        result.Success.Should().BeFalse();
        result.ErrorCode.Should().Be(InputErrorCode.CoordinatesOutOfBounds);
        result.Error.Should().Contain("outside screen bounds");
        VerifyNoInputSent();
    }

    [Fact]
    public void MoveMouse_WithValidCoordinates_MovesCursorWithoutClicking()
    {
        // Arrange
        SetupSingleMonitor();
        var sut = CreateSut();

        // Act
        var result = sut.MoveMouse(800, 600);

        // Assert
        result.Success.Should().BeTrue();
        result.X.Should().Be(800);
        result.Y.Should().Be(600);
        _mockInputWrapper.Verify(w => w.SetCursorPos(800, 600), Times.Once);
        _sentEvents.Should().BeEmpty();
    }

    [Fact]
    public void MoveMouse_WhenSetCursorPosFails_ReturnsSendInputFailed()
    {
        // Arrange
        SetupSingleMonitor();
        _mockInputWrapper.Setup(w => w.SetCursorPos(It.IsAny<int>(), It.IsAny<int>())).Returns(false);
        var sut = CreateSut();

        // Act
        var result = sut.MoveMouse(800, 600);

        // Assert
        result.Success.Should().BeFalse();
        result.ErrorCode.Should().Be(InputErrorCode.SendInputFailed);
    }

    #endregion

    #region GetCursorPosition Tests

    [Fact]
    public void GetCursorPosition_ReturnsPositionFromNativeLayer()
    {
        // Arrange
        _mockInputWrapper.Setup(w => w.GetCursorPos()).Returns((123, 456));
        var sut = CreateSut();

        // Act
        var result = sut.GetCursorPosition();

        // Assert
        result.Should().Be((123, 456));
    }

    #endregion

    #region TypeText Tests

    [Fact]
    public void TypeText_WithEmptyText_ReturnsError()
    {
        // Arrange
        var sut = CreateSut();

        // Act
        var result = sut.TypeText("");

        // Assert
        result.Success.Should().BeFalse();
        result.ErrorCode.Should().Be(InputErrorCode.EmptyText);
        result.Error.Should().Contain("empty");
        result.CharactersTyped.Should().Be(0);
        VerifyNoInputSent();
    }

    [Fact]
    public void TypeText_WithNullText_ReturnsError()
    {
        // Arrange
        var sut = CreateSut();

        // Act
        var result = sut.TypeText(null!);

        // Assert
        result.Success.Should().BeFalse();
        result.ErrorCode.Should().Be(InputErrorCode.EmptyText);
        VerifyNoInputSent();
    }

    [Fact]
    public void TypeText_WithValidText_SendsUnicodeKeyDownAndUpPerCharacter()
    {
        // Arrange
        var sut = CreateSut();

        // Act
        var result = sut.TypeText("Hi!");

        // Assert
        result.Success.Should().BeTrue();
        result.Text.Should().Be("Hi!");
        result.CharactersTyped.Should().Be(3);
        _sentEvents.Should().Equal(
            InputEvent.Unicode('H', KeyDown), InputEvent.Unicode('H', KeyUp),
            InputEvent.Unicode('i', KeyDown), InputEvent.Unicode('i', KeyUp),
            InputEvent.Unicode('!', KeyDown), InputEvent.Unicode('!', KeyUp));
    }

    [Fact]
    public void TypeText_WhenSendInputFailsMidway_ReportsCharactersTyped()
    {
        // Arrange - the batch is cut short: SendInput inserts 5 of its 8 events (two whole characters)
        _mockInputWrapper.Setup(w => w.SendInput(It.IsAny<IReadOnlyList<InputEvent>>())).Returns(5u);
        var sut = CreateSut();

        // Act
        var result = sut.TypeText("abcd");

        // Assert
        result.Success.Should().BeFalse();
        result.ErrorCode.Should().Be(InputErrorCode.SendInputFailed);
        result.CharactersTyped.Should().Be(2);
    }

    [Theory]
    [InlineData(-10)]
    [InlineData(0)]
    [InlineData(50)]
    [InlineData(100)]
    [InlineData(200)]
    public void TypeText_WithVariousDelays_Succeeds(int inputDelay)
    {
        // Arrange
        var sut = CreateSut();

        // Act - The service clamps the delay internally
        var result = sut.TypeText("a", inputDelay);

        // Assert
        result.Success.Should().BeTrue();
        result.Text.Should().Be("a");
    }

    #endregion

    #region SendKeys Tests

    [Fact]
    public void SendKeys_WithEmptyKeys_ReturnsError()
    {
        // Arrange
        var sut = CreateSut();

        // Act
        var result = sut.SendKeys("");

        // Assert
        result.Success.Should().BeFalse();
        result.ErrorCode.Should().Be(InputErrorCode.EmptyText);
        result.Error.Should().Contain("empty");
        VerifyNoInputSent();
    }

    [Fact]
    public void SendKeys_WithNullKeys_ReturnsError()
    {
        // Arrange
        var sut = CreateSut();

        // Act
        var result = sut.SendKeys(null!);

        // Assert
        result.Success.Should().BeFalse();
        result.ErrorCode.Should().Be(InputErrorCode.EmptyText);
        VerifyNoInputSent();
    }

    [Fact]
    public void SendKeys_WithEnter_SendsReturnKeyDownAndUp()
    {
        // Arrange
        var sut = CreateSut();

        // Act
        var result = sut.SendKeys("{ENTER}");

        // Assert
        result.Success.Should().BeTrue();
        result.Keys.Should().Be("{ENTER}");
        _sentEvents.Should().Equal(InputEvent.Key(VkReturn, KeyDown), InputEvent.Key(VkReturn, KeyUp));
    }

    [Fact]
    public void SendKeys_WithExtendedKey_SetsExtendedKeyFlag()
    {
        // Arrange
        var sut = CreateSut();

        // Act
        var result = sut.SendKeys("{UP}");

        // Assert
        result.Success.Should().BeTrue();
        _sentEvents.Should().Equal(
            InputEvent.Key(VkUp, ExtendedKey),
            InputEvent.Key(VkUp, ExtendedKey | KeyUp));
    }

    [Fact]
    public void SendKeys_WithAltF4_WrapsKeyInAltPressAndRelease()
    {
        // Arrange
        var sut = CreateSut();

        // Act
        var result = sut.SendKeys("%{F4}");

        // Assert
        result.Success.Should().BeTrue();
        _sentEvents.Should().Equal(
            InputEvent.Key(VkMenu, KeyDown),
            InputEvent.Key(VkF4, KeyDown),
            InputEvent.Key(VkF4, KeyUp),
            InputEvent.Key(VkMenu, KeyUp));
    }

    [Fact]
    public void SendKeys_WithCtrlC_WrapsLetterInCtrlPressAndRelease()
    {
        // Arrange
        var sut = CreateSut();

        // Act
        var result = sut.SendKeys("^c");

        // Assert
        result.Success.Should().BeTrue();
        _sentEvents.Should().Equal(
            InputEvent.Key(VkControl, KeyDown),
            InputEvent.Key(VkC, KeyDown),
            InputEvent.Key(VkC, KeyUp),
            InputEvent.Key(VkControl, KeyUp));
    }

    [Fact]
    public void SendKeys_WithPunctuation_SendsUnicodeCharacter()
    {
        // Arrange
        var sut = CreateSut();

        // Act
        var result = sut.SendKeys("!");

        // Assert
        result.Success.Should().BeTrue();
        _sentEvents.Should().Equal(InputEvent.Unicode('!', KeyDown), InputEvent.Unicode('!', KeyUp));
    }

    [Fact]
    public void SendKeys_WithUnclosedBrace_ReturnsError()
    {
        // Arrange
        var sut = CreateSut();

        // Act
        var result = sut.SendKeys("{ENTER");

        // Assert
        result.Success.Should().BeFalse();
        result.ErrorCode.Should().Be(InputErrorCode.UnknownKey);
        result.Error.Should().Contain("Unclosed brace");
        VerifyNoInputSent();
    }

    [Fact]
    public void SendKeys_WithUnknownKey_ReturnsError()
    {
        // Arrange
        var sut = CreateSut();

        // Act
        var result = sut.SendKeys("{UNKNOWNKEY}");

        // Assert
        result.Success.Should().BeFalse();
        result.ErrorCode.Should().Be(InputErrorCode.UnknownKey);
        result.Error.Should().Contain("Unknown key");
        VerifyNoInputSent();
    }

    [Fact]
    public void SendKeys_WhenSendInputFails_ReturnsSendInputFailed()
    {
        // Arrange
        SetupSendInputFailure();
        var sut = CreateSut();

        // Act
        var result = sut.SendKeys("{ENTER}");

        // Assert
        result.Success.Should().BeFalse();
        result.ErrorCode.Should().Be(InputErrorCode.SendInputFailed);
    }

    [Theory]
    [InlineData("{ENTER}")]
    [InlineData("{TAB}")]
    [InlineData("{ESC}")]
    [InlineData("{ESCAPE}")]
    [InlineData("{BACKSPACE}")]
    [InlineData("{BS}")]
    [InlineData("{DELETE}")]
    [InlineData("{DEL}")]
    [InlineData("{INSERT}")]
    [InlineData("{INS}")]
    [InlineData("{UP}")]
    [InlineData("{DOWN}")]
    [InlineData("{LEFT}")]
    [InlineData("{RIGHT}")]
    [InlineData("{HOME}")]
    [InlineData("{END}")]
    [InlineData("{PGUP}")]
    [InlineData("{PGDN}")]
    [InlineData("{F1}")]
    [InlineData("{F12}")]
    [InlineData("{SPACE}")]
    public void SendKeys_WithSpecialKey_SendsKeyDownAndUp(string keys)
    {
        // Arrange
        var sut = CreateSut();

        // Act
        var result = sut.SendKeys(keys);

        // Assert
        result.Success.Should().BeTrue();
        result.Keys.Should().Be(keys);
        _sentEvents.Should().HaveCount(2);
        _sentEvents.Should().OnlyContain(e => e.Kind == InputEventKind.VirtualKey);
        _sentEvents[0].Code.Should().Be(_sentEvents[1].Code);
        (_sentEvents[0].Flags & KeyUp).Should().Be(0);
        (_sentEvents[1].Flags & KeyUp).Should().Be(KeyUp);
    }

    [Theory]
    [InlineData("^c", 4)]
    [InlineData("^v", 4)]
    [InlineData("%{F4}", 4)]
    [InlineData("+{HOME}", 4)]
    [InlineData("^+{END}", 6)]
    public void SendKeys_WithModifiers_ReleasesEveryKeyPressed(string keys, int expectedEventCount)
    {
        // Arrange
        var sut = CreateSut();

        // Act
        var result = sut.SendKeys(keys);

        // Assert
        result.Success.Should().BeTrue();
        result.Keys.Should().Be(keys);
        _sentEvents.Should().HaveCount(expectedEventCount);
        var pressed = _sentEvents.Where(e => (e.Flags & KeyUp) == 0).Select(e => e.Code);
        var released = _sentEvents.Where(e => (e.Flags & KeyUp) != 0).Select(e => e.Code);
        released.Should().BeEquivalentTo(pressed);
    }

    #endregion
}
