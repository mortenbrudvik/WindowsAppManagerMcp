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
    [InlineData("abc^w", "^w")] // anywhere in the sequence
    public void FindDestructiveChord_DefaultList_FindsTheChord(string keys, string expectedSpec)
    {
        Find(keys)!.Spec.Should().Be(expectedSpec);
    }

    [Theory]
    [InlineData("{F4}")]
    [InlineData("^{F4}")]
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
            .Should().Equal("%{F4}", "^w", "^q", "^+{ESC}", "#l");
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
        Options("default ^{F4}").DestructiveChords.Select(c => c.Spec)
            .Should().Equal("%{F4}", "^w", "^q", "^+{ESC}", "#l", "^{F4}");
    }

    [Fact]
    public void FromEnvironment_None_TurnsTheRuleOff()
    {
        Find("%{F4}", Options("none")).Should().BeNull();
    }

    [Fact]
    public void FromEnvironment_EntriesThatAreNotOneChord_AreReportedAndIgnored()
    {
        // Arrange - a sequence, an unknown key and an unclosed brace
        var options = Options("^a{DELETE} ^{NOPE} %{F4 ^w");

        // Assert
        options.InvalidChordEntries.Should().Equal("^a{DELETE}", "^{NOPE}", "%{F4");
        options.DestructiveChords.Select(c => c.Spec).Should().Equal("^w");
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
