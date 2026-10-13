namespace WindowsAppManagerMcp.Tests.Unit.Services;

/// <summary>
/// read_element_text's service (roadmap M3.9) over an in-memory UI Automation tree: the sources in their order, paging,
/// password fields, refs and criteria, and the failures. No real window is read.
/// </summary>
public class ElementReadServiceTextTests
{
    private static readonly nint Handle = 0x7300;
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    private readonly FakeUiaTree _tree = new();
    private readonly InlineUiaWorker _worker = new();
    private readonly Mock<INativeWindowWrapper> _windows = new();
    private readonly Mock<IAccessibilityWarmup> _warmup = new();
    private readonly ElementRegistry _registry;
    private readonly FakeUiaNode _window = new("Window", "Notes");

    public ElementReadServiceTextTests()
    {
        _windows.Setup(w => w.IsWindow(Handle)).Returns(true);
        _windows.Setup(w => w.GetWindowThreadProcessId(Handle)).Returns(300u);
        _windows.Setup(w => w.RespondsWithin(Handle, It.IsAny<uint>())).Returns(true);
        _registry = new ElementRegistry(_tree, _worker, _windows.Object, ElementRegistry.DefaultCapacity, 1, TimeProvider.System);
        _tree.WithWindow(Handle, _window);
    }

    private ElementReadService CreateSut() =>
        new(_tree, _worker, _registry, new ElementSearchService(_tree, _worker, _registry, _windows.Object), _windows.Object, _warmup.Object);

    /// <summary>Puts <paramref name="element"/> in the window and gives it a ref, as an earlier read would.</summary>
    private string Add(FakeUiaNode element)
    {
        _window.With(element);
        return _registry.Register(Handle, _tree.ReadElement(element), new ElementOrigin(_registry.NextGeneration()))!;
    }

    private Task<ElementTextResult> Read(string elementRef, int offset = 0, int maxLength = 20_000) =>
        CreateSut().ReadTextAsync(new ElementTextRequest(elementRef, null, offset, maxLength, Timeout));

    private static FakeUiaNode Document(string text, bool lineBreakIsOneCharacter = false) =>
        new FakeUiaNode("Document", "Body") { OtherPatterns = UiaPatterns.Text }
            .WithPattern<IUiaTextPattern>(new FakeTextPattern(text) { LineBreakIsOneCharacter = lineBreakIsOneCharacter });

    #region Sources

    [Fact]
    public async Task ReadText_OfADocument_ReadsTheTextPattern_AndKnowsTheLengthOnceTheLastPageIsRead()
    {
        // Arrange
        var elementRef = Add(Document("Dear team, the release is on Monday."));

        // Act
        var result = await Read(elementRef);

        // Assert
        result.Success.Should().BeTrue();
        result.Ref.Should().Be(elementRef);
        result.WindowHandle.Should().Be(Handle);
        result.ControlType.Should().Be("Document");
        result.Source.Should().Be(ElementTextSource.Text);
        result.Text.Should().Be("Dear team, the release is on Monday.");
        result.Offset.Should().Be(0);
        result.TotalLength.Should().Be(36);
        result.Truncated.Should().BeFalse();
        result.NextOffset.Should().BeNull();
    }

    [Fact]
    public async Task ReadText_OfALongDocument_ReturnsAPage_AndTheOffsetOfTheNext()
    {
        // Arrange
        var elementRef = Add(Document("0123456789abcdefghij"));

        // Act
        var first = await Read(elementRef, maxLength: 8);
        var second = await Read(elementRef, offset: first.NextOffset!.Value, maxLength: 8);
        var last = await Read(elementRef, offset: second.NextOffset!.Value, maxLength: 8);

        // Assert - the length is not known until the end was reached: the provider is never asked for the whole document
        first.Should().BeEquivalentTo(new { Text = "01234567", Offset = 0, Truncated = true, NextOffset = 8, TotalLength = (int?)null });
        second.Should().BeEquivalentTo(new { Text = "89abcdef", Offset = 8, Truncated = true, NextOffset = 16 });
        last.Should().BeEquivalentTo(new { Text = "ghij", Offset = 16, Truncated = false, NextOffset = (int?)null, TotalLength = 20 });
    }

    [Fact]
    public async Task ReadText_WhenTheProviderCountsALineBreakAsOneCharacter_PagesInItsCharacters()
    {
        // Arrange - "a\r\nb\r\nc" is five of the provider's characters
        var elementRef = Add(Document("a\r\nb\r\nc", lineBreakIsOneCharacter: true));

        // Act - three of the provider's characters ("a\r\nb") would be four UTF-16 characters
        var first = await Read(elementRef, maxLength: 3);
        var second = await Read(elementRef, offset: first.NextOffset!.Value, maxLength: 3);

        // Assert - the page spans fewer characters, so it fits maxLength and nothing is skipped or read twice
        first.Text.Should().Be("a\r\n");
        first.NextOffset.Should().Be(2);
        second.Text.Should().Be("b\r\n");
        second.NextOffset.Should().Be(4);
        first.TextSkipped.Should().BeNull();
    }

    [Fact]
    public void FakeTextPattern_WithWideCharacters_ReturnsMoreTextThanThePageSpans()
    {
        // The fake is a provider's range, not an engine adapter: the tests below rely on its pages being too long
        new FakeTextPattern(new string('x', 100)) { CharacterLength = 10 }.GetTextPage(0, 5).Text.Should().HaveLength(50);
    }

    [Fact]
    public async Task ReadText_WhenAPageHoldsMoreTextThanMaxLength_ReadsFewerCharacters_AndSkipsNothing()
    {
        // Arrange - each of the provider's characters is ten UTF-16 characters: a page of 100 holds 1,000
        var document = string.Concat(Enumerable.Range(0, 1_000).Select(i => (i % 10).ToString()));
        var pattern = new FakeTextPattern(document) { CharacterLength = 10 };
        var elementRef = Add(new FakeUiaNode("Document", "Body") { OtherPatterns = UiaPatterns.Text }.WithPattern<IUiaTextPattern>(pattern));

        // Act
        var first = await Read(elementRef, maxLength: 100);
        var second = await Read(elementRef, offset: first.NextOffset!.Value, maxLength: 100);

        // Assert - the server caps the page itself, and the next page continues where it ends
        first.Text!.Length.Should().BeLessThanOrEqualTo(100).And.BeGreaterThan(0);
        first.Truncated.Should().BeTrue();
        first.NextOffset.Should().Be(first.Text.Length / 10);
        second.Offset.Should().Be(first.NextOffset);
        (first.Text + second.Text).Should().Be(document[..(first.Text.Length + second.Text!.Length)]);
        first.TextSkipped.Should().BeNull();
    }

    [Fact]
    public async Task ReadText_OfACharacterLongerThanMaxLength_CutsIt_AndSaysTextWasSkipped()
    {
        // Arrange - the provider counts "\r\n" as one character; a page of one character cannot hold it
        var elementRef = Add(Document("a\r\nb", lineBreakIsOneCharacter: true));

        // Act
        var result = await Read(elementRef, offset: 1, maxLength: 1);

        // Assert
        result.Text.Should().Be("\r");
        result.TextSkipped.Should().BeTrue();
        result.NextOffset.Should().Be(2);
    }

    [Theory]
    [InlineData('ж', 2)] // Cyrillic: two bytes in UTF-8
    [InlineData('中', 3)] // CJK: three bytes
    public async Task ReadText_OfNonAsciiText_StaysWithinTheByteLimit_AndPagesOnWithoutAGap(char letter, int bytesPerCharacter)
    {
        // Arrange
        var document = new string(letter, 100_000);
        var elementRef = Add(Document(document));

        // Act
        var first = await Read(elementRef, maxLength: ElementReadService.MaxTextLength);
        var second = await Read(elementRef, offset: first.NextOffset!.Value, maxLength: ElementReadService.MaxTextLength);

        // Assert
        first.Text!.Length.Should().Be(ElementReadService.MaxTextBytes / bytesPerCharacter);
        first.NextOffset.Should().Be(first.Text.Length);
        first.Truncated.Should().BeTrue();
        second.Offset.Should().Be(first.Text.Length);
        first.TextSkipped.Should().BeNull();
    }

    [Fact]
    public async Task ReadText_OfANonAsciiValue_StaysWithinTheByteLimit()
    {
        // Arrange
        var elementRef = Add(new FakeUiaNode("Edit", "Notes") { ActionPatterns = ["Value"], Value = new string('ж', 100_000) });

        // Act
        var result = await Read(elementRef, maxLength: ElementReadService.MaxTextLength);

        // Assert
        result.Source.Should().Be(ElementTextSource.Value);
        result.Text!.Length.Should().Be(ElementReadService.MaxTextBytes / 2);
        result.NextOffset.Should().Be(result.Text.Length);
        result.TotalLength.Should().Be(100_000);
    }

    [Fact]
    public async Task ReadText_PastTheEnd_ReturnsNoText_AndTheLength()
    {
        var elementRef = Add(Document("short"));

        var result = await Read(elementRef, offset: 50);

        result.Success.Should().BeTrue();
        result.Text.Should().BeEmpty();
        result.Offset.Should().Be(5);
        result.TotalLength.Should().Be(5);
        result.Truncated.Should().BeFalse();
    }

    [Fact]
    public async Task ReadText_OfAnEditWithOnlyAValue_ReadsTheValueInFull_AndCutsThePage()
    {
        // Arrange
        var elementRef = Add(new FakeUiaNode("Edit", "Subject") { ActionPatterns = ["Value"], Value = "Quarterly report draft" });

        // Act
        var result = await Read(elementRef, offset: 10, maxLength: 6);

        // Assert
        result.Source.Should().Be(ElementTextSource.Value);
        result.Text.Should().Be("report");
        result.TotalLength.Should().Be(22);
        result.Truncated.Should().BeTrue();
        result.NextOffset.Should().Be(16);
    }

    [Fact]
    public async Task ReadText_OfAValue_NeverCutsASurrogatePairInTwo()
    {
        // Arrange - "ab" then an emoji (two UTF-16 units) then "c"
        var elementRef = Add(new FakeUiaNode("Edit", "Chat") { ActionPatterns = ["Value"], Value = "ab\U0001F600c" });

        // Act
        var first = await Read(elementRef, maxLength: 3);
        var second = await Read(elementRef, offset: first.NextOffset!.Value, maxLength: 3);

        // Assert
        first.Text.Should().Be("ab");
        first.NextOffset.Should().Be(2);
        second.Text.Should().Be("\U0001F600c");
    }

    [Fact]
    public async Task ReadText_PrefersTheTextPatternToTheValue_WhichCanBeAPageUrl()
    {
        // Arrange - a browser document reports the page's URL as its value
        var elementRef = Add(new FakeUiaNode("Document", "Intranet")
        {
            ActionPatterns = ["Value"], OtherPatterns = UiaPatterns.Text, Value = "https://intranet.example/"
        }.WithPattern<IUiaTextPattern>(new FakeTextPattern("Welcome to the intranet")));

        // Act
        var result = await Read(elementRef);

        // Assert
        result.Source.Should().Be(ElementTextSource.Text);
        result.Text.Should().Be("Welcome to the intranet");
        _tree.Reads.Should().NotContain(r => r.StartsWith("Value:"), "the value is not read when the document has text");
    }

    [Fact]
    public async Task ReadText_WhenTheDocumentIsEmpty_FallsBackToTheValue()
    {
        var elementRef = Add(new FakeUiaNode("Edit", "Notes")
        {
            ActionPatterns = ["Value"], OtherPatterns = UiaPatterns.Text, Value = "from the value"
        }.WithPattern<IUiaTextPattern>(new FakeTextPattern(string.Empty)));

        var result = await Read(elementRef);

        result.Source.Should().Be(ElementTextSource.Value);
        result.Text.Should().Be("from the value");
    }

    [Fact]
    public async Task ReadText_WhenTheTextPatternFails_FallsBackToTheValue()
    {
        var elementRef = Add(new FakeUiaNode("Edit", "Notes")
        {
            ActionPatterns = ["Value"], OtherPatterns = UiaPatterns.Text, Value = "from the value"
        }.WithPattern<IUiaTextPattern>(new FakeTextPattern("x") { Failure = new InvalidOperationException("Not supported") }));

        var result = await Read(elementRef);

        result.Source.Should().Be(ElementTextSource.Value);
        result.Text.Should().Be("from the value");
    }

    [Fact]
    public async Task ReadText_OfAnEmptyEditBox_IsEmpty_NotItsLabel()
    {
        // Arrange - an empty edit box is named after its label; reading the label would pass it off as the box's text
        var elementRef = Add(new FakeUiaNode("Edit", "Search")
        {
            ActionPatterns = ["Value"], OtherPatterns = UiaPatterns.Text, Value = string.Empty
        }.WithPattern<IUiaTextPattern>(new FakeTextPattern(string.Empty)));

        // Act
        var result = await Read(elementRef);

        // Assert
        result.Source.Should().Be(ElementTextSource.Value);
        result.Text.Should().BeEmpty();
        result.TotalLength.Should().Be(0);
    }

    [Fact]
    public async Task ReadText_OfAnEmptyDocument_IsEmpty_NotItsName()
    {
        var elementRef = Add(Document(string.Empty));

        var result = await Read(elementRef);

        result.Source.Should().Be(ElementTextSource.Text);
        result.Text.Should().BeEmpty();
        result.TotalLength.Should().Be(0);
        result.Truncated.Should().BeFalse();
    }

    [Fact]
    public async Task ReadText_OfAnElementWithoutTextOrValue_ReadsTheName()
    {
        var elementRef = Add(new FakeUiaNode("Text", "Your changes were saved."));

        var result = await Read(elementRef);

        result.Source.Should().Be(ElementTextSource.Name);
        result.Text.Should().Be("Your changes were saved.");
        result.TotalLength.Should().Be(24);
    }

    [Fact]
    public async Task ReadText_OfAPasswordField_ReadsNothing_AndSaysItWasRedacted()
    {
        // Arrange
        var text = new FakeTextPattern("hunter2");
        var elementRef = Add(new FakeUiaNode("Edit", "Password")
        {
            ActionPatterns = ["Value"], OtherPatterns = UiaPatterns.Text, IsPassword = true, Value = "hunter2"
        }.WithPattern<IUiaTextPattern>(text));

        // Act
        var result = await Read(elementRef);

        // Assert
        result.Success.Should().BeTrue();
        result.Source.Should().Be(ElementTextSource.Redacted);
        result.IsPassword.Should().BeTrue();
        result.Text.Should().BeNull();
        result.TotalLength.Should().BeNull();
        text.Calls.Should().Be(0);
        _tree.Reads.Should().NotContain(r => r.StartsWith("Value:"));
    }

    [Fact]
    public async Task ReadText_WhenThePasswordFlagCannotBeRead_ReadsNothing()
    {
        // Arrange - the flag cannot be checked, so the text may be a password
        var elementRef = Add(new FakeUiaNode("Edit", "Field")
        {
            ActionPatterns = ["Value"], Value = "maybe secret", IdentityException = new InvalidOperationException("provider failed")
        });

        // Act
        var result = await Read(elementRef);

        // Assert
        result.Success.Should().BeFalse();
        result.ErrorCode.Should().Be(UIElementErrorCode.ElementAccessFailed);
        _tree.Reads.Should().NotContain(r => r.StartsWith("Value:"));
    }

    /// <summary>
    /// Puts an item its list has not realized in the window and gives it the ref find_ui_elements' searchVirtualized gives it:
    /// a placeholder that fails a whole read, as WPF's does, so it is read property by property.
    /// </summary>
    private string AddUnrealizedItem(FakeUiaNode item)
    {
        item.Unrealized = true;
        _window.With(new FakeUiaNode("List", "Files").With(item));
        return _registry.Register(Handle, _tree.ReadVirtualizedItem(item).Element, new ElementOrigin(_registry.NextGeneration()))!;
    }

    [Fact]
    public async Task ReadText_OfAnItemTheListHasNotRealized_ReadsItsName()
    {
        // Arrange - the gate's ref (1.15.0): its identity read fails as a whole, as its bounds and state do
        var elementRef = AddUnrealizedItem(new FakeUiaNode("ListItem", "Item 07500") { OtherPatterns = UiaPatterns.VirtualizedItem });

        // Act
        var result = await Read(elementRef);

        // Assert - not "the element disappeared": its name is its text, as for any item without a Text or Value pattern
        result.Success.Should().BeTrue(result.Error);
        result.Ref.Should().Be(elementRef);
        result.Source.Should().Be(ElementTextSource.Name);
        result.Text.Should().Be("Item 07500");
    }

    [Fact]
    public async Task ReadText_OfAnItemTheListHasNotRealized_WithAValue_ReadsNothing_WhileItDoesNotSayWhetherItIsAPassword()
    {
        // Arrange
        var elementRef = AddUnrealizedItem(new FakeUiaNode("ListItem", "Secret")
        {
            ActionPatterns = ["Value"], OtherPatterns = UiaPatterns.VirtualizedItem, Value = "maybe secret"
        });

        // Act
        var result = await Read(elementRef);

        // Assert
        result.Success.Should().BeFalse();
        result.ErrorCode.Should().Be(UIElementErrorCode.ElementAccessFailed);
        result.Error.Should().Contain("realize");
        _tree.Reads.Should().NotContain(r => r.StartsWith("Value:") || r.StartsWith("Pattern:IUiaValuePattern"));
    }

    #endregion

    #region Refs, criteria and failures

    [Fact]
    public async Task ReadText_OfAStaleRef_FailsWithElementStale()
    {
        // Arrange
        var element = new FakeUiaNode("Edit", "Subject") { ActionPatterns = ["Value"], Value = "x" };
        var elementRef = Add(element);
        element.Vanish();

        // Act
        var result = await Read(elementRef);

        // Assert
        result.Success.Should().BeFalse();
        result.ErrorCode.Should().Be(UIElementErrorCode.ElementStale);
        result.Text.Should().BeNull();
    }

    [Fact]
    public async Task ReadText_OfARefNeverIssued_FailsWithElementStale_WithoutReadingAnything()
    {
        var result = await Read("w99e1");

        result.ErrorCode.Should().Be(UIElementErrorCode.ElementStale);
        _worker.Calls.Should().Be(0);
    }

    [Fact]
    public async Task ReadText_OfAWindowThatDoesNotRespond_ReadsNothing()
    {
        // Arrange
        var elementRef = Add(Document("text"));
        _windows.Setup(w => w.IsHungAppWindow(Handle)).Returns(true);

        // Act
        var result = await Read(elementRef);

        // Assert
        result.ErrorCode.Should().Be(UIElementErrorCode.NotResponding);
        result.WindowHandle.Should().Be(Handle);
    }

    [Fact]
    public async Task ReadText_WarmsUpTheWindow_AndReportsIt()
    {
        // Arrange
        var elementRef = Add(Document("page"));
        var warmup = new AccessibilityWarmupInfo(AccessibilityWarmupStatus.Ready, WebContentHost.Chromium, 120, 3, 14);
        _warmup.Setup(w => w.WarmUp(Handle, It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>())).Returns(warmup);

        // Act
        var result = await Read(elementRef);

        // Assert
        result.AccessibilityWarmup.Should().Be(warmup);
        _warmup.Verify(w => w.WarmUp(Handle, It.Is<TimeSpan>(t => t > TimeSpan.Zero && t <= Timeout / 3), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ReadText_WhenTheWorkerGivesUp_FailsWithOperationTimedOut()
    {
        // Arrange
        var elementRef = Add(Document("text"));
        var worker = new Mock<IUiaWorker>();
        worker.Setup(w => w.RunAsync(It.IsAny<int>(), It.IsAny<Func<CancellationToken, ElementTextResult>>(), It.IsAny<CancellationToken>(), It.IsAny<TimeSpan>()))
            .ThrowsAsync(new TimeoutException("stuck"));
        var sut = new ElementReadService(_tree, worker.Object, _registry, new ElementSearchService(_tree, _worker, _registry), _windows.Object);

        // Act
        var result = await sut.ReadTextAsync(new ElementTextRequest(elementRef, null, 0, 100, Timeout));

        // Assert
        result.ErrorCode.Should().Be(UIElementErrorCode.OperationTimedOut);
        result.Error.Should().Contain("stuck");
    }

    private Task<ElementTextResult> ReadByQuery(ElementQuery query) =>
        CreateSut().ReadTextAsync(new ElementTextRequest(null, query, 0, 20_000, Timeout));

    private static ElementQuery Query(string? name = null, string[]? controlTypes = null, string? automationId = null) =>
        new(Handle, Name: name, AutomationId: automationId, ControlTypes: controlTypes, MaxResults: 5, Timeout: Timeout);

    [Fact]
    public async Task ReadText_ByCriteria_ReadsTheOneElementTheyFind_AndReturnsItsRef()
    {
        // Arrange
        _window.With(
            new FakeUiaNode("Edit", "Subject") { AutomationId = "subject", ActionPatterns = ["Value"], Value = "Lunch on Friday" },
            new FakeUiaNode("Edit", "To") { AutomationId = "to", ActionPatterns = ["Value"], Value = "team@example.com" });

        // Act
        var result = await ReadByQuery(Query(automationId: "subject"));

        // Assert
        result.Success.Should().BeTrue();
        result.Text.Should().Be("Lunch on Friday");
        result.Ref.Should().NotBeNull();
        _registry.Lookup(result.Ref!).Fingerprint!.AutomationId.Should().Be("subject");
    }

    [Fact]
    public async Task ReadText_ByCriteriaThatFindNothing_FailsWithElementNotFound()
    {
        _window.With(new FakeUiaNode("Edit", "Subject"));

        var result = await ReadByQuery(Query(name: "Body"));

        result.ErrorCode.Should().Be(UIElementErrorCode.ElementNotFound);
        result.Error.Should().Contain("No element");
    }

    [Fact]
    public async Task ReadText_ByCriteriaThatFindSeveral_FailsWithAmbiguousElement_AndListsThem()
    {
        // Arrange
        _window.With(
            new FakeUiaNode("Edit", "Subject") { ActionPatterns = ["Value"], Value = "a" },
            new FakeUiaNode("Edit", "To") { ActionPatterns = ["Value"], Value = "b" });

        // Act
        var result = await ReadByQuery(Query(controlTypes: ["Edit"]));

        // Assert - nothing is read from either: the agent picks one by its ref
        result.ErrorCode.Should().Be(UIElementErrorCode.AmbiguousElement);
        result.Candidates!.Select(c => c.Name).Should().Equal("Subject", "To");
        result.Candidates.Should().OnlyContain(c => c.Ref != null);
        result.Text.Should().BeNull();
        _tree.Reads.Should().NotContain(r => r.StartsWith("Value:"));
    }

    /// <summary>The service over a search that returns <paramref name="found"/>.</summary>
    private ElementReadService SutFinding(FindElementsResult found)
    {
        var search = new Mock<IElementSearchService>();
        search.Setup(s => s.FindAsync(It.IsAny<ElementQuery>(), It.IsAny<CancellationToken>())).ReturnsAsync(found);
        return new ElementReadService(_tree, _worker, _registry, search.Object, _windows.Object);
    }

    private static FoundElement Found(string elementRef, string name) =>
        new(elementRef, name, "Edit", null, "Edit", null, null, true, false, ["Value"]);

    [Theory]
    [InlineData(ElementSearchTruncation.SearchLimit)]
    [InlineData(ElementSearchTruncation.Timeout)]
    [InlineData(ElementSearchTruncation.PartsSkipped)]
    [InlineData(ElementSearchTruncation.WindowLimit)]
    public async Task ReadText_ByCriteria_WhenTheSearchDidNotFinishAndFoundNothing_IsOperationTimedOut_NotElementNotFound(
        ElementSearchTruncation reason)
    {
        // Arrange - a search cut short with no match so far: the element may be in the part it did not search
        var sut = SutFinding(new FindElementsResult(true, Handle, "Notes", 0, true, [], 1, TruncationReason: reason));

        // Act
        var result = await sut.ReadTextAsync(new ElementTextRequest(null, Query(name: "Body"), 0, 100, Timeout));

        // Assert - as element_action reports it
        result.Success.Should().BeFalse();
        result.ErrorCode.Should().Be(UIElementErrorCode.OperationTimedOut);
        result.Error.Should().Contain(reason.ToString()).And.Contain("found none");
    }

    [Fact]
    public async Task ReadText_ByCriteria_WhenAPartWasSkippedAndOneElementFound_IsOperationTimedOut_AsForElementAction()
    {
        var sut = SutFinding(new FindElementsResult(true, Handle, "Notes", 1, true, [Found("w1e5", "Body")], 1,
            TruncationReason: ElementSearchTruncation.PartsSkipped));

        var result = await sut.ReadTextAsync(new ElementTextRequest(null, Query(name: "Body"), 0, 100, Timeout));

        result.ErrorCode.Should().Be(UIElementErrorCode.OperationTimedOut);
        result.Error.Should().Contain("may not be the only match");
        _tree.Reads.Should().NotContain(r => r.StartsWith("Value:"));
    }

    [Fact]
    public async Task ReadText_ByCriteria_WhenTheSearchDidNotFinishButFoundSeveral_IsAmbiguous_WithTheCandidates()
    {
        // Arrange - more search cannot make two matches one
        var sut = SutFinding(new FindElementsResult(true, Handle, "Notes", 2, true, [Found("w1e5", "Body"), Found("w1e6", "Body")], 1,
            TruncationReason: ElementSearchTruncation.Timeout));

        // Act
        var result = await sut.ReadTextAsync(new ElementTextRequest(null, Query(name: "Body"), 0, 100, Timeout));

        // Assert
        result.ErrorCode.Should().Be(UIElementErrorCode.AmbiguousElement);
        result.Candidates!.Select(c => c.Ref).Should().Equal("w1e5", "w1e6");
        result.Error.Should().Contain("At least 2");
    }

    [Fact]
    public async Task ReadText_ByCriteria_InAWindowThatDoesNotRespond_FailsWithItsCode()
    {
        _windows.Setup(w => w.IsHungAppWindow(Handle)).Returns(true);

        var result = await ReadByQuery(Query(name: "Subject"));

        result.ErrorCode.Should().Be(UIElementErrorCode.NotResponding);
    }

    [Fact]
    public async Task ReadText_OfARefMovedToAReCreatedElement_ReadsIt_AndSaysSo()
    {
        // Arrange - the status label's search proved it the only one; the label was re-rendered with new text
        var status = new FakeUiaNode("Text", "Saved") { AutomationId = "Status" };
        _window.With(status);
        var statusRef = _registry.Register(Handle, _tree.ReadElement(status),
            new ElementOrigin(_registry.NextGeneration(), new ElementCriteria(AutomationId: "Status"), Unique: true))!;
        status.Vanish();
        _window.With(new FakeUiaNode("Text", "Saving...") { AutomationId = "Status" });

        // Act
        var result = await Read(statusRef);

        // Assert
        result.Success.Should().BeTrue();
        result.Text.Should().Be("Saving...");
        result.RefMoved.Should().BeTrue();
        result.Warning.Should().Contain($"Ref '{statusRef}' was moved");
    }

    [Fact]
    public async Task ReadText_OfALiveRef_HasNoMoveToReport()
    {
        var result = await Read(Add(new FakeUiaNode("Text", "Saved")));

        result.RefMoved.Should().BeNull();
        result.Warning.Should().BeNull();
    }

    #endregion

    #region Target policy

    /// <summary>The service with the default target policy, in a window of the password manager KeePass.</summary>
    private ElementReadService CreateSutInADeniedApp()
    {
        _windows.Setup(w => w.GetProcessName(300u)).Returns("KeePass");
        var policy = new TargetPolicy(TargetPolicySettings.Default);
        return new ElementReadService(_tree, _worker, _registry,
            new ElementSearchService(_tree, _worker, _registry, _windows.Object, _warmup.Object, policy: policy), _windows.Object,
            _warmup.Object, policy);
    }

    [Fact]
    public async Task ReadText_OfARefInAWindowOfADeniedApp_IsRefused_BeforeAnyWarmupOrRead()
    {
        // Arrange - a ref an earlier read returned, to the notes of a password entry
        var elementRef = Add(Document("Recovery code 4829-1337"));
        var sut = CreateSutInADeniedApp();
        var readsBefore = _tree.Reads.Count;

        // Act
        var result = await sut.ReadTextAsync(new ElementTextRequest(elementRef, null, 0, 20_000, Timeout));

        // Assert
        result.Success.Should().BeFalse();
        result.ErrorCode.Should().Be(UIElementErrorCode.TargetDeniedByPolicy);
        result.Error.Should().Contain("KeePass");
        result.Text.Should().BeNull();
        _tree.Reads.Should().HaveCount(readsBefore, "no UI Automation call reaches the window");
        _worker.Calls.Should().Be(0);
        _warmup.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task ReadText_ByCriteria_InAWindowOfADeniedApp_IsRefused_BeforeTheSearch()
    {
        // Arrange
        _window.With(Document("Recovery code 4829-1337"));
        var sut = CreateSutInADeniedApp();

        // Act
        var result = await sut.ReadTextAsync(new ElementTextRequest(null, Query(name: "Body"), 0, 20_000, Timeout));

        // Assert
        result.ErrorCode.Should().Be(UIElementErrorCode.TargetDeniedByPolicy);
        result.Text.Should().BeNull();
        _tree.Reads.Should().BeEmpty();
        _warmup.VerifyNoOtherCalls();
    }

    #endregion
}
