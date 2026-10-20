namespace WindowsAppManagerMcp.Tests.Unit.Services;

/// <summary>
/// press_keys' structured chords (roadmap M4.3): 'ctrl+shift+t', ['ctrl','shift','t'] and sequences such as 'tab*3', read
/// with M4.1's key table and mapped through the keyboard layout (the fake US, Norwegian and French layouts), each press one
/// step. Pure: nothing is sent.
/// </summary>
public class PressKeysParserTests
{
    private const uint Extended = 0x0001, KeyUp = 0x0002;
    private const ushort VkShift = 0x10, VkControl = 0x11, VkMenu = 0x12, VkRMenu = 0xA5, VkLWin = 0x5B;
    private const ushort VkTab = 0x09, VkDelete = 0x2E, VkF4 = 0x73;

    private static readonly InputEvent CtrlDown = Down(VkControl, 0x1D), CtrlUp = Up(VkControl, 0x1D);
    private static readonly InputEvent ShiftDown = Down(VkShift, 0x2A), ShiftUp = Up(VkShift, 0x2A);
    private static readonly InputEvent AltDown = Down(VkMenu, 0x38), AltUp = Up(VkMenu, 0x38);
    private static readonly InputEvent WinDown = Down(VkLWin, 0x5B, extended: true), WinUp = Up(VkLWin, 0x5B, extended: true);

    private static KeyboardLayout Layout(nint layout) => KeyboardLayout.Of(new FakeKeyboardLayoutWrapper { Layout = layout }, 0, capsLock: false);

    /// <summary><paramref name="layout"/> while a grave accent typed on a dead key waits on it (issue #8).</summary>
    private static KeyboardLayout AccentWaiting(nint layout) =>
        KeyboardLayout.Of(new FakeKeyboardLayoutWrapper { Layout = layout, PendingDeadKey = '`' }, 0, capsLock: false);

    /// <summary>The resolved chords as the result reports them: the chord asked, to the keys pressed.</summary>
    private static Dictionary<string, string>? Resolved(KeySpec spec) => spec.ResolvedChords?.ToDictionary(r => r.Asked, r => r.Pressed);

    private static KeySpec Us(string keys, int repeat = 1) => PressKeysParser.Parse(KeyList.Of(keys), repeat, Layout(FakeKeyboardLayoutWrapper.Us));

    private static KeySpec Us(string[] keys, int repeat = 1) => PressKeysParser.Parse(KeyList.Of(keys), repeat, Layout(FakeKeyboardLayoutWrapper.Us));

    private static InputEvent Down(ushort vk, ushort scan, bool extended = false) => InputEvent.Key(vk, extended ? Extended : 0, scan);

    private static InputEvent Up(ushort vk, ushort scan, bool extended = false) => InputEvent.Key(vk, (extended ? Extended : 0) | KeyUp, scan);

    private static InputEvent[] Tap(ushort vk, ushort scan, bool extended = false) => [Down(vk, scan, extended), Up(vk, scan, extended)];

    private static InputEvent[] Seq(params IEnumerable<InputEvent>[] parts) => parts.SelectMany(p => p).ToArray();

    private static InputEvent[] One(InputEvent e) => [e];

    private static InputEvent AltGr(InputEvent e) => e with { AltGr = true };

    [Fact]
    public void AChord_PressesItsModifiersInOrder_ThenTheKey_ThenReleasesLastPressedFirst()
    {
        var spec = Us("ctrl+shift+t");

        spec.Steps.Should().ContainSingle();
        spec.Events.Should().Equal(Seq(One(CtrlDown), One(ShiftDown), Tap('T', 0x14), One(ShiftUp), One(CtrlUp)));
    }

    [Fact]
    public void AnArrayOfModifiersAndOneKey_IsTheSameChord()
    {
        Us(["ctrl", "shift", "t"]).Events.Should().Equal(Us("ctrl+shift+t").Events);
    }

    [Fact]
    public void NamesIgnoreCase_AndALetterIsItsKey_WithoutAShiftOfItsOwn()
    {
        Us("Ctrl+T").Events.Should().Equal(Us("ctrl+t").Events);
        Us("CTRL+t").Events.Should().Equal(Seq(One(CtrlDown), Tap('T', 0x14), One(CtrlUp)));
    }

    [Fact]
    public void ARepeatedChord_IsOneStepPerPress()
    {
        var spec = Us("tab*3");

        spec.Steps.Should().HaveCount(3);
        spec.Steps.Should().OnlyContain(step => step.SequenceEqual(Tap(VkTab, 0x0F, false)));
    }

    [Fact]
    public void ASequence_IsSeparatedBySpacesOrCommas()
    {
        var spec = Us("ctrl+a, delete");

        spec.Steps.Should().HaveCount(2);
        spec.Steps[0].Should().Equal(Seq(One(CtrlDown), Tap('A', 0x1E), One(CtrlUp)));
        spec.Steps[1].Should().Equal(Tap(VkDelete, 0x53, extended: true));
        Us("ctrl+a delete").Events.Should().Equal(spec.Events);
    }

    [Fact]
    public void AnArrayOfChords_IsASequence()
    {
        // Not every item but the last is a modifier, or an item holds '+': each item is a chord
        Us(["ctrl+a", "delete"]).Steps.Should().HaveCount(2);
        Us(["tab", "tab"]).Steps.Should().HaveCount(2);
        Us(["down*2", "enter"]).Steps.Should().HaveCount(3);
    }

    [Fact]
    public void Repeat_RepeatsTheWholeSequence()
    {
        var spec = Us("ctrl+a delete", repeat: 2);

        spec.Steps.Should().HaveCount(4);
        spec.Steps[2].Should().Equal(spec.Steps[0]);
    }

    [Fact]
    public void ACharacterThatNeedsShift_GetsIt_WithTheModifiersAsked()
    {
        // Ctrl+? is Ctrl+Shift+/ on a US keyboard; '+' is written 'plus', since '+' joins a chord
        Us("ctrl+?").Events.Should().Equal(Seq(One(CtrlDown), One(ShiftDown), Tap(0xBF, 0x35), One(ShiftUp), One(CtrlUp)));
        Us("ctrl+plus").Events.Should().Equal(Seq(One(CtrlDown), One(ShiftDown), Tap(0xBB, 0x0D), One(ShiftUp), One(CtrlUp)));
        Us("comma").Events.Should().Equal(Tap(0xBC, 0x33));
    }

    [Fact]
    public void ACharacterBehindAltGr_IsTheLayoutsKey_WithTheLeftCtrlAndTheRightAlt()
    {
        var spec = PressKeysParser.Parse(KeyList.Of("@"), 1, Layout(FakeKeyboardLayoutWrapper.Norwegian));

        // Tagged as AltGr, so the chord checks read the pair as the character
        spec.Events.Should().Equal(Seq(One(AltGr(Down(0xA2, 0x1D))), One(AltGr(Down(VkRMenu, 0x38, extended: true))), Tap('2', 0x03),
            One(AltGr(Up(VkRMenu, 0x38, extended: true))), One(AltGr(Up(0xA2, 0x1D)))));
    }

    [Theory]
    [InlineData("alt+shift+plus")]
    [InlineData("alt+shift+?")]
    public void ACharacterTheLayoutTypes_IsItsKey_WhileAnAccentWaitsOnTheLayout(string keys)
    {
        // Issue #8: Alt+Shift+\ on a Norwegian keyboard leaves a grave accent waiting (a dead key) in the layout every thread
        // shares; the '+' key then types "`+", and these chords went out as the character with no key (KeyboardEvent.code
        // empty). '+' is the OEM_PLUS key at the US Minus position (scan 0C), '?' that key with Shift
        var spec = PressKeysParser.Parse(KeyList.Of(keys), 1, AccentWaiting(FakeKeyboardLayoutWrapper.Norwegian));

        spec.Events.Should().Equal(Seq(One(AltDown), One(ShiftDown), Tap(0xBB, 0x0C), One(ShiftUp), One(AltUp)));
        spec.SentAsCharacters.Should().BeNull();
    }

    [Theory]
    [InlineData("plus", new ushort[0])]
    [InlineData("shift+plus", new ushort[] { VkShift })]
    public void WhileAnAccentWaits_AChordWithoutCtrlAltOrWin_StaysItsCharacter(string keys, ushort[] modifiers)
    {
        // Such a chord types, and its key would type the waiting accent too ("`+"), so it stays exact as send_keys' text does
        var spec = PressKeysParser.Parse(KeyList.Of(keys), 1, AccentWaiting(FakeKeyboardLayoutWrapper.Norwegian));

        spec.Events.Where(e => e.Kind == InputEventKind.VirtualKey).Select(e => e.Code).Distinct().Should().Equal(modifiers);
        spec.Events.Should().Contain(InputEvent.Unicode('+', 0));
        spec.SentAsCharacters.Should().Equal(new CharacterWithoutKey(keys, "+", NoKeyReason.AccentWaiting, Event: modifiers.Length));
    }

    [Fact]
    public void APunctuationKey_IsNamedByItsVirtualKey_AndPressedWithTheScanCodeTheLayoutGivesIt()
    {
        // Issue #8: oem_plus is VK_OEM_PLUS, which each layout puts on its own physical key: the = key on US (scan 0D), the +
        // key right of 0 on Norwegian (scan 0C, KeyboardEvent.code Minus), the + key right of U-umlaut on German (scan 1B)
        var norwegian = PressKeysParser.Parse(KeyList.Of("alt+shift+oem_plus"), 1, Layout(FakeKeyboardLayoutWrapper.Norwegian));

        norwegian.Events.Should().Equal(Seq(One(AltDown), One(ShiftDown), Tap(0xBB, 0x0C), One(ShiftUp), One(AltUp)));
        Us("alt+shift+oem_plus").Events.Should().Equal(Seq(One(AltDown), One(ShiftDown), Tap(0xBB, 0x0D), One(ShiftUp), One(AltUp)));
        PressKeysParser.Parse(KeyList.Of("oem_plus"), 1, Layout(FakeKeyboardLayoutWrapper.German)).Events.Should().Equal(Tap(0xBB, 0x1B));
        Us("ctrl+oem_2").Events.Should().Equal(Seq(One(CtrlDown), Tap(0xBF, 0x35), One(CtrlUp)));
        Us("OEM_1").Events.Should().Equal(Us("oem1").Events).And.Equal(Tap(0xBA, 0x27));
        norwegian.ResolvedChords.Should().BeNull("a key named by its virtual key is pressed as written");
        norwegian.SentAsCharacters.Should().BeNull();
    }

    [Fact]
    public void AChordWhoseCharacterNeedsAShiftOrAltGrItDoesNotName_IsReportedAsPressed()
    {
        // Issue #10: on Norwegian '=' is Shift+0, so 'ctrl+=' is Ctrl+Shift+0; '@' is AltGr+2, the left Ctrl and the right Alt,
        // so 'ctrl+@' adds only the right Alt. On US '+' is Shift and the = key
        var norwegian = PressKeysParser.Parse(KeyList.Of("ctrl+= ctrl+=*2 ctrl+@ @ ctrl+plus tab"), 1, Layout(FakeKeyboardLayoutWrapper.Norwegian));

        Resolved(norwegian).Should().Equal(new Dictionary<string, string>
        {
            ["ctrl+="] = "ctrl+shift+0", ["ctrl+@"] = "ctrl+ralt+2", ["@"] = "lctrl+ralt+2"
        });
        norwegian.ResolvedChords!.Select(r => r.Chord).Should().Equal(
            new KeyChord(KeyModifiers.Control | KeyModifiers.Shift, InputEventKind.VirtualKey, '0'),
            new KeyChord(KeyModifiers.Control | KeyModifiers.Alt, InputEventKind.VirtualKey, '2'),
            new KeyChord(KeyModifiers.Control | KeyModifiers.Alt, InputEventKind.VirtualKey, '2'));
        norwegian.ResolvedChords!.Select(r => r.Event).Should().Equal([2, 20, 26],
            "each is reported at its first press's key-down, so a call that stops part way reports only what went out");
        norwegian.SentAsCharacters.Should().BeNull();
        Resolved(Us("ctrl+plus")).Should().Equal(new Dictionary<string, string> { ["ctrl+plus"] = "ctrl+shift+oem_plus" });
        Us("shift+? ctrl+t ctrl+1 comma").ResolvedChords.Should().BeNull("Shift is named, and a letter, digit or comma needs none");
    }

    [Fact]
    public void AResolvedChord_NamesOnlyTheKeysThatWentDown()
    {
        // 'ctrl+ralt+@' holds both keys of AltGr already: nothing is added, but '@' is the 2 key, so the chord is Ctrl+RAlt+2
        var spec = PressKeysParser.Parse(KeyList.Of("ctrl+ralt+@"), 1, Layout(FakeKeyboardLayoutWrapper.Norwegian));

        spec.Events.Should().Equal(Seq(One(CtrlDown), One(Down(VkRMenu, 0x38, extended: true)), Tap('2', 0x03),
            One(Up(VkRMenu, 0x38, extended: true)), One(CtrlUp)));
        Resolved(spec).Should().Equal(new Dictionary<string, string> { ["ctrl+ralt+@"] = "ctrl+ralt+2" });
    }

    [Fact]
    public void AChordWhoseCharacterIsALetterOrDigitKey_IsReportedAsPressed()
    {
        // On French AZERTY '-' and 'é' are the unshifted 6 and 2 keys, so 'ctrl+-' (zoom out) is the shortcut Ctrl+6; '=' and
        // '!' are punctuation keys of their own, pressed as asked
        var spec = PressKeysParser.Parse(KeyList.Of("ctrl+- ctrl+é ctrl+= ctrl+!"), 1, Layout(FakeKeyboardLayoutWrapper.French));

        spec.Steps[0].Should().Equal(Seq(One(CtrlDown), Tap('6', 0x07), One(CtrlUp)));
        Resolved(spec).Should().Equal(new Dictionary<string, string> { ["ctrl+-"] = "ctrl+6", ["ctrl+é"] = "ctrl+2" });
        spec.ResolvedChords![0].Chord.Should().Be(new KeyChord(KeyModifiers.Control, InputEventKind.VirtualKey, '6'));
    }

    [Fact]
    public void AChordWhoseCharacterNoKeyTypes_IsReportedAsSentAsTheCharacter()
    {
        // '^' is only a dead key on Norwegian, and no key types 'é': both go as Unicode, with no key (issue #9)
        var spec = PressKeysParser.Parse(KeyList.Of("ctrl+^ ctrl+^ alt+é ctrl+plus"), 1, Layout(FakeKeyboardLayoutWrapper.Norwegian));

        // Each with the index of its first character's key-down in the events, after the 4 of each Ctrl+^ press
        spec.SentAsCharacters.Should().Equal(new CharacterWithoutKey("ctrl+^", "^", NoKeyReason.NotOnLayout, Event: 1),
            new CharacterWithoutKey("alt+é", "é", NoKeyReason.NotOnLayout, Event: 9));
        spec.Events.Should().Contain(InputEvent.Unicode('^', 0));
    }

    [Fact]
    public void WithoutAReadableLayout_EveryCharacterButALetterOrDigitIsSentAsTheCharacter()
    {
        // GetKeyboardLayout gave 0 for the window: the invariant keys, no scan codes (issue #9's other cause)
        var layout = KeyboardLayout.Of(new FakeKeyboardLayoutWrapper { Layout = 0 }, 0x5150, capsLock: false);

        var spec = PressKeysParser.Parse(KeyList.Of("alt+shift+plus ctrl+1"), 1, layout);

        layout.ReadFailed.Should().BeTrue();
        KeyboardLayout.Invariant.ReadFailed.Should().BeFalse("the invariant layout is used on purpose where no window applies");
        spec.SentAsCharacters.Should().Equal(new CharacterWithoutKey("alt+shift+plus", "+", NoKeyReason.LayoutUnread, Event: 2));
    }

    [Fact]
    public void AChordThatPressesADeadKey_IsReportedWithItsAccent()
    {
        // Issue #8's trigger: on Norwegian the key right of + types '\', with Shift the dead grave accent and with AltGr the dead
        // acute; the ¨ key is dead unshifted. Alt without Ctrl does not change what a key types, so alt+shift+\ is the dead
        // grave and alt+\ is '\'; Ctrl+Shift types nothing on it
        var spec = PressKeysParser.Parse(KeyList.Of(@"alt+shift+\ shift+\ ctrl+alt+\ oem_1 alt+\ ctrl+shift+\ alt+shift+\ tab"), 1,
            Layout(FakeKeyboardLayoutWrapper.Norwegian));

        // Each chord once, at its key-down: 6 events for Alt, Shift and the key, 4 for Shift and the key, 6 for Ctrl, Alt and it
        spec.DeadKeys.Should().Equal(new DeadKeyPress(@"alt+shift+\", "`", Event: 2), new DeadKeyPress(@"shift+\", "`", Event: 7),
            new DeadKeyPress(@"ctrl+alt+\", "´", Event: 12), new DeadKeyPress("oem_1", "¨", Event: 16));
        spec.Steps[0].Should().Equal(Seq(One(AltDown), One(ShiftDown), Tap(0xDB, 0x0D), One(ShiftUp), One(AltUp)));
    }

    [Fact]
    public void ADigitKeyThatIsADeadKeyWithShift_IsReported_AndNotWithCtrl()
    {
        // Brazilian ABNT2: Shift+6 is the dead diaeresis
        var spec = PressKeysParser.Parse(KeyList.Of("shift+6 alt+shift+6 ctrl+shift+6 6"), 1, Layout(FakeKeyboardLayoutWrapper.Brazilian));

        spec.DeadKeys!.Select(d => (d.Keys, d.Accent)).Should().Equal(("shift+6", "¨"), ("alt+shift+6", "¨"));
    }

    [Fact]
    public void ADeadKey_IsNotReported_WhileAnAccentWaits_OrWithoutAReadableLayout()
    {
        // With an accent waiting the dead key types it and its own ("``"), so nothing is left waiting; an unread layout cannot tell
        PressKeysParser.Parse(KeyList.Of(@"alt+shift+\ oem_1"), 1, AccentWaiting(FakeKeyboardLayoutWrapper.Norwegian)).DeadKeys.Should().BeNull();
        PressKeysParser.Parse(KeyList.Of(@"alt+shift+oem_4 oem_1"), 1, KeyboardLayout.Of(new FakeKeyboardLayoutWrapper { Layout = 0 }, 1, capsLock: false))
            .DeadKeys.Should().BeNull();
        Us(@"alt+shift+\ oem_1 shift+6").DeadKeys.Should().BeNull("US has no dead keys");
    }

    [Fact]
    public void WhyNoKey_BlamesTheWaitingAccentOnlyForACharacterItKeptFromItsKey()
    {
        var waiting = AccentWaiting(FakeKeyboardLayoutWrapper.Norwegian);

        waiting.WhyNoKey('+').Should().Be(NoKeyReason.AccentWaiting, "the + key types \"`+\" now");
        waiting.WhyNoKey('é').Should().Be(NoKeyReason.NotOnLayout, "no key types it, accent or not");
        Layout(FakeKeyboardLayoutWrapper.Norwegian).WhyNoKey('^').Should().Be(NoKeyReason.NotOnLayout, "only a dead key types it");
        KeyboardLayout.Of(new FakeKeyboardLayoutWrapper { Layout = 0 }, 1, capsLock: false).WhyNoKey('+').Should().Be(NoKeyReason.LayoutUnread);
    }

    [Fact]
    public void WhyNoKey_BlamesTheWaitingAccent_AlsoForACharacterItCombinesWith()
    {
        // The E key types 'è' while a grave accent waits: it is the key for 'e', and only the accent kept 'e' from it
        var waiting = AccentWaiting(FakeKeyboardLayoutWrapper.Norwegian);

        waiting.Map('e', capsLock: false).Should().BeNull("the key would type 'è'");
        waiting.WhyNoKey('e').Should().Be(NoKeyReason.AccentWaiting);
        waiting.WhyNoKey('h').Should().Be(NoKeyReason.AccentWaiting, "the H key types \"`h\" now");
        waiting.WhyNoKey('^').Should().Be(NoKeyReason.AccentWaiting,
            "the dead ^ key types \"`^\" now, as a key the accent does not combine with does: the two cannot be told apart, so " +
            "the warning says only what the key would have typed");
    }

    [Fact]
    public void ACharacterOnTheBrazilianAbntKey_IsPressedOnIt_AndNamedAsPressKeysTakesIt()
    {
        // Issue #10 review: on Brazilian ABNT2 '/' and '?' are VK_ABNT_C1 (0xC1, scan 73), which has no OEM name; 'ctrl+?'
        // adds Shift, and the key is written abnt_c1, which press_keys takes back
        var brazilian = Layout(FakeKeyboardLayoutWrapper.Brazilian);

        var spec = PressKeysParser.Parse(KeyList.Of("ctrl+/ ctrl+?"), 1, brazilian);

        spec.Steps[0].Should().Equal(Seq(One(CtrlDown), Tap(0xC1, 0x73), One(CtrlUp)));
        Resolved(spec).Should().Equal(new Dictionary<string, string> { ["ctrl+?"] = "ctrl+shift+abnt_c1" },
            "'/' is the key's own character, pressed as asked");
        PressKeysParser.Parse(KeyList.Of("ctrl+shift+abnt_c1"), 1, brazilian).Events.Should().Equal(spec.Steps[1]);
    }

    [Fact]
    public void ACharacterOnAKeyWithoutAName_IsReportedOnlyWhenAModifierWasAdded()
    {
        // A key the key table has no name for (here VK 0xE1, an OEM-specific key) is a punctuation key of the layout: as asked
        // when nothing is added, and written vk_0xE1 when a Shift is
        var native = new Mock<INativeKeyboardLayoutWrapper>();
        native.Setup(n => n.GetKeyboardLayout(It.IsAny<nint>())).Returns(1);
        native.Setup(n => n.VkKeyScan('¤', 1)).Returns(0x00E1);
        native.Setup(n => n.VkKeyScan('¦', 1)).Returns(0x01E1);
        native.Setup(n => n.ScanCode(0xE1, 1)).Returns(0x70);
        native.Setup(n => n.KeyText(0xE1, 0x70, 0, false, 1)).Returns("¤");
        native.Setup(n => n.KeyText(0xE1, 0x70, 1, false, 1)).Returns("¦");

        var spec = PressKeysParser.Parse(KeyList.Of("ctrl+¤ ctrl+¦"), 1, KeyboardLayout.Of(native.Object, 0, capsLock: false));

        spec.Steps[0].Should().Equal(Seq(One(Down(VkControl, 0)), Tap(0xE1, 0x70), One(Up(VkControl, 0))));
        Resolved(spec).Should().Equal(new Dictionary<string, string> { ["ctrl+¦"] = "ctrl+shift+vk_0xE1" });
    }

    [Theory]
    [InlineData((ushort)0xBB, "oem_plus")]
    [InlineData((ushort)0xBA, "oem_1")]
    [InlineData((ushort)0xE2, "oem_102")]
    [InlineData((ushort)0xC1, "abnt_c1")]
    [InlineData((ushort)0xC2, "abnt_c2")]
    [InlineData((ushort)'T', "t")]
    [InlineData((ushort)'0', "0")]
    [InlineData((ushort)0x0D, "enter")]
    [InlineData((ushort)0x5B, "win")]
    [InlineData((ushort)0x11, "ctrl")]
    public void KeyName_IsTheNamePressKeysTakes(ushort virtualKey, string name)
    {
        PressKeysParser.KeyName(virtualKey).Should().Be(name);
        PressKeysParser.Parse(KeyList.Of(name), 1, Layout(FakeKeyboardLayoutWrapper.Us)).Events[0].Code.Should().Be(virtualKey);
    }

    [Fact]
    public void NameOf_WritesAChordAsPressKeysDoes()
    {
        PressKeysParser.NameOf(new KeyChord(KeyModifiers.Shift | KeyModifiers.Control, InputEventKind.VirtualKey, '0')).Should().Be("ctrl+shift+0");
        PressKeysParser.NameOf(new KeyChord(KeyModifiers.Alt, InputEventKind.VirtualKey, 0xBB)).Should().Be("alt+oem_plus");
        PressKeysParser.NameOf(new KeyChord(KeyModifiers.Control, InputEventKind.Unicode, 'é')).Should().Be("ctrl+é");
    }

    [Fact]
    public void TheGuard_ReadsAChordWithAKeyNamedByItsVirtualKey_AsItsSendKeysSpelling()
    {
        // A destructive chord configured as ^{OEM_2} (WAM_DESTRUCTIVE_CHORDS) is the press_keys chord ctrl+oem_2
        var options = InputGuardOptions.FromEnvironment(name => name == InputGuardOptions.DestructiveChordsVariable ? "^{OEM_2}" : null);

        options.FindDestructiveChord(Us("ctrl+oem_2").Events).Should().NotBeNull();
        options.FindDestructiveChord(Us("ctrl+oem_1").Events).Should().BeNull();
    }

    [Fact]
    public void ADigit_IsItsKey_EvenOnAzerty_SoCtrl1IsTheShortcut()
    {
        var spec = PressKeysParser.Parse(KeyList.Of("ctrl+1"), 1, Layout(FakeKeyboardLayoutWrapper.French));

        spec.Events.Should().Equal(Seq(One(CtrlDown), Tap('1', 0x02), One(CtrlUp)));
    }

    [Fact]
    public void NamedKeys_AndTheWindowsKey_ComeFromTheKeyTable()
    {
        Us("alt+f4").Events.Should().Equal(Seq(One(AltDown), Tap(VkF4, 0x3E), One(AltUp)));
        Us("win+d").Events.Should().Equal(Seq(One(WinDown), Tap('D', 0x20), One(WinUp)));
        Us("windows+d").Events.Should().Equal(Us("win+d").Events);
    }

    [Fact]
    public void ModifiersAlone_ArePressedAndReleased()
    {
        Us("shift").Events.Should().Equal(ShiftDown, ShiftUp);
        Us(["ctrl", "alt"]).Events.Should().Equal(CtrlDown, AltDown, AltUp, CtrlUp);
    }

    [Theory]
    [InlineData("ctrl+a+b", "one key")]
    [InlineData("ctrl++", "plus")]
    [InlineData("+a", "plus")]
    [InlineData("tab*0", "1 to 100")]
    [InlineData("tab*101", "1 to 100")]
    [InlineData("tab*x", "1 to 100")]
    [InlineData("   ", "no keys")]
    [InlineData("ctrl+ctrl", "twice")]
    public void Mistakes_AreInvalidKeySpecifications(string keys, string message)
    {
        var act = () => Us(keys);

        act.Should().Throw<KeySyntaxException>().WithMessage($"*{message}*");
    }

    [Fact]
    public void AnUnknownName_IsAnUnknownKey()
    {
        var act = () => Us("ctrl+frobnicate");

        act.Should().Throw<ArgumentException>().Where(e => !(e is KeySyntaxException)).WithMessage("*FROBNICATE*");
    }

    [Fact]
    public void MoreThanTheMostPresses_IsRefused()
    {
        var act = () => Us("tab*100 tab*100 tab");
        var repeated = () => Us("tab*100", repeat: 3);

        act.Should().Throw<KeySyntaxException>().WithMessage($"*{PressKeysParser.MaxPresses}*");
        repeated.Should().Throw<KeySyntaxException>().WithMessage($"*{PressKeysParser.MaxPresses}*");
        Us("tab*100 tab*100").Steps.Should().HaveCount(PressKeysParser.MaxPresses);
    }

    [Fact]
    public void KeyList_Text_IsHowTheResultEchoesIt()
    {
        KeyList.Of("ctrl+shift+t").Text.Should().Be("ctrl+shift+t");
        KeyList.Of(["ctrl", "shift", "t"]).Text.Should().Be("ctrl+shift+t");
        KeyList.Of(["ctrl+a", "delete"]).Text.Should().Be("ctrl+a delete");
    }
}
