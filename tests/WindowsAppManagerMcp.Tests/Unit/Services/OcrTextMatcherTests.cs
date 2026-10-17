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

    [Theory]
    [InlineData("(OK)", "OK")]
    [InlineData("OK.", "OK")]
    [InlineData("Save, then quit", "Save")]
    [InlineData("\"Save as\" dialog", "Save as")]
    public void Exact_IgnoresThePunctuationOcrReadsAsPartOfAWord(string line, string expected)
    {
        Find(Matcher(expected, OcrTextMatch.Exact), line).Should().Equal(expected);
    }

    [Fact]
    public void Exact_StillNeedsAWholeWord_NextToPunctuation()
    {
        Find(Matcher("OK", OcrTextMatch.Exact), "(OKAY)").Should().BeEmpty();
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
    public void ContainsAndExact_IgnoreTheSpacesOcrPutsBetweenJapaneseWords()
    {
        // Windows OCR joins the words of a Japanese or Chinese line with spaces: "保存 する" for 保存する
        var line = "保存 する";
        (int, int)[] words = [(0, 2), (3, 5)];

        var contains = Matcher("保存する").Find(line, words);
        var exact = Matcher("保存する", OcrTextMatch.Exact).Find(line, words);
        var partial = Matcher("存す", OcrTextMatch.Exact).Find(line, words);

        contains.Should().Equal((0, 5));
        exact.Should().Equal((0, 5));
        partial.Should().BeEmpty("存す starts inside the word 保存");
    }

    [Theory]
    [InlineData("Glyph quartz 471 1 km", "4711km", "471 1 km")]
    [InlineData("Glyph quartz 471 1 km", "4711 km", "471 1 km")]
    [InlineData("Small print 314 1pt", "3141pt", "314 1pt")]
    [InlineData("Total 1 2 3", "123", "1 2 3")]
    public void ContainsAndExact_IgnoreTheSpacesOcrPutsInsideANumber_AndBeforeItsUnit(string line, string text, string expected)
    {
        // Real Windows OCR reads "4711km" as "471 1 km" and "3141pt" as "314 1pt" (roadmap M6.3 desktop run)
        Find(Matcher(text), line).Should().Equal(expected);
        Find(Matcher(text, OcrTextMatch.Exact), line).Should().Equal(expected);
    }

    [Fact]
    public void ASpaceBetweenWordsAndNumbers_OtherwiseStillCounts()
    {
        Find(Matcher("Windows 11 Pro"), "Windows 11 Pro").Should().Equal("Windows 11 Pro");
        Find(Matcher("Windows11"), "Windows 11 Pro").Should().BeEmpty("a space after a word and before a number counts");
        Find(Matcher("4711"), "Glyph quartz 471 1 km").Should().Equal("471 1");
    }

    [Fact]
    public void Regex_RunsOnTheLineAsRead_WithItsSpacesInsideNumbers()
    {
        Find(Matcher(@"\d{4}km", OcrTextMatch.Regex), "Glyph quartz 471 1 km").Should().BeEmpty();
        Find(Matcher(@"\d(\s?\d){3}\s?km", OcrTextMatch.Regex), "Glyph quartz 471 1 km").Should().Equal("471 1 km");
    }

    [Fact]
    public void ASpaceBetweenLatinAndJapanese_StillCounts()
    {
        Find(Matcher("Save保存"), "Save 保存").Should().BeEmpty();
        Find(Matcher("Save 保存"), "Save 保存").Should().Equal("Save 保存");
    }

    [Fact]
    public void Regex_ThatUsesUpTheBudgetOfTheWholeSearch_Throws()
    {
        // 100 ms per line, but a pattern that takes 90 ms on each of 300 lines must not hold the call for half a minute
        var matcher = OcrTextMatcher.TryCreate("a", OcrTextMatch.Regex, false, out _, regexBudget: TimeSpan.Zero)!;

        var act = () => matcher.Find("a line", Words("a line"));

        act.Should().Throw<RegexMatchTimeoutException>();
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
