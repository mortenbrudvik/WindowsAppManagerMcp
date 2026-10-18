namespace WindowsAppManagerMcp.Tests.Unit.Services;

/// <summary>
/// What press_keys and send_keys tell about keys that went through (issues #9 and #10): the chords as pressed on the window's
/// layout (resolvedChords), what went out as a character with no key (sentAsCharacters), and the warnings SentAsCharacter,
/// KeyboardLayoutUnknown and SystemHotkey. The native input layer is <see cref="FakeInputWrapper"/>, the layouts and Windows'
/// hotkeys come from <see cref="FakeKeyboardLayoutWrapper"/>, and the guard is a mock, so nothing reaches the desktop or the
/// registry.
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
            .And.Contain("does not get it");
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
