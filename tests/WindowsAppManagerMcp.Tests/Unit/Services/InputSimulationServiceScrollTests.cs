namespace WindowsAppManagerMcp.Tests.Unit.Services;

/// <summary>
/// scroll (roadmap M2.15): the wheel turns at a checked point (or the target's client centre), in one SendInput batch
/// with a move to that point, one event per notch, only where the wheel reaches the window at the point, and through
/// the same guard and session checks as a click. The native input layer is <see cref="FakeInputWrapper"/>, the guard a
/// mock: nothing reaches the desktop.
/// </summary>
public class InputSimulationServiceScrollTests
{
    private const uint MouseWheel = 0x0800, MouseHorizontalWheel = 0x1000;
    private static readonly InputTarget NotepadTarget = InputTarget.Window(0x200);
    private static readonly InputTarget ListTarget = InputTarget.Window(0x210);

    private static readonly WindowSummary Notepad = new(0x200, "Untitled - Notepad", "notepad", 2001);
    private static readonly WindowSummary Terminal = new(0x100, "pwsh", "WindowsTerminal", 1001);

    private readonly FakeInputWrapper _input = new();
    private readonly Mock<IInputTargetGuard> _guard = TestDataFactory.CreateAllowingInputGuard(Notepad);
    private readonly Mock<INativeHitTestWrapper> _hitTest = new();
    private readonly Mock<INativeSystemInfoWrapper> _systemInfo = new();
    private readonly Mock<IMonitorService> _monitors = TestDataFactory.CreateMockMonitorService(
        [TestDataFactory.CreateMonitorInfo(boundsX: 0, boundsY: 0, boundsWidth: 1920, boundsHeight: 1080)]);
    private readonly InputSession _session;

    public InputSimulationServiceScrollTests()
    {
        _session = new InputSession(_input);
        _systemInfo.Setup(s => s.GetMouseWheelRouting()).Returns(MouseWheelRouting.MousePosition);
        _input.MovePointer(5, 5);
    }

    private InputSimulationService CreateSut(IClipboardOrigin? clipboardOrigin = null) =>
        new(_monitors.Object, _input, _guard.Object, _session, hitTest: _hitTest.Object, systemInfo: _systemInfo.Object,
            clipboardOrigin: clipboardOrigin);

    private static InputEvent[] Wheel(int notches, int delta) => Enumerable.Repeat(InputEvent.Wheel(delta), notches).ToArray();

    [Fact]
    public void Scroll_Down_MovesToThePoint_AndSendsOneNegativeNotchPerStep_InOneBatch()
    {
        // Act
        var result = CreateSut().Scroll(500, 300, deltaY: 3, deltaX: 0, NotepadTarget, false, default);

        // Assert
        result.Success.Should().BeTrue(result.Error);
        result.X.Should().Be(500);
        result.Y.Should().Be(300);
        result.DeltaY.Should().Be(3);
        result.ReceivingWindow.Should().Be(Notepad);
        result.ForegroundAfter.Should().Be(Notepad);
        result.ActualX.Should().Be(500);
        result.ActualY.Should().Be(300);
        _guard.Verify(g => g.CheckMouse(500, 300, NotepadTarget), Times.Once);
        _guard.Verify(g => g.RecheckMouse(500, 300, It.IsAny<InputGuardResult>()), Times.Once);

        var (dx, dy) = InputBatchBuilder.ToAbsolute(500, 300, new VirtualScreenBounds(0, 0, 1920, 1080));
        _input.Batches.Should().ContainSingle().Which.Should().Equal(
            [InputEvent.MouseMoveAbsolute(dx, dy), .. Wheel(3, -120)]);
    }

    [Fact]
    public void WheelEvents_CarryTheirDelta_AsMouseData()
    {
        InputEvent.Wheel(-120).Should().Be(new InputEvent(InputEventKind.Mouse, 0, MouseWheel, MouseData: unchecked((uint)-120)));
        InputEvent.HorizontalWheel(120).MouseData.Should().Be(120u);
        InputEvent.HorizontalWheel(120).Flags.Should().Be(MouseHorizontalWheel);
    }

    [Fact]
    public void Scroll_WithoutAPoint_TurnsTheWheelAtTheCentreOfTheTargetsClientArea()
    {
        // Arrange - a list control's client area at (100, 200), 300 x 400
        _hitTest.Setup(h => h.GetClientAreaOnScreen(0x210)).Returns(new WindowRect(100, 200, 300, 400));

        // Act
        var result = CreateSut().Scroll(null, null, deltaY: 1, deltaX: 0, ListTarget, false, default);

        // Assert
        result.Success.Should().BeTrue(result.Error);
        (result.X, result.Y).Should().Be((250, 400));
        _guard.Verify(g => g.CheckMouse(250, 400, ListTarget), Times.Once);
        _input.Batches.Should().ContainSingle();
    }

    [Fact]
    public void Scroll_WithoutAPoint_OnAHandleThatIsNoWindow_IsInvalidHandle_AndNothingIsSent()
    {
        _hitTest.Setup(h => h.GetClientAreaOnScreen(It.IsAny<nint>())).Returns((WindowRect?)null);

        var result = CreateSut().Scroll(null, null, 1, 0, InputTarget.Window(0x999), false, default);

        result.ErrorCode.Should().Be(InputErrorCode.InvalidHandle);
        result.Error.Should().Contain("find_windows");
        _input.Batches.Should().BeEmpty();
        _input.CursorMoves.Should().Be(0);
    }

    [Fact]
    public void Scroll_WithoutAPoint_OnAMinimizedWindow_IsCoordinatesOutOfBounds()
    {
        _hitTest.Setup(h => h.GetClientAreaOnScreen(0x200)).Returns(new WindowRect(-32000, -32000, 0, 0));

        var result = CreateSut().Scroll(null, null, 1, 0, InputTarget.Window(0x200), false, default);

        result.ErrorCode.Should().Be(InputErrorCode.CoordinatesOutOfBounds);
        result.Error.Should().Contain("minimized");
        _input.Batches.Should().BeEmpty();
    }

    [Fact]
    public void Scroll_OffEveryMonitor_IsCoordinatesOutOfBounds_WithoutTheGuard()
    {
        var result = CreateSut().Scroll(5000, 300, 1, 0, InputTarget.None, false, default);

        result.ErrorCode.Should().Be(InputErrorCode.CoordinatesOutOfBounds);
        _guard.Verify(g => g.CheckMouse(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<InputTarget>()), Times.Never);
        _input.Batches.Should().BeEmpty();
    }

    [Theory]
    [InlineData(InputErrorCode.TargetIsAgentHost)]
    [InlineData(InputErrorCode.TargetOccluded)]
    [InlineData(InputErrorCode.TargetDeniedByPolicy)]
    [InlineData(InputErrorCode.BlockedByModal)]
    public void Scroll_TheGuardRefuses_NothingIsSent_AndTheRefusalIsReported(InputErrorCode code)
    {
        _guard.Setup(g => g.CheckMouse(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<InputTarget>()))
            .Returns(InputGuardResult.Refuse(code, $"refused: {code}", InputTarget.None, Terminal));

        var result = CreateSut().Scroll(500, 300, 2, 0, InputTarget.None, false, default);

        result.Success.Should().BeFalse();
        result.ErrorCode.Should().Be(code);
        result.ReceivingWindow.Should().Be(Terminal);
        _input.Batches.Should().BeEmpty();
        _input.CursorMoves.Should().Be(0);
    }

    [Fact]
    public void Scroll_APopupAtThePointAfterTheMove_IsRefused_AndNoNotchIsSent()
    {
        _guard.Setup(g => g.RecheckMouse(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<InputGuardResult>()))
            .Returns(InputGuardResult.Refuse(InputErrorCode.TargetOccluded, "a tooltip of another app", InputTarget.None, Terminal));

        var result = CreateSut().Scroll(500, 300, 2, 0, InputTarget.None, false, default);

        result.ErrorCode.Should().Be(InputErrorCode.TargetOccluded);
        result.Error.Should().StartWith("Nothing was scrolled.");
        _input.Batches.Should().BeEmpty();
    }

    #region Where the wheel goes

    [Theory]
    [InlineData(MouseWheelRouting.Focus)]
    [InlineData(MouseWheelRouting.Hybrid)]
    public void WhenTheWheelFollowsTheFocus_AndAnotherWindowIsInFront_ItIsWheelFollowsFocus_AndThePointerStays(MouseWheelRouting routing)
    {
        // Arrange - the agent's terminal is in front: a wheel that follows the focus would scroll it
        _systemInfo.Setup(s => s.GetMouseWheelRouting()).Returns(routing);
        _guard.Setup(g => g.DescribeForeground()).Returns(Terminal);

        // Act
        var result = CreateSut().Scroll(500, 300, 2, 0, InputTarget.Window(0x200), false, default);

        // Assert
        result.ErrorCode.Should().Be(InputErrorCode.WheelFollowsFocus);
        result.Error.Should().Contain("Scroll inactive windows").And.Contain("focus_window").And.Contain("pwsh").And.Contain("Notepad");
        result.ReceivingWindow.Should().Be(Notepad);
        _input.Batches.Should().BeEmpty();
        _input.CursorMoves.Should().Be(0);
    }

    [Fact]
    public void WhenTheWheelFollowsTheFocus_AndTheWindowAtThePointIsInFront_ItIsSent()
    {
        _systemInfo.Setup(s => s.GetMouseWheelRouting()).Returns(MouseWheelRouting.Focus);

        var result = CreateSut().Scroll(500, 300, 2, 0, InputTarget.Window(0x200), false, default);

        result.Success.Should().BeTrue(result.Error);
        _input.Batches.Should().ContainSingle();
    }

    [Fact]
    public void WhenTheRoutingCannotBeRead_TheWheelIsSentOnlyToTheWindowInFront()
    {
        _systemInfo.Setup(s => s.GetMouseWheelRouting()).Returns((MouseWheelRouting?)null);
        _guard.Setup(g => g.DescribeForeground()).Returns(Terminal);

        var result = CreateSut().Scroll(500, 300, 2, 0, InputTarget.None, false, default);

        result.ErrorCode.Should().Be(InputErrorCode.WheelFollowsFocus);
        result.Error.Should().Contain("cannot be read");
        _input.Batches.Should().BeEmpty();
    }

    [Fact]
    public void WithoutTheSystemInfo_TheRoutingIsUnknown_AndTheWheelGoesOnlyToTheWindowInFront()
    {
        _guard.Setup(g => g.DescribeForeground()).Returns(Terminal);
        var sut = new InputSimulationService(_monitors.Object, _input, _guard.Object, _session);

        var result = sut.Scroll(500, 300, 2, 0, InputTarget.None, false, default);

        result.ErrorCode.Should().Be(InputErrorCode.WheelFollowsFocus);
    }

    [Fact]
    public void AnotherWindowComingToTheFront_WhileThePointerMoves_StopsTheWheel()
    {
        // Arrange - focus routing; the terminal takes the foreground after the first check
        _systemInfo.Setup(s => s.GetMouseWheelRouting()).Returns(MouseWheelRouting.Focus);
        var foreground = Notepad;
        _guard.Setup(g => g.DescribeForeground()).Returns(() => foreground);
        _input.AfterSetCursorPos = (_, _) => foreground = Terminal;

        // Act
        var result = CreateSut().Scroll(500, 300, 2, 0, InputTarget.Window(0x200), false, default);

        // Assert
        result.ErrorCode.Should().Be(InputErrorCode.WheelFollowsFocus);
        _input.Batches.Should().BeEmpty();
    }

    #endregion

    #region Modifiers held around the wheel (issue #8)

    private const uint KeyUp = 0x0002;
    private const ushort VkLControl = 0xA2, VkLShift = 0xA0, VkLMenu = 0xA4;

    private static InputEvent MoveTo(int x, int y)
    {
        var (dx, dy) = InputBatchBuilder.ToAbsolute(x, y, new VirtualScreenBounds(0, 0, 1920, 1080));
        return InputEvent.MouseMoveAbsolute(dx, dy);
    }

    private void VerifyNothingIsDown()
    {
        _session.ServerHeld.Should().BeEmpty();
        foreach (var key in new ushort[] { VkLControl, VkLShift, VkLMenu, 0x5B, 0x10, 0x11, 0x12 })
        {
            _input.IsKeyDown(key).Should().BeFalse($"key 0x{key:X2} must not stay down");
        }
    }

    [Fact]
    public void Scroll_WithModifiers_SendsTheirKeyDowns_TheMove_TheNotches_AndTheKeyUps_InOneBatch()
    {
        // Issue #8: Ctrl+wheel zooms (or sizes the font) and Shift+wheel scrolls sideways; no key stays held between calls,
        // so the modifiers go around the notches in the one batch, as mouse_click's go around its clicks
        var result = CreateSut().Scroll(500, 300, 2, 0, [MouseModifier.Ctrl, MouseModifier.Shift], NotepadTarget, false, default);

        result.Success.Should().BeTrue(result.Error);
        result.Modifiers.Should().Equal("ctrl", "shift");
        _input.Batches.Should().ContainSingle().Which.Should().Equal(
        [
            InputEvent.Key(VkLControl, 0), InputEvent.Key(VkLShift, 0), MoveTo(500, 300), .. Wheel(2, -120),
            InputEvent.Key(VkLShift, KeyUp), InputEvent.Key(VkLControl, KeyUp)
        ]);
        VerifyNothingIsDown();
    }

    [Fact]
    public void Scroll_WithoutModifiers_ReportsNone_AndReadsNoLayout()
    {
        var layouts = new FakeKeyboardLayoutWrapper();
        var sut = new InputSimulationService(_monitors.Object, _input, _guard.Object, _session, hitTest: _hitTest.Object,
            systemInfo: _systemInfo.Object, keyboardLayout: layouts);

        var result = sut.Scroll(500, 300, 1, 0, [], NotepadTarget, false, default);

        result.Modifiers.Should().BeNull();
        layouts.LayoutQueries.Should().BeEmpty();
        _input.Events.Should().NotContain(e => e.Kind == InputEventKind.VirtualKey);
    }

    [Fact]
    public void Scroll_TheModifiers_CarryTheTargetLayoutsScanCodes_AndAreSentOnce()
    {
        var layouts = new FakeKeyboardLayoutWrapper { Layout = FakeKeyboardLayoutWrapper.Us };
        var sut = new InputSimulationService(_monitors.Object, _input, _guard.Object, _session, hitTest: _hitTest.Object,
            systemInfo: _systemInfo.Object, keyboardLayout: layouts);

        var result = sut.Scroll(500, 300, 0, 3, [MouseModifier.Ctrl, MouseModifier.Ctrl], NotepadTarget, false, default);

        result.Modifiers.Should().Equal("ctrl");
        _input.Batches.Should().ContainSingle().Which.Where(e => e.Kind == InputEventKind.VirtualKey).Select(e => e.Scan).Should().Equal(0x1D, 0x1D);
        layouts.LayoutQueries.Should().Equal((nint)0x200);
    }

    [Fact]
    public void Scroll_WithAlt_MasksItBeforeItsRelease_SoNoMenuOpens()
    {
        CreateSut().Scroll(500, 300, 1, 0, [MouseModifier.Alt], NotepadTarget, false, default).Success.Should().BeTrue();

        _input.Batches.Should().ContainSingle().Which.TakeLast(3).Should().Equal(
            InputEvent.Key(HeldInputTracker.MaskKey, 0), InputEvent.Key(HeldInputTracker.MaskKey, KeyUp), InputEvent.Key(VkLMenu, KeyUp));
        VerifyNothingIsDown();
    }

    [Fact]
    public void Scroll_WithModifiers_WhileTheUserHoldsOne_IsModifiersHeld_AndSendsNothing()
    {
        // The call's own modifiers are not the user's: a key already down is refused as for every input call
        _input.Press(VkLShift);

        var result = CreateSut().Scroll(500, 300, 1, 0, [MouseModifier.Ctrl], NotepadTarget, false, default);

        result.ErrorCode.Should().Be(InputErrorCode.ModifiersHeld);
        _input.Batches.Should().BeEmpty();
    }

    [Fact]
    public void Scroll_WithModifiers_AndReleaseHeldModifiers_ReleasesTheUsersKeyFirst_ThenScrolls()
    {
        // A stuck Windows key (Shift, Ctrl and Alt are the default pause chord's, which are never released for the agent)
        _input.Press(0x5B);

        var result = CreateSut().Scroll(500, 300, 1, 0, [MouseModifier.Ctrl], NotepadTarget, releaseHeldModifiers: true, default);

        result.Success.Should().BeTrue(result.Error);
        result.ReleasedModifiers.Should().NotBeNullOrEmpty();
        _input.Batches.Last().First().Should().Be(InputEvent.Key(VkLControl, 0));
        VerifyNothingIsDown();
    }

    [Fact]
    public void Scroll_WithModifiers_WhilePaused_SendsNothing()
    {
        _session.Pause(InputControlSources.Hotkey);

        var result = CreateSut().Scroll(500, 300, 1, 0, [MouseModifier.Ctrl], NotepadTarget, false, default);

        result.ErrorCode.Should().Be(InputErrorCode.PausedByUser);
        _input.Batches.Should().BeEmpty();
    }

    [Fact]
    public void Scroll_ABatchCutShortAfterTheModifiers_ReleasesThem_AndSaysNoNotchWasScrolled()
    {
        // SendInput inserts the two modifier key-downs and stops: the session sends them up at once
        _input.Insert = (events, call) => call == 0 ? 2u : (uint)events.Count;

        var result = CreateSut().Scroll(500, 300, 3, 0, [MouseModifier.Ctrl, MouseModifier.Shift], NotepadTarget, false, default);

        result.ErrorCode.Should().Be(InputErrorCode.SendInputFailed);
        result.Error.Should().Contain("the modifier keys").And.Contain("0 of 3 notches");
        VerifyNothingIsDown();
    }

    #endregion

    [Fact]
    public void APartlyInsertedBatch_IsSendInputFailed_WithTheNotchesScrolled()
    {
        // The move and 2 of 5 notches went in
        _input.Insert = (_, _) => 3;

        var result = CreateSut().Scroll(500, 300, 5, 0, InputTarget.None, false, default);

        result.ErrorCode.Should().Be(InputErrorCode.SendInputFailed);
        result.Error.Should().Contain("2 of 5 notches");
    }

    [Fact]
    public void Scroll_ThePointerConfinedElsewhere_IsCursorNotAtPoint_AndNoNotchIsSent()
    {
        // A game or remote session holds the pointer: the wheel would turn where it is held
        _input.AfterSetCursorPos = (_, _) => _input.MovePointer(10, 10);

        var result = CreateSut().Scroll(500, 300, 2, 0, NotepadTarget, false, default);

        result.ErrorCode.Should().Be(InputErrorCode.CursorNotAtPoint);
        result.Error.Should().StartWith("Nothing was scrolled.").And.Contain("(10, 10)");
        _input.Batches.Should().BeEmpty();
    }

    [Fact]
    public void Scroll_APauseRightBeforeTheWheel_IsPausedByUser_AndNoNotchIsSent()
    {
        // The user pauses after the guard's last look at the point
        _guard.Setup(g => g.RecheckMouse(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<InputGuardResult>()))
            .Returns((int _, int _, InputGuardResult approved) =>
            {
                _session.Pause(InputControlSources.Hotkey);
                return approved;
            });

        var result = CreateSut().Scroll(500, 300, 2, 0, NotepadTarget, false, default);

        result.ErrorCode.Should().Be(InputErrorCode.PausedByUser);
        _input.Batches.Should().BeEmpty();
    }

    [Theory]
    [InlineData(100, null, 1, 0)] // only x
    [InlineData(null, null, 1, 0)] // no point and no target (below)
    [InlineData(100, 100, 0, 0)] // nothing to scroll
    [InlineData(100, 100, 101, 0)]
    [InlineData(100, 100, 0, -101)]
    [InlineData(100, 100, int.MinValue, 0)] // no overflow
    [InlineData(100, 100, 0, int.MinValue)]
    public void InvalidArguments_Throw_AndSendNothing(int? x, int? y, int deltaY, int deltaX)
    {
        var act = () => CreateSut().Scroll(x, y, deltaY, deltaX, InputTarget.None, false, default);

        act.Should().Throw<ArgumentException>();
        _input.Batches.Should().BeEmpty();
    }

    #region The agent's copies (get_clipboard, roadmap M2.16)

    private ClipboardOrigin NewOrigin(FakeClipboardWrapper clipboard) => new(clipboard, new ManualClock(), Timeout.InfiniteTimeSpan);

    [Fact]
    public async Task SendKeys_CopyKeys_StartTheCopyWindow()
    {
        var clipboard = new FakeClipboardWrapper();
        var origin = NewOrigin(clipboard);

        var result = await CreateSut(origin).SendKeysAsync("^c", InputTarget.Window(0x200), false, 0, default);

        result.Success.Should().BeTrue(result.Error);
        origin.IsCopyPending(clipboard.Sequence).Should().BeTrue("the app has not copied yet");
        clipboard.AppCopies(Notepad.ProcessId, "the selection");
        origin.Classify(clipboard.Sequence).Should().Be(ClipboardOriginKind.AgentInput);
    }

    [Fact]
    public async Task SendKeys_ACopyByAnotherProcessThanTheWindowInFront_IsNotTheAgents()
    {
        var clipboard = new FakeClipboardWrapper();
        var origin = NewOrigin(clipboard);

        (await CreateSut(origin).SendKeysAsync("^c", InputTarget.Window(0x200), false, 0, default)).Success.Should().BeTrue();
        clipboard.AppCopies(Terminal.ProcessId, "what the user copied in the terminal");

        origin.Classify(clipboard.Sequence).Should().Be(ClipboardOriginKind.NotFromAgent);
    }

    [Fact]
    public void Click_StartsTheCopyWindow()
    {
        // A click on a Copy menu item or button copies
        var clipboard = new FakeClipboardWrapper();
        var origin = NewOrigin(clipboard);

        CreateSut(origin).Click(500, 300, InputTarget.Window(0x200), false, default).Success.Should().BeTrue();
        clipboard.AppCopies(Notepad.ProcessId, "the selection");

        origin.Classify(clipboard.Sequence).Should().Be(ClipboardOriginKind.AgentInput, "the click went to Notepad, which copied");
        origin.IsCopyPending(clipboard.Sequence).Should().BeFalse();
    }

    [Fact]
    public async Task TypedText_AScroll_AndOtherKeys_DoNotStartTheCopyWindow()
    {
        var clipboard = new FakeClipboardWrapper();
        var origin = NewOrigin(clipboard);
        var sut = CreateSut(origin);

        (await sut.TypeTextAsync("^c", 0, InputTarget.Window(0x200), false, null, default)).Success.Should().BeTrue();
        (await sut.SendKeysAsync("^v", InputTarget.Window(0x200), false, 0, default)).Success.Should().BeTrue();
        sut.Scroll(500, 300, 1, 0, InputTarget.None, false, default).Success.Should().BeTrue();
        sut.RightClick(500, 300, InputTarget.None, false, default).Success.Should().BeTrue();
        clipboard.AppCopies(Notepad.ProcessId, "copied by the app on its own");

        origin.Classify(clipboard.Sequence).Should().Be(ClipboardOriginKind.NotFromAgent);
    }

    [Fact]
    public async Task CopyKeysThatWereNotSent_DoNotStartTheCopyWindow()
    {
        var clipboard = new FakeClipboardWrapper();
        var origin = NewOrigin(clipboard);
        _input.Insert = (_, _) => 0;

        var result = await CreateSut(origin).SendKeysAsync("^c", InputTarget.Window(0x200), false, 0, default);

        result.ErrorCode.Should().Be(InputErrorCode.SendInputFailed);
        clipboard.AppCopies(Notepad.ProcessId, "copied by the app on its own");
        origin.Classify(clipboard.Sequence).Should().Be(ClipboardOriginKind.NotFromAgent);
    }

    #endregion
}
