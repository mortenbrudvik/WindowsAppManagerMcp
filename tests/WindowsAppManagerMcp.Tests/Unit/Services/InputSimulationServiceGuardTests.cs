namespace WindowsAppManagerMcp.Tests.Unit.Services;

/// <summary>
/// How InputSimulationService uses the input guard (roadmap M1.5): the guard runs before the first batch
/// and again before every later batch, a refusal sends nothing more, and every result reports the receiving
/// window, the foreground window afterwards and (mouse) the cursor position. The guard and the native input
/// wrapper are mocks, so no real input is sent.
/// </summary>
public class InputSimulationServiceGuardTests
{
    private const uint KeyDown = 0x0000;
    private const uint KeyUp = 0x0002;
    private const uint LeftDown = 0x0002;
    private const uint LeftUp = 0x0004;

    private static readonly WindowSummary Notepad = new(0x200, "Untitled - Notepad", "notepad", 2001);
    private static readonly WindowSummary Terminal = new(0x100, "pwsh", "WindowsTerminal", 1001);
    private static readonly WindowSummary Dialog = new(0x210, "Save As", "notepad", 2001);

    private readonly Mock<IMonitorService> _monitors = TestDataFactory.CreateMockMonitorService(
        [TestDataFactory.CreateMonitorInfo(boundsX: 0, boundsY: 0, boundsWidth: 1920, boundsHeight: 1080)]);
    private readonly Mock<INativeInputWrapper> _input = new();
    private readonly Mock<IInputTargetGuard> _guard = TestDataFactory.CreateAllowingInputGuard(Notepad);
    private readonly List<IReadOnlyList<InputEvent>> _batches = [];

    // Where the fake pointer is: it starts off the point and follows SetCursorPos and absolute moves (1920x1080)
    private (int X, int Y)? _cursor = (501, 302);

    public InputSimulationServiceGuardTests()
    {
        _input.Setup(w => w.SetCursorPos(It.IsAny<int>(), It.IsAny<int>()))
            .Callback((int x, int y) => _cursor = (x, y))
            .Returns(true);
        _input.Setup(w => w.GetCursorPos()).Returns(() => _cursor);
        _input.Setup(w => w.SendInput(It.IsAny<IReadOnlyList<InputEvent>>()))
            .Callback((IReadOnlyList<InputEvent> events) =>
            {
                _batches.Add(events.ToList());
                foreach (var move in events.Where(e => e.Flags == InputEvent.MouseMoveAbsoluteFlags))
                {
                    _cursor = ((int)((long)move.Dx * 1920 / 65536), (int)((long)move.Dy * 1080 / 65536));
                }
            })
            .Returns((IReadOnlyList<InputEvent> events) => (uint)events.Count);
        _guard.Setup(g => g.DescribeForeground()).Returns(Terminal);
    }

    private InputSimulationService CreateSut() => new(_monitors.Object, _input.Object, _guard.Object, new InputSession(_input.Object));

    private static InputGuardResult Refused(InputErrorCode code, WindowSummary? receiving = null, WindowSummary? blocking = null) =>
        InputGuardResult.Refuse(code, $"refused: {code}", InputTarget.None, receiving, blocking);

    private void VerifyNothingSent()
    {
        _input.Verify(w => w.SetCursorPos(It.IsAny<int>(), It.IsAny<int>()), Times.Never);
        _input.Verify(w => w.SendInput(It.IsAny<IReadOnlyList<InputEvent>>()), Times.Never);
    }

    #region Mouse

    [Fact]
    public void Click_PassesTheTargetToTheGuard_AndReportsWhereTheClickWent()
    {
        // Arrange
        var target = InputTarget.Window(0x200);

        // Act
        var result = CreateSut().Click(500, 300, target);

        // Assert
        result.Success.Should().BeTrue();
        _guard.Verify(g => g.CheckMouse(500, 300, target), Times.Once);
        result.ReceivingWindow.Should().Be(Notepad);
        result.ForegroundAfter.Should().Be(Terminal);
        result.ActualX.Should().Be(500);
        result.ActualY.Should().Be(300);
        result.BlockingWindow.Should().BeNull();
    }

    [Theory]
    [InlineData(InputErrorCode.TargetIsAgentHost)]
    [InlineData(InputErrorCode.TargetOccluded)]
    [InlineData(InputErrorCode.TargetElevated)]
    [InlineData(InputErrorCode.TargetDeniedByPolicy)]
    [InlineData(InputErrorCode.DesktopUnavailable)]
    public void Click_GuardRefuses_NothingIsSent_AndTheRefusalIsReported(InputErrorCode code)
    {
        // Arrange
        _guard.Setup(g => g.CheckMouse(500, 300, It.IsAny<InputTarget>())).Returns(Refused(code, Terminal));

        // Act
        var result = CreateSut().Click(500, 300);

        // Assert
        result.Success.Should().BeFalse();
        result.ErrorCode.Should().Be(code);
        result.Error.Should().Be($"refused: {code}");
        result.ReceivingWindow.Should().Be(Terminal);
        result.ForegroundAfter.Should().Be(Terminal);
        result.ActualX.Should().Be(501, "the cursor position is reported even when nothing was sent");
        VerifyNothingSent();
    }

    [Fact]
    public void Click_BlockedByModal_ReportsTheDialog()
    {
        _guard.Setup(g => g.CheckMouse(500, 300, It.IsAny<InputTarget>()))
            .Returns(Refused(InputErrorCode.BlockedByModal, Notepad, Dialog));

        var result = CreateSut().RightClick(500, 300, InputTarget.Window(0x200));

        result.ErrorCode.Should().Be(InputErrorCode.BlockedByModal);
        result.BlockingWindow.Should().Be(Dialog);
        VerifyNothingSent();
    }

    [Fact]
    public void Click_GuardRunsBeforeTheCursorMoves_AndAgainBeforeTheButton()
    {
        // Arrange - record the order of guard and native calls
        var order = new List<string>();
        _guard.Setup(g => g.CheckMouse(500, 300, It.IsAny<InputTarget>()))
            .Callback(() => order.Add("check"))
            .Returns(InputGuardResult.Allow(InputTarget.None, 0x200, Notepad, 0x200));
        _guard.Setup(g => g.RecheckMouse(500, 300, It.IsAny<InputGuardResult>()))
            .Callback(() => order.Add("recheck"))
            .Returns((int _, int _, InputGuardResult approved) => approved);
        _input.Setup(w => w.SetCursorPos(500, 300)).Callback(() => { order.Add("move"); _cursor = (500, 300); }).Returns(true);
        _input.Setup(w => w.SendInput(It.IsAny<IReadOnlyList<InputEvent>>()))
            .Callback(() => order.Add("click"))
            .Returns((IReadOnlyList<InputEvent> events) => (uint)events.Count);

        // Act
        CreateSut().Click(500, 300);

        // Assert
        order.Should().Equal("check", "move", "recheck", "click");
    }

    [Fact]
    public void Click_WindowAtThePointChangesAfterTheCursorMove_TheButtonIsNotPressed()
    {
        // Arrange - hovering opened another app's window at the point
        _guard.Setup(g => g.RecheckMouse(500, 300, It.IsAny<InputGuardResult>()))
            .Returns(Refused(InputErrorCode.TargetOccluded, Terminal));

        // Act
        var result = CreateSut().Click(500, 300, InputTarget.Window(0x200));

        // Assert
        result.ErrorCode.Should().Be(InputErrorCode.TargetOccluded);
        result.Error.Should().StartWith("Nothing was clicked");
        result.ReceivingWindow.Should().Be(Terminal);
        _input.Verify(w => w.SetCursorPos(500, 300), Times.Once);
        _batches.Should().BeEmpty();
    }

    [Fact]
    public void DoubleClick_IsCheckedOnce_RightBeforeTheBatchThatHoldsBothClicks()
    {
        // Roadmap M4.2: both clicks go in one batch, so nothing can come between them to check
        var result = CreateSut().DoubleClick(500, 300, InputTarget.Window(0x200));

        result.Success.Should().BeTrue(result.Error);
        _guard.Verify(g => g.RecheckMouse(500, 300, It.IsAny<InputGuardResult>()), Times.Once);
        _batches.Should().ContainSingle().Which.Select(e => e.Flags).Should().Equal(
            InputEvent.MouseMoveAbsoluteFlags, LeftDown, LeftUp, LeftDown, LeftUp);
    }

    [Fact]
    public void DoubleClick_WindowChangesBeforeTheBatch_NeitherClickIsSent()
    {
        _guard.Setup(g => g.RecheckMouse(500, 300, It.IsAny<InputGuardResult>())).Returns(Refused(InputErrorCode.TargetOccluded, Terminal));

        var result = CreateSut().DoubleClick(500, 300, InputTarget.Window(0x200));

        result.ErrorCode.Should().Be(InputErrorCode.TargetOccluded);
        result.Error.Should().StartWith("Nothing was clicked");
        _batches.Should().BeEmpty();
    }

    [Fact]
    public void MoveMouse_GuardRefuses_TheCursorDoesNotMove()
    {
        _guard.Setup(g => g.CheckMouse(500, 300, It.IsAny<InputTarget>())).Returns(Refused(InputErrorCode.TargetIsAgentHost, Terminal));

        var result = CreateSut().MoveMouse(500, 300);

        result.ErrorCode.Should().Be(InputErrorCode.TargetIsAgentHost);
        result.ReceivingWindow.Should().Be(Terminal);
        VerifyNothingSent();
    }

    [Fact]
    public void MoveMouse_Success_ReportsTheWindowAndTheCursor()
    {
        var result = CreateSut().MoveMouse(500, 300);

        result.Success.Should().BeTrue();
        result.ReceivingWindow.Should().Be(Notepad);
        result.ForegroundAfter.Should().Be(Terminal);
        (result.ActualX, result.ActualY).Should().Be((500, 300));
    }

    [Fact]
    public void Click_CoordinatesOutOfBounds_ReachNeitherTheGuardNorTheDesktop()
    {
        var result = CreateSut().Click(5000, 5000);

        result.ErrorCode.Should().Be(InputErrorCode.CoordinatesOutOfBounds);
        result.ReceivingWindow.Should().BeNull();
        _guard.Verify(g => g.CheckMouse(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<InputTarget>()), Times.Never);
        VerifyNothingSent();
    }

    #endregion

    #region type_text

    [Fact]
    public async Task TypeText_GuardRefuses_NothingIsTyped()
    {
        // Arrange
        _guard.Setup(g => g.CheckKeyboard(It.IsAny<InputTarget>(), KeyboardInputKind.Text, null))
            .Returns(Refused(InputErrorCode.TargetIsAgentHost, Terminal));

        // Act
        var result = await CreateSut().TypeTextAsync("rm -rf ~");

        // Assert
        result.ErrorCode.Should().Be(InputErrorCode.TargetIsAgentHost);
        result.CharactersTyped.Should().Be(0);
        result.ReceivingWindow.Should().Be(Terminal);
        result.ForegroundAfter.Should().Be(Terminal);
        VerifyNothingSent();
    }

    [Fact]
    public async Task TypeText_PassesTheTargetToTheGuard_AndReportsWhereTheTextWent()
    {
        // Arrange
        var target = InputTarget.Window(0x200, allowHostTarget: true);

        // Act
        var result = await CreateSut().TypeTextAsync("Hello", 0, target);

        // Assert
        result.Success.Should().BeTrue();
        result.CharactersTyped.Should().Be(5);
        result.ReceivingWindow.Should().Be(Notepad);
        result.ForegroundAfter.Should().Be(Terminal);
        _guard.Verify(g => g.CheckKeyboard(target, KeyboardInputKind.Text, null), Times.Once);
    }

    [Fact]
    public async Task TypeText_LongText_GoesInBatchesOf32Characters_WithARecheckRightBeforeEachBatch()
    {
        // Arrange
        var text = new string('x', 70);

        // Act
        var result = await CreateSut().TypeTextAsync(text);

        // Assert
        result.Success.Should().BeTrue();
        result.CharactersTyped.Should().Be(70);
        _batches.Select(b => b.Count).Should().Equal(64, 64, 12);
        _guard.Verify(g => g.RecheckKeyboard(It.IsAny<InputGuardResult>()), Times.Exactly(3));
        InputSimulationService.TypeTextBatchSize.Should().Be(32);
    }

    [Fact]
    public async Task TypeText_ForegroundChangesMidway_StopsWithTargetLostFocusAndTheCharactersTyped()
    {
        // Arrange - the third re-check (before characters 65-96) finds another window in front
        var rechecks = 0;
        _guard.Setup(g => g.RecheckKeyboard(It.IsAny<InputGuardResult>()))
            .Returns((InputGuardResult approved) =>
                ++rechecks <= 2 ? approved : Refused(InputErrorCode.TargetLostFocus, Notepad));
        var text = new string('x', 100);

        // Act
        var result = await CreateSut().TypeTextAsync(text);

        // Assert
        result.Success.Should().BeFalse();
        result.ErrorCode.Should().Be(InputErrorCode.TargetLostFocus);
        result.CharactersTyped.Should().Be(64);
        result.Error.Should().Contain("Typing stopped after 64 of 100 characters").And.Contain("Untitled - Notepad");
        result.ReceivingWindow.Should().Be(Notepad);
        result.ForegroundAfter.Should().Be(Terminal);
        _batches.Sum(b => b.Count).Should().Be(128, "only the first two batches were sent");
    }

    [Fact]
    public async Task TypeText_ForegroundChangesBetweenTheCheckAndTheFirstBatch_NothingIsTyped()
    {
        // Arrange - a window came to the front while the guard was reading the target
        _guard.Setup(g => g.RecheckKeyboard(It.IsAny<InputGuardResult>()))
            .Returns(Refused(InputErrorCode.TargetLostFocus, Notepad));

        // Act
        var result = await CreateSut().TypeTextAsync("echo pwned\r");

        // Assert
        result.ErrorCode.Should().Be(InputErrorCode.TargetLostFocus);
        result.CharactersTyped.Should().Be(0);
        result.Error.Should().StartWith("Nothing was typed.");
        VerifyNothingSent();
    }

    [Fact]
    public async Task TypeText_WithADelay_SendsOneCharacterPerBatch_AndRechecksBeforeEach()
    {
        // Act
        var result = await CreateSut().TypeTextAsync("abc", 1);

        // Assert
        result.Success.Should().BeTrue();
        _batches.Should().HaveCount(3);
        _batches[0].Should().Equal(InputEvent.Unicode('a', KeyDown), InputEvent.Unicode('a', KeyUp));
        _guard.Verify(g => g.RecheckKeyboard(It.IsAny<InputGuardResult>()), Times.Exactly(3));
    }

    [Fact]
    public async Task TypeText_SurrogatePairAtABatchBoundary_StaysInOneBatch()
    {
        // Arrange - an emoji (two UTF-16 code units) would straddle the 32-character boundary
        var text = new string('a', 31) + "\U0001F600" + "b";

        // Act
        var result = await CreateSut().TypeTextAsync(text);

        // Assert
        result.CharactersTyped.Should().Be(text.Length);
        _batches.Select(b => b.Count).Should().Equal(66, 2);
    }

    [Fact]
    public async Task TypeText_DelayedSurrogatePair_IsOneBatch()
    {
        await CreateSut().TypeTextAsync("\U0001F600z", 1);

        _batches.Select(b => b.Count).Should().Equal(4, 2);
    }

    #endregion

    #region send_keys

    [Fact]
    public async Task SendKeys_GuardGetsTheParsedKeys_AndTheTarget()
    {
        // Arrange
        var target = InputTarget.Window(0x200);
        IReadOnlyList<InputEvent>? checkedKeys = null;
        _guard.Setup(g => g.CheckKeyboard(target, KeyboardInputKind.Keys, It.IsAny<IReadOnlyList<InputEvent>?>()))
            .Callback((InputTarget _, KeyboardInputKind _, IReadOnlyList<InputEvent>? keys) => checkedKeys = keys)
            .Returns(InputGuardResult.Allow(target, 0x200, Notepad, 0x200));

        // Act
        var result = await CreateSut().SendKeysAsync("^w", target);

        // Assert
        result.Success.Should().BeTrue();
        checkedKeys.Should().Equal(SendKeysParser.Parse("^w"));
        _batches.Should().ContainSingle().Which.Should().Equal(SendKeysParser.Parse("^w"));
        result.ReceivingWindow.Should().Be(Notepad);
        result.ForegroundAfter.Should().Be(Terminal);
    }

    [Theory]
    [InlineData(InputErrorCode.DestructiveChordNeedsTarget)]
    [InlineData(InputErrorCode.TargetNotForeground)]
    [InlineData(InputErrorCode.SessionLocked)]
    public async Task SendKeys_GuardRefuses_NothingIsSent(InputErrorCode code)
    {
        _guard.Setup(g => g.CheckKeyboard(It.IsAny<InputTarget>(), KeyboardInputKind.Keys, It.IsAny<IReadOnlyList<InputEvent>?>()))
            .Returns(Refused(code, Terminal));

        var result = await CreateSut().SendKeysAsync("%{F4}");

        result.ErrorCode.Should().Be(code);
        result.ReceivingWindow.Should().Be(Terminal);
        VerifyNothingSent();
    }

    [Fact]
    public async Task SendKeys_ForegroundChangesBetweenTheCheckAndTheKeys_NothingIsSent()
    {
        // Arrange
        _guard.Setup(g => g.RecheckKeyboard(It.IsAny<InputGuardResult>()))
            .Returns(Refused(InputErrorCode.TargetLostFocus, Terminal));

        // Act
        var result = await CreateSut().SendKeysAsync("{ENTER}", InputTarget.Window(0x200));

        // Assert
        result.ErrorCode.Should().Be(InputErrorCode.TargetLostFocus);
        result.Error.Should().StartWith("Nothing was sent.");
        result.ReceivingWindow.Should().Be(Terminal);
        VerifyNothingSent();
    }

    [Fact]
    public async Task SendKeys_RechecksRightBeforeTheKeys()
    {
        var order = new List<string>();
        _guard.Setup(g => g.RecheckKeyboard(It.IsAny<InputGuardResult>()))
            .Callback(() => order.Add("recheck"))
            .Returns((InputGuardResult approved) => approved);
        _input.Setup(w => w.SendInput(It.IsAny<IReadOnlyList<InputEvent>>()))
            .Callback(() => order.Add("keys"))
            .Returns((IReadOnlyList<InputEvent> events) => (uint)events.Count);

        await CreateSut().SendKeysAsync("{ENTER}");

        order.Should().Equal("recheck", "keys");
    }

    [Fact]
    public async Task SendKeys_UnknownKey_IsReportedBeforeTheGuardRuns()
    {
        var result = await CreateSut().SendKeysAsync("{NOPE}");

        result.ErrorCode.Should().Be(InputErrorCode.UnknownKey);
        _guard.Verify(g => g.CheckKeyboard(It.IsAny<InputTarget>(), It.IsAny<KeyboardInputKind>(), It.IsAny<IReadOnlyList<InputEvent>?>()), Times.Never);
    }

    #endregion
}
