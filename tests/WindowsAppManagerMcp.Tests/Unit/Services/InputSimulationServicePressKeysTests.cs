namespace WindowsAppManagerMcp.Tests.Unit.Services;

/// <summary>
/// press_keys (roadmap M4.3) through the input service: its chords go through send_keys' guarded path (the guard sees every
/// chord first, the pause chord is refused), all in one batch without waits, one batch per press with delayBetweenMs, and
/// with holdMs the keys stay down that long and are released whatever stops the call. The native input layer is
/// <see cref="FakeInputWrapper"/>, the layout the fake US layout and the guard a mock, so nothing reaches the desktop.
/// </summary>
public class InputSimulationServicePressKeysTests
{
    private const uint KeyUp = 0x0002;
    private const ushort VkControl = 0x11, VkShift = 0x10, VkTab = 0x09;
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);
    private static readonly InputTarget Notepad = InputTarget.Window(0x5150);

    private readonly FakeInputWrapper _input = new();
    private readonly FakeKeyboardLayoutWrapper _layouts = new();
    private readonly Mock<IInputTargetGuard> _guard = TestDataFactory.CreateAllowingInputGuard();
    private readonly InputSession _session;

    public InputSimulationServicePressKeysTests()
    {
        _session = new InputSession(_input);
    }

    private InputSimulationService CreateSut() =>
        new(TestDataFactory.CreateMockMonitorService().Object, _input, _guard.Object, _session, keyboardLayout: _layouts);

    private void VerifyNothingIsDown()
    {
        _session.ServerHeld.Should().BeEmpty();
        foreach (var key in new ushort[] { VkControl, VkShift, 0xA2, 0xA0, (ushort)'T', VkTab })
        {
            _input.IsKeyDown(key).Should().BeFalse($"key 0x{key:X2} must not stay down");
        }
    }

    [Fact]
    public async Task PressKeys_AChord_GoesInOneBatch_ThroughTheKeyboardGuard()
    {
        var result = await CreateSut().PressKeysAsync(KeyList.Of("ctrl+shift+t"), 1, 0, 0, Notepad);

        result.Success.Should().BeTrue(result.Error);
        result.Keys.Should().Be("ctrl+shift+t");
        result.KeysSent.Should().Be(1);
        _input.Batches.Should().ContainSingle().Which.Select(e => (e.Code, (e.Flags & KeyUp) != 0)).Should().Equal(
            (VkControl, false), (VkShift, false), ((ushort)'T', false), ((ushort)'T', true), (VkShift, true), (VkControl, true));
        _guard.Verify(g => g.CheckKeyboard(Notepad, KeyboardInputKind.Keys,
            It.Is<IReadOnlyList<InputEvent>>(events => events.Count == 6)), Times.Once);
        VerifyNothingIsDown();
    }

    [Fact]
    public async Task PressKeys_ASequenceWithoutWaits_IsOneBatch_AndCountsEveryPress()
    {
        var result = await CreateSut().PressKeysAsync(KeyList.Of("tab*3 enter"), 2, 0, 0, Notepad);

        result.KeysSent.Should().Be(8);
        _input.Batches.Should().ContainSingle().Which.Should().HaveCount(16);
    }

    [Fact]
    public async Task PressKeys_WithDelayBetweenMs_SendsOneBatchPerPress_EachCheckedAgain()
    {
        var result = await CreateSut().PressKeysAsync(KeyList.Of("tab*3"), 1, 0, 10, Notepad);

        result.Success.Should().BeTrue(result.Error);
        result.KeysSent.Should().Be(3);
        _input.Batches.Should().HaveCount(3);
        _guard.Verify(g => g.RecheckKeyboard(It.IsAny<InputGuardResult>()), Times.Exactly(3));
    }

    [Fact]
    public async Task PressKeys_WithHoldMs_PressesTheKeys_WaitsWithThemDown_ThenReleasesThem()
    {
        var downDuring = false;
        _input.BeforeSend = batch =>
        {
            // The release batch: the chord's keys are still down when it is sent
            if (batch.All(e => (e.Flags & KeyUp) != 0))
            {
                downDuring = _input.IsKeyDown(VkControl) && _input.IsKeyDown('T');
            }
        };
        var started = Environment.TickCount64;

        var result = await CreateSut().PressKeysAsync(KeyList.Of("ctrl+t"), 1, 150, 0, Notepad);

        result.Success.Should().BeTrue(result.Error);
        result.KeysSent.Should().Be(1);
        (Environment.TickCount64 - started).Should().BeGreaterThanOrEqualTo(140);
        _input.Batches.Should().HaveCount(2);
        _input.Batches[0].Should().OnlyContain(e => (e.Flags & KeyUp) == 0);
        _input.Batches[1].Should().OnlyContain(e => (e.Flags & KeyUp) != 0);
        downDuring.Should().BeTrue();
        _guard.Verify(g => g.RecheckKeyboard(It.IsAny<InputGuardResult>()), Times.Once, "a release batch is not checked again");
        VerifyNothingIsDown();
    }

    [Fact]
    public async Task PressKeys_PausedWhileAChordIsHeld_ReleasesIt_AndSendsNothingMore()
    {
        var press = CreateSut().PressKeysAsync(KeyList.Of("ctrl+t tab"), 1, 2000, 0, Notepad);
        await WaitUntil(() => _input.Batches.Count >= 1);
        _session.Pause(InputControlSources.Hotkey);

        var result = await press.WaitAsync(Timeout);

        result.ErrorCode.Should().Be(InputErrorCode.PausedByUser);
        result.KeysSent.Should().Be(0);
        _input.Batches.Should().HaveCount(2, "the press, then the session's release at the pause");
        VerifyNothingIsDown();
    }

    [Fact]
    public async Task PressKeys_CancelledWhileAChordIsHeld_ReleasesIt()
    {
        using var cancel = new CancellationTokenSource();
        var press = CreateSut().PressKeysAsync(KeyList.Of("shift+tab"), 1, 2000, 0, Notepad, false, cancel.Token);
        await WaitUntil(() => _input.Batches.Count >= 1);
        cancel.Cancel();

        var act = () => press.WaitAsync(Timeout);

        await act.Should().ThrowAsync<OperationCanceledException>();
        VerifyNothingIsDown();
    }

    [Fact]
    public async Task PressKeys_TheUsersPauseChord_IsRefused()
    {
        var result = await CreateSut().PressKeysAsync(KeyList.Of(["ctrl", "alt", "shift", "pause"]), 1, 0, 0, Notepad);

        result.ErrorCode.Should().Be(InputErrorCode.PauseChordNotAllowed);
        _input.Batches.Should().BeEmpty();
    }

    [Fact]
    public async Task PressKeys_TheGuardsRefusal_SendsNothing()
    {
        _guard.Setup(g => g.CheckKeyboard(It.IsAny<InputTarget>(), KeyboardInputKind.Keys, It.IsAny<IReadOnlyList<InputEvent>?>()))
            .Returns(InputGuardResult.Refuse(InputErrorCode.DestructiveChordNeedsTarget, "^w closes a window.", InputTarget.None));

        var result = await CreateSut().PressKeysAsync(KeyList.Of("ctrl+w"), 1, 0, 0, InputTarget.None);

        result.ErrorCode.Should().Be(InputErrorCode.DestructiveChordNeedsTarget);
        _input.Batches.Should().BeEmpty();
    }

    [Theory]
    [InlineData("ctrl+a+b", InputErrorCode.InvalidKeySpecification)]
    [InlineData("ctrl+frobnicate", InputErrorCode.UnknownKey)]
    [InlineData("", InputErrorCode.InvalidKeySpecification)]
    public async Task PressKeys_KeysThatCannotBeRead_AreRefused_WithNothingSent(string keys, InputErrorCode code)
    {
        var result = await CreateSut().PressKeysAsync(KeyList.Of(keys), 1, 0, 0, Notepad);

        result.ErrorCode.Should().Be(code);
        result.Error.Should().EndWith("Nothing was sent.");
        _input.Batches.Should().BeEmpty();
        _guard.Verify(g => g.CheckKeyboard(It.IsAny<InputTarget>(), It.IsAny<KeyboardInputKind>(), It.IsAny<IReadOnlyList<InputEvent>?>()), Times.Never);
    }

    [Fact]
    public async Task PressKeys_LongerThanTenSeconds_Throws()
    {
        var act = () => CreateSut().PressKeysAsync(KeyList.Of("tab*100"), 1, 50, 51, Notepad);

        await act.Should().ThrowAsync<ArgumentException>().WithMessage("*10000 ms*");
        _input.Batches.Should().BeEmpty();
    }

    [Theory]
    [InlineData(0, 0, 0)]
    [InlineData(101, 0, 0)]
    [InlineData(1, 5001, 0)]
    [InlineData(1, 0, 1001)]
    public async Task PressKeys_OutOfRange_Throws(int repeat, int holdMs, int delayBetweenMs)
    {
        var act = () => CreateSut().PressKeysAsync(KeyList.Of("tab"), repeat, holdMs, delayBetweenMs, Notepad);

        await act.Should().ThrowAsync<ArgumentOutOfRangeException>();
    }

    [Fact]
    public async Task PressKeys_WhileTheUserHoldsAModifier_IsModifiersHeld()
    {
        _input.Press(0xA0);

        var result = await CreateSut().PressKeysAsync(KeyList.Of("tab"), 1, 0, 0, Notepad);

        result.ErrorCode.Should().Be(InputErrorCode.ModifiersHeld);
        _input.Batches.Should().BeEmpty();
    }

    private static async Task WaitUntil(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + Timeout;
        while (!condition())
        {
            DateTime.UtcNow.Should().BeBefore(deadline);
            await Task.Delay(5);
        }
    }
}
