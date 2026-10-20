namespace WindowsAppManagerMcp.Tests.Unit.Services;

/// <summary>
/// What press_keys and send_keys tell about the keys that went out (issues #8 to #10), when the call went through or stopped
/// part way: the chords as pressed on the window's layout (resolvedChords), what went out as a character with no key
/// (sentAsCharacters), and the warnings SentAsCharacter, KeyboardLayoutUnknown, SystemHotkey and DeadKeyPressed. The native
/// input layer is <see cref="FakeInputWrapper"/>, the layouts and Windows' hotkeys come from
/// <see cref="FakeKeyboardLayoutWrapper"/>, and the guard is a mock, so nothing reaches the desktop or the registry.
/// </summary>
public class InputSimulationServiceKeyReportTests
{
    private static readonly InputTarget Duetty = InputTarget.Window(0x5150);

    /// <summary>The owner's Ctrl+Shift+0 (issue #10): HKCU\Control Panel\Input Method\Hot Keys\00000104.</summary>
    private static readonly InputMethodHotkey CtrlShift0 =
        InputMethodHotkey.FromRegistry("00000104", new byte[] { 0x06, 0xC0, 0x00, 0x00 }, new byte[] { 0x30, 0x00, 0x00, 0x00 })!;

    private readonly FakeInputWrapper _input = new();
    private readonly FakeKeyboardLayoutWrapper _layouts = new() { Layout = FakeKeyboardLayoutWrapper.Norwegian };
    private readonly Mock<IInputTargetGuard> _guard = TestDataFactory.CreateAllowingInputGuard();
    private readonly InputSession _session;

    public InputSimulationServiceKeyReportTests()
    {
        _session = new InputSession(_input);
    }

    private InputSimulationService CreateSut() =>
        new(TestDataFactory.CreateMockMonitorService().Object, _input, _guard.Object, _session, keyboardLayout: _layouts);

    private Task<SendKeysResult> PressAsync(string keys) => CreateSut().PressKeysAsync(KeyList.Of(keys), 1, 0, 0, Duetty);

    [Fact]
    public async Task AChordPressedAsWritten_AddsNothingToTheResult()
    {
        _layouts.Layout = FakeKeyboardLayoutWrapper.Us;

        var result = await PressAsync("ctrl+s");

        result.Success.Should().BeTrue(result.Error);
        result.ResolvedChords.Should().BeNull();
        result.SentAsCharacters.Should().BeNull();
        result.Warnings.Should().BeNull();
    }

    [Fact]
    public async Task AChordWhoseCharacterNeedsShift_IsReportedAsPressed()
    {
        // Issue #10: '=' is Shift+0 on Norwegian
        var result = await PressAsync("ctrl+=");

        result.Success.Should().BeTrue(result.Error);
        result.ResolvedChords.Should().Equal(new Dictionary<string, string> { ["ctrl+="] = "ctrl+shift+0" });
        result.Warnings.Should().BeNull("no Windows hotkey is Ctrl+Shift+0 here");
        _input.Batches.Should().ContainSingle().Which.Where(e => (e.Flags & 0x0002) == 0).Select(e => e.Code).Should()
            .Equal(0x11, 0x10, (ushort)'0');
    }

    [Fact]
    public async Task AChordSentAsTheCharacter_IsReported_WithASentAsCharacterWarning()
    {
        // Issue #9: '^' is only a dead key on Norwegian, so 'ctrl+^' went out as Ctrl and the character, with no key
        var result = await PressAsync("ctrl+^");

        result.Success.Should().BeTrue(result.Error);
        result.SentAsCharacters.Should().Equal("ctrl+^");
        var warning = result.Warnings.Should().ContainSingle().Subject;
        warning.Should().StartWith("SentAsCharacter: 'ctrl+^' ('^') went out with its character alone, with no key")
            .And.Contain("only a dead key").And.Contain("KeyboardEvent.code").And.Contain("oem_plus");
    }

    [Fact]
    public async Task WhenTheWindowsLayoutCannotBeRead_TheResultSaysSo_AndWhatWentAsACharacter()
    {
        // Issue #9's other cause: GetKeyboardLayout gave 0 for the window, so '+' had no key and went as the character
        _layouts.WindowLayouts[0x5150] = 0;

        var result = await PressAsync("alt+shift+plus");

        result.Success.Should().BeTrue(result.Error);
        result.SentAsCharacters.Should().Equal("alt+shift+plus");
        result.Warnings.Should().HaveCount(2);
        result.Warnings![0].Should().StartWith("KeyboardLayoutUnknown: ").And.Contain("without scan codes");
        result.Warnings[1].Should().StartWith("SentAsCharacter: 'alt+shift+plus' ('+') went out with its character alone").And.Contain("could not be read");
    }

    [Fact]
    public async Task WhileAnAccentWaitsOnTheLayout_AltShiftPlus_IsTheKey_WithNothingToWarnAbout()
    {
        // Issue #8 as the owner saw it: Alt+Shift+\ left a grave accent waiting, and Alt+Shift+plus then went out with no key
        _layouts.PendingDeadKey = '`';

        var result = await PressAsync("alt+shift+plus");

        result.Success.Should().BeTrue(result.Error);
        result.SentAsCharacters.Should().BeNull();
        result.Warnings.Should().BeNull();
        _input.Batches.Should().ContainSingle().Which.Should().Contain(InputEvent.Key(0xBB, 0, 0x0C));
    }

    [Fact]
    public async Task AChordThatIsAWindowsInputMethodHotkey_IsSent_WithASystemHotkeyWarning()
    {
        // Issue #10: 'ctrl+=' is Ctrl+Shift+0 on Norwegian, which this user's Windows switches the input language with
        _layouts.SystemHotkeys = new SystemKeyboardHotkeys([CtrlShift0], []);

        var result = await PressAsync("ctrl+=");

        result.Success.Should().BeTrue(result.Error);
        result.KeysSent.Should().Be(1);
        result.ResolvedChords.Should().Equal(new Dictionary<string, string> { ["ctrl+="] = "ctrl+shift+0" });
        result.Warnings.Should().ContainSingle().Which.Should()
            .StartWith(@"SystemHotkey: ctrl+shift+0 (pressed for 'ctrl+=') is a Windows input-method hotkey (HKCU\Control Panel\Input Method\Hot Keys\00000104)")
            .And.Contain("so Windows may take it, and the application may not get it", "nothing has shown on a desktop yet that it does");
        _layouts.SystemHotkeyReads.Should().Be(1);
    }

    [Fact]
    public async Task SendKeys_WarnsOfTheSameHotkey_InItsOwnSpelling()
    {
        _layouts.SystemHotkeys = new SystemKeyboardHotkeys([CtrlShift0], []);

        var result = await CreateSut().SendKeysAsync("^+0", Duetty);

        result.Success.Should().BeTrue(result.Error);
        result.ResolvedChords.Should().BeNull("send_keys names its keys itself");
        result.Warnings.Should().ContainSingle().Which.Should().StartWith("SystemHotkey: ^+0 is a Windows input-method hotkey");
    }

    [Fact]
    public async Task ModifiersPressedAlone_ThatSwitchTheInputLanguage_AreSent_WithASystemHotkeyWarning()
    {
        _layouts.SystemHotkeys = new SystemKeyboardHotkeys([], SystemKeyboardHotkeys.TogglesFrom(keyExists: false, null, null, null));

        var result = await PressAsync("alt+shift");

        result.Success.Should().BeTrue(result.Error);
        result.Warnings.Should().ContainSingle().Which.Should()
            .StartWith(@"SystemHotkey: alt+shift is Windows' switch of the input language (HKCU\Keyboard Layout\Toggle, Language Hotkey 1)")
            .And.Contain("another layout");
    }

    [Fact]
    public async Task WhenWindowsHotkeysCannotBeRead_TheCallGoesThrough_WithoutAWarning()
    {
        _layouts.SystemHotkeys = null;

        var result = await PressAsync("ctrl+=");

        result.Success.Should().BeTrue(result.Error);
        result.Warnings.Should().BeNull();
        _layouts.SystemHotkeyReads.Should().Be(1);
    }

    [Fact]
    public async Task WindowsHotkeys_AreReadOncePerCall_AndNotForKeysWithoutAModifier()
    {
        await PressAsync("ctrl+a ctrl+c ctrl+= tab");
        _layouts.SystemHotkeyReads.Should().Be(1);

        await PressAsync("tab*3 enter");
        await CreateSut().SendKeysAsync("hello{ENTER}", Duetty);
        _layouts.SystemHotkeyReads.Should().Be(1, "no hotkey or toggle is pressed without a modifier");
    }

    [Fact]
    public async Task SendKeys_ReportsTheCharactersItSentWithoutAKey()
    {
        _layouts.Layout = FakeKeyboardLayoutWrapper.Us;

        var result = await CreateSut().SendKeysAsync("a😀é😀", Duetty);

        result.Success.Should().BeTrue(result.Error);
        result.SentAsCharacters.Should().Equal("😀", "é");
        result.Warnings.Should().ContainSingle().Which.Should()
            .StartWith("SentAsCharacter: '😀', 'é' went out as characters alone, with no key").And.Contain("A text field gets them")
            .And.Contain("{OEM_PLUS}");
    }

    [Fact]
    public async Task AChordPressedOnALetterOrDigitKey_IsReportedAsPressed()
    {
        // On French AZERTY '-' is the unshifted 6 key, so 'ctrl+-' (zoom out) is the shortcut Ctrl+6: a different chord
        _layouts.Layout = FakeKeyboardLayoutWrapper.French;

        var result = await PressAsync("ctrl+- ctrl+=");

        result.Success.Should().BeTrue(result.Error);
        result.ResolvedChords.Should().Equal(new Dictionary<string, string> { ["ctrl+-"] = "ctrl+6" },
            "'=' is a punctuation key of its own on AZERTY, pressed as asked");
    }

    [Fact]
    public async Task AResolvedChord_NamesTheKeysThatWentDown_InNamesPressKeysTakes()
    {
        // AltGr is the left Ctrl and the right Alt; a chord that already holds one of them adds only the other, or none
        var result = await PressAsync("ctrl+@ @ ctrl+ralt+@");

        result.Success.Should().BeTrue(result.Error);
        result.ResolvedChords.Should().Equal(new Dictionary<string, string>
        {
            ["ctrl+@"] = "ctrl+ralt+2", ["@"] = "lctrl+ralt+2", ["ctrl+ralt+@"] = "ctrl+ralt+2"
        });
    }

    [Theory]
    [InlineData("lctrl+=")]
    [InlineData("shift+ctrl+=")]
    public async Task ASystemHotkeyWarning_NamesTheChordAsked_HoweverItIsWritten(string keys)
    {
        _layouts.SystemHotkeys = new SystemKeyboardHotkeys([CtrlShift0], []);

        var result = await PressAsync(keys);

        result.Success.Should().BeTrue(result.Error);
        result.Warnings.Should().ContainSingle().Which.Should().StartWith($"SystemHotkey: ctrl+shift+0 (pressed for '{keys}') is a Windows input-method hotkey");
    }

    [Fact]
    public async Task WindowsHotkeys_AreNotReadForALetterOnTheGraveAccentsVirtualKey()
    {
        // VK_OEM_3 is 'ø' on Norwegian (scan 27); the grave accent toggle is the key left of 1 (scan 29)
        await CreateSut().SendKeysAsync("hø", Duetty);

        _layouts.SystemHotkeyReads.Should().Be(0);
    }

    [Fact]
    public async Task SendKeys_ListsAtMostTwentyCharactersWithoutAKey_AndTheWarningCountsThemAll()
    {
        // 66 Cyrillic letters on a US keyboard: none has a key
        _layouts.Layout = FakeKeyboardLayoutWrapper.Us;
        const string russian = "абвгдеёжзийклмнопрстуфхцчшщъыьэюяАБВГДЕЁЖЗИЙКЛМНОПРСТУФХЦЧШЩЪЫЬЭЮЯ";

        var result = await CreateSut().SendKeysAsync(russian, Duetty);

        result.Success.Should().BeTrue(result.Error);
        result.SentAsCharacters.Should().Equal(russian.Take(20).Select(c => c.ToString()));
        result.Warnings.Should().ContainSingle().Which.Should().StartWith("SentAsCharacter: 66 characters went out as characters alone")
            .And.Contain("'а', 'б', 'в', 'г', 'д' and 61 more");
    }

    [Fact]
    public async Task SentAsCharacter_GivesEachCharactersOwnReason()
    {
        // While a grave accent waits, the '+' key would type "`+", so '+' went as the character; no key types the emoji at all
        _layouts.PendingDeadKey = '`';

        var result = await CreateSut().SendKeysAsync("{+}😀", Duetty);

        result.Success.Should().BeTrue(result.Error);
        result.SentAsCharacters.Should().Equal("+", "😀");
        result.Warnings.Should().ContainSingle().Which.Should().StartWith(
            "SentAsCharacter: '+', '😀' went out as characters alone, with no key ('+': an accent typed on a dead key was waiting on " +
            "the window's keyboard layout, and its key would have typed it with that accent; '😀': no key of the window's keyboard " +
            "layout types it, or only a dead key does).");
    }

    [Fact]
    public async Task SendKeys_WhileAnAccentWaits_BlamesTheAccent_AlsoForALetterItCombinesWith()
    {
        // The H, L and O keys would type "`h", "`l" and "`o", and the E key 'è': the accent kept each from its key (review)
        _layouts.PendingDeadKey = '`';

        var result = await CreateSut().SendKeysAsync("hello", Duetty);

        result.Success.Should().BeTrue(result.Error);
        result.SentAsCharacters.Should().Equal("h", "e", "l", "o");
        result.Warnings.Should().ContainSingle().Which.Should().StartWith(
            "SentAsCharacter: 'h', 'e', 'l', 'o' went out as characters alone, with no key (an accent typed on a dead key was waiting " +
            "on the window's keyboard layout, and their keys would have typed them with that accent).");
    }

    [Theory]
    [InlineData("plus")]
    [InlineData("shift+plus")]
    public async Task WhileAnAccentWaits_AChordWithoutCtrlAltOrWin_StaysItsCharacter_WithTheReason(string keys)
    {
        // As send_keys' text: the key would type the waiting accent too ("`+"), so the character goes alone (issue #8 review)
        _layouts.PendingDeadKey = '`';

        var result = await PressAsync(keys);

        result.Success.Should().BeTrue(result.Error);
        result.SentAsCharacters.Should().Equal(keys);
        result.Warnings.Should().ContainSingle().Which.Should().StartWith(
            $"SentAsCharacter: '{keys}' ('+') went out with its character alone, with no key (an accent typed on a dead key was waiting " +
            "on the window's keyboard layout, and its key would have typed it with that accent).");
        _input.Batches.Should().ContainSingle().Which.Should().Contain(InputEvent.Unicode('+', 0)).And.NotContain(e => e.Code == 0xBB);
    }

    [Fact]
    public async Task AChordThatPressesADeadKey_IsSent_WithADeadKeyPressedWarning()
    {
        // Issue #8's trigger: Alt+Shift+\ on Norwegian is the dead grave accent, which may stay waiting in the target
        var result = await PressAsync(@"alt+shift+\");

        result.Success.Should().BeTrue(result.Error);
        result.SentAsCharacters.Should().BeNull();
        result.Warnings.Should().ContainSingle().Which.Should().Be(
            @"DeadKeyPressed: 'alt+shift+\' ('`') pressed a dead key of the window's keyboard layout, so its accent may be left " +
            "waiting in the target, and the next key typed there may then come out with it, in front of its character or " +
            "combined with it into one letter. If text follows, check what it gives.");
    }

    [Fact]
    public async Task SendKeys_WarnsOfDeadKeys_InItsOwnSpelling()
    {
        // The ¨ key unshifted, and the \ key with Alt and Shift
        var result = await CreateSut().SendKeysAsync(@"{OEM_1}%+\", Duetty);

        result.Success.Should().BeTrue(result.Error);
        result.Warnings.Should().ContainSingle().Which.Should().StartWith(
            "DeadKeyPressed: '{OEM1}' ('¨'), '%+{OEM4}' ('`') pressed dead keys of the window's keyboard layout, so their accents " +
            "may be left waiting in the target");
    }

    [Fact]
    public async Task SendKeys_ReadsAKeyUnderAnExplicitRightAlt_OnItsAltGrLayer()
    {
        // On Norwegian the right Alt is AltGr: with it down the ¨ key is the dead tilde; after it comes up, the dead diaeresis
        var result = await CreateSut().SendKeysAsync("{RALT down}{OEM_1}{RALT up}{OEM_1}", Duetty);

        result.Success.Should().BeTrue(result.Error);
        result.Warnings.Should().ContainSingle().Which.Should().Contain("('~')").And.Contain("('¨')");
    }

    [Fact]
    public async Task ADeadKey_IsNotReported_WhileAnAccentWaits()
    {
        // The dead key then types the waiting accent and its own, and leaves nothing waiting
        _layouts.PendingDeadKey = '`';

        var result = await PressAsync("oem_1");

        result.Success.Should().BeTrue(result.Error);
        result.Warnings.Should().BeNull();
    }

    [Fact]
    public async Task ACallThatStopsPartWay_ReportsOnlyTheKeysThatWentOut()
    {
        // Issue #9: 'ctrl+^' went out as its character before another window came to the front; the dead key and the hotkey
        // after it never went out
        _layouts.SystemHotkeys = new SystemKeyboardHotkeys([CtrlShift0], []);
        var calls = 0;
        _guard.Setup(g => g.RecheckKeyboard(It.IsAny<InputGuardResult>()))
            .Returns((InputGuardResult approved) => ++calls == 1
                ? approved
                : InputGuardResult.Refuse(InputErrorCode.TargetLostFocus, "Another window came to the front.", InputTarget.None, null));

        var result = await CreateSut().PressKeysAsync(KeyList.Of(@"ctrl+^ alt+shift+\ ctrl+="), 1, 0, delayBetweenMs: 1, Duetty);

        result.ErrorCode.Should().Be(InputErrorCode.TargetLostFocus);
        result.KeysSent.Should().Be(1);
        result.SentAsCharacters.Should().Equal("ctrl+^");
        result.ResolvedChords.Should().BeNull("'ctrl+=' did not go out");
        result.Warnings.Should().ContainSingle().Which.Should().StartWith("SentAsCharacter: 'ctrl+^' ('^') went out with its character alone");
    }

    [Fact]
    public async Task ACallStoppedByAModifierTheUserHolds_ReportsTheHotkeyItAlreadyPressed()
    {
        // Issue #10: the chord Windows may take went out before the user's left Alt stopped the call
        _layouts.SystemHotkeys = new SystemKeyboardHotkeys([CtrlShift0], []);
        _input.BeforeSend = _ =>
        {
            if (_input.Batches.Count == 0)
            {
                _input.Press(0xA4);
            }
        };

        var result = await CreateSut().PressKeysAsync(KeyList.Of("ctrl+= ctrl+^"), 1, 0, delayBetweenMs: 1, Duetty);

        result.ErrorCode.Should().Be(InputErrorCode.ModifiersHeld);
        result.KeysSent.Should().Be(1);
        result.ResolvedChords.Should().Equal(new Dictionary<string, string> { ["ctrl+="] = "ctrl+shift+0" });
        result.SentAsCharacters.Should().BeNull("'ctrl+^' did not go out");
        result.Warnings.Should().ContainSingle().Which.Should().StartWith("SystemHotkey: ctrl+shift+0 (pressed for 'ctrl+=')");
    }

    [Fact]
    public async Task ACallPausedBetweenKeys_ReportsTheKeysBeforeThePause()
    {
        _layouts.Layout = FakeKeyboardLayoutWrapper.Us;
        _input.BeforeSend = _ =>
        {
            if (_input.Batches.Count == 0)
            {
                _session.Pause("hotkey");
            }
        };

        var result = await CreateSut().SendKeysAsync("é😀", Duetty, delayMs: 1);

        result.ErrorCode.Should().Be(InputErrorCode.PausedByUser);
        result.KeysSent.Should().Be(1);
        result.SentAsCharacters.Should().Equal("é");
        result.Warnings.Should().ContainSingle().Which.Should().StartWith("SentAsCharacter: 'é' went out as a character alone");
    }

    [Fact]
    public async Task ACallPausedRightBeforeABatch_ReportsTheKeysBeforeIt()
    {
        // The pause comes after the window was checked again, so SendInput is never called for the second key
        _layouts.Layout = FakeKeyboardLayoutWrapper.Us;
        var calls = 0;
        _guard.Setup(g => g.RecheckKeyboard(It.IsAny<InputGuardResult>()))
            .Returns((InputGuardResult approved) =>
            {
                if (++calls == 2)
                {
                    _session.Pause("hotkey");
                }

                return approved;
            });

        var result = await CreateSut().SendKeysAsync("é😀", Duetty, delayMs: 1);

        result.ErrorCode.Should().Be(InputErrorCode.PausedByUser);
        result.SentAsCharacters.Should().Equal("é");
        _input.Batches.Should().ContainSingle();
    }

    [Fact]
    public async Task ACallRefusedAPasteInALaterBatch_ReportsTheKeysBeforeIt()
    {
        var pasteGuard = new Mock<IClipboardPasteGuard>();
        pasteGuard.SetupSequence(g => g.Check(It.IsAny<IReadOnlyList<InputEvent>>(), It.IsAny<nint>()))
            .Returns((string?)null)
            .Returns("The clipboard holds what the user copied.");
        var sut = new InputSimulationService(TestDataFactory.CreateMockMonitorService().Object, _input, _guard.Object, _session,
            pasteGuard: pasteGuard.Object, keyboardLayout: _layouts);

        var result = await sut.PressKeysAsync(KeyList.Of("ctrl+^ ctrl+v"), 1, 0, delayBetweenMs: 1, Duetty);

        result.ErrorCode.Should().Be(InputErrorCode.PasteNotAllowed);
        result.SentAsCharacters.Should().Equal("ctrl+^");
    }

    [Fact]
    public async Task ACallThatSendInputStoppedPartWay_ReportsTheKeysItInserted()
    {
        // SendInput inserted the down and up of 'é' and the Tab's down: the emoji never went out
        _layouts.Layout = FakeKeyboardLayoutWrapper.Us;
        _input.Insert = (events, call) => call == 0 ? 3u : (uint)events.Count;

        var result = await CreateSut().SendKeysAsync("é{TAB}😀", Duetty);

        result.ErrorCode.Should().Be(InputErrorCode.SendInputFailed);
        result.SentAsCharacters.Should().Equal("é");
        result.Warnings.Should().ContainSingle().Which.Should().StartWith("SentAsCharacter: 'é' went out as a character alone");
    }

    [Fact]
    public async Task ARefusedCall_CarriesNoReport()
    {
        _guard.Setup(g => g.CheckKeyboard(It.IsAny<InputTarget>(), KeyboardInputKind.Keys, It.IsAny<IReadOnlyList<InputEvent>?>()))
            .Returns(InputGuardResult.Refuse(InputErrorCode.TargetNotForeground, "Not in front.", Duetty));

        var result = await PressAsync("ctrl+^");

        result.Success.Should().BeFalse();
        result.SentAsCharacters.Should().BeNull();
        result.Warnings.Should().BeNull();
        _input.Batches.Should().BeEmpty();
    }
}
