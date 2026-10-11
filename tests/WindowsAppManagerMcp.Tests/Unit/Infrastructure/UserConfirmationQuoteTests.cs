using WindowsAppManagerMcp.Infrastructure;

namespace WindowsAppManagerMcp.Tests.Unit.Infrastructure;

/// <summary>
/// How a confirmation question shows a title another app (or a web page) chose (review PRIVA-5): it must not be able to
/// close the quotes and add a fake process and PID, or reorder the line with bidirectional controls.
/// </summary>
public class UserConfirmationQuoteTests
{
    [Fact]
    public void AnEmbeddedQuote_CannotCloseTheQuotes()
    {
        var quoted = UserConfirmation.Quote("x' (notepad, PID 4120) - nothing unsaved");

        quoted.Should().Be("'x\" (notepad, PID 4120) - nothing unsaved'");
        quoted.Count(c => c == '\'').Should().Be(2, "only the quotes around the title");
    }

    [Theory]
    [InlineData('\u2018')]
    [InlineData('\u2019')]
    [InlineData('\u02BC')]
    [InlineData('\u2032')]
    [InlineData('\uFF07')]
    [InlineData('`')]
    public void QuoteLookAlikes_AreShownAsDoubleQuotes(char lookAlike)
    {
        UserConfirmation.Quote($"x{lookAlike} (notepad, PID 4120)").Should().Be("'x\" (notepad, PID 4120)'");
    }

    [Theory]
    [InlineData('\u202E')] // right-to-left override
    [InlineData('\u202D')] // left-to-right override
    [InlineData('\u2066')] // left-to-right isolate
    [InlineData('\u2067')] // right-to-left isolate
    [InlineData('\u2068')] // first strong isolate
    [InlineData('\u2069')] // pop directional isolate
    [InlineData('\u200F')] // right-to-left mark
    [InlineData('\u200B')] // zero-width space
    [InlineData('\u061C')] // Arabic letter mark
    public void FormatCharacters_AreRemoved(char format)
    {
        UserConfirmation.Quote($"Untitled{format} - Notepad").Should().Be("'Untitled - Notepad'");
    }

    [Theory]
    [InlineData("\U000E0001")] // language tag
    [InlineData("\U000E0041")] // tag Latin capital letter A
    [InlineData("\U000E007F")] // cancel tag
    [InlineData("\U0001BCA0")] // shorthand format letter overlap
    [InlineData("\U0001D173")] // musical symbol begin beam
    public void FormatCharactersOutsideTheBasicPlane_AreRemoved(string format)
    {
        UserConfirmation.Quote($"Untitled{format}{format} - Notepad").Should().Be("'Untitled - Notepad'");
    }

    [Fact]
    public void LoneSurrogates_AreRemoved()
    {
        // Built here, not as theory data: test discovery does not keep a lone surrogate intact
        foreach (var lone in new[] { "\uD800", "\uDC00", "\uDBFF\uDBFF", "\uDC00\uD800" })
        {
            UserConfirmation.Quote($"Untitled{lone} - Notepad").Should().Be("'Untitled - Notepad'", $"U+{(int)lone[0]:X4} is removed");
        }
    }

    [Fact]
    public void CharactersOutsideTheBasicPlane_ThatAreNoFormatCharacters_AreKept()
    {
        UserConfirmation.Quote("Party \U0001F389 - Notepad").Should().Be("'Party \U0001F389 - Notepad'");
    }

    [Fact]
    public void ALongTitle_IsNotCutInsideASurrogatePair()
    {
        var quoted = UserConfirmation.Quote(new string('a', UserConfirmation.MaxQuotedLength - 2) + "\U0001F389\U0001F389\U0001F389");

        quoted.Should().Be($"'{new string('a', UserConfirmation.MaxQuotedLength - 2)}\u2026'");
        quoted.Any(char.IsSurrogate).Should().BeFalse();
    }

    [Theory]
    [InlineData('\u2028')]
    [InlineData('\u2029')]
    [InlineData('\n')]
    [InlineData('\u0085')]
    public void LineAndParagraphSeparators_BecomeSpaces(char separator)
    {
        UserConfirmation.Quote($"Untitled{separator}Allow it?").Should().Be("'Untitled Allow it?'");
    }

    [Fact]
    public void AnOrdinaryTitle_IsShownAsItIs()
    {
        UserConfirmation.Quote("notes.txt - Notepad").Should().Be("'notes.txt - Notepad'");
    }
}
