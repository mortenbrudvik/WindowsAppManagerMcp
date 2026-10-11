using System.Text.RegularExpressions;

namespace WindowsAppManagerMcp.Tests.Unit.Services;

/// <summary>
/// The browser-aware title comparison the window selectors, find_windows, layouts and the CDP tab matcher share
/// (roadmap M2.4, M0.7): invisible format characters and Edge's " and N more pages" segment never make a title filter
/// miss the window it was written for.
/// </summary>
public class WindowTitlesTests
{
    /// <summary>An Edge window title as Windows reports it: a zero-width space inside "Microsoft Edge".</summary>
    private const string EdgeTitle = "Docs and 2 more pages - Personal - Microsoft​ Edge";

    [Theory]
    [InlineData(null, "")]
    [InlineData("", "")]
    [InlineData("  Inbox \t-  Outlook ", "Inbox - Outlook")]
    [InlineData("Microsoft​ Edge", "Microsoft Edge")]
    [InlineData("‎Bidi‏ title", "Bidi title")]
    public void Normalize_RemovesFormatCharactersAndCollapsesWhitespace(string? title, string expected)
    {
        WindowTitles.Normalize(title).Should().Be(expected);
    }

    [Fact]
    public void CdpTargetMatcher_SharesTheNormalizer()
    {
        CdpTargetMatcher.NormalizeTitle(EdgeTitle).Should().Be(WindowTitles.Normalize(EdgeTitle));
    }

    [Theory]
    [InlineData(EdgeTitle, "Docs - Personal - Microsoft Edge")]
    [InlineData("Docs and 1 more page - Microsoft​ Edge", "Docs - Microsoft Edge")]
    [InlineData("Docs and 3 more pages", "Docs")]
    [InlineData("Plans and 2 more pages of notes - Notepad", "Plans and 2 more pages of notes - Notepad")]
    public void ForMatching_RemovesEdgesMorePagesSegment(string title, string expected)
    {
        WindowTitles.ForMatching(title).Should().Be(expected);
    }

    [Theory]
    [InlineData(EdgeTitle, "microsoft edge", true)] // the zero-width space does not hide the browser's name
    [InlineData(EdgeTitle, "Docs - Personal", true)] // read without the more-pages segment
    [InlineData(EdgeTitle, "Docs and 2 more", true)] // read as it is
    [InlineData(EdgeTitle, "GitHub", false)]
    [InlineData("Untitled - Notepad", "", true)]
    [InlineData("Untitled - Notepad", "  untitled  ", true)]
    public void Contains_IgnoresCaseFormatCharactersAndTheMorePagesSegment(string title, string part, bool expected)
    {
        WindowTitles.Contains(title, part).Should().Be(expected);
    }

    [Theory]
    [InlineData(EdgeTitle, "Docs - Personal - Microsoft Edge", true)]
    [InlineData(EdgeTitle, "docs and 2 more pages - personal - microsoft edge", true)]
    [InlineData("Untitled - Notepad", "untitled - notepad", true)]
    [InlineData("Untitled - Notepad", "Untitled", false)] // whole titles only: no prefix match
    [InlineData("Untitled - Notepad", "Untitled - Notepad - Extra", false)]
    public void AreEqual_ComparesWholeTitles(string title, string expected, bool equal)
    {
        WindowTitles.AreEqual(title, expected).Should().Be(equal);
    }

    [Fact]
    public void Matches_ReadsTheNormalizedTitle_WithAndWithoutTheMorePagesSegment()
    {
        var edge = WindowTitles.CompileTitleRegex("^Docs - Personal - Microsoft Edge$", out _)!;
        var morePages = WindowTitles.CompileTitleRegex(@"and \d more pages", out _)!;
        var notepad = WindowTitles.CompileTitleRegex("^untitled - notepad$", out _)!;

        WindowTitles.Matches(EdgeTitle, edge).Should().BeTrue();
        WindowTitles.Matches(EdgeTitle, morePages).Should().BeTrue();
        WindowTitles.Matches("Untitled - Notepad", notepad).Should().BeTrue("the pattern ignores case");
        WindowTitles.Matches("notes.txt - Notepad", notepad).Should().BeFalse();
    }

    [Fact]
    public void CompileTitleRegex_RefusesAnInvalidPattern()
    {
        WindowTitles.CompileTitleRegex("(unclosed", out var error).Should().BeNull();
        error.Should().Contain("titleRegex").And.Contain("(unclosed");
    }

    [Fact]
    public void CompileTitleRegex_HasTheTimeout()
    {
        var regex = WindowTitles.CompileTitleRegex("x", out var error)!;

        error.Should().BeNull();
        regex.MatchTimeout.Should().Be(TimeSpan.FromMilliseconds(100));
        regex.Options.Should().HaveFlag(RegexOptions.IgnoreCase);
    }

    [Fact]
    public void Matches_APatternThatBacktracksBadly_TimesOut()
    {
        // Arrange - the classic catastrophic pattern on a title that almost matches
        var regex = WindowTitles.CompileTitleRegex("^(a+)+$", out _)!;
        var title = new string('a', 5000) + "!";

        // Act
        var match = () => WindowTitles.Matches(title, regex);

        // Assert
        match.Should().Throw<RegexMatchTimeoutException>();
    }
}
