namespace WindowsAppManagerMcp.Tests.Unit.Services;

/// <summary>
/// Unit tests for the destructive-chord rule (roadmap M1.5): which key chords send_keys refuses without an
/// explicit target, how the chord list is read from WAM_DESTRUCTIVE_CHORDS, and how a parsed key sequence
/// is turned into chords. Pure: nothing is sent.
/// </summary>
public class DestructiveChordTests
{
    private const ushort VkF4 = 0x73;
    private const ushort VkW = 0x57;
    private const ushort VkTab = 0x09;
    private const ushort VkLwin = 0x5B;

    private static InputGuardOptions Options(string? value) =>
        InputGuardOptions.FromEnvironment(name => name == InputGuardOptions.DestructiveChordsVariable ? value : null);

    private static DestructiveChord? Find(string keys, InputGuardOptions? options = null) =>
        (options ?? InputGuardOptions.Default).FindDestructiveChord(SendKeysParser.Parse(keys));

    #region Chords

    [Fact]
    public void Chords_AltF4_IsF4WithAltHeld()
    {
        SendKeysParser.Chords(SendKeysParser.Parse("%{F4}")).Should().Equal(
            new KeyChord(KeyModifiers.Alt, InputEventKind.VirtualKey, VkF4));
    }

    [Fact]
    public void Chords_ModifiersApplyToTheNextKeyOnly()
    {
        SendKeysParser.Chords(SendKeysParser.Parse("^wW")).Should().Equal(
            new KeyChord(KeyModifiers.Control, InputEventKind.VirtualKey, VkW),
            new KeyChord(KeyModifiers.None, InputEventKind.VirtualKey, VkW));
    }

    [Fact]
    public void Chords_AModifierSentAsItsOwnKey_HoldsNothing()
    {
        // {ALT} is pressed and released before {F4}: not a chord
        SendKeysParser.Chords(SendKeysParser.Parse("{ALT}{F4}")).Should().Equal(
            new KeyChord(KeyModifiers.None, InputEventKind.VirtualKey, VkF4));
    }

    [Fact]
    public void Parse_WithoutTheWindowsModifier_SendsHashAsACharacter()
    {
        SendKeysParser.Parse("#l").First().Should().Be(InputEvent.Unicode('#', 0));
    }

    [Fact]
    public void KeyPresses_ATappedModifier_IsAPressOfThatKey()
    {
        // {WIN} goes down and up with nothing in between: it opens the Start menu
        SendKeysParser.KeyPresses(SendKeysParser.Parse("{WIN}a")).Should().Equal(
            new KeyChord(KeyModifiers.None, InputEventKind.VirtualKey, VkLwin),
            new KeyChord(KeyModifiers.None, InputEventKind.VirtualKey, 0x41));
    }

    [Fact]
    public void KeyPresses_AModifierHeldForAKey_IsNotAPressOfItsOwn()
    {
        SendKeysParser.KeyPresses(SendKeysParser.Parse("%{TAB}")).Should().Equal(
            new KeyChord(KeyModifiers.Alt, InputEventKind.VirtualKey, VkTab));
        SendKeysParser.KeyPresses(SendKeysParser.Parse("#l", windowsModifier: true)).Should().Equal(
            new KeyChord(KeyModifiers.Windows, InputEventKind.VirtualKey, 0x4C));
    }

    #endregion

    #region Keys after a chord that moves the focus

    [Theory]
    [InlineData("%{TAB}{ENTER}", "%{TAB}")] // Alt+Tab to the previous window, often the agent's terminal
    [InlineData("%{TAB}echo hi", "%{TAB}")]
    [InlineData("+%{TAB}x", "%{TAB}")]
    [InlineData("%{ESC}a", "%{ESC}")]
    [InlineData("^{ESC}notepad{ENTER}", "^{ESC}")]
    [InlineData("{WIN}cmd{ENTER}", "{LWIN}")]
    [InlineData("{RWIN}x", "{RWIN}")]
    [InlineData("%{SPACE}c", "%{SPACE}")] // the window menu's Close
    [InlineData("%{F4}n", "%{F4}")] // after a close, the next window gets the keys
    [InlineData("^w^w", "^w")]
    [InlineData("a%{TAB}b", "%{TAB}")]
    public void FindKeysAfterFocusChange_KeysAfterTheChord_FindsIt(string keys, string expectedSpec)
    {
        InputGuardOptions.Default.FindKeysAfterFocusChange(SendKeysParser.Parse(keys)).Should().Be(expectedSpec);
    }

    [Theory]
    [InlineData("%{TAB}")]
    [InlineData("^a%{TAB}")]
    [InlineData("{WIN}")]
    [InlineData("%{F4}")]
    [InlineData("^a{DELETE}")]
    [InlineData("{ALT}{TAB}x")] // Alt is released before Tab: not Alt+Tab
    [InlineData("^c{ESC}{ENTER}")]
    public void FindKeysAfterFocusChange_TheChordLastOrNoChord_FindsNothing(string keys)
    {
        InputGuardOptions.Default.FindKeysAfterFocusChange(SendKeysParser.Parse(keys)).Should().BeNull();
    }

    [Theory]
    [InlineData("% c")] // a literal space with Alt held: Windows opens the window menu on it as on {SPACE}
    [InlineData("+% c")]
    [InlineData("%\tx")] // a literal tab with Alt held
    public void FindKeysAfterFocusChange_AnotherSpellingOfAFocusChord_FindsItToo(string keys)
    {
        InputGuardOptions.Default.FindKeysAfterFocusChange(SendKeysParser.Parse(keys)).Should().NotBeNull();
    }

    [Fact]
    public void Chords_ACharacterThatHasItsOwnKey_CountsAsThatKey()
    {
        // The parser sends a space as a Unicode character; a window acts on Alt and that character as on Alt+Space
        SendKeysParser.Chords(SendKeysParser.Parse("% ")).Should().Equal(
            new KeyChord(KeyModifiers.Alt, InputEventKind.VirtualKey, 0x20));
        SendKeysParser.KeyPresses(SendKeysParser.Parse("%\t")).Should().Equal(
            new KeyChord(KeyModifiers.Alt, InputEventKind.VirtualKey, VkTab));
        SendKeysParser.KeyPresses([InputEvent.Unicode('w', 0)]).Should().Equal(
            new KeyChord(KeyModifiers.None, InputEventKind.VirtualKey, VkW));
    }

    [Fact]
    public void Chords_OtherCharacters_StayCharacters()
    {
        SendKeysParser.Chords(SendKeysParser.Parse("%-")).Should().Equal(
            new KeyChord(KeyModifiers.Alt, InputEventKind.Unicode, '-'));
    }

    [Fact]
    public void FindDestructiveChord_ACharacterSpellingOfAConfiguredChord_IsFound()
    {
        // Ctrl+Space configured as destructive: a literal space with Ctrl held is the same key press
        Find("^ ", Options("^{SPACE}")).Should().NotBeNull();
    }

    [Fact]
    public void FindKeysAfterFocusChange_ConfiguredDestructiveChords_CountToo()
    {
        var options = Options("^{F5}");

        options.FindKeysAfterFocusChange(SendKeysParser.Parse("^{F5}x")).Should().Be("^{F5}");
        options.FindKeysAfterFocusChange(SendKeysParser.Parse("%{TAB}x")).Should().Be("%{TAB}", "the focus chords are fixed");
    }

    #endregion

    #region The default list

    [Theory]
    [InlineData("%{F4}", "%{F4}")]
    [InlineData("%{f4}", "%{F4}")]
    [InlineData("+%{F4}", "%{F4}")] // more modifiers: still Alt+F4
    [InlineData("^w", "^w")]
    [InlineData("^W", "^w")]
    [InlineData("^+w", "^w")] // Ctrl+Shift+W closes the whole browser window
    [InlineData("^q", "^q")]
    [InlineData("^+{ESC}", "^+{ESC}")]
    [InlineData("^{F4}", "^{F4}")] // closes the document or tab
    [InlineData("abc^w", "^w")] // anywhere in the sequence
    public void FindDestructiveChord_DefaultList_FindsTheChord(string keys, string expectedSpec)
    {
        Find(keys)!.Spec.Should().Be(expectedSpec);
    }

    [Theory]
    [InlineData("{F4}")]
    [InlineData("w")]
    [InlineData("%w")]
    [InlineData("{ALT}{F4}")]
    [InlineData("^{ESC}")] // Start menu, not Task Manager
    [InlineData("^c")]
    [InlineData("#l")] // send_keys has no Windows-key modifier: this types '#' and 'l'
    public void FindDestructiveChord_DefaultList_IgnoresOtherKeys(string keys)
    {
        Find(keys).Should().BeNull();
    }

    [Fact]
    public void Default_ListsTheRoadmapChords()
    {
        InputGuardOptions.Default.DestructiveChords.Select(c => c.Spec)
            .Should().Equal("%{F4}", "^w", "^q", "^{F4}", "^+{ESC}", "#l");
        InputGuardOptions.Default.InvalidChordEntries.Should().BeEmpty();
        InputGuardOptions.Default.DestructiveChordSource.Should().Be(InputGuardOptions.DefaultSource);
    }

    [Fact]
    public void Default_WinL_IsTheWindowsModifierWithL()
    {
        InputGuardOptions.Default.DestructiveChords.Single(c => c.Spec == "#l").Chord.Should().Be(
            new KeyChord(KeyModifiers.Windows, InputEventKind.VirtualKey, 0x4C));
    }

    #endregion

    #region WAM_DESTRUCTIVE_CHORDS

    [Fact]
    public void FromEnvironment_ReplacesTheDefaults()
    {
        // Arrange
        var options = Options("^{F4}  %{F4}");

        // Assert
        options.DestructiveChords.Select(c => c.Spec).Should().Equal("^{F4}", "%{F4}");
        options.DestructiveChordSource.Should().Be(InputGuardOptions.DestructiveChordsVariable);
        Find("^{F4}", options).Should().NotBeNull();
        Find("^w", options).Should().BeNull();
    }

    [Fact]
    public void FromEnvironment_DefaultEntry_KeepsTheDefaultsAndAddsMore()
    {
        Options("default ^{F5}").DestructiveChords.Select(c => c.Spec)
            .Should().Equal("%{F4}", "^w", "^q", "^{F4}", "^+{ESC}", "#l", "^{F5}");
    }

    [Fact]
    public void FromEnvironment_None_TurnsTheRuleOff()
    {
        Find("%{F4}", Options("none")).Should().BeNull();
    }

    [Fact]
    public void FromEnvironment_EntriesThatAreNotOneChord_AreReported_AndKeepTheDefaultsInForce()
    {
        // Arrange - a sequence, an unknown key and an unclosed brace
        var options = Options("^a{DELETE} ^{NOPE} %{F4 ^{F5}");

        // Assert - fails closed: a mistyped entry must not silently remove Alt+F4 and the others
        options.InvalidChordEntries.Should().Equal("^a{DELETE}", "^{NOPE}", "%{F4");
        options.DestructiveChords.Select(c => c.Spec).Should().Equal("^{F5}", "%{F4}", "^w", "^q", "^{F4}", "^+{ESC}", "#l");
    }

    [Fact]
    public void FromEnvironment_CommaSeparatedList_IsInvalid_SoTheDefaultsStay()
    {
        // Copying WAM_DENY_PROCESSES' comma style: one entry that is not one chord
        var options = Options("%{F4},^w");

        options.InvalidChordEntries.Should().Equal("%{F4},^w");
        options.DestructiveChords.Select(c => c.Spec).Should().Equal(InputGuardOptions.DefaultDestructiveChords);
        Find("%{F4}", options).Should().NotBeNull();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void FromEnvironment_Unset_UsesTheDefaults(string? value)
    {
        Options(value).DestructiveChords.Should().HaveCount(InputGuardOptions.DefaultDestructiveChords.Count);
    }

    #endregion
}
