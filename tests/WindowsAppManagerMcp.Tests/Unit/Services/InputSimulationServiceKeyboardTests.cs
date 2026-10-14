namespace WindowsAppManagerMcp.Tests.Unit.Services;

/// <summary>
/// The keyboard engine in send_keys and type_text (roadmap M4.1): keys mapped with the target window's layout and Caps
/// Lock, delayMs pacing with the guard checked before every batch, progress when SendInput stops part way, and type_text's
/// controlChars. The native input layer is <see cref="FakeInputWrapper"/>, the layouts <see cref="FakeKeyboardLayoutWrapper"/>
/// and the guard a mock, so nothing reaches the desktop.
/// </summary>
public class InputSimulationServiceKeyboardTests
{
    private const uint Extended = 0x0001, KeyUp = 0x0002;
    private const ushort VkShift = 0x10, VkControl = 0x11, VkRMenu = 0xA5, VkLControl = 0xA2, VkReturn = 0x0D, VkTab = 0x09, VkCapital = 0x14;
    private static readonly nint Target = 0x200;

    private readonly FakeInputWrapper _input = new();
    private readonly FakeKeyboardLayoutWrapper _layouts = new();
    private readonly Mock<IInputTargetGuard> _guard = TestDataFactory.CreateAllowingInputGuard();
    private readonly InputSession _session;

    public InputSimulationServiceKeyboardTests()
    {
        _session = new InputSession(_input);
    }

    private InputSimulationService CreateSut() =>
        new(TestDataFactory.CreateMockMonitorService().Object, _input, _guard.Object, _session, keyboardLayout: _layouts);

    private static InputEvent[] Tap(ushort vk, ushort scan, bool extended = false) =>
    [
        InputEvent.Key(vk, extended ? Extended : 0, scan), InputEvent.Key(vk, (extended ? Extended : 0) | KeyUp, scan)
    ];

    private static InputEvent[] Unicode(char c) => [InputEvent.Unicode(c, 0), InputEvent.Unicode(c, KeyUp)];

    private void VerifyNothingIsDown()
    {
        _session.ServerHeld.Should().BeEmpty();
        foreach (var key in new ushort[] { VkShift, VkControl, VkRMenu, 0xA0, 0xA2, 'A', 'B' })
        {
            _input.IsKeyDown(key).Should().BeFalse($"key 0x{key:X2} must not stay down");
        }
    }

    #region send_keys: the layout

    [Fact]
    public async Task SendKeys_MapsWithTheTargetWindowsLayout()
    {
        // Arrange - the target's thread types Norwegian, whatever the foreground window types
        _layouts.WindowLayouts[Target] = FakeKeyboardLayoutWrapper.Norwegian;

        // Act
        var result = await CreateSut().SendKeysAsync("@", InputTarget.Window(Target));

        // Assert - AltGr+2, sent as the left Ctrl and the right Alt
        result.Success.Should().BeTrue();
        _layouts.LayoutQueries.Should().Equal(Target);
        _input.Events.Should().Equal(
            InputEvent.Key(VkLControl, 0, 0x1D), InputEvent.Key(VkRMenu, Extended, 0x38),
            InputEvent.Key('2', 0, 0x03), InputEvent.Key('2', KeyUp, 0x03),
            InputEvent.Key(VkRMenu, Extended | KeyUp, 0x38), InputEvent.Key(VkLControl, KeyUp, 0x1D));
        VerifyNothingIsDown();
    }

    [Fact]
    public async Task SendKeys_WithoutATarget_MapsWithTheForegroundWindowsLayout()
    {
        _layouts.Layout = FakeKeyboardLayoutWrapper.French;

        (await CreateSut().SendKeysAsync("a1")).Success.Should().BeTrue();

        _layouts.LayoutQueries.Should().Equal((nint)0);
        _input.Events.Should().Equal([
            .. Tap('A', 0x10),
            InputEvent.Key(VkShift, 0, 0x2A), .. Tap('1', 0x02), InputEvent.Key(VkShift, KeyUp, 0x2A)]);
    }

    [Fact]
    public async Task SendKeys_WithCapsLockOn_KeepsTheTextAsWritten()
    {
        _input.Toggled.Add(VkCapital);

        (await CreateSut().SendKeysAsync("Hi")).Success.Should().BeTrue();

        _input.Events.Should().Equal([
            .. Tap('H', 0x23),
            InputEvent.Key(VkShift, 0, 0x2A), .. Tap('I', 0x17), InputEvent.Key(VkShift, KeyUp, 0x2A)]);
    }

    [Fact]
    public async Task SendKeys_ASyntaxError_IsInvalidKeySpecification_AndNothingIsChecked()
    {
        var result = await CreateSut().SendKeysAsync("100%");

        result.ErrorCode.Should().Be(InputErrorCode.InvalidKeySpecification);
        result.Error.Should().Contain("{%}");
        result.KeysSent.Should().BeNull();
        _input.Batches.Should().BeEmpty();
        _guard.Verify(g => g.CheckKeyboard(It.IsAny<InputTarget>(), It.IsAny<KeyboardInputKind>(), It.IsAny<IReadOnlyList<InputEvent>?>()), Times.Never);
    }

    [Fact]
    public async Task SendKeys_TheGuardSeesTheMappedKeys()
    {
        IReadOnlyList<InputEvent>? checkedKeys = null;
        _guard.Setup(g => g.CheckKeyboard(It.IsAny<InputTarget>(), KeyboardInputKind.Keys, It.IsAny<IReadOnlyList<InputEvent>?>()))
            .Callback((InputTarget _, KeyboardInputKind _, IReadOnlyList<InputEvent>? keys) => checkedKeys = keys)
            .Returns((InputTarget target, KeyboardInputKind _, IReadOnlyList<InputEvent>? _) =>
                InputGuardResult.Allow(target, Target, new WindowSummary(Target, "Notepad", "notepad", 1), Target));

        await CreateSut().SendKeysAsync("{CTRL down}w{CTRL up}", InputTarget.Window(Target));

        SendKeysParser.Chords(checkedKeys!).Should().Equal(new KeyChord(KeyModifiers.Control, InputEventKind.VirtualKey, 'W'));
    }

    #endregion

    #region send_keys: one batch, or paced with delayMs

    [Fact]
    public async Task SendKeys_WithoutADelay_SendsEverythingInOneBatch()
    {
        var result = await CreateSut().SendKeysAsync("ab{LEFT 3}");

        result.Success.Should().BeTrue();
        result.KeysSent.Should().Be(5);
        _input.Batches.Should().ContainSingle();
    }

    [Fact]
    public async Task SendKeys_WithADelay_SendsEachKeyInItsOwnBatch_AndRechecksTheWindowBeforeEach()
    {
        var result = await CreateSut().SendKeysAsync("a{LEFT 2}+(bc)", delayMs: 1);

        result.Success.Should().BeTrue();
        result.KeysSent.Should().Be(4);
        _input.Batches.Select(b => b.Count).Should().Equal(2, 2, 2, 6);
        _guard.Verify(g => g.RecheckKeyboard(It.IsAny<InputGuardResult>()), Times.Exactly(4));
    }

    [Fact]
    public async Task SendKeys_ADelayAboveTheMaximum_IsClamped()
    {
        var watch = System.Diagnostics.Stopwatch.StartNew();
        var sut = CreateSut();

        var result = await sut.SendKeysAsync("ab", delayMs: 100_000, cancellationToken: new CancellationTokenSource(TimeSpan.FromSeconds(30)).Token);

        result.Success.Should().BeTrue();
        watch.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task SendKeys_WithADelay_StopsWhenAnotherWindowComesToTheFront_AndSaysHowFarItGot()
    {
        // Arrange - the window in front changes after the second key
        var calls = 0;
        _guard.Setup(g => g.RecheckKeyboard(It.IsAny<InputGuardResult>()))
            .Returns((InputGuardResult approved) => ++calls <= 2
                ? approved
                : InputGuardResult.Refuse(InputErrorCode.TargetLostFocus, "Another window came to the front.", InputTarget.None, null));

        // Act
        var result = await CreateSut().SendKeysAsync("abc", delayMs: 1);

        // Assert
        result.ErrorCode.Should().Be(InputErrorCode.TargetLostFocus);
        result.KeysSent.Should().Be(2);
        result.Error.Should().StartWith("Sending stopped after 2 of 3 keys");
        _input.Batches.Should().HaveCount(2);
    }

    [Fact]
    public async Task SendKeys_WithADelay_AKeyItHoldsDown_IsNotTakenForTheUsers()
    {
        // {SHIFT down} in the first batch: Windows reports Shift down before the next one, and that Shift is the server's
        var result = await CreateSut().SendKeysAsync("{SHIFT down}{RIGHT 2}{SHIFT up}", delayMs: 1);

        result.Success.Should().BeTrue();
        result.KeysSent.Should().Be(4);
        VerifyNothingIsDown();
    }

    [Fact]
    public async Task SendKeys_WithADelay_AModifierTheUserPressesMeanwhile_StopsIt()
    {
        _input.BeforeSend = batch =>
        {
            if (_input.Batches.Count == 0)
            {
                _input.Press(0xA2); // the user's left Ctrl, while the first key goes in
            }
        };

        var result = await CreateSut().SendKeysAsync("abc", delayMs: 1);

        result.ErrorCode.Should().Be(InputErrorCode.ModifiersHeld);
        result.KeysSent.Should().Be(1);
        result.Error.Should().StartWith("Sending stopped after 1 of 3 keys");
    }

    [Fact]
    public async Task SendKeys_WithADelay_TheUsersPause_StopsItBetweenKeys()
    {
        _input.BeforeSend = _ =>
        {
            if (_input.Batches.Count == 0)
            {
                _session.Pause("hotkey");
            }
        };

        var result = await CreateSut().SendKeysAsync("{SHIFT down}abc", delayMs: 1);

        result.ErrorCode.Should().Be(InputErrorCode.PausedByUser);
        result.KeysSent.Should().Be(1);
        VerifyNothingIsDown();
    }

    #endregion

    #region send_keys: SendInput stops part way

    [Fact]
    public async Task SendKeys_ShortInsert_ReleasesWhatWentDown_AndSaysHowManyKeysWereSent()
    {
        // Arrange - SendInput inserts Ctrl down, A down and up, then B down only
        _input.Insert = (events, call) => call == 0 ? 4u : (uint)events.Count;

        // Act
        var result = await CreateSut().SendKeysAsync("^(ab)c");

        // Assert - the group is one key and did not finish
        result.ErrorCode.Should().Be(InputErrorCode.SendInputFailed);
        result.KeysSent.Should().Be(0);
        result.Error.Should().Contain("inserted only 4 of").And.Contain("0 of 2 keys");
        VerifyNothingIsDown();
    }

    [Fact]
    public async Task SendKeys_ShortInsert_CountsTheKeysBeforeIt()
    {
        _input.Insert = (events, call) => call == 0 ? 5u : (uint)events.Count;

        var result = await CreateSut().SendKeysAsync("ab+c");

        result.KeysSent.Should().Be(2);
        VerifyNothingIsDown();
    }

    [Fact]
    public async Task SendKeys_WithADelay_ShortInsert_CountsTheBatchesBefore()
    {
        _input.Insert = (events, call) => call == 2 ? 1u : (uint)events.Count;

        var result = await CreateSut().SendKeysAsync("ab+c", delayMs: 1);

        result.ErrorCode.Should().Be(InputErrorCode.SendInputFailed);
        result.KeysSent.Should().Be(2);
        VerifyNothingIsDown();
    }

    #endregion

    #region type_text: controlChars

    [Theory]
    [InlineData("line one\nline two")]
    [InlineData("a\r\nb")]
    [InlineData("a\rb")]
    [InlineData("name\tvalue")]
    public async Task TypeText_WithALineBreakOrTab_AndNoControlChars_IsRefused_AndNothingIsChecked(string text)
    {
        var result = await CreateSut().TypeTextAsync(text);

        result.ErrorCode.Should().Be(InputErrorCode.InvalidArguments);
        result.Error.Should().Contain("controlChars").And.Contain("'unicode'").And.Contain("'keys'").And.Contain("'shiftEnter'");
        _input.Batches.Should().BeEmpty();
        _guard.Verify(g => g.CheckKeyboard(It.IsAny<InputTarget>(), It.IsAny<KeyboardInputKind>(), It.IsAny<IReadOnlyList<InputEvent>?>()), Times.Never);
    }

    [Fact]
    public async Task TypeText_Unicode_TypesTheLineBreakAsACharacter_AfterAWindowsLineBreakBecameOne()
    {
        var result = await CreateSut().TypeTextAsync("a\r\nb\tc", controlChars: TextControlChars.Unicode);

        result.Success.Should().BeTrue();
        result.CharactersTyped.Should().Be(5);
        result.Text.Should().Be("a\r\nb\tc");
        _input.Events.Should().Equal([.. Unicode('a'), .. Unicode('\n'), .. Unicode('b'), .. Unicode('\t'), .. Unicode('c')]);
    }

    [Fact]
    public async Task TypeText_Keys_PressesEnterAndTab()
    {
        var result = await CreateSut().TypeTextAsync("a\nb\tc", controlChars: TextControlChars.Keys);

        result.Success.Should().BeTrue();
        _input.Events.Should().Equal([.. Unicode('a'), .. Tap(VkReturn, 0x1C), .. Unicode('b'), .. Tap(VkTab, 0x0F), .. Unicode('c')]);
    }

    [Fact]
    public async Task TypeText_ShiftEnter_PressesShiftEnter_AndTypesTabsAsCharacters()
    {
        var result = await CreateSut().TypeTextAsync("a\nb\tc", controlChars: TextControlChars.ShiftEnter);

        result.Success.Should().BeTrue();
        _input.Events.Should().Equal([
            .. Unicode('a'),
            InputEvent.Key(VkShift, 0, 0x2A), .. Tap(VkReturn, 0x1C), InputEvent.Key(VkShift, KeyUp, 0x2A),
            .. Unicode('b'), .. Unicode('\t'), .. Unicode('c')]);
        VerifyNothingIsDown();
    }

    [Fact]
    public async Task TypeText_ShiftEnter_ShortInsert_CountsOnlyWholeCharacters()
    {
        // 'a' (2 events), then Shift down and Enter down of the line break (4 events in all)
        _input.Insert = (events, call) => call == 0 ? 4u : (uint)events.Count;

        var result = await CreateSut().TypeTextAsync("a\nb", controlChars: TextControlChars.ShiftEnter);

        result.ErrorCode.Should().Be(InputErrorCode.SendInputFailed);
        result.CharactersTyped.Should().Be(1);
        VerifyNothingIsDown();
    }

    [Fact]
    public async Task TypeText_WithoutControlCharacters_NeedsNoControlChars_AndStaysUnicode()
    {
        var result = await CreateSut().TypeTextAsync("Hi!");

        result.Success.Should().BeTrue();
        _input.Events.Should().Equal([.. Unicode('H'), .. Unicode('i'), .. Unicode('!')]);
        _layouts.LayoutQueries.Should().BeEmpty("Unicode typing needs no layout");
    }

    #endregion
}
