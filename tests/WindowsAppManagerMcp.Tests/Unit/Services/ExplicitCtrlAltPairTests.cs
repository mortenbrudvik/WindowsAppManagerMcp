namespace WindowsAppManagerMcp.Tests.Unit.Services;

/// <summary>
/// The left Ctrl and the right Alt are AltGr only where a key parser pressed them for a character the layout types with
/// AltGr ('@' on a German keyboard, roadmap M4.1). Spelled out as keys, send_keys' <c>{LCTRL down}{RALT down}</c> and
/// press_keys' <c>lctrl+ralt</c> are Ctrl and Alt, as Windows reads them: Ctrl+Alt+Tab opens the task switcher, Ctrl+Alt+V
/// pastes in Office, and the pause hotkey's RegisterHotKey fires on either side. So every chord check (destructive chords,
/// keys after a focus change, the pause chord, copy and paste) reads them as Ctrl+Alt. Pure, except the last tests, which
/// go through the input service with <see cref="FakeInputWrapper"/>, so nothing reaches the desktop.
/// </summary>
public class ExplicitCtrlAltPairTests
{
    private const ushort VkTab = 0x09, VkPause = 0x13, VkLControl = 0xA2, VkRMenu = 0xA5;

    private static KeyboardLayout Layout(nint layout) =>
        KeyboardLayout.Of(new FakeKeyboardLayoutWrapper { Layout = layout }, 0, capsLock: false);

    /// <summary>send_keys' events for <paramref name="keys"/> on the German layout, where '@' is AltGr+Q.</summary>
    private static IReadOnlyList<InputEvent> SendKeys(string keys) =>
        KeySpecParser.Parse(keys, Layout(FakeKeyboardLayoutWrapper.German)).Events;

    /// <summary>press_keys' events for <paramref name="keys"/> on the German layout.</summary>
    private static IReadOnlyList<InputEvent> PressKeys(string keys) =>
        PressKeysParser.Parse(KeyList.Of(keys), 1, Layout(FakeKeyboardLayoutWrapper.German)).Events;

    /// <summary>The same keys in send_keys' and press_keys' spelling: the left Ctrl and the right Alt held for a key.</summary>
    public static TheoryData<string, string> Spellings(string sendKeysKey, string pressKeysKey) => new()
    {
        { "sendKeys", $"{{LCTRL down}}{{RALT down}}{sendKeysKey}{{RALT up}}{{LCTRL up}}" },
        { "pressKeys", $"lctrl+ralt+{pressKeysKey}" }
    };

    private static IReadOnlyList<InputEvent> Events(string tool, string keys) =>
        tool == "sendKeys" ? SendKeys(keys) : PressKeys(keys);

    public static TheoryData<string, string> CtrlAltQ() => Spellings("q", "q");

    public static TheoryData<string, string> CtrlAltW() => Spellings("w", "w");

    public static TheoryData<string, string> CtrlAltShiftPause() => Spellings("+{PAUSE}", "shift+pause");

    public static TheoryData<string, string> CtrlAltV() => Spellings("v", "v");

    public static TheoryData<string, string> CtrlAltC() => Spellings("c", "c");

    #region The chords

    [Theory]
    [MemberData(nameof(CtrlAltQ))]
    public void Chords_TheLeftCtrlAndTheRightAltAsKeys_AreCtrlAndAlt(string tool, string keys)
    {
        SendKeysParser.Chords(Events(tool, keys)).Should().Equal(
            new KeyChord(KeyModifiers.Control | KeyModifiers.Alt, InputEventKind.VirtualKey, 'Q'));
    }

    [Theory]
    [InlineData("sendKeys", "@")]
    [InlineData("pressKeys", "@")]
    public void Chords_TheLayoutsAltGrCharacter_StaysACharacter(string tool, string keys)
    {
        // The same keys the layout sends for '@' (AltGr+Q): the parser pressed them, so they hold nothing
        SendKeysParser.Chords(Events(tool, keys)).Should().Equal(new KeyChord(KeyModifiers.None, InputEventKind.VirtualKey, 'Q'));
        InputGuardOptions.Default.FindDestructiveChord(Events(tool, keys)).Should().BeNull();
    }

    [Fact]
    public void Parse_TagsOnlyTheAltGrKeysTheParserPressedForACharacter()
    {
        var typed = SendKeys("@");
        var spelled = SendKeys("{LCTRL down}{RALT down}q{RALT up}{LCTRL up}");
        var pressed = PressKeys("@");

        typed.Where(e => e.Code is VkLControl or VkRMenu).Should().HaveCount(4).And.OnlyContain(e => e.AltGr);
        pressed.Where(e => e.Code is VkLControl or VkRMenu).Should().HaveCount(4).And.OnlyContain(e => e.AltGr);
        spelled.Should().OnlyContain(e => !e.AltGr);
        PressKeys("lctrl+ralt+q").Should().OnlyContain(e => !e.AltGr);
    }

    [Theory]
    [InlineData("{LCTRL down}@{LCTRL up}")] // an explicit left Ctrl with the AltGr character: Ctrl+Alt+Q
    [InlineData("{RALT down}@{RALT up}")] // an explicit right Alt: the parser adds only the left Ctrl
    public void Chords_AnExplicitHalfOfThePair_MakesItCtrlAndAlt(string keys)
    {
        SendKeysParser.Chords(SendKeys(keys)).Should().Equal(
            new KeyChord(KeyModifiers.Control | KeyModifiers.Alt, InputEventKind.VirtualKey, 'Q'));
    }

    #endregion

    #region Every check reads them as Ctrl+Alt

    [Theory]
    [MemberData(nameof(CtrlAltW))]
    public void FindDestructiveChord_CtrlAltW_IsCtrlW(string tool, string keys)
    {
        InputGuardOptions.Default.FindDestructiveChord(Events(tool, keys))!.Spec.Should().Be("^w");
    }

    [Theory]
    [InlineData("sendKeys", "{LCTRL down}{RALT down}{TAB}{RALT up}{LCTRL up}x")]
    [InlineData("pressKeys", "lctrl+ralt+tab x")]
    public void FindKeysAfterFocusChange_CtrlAltTab_HandsTheKeyboardToTheTaskSwitcher(string tool, string keys)
    {
        // Windows opens the Ctrl+Alt+Tab task switcher: the keys after it would go there
        InputGuardOptions.Default.FindKeysAfterFocusChange(Events(tool, keys)).Should().Be("%{TAB}");
    }

    [Theory]
    [MemberData(nameof(CtrlAltShiftPause))]
    public void PauseChord_CtrlAltShiftPause_IsTheDefaultChord(string tool, string keys)
    {
        // RegisterHotKey(MOD_CONTROL | MOD_ALT | MOD_SHIFT) fires on either side's Ctrl and Alt
        PauseHotkeyOptions.Default.Chord!.IsPressedBy(Events(tool, keys)).Should().BeTrue();
    }

    [Theory]
    [InlineData("sendKeys")]
    [InlineData("pressKeys")]
    public void PauseChord_ALayoutsAltGrCharacter_PressesAChordOnItsKey(string tool)
    {
        // A hotkey reads the modifiers, not the character: Ctrl+Alt+Q fires when '@' is typed as AltGr+Q
        PauseHotkeyChord.TryParse("Ctrl+Alt+Q", out var chord, out _).Should().BeTrue();

        chord!.IsPressedBy(Events(tool, "@")).Should().BeTrue();
    }

    [Theory]
    [MemberData(nameof(CtrlAltV))]
    public void PasteTrigger_CtrlAltV_Pastes(string tool, string keys)
    {
        // Office's Paste Special
        ClipboardOrigin.PasteTrigger(Events(tool, keys)).Should().Be(ClipboardPasteKind.Paste);
    }

    [Theory]
    [MemberData(nameof(CtrlAltC))]
    public void CopyTrigger_CtrlAltC_IsCopyKeys(string tool, string keys)
    {
        ClipboardOrigin.CopyTrigger(Events(tool, keys)).Should().Be(ClipboardCopyTrigger.CopyKeys);
    }

    [Theory]
    [InlineData("sendKeys", "{LCTRL down}{RALT down}cv{RALT up}{LCTRL up}")]
    [InlineData("pressKeys", "lctrl+ralt+c lctrl+ralt+v")]
    public void CopiesBeforePaste_CtrlAltCThenCtrlAltV_CopiesAndPastes(string tool, string keys)
    {
        ClipboardOrigin.CopiesBeforePaste(Events(tool, keys)).Should().BeTrue();
    }

    #endregion

    #region Through the input service

    private readonly FakeInputWrapper _input = new();
    private readonly FakeKeyboardLayoutWrapper _layouts = new() { Layout = FakeKeyboardLayoutWrapper.German };

    private InputSimulationService CreateSut() =>
        new(TestDataFactory.CreateMockMonitorService().Object, _input, TestDataFactory.CreateAllowingInputGuard().Object,
            new InputSession(_input), keyboardLayout: _layouts);

    [Fact]
    public async Task SendKeys_TheDefaultPauseChordWithTheLeftCtrlAndTheRightAlt_IsRefused_AndNothingIsSent()
    {
        var result = await CreateSut().SendKeysAsync("{LCTRL down}{RALT down}+{PAUSE}{RALT up}{LCTRL up}");

        result.ErrorCode.Should().Be(InputErrorCode.PauseChordNotAllowed);
        result.Error.Should().Contain("Nothing was sent");
        _input.Batches.Should().BeEmpty();
    }

    [Fact]
    public async Task PressKeys_TheDefaultPauseChordWithTheLeftCtrlAndTheRightAlt_IsRefused_AndNothingIsSent()
    {
        var result = await CreateSut().PressKeysAsync(KeyList.Of("lctrl+ralt+shift+pause"), 1, 0, 0, InputTarget.None);

        result.ErrorCode.Should().Be(InputErrorCode.PauseChordNotAllowed);
        _input.Batches.Should().BeEmpty();
        _input.Events.Should().NotContain(e => e.Code == VkPause || e.Code == VkTab);
    }

    [Fact]
    public async Task SendKeys_TheLayoutsAltGrCharacters_AreStillTyped()
    {
        var result = await CreateSut().SendKeysAsync("user@example.de");

        result.Success.Should().BeTrue(result.Error);
    }

    #endregion
}
