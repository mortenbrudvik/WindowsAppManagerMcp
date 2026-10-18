namespace WindowsAppManagerMcp.Tests.Unit.Services;

/// <summary>
/// Windows' own keyboard hotkeys (issue #10): the input-method hotkeys and language-switch toggles read from the registry's
/// values, and the chords of a call's events that press one. Pure: the registry values are given, and nothing is sent.
/// </summary>
public class SystemKeyboardHotkeysTests
{
    private static readonly nint Chinese = 0x08040804;

    /// <summary>The owner's Ctrl+Shift+0 (issue #10): Key Modifiers 06 C0 00 00, Virtual Key 30 00 00 00.</summary>
    private static readonly InputMethodHotkey CtrlShift0 =
        InputMethodHotkey.FromRegistry("00000104", new byte[] { 0x06, 0xC0, 0x00, 0x00 }, new byte[] { 0x30, 0x00, 0x00, 0x00 })!;

    private static IReadOnlyList<InputEvent> Keys(string keys, nint layout) =>
        PressKeysParser.Parse(KeyList.Of(keys), 1, KeyboardLayout.Of(new FakeKeyboardLayoutWrapper { Layout = layout }, 0, capsLock: false)).Events;

    private static SystemKeyboardHotkeys Hotkeys(params InputMethodHotkey[] hotkeys) => new(hotkeys, []);

    [Fact]
    public void AnInputMethodHotkey_IsReadFromItsRegistryValues()
    {
        CtrlShift0.Should().Be(new InputMethodHotkey(0x104, KeyModifiers.Control | KeyModifiers.Shift, 0x30, AnyModifiers: false));
        CtrlShift0.Name.Should().Be("00000104");
        CtrlShift0.Does.Should().Contain("switches to an input language");

        // MOD_ALT, a DWORD instead of binary, and MOD_IGNORE_ALL_MODIFIER (0x400)
        InputMethodHotkey.FromRegistry("00000010", 0x8001, 0x20).Should()
            .Be(new InputMethodHotkey(0x10, KeyModifiers.Alt, 0x20, AnyModifiers: false));
        InputMethodHotkey.FromRegistry("00000070", new byte[] { 0x00, 0x04, 0x00, 0x00 }, new byte[] { 0x19 })!.AnyModifiers.Should().BeTrue();
    }

    [Theory]
    [InlineData("00000104", null)]
    [InlineData("00000104", new byte[] { 0x00, 0x00, 0x00, 0x00 })]
    [InlineData("00000104", new byte[] { 0xFF, 0x00, 0x00, 0x00 })]
    [InlineData("not hex", new byte[] { 0x30, 0x00, 0x00, 0x00 })]
    public void AHotkeyWithoutAKey_OrWithoutAnId_IsNone(string name, byte[]? virtualKey)
    {
        InputMethodHotkey.FromRegistry(name, new byte[] { 0x06, 0xC0, 0x00, 0x00 }, virtualKey).Should().BeNull();
    }

    [Fact]
    public void TheToggles_AreWindowsDefaults_WhenNotSet_AndOffAtThree()
    {
        SystemKeyboardHotkeys.TogglesFrom(keyExists: false, null, null, null).Select(t => t.Keys).Should()
            .Equal(LanguageToggleKeys.LeftAltShift, LanguageToggleKeys.CtrlShift);
        SystemKeyboardHotkeys.TogglesFrom(keyExists: true, "1", "3", "3").Should().BeEmpty("Language Hotkey wins over the older Hotkey");
        SystemKeyboardHotkeys.TogglesFrom(keyExists: true, "2", null, "3").Should().ContainSingle()
            .Which.Should().Be(new LanguageToggle("Language Hotkey", LanguageToggleKeys.CtrlShift, "the input language"));
        SystemKeyboardHotkeys.TogglesFrom(keyExists: true, null, "4", "1").Select(t => t.Keys).Should()
            .Equal(LanguageToggleKeys.GraveAccent, LanguageToggleKeys.LeftAltShift);
        SystemKeyboardHotkeys.TogglesFrom(keyExists: true, null, "x", "9").Select(t => t.Keys).Should()
            .Equal([LanguageToggleKeys.LeftAltShift], "a value that is no number counts as not set, one out of range as off");
    }

    [Fact]
    public void CtrlEquals_OnALayoutWhereEqualsNeedsShift_PressesTheCtrlShift0Hotkey()
    {
        // Issue #10: on Norwegian '=' is Shift+0, so 'ctrl+=' is Ctrl+Shift+0, which Windows takes
        var matches = Hotkeys(CtrlShift0).Match(Keys("ctrl+=", FakeKeyboardLayoutWrapper.Norwegian), FakeKeyboardLayoutWrapper.Norwegian);

        var match = matches.Should().ContainSingle().Subject;
        match.Chord.Should().Be(new KeyChord(KeyModifiers.Control | KeyModifiers.Shift, InputEventKind.VirtualKey, '0'));
        match.What.Should().Contain(@"HKCU\Control Panel\Input Method\Hot Keys\00000104");
        match.Toggle.Should().BeFalse();
        Hotkeys(CtrlShift0).Match(Keys("ctrl+=", FakeKeyboardLayoutWrapper.Us), FakeKeyboardLayoutWrapper.Us).Should()
            .BeEmpty("on a US keyboard '=' has a key of its own");
    }

    [Theory]
    [InlineData("ctrl+0")]
    [InlineData("ctrl+alt+shift+0")]
    [InlineData("shift+0")]
    [InlineData("ctrl+shift+9")]
    public void AChordWithOtherModifiersOrAnotherKey_IsNoHotkey(string keys)
    {
        Hotkeys(CtrlShift0).Match(Keys(keys, FakeKeyboardLayoutWrapper.Us), FakeKeyboardLayoutWrapper.Us).Should().BeEmpty();
    }

    [Fact]
    public void AHotkeyPressedSeveralTimes_IsOneMatch()
    {
        Hotkeys(CtrlShift0).Match(Keys("ctrl+shift+0*3 tab", FakeKeyboardLayoutWrapper.Us), FakeKeyboardLayoutWrapper.Us).Should().ContainSingle();
    }

    [Fact]
    public void AnImesHotkey_CountsOnlyOnALayoutOfItsLanguage()
    {
        // IME_CHOTKEY_IME_NONIME_TOGGLE: Ctrl+Space turns the Chinese IME on and off; on other layouts Ctrl+Space is the app's
        var ctrlSpace = InputMethodHotkey.FromRegistry("00000010", new byte[] { 0x02, 0xC0, 0x00, 0x00 }, new byte[] { 0x20, 0x00, 0x00, 0x00 })!;
        var events = Keys("ctrl+space", FakeKeyboardLayoutWrapper.Us);

        Hotkeys(ctrlSpace).Match(events, FakeKeyboardLayoutWrapper.Us).Should().BeEmpty();
        Hotkeys(ctrlSpace).Match(events, Chinese).Should().ContainSingle().Which.What.Should().Contain("input method (IME)");
        Hotkeys(ctrlSpace).Match(events, layout: 0).Should().ContainSingle("a layout that could not be read may be that IME's");
    }

    [Theory]
    [InlineData("alt+shift", LanguageToggleKeys.LeftAltShift)]
    [InlineData("shift+alt", LanguageToggleKeys.LeftAltShift)]
    [InlineData("ctrl+shift", LanguageToggleKeys.CtrlShift)]
    [InlineData("shift+ctrl", LanguageToggleKeys.CtrlShift)]
    public void ModifiersPressedAlone_ThatMakeAToggle_PressIt(string keys, LanguageToggleKeys toggle)
    {
        var hotkeys = new SystemKeyboardHotkeys([], SystemKeyboardHotkeys.TogglesFrom(keyExists: false, null, null, null));

        var match = hotkeys.Match(Keys(keys, FakeKeyboardLayoutWrapper.Us), FakeKeyboardLayoutWrapper.Us).Should().ContainSingle().Subject;

        match.Toggle.Should().BeTrue();
        match.What.Should().Contain(@"HKCU\Keyboard Layout\Toggle").And.Contain($"Hotkey {(int)toggle}");
    }

    [Theory]
    [InlineData("alt+shift+plus")]
    [InlineData("ctrl+shift+t")]
    [InlineData("ctrl+alt+shift")]
    [InlineData("alt")]
    public void ModifiersWithAKey_OrNotMakingAToggle_PressNone(string keys)
    {
        // A toggle fires only when its modifiers go up with no other key pressed between (issue #8's alt+shift+plus is no switch)
        var hotkeys = new SystemKeyboardHotkeys([], SystemKeyboardHotkeys.TogglesFrom(keyExists: false, null, null, null));

        hotkeys.Match(Keys(keys, FakeKeyboardLayoutWrapper.Us), FakeKeyboardLayoutWrapper.Us).Should().BeEmpty();
    }

    [Fact]
    public void TheGraveAccentToggle_IsTheKeyLeftOf1_Alone()
    {
        var hotkeys = new SystemKeyboardHotkeys([], SystemKeyboardHotkeys.TogglesFrom(keyExists: true, null, "4", "3"));

        hotkeys.Match(Keys("oem_3", FakeKeyboardLayoutWrapper.Us), FakeKeyboardLayoutWrapper.Us).Should().ContainSingle();
        hotkeys.Match(Keys("ctrl+oem_3", FakeKeyboardLayoutWrapper.Us), FakeKeyboardLayoutWrapper.Us).Should().BeEmpty();
    }
}
