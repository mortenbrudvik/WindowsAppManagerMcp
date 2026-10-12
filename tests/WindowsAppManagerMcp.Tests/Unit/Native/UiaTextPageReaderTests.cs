namespace WindowsAppManagerMcp.Tests.Unit.Native;

/// <summary>
/// How both engines read a page of a document (<see cref="UiaTextPageReader"/>, roadmap M3.9) through a range of a provider
/// whose characters can be longer than one UTF-16 character: the page's text never passes maxLength, its end points just past
/// its text so the next page skips nothing, and the range's text is never read without a limit.
/// </summary>
public class UiaTextPageReaderTests
{
    /// <summary>
    /// A provider's text range over a document of the provider's characters, each one or more UTF-16 characters. It starts
    /// as the whole document, as DocumentRange does.
    /// </summary>
    private sealed class FakeRange(IReadOnlyList<string> characters) : UiaTextPageReader
    {
        private int _start;
        private int _end = characters.Count;

        /// <summary>The maxLength of every GetText call.</summary>
        public List<int> TextLimits { get; } = [];

        protected override int MoveStart(int count)
        {
            var moved = Math.Min(count, characters.Count - _start);
            _start += moved;
            _end = Math.Max(_end, _start);
            return moved;
        }

        protected override void Collapse() => _end = _start;

        protected override int MoveEnd(int count)
        {
            var moved = Math.Min(count, characters.Count - _end);
            _end += moved;
            return moved;
        }

        protected override string GetText(int maxLength)
        {
            TextLimits.Add(maxLength);
            var text = string.Concat(characters.Skip(_start).Take(_end - _start));
            return maxLength >= 0 && text.Length > maxLength ? text[..maxLength] : text;
        }

        protected override bool HasCharacterAfterEnd() => _end < characters.Count;
    }

    private static List<string> OneEach(string text) => text.Select(c => c.ToString()).ToList();

    /// <summary>A document whose provider counts each "\r\n" as one character.</summary>
    private static List<string> LineBreakIsOne(string text) =>
        text.Replace("\r\n", "\n").Select(c => c == '\n' ? "\r\n" : c.ToString()).ToList();

    [Fact]
    public void Read_ReturnsThePageFromTheOffset_AndProbesOnceForMore()
    {
        // Act
        var page = new FakeRange(OneEach("abcdefghij")).Read(2, 3);

        // Assert
        page.Should().Be(new UiaTextPage("cde", 2, 3, HasMore: true));
    }

    [Fact]
    public void Read_AtTheEndOfTheDocument_KnowsThereIsNoMore()
    {
        var page = new FakeRange(OneEach("abcdefghij")).Read(8, 5);

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
            var range = new FakeRange(characters);
            pages.Add(range.Read(offset, 50));
            limits.AddRange(range.TextLimits);
        }

        // Assert
        string.Concat(pages.Select(p => p.Text)).Should().Be(document);
        pages.Should().OnlyContain(p => p.Text.Length <= 50 && p.Text.Length > 0 && !p.Cut);
        limits.Should().OnlyContain(limit => limit > 0 && limit <= 100, "the range's text is read with a limit, never in full");
    }

    [Fact]
    public void Read_WhenOnlyAFewCharactersAreWide_KeepsThePageNearlyFull()
    {
        // Arrange - one line break per 100 characters: the page is made shorter in proportion, not halved
        var document = string.Concat(Enumerable.Repeat(new string('x', 98) + "\r\n", 100));

        // Act
        var page = new FakeRange(LineBreakIsOne(document)).Read(0, 1_000);

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
        var page = new FakeRange(characters).Read(1, 1);

        // Assert - the page ends after the line break, so the next one starts at "b"
        page.Should().Be(new UiaTextPage("\r", 1, 1, HasMore: true, Cut: true));
    }

    [Fact]
    public void Read_OfAProviderWithoutACharacterUnit_ReadsAtMostTwiceThePage()
    {
        // Arrange - a provider whose "character" is the whole document, as one that falls back to a larger unit
        var range = new FakeRange([new string('x', 1_000_000)]);

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
        var range = new FakeRange([emoji + emoji + emoji, "z"]);

        // Act
        var page = range.Read(0, 3);

        // Assert
        page.Text.Should().Be(emoji);
        page.Cut.Should().BeTrue();
        page.Length.Should().Be(1);
    }
}
