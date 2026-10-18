namespace WindowsAppManagerMcp.Tests.Unit.Services;

/// <summary>
/// send_keys is bounded as type_text is: at most <see cref="InputSimulationService.MaxSendKeysPresses"/> keys (TextTooLong),
/// at most <see cref="InputSimulationService.MaxSendKeysDurationMs"/> of delayMs waits in all (InvalidArguments), both
/// refused before anything is checked or sent; and a long call without delayMs goes in batches of whole keys, with the
/// window, the modifiers, the paste rule and the user's pause checked again before each, so a focus change or the pause
/// stops it within a batch. The native input layer is <see cref="FakeInputWrapper"/> and the guard a mock, so nothing
/// reaches the desktop.
/// </summary>
public class SendKeysLimitsTests
{
    private const uint KeyUp = 0x0002;
    private const ushort VkShift = 0x10, VkLShift = 0xA0;
    private const int Batch = InputSimulationService.SendKeysBatchSize;

    private readonly FakeInputWrapper _input = new();
    private readonly Mock<IInputTargetGuard> _guard = TestDataFactory.CreateAllowingInputGuard();
    private readonly InputSession _session;

    public SendKeysLimitsTests()
    {
        _session = new InputSession(_input);
    }

    private InputSimulationService CreateSut() =>
        new(TestDataFactory.CreateMockMonitorService().Object, _input, _guard.Object, _session);

    /// <summary><paramref name="count"/> presses of <paramref name="key"/>, as {KEY 100} groups.</summary>
    private static string Presses(char key, int count) =>
        string.Concat(Enumerable.Repeat($"{{{key} 100}}", count / 100)) + (count % 100 == 0 ? string.Empty : $"{{{key} {count % 100}}}");

    private void VerifyNothingCheckedOrSent()
    {
        _input.Batches.Should().BeEmpty();
        _guard.Verify(g => g.CheckKeyboard(It.IsAny<InputTarget>(), It.IsAny<KeyboardInputKind>(), It.IsAny<IReadOnlyList<InputEvent>?>()), Times.Never);
    }

    private void VerifyNothingIsDown()
    {
        _session.ServerHeld.Should().BeEmpty();
        foreach (var key in new ushort[] { VkShift, VkLShift, 'A', 'B' })
        {
            _input.IsKeyDown(key).Should().BeFalse($"key 0x{key:X2} must not stay down");
        }
    }

    #region How many keys

    [Fact]
    public void TheMaximum_IsTypeTextsMaximum()
    {
        InputSimulationService.MaxSendKeysPresses.Should().Be(InputSimulationService.MaxTypedLength);
        InputSimulationService.SendKeysBatchSize.Should().Be(InputSimulationService.TypeTextBatchSize);
    }

    [Theory]
    [InlineData(0)] // {a 100} repeated
    [InlineData(1)] // characters
    [InlineData(2)] // one group
    public async Task MoreKeysThanTheMaximum_AreTextTooLong_AndNothingIsCheckedOrSent(int spelling)
    {
        var count = InputSimulationService.MaxSendKeysPresses + 1;
        var keys = spelling switch
        {
            0 => Presses('a', count),
            1 => new string('a', count),
            _ => "+(" + new string('a', count) + ")"
        };

        var result = await CreateSut().SendKeysAsync(keys);

        result.Success.Should().BeFalse();
        result.ErrorCode.Should().Be(InputErrorCode.TextTooLong);
        result.Error.Should().Contain("5,001").And.Contain("5,000").And.Contain("Nothing was sent");
        result.KeysSent.Should().BeNull();
        VerifyNothingCheckedOrSent();
    }

    [Fact]
    public async Task TheMaximum_IsSent_InBatches()
    {
        var result = await CreateSut().SendKeysAsync(Presses('a', InputSimulationService.MaxSendKeysPresses));

        result.Success.Should().BeTrue(result.Error);
        result.KeysSent.Should().Be(InputSimulationService.MaxSendKeysPresses);
        _input.Batches.Should().HaveCount((InputSimulationService.MaxSendKeysPresses + Batch - 1) / Batch);
        VerifyNothingIsDown();
    }

    [Fact]
    public async Task ModifiersHeldForTheKeys_DoNotCount()
    {
        // Every capital is Shift with its key: 5,000 keys, not 10,000
        var result = await CreateSut().SendKeysAsync(new string('A', InputSimulationService.MaxSendKeysPresses));

        result.Success.Should().BeTrue(result.Error);
    }

    /// <summary>Shift and Ctrl down and up again, crossed: four events and two key-downs, but no key press.</summary>
    private const string CrossedModifiers = "{SHIFT down}{CTRL down}{SHIFT up}{CTRL up}";

    [Fact]
    public async Task MoreEventsThanTheMaximum_AreTextTooLong_EvenWithoutAKeyPress_AndNothingIsCheckedOrSent()
    {
        var keys = string.Concat(Enumerable.Repeat(CrossedModifiers, InputSimulationService.MaxSendKeysEvents / 4 + 1));

        var result = await CreateSut().SendKeysAsync(keys);

        result.Success.Should().BeFalse();
        result.ErrorCode.Should().Be(InputErrorCode.TextTooLong);
        result.Error.Should().Contain("60,004").And.Contain("60,000").And.Contain("Nothing was sent");
        result.KeysSent.Should().BeNull();
        VerifyNothingCheckedOrSent();
    }

    [Fact]
    public void TheEventMaximum_TakesTheMaximumNumberOfTheHeaviestKey()
    {
        // The most events one key press takes: Ctrl, Alt, Shift and Win held for an AltGr character ('@' on a German
        // keyboard, for which the right Alt goes down too), each down and up again around the key's own down and up
        var german = KeyboardLayout.Of(new FakeKeyboardLayoutWrapper { Layout = FakeKeyboardLayoutWrapper.German }, 0, capsLock: false);
        var heaviest = KeySpecParser.Parse("^%+#@", german).Events;

        heaviest.Should().HaveCount(12);
        SendKeysParser.KeyPresses(heaviest).Should().ContainSingle();
        (heaviest.Count * InputSimulationService.MaxSendKeysPresses).Should().BeLessThanOrEqualTo(InputSimulationService.MaxSendKeysEvents);
    }

    #endregion

    #region How long

    [Fact]
    public async Task WaitsLongerThanTheMaximum_AreInvalidArguments_AndNothingIsCheckedOrSent()
    {
        // 22 keys with 500 ms between them wait 21 x 500 = 10,500 ms; the input gate would be held all along
        var started = Environment.TickCount64;

        var result = await CreateSut().SendKeysAsync("{a 22}", delayMs: 500);

        result.Success.Should().BeFalse();
        result.ErrorCode.Should().Be(InputErrorCode.InvalidArguments);
        result.Error.Should().Contain("10,500").And.Contain("10,000").And.Contain("Nothing was sent");
        (Environment.TickCount64 - started).Should().BeLessThan(5_000);
        VerifyNothingCheckedOrSent();
    }

    [Fact]
    public async Task WaitsOfADelayAboveItsMaximum_AreCountedAsClamped()
    {
        // delayMs is clamped to 500 first: 21 x 500 is 10,500
        var result = await CreateSut().SendKeysAsync("{a 22}", delayMs: 100_000);

        result.ErrorCode.Should().Be(InputErrorCode.InvalidArguments);
        result.Error.Should().Contain("10,500");
    }

    [Fact]
    public void TheMaximumWait_IsPressKeysMaximum()
    {
        InputSimulationService.MaxSendKeysDurationMs.Should().Be(InputSimulationService.MaxPressKeysDurationMs);
    }

    [Fact]
    public void TheWaits_AreCountedInLong_SoVeryManyKeysCannotWrapBelowTheMaximum()
    {
        // 4,294,969 keys with 500 ms between them wait 2,147,484,000 ms, past int.MaxValue: counted in int, that wraps to a
        // negative number, which passes the 10,000 ms check and holds the input gate for weeks
        InputSimulationService.SendKeysWaitMs(4_294_969, 500).Should().Be(2_147_484_000L);
        InputSimulationService.SendKeysWaitMs(22, 500).Should().Be(10_500);
        InputSimulationService.SendKeysWaitMs(1, 500).Should().Be(0);
    }

    #endregion

    #region Batches without delayMs

    [Fact]
    public async Task AShortCall_IsStillOneBatch()
    {
        var result = await CreateSut().SendKeysAsync(Presses('a', Batch));

        result.Success.Should().BeTrue(result.Error);
        _input.Batches.Should().ContainSingle();
    }

    [Fact]
    public async Task ALongCall_GoesInBatchesOfWholeKeys_EachCheckedAgainFirst()
    {
        var result = await CreateSut().SendKeysAsync(Presses('a', 100) + Presses('b', 100));

        result.Success.Should().BeTrue(result.Error);
        result.KeysSent.Should().Be(200);
        _input.Batches.Select(b => b.Count).Should().Equal(2 * Batch, 2 * Batch, 2 * Batch, 2 * (200 - 3 * Batch));
        _input.Events.Should().HaveCount(400);
        _guard.Verify(g => g.RecheckKeyboard(It.IsAny<InputGuardResult>()), Times.Exactly(4));
        VerifyNothingIsDown();
    }

    /// <summary>How many key-downs <paramref name="batch"/> has: every key going down, a modifier's included.</summary>
    private static int KeyDowns(IReadOnlyList<InputEvent> batch) => batch.Count(e => (e.Flags & KeyUp) == 0);

    [Fact]
    public async Task ModifierEventsThatPressNoKey_AreSplitIntoBatchesByTheirKeyDowns_EachCheckedAgainFirst()
    {
        // 40 x (Shift down, Ctrl down, Shift up, Ctrl up): 80 key-downs, no key press
        var result = await CreateSut().SendKeysAsync(string.Concat(Enumerable.Repeat(CrossedModifiers, 40)));

        result.Success.Should().BeTrue(result.Error);
        _input.Batches.Select(KeyDowns).Should().Equal(Batch, 80 - Batch);
        _guard.Verify(g => g.RecheckKeyboard(It.IsAny<InputGuardResult>()), Times.Exactly(2));
        VerifyNothingIsDown();
    }

    [Fact]
    public async Task AModifierTapSplitAcrossTwoKeys_CountsItsKeyDownInTheBatchSplit()
    {
        // {SHIFT down} and {SHIFT up} are two keys: a tap that neither key presses alone
        var result = await CreateSut().SendKeysAsync(string.Concat(Enumerable.Repeat("{SHIFT down}{SHIFT up}", 100)));

        result.Success.Should().BeTrue(result.Error);
        result.KeysSent.Should().Be(200);
        _input.Batches.Select(KeyDowns).Should().Equal(Batch, 100 - Batch);
        VerifyNothingIsDown();
    }

    [Fact]
    public async Task TheModifiersHeldForAKey_CountInTheBatchSplit()
    {
        // Every capital is Shift down with its key: two key-downs, so 32 capitals fill a batch
        var result = await CreateSut().SendKeysAsync(new string('A', 100));

        result.Success.Should().BeTrue(result.Error);
        result.KeysSent.Should().Be(100);
        _input.Batches.Select(KeyDowns).Should().Equal(Batch, Batch, Batch, 200 - 3 * Batch);
        VerifyNothingIsDown();
    }

    [Fact]
    public async Task AGroupLongerThanABatch_GoesWhole_InABatchOfItsOwn()
    {
        // A key (here a group) is never split; the keys before it go first
        var result = await CreateSut().SendKeysAsync("ab+(" + new string('c', 100) + ")d");

        result.Success.Should().BeTrue(result.Error);
        result.KeysSent.Should().Be(4);
        _input.Batches.Select(b => b.Count).Should().Equal(4, 1 + 200 + 1, 2);
    }

    [Fact]
    public async Task ALongCall_StopsWhenAnotherWindowComesToTheFront_AndSaysHowFarItGot()
    {
        var calls = 0;
        _guard.Setup(g => g.RecheckKeyboard(It.IsAny<InputGuardResult>()))
            .Returns((InputGuardResult approved) => ++calls <= 1
                ? approved
                : InputGuardResult.Refuse(InputErrorCode.TargetLostFocus, "Another window came to the front.", InputTarget.None, null));

        var result = await CreateSut().SendKeysAsync(Presses('a', 200));

        result.ErrorCode.Should().Be(InputErrorCode.TargetLostFocus);
        result.KeysSent.Should().Be(Batch);
        result.Error.Should().StartWith($"Sending stopped after {Batch} of 200 keys");
        _input.Batches.Should().ContainSingle();
    }

    [Fact]
    public async Task ALongCall_AModifierTheUserPressesMeanwhile_StopsIt()
    {
        _input.BeforeSend = _ =>
        {
            if (_input.Batches.Count == 0)
            {
                _input.Press(0xA2); // the user's left Ctrl, while the first batch goes in
            }
        };

        var result = await CreateSut().SendKeysAsync(Presses('a', 200));

        result.ErrorCode.Should().Be(InputErrorCode.ModifiersHeld);
        result.KeysSent.Should().Be(Batch);
        _input.Batches.Should().ContainSingle();
    }

    [Fact]
    public async Task ALongCall_TheUsersPause_StopsItBetweenBatches()
    {
        _input.BeforeSend = _ =>
        {
            if (_input.Batches.Count == 0)
            {
                _session.Pause(InputControlSources.Hotkey);
            }
        };

        var result = await CreateSut().SendKeysAsync("{SHIFT down}" + Presses('a', 200));

        result.ErrorCode.Should().Be(InputErrorCode.PausedByUser);
        result.KeysSent.Should().Be(Batch, "{SHIFT down} and the keys of the first batch's other key-downs");
        VerifyNothingIsDown();
    }

    [Fact]
    public async Task ALongCall_AKeyItHoldsDownAcrossBatches_IsNotTakenForTheUsers()
    {
        var result = await CreateSut().SendKeysAsync("{SHIFT down}" + Presses('a', 200) + "{SHIFT up}");

        result.Success.Should().BeTrue(result.Error);
        result.KeysSent.Should().Be(202);
        _input.Batches.Should().HaveCountGreaterThan(1);
        VerifyNothingIsDown();
    }

    [Fact]
    public async Task ALongCall_ShortInsertInALaterBatch_CountsTheKeysBefore()
    {
        // The second batch: SendInput inserts 'b' down and up, then the next 'b' down only
        _input.Insert = (events, call) => call == 1 ? 3u : (uint)events.Count;

        var result = await CreateSut().SendKeysAsync(Presses('a', Batch) + Presses('b', 10));

        result.ErrorCode.Should().Be(InputErrorCode.SendInputFailed);
        result.KeysSent.Should().Be(Batch + 1);
        result.Error.Should().Contain($"{Batch + 1} of {Batch + 10} keys");
        VerifyNothingIsDown();
    }

    #endregion

    #region press_keys

    [Fact]
    public async Task PressKeys_WithoutWaits_StaysOneBatch()
    {
        // press_keys is bounded by its own 200 presses
        var result = await CreateSut().PressKeysAsync(KeyList.Of("tab*100 enter*100"), 1, 0, 0, InputTarget.None);

        result.Success.Should().BeTrue(result.Error);
        _input.Batches.Should().ContainSingle();
    }

    #endregion
}
