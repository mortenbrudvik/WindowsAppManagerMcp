namespace WindowsAppManagerMcp.Tests.Unit.Native;

/// <summary>
/// How both engines read a page of a document (<see cref="UiaTextPageReader"/>, roadmap M3.9) through a range of a provider
/// whose characters can be longer than one UTF-16 character: the page's text never passes maxLength, its end points just past
/// its text so the next page skips nothing, and the range's text is never read without a limit.
/// </summary>
public class UiaTextPageReaderTests
{
    private static List<string> OneEach(string text) => FakeTextRange.OneEach(text);

    /// <summary>A document whose provider counts each "\r\n" as one character.</summary>
    private static List<string> LineBreakIsOne(string text) =>
        text.Replace("\r\n", "\n").Select(c => c == '\n' ? "\r\n" : c.ToString()).ToList();

    [Fact]
    public void Read_ReturnsThePageFromTheOffset_AndProbesOnceForMore()
    {
        // Act
        var page = new FakeTextRange(OneEach("abcdefghij")).Read(2, 3);

        // Assert
        page.Should().Be(new UiaTextPage("cde", 2, 3, HasMore: true));
    }

    [Fact]
    public void Read_AtTheEndOfTheDocument_KnowsThereIsNoMore()
    {
        var page = new FakeTextRange(OneEach("abcdefghij")).Read(8, 5);

        page.Should().Be(new UiaTextPage("ij", 8, 2, HasMore: false));
    }

    [Fact]
    public void Read_WhenLineBreaksAreOneCharacter_ShortensThePage_SoNothingIsSkippedOrReadTwice()
    {
        // Arrange - 400 lines: every page of 50 of the provider's characters holds more than 50 UTF-16 characters
        var document = string.Concat(Enumerable.Range(0, 400).Select(i => $"line {i}\r\n"));
        var characters = LineBreakIsOne(document);

        // Act - page through the whole document, each page from the previous page's end
        var pages = new List<UiaTextPage>();
        var limits = new List<int>();
        for (var offset = 0; pages.Count == 0 || pages[^1].HasMore; offset = pages[^1].Start + pages[^1].Length)
        {
            var range = new FakeTextRange(characters);
            pages.Add(range.Read(offset, 50));
            limits.AddRange(range.TextLimits);
        }

        // Assert
        string.Concat(pages.Select(p => p.Text)).Should().Be(document);
        pages.Should().OnlyContain(p => p.Text.Length <= 50 && p.Text.Length > 0 && !p.Cut);
        limits.Should().OnlyContain(limit => limit > 0 && limit <= 100 + UiaTextPageReader.MaxLeadInLength + 1,
            "the range's text is read with a limit, never in full");
    }

    [Fact]
    public void Read_WhenOnlyAFewCharactersAreWide_KeepsThePageNearlyFull()
    {
        // Arrange - one line break per 100 characters: the page is made shorter in proportion, not halved
        var document = string.Concat(Enumerable.Repeat(new string('x', 98) + "\r\n", 100));

        // Act
        var page = new FakeTextRange(LineBreakIsOne(document)).Read(0, 1_000);

        // Assert
        page.Text.Length.Should().BeInRange(900, 1_000);
        page.Text.Should().Be(document[..page.Text.Length]);
        page.HasMore.Should().BeTrue();
    }

    [Fact]
    public void Read_OfACharacterLongerThanThePage_CutsIt_AndSaysSo()
    {
        // Arrange - "a", "\r\n", "b": a page of one character cannot hold the line break
        var characters = LineBreakIsOne("a\r\nb");

        // Act
        var page = new FakeTextRange(characters).Read(1, 1);

        // Assert - the page ends after the line break, so the next one starts at "b"
        page.Should().Be(new UiaTextPage("\r", 1, 1, HasMore: true, Cut: true));
    }

    [Fact]
    public void Read_OfAProviderWithoutACharacterUnit_ReadsAtMostTwiceThePage()
    {
        // Arrange - a provider whose "character" is the whole document, as one that falls back to a larger unit
        var range = new FakeTextRange([new string('x', 1_000_000)]);

        // Act
        var page = range.Read(0, 100);

        // Assert
        page.Text.Should().HaveLength(100);
        page.Cut.Should().BeTrue();
        page.Length.Should().Be(1);
        page.HasMore.Should().BeFalse();
        range.TextLimits.Should().OnlyContain(limit => limit > 0 && limit <= 200);
    }

    [Fact]
    public void Read_NeverCutsBetweenTheHalvesOfASurrogatePair()
    {
        // Arrange - one character of the provider: three emoji, six UTF-16 characters
        var emoji = char.ConvertFromUtf32(0x1F600);
        var range = new FakeTextRange([emoji + emoji + emoji, "z"]);

        // Act
        var page = range.Read(0, 3);

        // Assert
        page.Text.Should().Be(emoji);
        page.Cut.Should().BeTrue();
        page.Length.Should().Be(1);
    }

    /// <summary>The document of the M5.6 desktop check: 1000 lines of 99 characters, joined by "\n" (99,999 characters).</summary>
    private static readonly string Lines = string.Join("\n", Enumerable.Range(0, 1000).Select(i => $"{i:00000} " + string.Concat(Enumerable.Repeat("abcdefghij", 9)) + "abc"));

    [Fact]
    public void Read_InChromium_APageThatStartsJustAfterALineBreak_DoesNotReadItAgain()
    {
        // Arrange - character 20000 starts line 200, just after line 199's "\n"
        var range = new FakeTextRange(OneEach(Lines)) { RereadsLineBreakBeforeStart = true };

        // Act
        var page = range.Read(20_000, 20_000);

        // Assert - as on Edge 155: without the fix the page read "\n00200 ..." and ended one character early
        page.Should().Be(new UiaTextPage(Lines.Substring(20_000, 20_000), 20_000, 20_000, HasMore: true));
    }

    [Theory]
    [InlineData(20_000)]
    [InlineData(100)]
    [InlineData(37)]
    [InlineData(1)]
    public void Read_InChromium_PagesJoinToTheDocument_WithNothingReadTwiceOrSkipped(int maxLength)
    {
        // Arrange
        var document = maxLength == 1 ? Lines[..1_000] : Lines;
        var characters = OneEach(document);

        // Act - page through the whole document, each page from the previous page's end
        var pages = new List<UiaTextPage>();
        for (var offset = 0; pages.Count == 0 || pages[^1].HasMore; offset = pages[^1].Start + pages[^1].Length)
        {
            pages.Add(new FakeTextRange(characters) { RereadsLineBreakBeforeStart = true }.Read(offset, maxLength));
        }

        // Assert - every page starts where the last one ended, as CDP's pages do
        string.Concat(pages.Select(p => p.Text)).Should().Be(document);
        pages.Select(p => p.Start).Should().Equal(Enumerable.Range(0, pages.Count).Select(i => i * maxLength));
        pages.Should().OnlyContain(p => !p.Cut && p.Text.Length == p.Length);
    }

    [Fact]
    public void Read_ReadsTheCharacterBeforeTheOffset_OnlyToLeaveItOut()
    {
        // Arrange - a provider whose ranges read what they span, and one that counts "\r\n" as one character
        var plain = new FakeTextRange(OneEach("abcdefghij"));
        var lineBreaks = new FakeTextRange(LineBreakIsOne("ab\r\ncd"));

        // Act
        var page = plain.Read(1, 3);
        var afterBreak = lineBreaks.Read(3, 2);

        // Assert
        page.Should().Be(new UiaTextPage("bcd", 1, 3, HasMore: true));
        afterBreak.Should().Be(new UiaTextPage("cd", 3, 2, HasMore: false));
        plain.TextLimits.Should().OnlyContain(limit => limit > 0 && limit <= 2 * 3 + UiaTextPageReader.MaxLeadInLength + 1);
    }

    [Fact]
    public void Read_PastTheEndOfTheDocument_ReturnsAnEmptyPageAtItsEnd()
    {
        var page = new FakeTextRange(OneEach("abc")) { RereadsLineBreakBeforeStart = true }.Read(10, 5);

        page.Should().Be(new UiaTextPage(string.Empty, 3, 0, HasMore: false));
    }
}
