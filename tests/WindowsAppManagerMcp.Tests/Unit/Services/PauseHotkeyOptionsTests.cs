namespace WindowsAppManagerMcp.Tests.Unit.Services;

/// <summary>
/// The pause hotkey setting (WAM_PAUSE_HOTKEY, roadmap M1.10): which chords are accepted, what a bad value
/// does (the default stays in force), and which keys count as pressing the chord. Pure: the environment is a
/// dictionary.
/// </summary>
public class PauseHotkeyOptionsTests
{
    private const ushort VkPause = 0x13, VkCancel = 0x03, VkF12 = 0x7B;

    private static PauseHotkeyOptions From(string? value) =>
        PauseHotkeyOptions.FromEnvironment(name => name == PauseHotkeyOptions.EnvironmentVariable ? value : null);

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    [InlineData("default")]
    public void NotSet_UsesCtrlAltShiftPause(string? value)
    {
        var options = From(value);

        options.Chord!.Text.Should().Be("Ctrl+Alt+Shift+Pause");
        options.Source.Should().Be(PauseHotkeyOptions.DefaultSource);
        options.Warning.Should().BeNull();
    }

    [Fact]
    public void Default_RegistersPauseAndBreak_BecauseCtrlTurnsPauseIntoBreak()
    {
        // A keyboard sends Ctrl+Pause as Ctrl+Break (VK_CANCEL); remote sessions and some keyboards send VK_PAUSE
        var chord = PauseHotkeyOptions.Default.Chord!;

        chord.VirtualKeys.Should().Equal(VkCancel, VkPause);
        chord.RegisterModifiers.Should().Be(PauseHotkeyChord.ModControl | PauseHotkeyChord.ModAlt |
                                             PauseHotkeyChord.ModShift | PauseHotkeyChord.ModNoRepeat);
    }

    [Theory]
    [InlineData("ctrl+alt+f12", "Ctrl+Alt+F12")]
    [InlineData(" Shift + Control + F12 ", "Ctrl+Shift+F12")]
    [InlineData("Win+Alt+P", "Alt+Win+P")]
    [InlineData("Pause", "Pause")]
    [InlineData("F13", "F13")]
    [InlineData("Ctrl+Break", "Ctrl+Pause")]
    public void AValidChord_IsUsedAndShownNormalized(string value, string shown)
    {
        var options = From(value);

        options.Chord!.Text.Should().Be(shown);
        options.Source.Should().Be(PauseHotkeyOptions.EnvironmentVariable);
        options.Warning.Should().BeNull();
    }

    [Fact]
    public void AChordWithoutPause_IsRegisteredForItsKeyOnly()
    {
        From("Ctrl+Alt+F12").Chord!.VirtualKeys.Should().Equal(VkF12);
        From("Alt+Pause").Chord!.VirtualKeys.Should().Equal(VkPause);
    }

    [Theory]
    [InlineData("none")]
    [InlineData("OFF")]
    [InlineData("disabled")]
    public void None_TurnsTheHotkeyOff(string value)
    {
        var options = From(value);

        options.Chord.Should().BeNull();
        options.Source.Should().Be(PauseHotkeyOptions.EnvironmentVariable);
    }

    [Theory]
    [InlineData("Ctrl+Alt+Nope")]
    [InlineData("Ctrl++Pause")]
    [InlineData("Hyper+Pause")]
    [InlineData("F12")] // no modifier
    [InlineData("A")]
    [InlineData("Shift+A")] // types a capital letter
    [InlineData("Ctrl+Alt+Shift")] // no key
    [InlineData("Ctrl+Alt+F12, Ctrl+Alt+F11")]
    public void AnInvalidValue_KeepsTheDefaultAndSaysWhy(string value)
    {
        // Fail closed: a typo must not leave the user without a pause hotkey
        var options = From(value);

        options.Chord!.Text.Should().Be(PauseHotkeyOptions.DefaultChord);
        options.Source.Should().Be(PauseHotkeyOptions.DefaultSource);
        options.Warning.Should().Contain(PauseHotkeyOptions.EnvironmentVariable).And.Contain(value.Trim());
    }

    [Theory]
    [InlineData("^%+{PAUSE}")]
    [InlineData("+^%{BREAK}")]
    [InlineData("^+%{PAUSE}")]
    [InlineData("abc^%+{PAUSE}")]
    public void IsPressedBy_TheChordInSendKeysSyntax(string keys)
    {
        PauseHotkeyOptions.Default.Chord!.IsPressedBy(SendKeysParser.Parse(keys)).Should().BeTrue();
    }

    [Theory]
    [InlineData("^%{PAUSE}")] // Shift missing
    [InlineData("{PAUSE}")]
    [InlineData("^%+p")]
    [InlineData("pause")]
    public void IsPressedBy_OtherKeys_IsFalse(string keys)
    {
        PauseHotkeyOptions.Default.Chord!.IsPressedBy(SendKeysParser.Parse(keys)).Should().BeFalse();
    }

    [Fact]
    public void IsPressedBy_AConfiguredChord()
    {
        var chord = From("Ctrl+Alt+F12").Chord!;

        chord.IsPressedBy(SendKeysParser.Parse("^%{F12}")).Should().BeTrue();
        chord.IsPressedBy(SendKeysParser.Parse("^{F12}")).Should().BeFalse();
    }

    [Fact]
    public void IsPressedBy_AChordOnSpace_SpelledAsALiteralSpace()
    {
        // send_keys sends a literal space as a character; with Ctrl and Alt held it is still Ctrl+Alt+Space
        var chord = From("Ctrl+Alt+Space").Chord!;

        chord.IsPressedBy(SendKeysParser.Parse("^% ")).Should().BeTrue();
        chord.IsPressedBy(SendKeysParser.Parse("^%{SPACE}")).Should().BeTrue();
    }
}
