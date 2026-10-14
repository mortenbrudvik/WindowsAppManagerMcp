namespace WindowsAppManagerMcp.Tests.Unit.Services;

/// <summary>
/// Unit tests for send_keys' key grammar and its layout mapping (roadmap M4.1): KeySpecParser over a KeyboardLayout read
/// through the fake layout queries, which model a US, a Norwegian (AltGr) and a French AZERTY layout. Pure: nothing is sent.
/// </summary>
public class KeySpecParserTests
{
    private const uint Extended = 0x0001, KeyUp = 0x0002;
    private const ushort VkShift = 0x10, VkControl = 0x11, VkMenu = 0x12, VkRMenu = 0xA5, VkLWin = 0x5B, VkReturn = 0x0D;

    private static readonly InputEvent ShiftDown = Down(VkShift, 0x2A), ShiftUp = Up(VkShift, 0x2A);
    private static readonly InputEvent CtrlDown = Down(VkControl, 0x1D), CtrlUp = Up(VkControl, 0x1D);
    private static readonly InputEvent AltDown = Down(VkMenu, 0x38), AltUp = Up(VkMenu, 0x38);
    private static readonly InputEvent AltGrDown = Down(VkRMenu, 0x38, extended: true), AltGrUp = Up(VkRMenu, 0x38, extended: true);
    private static readonly InputEvent WinDown = Down(VkLWin, 0x5B, extended: true), WinUp = Up(VkLWin, 0x5B, extended: true);

    private static KeyboardLayout Layout(nint layout, bool capsLock = false) =>
        KeyboardLayout.Of(new FakeKeyboardLayoutWrapper { Layout = layout }, 0, capsLock);

    private static KeySpec Spec(string keys, nint layout, bool capsLock = false) => KeySpecParser.Parse(keys, Layout(layout, capsLock));

    private static IReadOnlyList<InputEvent> Us(string keys, bool capsLock = false) => Spec(keys, FakeKeyboardLayoutWrapper.Us, capsLock).Events;

    private static IReadOnlyList<InputEvent> Norwegian(string keys) => Spec(keys, FakeKeyboardLayoutWrapper.Norwegian).Events;

    private static IReadOnlyList<InputEvent> French(string keys, bool capsLock = false) =>
        Spec(keys, FakeKeyboardLayoutWrapper.French, capsLock).Events;

    private static InputEvent Down(ushort vk, ushort scan, bool extended = false) => InputEvent.Key(vk, extended ? Extended : 0, scan);

    private static InputEvent Up(ushort vk, ushort scan, bool extended = false) => InputEvent.Key(vk, (extended ? Extended : 0) | KeyUp, scan);

    private static InputEvent[] Tap(ushort vk, ushort scan, bool extended = false) => [Down(vk, scan, extended), Up(vk, scan, extended)];

    private static InputEvent[] Unicode(char c) => [InputEvent.Unicode(c, 0), InputEvent.Unicode(c, KeyUp)];

    private static InputEvent[] Seq(params IEnumerable<InputEvent>[] parts) => parts.SelectMany(p => p).ToArray();

    private static InputEvent[] One(InputEvent e) => [e];

    #region Characters on the layout

    [Fact]
    public void ACapital_IsItsKeyWithShift_SoHelloKeepsItsCase()
    {
        Us("Hello").Should().Equal(Seq(
            One(ShiftDown), Tap('H', 0x23), One(ShiftUp),
            Tap('E', 0x12), Tap('L', 0x26), Tap('L', 0x26), Tap('O', 0x18)));
    }

    [Fact]
    public void WithCapsLockOn_ShiftIsFlippedForLetters_SoTheTextStillComesOutAsWritten()
    {
        Us("Hi!", capsLock: true).Should().Equal(Seq(
            Tap('H', 0x23),
            One(ShiftDown), Tap('I', 0x17), One(ShiftUp),
            One(ShiftDown), Tap('1', 0x02), One(ShiftUp)));
    }

    [Fact]
    public void Punctuation_IsItsKeyOnTheLayout_NotUnicode()
    {
        Us("a,-/").Should().Equal(Seq(Tap('A', 0x1E), Tap(0xBC, 0x33), Tap(0xBD, 0x0C), Tap(0xBF, 0x35)));
    }

    [Fact]
    public void CtrlWithPunctuation_IsAChordOfTheKey_SoTheShortcutFires()
    {
        // Ctrl+- zooms out, Alt+. is a menu accelerator: both need the key, which a Unicode character is not
        Us("^-").Should().Equal(Seq(One(CtrlDown), Tap(0xBD, 0x0C), One(CtrlUp)));
        Us("%.").Should().Equal(Seq(One(AltDown), Tap(0xBE, 0x34), One(AltUp)));
        Us("^{+}").Should().Equal(Seq(One(CtrlDown), One(ShiftDown), Tap(0xBB, 0x0D), One(ShiftUp), One(CtrlUp)));
    }

    [Fact]
    public void AzertyDigits_NeedShift_AndTheSymbolsDoNot()
    {
        French("1&a").Should().Equal(Seq(One(ShiftDown), Tap('1', 0x02), One(ShiftUp), Tap('1', 0x02), Tap('A', 0x10)));
    }

    [Fact]
    public void AzertyDigits_WithCapsLockOn_NeedNoShift_BecauseCapsLockFlipsTheDigitRow()
    {
        French("1", capsLock: true).Should().Equal(Tap('1', 0x02));
    }

    [Fact]
    public void AnAltGrCharacter_IsTheLeftCtrlAndTheRightAltWithItsKey()
    {
        // Norwegian: @ is AltGr+2, { AltGr+7, $ AltGr+4
        Norwegian("@{{}").Should().Equal(Seq(
            One(CtrlDown), One(AltGrDown), Tap('2', 0x03), One(AltGrUp), One(CtrlUp),
            One(CtrlDown), One(AltGrDown), Tap('7', 0x08), One(AltGrUp), One(CtrlUp)));
    }

    [Fact]
    public void ALetterOfTheLayout_IsItsOwnKey()
    {
        Norwegian("æ\\").Should().Equal(Seq(Tap(0xDE, 0x28), Tap(0xDB, 0x0D)));
    }

    [Fact]
    public void ACharacterThatOnlyADeadKeyTypes_IsUnicode_SoItDoesNotWaitForTheNextKey()
    {
        // Norwegian ~ ^ ` ´ and ¨ are dead keys: sent as keys they would turn the next 'a' into ã, â, à, á or ä
        Norwegian("{~}{^}`´¨a").Should().Equal(Seq(Unicode('~'), Unicode('^'), Unicode('`'), Unicode('´'), Unicode('¨'), Tap('A', 0x1E)));
        Norwegian("^{^}").Should().Equal(Seq(One(CtrlDown), Unicode('^'), One(CtrlUp)));
    }

    [Fact]
    public void ACharacterNotOnTheLayout_IsUnicode()
    {
        Us("€é").Should().Equal(Seq(Unicode('€'), Unicode('é')));
        Norwegian("€").Should().Equal(Seq(One(CtrlDown), One(AltGrDown), Tap('5', 0x06), One(AltGrUp), One(CtrlUp)));
    }

    [Fact]
    public void ALetterOffTheLayout_WithCtrlHeld_IsItsLatinKey_SoShortcutsWorkOnAnyLayout()
    {
        // A layout without Latin letters (Cyrillic, Greek): ^c must still be Ctrl+C, while a plain c is the character
        var layout = KeyboardLayout.Of(new FakeKeyboardLayoutWrapper { Layout = FakeKeyboardLayoutWrapper.Empty }, 0, capsLock: false);

        KeySpecParser.Parse("^c", layout).Events.Should().Equal(Seq(One(CtrlDown), Tap('C', 0), One(CtrlUp)));
        KeySpecParser.Parse("c", layout).Events.Should().Equal(Unicode('c'));
    }

    [Fact]
    public void WithoutALayout_LettersAndDigitsAreKeys_AndEverythingElseUnicode_WithoutScanCodes()
    {
        KeySpecParser.Parse("Ab1-", KeyboardLayout.Invariant).Events.Should().Equal(Seq(
            One(InputEvent.Key(VkShift, 0)), Tap('A', 0), One(InputEvent.Key(VkShift, KeyUp)), Tap('B', 0), Tap('1', 0), Unicode('-')));
    }

    [Fact]
    public void ControlCharacters_AreTheirKeys_AndAWindowsLineBreakIsOneEnter()
    {
        Us("a\r\nb\tc\n").Should().Equal(Seq(
            Tap('A', 0x1E), Tap(VkReturn, 0x1C), Tap('B', 0x30), Tap(0x09, 0x0F), Tap('C', 0x2E), Tap(VkReturn, 0x1C)));
    }

    [Fact]
    public void AnEmoji_IsBothHalvesAsUnicode()
    {
        Us("\U0001F600").Should().Equal(Seq(Unicode('\uD83D'), Unicode('\uDE00')));
    }

    #endregion

    #region Modifiers, groups and escapes

    [Fact]
    public void Hash_IsTheWindowsKey()
    {
        Us("#r").Should().Equal(Seq(One(WinDown), Tap('R', 0x13), One(WinUp)));
    }

    [Fact]
    public void Tilde_IsEnter()
    {
        Us("~").Should().Equal(Tap(VkReturn, 0x1C));
    }

    [Theory]
    [InlineData("{+}", '+')]
    [InlineData("{^}", '^')]
    [InlineData("{%}", '%')]
    [InlineData("{~}", '~')]
    [InlineData("{#}", '#')]
    [InlineData("{(}", '(')]
    [InlineData("{)}", ')')]
    [InlineData("{{}", '{')]
    [InlineData("{}}", '}')]
    public void ABracedCharacter_TypesThatCharacter(string keys, char expected)
    {
        // Each of them is a shifted key on the US layout
        var fake = new FakeKeyboardLayoutWrapper();
        var scan = fake.VkKeyScan(expected, FakeKeyboardLayoutWrapper.Us);
        var vk = (ushort)(scan & 0xFF);

        (scan >> 8).Should().Be(1);
        Us(keys).Should().Equal(Seq(One(ShiftDown), Tap(vk, (ushort)fake.ScanCode(vk, FakeKeyboardLayoutWrapper.Us)), One(ShiftUp)));
    }

    [Fact]
    public void HundredPercent_IsWrittenWithTheEscape()
    {
        // '100%{ENTER}' is Alt+Enter, as % always holds Alt: the literal is {%}
        Us("100{%}{ENTER}").Should().Equal(Seq(
            Tap('1', 0x02), Tap('0', 0x0B), Tap('0', 0x0B), One(ShiftDown), Tap('5', 0x06), One(ShiftUp), Tap(VkReturn, 0x1C)));
        Us("100%{ENTER}").Should().EndWith(Seq(One(AltDown), Tap(VkReturn, 0x1C), One(AltUp)));
    }

    [Fact]
    public void AGroup_HoldsItsModifiersForEveryKeyInIt_AndShiftIsNotPressedTwice()
    {
        Us("+(ab)c").Should().Equal(Seq(One(ShiftDown), Tap('A', 0x1E), Tap('B', 0x30), One(ShiftUp), Tap('C', 0x2E)));
        Us("+(A)").Should().Equal(Seq(One(ShiftDown), Tap('A', 0x1E), One(ShiftUp)));
    }

    [Fact]
    public void Groups_Nest()
    {
        Us("^(a+(b))").Should().Equal(Seq(
            One(CtrlDown), Tap('A', 0x1E), One(ShiftDown), Tap('B', 0x30), One(ShiftUp), One(CtrlUp)));
    }

    [Fact]
    public void ACapitalInAChord_KeepsItsShift_AndCapsLockDoesNotChangeAShortcut()
    {
        Us("^A").Should().Equal(Seq(One(CtrlDown), One(ShiftDown), Tap('A', 0x1E), One(ShiftUp), One(CtrlUp)));
        Us("^a", capsLock: true).Should().Equal(Seq(One(CtrlDown), Tap('A', 0x1E), One(CtrlUp)));
    }

    #endregion

    #region Named keys, repeats, down and up

    [Theory]
    [InlineData("{NUMPAD0}", 0x60)]
    [InlineData("{numpad5}", 0x65)]
    [InlineData("{ADD}", 0x6B)]
    [InlineData("{SUBTRACT}", 0x6D)]
    [InlineData("{MULTIPLY}", 0x6A)]
    [InlineData("{DIVIDE}", 0x6F)]
    [InlineData("{F13}", 0x7C)]
    [InlineData("{F24}", 0x87)]
    [InlineData("{VK_OEM_1}", 0xBA)]
    [InlineData("{OEM_102}", 0xE2)]
    [InlineData("{VOLUME_MUTE}", 0xAD)]
    [InlineData("{MEDIA_PLAY_PAUSE}", 0xB3)]
    [InlineData("{BROWSER_BACK}", 0xA6)]
    [InlineData("{LAUNCH_APP2}", 0xB7)]
    [InlineData("{RALT}", 0xA5)]
    [InlineData("{PGDN}", 0x22)]
    public void NamedKeys_AreTheirVirtualKeys(string keys, int vk)
    {
        Us(keys).Select(e => (int)e.Code).Should().Equal(vk, vk);
    }

    [Fact]
    public void ExtendedKeys_CarryTheFlag_FromTheLayoutsE0ScanCode()
    {
        Us("{UP}{DIVIDE}{VOLUME_MUTE}").Should().Equal(Seq(Tap(0x26, 0x48, true), Tap(0x6F, 0x35, true), Tap(0xAD, 0x20, true)));
        Us("{PAUSE}").Should().Equal(Tap(0x13, 0), "Pause's E1 sequence has no single scan code");
    }

    [Fact]
    public void ARepeat_PressesTheKeyThatOften_EachAStepOfItsOwn()
    {
        var spec = Spec("{LEFT 5}x", FakeKeyboardLayoutWrapper.Us);

        spec.Events.Should().Equal(Seq(Enumerable.Repeat(Tap(0x25, 0x4B, true), 5).SelectMany(t => t).ToArray(), Tap('X', 0x2D)));
        spec.Steps.Should().HaveCount(6);
    }

    [Fact]
    public void ARepeat_OfACharacter_TypesItThatOften()
    {
        Us("{a 3}").Should().Equal(Seq(Tap('A', 0x1E), Tap('A', 0x1E), Tap('A', 0x1E)));
        Us("{+ 2}").Should().HaveCount(8);
        Us("{} 2}").Should().HaveCount(8);
    }

    [Fact]
    public void Steps_AreTheTopLevelKeys_ChordsAndGroups()
    {
        Spec("ab^c+(de){TAB}", FakeKeyboardLayoutWrapper.Us).Steps.Should().HaveCount(5);
    }

    [Fact]
    public void KeyDownAndUp_HoldAKeyAcrossOthers()
    {
        Us("{SHIFT down}{RIGHT 2}{SHIFT up}").Should().Equal(Seq(
            One(ShiftDown), Tap(0x27, 0x4D, true), Tap(0x27, 0x4D, true), One(ShiftUp)));
    }

    [Fact]
    public void AKeyLeftDown_IsReleasedAtTheEndOfTheLastStep()
    {
        var spec = Spec("{CTRL down}{ALT down}x", FakeKeyboardLayoutWrapper.Us);

        spec.Events.Should().Equal(Seq(One(CtrlDown), One(AltDown), Tap('X', 0x2D), One(AltUp), One(CtrlUp)));
        spec.Steps.Last().Should().EndWith(Seq(One(AltUp), One(CtrlUp)));
    }

    [Fact]
    public void ACharacterTypedWhileAModifierIsHeld_GetsNoCapsLockCorrection()
    {
        // {SHIFT down} is the agent's explicit Shift: 'a' under it is Shift+A, whatever Caps Lock says
        Us("{SHIFT down}a{SHIFT up}", capsLock: true).Should().Equal(Seq(One(ShiftDown), Tap('A', 0x1E), One(ShiftUp)));
    }

    [Fact]
    public void AChordHeldWithKeyDown_IsSeenByTheChordChecks()
    {
        SendKeysParser.Chords(Us("{CTRL down}w{CTRL up}")).Should().Equal(
            new KeyChord(KeyModifiers.Control, InputEventKind.VirtualKey, 'W'));
    }

    #endregion

    #region Errors

    [Theory]
    [InlineData("{ENTER", "is not closed")]
    [InlineData("abc^", "has no key after it")]
    [InlineData("100%", "has no key after it")]
    [InlineData("+(ab^)", "has no key after it")]
    [InlineData("(ab", "is not closed")]
    [InlineData("ab)", "closes no group")]
    [InlineData("a}", "closes no brace")]
    [InlineData("+()", "is empty")]
    [InlineData("{LEFT 0}", "a count from 1 to 100")]
    [InlineData("{LEFT 101}", "a count from 1 to 100")]
    [InlineData("{LEFT five}", "a count from 1 to 100")]
    [InlineData("{a down}", "take a key name")]
    [InlineData("^{SHIFT down}", "Hold modifiers with their own")]
    [InlineData("{SHIFT up}", "has no {SHIFT down}")]
    [InlineData("{SHIFT down}{SHIFT down}", "comes twice")]
    [InlineData("{}abc}", "are empty")]
    public void ASyntaxError_IsAKeySyntaxException(string keys, string message)
    {
        var act = () => Us(keys);

        act.Should().Throw<KeySyntaxException>().WithMessage($"*{message}*");
    }

    [Fact]
    public void AnUnknownName_IsAnUnknownKey_NotASyntaxError()
    {
        var act = () => Us("{NOPE}");

        act.Should().Throw<ArgumentException>().WithMessage("Unknown key: NOPE").Which.Should().NotBeOfType<KeySyntaxException>();
    }

    [Fact]
    public void TheSleepKey_HasNoName()
    {
        var act = () => Us("{SLEEP}");

        act.Should().Throw<ArgumentException>().WithMessage("Unknown key: SLEEP");
    }

    #endregion
}
