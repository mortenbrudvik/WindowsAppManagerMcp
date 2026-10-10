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
        new InputSimulationService(_mockMonitorService.Object, _mockInputWrapper.Object, _guard.Object, new InputSession(_mockInputWrapper.Object));

    private void SetupSingleMonitor()
    {
        var monitor = TestDataFactory.CreateMonitorInfo(
            boundsX: 0, boundsY: 0, boundsWidth: 1920, boundsHeight: 1080);
        _mockMonitorService.Setup(m => m.GetAllMonitors())
            .Returns(new List<MonitorInfo> { monitor });
    }

    /// <summary>
    /// The absolute move to (x, y) on the single 1920x1080 monitor, checked against the mapping Windows
    /// applies (pixel = n * size / 65536, rounded down), not against the server's own arithmetic.
    /// </summary>
    private static InputEvent MoveTo(int x, int y)
    {
        var dx = Enumerable.Range(0, 65536).First(n => (long)n * 1920 / 65536 == x);
        var dy = Enumerable.Range(0, 65536).First(n => (long)n * 1080 / 65536 == y);
        var expected = InputSimulationService.ToVirtualDesktopAbsolute(x, y, [TestDataFactory.CreateMonitorInfo(
            boundsX: 0, boundsY: 0, boundsWidth: 1920, boundsHeight: 1080)]);
        ((long)expected.X * 1920 / 65536).Should().Be(x, $"normalized x {expected.X} (first is {dx}) must map back to {x}");
        ((long)expected.Y * 1080 / 65536).Should().Be(y, $"normalized y {expected.Y} (first is {dy}) must map back to {y}");
        return InputEvent.MouseMoveAbsolute(expected.X, expected.Y);
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
        _sentEvents.Should().Equal(MoveTo(500, 300), InputEvent.Mouse(LeftDown), InputEvent.Mouse(LeftUp));
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
        _sentEvents.Should().Equal(MoveTo(750, 400), InputEvent.Mouse(RightDown), InputEvent.Mouse(RightUp));
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
            MoveTo(600, 350), InputEvent.Mouse(LeftDown), InputEvent.Mouse(LeftUp),
            MoveTo(600, 350), InputEvent.Mouse(LeftDown), InputEvent.Mouse(LeftUp));
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

    [Fact]
    public void MoveMouse_PointerConfinedElsewhere_IsCursorNotAtPoint()
    {
        // Arrange - SetCursorPos is clamped (ClipCursor): the pointer never gets to the point
        SetupSingleMonitor();
        _mockInputWrapper.Setup(w => w.GetCursorPos()).Returns((100, 100));
        var sut = CreateSut();

        // Act
        var result = sut.MoveMouse(800, 600);

        // Assert
        result.Success.Should().BeFalse();
        result.ErrorCode.Should().Be(InputErrorCode.CursorNotAtPoint);
        result.Error.Should().Contain("(800, 600)").And.Contain("(100, 100)");
        (result.ActualX, result.ActualY).Should().Be((100, 100));
    }

    #endregion

    #region The click lands where the guard looked

    [Fact]
    public void Click_UserMovedTheMouseAfterTheCursorMove_PutsItBackAndClicksAtThePoint()
    {
        // Arrange - the first read finds the pointer moved away, the second (right after placing it again) does not
        SetupSingleMonitor();
        _mockInputWrapper.SetupSequence(w => w.GetCursorPos())
            .Returns((40, 40))
            .Returns((500, 300))
            .Returns((500, 300));
        var sut = CreateSut();

        // Act
        var result = sut.Click(500, 300);

        // Assert - the press carries its own move to the point, so later movement cannot redirect it either
        result.Success.Should().BeTrue(result.Error);
        _mockInputWrapper.Verify(w => w.SetCursorPos(500, 300), Times.Exactly(2));
        _sentEvents.Should().Equal(MoveTo(500, 300), InputEvent.Mouse(LeftDown), InputEvent.Mouse(LeftUp));
    }

    [Fact]
    public void Click_PointerConfinedElsewhere_ClicksNothing()
    {
        // Arrange - a game or remote session confines the pointer: the button would go down where it is held
        SetupSingleMonitor();
        _mockInputWrapper.Setup(w => w.GetCursorPos()).Returns((10, 10));
        var sut = CreateSut();

        // Act
        var result = sut.Click(500, 300);

        // Assert
        result.Success.Should().BeFalse();
        result.ErrorCode.Should().Be(InputErrorCode.CursorNotAtPoint);
        result.Error.Should().StartWith("Nothing was clicked").And.Contain("(10, 10)");
        _sentEvents.Should().BeEmpty();
    }

    [Fact]
    public void DoubleClick_PointerConfinedBeforeTheSecondClick_SendsOnlyTheFirst()
    {
        // Arrange
        SetupSingleMonitor();
        _mockInputWrapper.SetupSequence(w => w.GetCursorPos())
            .Returns((600, 350))
            .Returns((20, 20))
            .Returns((20, 20));
        var sut = CreateSut();

        // Act
        var result = sut.DoubleClick(600, 350);

        // Assert
        result.ErrorCode.Should().Be(InputErrorCode.CursorNotAtPoint);
        result.Error.Should().StartWith("The first click was sent, the second was not");
        _sentEvents.Should().Equal(MoveTo(600, 350), InputEvent.Mouse(LeftDown), InputEvent.Mouse(LeftUp));
    }

    [Theory]
    [InlineData(0, 0, 1920, 1080)]
    [InlineData(0, 0, 3840, 2160)]
    [InlineData(-1920, -300, 6400, 1740)] // a monitor left of and above the primary one
    [InlineData(0, 0, 7, 3)]
    public void ToVirtualDesktopAbsolute_EveryPixelMapsBackToItself(int left, int top, int width, int height)
    {
        // Arrange - the virtual desktop is the bounding rectangle of the monitors
        var monitors = new List<MonitorInfo>
        {
            TestDataFactory.CreateMonitorInfo(boundsX: left, boundsY: top, boundsWidth: width / 2, boundsHeight: height),
            TestDataFactory.CreateMonitorInfo(boundsX: left + width / 2, boundsY: top, boundsWidth: width - width / 2, boundsHeight: height / 2)
        };

        // Act & Assert - with the mapping Windows applies (left + n * width / 65536, rounded down) and with
        // the alternative that rounds n * (width - 1) / 65535
        for (var x = left; x < left + width; x++)
        {
            var (nx, _) = InputSimulationService.ToVirtualDesktopAbsolute(x, top, monitors);
            nx.Should().BeInRange(0, 65535);
            (left + (int)((long)nx * width / 65536)).Should().Be(x);
            (left + (int)Math.Round((double)nx * (width - 1) / 65535, MidpointRounding.AwayFromZero)).Should().Be(x);
        }

        for (var y = top; y < top + height; y++)
        {
            var (_, ny) = InputSimulationService.ToVirtualDesktopAbsolute(left, y, monitors);
            (top + (int)((long)ny * height / 65536)).Should().Be(y);
            (top + (int)Math.Round((double)ny * (height - 1) / 65535, MidpointRounding.AwayFromZero)).Should().Be(y);
        }
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
    public async Task TypeText_WithEmptyText_ReturnsError()
    {
        // Arrange
        var sut = CreateSut();

        // Act
        var result = await sut.TypeTextAsync("");

        // Assert
        result.Success.Should().BeFalse();
        result.ErrorCode.Should().Be(InputErrorCode.EmptyText);
        result.Error.Should().Contain("empty");
        result.CharactersTyped.Should().Be(0);
        VerifyNoInputSent();
    }

    [Fact]
    public async Task TypeText_WithNullText_ReturnsError()
    {
        // Arrange
        var sut = CreateSut();

        // Act
        var result = await sut.TypeTextAsync(null!);

        // Assert
        result.Success.Should().BeFalse();
        result.ErrorCode.Should().Be(InputErrorCode.EmptyText);
        VerifyNoInputSent();
    }

    [Fact]
    public async Task TypeText_WithValidText_SendsUnicodeKeyDownAndUpPerCharacter()
    {
        // Arrange
        var sut = CreateSut();

        // Act
        var result = await sut.TypeTextAsync("Hi!");

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
    public async Task TypeText_WhenSendInputFailsMidway_ReportsCharactersTyped()
    {
        // Arrange - the batch is cut short: SendInput inserts 5 of its 8 events (two whole characters)
        _mockInputWrapper.Setup(w => w.SendInput(It.IsAny<IReadOnlyList<InputEvent>>())).Returns(5u);
        var sut = CreateSut();

        // Act
        var result = await sut.TypeTextAsync("abcd");

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
    public async Task TypeText_WithVariousDelays_Succeeds(int inputDelay)
    {
        // Arrange
        var sut = CreateSut();

        // Act - The service clamps the delay internally
        var result = await sut.TypeTextAsync("a", inputDelay);

        // Assert
        result.Success.Should().BeTrue();
        result.Text.Should().Be("a");
    }

    #endregion

    #region SendKeys Tests

    [Fact]
    public async Task SendKeys_WithEmptyKeys_ReturnsError()
    {
        // Arrange
        var sut = CreateSut();

        // Act
        var result = await sut.SendKeysAsync("");

        // Assert
        result.Success.Should().BeFalse();
        result.ErrorCode.Should().Be(InputErrorCode.EmptyText);
        result.Error.Should().Contain("empty");
        VerifyNoInputSent();
    }

    [Fact]
    public async Task SendKeys_WithNullKeys_ReturnsError()
    {
        // Arrange
        var sut = CreateSut();

        // Act
        var result = await sut.SendKeysAsync(null!);

        // Assert
        result.Success.Should().BeFalse();
        result.ErrorCode.Should().Be(InputErrorCode.EmptyText);
        VerifyNoInputSent();
    }

    [Fact]
    public async Task SendKeys_WithEnter_SendsReturnKeyDownAndUp()
    {
        // Arrange
        var sut = CreateSut();

        // Act
        var result = await sut.SendKeysAsync("{ENTER}");

        // Assert
        result.Success.Should().BeTrue();
        result.Keys.Should().Be("{ENTER}");
        _sentEvents.Should().Equal(InputEvent.Key(VkReturn, KeyDown), InputEvent.Key(VkReturn, KeyUp));
    }

    [Fact]
    public async Task SendKeys_WithExtendedKey_SetsExtendedKeyFlag()
    {
        // Arrange
        var sut = CreateSut();

        // Act
        var result = await sut.SendKeysAsync("{UP}");

        // Assert
        result.Success.Should().BeTrue();
        _sentEvents.Should().Equal(
            InputEvent.Key(VkUp, ExtendedKey),
            InputEvent.Key(VkUp, ExtendedKey | KeyUp));
    }

    [Fact]
    public async Task SendKeys_WithAltF4_WrapsKeyInAltPressAndRelease()
    {
        // Arrange
        var sut = CreateSut();

        // Act
        var result = await sut.SendKeysAsync("%{F4}");

        // Assert
        result.Success.Should().BeTrue();
        _sentEvents.Should().Equal(
            InputEvent.Key(VkMenu, KeyDown),
            InputEvent.Key(VkF4, KeyDown),
            InputEvent.Key(VkF4, KeyUp),
            InputEvent.Key(VkMenu, KeyUp));
    }

    [Fact]
    public async Task SendKeys_WithCtrlC_WrapsLetterInCtrlPressAndRelease()
    {
        // Arrange
        var sut = CreateSut();

        // Act
        var result = await sut.SendKeysAsync("^c");

        // Assert
        result.Success.Should().BeTrue();
        _sentEvents.Should().Equal(
            InputEvent.Key(VkControl, KeyDown),
            InputEvent.Key(VkC, KeyDown),
            InputEvent.Key(VkC, KeyUp),
            InputEvent.Key(VkControl, KeyUp));
    }

    [Fact]
    public async Task SendKeys_WithPunctuation_SendsUnicodeCharacter()
    {
        // Arrange
        var sut = CreateSut();

        // Act
        var result = await sut.SendKeysAsync("!");

        // Assert
        result.Success.Should().BeTrue();
        _sentEvents.Should().Equal(InputEvent.Unicode('!', KeyDown), InputEvent.Unicode('!', KeyUp));
    }

    [Fact]
    public async Task SendKeys_WithUnclosedBrace_ReturnsError()
    {
        // Arrange
        var sut = CreateSut();

        // Act
        var result = await sut.SendKeysAsync("{ENTER");

        // Assert
        result.Success.Should().BeFalse();
        result.ErrorCode.Should().Be(InputErrorCode.UnknownKey);
        result.Error.Should().Contain("Unclosed brace");
        VerifyNoInputSent();
    }

    [Fact]
    public async Task SendKeys_WithUnknownKey_ReturnsError()
    {
        // Arrange
        var sut = CreateSut();

        // Act
        var result = await sut.SendKeysAsync("{UNKNOWNKEY}");

        // Assert
        result.Success.Should().BeFalse();
        result.ErrorCode.Should().Be(InputErrorCode.UnknownKey);
        result.Error.Should().Contain("Unknown key");
        VerifyNoInputSent();
    }

    [Fact]
    public async Task SendKeys_WhenSendInputFails_ReturnsSendInputFailed()
    {
        // Arrange
        SetupSendInputFailure();
        var sut = CreateSut();

        // Act
        var result = await sut.SendKeysAsync("{ENTER}");

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
    public async Task SendKeys_WithSpecialKey_SendsKeyDownAndUp(string keys)
    {
        // Arrange
        var sut = CreateSut();

        // Act
        var result = await sut.SendKeysAsync(keys);

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
    public async Task SendKeys_WithModifiers_ReleasesEveryKeyPressed(string keys, int expectedEventCount)
    {
        // Arrange
        var sut = CreateSut();

        // Act
        var result = await sut.SendKeysAsync(keys);

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
