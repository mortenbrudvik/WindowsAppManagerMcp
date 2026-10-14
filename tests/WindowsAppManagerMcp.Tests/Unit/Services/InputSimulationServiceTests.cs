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
        var expected = InputBatchBuilder.ToAbsolute(x, y, new VirtualScreenBounds(0, 0, 1920, 1080));
        ((long)expected.X * 1920 / 65536).Should().Be(x, $"normalized x {expected.X} must map back to {x}");
        ((long)expected.Y * 1080 / 65536).Should().Be(y, $"normalized y {expected.Y} must map back to {y}");
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
    public void Click_WhenSetCursorPosFails_ReturnsCursorPositionFailed()
    {
        // Arrange
        SetupSingleMonitor();
        _mockInputWrapper.Setup(w => w.SetCursorPos(It.IsAny<int>(), It.IsAny<int>())).Returns(false);
        var sut = CreateSut();

        // Act
        var result = sut.Click(500, 300);

        // Assert - roadmap M2.2: not SendInputFailed, which means the input itself was not accepted
        result.Success.Should().BeFalse();
        result.ErrorCode.Should().Be(InputErrorCode.CursorPositionFailed);
        result.Error.Should().Contain("Nothing was clicked");
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
    public void DoubleClick_WithValidCoordinates_SendsBothLeftClicksInOneBatch()
    {
        // Arrange
        SetupSingleMonitor();
        var sut = CreateSut();

        // Act
        var result = sut.DoubleClick(600, 350);

        // Assert - roadmap M4.2: one SendInput call, so the clicks are one double click whatever the double-click time
        result.Success.Should().BeTrue();
        result.X.Should().Be(600);
        result.Y.Should().Be(350);
        result.ClickType.Should().Be("double");
        _mockInputWrapper.Verify(w => w.SetCursorPos(600, 350), Times.Once);
        _mockInputWrapper.Verify(w => w.SendInput(It.IsAny<IReadOnlyList<InputEvent>>()), Times.Once);
        _sentEvents.Should().Equal(
            MoveTo(600, 350), InputEvent.Mouse(LeftDown), InputEvent.Mouse(LeftUp), InputEvent.Mouse(LeftDown), InputEvent.Mouse(LeftUp));
    }

    [Fact]
    public void DoubleClick_WhenSendInputInsertsNothing_SaysNoClickWentIn()
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
        result.Error.Should().Contain("inserted 0 of 5 events").And.Contain("0 of its 2 clicks went in");
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
    public void MoveMouse_WithValidCoordinates_SendsOneAbsoluteMoveWithoutClicking()
    {
        // Arrange
        SetupSingleMonitor();
        var sut = CreateSut();

        // Act
        var result = sut.MoveMouse(800, 600);

        // Assert - roadmap M4.2: an injected move, as a click's, not SetCursorPos
        result.Success.Should().BeTrue();
        result.X.Should().Be(800);
        result.Y.Should().Be(600);
        _mockInputWrapper.Verify(w => w.SetCursorPos(It.IsAny<int>(), It.IsAny<int>()), Times.Never);
        _sentEvents.Should().Equal(MoveTo(800, 600));
    }

    [Fact]
    public void MoveMouse_WhenSendInputInsertsNothing_ReturnsSendInputFailed()
    {
        // Arrange
        SetupSingleMonitor();
        SetupSendInputFailure();
        var sut = CreateSut();

        // Act
        var result = sut.MoveMouse(800, 600);

        // Assert
        result.Success.Should().BeFalse();
        result.ErrorCode.Should().Be(InputErrorCode.SendInputFailed);
        result.Error.Should().Contain("the pointer was not moved");
    }

    [Fact]
    public void MoveMouse_PointerConfinedElsewhere_IsCursorClamped_AndSaysTheMoveWasSent()
    {
        // Arrange - the move is clamped (ClipCursor): the pointer never gets to the point
        SetupSingleMonitor();
        _mockInputWrapper.Setup(w => w.GetCursorPos()).Returns((100, 100));
        var sut = CreateSut();

        // Act
        var result = sut.MoveMouse(800, 600);

        // Assert
        result.Success.Should().BeFalse();
        result.ErrorCode.Should().Be(InputErrorCode.CursorClamped);
        result.Error.Should().StartWith("The move was sent").And.Contain("(800, 600)").And.Contain("(100, 100)");
        (result.ActualX, result.ActualY).Should().Be((100, 100));
        _sentEvents.Should().Equal(MoveTo(800, 600));
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
    public void DoubleClick_PointerConfinedElsewhere_ClicksNothing()
    {
        // Arrange - the pointer is checked once, before the one batch that holds both clicks
        SetupSingleMonitor();
        _mockInputWrapper.Setup(w => w.GetCursorPos()).Returns((20, 20));
        var sut = CreateSut();

        // Act
        var result = sut.DoubleClick(600, 350);

        // Assert
        result.ErrorCode.Should().Be(InputErrorCode.CursorNotAtPoint);
        result.Error.Should().StartWith("Nothing was clicked");
        _sentEvents.Should().BeEmpty();
    }

    #endregion

    #region Mouse correctness (roadmap M4.2)

    private readonly Mock<INativeSystemInfoWrapper> _systemInfo = new();

    private InputSimulationService CreateSutWithSystemInfo(IClipboardPasteGuard? pasteGuard = null, IClipboardOrigin? clipboardOrigin = null) =>
        new(_mockMonitorService.Object, _mockInputWrapper.Object, _guard.Object, new InputSession(_mockInputWrapper.Object),
            systemInfo: _systemInfo.Object, pasteGuard: pasteGuard, clipboardOrigin: clipboardOrigin);

    [Fact]
    public void Click_WithSwappedButtons_SendsTheRightButton_WhichWindowsSwapsBackToTheUsersPrimary()
    {
        SetupSingleMonitor();
        _systemInfo.Setup(s => s.AreMouseButtonsSwapped()).Returns(true);

        var result = CreateSutWithSystemInfo().Click(500, 300);

        result.Success.Should().BeTrue(result.Error);
        result.ClickType.Should().Be("left");
        _sentEvents.Should().Equal(MoveTo(500, 300), InputEvent.Mouse(RightDown), InputEvent.Mouse(RightUp));
    }

    [Fact]
    public void RightClick_WithSwappedButtons_SendsTheLeftButton()
    {
        SetupSingleMonitor();
        _systemInfo.Setup(s => s.AreMouseButtonsSwapped()).Returns(true);

        CreateSutWithSystemInfo().RightClick(500, 300).Success.Should().BeTrue();

        _sentEvents.Should().Equal(MoveTo(500, 300), InputEvent.Mouse(LeftDown), InputEvent.Mouse(LeftUp));
    }

    [Fact]
    public void DoubleClick_WithSwappedButtons_DoubleClicksTheRightButton()
    {
        SetupSingleMonitor();
        _systemInfo.Setup(s => s.AreMouseButtonsSwapped()).Returns(true);

        CreateSutWithSystemInfo().DoubleClick(600, 350).Success.Should().BeTrue();

        _sentEvents.Should().Equal(
            MoveTo(600, 350), InputEvent.Mouse(RightDown), InputEvent.Mouse(RightUp), InputEvent.Mouse(RightDown), InputEvent.Mouse(RightUp));
    }

    [Fact]
    public void Click_ReadsTheSwapForEveryCall_SoAChangeInTheSettingsCountsAtOnce()
    {
        SetupSingleMonitor();
        _systemInfo.SetupSequence(s => s.AreMouseButtonsSwapped()).Returns(false).Returns(true);
        var sut = CreateSutWithSystemInfo();

        sut.Click(500, 300);
        sut.Click(500, 300);

        _sentEvents.Where(e => e.Flags is LeftDown or RightDown).Select(e => e.Flags).Should().Equal(LeftDown, RightDown);
    }

    [Fact]
    public void RightClick_WithSwappedButtons_IsStillCheckedAsAPaste_AndRefused()
    {
        // The secondary button pastes in a terminal whichever physical button it is: the paste guard sees the user's buttons
        SetupSingleMonitor();
        _systemInfo.Setup(s => s.AreMouseButtonsSwapped()).Returns(true);
        var pasteGuard = new RefusingPasteGuard();

        var result = CreateSutWithSystemInfo(pasteGuard).RightClick(750, 400);

        result.ErrorCode.Should().Be(InputErrorCode.PasteNotAllowed);
        pasteGuard.Checked.Should().ContainSingle().Which.Batch.Should().Contain(InputEvent.Mouse(RightDown));
        _sentEvents.Should().BeEmpty();
    }

    [Fact]
    public void Click_WithSwappedButtons_IsNotCheckedAsAPaste_AndIsReportedAsAClickThatCanCopy()
    {
        // A primary click is not a paste although it is the right button's events, and it can copy (a terminal that copies
        // on selection): the clipboard origin sees it as the left-button press it is to the user
        SetupSingleMonitor();
        _systemInfo.Setup(s => s.AreMouseButtonsSwapped()).Returns(true);
        var pasteGuard = new RefusingPasteGuard();
        var origin = new Mock<IClipboardOrigin>();
        IReadOnlyList<InputEvent>? told = null;
        origin.Setup(o => o.BeforeInput(It.IsAny<IReadOnlyList<InputEvent>>(), It.IsAny<int>(), It.IsAny<nint>(), It.IsAny<nint>()))
            .Callback((IReadOnlyList<InputEvent> batch, int _, nint _, nint _) => told = batch.ToList())
            .Returns((ClipboardCopyMark?)null);

        CreateSutWithSystemInfo(pasteGuard, origin.Object).Click(750, 400).Success.Should().BeTrue();

        pasteGuard.Checked.Should().BeEmpty();
        told.Should().NotBeNull();
        ClipboardOrigin.CopyTrigger(told!).Should().Be(ClipboardCopyTrigger.Click);
        _sentEvents.Should().Contain(InputEvent.Mouse(RightDown), "Windows gets the physical button");
    }

    [Fact]
    public void Click_NormalizesAcrossWindowsVirtualScreen_WithANegativeOriginAndMixedDpi()
    {
        // Arrange - a 100% monitor left of a 150% primary (physical pixels); Windows reports the virtual screen
        var left = TestDataFactory.CreateMonitorInfo(1, @"\\.\DISPLAY2", false, -1920, 300, 1920, 1080, scaleFactor: 1.0);
        var primary = TestDataFactory.CreateMonitorInfo(0, @"\\.\DISPLAY1", true, 0, 0, 3840, 2160, scaleFactor: 1.5);
        _mockMonitorService.Setup(m => m.GetAllMonitors()).Returns([primary, left]);
        var screen = new VirtualScreenBounds(-1920, 0, 5760, 2160);
        _systemInfo.Setup(s => s.GetVirtualScreen()).Returns(screen);
        var sut = CreateSutWithSystemInfo();

        // Act - the left monitor's last column and the primary's last pixel
        sut.Click(-1, 1379).Success.Should().BeTrue();
        sut.Click(3839, 2159).Success.Should().BeTrue();

        // Assert - each move maps back, as Windows maps it, to the clicked pixel
        var moves = _sentEvents.Where(e => e.Flags == InputEvent.MouseMoveAbsoluteFlags).ToList();
        moves.Select(m => (screen.X + (int)((long)m.Dx * screen.Width / 65536), screen.Y + (int)((long)m.Dy * screen.Height / 65536)))
            .Should().Equal((-1, 1379), (3839, 2159));
    }

    [Fact]
    public void Click_UsesWindowsVirtualScreen_NotTheMonitorList()
    {
        // Arrange - the monitor list (read once, and possibly stale) lacks a monitor above the primary that Windows has
        SetupSingleMonitor();
        var screen = new VirtualScreenBounds(0, -1440, 2560, 2520);
        _systemInfo.Setup(s => s.GetVirtualScreen()).Returns(screen);

        // Act
        CreateSutWithSystemInfo().Click(500, 300).Success.Should().BeTrue();

        // Assert
        var move = _sentEvents[0];
        (screen.X + (int)((long)move.Dx * screen.Width / 65536), screen.Y + (int)((long)move.Dy * screen.Height / 65536)).Should().Be((500, 300));
    }

    [Fact]
    public void Click_PointerElsewhereAfterTheBatch_IsCursorClamped_AndSaysTheClickWasSent()
    {
        // Arrange - a fake that moves the pointer as Windows would; a game confines it right before the batch goes in
        var input = new FakeInputWrapper();
        var monitors = TestDataFactory.CreateMockMonitorService();
        input.BeforeSend = _ => input.Clip = (0, 0, 400, 400);
        var sut = new InputSimulationService(monitors.Object, input, _guard.Object, new InputSession(input));

        // Act
        var result = sut.Click(500, 300);

        // Assert - the click went in (where the pointer was held), and the result says so
        result.Success.Should().BeFalse();
        result.ErrorCode.Should().Be(InputErrorCode.CursorClamped);
        result.Error.Should().StartWith("The click was sent").And.Contain("(399, 300)").And.Contain("(500, 300)").And.Contain("do not simply retry");
        (result.ActualX, result.ActualY).Should().Be((399, 300));
        result.ReceivingWindow.Should().NotBeNull();
        input.Batches.Should().ContainSingle().Which.Should().HaveCount(3);
    }

    [Fact]
    public void DoubleClick_PointerElsewhereAfterTheBatch_IsCursorClamped()
    {
        var input = new FakeInputWrapper();
        input.BeforeSend = _ => input.Clip = (0, 0, 400, 400);
        var sut = new InputSimulationService(TestDataFactory.CreateMockMonitorService().Object, input, _guard.Object, new InputSession(input));

        var result = sut.DoubleClick(500, 300);

        result.ErrorCode.Should().Be(InputErrorCode.CursorClamped);
        result.Error.Should().StartWith("The double click was sent");
    }

    [Fact]
    public void Click_PointerThatGetsToThePointLate_IsNotClamped()
    {
        // Windows can move the pointer after SendInput returns: the first read after the batch still has the old position
        SetupSingleMonitor();
        var sends = 0;
        _mockInputWrapper.Setup(w => w.SendInput(It.IsAny<IReadOnlyList<InputEvent>>()))
            .Callback(() => sends++)
            .Returns((IReadOnlyList<InputEvent> events) => (uint)events.Count);
        var readsAfterTheBatch = 0;
        _mockInputWrapper.Setup(w => w.GetCursorPos()).Returns(() => sends == 0 ? (500, 300) : ++readsAfterTheBatch == 1 ? (40, 40) : (500, 300));

        var result = CreateSut().Click(500, 300);

        result.Success.Should().BeTrue(result.Error);
        readsAfterTheBatch.Should().BeGreaterThan(1);
    }

    [Fact]
    public void Click_PartOfTheBatchInserted_SaysHowManyClicksWentIn_AndReleasesTheButton()
    {
        // Arrange - SendInput stops after the first click's press: the button would stay down
        var input = new FakeInputWrapper { Insert = (_, call) => call == 0 ? 2u : uint.MaxValue };
        var sut = new InputSimulationService(TestDataFactory.CreateMockMonitorService().Object, input, _guard.Object, new InputSession(input));

        // Act
        var result = sut.DoubleClick(500, 300);

        // Assert
        result.ErrorCode.Should().Be(InputErrorCode.SendInputFailed);
        result.Error.Should().Contain("inserted 2 of 5 events").And.Contain("0 of its 2 clicks went in")
            .And.Contain("without its release, which was sent at once");
        input.Batches.Should().HaveCount(2);
        input.Batches[1].Should().Equal(InputEvent.Mouse(LeftUp));
        input.IsKeyDown(0x01).Should().BeFalse("the left button is up again");
    }

    [Fact]
    public void DoubleClick_FirstClickInserted_SaysOneOfTwo()
    {
        var input = new FakeInputWrapper { Insert = (_, _) => 3u };
        var sut = new InputSimulationService(TestDataFactory.CreateMockMonitorService().Object, input, _guard.Object, new InputSession(input));

        var result = sut.DoubleClick(500, 300);

        result.ErrorCode.Should().Be(InputErrorCode.SendInputFailed);
        result.Error.Should().Contain("1 of its 2 clicks went in").And.NotContain("without its release");
    }

    [Fact]
    public void Scroll_PointerElsewhereAfterTheBatch_IsCursorClamped_AndSaysTheScrollWasSent()
    {
        var input = new FakeInputWrapper();
        input.BeforeSend = _ => input.Clip = (0, 0, 400, 400);
        var sut = new InputSimulationService(TestDataFactory.CreateMockMonitorService().Object, input, _guard.Object, new InputSession(input));

        var result = sut.Scroll(500, 300, deltaY: 2, deltaX: 0, InputTarget.None, false, default);

        result.ErrorCode.Should().Be(InputErrorCode.CursorClamped);
        result.Error.Should().StartWith("The scroll was sent");
    }

    [Fact]
    public void MoveMouse_WithTheFake_PutsThePointerOnThePixel()
    {
        var input = new FakeInputWrapper();
        input.MovePointer(5, 5);
        var sut = new InputSimulationService(TestDataFactory.CreateMockMonitorService().Object, input, _guard.Object, new InputSession(input));

        var result = sut.MoveMouse(1919, 1079);

        result.Success.Should().BeTrue(result.Error);
        input.GetCursorPos().Should().Be((1919, 1079));
        input.CursorMoves.Should().Be(0, "the move is injected, not SetCursorPos");
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

        // Assert - a syntax error to fix, not a key name to change (roadmap M2.2)
        result.Success.Should().BeFalse();
        result.ErrorCode.Should().Be(InputErrorCode.InvalidKeySpecification);
        result.Error.Should().Contain("is not closed");
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

    #region Paste (review PRIVA-1)

    /// <summary>A paste guard that refuses every paste and records what it was asked about.</summary>
    private sealed class RefusingPasteGuard : IClipboardPasteGuard
    {
        public List<(IReadOnlyList<InputEvent> Batch, nint Receiving)> Checked { get; } = [];

        public string? Check(IReadOnlyList<InputEvent> batch, nint receivingWindow)
        {
            Checked.Add((batch.ToList(), receivingWindow));
            return ClipboardOrigin.PasteTrigger(batch) != null || batch.Any(e => (e.Flags & RightDown) != 0 && e.Kind == InputEventKind.Mouse)
                ? "The input pastes the clipboard, but the clipboard holds content the agent did not put there. Nothing was sent."
                : null;
        }
    }

    private InputSimulationService CreateSut(IClipboardPasteGuard pasteGuard) =>
        new(_mockMonitorService.Object, _mockInputWrapper.Object, _guard.Object, new InputSession(_mockInputWrapper.Object),
            pasteGuard: pasteGuard);

    [Fact]
    public async Task SendKeys_ThatPaste_AreCheckedRightBeforeTheyAreSent_AndRefusedWithPasteNotAllowed()
    {
        var pasteGuard = new RefusingPasteGuard();
        var sut = CreateSut(pasteGuard);

        var result = await sut.SendKeysAsync("^v");

        result.Success.Should().BeFalse();
        result.ErrorCode.Should().Be(InputErrorCode.PasteNotAllowed);
        result.Error.Should().Contain("Nothing was sent");
        result.ReceivingWindow.Should().NotBeNull("the result names the window the paste was meant for");
        pasteGuard.Checked.Should().ContainSingle().Which.Receiving.Should().Be((nint)0x5150);
        _mockInputWrapper.Verify(w => w.SendInput(It.IsAny<IReadOnlyList<InputEvent>>()), Times.Never);
    }

    [Fact]
    public async Task SendKeys_ThatDoNotPaste_AreSent()
    {
        var sut = CreateSut(new RefusingPasteGuard());

        var result = await sut.SendKeysAsync("hello{ENTER}");

        result.Success.Should().BeTrue();
    }

    [Fact]
    public async Task TypeText_StopsBeforeTheBatchWithTheSynCharacter()
    {
        // The SYN character (U+0016) pastes in an edit control, as Ctrl+V does; the batch before it was typed
        var sut = CreateSut(new RefusingPasteGuard());
        var text = new string('a', InputSimulationService.TypeTextBatchSize) + "\u0016b";

        var result = await sut.TypeTextAsync(text);

        result.Success.Should().BeFalse();
        result.ErrorCode.Should().Be(InputErrorCode.PasteNotAllowed);
        result.CharactersTyped.Should().Be(InputSimulationService.TypeTextBatchSize);
        _sentEvents.Should().HaveCount(InputSimulationService.TypeTextBatchSize * 2);
    }

    [Fact]
    public async Task TypeText_ThatCopiesInOneBatchAndPastesInALaterOne_IsRefusedBeforeAnythingIsTyped()
    {
        // ETX (U+0003) copies in an edit control as Ctrl+C does; the SYN that pastes it comes batches later, in the same call
        var sut = CreateSut(new RefusingPasteGuard());
        var text = "\u0003" + new string('a', InputSimulationService.TypeTextBatchSize * 2) + "\u0016";

        var result = await sut.TypeTextAsync(text);

        result.Success.Should().BeFalse();
        result.ErrorCode.Should().Be(InputErrorCode.PasteNotAllowed);
        result.Error.Should().Contain("separate calls").And.Contain("Nothing was typed");
        result.CharactersTyped.Should().Be(0);
        _sentEvents.Should().BeEmpty();
    }

    [Fact]
    public void RightClick_IsCheckedAsAPaste_AndRefusedWithoutAPress()
    {
        SetupSingleMonitor();
        var pasteGuard = new RefusingPasteGuard();
        var sut = CreateSut(pasteGuard);

        var result = sut.RightClick(750, 400);

        result.Success.Should().BeFalse();
        result.ErrorCode.Should().Be(InputErrorCode.PasteNotAllowed);
        _sentEvents.Should().NotContain(e => e.Kind == InputEventKind.Mouse && (e.Flags & RightDown) != 0);
    }

    [Fact]
    public void LeftClick_IsNotCheckedAsAPaste()
    {
        SetupSingleMonitor();
        var pasteGuard = new RefusingPasteGuard();
        var sut = CreateSut(pasteGuard);

        sut.Click(750, 400).Success.Should().BeTrue();
        pasteGuard.Checked.Should().BeEmpty();
    }

    #endregion
}
