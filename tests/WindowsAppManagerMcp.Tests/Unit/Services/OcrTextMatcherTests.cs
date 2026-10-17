using System.Text.RegularExpressions;

namespace WindowsAppManagerMcp.Tests.Unit.Services;

/// <summary>find_text_on_screen's comparison of its text with the lines OCR read (roadmap M6.3).</summary>
public class OcrTextMatcherTests
{
    private static OcrTextMatcher Matcher(string text, OcrTextMatch match = OcrTextMatch.Contains, bool caseSensitive = false) =>
        OcrTextMatcher.TryCreate(text, match, caseSensitive, out var error) ?? throw new InvalidOperationException(error);

    /// <summary>The [start, end) ranges of the words of a line whose words are separated by single spaces.</summary>
    private static (int Start, int End)[] Words(string line)
    {
        var ranges = new List<(int, int)>();
        var start = -1;
        for (var i = 0; i <= line.Length; i++)
        {
            var space = i == line.Length || line[i] == ' ';
            if (!space && start < 0)
            {
                start = i;
            }
            else if (space && start >= 0)
            {
                ranges.Add((start, i));
                start = -1;
            }
        }

        return ranges.ToArray();
    }

    private static IReadOnlyList<string> Find(OcrTextMatcher matcher, string line) =>
        matcher.Find(line, Words(line)).Select(m => line[m.Start..m.End]).ToList();

    [Fact]
    public void Contains_FindsEveryPlace_IgnoringCase_EvenInsideWords()
    {
        var found = Find(Matcher("save"), "Save as, autosave and SAVE");

        found.Should().Equal("Save", "save", "SAVE");
    }

    [Fact]
    public void Contains_CaseSensitive_KeepsCase()
    {
        Find(Matcher("Save", caseSensitive: true), "Save as, autosave and SAVE").Should().Equal("Save");
    }

    [Fact]
    public void Contains_TreatsRunsOfWhitespaceAsOneSpace_AndReturnsTheRangeOfTheLineAsOcrReadIt()
    {
        var line = "Canvas   alpha  4711";

        var found = Find(Matcher("  canvas alpha\t4711 "), line);

        found.Should().Equal("Canvas   alpha  4711");
    }

    [Fact]
    public void Exact_MatchesWholeWordsOnly()
    {
        var matcher = Matcher("save as", OcrTextMatch.Exact);

        Find(matcher, "Save As").Should().Equal("Save As");
        Find(matcher, "Autosave Assets").Should().BeEmpty();
        Find(matcher, "Save Assets").Should().BeEmpty("'Assets' is not the word 'as'");
    }

    [Fact]
    public void Exact_SkipsAPartialHit_AndFindsTheWholeWordAfterIt()
    {
        Find(Matcher("bravo", OcrTextMatch.Exact), "Bravos bravo").Should().Equal("bravo");
    }

    [Fact]
    public void Exact_UsesTheWordRanges_ForLinesWithoutSpaces()
    {
        // Japanese is read without spaces: two words, 保存 and する
        var line = "保存する";
        (int, int)[] words = [(0, 2), (2, 4)];

        var exact = Matcher("保存", OcrTextMatch.Exact).Find(line, words);
        var partial = Matcher("存す", OcrTextMatch.Exact).Find(line, words);

        exact.Should().Equal((0, 2));
        partial.Should().BeEmpty();
    }

    [Fact]
    public void Regex_RunsOnTheLineAsRead_IgnoringCaseUnlessCaseSensitive()
    {
        Find(Matcher(@"alpha\s+\d+", OcrTextMatch.Regex), "Canvas ALPHA 4711, alpha 42").Should().Equal("ALPHA 4711", "alpha 42");
        Find(Matcher(@"alpha\s+\d+", OcrTextMatch.Regex, caseSensitive: true), "Canvas ALPHA 4711, alpha 42").Should().Equal("alpha 42");
    }

    [Fact]
    public void Regex_SkipsEmptyMatches()
    {
        Find(Matcher("x*", OcrTextMatch.Regex), "abc").Should().BeEmpty();
    }

    [Fact]
    public void Regex_ThatTakesTooLong_Throws()
    {
        var matcher = Matcher("^(a+)+$", OcrTextMatch.Regex);
        var line = new string('a', 5000) + "!";

        var act = () => matcher.Find(line, Words(line));

        act.Should().Throw<RegexMatchTimeoutException>();
    }

    [Theory]
    [InlineData("", OcrTextMatch.Contains)]
    [InlineData("   ", OcrTextMatch.Exact)]
    [InlineData("", OcrTextMatch.Regex)]
    [InlineData("(unclosed", OcrTextMatch.Regex)]
    public void TryCreate_RefusesNothingToMatch_AndAnInvalidRegex(string text, OcrTextMatch match)
    {
        var matcher = OcrTextMatcher.TryCreate(text, match, caseSensitive: false, out var error);

        matcher.Should().BeNull();
        error.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public void Find_ReturnsMatchesInOrder_WithoutOverlap()
    {
        Matcher("aa").Find("aaaa", [(0, 4)]).Should().Equal((0, 2), (2, 4));
    }
}
