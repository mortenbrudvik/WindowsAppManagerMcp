namespace WindowsAppManagerMcp.Tests.Unit.Services;

/// <summary>
/// Unit tests for CdpTargetMatcher, which binds a browser window to the CDP tab it shows.
/// The matcher must never guess: a wrong match returns another tab's data with Success=true.
/// </summary>
public class CdpTargetMatcherTests
{
    private static CdpTarget Page(string id, string title, string url) =>
        new(Id: id, Type: "page", Title: title, Url: url, WebSocketDebuggerUrl: $"ws://127.0.0.1:9222/devtools/page/{id}", Port: 9222);

    [Fact]
    public void Match_ReturnsTabWhoseTitleIsShownByWindow()
    {
        var targets = new[]
        {
            Page("A", "Google", "https://www.google.com/"),
            Page("B", "GitHub", "https://github.com/")
        };

        var result = CdpTargetMatcher.Match("GitHub - Google Chrome", targets);

        result.Success.Should().BeTrue();
        result.Value!.Id.Should().Be("B");
    }

    [Fact]
    public void Match_DoesNotMatchTabTitleThatIsOnlyASubstringOfTheWindowTitle()
    {
        // 'GitHub - Google Chrome' contains 'Google', but the window is not showing the Google tab.
        var targets = new[] { Page("A", "Google", "https://www.google.com/") };

        var result = CdpTargetMatcher.Match("GitHub - Google Chrome", targets);

        result.Success.Should().BeFalse();
        result.ErrorCode.Should().Be(BrowserErrorCode.CdpTargetNotFound);
    }

    [Fact]
    public void Match_DoesNotMatchWhenTabTitleIsPrefixWithoutSeparator()
    {
        var targets = new[] { Page("A", "Google", "https://www.google.com/") };

        var result = CdpTargetMatcher.Match("Googleplex - Google Chrome", targets);

        result.ErrorCode.Should().Be(BrowserErrorCode.CdpTargetNotFound);
    }

    [Fact]
    public void Match_NeverMatchesTabWithEmptyTitle()
    {
        // An empty title used to match every window ("x".Contains("") is true).
        var targets = new[] { Page("A", "", "https://loading.example/") };

        var result = CdpTargetMatcher.Match("Inbox - Google Chrome", targets);

        result.ErrorCode.Should().Be(BrowserErrorCode.CdpTargetNotFound);
    }

    [Theory]
    [InlineData("about:blank")]
    [InlineData("file:///C:/temp/page.html")]
    [InlineData("data:text/html,hello")]
    public void Match_NeverMatchesOnEmptyUrlHost(string url)
    {
        // These URLs have an empty host, which used to match every window title.
        var targets = new[] { Page("A", "Some other page", url) };

        var result = CdpTargetMatcher.Match("Inbox - Google Chrome", targets);

        result.ErrorCode.Should().Be(BrowserErrorCode.CdpTargetNotFound);
    }

    [Fact]
    public void Match_DoesNotMatchOnUrlHostInWindowTitle()
    {
        // The window title mentions github.com, but no tab has the title the window shows.
        var targets = new[] { Page("A", "Sign in", "https://github.com/login") };

        var result = CdpTargetMatcher.Match("Notes about github.com - Google Chrome", targets);

        result.ErrorCode.Should().Be(BrowserErrorCode.CdpTargetNotFound);
    }

    [Fact]
    public void Match_PrefersMostSpecificTitle_WhenShorterTitleIsAlsoAPrefix()
    {
        var targets = new[]
        {
            Page("A", "GitHub", "https://github.com/"),
            Page("B", "GitHub - Pull requests", "https://github.com/pulls")
        };

        var result = CdpTargetMatcher.Match("GitHub - Pull requests - Google Chrome", targets);

        result.Success.Should().BeTrue();
        result.Value!.Id.Should().Be("B");
    }

    [Fact]
    public void Match_ReturnsAmbiguous_WhenSeveralTabsHaveTheSameTitle()
    {
        var targets = new[]
        {
            Page("A", "Inbox", "https://mail.example/#inbox"),
            Page("B", "Inbox", "https://mail.example/#inbox")
        };

        var result = CdpTargetMatcher.Match("Inbox - Google Chrome", targets);

        result.Success.Should().BeFalse();
        result.ErrorCode.Should().Be(BrowserErrorCode.CdpTargetAmbiguous);
        result.Error.Should().Contain("A").And.Contain("B");
    }

    [Fact]
    public void Match_Ambiguous_SaysToPassOneOfTheTabsAsTargetId()
    {
        // Roadmap M5.2: the listed ids are what get_browser_url and get_browser_content take as targetId
        var targets = new[]
        {
            Page("A", "Inbox", "https://mail.example/a"),
            Page("B", "Inbox", "https://mail.example/b")
        };

        var result = CdpTargetMatcher.Match("Inbox - Google Chrome", targets);

        result.Error.Should().Contain("targetId A (https://mail.example/a)")
            .And.Contain("pass one as targetId to get_browser_url or get_browser_content, with the same window and cdpPort");
    }

    [Fact]
    public void Match_HandlesEdgeTitleWithProfileMorePagesAndZeroWidthSpace()
    {
        // Edge titles look like '<title> and N more pages - <Profile> - Microsoft\u200B Edge'.
        var targets = new[]
        {
            Page("A", "Docs", "https://docs.example/"),
            Page("B", "Other", "https://other.example/")
        };

        var result = CdpTargetMatcher.Match("Docs and 2 more pages - Personal - Microsoft\u200B Edge", targets);

        result.Success.Should().BeTrue();
        result.Value!.Id.Should().Be("A");
    }

    [Fact]
    public void Match_HandlesEdgeTitleWithSingleMorePage()
    {
        var targets = new[] { Page("A", "Docs", "https://docs.example/") };

        var result = CdpTargetMatcher.Match("Docs and 1 more page - Work - Microsoft Edge", targets);

        result.Success.Should().BeTrue();
        result.Value!.Id.Should().Be("A");
    }

    [Fact]
    public void Match_IgnoresFormatCharactersAndExtraWhitespaceInTitles()
    {
        var targets = new[] { Page("A", "  Quarterly\u200B   report ", "https://example.com/report") };

        var result = CdpTargetMatcher.Match("Quarterly report - Google Chrome", targets);

        result.Success.Should().BeTrue();
        result.Value!.Id.Should().Be("A");
    }

    [Fact]
    public void Match_IsCaseSensitive()
    {
        var targets = new[] { Page("A", "inbox", "https://mail.example/") };

        var result = CdpTargetMatcher.Match("Inbox - Google Chrome", targets);

        result.ErrorCode.Should().Be(BrowserErrorCode.CdpTargetNotFound);
    }

    [Fact]
    public void Match_AcceptsWindowTitleWithoutBrowserSuffix()
    {
        var targets = new[] { Page("A", "Inbox", "https://mail.example/") };

        var result = CdpTargetMatcher.Match("Inbox", targets);

        result.Success.Should().BeTrue();
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void Match_ReturnsNotFound_WhenWindowTitleIsEmpty(string? windowTitle)
    {
        var targets = new[] { Page("A", "Inbox", "https://mail.example/") };

        var result = CdpTargetMatcher.Match(windowTitle, targets);

        result.ErrorCode.Should().Be(BrowserErrorCode.CdpTargetNotFound);
    }

    [Fact]
    public void Match_ReturnsNotFound_WhenThereAreNoTabs()
    {
        var result = CdpTargetMatcher.Match("Inbox - Google Chrome", Array.Empty<CdpTarget>());

        result.ErrorCode.Should().Be(BrowserErrorCode.CdpTargetNotFound);
        result.Error.Should().Contain("no open tabs");
    }

    [Fact]
    public void Match_ReportsWindowTitleInNotFoundError()
    {
        var targets = new[] { Page("A", "Inbox", "https://mail.example/") };

        var result = CdpTargetMatcher.Match("Calendar - Google Chrome", targets);

        result.Error.Should().Contain("Calendar - Google Chrome");
    }

    [Theory]
    [InlineData("Microsoft\u200B Edge", "Microsoft Edge")]
    [InlineData("  a \t b\u00A0 c  ", "a b c")]
    [InlineData("\u200Etitle\u200F", "title")]
    [InlineData(null, "")]
    public void NormalizeTitle_RemovesFormatCharactersAndCollapsesWhitespace(string? input, string expected)
    {
        CdpTargetMatcher.NormalizeTitle(input).Should().Be(expected);
    }
}
