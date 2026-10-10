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
    private const ushort VkControl = 0x11, VkLControl = 0xA2, VkRShift = 0xA1, VkLShift = 0xA0, VkLMenu = 0xA4;
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
    public static TheoryData<string> InputPaths() => new() { "click", "right_click", "double_click", "mouse_move", "type_text", "send_keys" };

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
                var twice = sut.DoubleClick(100, 100, 10, InputTarget.None, releaseHeldModifiers, token);
                return (twice.Success, twice.ErrorCode, twice.Error, twice.HeldModifiers, twice.ReleasedModifiers);
            case "mouse_move":
                var move = sut.MoveMouse(100, 100, InputTarget.None, releaseHeldModifiers, token);
                return (move.Success, move.ErrorCode, move.Error, move.HeldModifiers, move.ReleasedModifiers);
            case "type_text":
                var typed = await sut.TypeTextAsync("hello", 0, InputTarget.None, releaseHeldModifiers, token);
                return (typed.Success, typed.ErrorCode, typed.Error, typed.HeldModifiers, typed.ReleasedModifiers);
            default:
                var keys = await sut.SendKeysAsync("^c", InputTarget.None, releaseHeldModifiers, token);
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
        // Arrange - the user presses the hotkey right before the third batch of 32 characters
        var text = new string('x', 100);
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
        result.CharactersTyped.Should().Be(64);
        result.Error.Should().StartWith("Typing stopped after 64 of 100 characters");
        result.ReceivingWindow.Should().NotBeNull();
        _input.Batches.Should().HaveCount(2);
        VerifyNothingIsDown();
    }

    [Fact]
    public async Task Pause_DuringTheDelayBetweenKeystrokes_CancelsTheWait()
    {
        // Arrange - the hotkey is pressed while the first character is being sent; the pause waits for that batch,
        // so it comes during the delay before the second character
        var pausing = Task.CompletedTask;
        _input.BeforeSend = _ =>
        {
            if (_input.Batches.Count == 0)
            {
                pausing = Task.Run(() => _session.Pause(InputControlSources.Hotkey));
            }
        };
        var watch = System.Diagnostics.Stopwatch.StartNew();

        // Act - ten characters, 10 s apart
        var result = await CreateSut().TypeTextAsync("abcdefghij", 10_000);
        await pausing.WaitAsync(Timeout);

        // Assert - the cancelled delay ended the call: a delay that was waited out would have taken 10 s. The bound is
        // far from both, so a slow machine, or another test holding the input gate for a moment, cannot fail it.
        result.ErrorCode.Should().Be(InputErrorCode.PausedByUser);
        result.CharactersTyped.Should().Be(1);
        watch.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(5));
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
        var act = () => CreateSut().TypeTextAsync("abcdefghij", 10, InputTarget.None, false, cancel.Token);

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

        var act = () => CreateSut(Timeout).SendKeysAsync("^c", InputTarget.None, false, cancel.Token);

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
    public async Task ReleaseHeldModifiers_ReleasesThemFirst_AndReportsIt(string path)
    {
        // Arrange - Ctrl is stuck down
        _input.Press(VkLControl);

        // Act
        var result = await Call(CreateSut(), path, releaseHeldModifiers: true);

        // Assert - the first batch releases it, then the input is sent
        result.Success.Should().BeTrue(result.Error);
        result.Released.Should().Equal("LeftCtrl");
        _input.Batches[0].Should().Equal(InputEvent.Key(VkLControl, KeyUp));
        _input.Batches.Should().HaveCountGreaterThan(path == "mouse_move" ? 0 : 1);
        _input.IsKeyDown(VkLControl).Should().BeFalse();
    }

    [Fact]
    public async Task AModifierGoingDownWhileTyping_StopsTyping()
    {
        // Arrange - the user grabs the keyboard (or starts the pause chord) during the first batch
        _input.BeforeSend = _ => _input.Press(VkRShift);

        // Act
        var result = await CreateSut().TypeTextAsync(new string('x', 40));

        // Assert
        result.ErrorCode.Should().Be(InputErrorCode.ModifiersHeld);
        result.CharactersTyped.Should().Be(32);
        result.HeldModifiers.Should().Equal("RightShift");
        result.Error.Should().StartWith("Typing stopped after 32 of 40 characters");
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

    #endregion

    #region The pointer check and the pause

    [Theory]
    [InlineData("mouse_move")]
    [InlineData("click")]
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
