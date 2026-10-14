namespace WindowsAppManagerMcp.Tests.Unit.Services;

/// <summary>
/// Unit tests for BrowserUiaReader, the UI Automation reads of get_browser_url and get_browser_content on the IUiaTree seam
/// (roadmap M3.F24), over an in-memory tree. No real browser or UI Automation client is read.
/// </summary>
public class BrowserUiaReaderTests
{
    private static readonly UiaRect AddressBarBounds = new(200, 40, 800, 28);

    private readonly FakeUiaTree _tree = new();

    private BrowserUiaReader CreateSut() => new(_tree);

    private static FakeUiaNode Window(params FakeUiaNode[] children) =>
        new FakeUiaNode("Window", "Example - Google Chrome").With(children);

    private static FakeUiaNode Edit(string name, string? value, UiaRect? bounds = null, bool isPassword = false) =>
        new FakeUiaNode("Edit", name) { Bounds = bounds ?? new UiaRect(0, 0, 100, 30), IsPassword = isPassword, ActionPatterns = ["Value"] }
            .WithPattern<IUiaValuePattern>(new FakeValuePattern(value));

    #region get_browser_url

    [Theory]
    [InlineData("chrome")]
    [InlineData("edge")]
    [InlineData("Brave")]
    [InlineData("vivaldi")]
    [InlineData("opera")]
    [InlineData("chromium")]
    public void ReadUrl_Chromium_ReadsTheAddressBarByItsName(string browserType)
    {
        // Arrange
        var window = Window(
            new FakeUiaNode("Pane", "Google Chrome").With(
                new FakeUiaNode("ToolBar").With(Edit("Address and search bar", "https://example.com/"))));

        // Act
        var url = CreateSut().ReadUrl(window, browserType, CancellationToken.None);

        // Assert
        url.Should().Be("https://example.com/");
    }

    [Fact]
    public void ReadUrl_Chromium_FallsBackToEdgesAddressBarName()
    {
        var window = Window(new FakeUiaNode("ToolBar").With(Edit("Address bar", "https://edge.example/")));

        var url = CreateSut().ReadUrl(window, "edge", CancellationToken.None);

        url.Should().Be("https://edge.example/");
    }

    [Fact]
    public void ReadUrl_Chromium_FallsBackToAUrlLikeEditBoxInTheToolbar()
    {
        // Arrange - no edit box has a known name; a wide, short one with a URL-like value is taken
        var window = Window(new FakeUiaNode("ToolBar").With(
            Edit("Tiny", "tiny.example", new UiaRect(0, 0, 150, 28)),
            Edit("Find", "no dots here", AddressBarBounds),
            Edit("Omnibox", "example.com/path", AddressBarBounds)));

        // Act
        var url = CreateSut().ReadUrl(window, "chrome", CancellationToken.None);

        // Assert
        url.Should().Be("example.com/path");
    }

    [Fact]
    public void ReadUrl_Chromium_DoesNotFallBack_WhenTheNamedAddressBarHasNoValue()
    {
        // As the old wrapper: the bar found by name decides, even when it is empty
        var window = Window(new FakeUiaNode("ToolBar").With(
            Edit("Address and search bar", ""),
            Edit("Omnibox", "example.com/path", AddressBarBounds)));

        var url = CreateSut().ReadUrl(window, "chrome", CancellationToken.None);

        url.Should().BeEmpty();
    }

    [Fact]
    public void ReadUrl_Firefox_ReadsTheUrlBarByItsAutomationId()
    {
        var window = Window(new FakeUiaNode("ToolBar").With(
            new FakeUiaNode("Edit", "Search with Google or enter address") { AutomationId = "urlbar-input" }
                .WithPattern<IUiaValuePattern>(new FakeValuePattern("https://mozilla.org/"))));

        var url = CreateSut().ReadUrl(window, "firefox", CancellationToken.None);

        url.Should().Be("https://mozilla.org/");
        _tree.Finds.Should().Contain("Example - Google Chrome:Descendants");
    }

    [Fact]
    public void ReadUrl_Firefox_FallsBackToTheUrlBarsContainer_ThenToItsName()
    {
        // Arrange
        var byContainer = Window(new FakeUiaNode("Group") { AutomationId = "urlbar" }
            .WithPattern<IUiaValuePattern>(new FakeValuePattern("https://container.example/")));
        var byName = Window(new FakeUiaNode("ToolBar").With(Edit("Search or enter address", "https://named.example/")));

        // Act
        var first = CreateSut().ReadUrl(byContainer, "firefox", CancellationToken.None);
        var second = CreateSut().ReadUrl(byName, "firefox", CancellationToken.None);

        // Assert
        first.Should().Be("https://container.example/");
        second.Should().Be("https://named.example/");
    }

    [Fact]
    public void ReadUrl_UnknownBrowser_TriesTheChromiumLookup_ThenTheFirefoxOne()
    {
        var window = Window(new FakeUiaNode("Edit", "Location") { AutomationId = "urlbar-input" }
            .WithPattern<IUiaValuePattern>(new FakeValuePattern("https://other.example/")));

        var url = CreateSut().ReadUrl(window, "unknown", CancellationToken.None);

        url.Should().Be("https://other.example/");
    }

    [Fact]
    public void ReadUrl_ReadsTheTextPattern_ThenTheName_WhenTheBarHasNoValuePattern()
    {
        // Arrange
        var withText = Window(new FakeUiaNode("Edit", "Address and search bar")
            .WithPattern<IUiaTextPattern>(new FakeTextPattern("https://text.example/")));
        var withName = Window(new FakeUiaNode("Custom", "Address and search bar"));

        // Act
        var fromText = CreateSut().ReadUrl(withText, "chrome", CancellationToken.None);
        var fromName = CreateSut().ReadUrl(withName, "chrome", CancellationToken.None);

        // Assert - as the old wrapper did (roadmap M5.7 stops using the name as a value)
        fromText.Should().Be("https://text.example/");
        fromName.Should().Be("Address and search bar");
    }

    [Fact]
    public void ReadUrl_SearchesTheNamesTenLevelsBelowTheWindow_AndNoDeeper()
    {
        // Arrange - the window is level 0; a bar at level 10 is found, one at level 11 is not
        FakeUiaNode Nest(int levels, FakeUiaNode leaf)
        {
            var node = leaf;
            for (var i = 0; i < levels; i++)
            {
                node = new FakeUiaNode("Pane", $"Level {levels - i}").With(node);
            }

            return node;
        }

        var reachable = Window(Nest(9, Edit("Address and search bar", "https://deep.example/")));
        var tooDeep = Window(Nest(10, Edit("Address and search bar", "https://deeper.example/")));

        // Act
        var found = CreateSut().ReadUrl(reachable, "chrome", CancellationToken.None);
        var missed = CreateSut().ReadUrl(tooDeep, "chrome", CancellationToken.None);

        // Assert
        found.Should().Be("https://deep.example/");
        missed.Should().BeNull();
    }

    [Fact]
    public void ReadUrl_SkipsAnElementThatCannotBeRead_AndGoesOnWithItsSiblings()
    {
        // Arrange - the level's cached read fails because of the broken element, so it is read one element at a time
        var window = Window(new FakeUiaNode("ToolBar").With(
            new FakeUiaNode("Button", "Broken") { ReadException = FakeUiaTree.Vanished() },
            Edit("Address and search bar", "https://example.com/")));

        // Act
        var url = CreateSut().ReadUrl(window, "chrome", CancellationToken.None);

        // Assert
        url.Should().Be("https://example.com/");
    }

    [Fact]
    public void ReadUrl_NeverReadsTheValueOfAPasswordField()
    {
        // Arrange - a URL-like password field in the toolbar, and one whose name matches
        var window = Window(new FakeUiaNode("ToolBar").With(
            Edit("Secret", "hunter2.example", AddressBarBounds, isPassword: true)));
        var named = Window(Edit("Address and search bar", "hunter2.example", isPassword: true));

        // Act
        var fromToolbar = CreateSut().ReadUrl(window, "chrome", CancellationToken.None);
        var fromName = CreateSut().ReadUrl(named, "chrome", CancellationToken.None);

        // Assert
        fromToolbar.Should().BeNull();
        fromName.Should().Be("Address and search bar");
        _tree.Reads.Should().NotContain(r => r.StartsWith("Pattern:IUiaValuePattern", StringComparison.Ordinal));
    }

    [Fact]
    public void ReadUrl_ReturnsNull_WhenTheWindowCannotBeRead()
    {
        var window = Window(Edit("Address and search bar", "https://example.com/"));
        window.ReadException = FakeUiaTree.Vanished();
        window.FindAllException = FakeUiaTree.Vanished();

        var url = CreateSut().ReadUrl(window, "chrome", CancellationToken.None);

        url.Should().BeNull();
    }

    [Fact]
    public void ReadUrl_StopsOnceTheTokenIsCancelled()
    {
        // Arrange
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        var window = Window(new FakeUiaNode("ToolBar").With(Edit("Address and search bar", "https://example.com/")));

        // Act
        var act = () => CreateSut().ReadUrl(window, "chrome", cancelled.Token);

        // Assert
        act.Should().Throw<OperationCanceledException>();
    }

    #endregion

    #region get_browser_content

    [Fact]
    public void ReadContent_ReadsTheDocumentsTextPattern()
    {
        // Arrange
        var text = new FakeTextPattern("The whole article.");
        var window = Window(new FakeUiaNode("Pane").With(
            new FakeUiaNode("Document", "Example").WithPattern<IUiaTextPattern>(text).With(new FakeUiaNode("Text", "Ignored"))));

        // Act
        var content = CreateSut().ReadContent(window, maxLength: null, CancellationToken.None);

        // Assert
        content.Should().Be("The whole article.");
        _tree.ReadNodes.Should().NotContain("Ignored", "the Text pattern reads the document in one call");
    }

    [Fact]
    public void ReadContent_ReadsOnlyMaxLengthCharactersOfTheTextPattern()
    {
        var window = Window(new FakeUiaNode("Document").WithPattern<IUiaTextPattern>(new FakeTextPattern("abcdefghij")));

        var content = CreateSut().ReadContent(window, maxLength: 4, CancellationToken.None);

        content.Should().Be("abcd");
    }

    [Fact]
    public void ReadContent_TakesTheFirstDocument_InTreeOrder()
    {
        var window = Window(
            new FakeUiaNode("Document", "Page").WithPattern<IUiaTextPattern>(new FakeTextPattern("page")).With(
                new FakeUiaNode("Document", "Frame").WithPattern<IUiaTextPattern>(new FakeTextPattern("frame"))));

        var content = CreateSut().ReadContent(window, maxLength: null, CancellationToken.None);

        content.Should().Be("page");
    }

    [Fact]
    public void ReadContent_WalksTheDocument_WhenItHasNoTextPattern()
    {
        // Arrange - names of text, edit boxes and links, and values that differ from the name
        var window = Window(new FakeUiaNode("Document", "Example").With(
            new FakeUiaNode("Text", "  Heading"),
            new FakeUiaNode("Hyperlink", "Docs") { ActionPatterns = ["Invoke", "Value"] }
                .WithPattern<IUiaValuePattern>(new FakeValuePattern("https://example.com/docs")),
            Edit("Search", "query"),
            Edit("Same", "Same"),
            new FakeUiaNode("Button", "Not text"),
            new FakeUiaNode("Text", "   "),
            new FakeUiaNode("Group").With(new FakeUiaNode("Text", "Nested"))));

        // Act
        var content = CreateSut().ReadContent(window, maxLength: null, CancellationToken.None);

        // Assert
        content.Should().Be(string.Join(Environment.NewLine,
            "Heading", "Docs", "https://example.com/docs", "Search", "query", "Same", "Nested"));
    }

    [Fact]
    public void ReadContent_NeverReadsTheValueOfAPasswordField()
    {
        var window = Window(new FakeUiaNode("Document").With(
            new FakeUiaNode("Text", "Sign in"),
            Edit("Password", "hunter2", isPassword: true)));

        var content = CreateSut().ReadContent(window, maxLength: null, CancellationToken.None);

        content.Should().Be($"Sign in{Environment.NewLine}Password");
        _tree.Reads.Should().NotContain("Pattern:IUiaValuePattern:Password");
    }

    [Fact]
    public void ReadContent_DoesNotReadTheValueOfAnEditBox_WhosePasswordFlagCannotBeRead()
    {
        var window = Window(new FakeUiaNode("Document").With(
            new FakeUiaNode("Edit", "Field") { ActionPatterns = ["Value"], IdentityException = FakeUiaTree.Vanished() }
                .WithPattern<IUiaValuePattern>(new FakeValuePattern("maybe secret"))));

        var content = CreateSut().ReadContent(window, maxLength: null, CancellationToken.None);

        content.Should().Be("Field");
    }

    [Fact]
    public void ReadContent_StopsWalking_OnceMaxLengthCharactersAreRead()
    {
        // Arrange
        var window = Window(new FakeUiaNode("Document").With(
            new FakeUiaNode("Text", "First line"),
            new FakeUiaNode("Group").With(new FakeUiaNode("Text", "Second line")),
            new FakeUiaNode("Group", "Later").With(new FakeUiaNode("Text", "Never read"))));

        // Act
        var content = CreateSut().ReadContent(window, maxLength: 14, CancellationToken.None);

        // Assert - the same text as a walk of the whole page cut afterwards
        content.Should().Be($"First line{Environment.NewLine}Se"[..14]);
        _tree.ReadNodes.Should().NotContain("Never read");
        _tree.ChildListings.Should().NotContain("Later");
    }

    [Fact]
    public void ReadContent_ReadsAWideLevelOneElementAtATime()
    {
        // Arrange - more children than one cached read takes
        var items = Enumerable.Range(0, UIElementService.CachedLevelLimit + 1)
            .Select(i => new FakeUiaNode("Text", $"Item {i}"))
            .ToArray();
        var document = new FakeUiaNode("Document", "List").With(items);
        var window = Window(document);

        // Act
        var content = CreateSut().ReadContent(window, maxLength: null, CancellationToken.None);

        // Assert
        content!.Split(Environment.NewLine).Should().HaveCount(items.Length);
        _tree.CachedFetches.Should().NotContain("List");
    }

    [Fact]
    public void ReadContent_WalksTwentyLevelsBelowTheDocument_AndNoDeeper()
    {
        // Arrange - the document is level 0
        var leaf = new FakeUiaNode("Text", "Level 21");
        var node = new FakeUiaNode("Group").With(new FakeUiaNode("Text", "Level 20"), new FakeUiaNode("Group").With(leaf));
        for (var level = 18; level >= 1; level--)
        {
            node = new FakeUiaNode("Group").With(node);
        }

        var window = Window(new FakeUiaNode("Document").With(node));

        // Act
        var content = CreateSut().ReadContent(window, maxLength: null, CancellationToken.None);

        // Assert
        content.Should().Be("Level 20");
    }

    [Fact]
    public void ReadContent_UsesTheLargestPane_WhenThereIsNoDocument()
    {
        var window = Window(
            new FakeUiaNode("Pane", "Sidebar") { Bounds = new UiaRect(0, 0, 250, 900) }.With(new FakeUiaNode("Text", "Side")),
            new FakeUiaNode("Pane", "Page") { Bounds = new UiaRect(250, 100, 1200, 800) }.With(new FakeUiaNode("Text", "Main")),
            new FakeUiaNode("Pane", "Thin") { Bounds = new UiaRect(0, 0, 3000, 150) }.With(new FakeUiaNode("Text", "Strip")));

        var content = CreateSut().ReadContent(window, maxLength: null, CancellationToken.None);

        content.Should().Be("Main");
    }

    [Fact]
    public void ReadContent_ReturnsNull_WhenThereIsNoContentArea_OrNoText()
    {
        // Arrange
        var noArea = Window(new FakeUiaNode("ToolBar").With(new FakeUiaNode("Text", "Toolbar text")));
        var empty = Window(new FakeUiaNode("Document").With(new FakeUiaNode("Button", "Only a button")));

        // Act
        var first = CreateSut().ReadContent(noArea, maxLength: null, CancellationToken.None);
        var second = CreateSut().ReadContent(empty, maxLength: null, CancellationToken.None);

        // Assert
        first.Should().BeNull();
        second.Should().BeNull();
    }

    [Fact]
    public void ReadContent_ReturnsNull_WhenTheSearchForTheDocumentFails()
    {
        var window = Window(new FakeUiaNode("Document").WithPattern<IUiaTextPattern>(new FakeTextPattern("text")));
        window.FindAllException = new TimeoutException("The provider did not answer.");

        var content = CreateSut().ReadContent(window, maxLength: null, CancellationToken.None);

        content.Should().BeNull();
    }

    [Fact]
    public void ReadContent_StopsOnceTheTokenIsCancelled()
    {
        // Arrange
        using var cancellation = new CancellationTokenSource();
        var window = Window(new FakeUiaNode("Document").With(
            new FakeUiaNode("Text", "First") { OnRead = cancellation.Cancel },
            new FakeUiaNode("Group").With(new FakeUiaNode("Text", "Second"))));

        // Act
        var act = () => CreateSut().ReadContent(window, maxLength: null, cancellation.Token);

        // Assert
        act.Should().Throw<OperationCanceledException>();
    }

    #endregion
}
