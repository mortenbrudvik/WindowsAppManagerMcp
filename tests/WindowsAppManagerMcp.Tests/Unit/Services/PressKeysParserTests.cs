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
