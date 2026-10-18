namespace WindowsAppManagerMcp.Tests.Unit.Services;

/// <summary>
/// Session safety of the input tools (roadmap M1.10): the user's pause stops every input path and a running
/// call, the request's cancellation stops typing, the input gate wait is bounded, held modifiers are refused
/// (or released on request), and no key or button stays down after a short SendInput count or an exception.
/// The native input layer is <see cref="FakeInputWrapper"/> and the guard a mock, so nothing reaches the desktop.
/// </summary>
public class InputSimulationServiceSessionTests
{
    private const uint KeyDown = 0x0000, KeyUp = 0x0002;
    private const ushort VkControl = 0x11, VkLControl = 0xA2, VkRShift = 0xA1, VkLShift = 0xA0, VkLMenu = 0xA4, VkLWin = 0x5B;
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    private readonly FakeInputWrapper _input = new();
    private readonly Mock<IInputTargetGuard> _guard = TestDataFactory.CreateAllowingInputGuard();
    private readonly Mock<IMonitorService> _monitors = TestDataFactory.CreateMockMonitorService(
    [
        TestDataFactory.CreateMonitorInfo(boundsX: 0, boundsY: 0, boundsWidth: 1920, boundsHeight: 1080)
    ]);

    private readonly InputSession _session;

    public InputSimulationServiceSessionTests()
    {
        _session = new InputSession(_input);
    }

    private InputSimulationService CreateSut(TimeSpan? gateTimeout = null, InputSession? session = null) =>
        new(_monitors.Object, _input, _guard.Object, session ?? _session, gateTimeout);

    /// <summary>Every input path, as the tools call it.</summary>
    public static TheoryData<string> InputPaths() =>
        new() { "click", "right_click", "double_click", "mouse_click", "drag", "mouse_move", "scroll", "type_text", "send_keys", "press_keys" };

    private static async Task<(bool Success, InputErrorCode Code, string? Error, IReadOnlyList<string>? Held, IReadOnlyList<string>? Released)> Call(
        InputSimulationService sut, string path, bool releaseHeldModifiers = false, CancellationToken token = default)
    {
        switch (path)
        {
            case "click":
                var click = sut.Click(100, 100, InputTarget.None, releaseHeldModifiers, token);
                return (click.Success, click.ErrorCode, click.Error, click.HeldModifiers, click.ReleasedModifiers);
            case "right_click":
                var right = sut.RightClick(100, 100, InputTarget.None, releaseHeldModifiers, token);
                return (right.Success, right.ErrorCode, right.Error, right.HeldModifiers, right.ReleasedModifiers);
            case "double_click":
                var twice = sut.DoubleClick(100, 100, InputTarget.None, releaseHeldModifiers, token);
                return (twice.Success, twice.ErrorCode, twice.Error, twice.HeldModifiers, twice.ReleasedModifiers);
            case "mouse_click":
                var mouse = sut.MouseClick(100, 100, MouseButton.Middle, 3, [], InputTarget.None, releaseHeldModifiers, token);
                return (mouse.Success, mouse.ErrorCode, mouse.Error, mouse.HeldModifiers, mouse.ReleasedModifiers);
            case "drag":
                var drag = await sut.DragAsync(new DragRequest(100, 100, 200, 100, MouseButton.Primary, [], 2, 0, 0), InputTarget.None,
                    InputTarget.None, releaseHeldModifiers, token);
                return (drag.Success, drag.ErrorCode, drag.Error, drag.HeldModifiers, drag.ReleasedModifiers);
            case "press_keys":
                var pressed = await sut.PressKeysAsync(KeyList.Of("ctrl+c"), 1, 0, 0, InputTarget.None, releaseHeldModifiers, token);
                return (pressed.Success, pressed.ErrorCode, pressed.Error, pressed.HeldModifiers, pressed.ReleasedModifiers);
            case "mouse_move":
                var move = sut.MoveMouse(100, 100, InputTarget.None, releaseHeldModifiers, token);
                return (move.Success, move.ErrorCode, move.Error, move.HeldModifiers, move.ReleasedModifiers);
            case "scroll":
                var scroll = sut.Scroll(100, 100, 3, 0, InputTarget.None, releaseHeldModifiers, token);
                return (scroll.Success, scroll.ErrorCode, scroll.Error, scroll.HeldModifiers, scroll.ReleasedModifiers);
            case "type_text":
                var typed = await sut.TypeTextAsync("hello", 0, InputTarget.None, releaseHeldModifiers, null, TextInputMethod.Unicode, token);
                return (typed.Success, typed.ErrorCode, typed.Error, typed.HeldModifiers, typed.ReleasedModifiers);
            default:
                var keys = await sut.SendKeysAsync("^c", InputTarget.None, releaseHeldModifiers, 0, token);
                return (keys.Success, keys.ErrorCode, keys.Error, keys.HeldModifiers, keys.ReleasedModifiers);
        }
    }

    private void VerifyNothingSentOrChecked()
    {
        _input.Batches.Should().BeEmpty();
        _input.CursorMoves.Should().Be(0);
        _guard.Verify(g => g.CheckKeyboard(It.IsAny<InputTarget>(), It.IsAny<KeyboardInputKind>(), It.IsAny<IReadOnlyList<InputEvent>?>()), Times.Never);
        _guard.Verify(g => g.CheckMouse(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<InputTarget>()), Times.Never);
    }

    private void VerifyNothingIsDown()
    {
        _session.ServerHeld.Should().BeEmpty();
        foreach (var key in new ushort[] { VkControl, VkLControl, 0x01, 0x02 })
        {
            _input.IsKeyDown(key).Should().BeFalse($"key 0x{key:X2} must not stay down");
        }
    }

    #region Pause

    [Theory]
    [MemberData(nameof(InputPaths))]
    public async Task WhilePaused_EveryInputPath_ReturnsPausedByUser_AndSendsNothing(string path)
    {
        // Arrange
        _session.Pause(InputControlSources.Hotkey);

        // Act
        var result = await Call(CreateSut(), path);

        // Assert
        result.Success.Should().BeFalse();
        result.Code.Should().Be(InputErrorCode.PausedByUser);
        result.Error.Should().Contain("pause hotkey").And.Contain("Ctrl+Alt+Shift+Pause").And.Contain("Do not try to resume");
        VerifyNothingSentOrChecked();
    }

    [Fact]
    public async Task Pause_BetweenBatches_StopsALongTypeText_WithWhatWasTyped()
    {
        // Arrange - the user presses the hotkey right before the third batch of 64 characters
        var text = new string('x', 200);
        var rechecks = 0;
        _guard.Setup(g => g.RecheckKeyboard(It.IsAny<InputGuardResult>()))
            .Returns((InputGuardResult approved) =>
            {
                if (++rechecks == 3)
                {
                    _session.Pause(InputControlSources.Hotkey);
                }

                return approved;
            });

        // Act
        var result = await CreateSut().TypeTextAsync(text);

        // Assert - two batches went in, nothing after the pause, and nothing is held down
        result.ErrorCode.Should().Be(InputErrorCode.PausedByUser);
        result.CharactersTyped.Should().Be(128);
        result.Error.Should().StartWith("Typing stopped after 128 of 200 characters");
        result.ReceivingWindow.Should().NotBeNull();
        _input.Batches.Should().HaveCount(2);
        VerifyNothingIsDown();
    }

    [Fact]
    public async Task Pause_WhileSlowTypingSendsACharacter_LetsItFinish_AndTypesNoMore()
    {
        // Arrange - with a delay between keys, every character is a batch of its own. The hotkey is pressed while
        // the first character is being sent: the pause waits for that batch (Send holds the session's lock), so it
        // takes effect right after it, usually during the delay before the second character. On a loaded machine
        // the pausing thread can be slow to get the lock, and the delay is at most 100 ms, so the check before the
        // second character waits for the pause: wherever it lands, it comes before that character.
        var pausing = Task.CompletedTask;
        _input.BeforeSend = _ =>
        {
            if (_input.Batches.Count == 0)
            {
                pausing = Task.Run(() => _session.Pause(InputControlSources.Hotkey));
            }
        };
        var rechecks = 0;
        _guard.Setup(g => g.RecheckKeyboard(It.IsAny<InputGuardResult>()))
            .Returns((InputGuardResult approved) =>
            {
                if (++rechecks == 2)
                {
                    pausing.Wait(Timeout);
                }

                return approved;
            });

        // Act - ten characters, as slowly as type_text types (it clamps the delay to 100 ms)
        var result = await CreateSut().TypeTextAsync("abcdefghij", 100);
        await pausing.WaitAsync(Timeout);

        // Assert - the character being sent went in whole; nothing after the pause did, and nothing is held down
        result.ErrorCode.Should().Be(InputErrorCode.PausedByUser);
        result.CharactersTyped.Should().Be(1);
        result.Error.Should().StartWith("Typing stopped after 1 of 10 characters");
        _input.Batches.Should().ContainSingle();
        _input.Events.Count(e => (e.Flags & KeyUp) == 0).Should().Be(_input.Events.Count(e => (e.Flags & KeyUp) != 0),
            "every key that went down went up again");
        VerifyNothingIsDown();
    }

    [Fact]
    public async Task Pause_WhileTheGuardActivatesTheTarget_IsReportedAsPausedByUser()
    {
        // EnsureForeground refuses to activate once the user has paused; the call reports the pause, not the guard's code
        _guard.Setup(g => g.CheckKeyboard(It.IsAny<InputTarget>(), It.IsAny<KeyboardInputKind>(), It.IsAny<IReadOnlyList<InputEvent>?>()))
            .Returns(() =>
            {
                _session.Pause(InputControlSources.Hotkey);
                return InputGuardResult.Refuse(InputErrorCode.TargetNotForeground, "not in front", InputTarget.None, null);
            });

        var result = await CreateSut().TypeTextAsync("hello", 0, InputTarget.Window(0x200));

        result.ErrorCode.Should().Be(InputErrorCode.PausedByUser);
        _input.Batches.Should().BeEmpty();
    }

    #endregion

    #region Cancellation and the gate

    [Fact]
    public async Task CancellingTheRequest_StopsTyping_AndThrows()
    {
        // Arrange - the client cancels the call after the second character
        using var cancel = new CancellationTokenSource();
        _input.BeforeSend = _ =>
        {
            if (_input.Batches.Count == 1)
            {
                cancel.Cancel();
            }
        };

        // Act
        var act = () => CreateSut().TypeTextAsync("abcdefghij", 10, InputTarget.None, false, null, TextInputMethod.Unicode, cancel.Token);

        // Assert - the SDK reports the cancellation; nothing more is typed and nothing stays down
        await act.Should().ThrowAsync<OperationCanceledException>();
        _input.Batches.Should().HaveCount(2);
        VerifyNothingIsDown();
    }

    [Theory]
    [MemberData(nameof(InputPaths))]
    public async Task AnotherCallHoldingTheGate_EndsTheWaitWithInputBusy(string path)
    {
        // Arrange - a window-changing tool (or another input call) holds the gate past the limit
        using var gate = InputGate.Enter();

        // Act
        var result = await Call(CreateSut(TimeSpan.FromMilliseconds(50)), path);

        // Assert
        result.Code.Should().Be(InputErrorCode.InputBusy);
        result.Error.Should().Contain("input gate");
        VerifyNothingSentOrChecked();
    }

    [Fact]
    public async Task CancellingTheRequest_WhileWaitingForTheGate_EndsTheWait()
    {
        using var gate = InputGate.Enter();
        using var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));

        var act = () => CreateSut(Timeout).SendKeysAsync("^c", InputTarget.None, false, 0, cancel.Token);

        await act.Should().ThrowAsync<OperationCanceledException>().WaitAsync(Timeout);
        _input.Batches.Should().BeEmpty();
    }

    [Fact]
    public async Task Pausing_WhileWaitingForTheGate_EndsTheWaitWithPausedByUser()
    {
        // Arrange
        Task<TypeTextResult> call;
        using (InputGate.Enter())
        {
            call = CreateSut(Timeout).TypeTextAsync("hello");
            await Task.Delay(50);

            // Act
            _session.Pause(InputControlSources.Hotkey);
            var result = await call.WaitAsync(Timeout);

            // Assert - it did not wait for the gate to be released
            result.ErrorCode.Should().Be(InputErrorCode.PausedByUser);
        }

        _input.Batches.Should().BeEmpty();
    }

    #endregion

    #region Held modifiers

    [Theory]
    [MemberData(nameof(InputPaths))]
    public async Task AModifierTheUserHolds_IsModifiersHeld_AndNotReleased(string path)
    {
        // Arrange - the user holds Ctrl: a typed 'c' would be Ctrl+C, a click a Ctrl+click
        _input.Press(VkLControl);

        // Act
        var result = await Call(CreateSut(), path);

        // Assert - nothing is sent, and the user's key is left alone
        result.Code.Should().Be(InputErrorCode.ModifiersHeld);
        result.Held.Should().Equal("LeftCtrl");
        result.Error.Should().Contain("LeftCtrl").And.Contain("releaseHeldModifiers");
        VerifyNothingSentOrChecked();
        _input.IsKeyDown(VkLControl).Should().BeTrue();
    }

    [Theory]
    [MemberData(nameof(InputPaths))]
    public async Task AStuckModifier_IsModifiersHeld_WithTheAdviceElementActionReplaces(string path)
    {
        // Arrange - Windows is down, which the pause hotkey does not use
        _input.Press(VkLWin);

        // Act
        var result = await Call(CreateSut(), path);

        // Assert - element_action, which has no releaseHeldModifiers, puts its own advice in place of this one
        result.Code.Should().Be(InputErrorCode.ModifiersHeld);
        result.Error.Should().Contain(InputSimulationService.ReleaseHeldModifiersAdvice);
    }

    [Theory]
    [MemberData(nameof(InputPaths))]
    public async Task ReleaseHeldModifiers_ReleasesThemFirst_AndReportsIt(string path)
    {
        // Arrange - Ctrl is stuck down, and the pause hotkey does not use Ctrl
        _input.Press(VkLControl);
        var session = new InputSession(_input, PauseHotkeyOptions.FromEnvironment(_ => "Alt+Shift+F12"));

        // Act
        var result = await Call(CreateSut(session: session), path, releaseHeldModifiers: true);

        // Assert - the first batch releases it (with the left Ctrl's scan code), then the input is sent
        result.Success.Should().BeTrue(result.Error);
        result.Released.Should().Equal("LeftCtrl");
        _input.Batches[0].Should().Equal(InputEvent.Key(VkLControl, KeyUp, 0x1D));
        _input.Batches.Should().HaveCountGreaterThan(path == "mouse_move" ? 0 : 1);
        _input.IsKeyDown(VkLControl).Should().BeFalse();
    }

    [Fact]
    public async Task AModifierGoingDownWhileTyping_StopsTyping()
    {
        // Arrange - the user grabs the keyboard (or starts the pause chord) during the first batch
        _input.BeforeSend = _ => _input.Press(VkRShift);

        // Act
        var result = await CreateSut().TypeTextAsync(new string('x', 80));

        // Assert
        result.ErrorCode.Should().Be(InputErrorCode.ModifiersHeld);
        result.CharactersTyped.Should().Be(64);
        result.HeldModifiers.Should().Equal("RightShift");
        result.Error.Should().StartWith("Typing stopped after 64 of 80 characters");
        _input.Batches.Should().ContainSingle();
    }

    [Fact]
    public async Task SendKeys_AModifierPressedWhileTheGuardChecks_IsModifiersHeld_AndNothingIsSent()
    {
        // Arrange - the user presses Shift while the guard checks the target (and brings it to the front): Delete
        // would become Shift+Delete, a permanent delete
        _guard.Setup(g => g.RecheckKeyboard(It.IsAny<InputGuardResult>()))
            .Returns((InputGuardResult approved) =>
            {
                _input.Press(VkLShift);
                return approved;
            });

        // Act
        var result = await CreateSut().SendKeysAsync("{DELETE}");

        // Assert
        result.ErrorCode.Should().Be(InputErrorCode.ModifiersHeld);
        result.HeldModifiers.Should().Equal("LeftShift");
        _input.Batches.Should().BeEmpty();
        _input.IsKeyDown(VkLShift).Should().BeTrue("the user's key is left alone");
    }

    [Theory]
    [InlineData("click")]
    [InlineData("double_click")]
    [InlineData("mouse_click")]
    [InlineData("drag")]
    [InlineData("scroll")] // Ctrl turns the wheel into zoom
    public async Task Click_AModifierPressedBeforeThePress_IsModifiersHeld_AndNothingIsClicked(string path)
    {
        // Arrange - the user presses Ctrl after the guard checked the point: the click would be a Ctrl+click
        _guard.Setup(g => g.RecheckMouse(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<InputGuardResult>()))
            .Returns((int _, int _, InputGuardResult approved) =>
            {
                _input.Press(VkLControl);
                return approved;
            });

        // Act
        var result = await Call(CreateSut(), path);

        // Assert
        result.Code.Should().Be(InputErrorCode.ModifiersHeld);
        result.Held.Should().Equal("LeftCtrl");
        _input.Batches.Should().BeEmpty();
    }

    [Theory]
    [MemberData(nameof(InputPaths))]
    public async Task ThePauseChordsModifiers_AreNotReleased_EvenWhenAsked(string path)
    {
        // Arrange - the user holds Ctrl+Alt+Shift and is about to press Pause: releasing them would make the chord miss
        _input.Press(VkLControl);
        _input.Press(VkLMenu);
        _input.Press(VkLShift);

        // Act
        var result = await Call(CreateSut(), path, releaseHeldModifiers: true);

        // Assert
        result.Code.Should().Be(InputErrorCode.ModifiersHeld);
        result.Error.Should().Contain("pause hotkey").And.Contain("not released");
        result.Released.Should().BeNull();
        VerifyNothingSentOrChecked();
        new[] { VkLControl, VkLMenu, VkLShift }.Should().OnlyContain(key => _input.IsKeyDown(key));
    }

    [Theory]
    [MemberData(nameof(InputPaths))]
    public async Task SomeOfThePauseChordsModifiers_AreNotReleasedEither(string path)
    {
        // Arrange - the user has pressed Ctrl and Alt and is reaching for Shift: releasing them would make the
        // chord miss when the rest goes down
        _input.Press(VkLControl);
        _input.Press(VkLMenu);

        // Act
        var result = await Call(CreateSut(), path, releaseHeldModifiers: true);

        // Assert
        result.Code.Should().Be(InputErrorCode.ModifiersHeld);
        result.Error.Should().Contain("pause hotkey").And.Contain("not released");
        result.Released.Should().BeNull();
        VerifyNothingSentOrChecked();
        new[] { VkLControl, VkLMenu }.Should().OnlyContain(key => _input.IsKeyDown(key));
    }

    [Fact]
    public async Task ThePauseChordMessage_AgreesWithTheNumberOfKeysDown()
    {
        // Arrange & Act - first one modifier of the chord, then three
        _input.Press(VkLControl);
        var one = await CreateSut().SendKeysAsync("a", releaseHeldModifiers: true);
        _input.Press(VkLMenu);
        _input.Press(VkLShift);
        var three = await CreateSut().SendKeysAsync("a", releaseHeldModifiers: true);

        // Assert
        one.ErrorCode.Should().Be(InputErrorCode.ModifiersHeld);
        one.Error.Should().Contain("LeftCtrl is held down").And.Contain("so it is not released");
        three.ErrorCode.Should().Be(InputErrorCode.ModifiersHeld);
        three.Error.Should().Contain("are held down").And.Contain("so they are not released").And.NotContain("them are");
    }

    [Fact]
    public async Task AStuckModifierOutsideThePauseChord_IsStillReleasedWhenAsked()
    {
        // Arrange - the default chord is Ctrl+Alt+Shift+Pause; a stuck Windows key is not part of it
        _input.Press(VkLWin);

        // Act
        var result = await CreateSut().SendKeysAsync("a", releaseHeldModifiers: true);

        // Assert
        result.Success.Should().BeTrue(result.Error);
        result.ReleasedModifiers.Should().Equal("LeftWin");
        _input.IsKeyDown(VkLWin).Should().BeFalse();
    }

    #endregion

    #region Held mouse buttons

    /// <summary>The input paths that move the pointer or press a button.</summary>
    public static TheoryData<string> MousePaths() => new() { "click", "right_click", "double_click", "mouse_click", "drag", "mouse_move", "scroll" };

    [Theory]
    [MemberData(nameof(MousePaths))]
    public async Task AMouseButtonTheUserHolds_IsMouseInUse_AndThePointerIsNotTouched(string path)
    {
        // Arrange - the user is dragging or selecting: the window under the press has the mouse, so a click
        // would go to it, and moving the pointer would move the user's drag
        _input.Press(0x01);

        // Act
        var result = await Call(CreateSut(), path);

        // Assert
        result.Success.Should().BeFalse();
        result.Code.Should().Be(InputErrorCode.MouseInUse);
        result.Error.Should().Contain("left mouse button");
        _input.Batches.Should().BeEmpty();
        _input.CursorMoves.Should().Be(0);
    }

    [Theory]
    [InlineData("click")]
    [InlineData("double_click")]
    [InlineData("mouse_click")]
    [InlineData("drag")]
    [InlineData("scroll")]
    public async Task Click_AMouseButtonPressedBeforeThePress_IsMouseInUse_AndNothingIsClicked(string path)
    {
        // Arrange - the user presses the right button after the guard checked the point
        _guard.Setup(g => g.RecheckMouse(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<InputGuardResult>()))
            .Returns((int _, int _, InputGuardResult approved) =>
            {
                _input.Press(0x02);
                return approved;
            });

        // Act
        var result = await Call(CreateSut(), path);

        // Assert
        result.Code.Should().Be(InputErrorCode.MouseInUse);
        result.Error.Should().Contain("right mouse button");
        _input.Batches.Should().BeEmpty();
    }

    [Theory]
    [InlineData("type_text")]
    [InlineData("send_keys")]
    [InlineData("press_keys")]
    public async Task AHeldMouseButton_DoesNotStopKeys(string path)
    {
        // Keys go to the window with keyboard focus, which the mouse capture does not change
        _input.Press(0x01);

        var result = await Call(CreateSut(), path);

        result.Success.Should().BeTrue(result.Error);
    }

    #endregion

    #region The pointer check and the pause

    [Theory]
    [InlineData("click")]
    [InlineData("drag")]
    [InlineData("scroll")]
    public async Task APauseWhileThePointerIsPlacedAgain_IsPausedByUser_NotCursorNotAtPoint(string path)
    {
        // Arrange - after the move, the user moves the mouse away and pauses (through another server's hotkey,
        // without this session's lock); placing the pointer again is refused
        var flag = new FakeDesktopPauseFlag();
        using var session = new InputSession(_input);
        session.ShareDesktopPause(flag);
        _input.AfterSetCursorPos = (_, _) =>
        {
            _input.AfterSetCursorPos = null;
            _input.MovePointer(500, 500);
            flag.SetByAnotherServer();
        };

        // Act
        var result = await Call(CreateSut(session: session), path);

        // Assert - the agent is told to stop, not to try again
        result.Code.Should().Be(InputErrorCode.PausedByUser);
        result.Error.Should().Contain("Do not try to resume");
        _input.CursorMoves.Should().Be(1);
        _input.Batches.Should().BeEmpty();
    }

    #endregion

    #region Stuck-input recovery

    [Fact]
    public async Task SendKeys_ShortSendInputCount_ReleasesWhatWentDown()
    {
        // Arrange - SendInput stops after Ctrl went down (a desktop switch mid-batch)
        _input.Insert = (_, call) => call == 0 ? 1u : uint.MaxValue;

        // Act
        var result = await CreateSut().SendKeysAsync("^c");

        // Assert - Ctrl is sent up again at once
        result.ErrorCode.Should().Be(InputErrorCode.SendInputFailed);
        _input.Batches[^1].Should().Equal(InputEvent.Key(VkControl, KeyUp));
        VerifyNothingIsDown();
    }

    [Fact]
    public async Task SendKeys_SendInputThrows_ReleasesWhatWentDown_AndThrows()
    {
        // Arrange - SendInput throws after Ctrl went down
        _input.Insert = (_, call) =>
        {
            if (call == 0)
            {
                _input.Press(VkControl);
                throw new InvalidOperationException("native failure");
            }

            return uint.MaxValue;
        };

        // Act
        var act = () => CreateSut().SendKeysAsync("^c");

        // Assert - the tool error filter reports the exception; Ctrl is not left down
        await act.Should().ThrowAsync<InvalidOperationException>();
        _input.IsKeyDown(VkControl).Should().BeFalse();
        _session.ServerHeld.Should().BeEmpty();
    }

    [Fact]
    public async Task Click_ShortSendInputCount_ReleasesTheButton()
    {
        // The absolute move and the button down went in, the button up did not
        _input.Insert = (_, call) => call == 0 ? 2u : uint.MaxValue;

        var result = await Call(CreateSut(), "click");

        result.Code.Should().Be(InputErrorCode.SendInputFailed);
        _input.Batches[^1].Should().Equal(InputEvent.Mouse(0x0004));
        VerifyNothingIsDown();
    }

    [Fact]
    public async Task KeysLeftDownByAnEarlierFailedRelease_AreReleasedBeforeTheNextCall()
    {
        // Arrange - a send_keys batch stopped after Ctrl, and both the release right after it and the one at the
        // end of the call failed too (the desktop was gone)
        _input.Insert = (_, call) => call switch { 0 => 1u, 1 or 2 => 0u, _ => uint.MaxValue };
        var sut = CreateSut();
        await sut.SendKeysAsync("^c");
        var heldBetweenCalls = _session.ServerHeld.Select(h => h.Name).ToList();

        // Act
        var next = await sut.TypeTextAsync("ok");

        // Assert - no hold spans two calls: the next call releases Ctrl before it types
        heldBetweenCalls.Should().Equal("Ctrl");
        next.Success.Should().BeTrue();
        _input.Batches[3].Should().Equal(InputEvent.Key(VkControl, KeyUp));
        VerifyNothingIsDown();
    }

    [Fact]
    public async Task AFailedReleaseIsRetriedAtTheEndOfTheCall()
    {
        // The release right after the short count fails; the one every call makes before it returns succeeds
        _input.Insert = (_, call) => call switch { 0 => 1u, 1 => 0u, _ => uint.MaxValue };

        await CreateSut().SendKeysAsync("^c");

        _input.Batches.Should().HaveCount(3);
        _input.Batches[2].Should().Equal(InputEvent.Key(VkControl, KeyUp));
        VerifyNothingIsDown();
    }

    [Fact]
    public async Task KeysTheServerCannotRelease_RefuseTheNextCall()
    {
        // Every release fails: the server's own Ctrl stays down, so input would arrive as Ctrl+key
        _input.Insert = (events, call) => call == 0 ? 1u : events.All(e => (e.Flags & KeyUp) != 0) ? 0u : (uint)events.Count;
        var sut = CreateSut();
        await sut.SendKeysAsync("^c");

        var next = await sut.TypeTextAsync("ok");

        next.ErrorCode.Should().Be(InputErrorCode.ModifiersHeld);
        next.HeldModifiers.Should().Equal("Ctrl");
        next.Error.Should().Contain("release_all_input");
    }

    #endregion

    #region Pause chord

    [Theory]
    [InlineData("^%+{PAUSE}")]
    [InlineData("^+%{BREAK}")]
    [InlineData("hello^%+{PAUSE}")]
    public async Task SendKeys_ThePauseChord_IsRefused(string keys)
    {
        // The chord would reach the hotkey like a real press: the agent could resume itself
        var result = await CreateSut().SendKeysAsync(keys);

        result.ErrorCode.Should().Be(InputErrorCode.PauseChordNotAllowed);
        result.Error.Should().Contain("Ctrl+Alt+Shift+Pause");
        VerifyNothingSentOrChecked();
    }

    [Fact]
    public async Task SendKeys_ThePauseChord_IsRefusedWhilePausedToo()
    {
        _session.Pause(InputControlSources.Hotkey);

        var result = await CreateSut().SendKeysAsync("^%+{PAUSE}");

        result.ErrorCode.Should().Be(InputErrorCode.PauseChordNotAllowed);
    }

    [Fact]
    public async Task SendKeys_PauseWithoutTheChordsModifiers_IsSent()
    {
        var result = await CreateSut().SendKeysAsync("{PAUSE}");

        result.Success.Should().BeTrue();
    }

    [Fact]
    public async Task SendKeys_AConfiguredChord_IsRefused()
    {
        var session = new InputSession(_input, PauseHotkeyOptions.FromEnvironment(_ => "Ctrl+Alt+F12"));

        var result = await CreateSut(session: session).SendKeysAsync("^%{F12}");

        result.ErrorCode.Should().Be(InputErrorCode.PauseChordNotAllowed);
        result.Error.Should().Contain("Ctrl+Alt+F12");
    }

    #endregion
}
