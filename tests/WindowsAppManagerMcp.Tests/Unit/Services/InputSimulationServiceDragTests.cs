namespace WindowsAppManagerMcp.Tests.Unit.Services;

/// <summary>
/// drag (roadmap M4.3): the press at the from point, the moves, the drop at the to point, the guard's checks of both points,
/// and that nothing stays held whatever stops it: a refused drop, a pointer kept off the drop, the user's pause, a cancelled
/// request, a short SendInput count. The native input layer is <see cref="FakeInputWrapper"/> and the guard a mock, so
/// nothing reaches the desktop.
/// </summary>
public class InputSimulationServiceDragTests
{
    private const uint KeyDown = 0x0000, KeyUp = 0x0002;
    private const uint LeftDown = 0x0002, LeftUp = 0x0004, XDown = 0x0080, XUp = 0x0100;
    private const ushort VkLControl = 0xA2;
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);
    private static readonly InputTarget Source = InputTarget.Window(0x5150);
    private static readonly InputTarget DropWindow = InputTarget.Window(0x6160);

    private readonly FakeInputWrapper _input = new();
    private readonly Mock<IInputTargetGuard> _guard = TestDataFactory.CreateAllowingInputGuard();
    private readonly Mock<INativeSystemInfoWrapper> _systemInfo = new();
    private readonly Mock<IMonitorService> _monitors = TestDataFactory.CreateMockMonitorService(
    [
        TestDataFactory.CreateMonitorInfo(boundsX: 0, boundsY: 0, boundsWidth: 1920, boundsHeight: 1080)
    ]);

    private readonly InputSession _session;

    public InputSimulationServiceDragTests()
    {
        _session = new InputSession(_input);
        _systemInfo.Setup(s => s.GetVirtualScreen()).Returns(new VirtualScreenBounds(0, 0, 1920, 1080));
        _systemInfo.Setup(s => s.AreMouseButtonsSwapped()).Returns(false);
        _systemInfo.Setup(s => s.GetDragThreshold()).Returns((4, 4));
    }

    private InputSimulationService CreateSut() =>
        new(_monitors.Object, _input, _guard.Object, _session, systemInfo: _systemInfo.Object);

    private static DragRequest Request(MouseButton button = MouseButton.Primary, MouseModifier[]? modifiers = null, int steps = 4,
        int stepDelayMs = 0, int holdBeforeMoveMs = 0) =>
        new(100, 100, 300, 100, button, modifiers ?? [], steps, stepDelayMs, holdBeforeMoveMs);

    private static InputEvent MoveTo(int x, int y)
    {
        var (dx, dy) = InputBatchBuilder.ToAbsolute(x, y, new VirtualScreenBounds(0, 0, 1920, 1080));
        return InputEvent.MouseMoveAbsolute(dx, dy);
    }

    private void VerifyNothingIsDown()
    {
        _session.ServerHeld.Should().BeEmpty();
        foreach (var key in new ushort[] { 0x01, 0x02, 0x04, 0x05, 0x06, VkLControl, 0x11 })
        {
            _input.IsKeyDown(key).Should().BeFalse($"key 0x{key:X2} must not stay down");
        }
    }

    private void VerifyNothingSent()
    {
        _input.Batches.Should().BeEmpty();
        _input.CursorMoves.Should().Be(0);
    }

    [Fact]
    public async Task Drag_PressesAtFrom_MovesInSteps_AndReleasesAtTo()
    {
        var result = await CreateSut().DragAsync(Request(), Source, DropWindow, false, CancellationToken.None);

        result.Success.Should().BeTrue();
        result.Pressed.Should().BeTrue();
        result.Button.Should().Be("primary");
        (result.ActualX, result.ActualY).Should().Be((300, 100));
        _input.Batches.Should().HaveCount(6);
        _input.Batches[0].Should().Equal(MoveTo(100, 100), InputEvent.Mouse(LeftDown));
        _input.Batches.Skip(1).Take(4).Select(b => b.Single()).Should().Equal(
            MoveTo(150, 100), MoveTo(200, 100), MoveTo(250, 100), MoveTo(300, 100));
        _input.Batches[5].Should().Equal(MoveTo(300, 100), InputEvent.Mouse(LeftUp));
        VerifyNothingIsDown();
    }

    [Fact]
    public async Task Drag_ChecksFromAgainstTheTarget_AndToAgainstTheDropTarget_ThenBothAgain()
    {
        await CreateSut().DragAsync(Request(), Source, DropWindow, false, CancellationToken.None);

        _guard.Verify(g => g.CheckMouse(100, 100, Source), Times.Once);
        _guard.Verify(g => g.CheckMouse(300, 100, DropWindow), Times.Once);
        _guard.Verify(g => g.RecheckMouse(100, 100, It.Is<InputGuardResult>(r => r.Target == Source)), Times.Once);
        _guard.Verify(g => g.RecheckDrop(300, 100, It.Is<InputGuardResult>(r => r.Target == DropWindow)), Times.Once);
    }

    [Fact]
    public async Task Drag_WithModifiers_HoldsThemFromBeforeThePressUntilAfterTheRelease()
    {
        var result = await CreateSut().DragAsync(Request(modifiers: [MouseModifier.Ctrl]), Source, Source, false, CancellationToken.None);

        result.Modifiers.Should().Equal("ctrl");
        _input.Batches[0].Should().Equal(InputEvent.Key(VkLControl, KeyDown), MoveTo(100, 100), InputEvent.Mouse(LeftDown));
        _input.Batches[^1].Should().Equal(MoveTo(300, 100), InputEvent.Mouse(LeftUp), InputEvent.Key(VkLControl, KeyUp));
        VerifyNothingIsDown();
    }

    [Fact]
    public async Task Drag_TheModifiers_CarryTheLayoutsScanCodes_AtThePress_TheDrop_AndACancel()
    {
        var layouts = new FakeKeyboardLayoutWrapper { Layout = FakeKeyboardLayoutWrapper.Us };
        var sut = new InputSimulationService(_monitors.Object, _input, _guard.Object, _session, systemInfo: _systemInfo.Object,
            keyboardLayout: layouts);

        await sut.DragAsync(Request(modifiers: [MouseModifier.Ctrl]), Source, Source, false, CancellationToken.None);
        _guard.Setup(g => g.RecheckDrop(300, 100, It.IsAny<InputGuardResult>())).Returns(
            InputGuardResult.Refuse(InputErrorCode.TargetOccluded, "covered", Source));
        await sut.DragAsync(Request(modifiers: [MouseModifier.Ctrl]), Source, Source, false, CancellationToken.None);

        _input.Events.Where(e => e.Kind == InputEventKind.VirtualKey).Should().OnlyContain(e => e.Scan == 0x1D)
            .And.HaveCount(4, "a down and an up in each drag, the second released by the cancel");
        VerifyNothingIsDown();
    }

    [Fact]
    public async Task Drag_WithAnXButton_PressesAndReleasesThatButton()
    {
        await CreateSut().DragAsync(Request(button: MouseButton.X1, steps: 1), Source, Source, false, CancellationToken.None);

        _input.Batches[0][^1].Should().Be(InputEvent.Mouse(XDown, 1));
        _input.Batches[^1][1].Should().Be(InputEvent.Mouse(XUp, 1));
        VerifyNothingIsDown();
    }

    [Fact]
    public async Task Drag_TheFirstMoveLeavesTheDragRectangle()
    {
        // 200 pixels in 100 steps is 2 a step, inside SM_CXDRAG: the first move goes beyond twice the threshold
        await CreateSut().DragAsync(Request(steps: 100), Source, Source, false, CancellationToken.None);

        var firstMove = _input.Batches[1].Single();
        var x = (int)((long)firstMove.Dx * 1920 / 65536);
        x.Should().BeGreaterThan(100 + 8);
    }

    // ---- A right or middle press in a terminal pastes (review F6) ----

    /// <summary>
    /// The service with the real paste rule over a fake clipboard that holds what the user copied, and the window at the from
    /// point (the allowing guard's 0x5150) of class <paramref name="receivingClass"/>.
    /// </summary>
    private InputSimulationService CreateSutWithThePasteRule(string receivingClass, FakeClipboardWrapper clipboard)
    {
        var origin = new ClipboardOrigin(clipboard, new ManualClock(), System.Threading.Timeout.InfiniteTimeSpan);
        var windows = new Mock<INativeWindowWrapper>();
        windows.Setup(w => w.GetClassName(0x5150)).Returns(receivingClass);
        windows.Setup(w => w.GetWindowThreadProcessId(0x5150)).Returns(77u);
        windows.Setup(w => w.GetProcessName(77u)).Returns(receivingClass == "Notepad" ? "notepad" : "WindowsTerminal");
        return new InputSimulationService(_monitors.Object, _input, _guard.Object, _session, systemInfo: _systemInfo.Object,
            clipboardOrigin: origin, pasteGuard: new ClipboardPasteGuard(clipboard, origin, ClipboardOptions.Default, windows.Object));
    }

    [Theory]
    [InlineData(MouseButton.Secondary)]
    [InlineData(MouseButton.Middle)]
    public async Task Drag_WithTheRightOrMiddleButton_InATerminal_WithTheUsersClipboard_IsPasteNotAllowed_AndSendsNoPress(MouseButton button)
    {
        // Arrange - the press alone would paste what the user copied into the terminal, where the agent can read it
        var clipboard = new FakeClipboardWrapper();
        clipboard.UserCopies("hunter2");
        var sut = CreateSutWithThePasteRule("CASCADIA_HOSTING_WINDOW_CLASS", clipboard);

        // Act
        var result = await sut.DragAsync(Request(button: button), Source, Source, false, CancellationToken.None);

        // Assert
        result.Success.Should().BeFalse();
        result.ErrorCode.Should().Be(InputErrorCode.PasteNotAllowed);
        result.Pressed.Should().BeFalse();
        result.Error.Should().StartWith(NothingDraggedText).And.Contain("terminal").And.NotContain("hunter2");
        _input.Batches.Should().BeEmpty("nothing is pressed");
        VerifyNothingIsDown();
    }

    private const string NothingDraggedText = "Nothing was dragged.";

    [Fact]
    public async Task Drag_WithThePrimaryButton_InATerminal_IsNotAPaste()
    {
        var clipboard = new FakeClipboardWrapper();
        clipboard.UserCopies("hunter2");
        var sut = CreateSutWithThePasteRule("CASCADIA_HOSTING_WINDOW_CLASS", clipboard);

        var result = await sut.DragAsync(Request(), Source, Source, false, CancellationToken.None);

        result.Success.Should().BeTrue(result.Error);
        clipboard.Reads.Should().Be(0, "a left press pastes nothing, so the clipboard is not looked at");
    }

    [Fact]
    public async Task Drag_WithTheRightButton_OutsideATerminal_IsNotAPaste()
    {
        var clipboard = new FakeClipboardWrapper();
        clipboard.UserCopies("hunter2");
        var sut = CreateSutWithThePasteRule("Notepad", clipboard);

        var result = await sut.DragAsync(Request(button: MouseButton.Secondary), Source, Source, false, CancellationToken.None);

        result.Success.Should().BeTrue(result.Error);
    }

    [Fact]
    public async Task Drag_ADropPointTheGuardRefuses_SendsNothing()
    {
        // A drop on a denied app is input to that app: refused before anything is pressed
        var denied = new WindowSummary(0x7170, "Passwords", "keepass", 7170);
        _guard.Setup(g => g.CheckMouse(300, 100, DropWindow)).Returns(
            InputGuardResult.Refuse(InputErrorCode.TargetDeniedByPolicy, "The user's policy denies keepass.", DropWindow, denied));

        var result = await CreateSut().DragAsync(Request(), Source, DropWindow, false, CancellationToken.None);

        result.ErrorCode.Should().Be(InputErrorCode.TargetDeniedByPolicy);
        result.Error.Should().StartWith("The to point:");
        result.Pressed.Should().BeFalse();
        result.DropWindow.Should().Be(denied);
        result.ReceivingWindow.Should().NotBeNull();
        VerifyNothingSent();
    }

    [Fact]
    public async Task Drag_AFromPointTheGuardRefuses_SendsNothing_AndDoesNotCheckTheDrop()
    {
        _guard.Setup(g => g.CheckMouse(100, 100, Source)).Returns(
            InputGuardResult.Refuse(InputErrorCode.TargetOccluded, "Another window covers it.", Source));

        var result = await CreateSut().DragAsync(Request(), Source, DropWindow, false, CancellationToken.None);

        result.ErrorCode.Should().Be(InputErrorCode.TargetOccluded);
        result.Error.Should().StartWith("The from point:");
        _guard.Verify(g => g.CheckMouse(300, 100, It.IsAny<InputTarget>()), Times.Never);
        VerifyNothingSent();
    }

    [Fact]
    public async Task Drag_TheDropPointRefusedRightBeforeTheDrop_GoesBackToFrom_AndReleasesThere()
    {
        var popup = new WindowSummary(0x8180, "", "explorer", 8180);
        _guard.Setup(g => g.RecheckDrop(300, 100, It.IsAny<InputGuardResult>())).Returns(
            InputGuardResult.Refuse(InputErrorCode.TargetOccluded, "A popup now covers the point.", DropWindow, popup));

        var result = await CreateSut().DragAsync(Request(modifiers: [MouseModifier.Ctrl]), Source, DropWindow, false, CancellationToken.None);

        result.Success.Should().BeFalse();
        result.ErrorCode.Should().Be(InputErrorCode.TargetOccluded);
        result.Pressed.Should().BeTrue();
        result.DropWindow.Should().Be(popup);
        result.Error.Should().Contain("drop was not sent").And.Contain("went back to the from point (100, 100)");
        _input.Batches[^1].Should().Equal(MoveTo(100, 100), InputEvent.Mouse(LeftUp), InputEvent.Key(VkLControl, KeyUp));
        (result.ActualX, result.ActualY).Should().Be((100, 100));
        VerifyNothingIsDown();
    }

    [Fact]
    public async Task Drag_APointerKeptOffTheDropPoint_IsCursorClamped_AndGoesBackToFrom()
    {
        // ClipCursor-like confinement: the pointer cannot get past x 199
        _input.Clip = (0, 0, 200, 1080);

        var result = await CreateSut().DragAsync(Request(), Source, Source, false, CancellationToken.None);

        result.ErrorCode.Should().Be(InputErrorCode.CursorClamped);
        result.Pressed.Should().BeTrue();
        result.Error.Should().Contain("(199, 100)").And.Contain("went back to the from point");
        _input.Batches[^1].Should().Equal(MoveTo(100, 100), InputEvent.Mouse(LeftUp));
        _guard.Verify(g => g.RecheckDrop(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<InputGuardResult>()), Times.Never);
        VerifyNothingIsDown();
    }

    [Fact]
    public async Task Drag_PausedWhileTheButtonIsDown_ReleasesItWhereThePointerIs_AndSendsNothingMore()
    {
        // The user presses the pause hotkey during the hold: the session releases the button at once, and the drag sends
        // nothing after that (no move back: input is paused)
        var sut = CreateSut();
        var drag = sut.DragAsync(Request(holdBeforeMoveMs: 2000), Source, Source, false, CancellationToken.None);
        await WaitUntil(() => _input.Batches.Count >= 1);
        _session.Pause(InputControlSources.Hotkey);

        var result = await drag.WaitAsync(Timeout);

        result.ErrorCode.Should().Be(InputErrorCode.PausedByUser);
        result.Pressed.Should().BeTrue();
        result.Error.Should().Contain("released where the pointer was, at (100, 100)");
        _input.Batches.Should().HaveCount(2);
        _input.Batches[1].Should().Equal(InputEvent.Mouse(LeftUp));
        VerifyNothingIsDown();
    }

    [Fact]
    public async Task Drag_PausedBetweenMoves_StopsThere()
    {
        var sut = CreateSut();
        var drag = sut.DragAsync(Request(steps: 10, stepDelayMs: 500), Source, Source, false, CancellationToken.None);
        await WaitUntil(() => _input.Batches.Count >= 2);
        _session.Pause(InputControlSources.Hotkey);

        var result = await drag.WaitAsync(Timeout);

        result.ErrorCode.Should().Be(InputErrorCode.PausedByUser);
        _input.Batches[^1].Should().Equal(InputEvent.Mouse(LeftUp));
        _input.Batches.Count(b => b.Count == 1 && b[0].Flags != LeftUp).Should().BeLessThan(10, "the moves stopped at the pause");
        VerifyNothingIsDown();
    }

    [Fact]
    public async Task Drag_TheRequestCancelledMidDrag_GoesBackToFrom_ReleasesThere_AndThrows()
    {
        using var cancel = new CancellationTokenSource();
        var drag = CreateSut().DragAsync(Request(steps: 10, stepDelayMs: 500), Source, Source, false, cancel.Token);
        await WaitUntil(() => _input.Batches.Count >= 2);
        cancel.Cancel();

        var act = () => drag.WaitAsync(Timeout);

        await act.Should().ThrowAsync<OperationCanceledException>();
        _input.Batches[^1].Should().Equal(MoveTo(100, 100), InputEvent.Mouse(LeftUp));
        VerifyNothingIsDown();
    }

    [Fact]
    public async Task Drag_APressBatchCutShort_ReleasesWhatWentDown_AndSaysNothingWasDragged()
    {
        // The modifier key and the move went in, the button did not
        _input.Insert = (events, call) => call == 0 ? 2u : (uint)events.Count;

        var result = await CreateSut().DragAsync(Request(modifiers: [MouseModifier.Ctrl]), Source, Source, false, CancellationToken.None);

        result.ErrorCode.Should().Be(InputErrorCode.SendInputFailed);
        result.Pressed.Should().BeFalse();
        result.Error.Should().Contain("nothing was dragged");
        _input.Batches.Should().HaveCount(2, "the press, then the session's release of Ctrl; no moves");
        VerifyNothingIsDown();
    }

    [Fact]
    public async Task Drag_AMoveSendInputRefuses_ReleasesTheButton_AndStops()
    {
        _input.Insert = (events, call) => call == 2 ? 0u : (uint)events.Count;

        var result = await CreateSut().DragAsync(Request(), Source, Source, false, CancellationToken.None);

        result.ErrorCode.Should().Be(InputErrorCode.SendInputFailed);
        result.Pressed.Should().BeTrue();
        result.Error.Should().Contain("move 2 of 4");
        _input.Batches[^1].Should().Equal(InputEvent.Mouse(LeftUp));
        VerifyNothingIsDown();
    }

    [Fact]
    public async Task Drag_WhileTheUserHoldsAButton_IsMouseInUse_AndSendsNothing()
    {
        _input.Press(0x01);

        var result = await CreateSut().DragAsync(Request(), Source, Source, false, CancellationToken.None);

        result.ErrorCode.Should().Be(InputErrorCode.MouseInUse);
        VerifyNothingSent();
    }

    [Fact]
    public async Task Drag_WhileTheUserHoldsAModifier_IsModifiersHeld_AndSendsNothing()
    {
        _input.Press(VkLControl);

        var result = await CreateSut().DragAsync(Request(), Source, Source, false, CancellationToken.None);

        result.ErrorCode.Should().Be(InputErrorCode.ModifiersHeld);
        VerifyNothingSent();
    }

    [Fact]
    public async Task Drag_WhilePaused_SendsNothing()
    {
        _session.Pause(InputControlSources.Hotkey);

        var result = await CreateSut().DragAsync(Request(), Source, Source, false, CancellationToken.None);

        result.ErrorCode.Should().Be(InputErrorCode.PausedByUser);
        result.Pressed.Should().BeFalse();
        VerifyNothingSent();
    }

    [Fact]
    public async Task Drag_ToAPointOffEveryMonitor_IsCoordinatesOutOfBounds()
    {
        var result = await CreateSut().DragAsync(Request() with { ToX = 5000 }, Source, Source, false, CancellationToken.None);

        result.ErrorCode.Should().Be(InputErrorCode.CoordinatesOutOfBounds);
        result.Error.Should().Contain("to point");
        VerifyNothingSent();
    }

    [Theory]
    [InlineData(0, 0, 0)]
    [InlineData(101, 0, 0)]
    [InlineData(10, 1001, 0)]
    [InlineData(10, 0, 2001)]
    [InlineData(100, 100, 100)] // 10.1 s
    public async Task Drag_OutOfRange_Throws_AndSendsNothing(int steps, int stepDelayMs, int holdBeforeMoveMs)
    {
        var act = () => CreateSut().DragAsync(Request(steps: steps, stepDelayMs: stepDelayMs, holdBeforeMoveMs: holdBeforeMoveMs),
            Source, Source, false, CancellationToken.None);

        await act.Should().ThrowAsync<ArgumentException>();
        VerifyNothingSent();
    }

    private static async Task WaitUntil(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + Timeout;
        while (!condition())
        {
            DateTime.UtcNow.Should().BeBefore(deadline, "the drag should have got there");
            await Task.Delay(5);
        }
    }
}
