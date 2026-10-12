using System.Text.RegularExpressions;

namespace WindowsAppManagerMcp.Tests.Unit.Services;

/// <summary>
/// find_ui_elements' service (roadmap M3.4) over an in-memory UI Automation tree: the criteria, the refs, click points and
/// detail levels, withinRef, and the failures. No real window is read.
/// </summary>
public class ElementSearchServiceTests
{
    private static readonly nint Handle = 0x7100;

    private readonly FakeUiaTree _tree = new();
    private readonly InlineUiaWorker _worker = new();
    private readonly Mock<INativeWindowWrapper> _windows = new();
    private readonly ElementRegistry _registry;

    public ElementSearchServiceTests()
    {
        _windows.Setup(w => w.IsWindow(Handle)).Returns(true);
        _windows.Setup(w => w.GetWindowThreadProcessId(Handle)).Returns(300u);
        _windows.Setup(w => w.RespondsWithin(Handle, It.IsAny<uint>())).Returns(true);
        _registry = new ElementRegistry(_tree, _worker, _windows.Object, ElementRegistry.DefaultCapacity, 1, TimeProvider.System);
    }

    private ElementSearchService CreateSut() => new(_tree, _worker, _registry, _windows.Object);

    private Task<FindElementsResult> Find(ElementQuery query) => CreateSut().FindAsync(query);

    private static ElementQuery Query(
        string? name = null, ElementNameMatch nameMatch = ElementNameMatch.Exact, string? automationId = null,
        string[]? controlTypes = null, string? className = null, bool visibleOnly = true, bool enabledOnly = false,
        int maxResults = 20, ElementDetail detail = ElementDetail.Minimal, string? withinRef = null) =>
        new(Handle, withinRef, name, nameMatch, automationId, controlTypes, className, visibleOnly, enabledOnly, maxResults, detail,
            TimeSpan.FromSeconds(5));

    /// <summary>
    /// Window "Editor"
    ///   ToolBar "Standard" > Button "Save" (SaveBtn), Button "Save As" (SaveAsBtn), Button "Open" (disabled)
    ///   Pane "Body" > Group "Options" > CheckBox "Wrap" (on), Edit "Search" (focus, value), Edit "Password"
    ///   List "Hidden" (offscreen) > ListItem "Gone"
    /// </summary>
    private (FakeUiaNode Window, FakeUiaNode Save, FakeUiaNode Options) BuildEditor()
    {
        var save = new FakeUiaNode("Button", "Save")
        {
            AutomationId = "SaveBtn", ClassName = "Button", Bounds = new UiaRect(100, 40, 80, 24), ActionPatterns = ["Invoke"],
            ClickablePoint = new UiaPoint(120, 50)
        };
        var options = new FakeUiaNode("Group", "Options").With(
            new FakeUiaNode("CheckBox", "Wrap") { ActionPatterns = ["Toggle"] }.WithPattern<IUiaTogglePattern>(new FakeTogglePattern(UiaToggleState.On)),
            new FakeUiaNode("Edit", "Search")
            {
                AutomationId = "SearchBox", ActionPatterns = ["Value"], IsKeyboardFocusable = true, HasKeyboardFocus = true,
                Value = new string('x', 250), FrameworkId = "WPF", ProcessId = 300, HelpText = "Type to search", AccessKey = "Alt+S",
                LocalizedControlType = "edit"
            },
            new FakeUiaNode("Edit", "Password") { ActionPatterns = ["Value"], IsPassword = true, Value = "secret" });
        var window = new FakeUiaNode("Window", "Editor").With(
            new FakeUiaNode("ToolBar", "Standard").With(
                save,
                new FakeUiaNode("Button", "Save As") { AutomationId = "SaveAsBtn", Bounds = new UiaRect(200, 40, 80, 24) },
                new FakeUiaNode("Button", "Open") { IsEnabled = false }),
            new FakeUiaNode("Pane", "Body").With(options),
            new FakeUiaNode("List", "Hidden") { IsOffscreen = true }.With(new FakeUiaNode("ListItem", "Gone") { IsOffscreen = true }));
        _tree.WithWindow(Handle, window);
        return (window, save, options);
    }

    #region Criteria

    [Fact]
    public async Task Find_ByExactName_IgnoringCase_ReturnsTheMatchWithARefClickPointAndPatterns()
    {
        // Arrange
        BuildEditor();

        // Act
        var result = await Find(Query(name: "save"));

        // Assert
        result.Success.Should().BeTrue();
        result.WindowTitle.Should().Be("Editor");
        result.MatchCount.Should().Be(1);
        result.Truncated.Should().BeFalse();
        result.Elements.Should().ContainSingle().Which.Should().BeEquivalentTo(new FoundElement(
            Ref: "w1e1", Name: "Save", ControlType: "Button", AutomationId: "SaveBtn", ClassName: "Button",
            Bounds: new BoundsDto(100, 40, 80, 24), ClickPoint: new ScreenPoint(120, 50), IsEnabled: true, IsOffscreen: false,
            Patterns: ["Invoke"]));
    }

    [Fact]
    public async Task Find_WithoutAClickablePoint_UsesTheCentreOfTheBounds()
    {
        BuildEditor();

        var result = await Find(Query(automationId: "saveasbtn"));

        result.Elements!.Single().ClickPoint.Should().Be(new ScreenPoint(240, 52));
    }

    [Fact]
    public async Task Find_ByContainsAndRegex_TestsTheNamesOfTheOtherCriteriasMatches()
    {
        BuildEditor();

        (await Find(Query(name: "SAVE", nameMatch: ElementNameMatch.Contains))).Elements!.Select(e => e.Name).Should().Equal("Save", "Save As");
        (await Find(Query(name: "^save$|^open$", nameMatch: ElementNameMatch.Regex, enabledOnly: true))).Elements!
            .Select(e => e.Name).Should().Equal("Save");
    }

    [Fact]
    public async Task Find_ByControlTypes_MatchesAnyOfThem()
    {
        BuildEditor();

        var result = await Find(Query(controlTypes: ["checkbox", "Edit"]));

        result.Elements!.Select(e => e.Name).Should().Equal("Wrap", "Search", "Password");
    }

    [Fact]
    public async Task Find_WithAnUnknownControlType_FailsWithInvalidControlType()
    {
        BuildEditor();

        var result = await Find(Query(controlTypes: ["Buton"]));

        result.ErrorCode.Should().Be(UIElementErrorCode.InvalidControlType);
        result.Error.Should().Contain("Valid names");
        _worker.Calls.Should().Be(0);
    }

    [Fact]
    public async Task Find_WhenTheEngineCannotSearchForTheControlType_TestsItOnTheServer()
    {
        // Arrange - the managed client throws for AppBar (it has no id for it); every part is searched by the provider here
        var bars = new FakeUiaNode("Pane", "Shell").With(Enumerable.Range(1, 60)
            .Select(i => new FakeUiaNode(i == 30 ? "AppBar" : "Button", $"Item {i}")).ToArray());
        _tree.WithWindow(Handle, new FakeUiaNode("Window", "Shell window").With(bars));
        _tree.OnFindAll = (_, condition) =>
        {
            if (Compares(condition, UiaProperty.ControlType))
            {
                throw new NotSupportedException("The managed client cannot search for AppBar elements.");
            }
        };

        // Act
        var result = await Find(Query(controlTypes: ["AppBar"]));

        // Assert
        result.Elements!.Select(e => e.Name).Should().Equal("Item 30");
    }

    [Fact]
    public async Task Find_VisibleOnly_LeavesOutOffscreenElements_AndFalseReturnsThem()
    {
        BuildEditor();

        (await Find(Query(name: "Gone"))).MatchCount.Should().Be(0);
        (await Find(Query(name: "Gone", visibleOnly: false))).Elements!.Single().Should().Match<FoundElement>(e =>
            e.IsOffscreen && e.ClickPoint == null);
    }

    [Fact]
    public async Task Find_BeyondMaxResults_CountsEveryMatch_AndSaysItIsTruncated()
    {
        BuildEditor();

        var result = await Find(Query(controlTypes: ["Button"], maxResults: 2));

        result.MatchCount.Should().Be(3);
        result.Elements.Should().HaveCount(2);
        result.Truncated.Should().BeTrue();
        result.TruncationReason.Should().Be(ElementSearchTruncation.MaxResults);
    }

    [Fact]
    public async Task Find_WithNoMatch_SucceedsWithAnEmptyList()
    {
        BuildEditor();

        var result = await Find(Query(name: "Print"));

        result.Success.Should().BeTrue();
        result.MatchCount.Should().Be(0);
        result.Elements.Should().BeEmpty();
    }

    [Fact]
    public async Task Find_WithARegexThatRunsTooLong_ThrowsTheRegexTimeout()
    {
        // The tool turns it into InvalidArguments
        _tree.WithWindow(Handle, new FakeUiaNode("Window", "W").With(new FakeUiaNode("Text", new string('a', 40) + "!")));

        await FluentActions.Awaiting(() => Find(Query(name: "^(a+)+$", nameMatch: ElementNameMatch.Regex)))
            .Should().ThrowAsync<RegexMatchTimeoutException>();
    }

    #endregion

    private static bool Compares(UiaCondition condition, UiaProperty property) => condition switch
    {
        UiaPropertyCondition p => p.Property == property,
        UiaAndCondition and => and.Conditions.Any(c => Compares(c, property)),
        UiaOrCondition or => or.Conditions.Any(c => Compares(c, property)),
        UiaNotCondition not => Compares(not.Condition, property),
        _ => false
    };

    #region Refs

    [Fact]
    public async Task Refs_AreTheSameForTheSameElement_InFindAndInTheTreeWalk()
    {
        // Arrange
        var (_, save, _) = BuildEditor();
        var walk = new UIElementService(_tree, _worker, _windows.Object, registry: _registry);

        // Act
        var found = await Find(Query(name: "Save"));
        var tree = await walk.GetUIElementsAsync(Handle);
        var again = await Find(Query(automationId: "SaveBtn"));

        // Assert
        var saveRef = found.Elements!.Single().Ref;
        Flatten(tree.Elements!).Single(e => e.Name == "Save").Ref.Should().Be(saveRef);
        again.Elements!.Single().Ref.Should().Be(saveRef);
        (await _registry.ResolveAsync(saveRef!, TimeSpan.FromSeconds(5), CancellationToken.None)).Element!.Node.Should().BeSameAs(save);

        static IEnumerable<UIElementInfo> Flatten(IEnumerable<UIElementInfo> elements) =>
            elements.SelectMany(e => Flatten(e.Children ?? []).Prepend(e));
    }

    [Fact]
    public async Task Find_WithinRef_SearchesOnlyBelowThatElement()
    {
        // Arrange
        var (_, _, options) = BuildEditor();
        var optionsRef = _registry.Register(Handle, _tree.ReadElement(options), new ElementOrigin(1));

        // Act
        var result = await Find(Query(controlTypes: ["Edit", "Button"], withinRef: optionsRef));

        // Assert
        result.WithinRef.Should().Be(optionsRef);
        result.Elements!.Select(e => e.Name).Should().Equal("Search", "Password");
    }

    [Fact]
    public async Task Find_WithinAStaleRef_FailsWithElementStale()
    {
        // Arrange
        var (_, save, _) = BuildEditor();
        var saveRef = _registry.Register(Handle, _tree.ReadElement(save), new ElementOrigin(1));
        save.Vanish();
        _tree.FromHandle(Handle).Should().NotBeNull();

        // Act - no element has its automation id or name any more
        var result = await Find(Query(controlTypes: ["Button"], withinRef: saveRef));

        // Assert
        result.Success.Should().BeFalse();
        result.ErrorCode.Should().Be(UIElementErrorCode.ElementStale);
        result.Error.Should().StartWith("withinRef:");
    }

    [Fact]
    public async Task Find_AMatchAloneInTheWindow_KeepsItsRefWhenTheElementIsReCreated()
    {
        // Arrange - visibleOnly found one Save button; one more search proved no offscreen twin
        var (window, save, _) = BuildEditor();
        var saveRef = (await Find(Query(automationId: "SaveBtn"))).Elements!.Single().Ref;

        // Act - the toolbar rebuilt its button
        save.Vanish();
        var recreated = new FakeUiaNode("Button", "Save") { AutomationId = "SaveBtn", ClassName = "Button" };
        window.Children[0].Insert(0, recreated);
        var resolution = _registry.Resolve(saveRef!, CancellationToken.None);

        // Assert
        resolution.Element!.Should().Match<ResolvedElement>(r =>
            r.Ref == saveRef && ReferenceEquals(r.Node, recreated) && r.Via == ElementResolutionPath.Selector);
    }

    [Fact]
    public async Task Find_AVisibleMatchWithAnOffscreenTwin_IsNotMovedToALookAlike()
    {
        // Arrange - the visible Save button, and the same button on a hidden page
        var save = new FakeUiaNode("Button", "Save") { AutomationId = "SaveBtn" };
        var page = new FakeUiaNode("Pane", "Page 1").With(save);
        var window = new FakeUiaNode("Window", "Wizard").With(
            page,
            new FakeUiaNode("Pane", "Page 2") { IsOffscreen = true }.With(
                new FakeUiaNode("Button", "Save") { AutomationId = "SaveBtn", IsOffscreen = true }));
        _tree.WithWindow(Handle, window);
        var found = await Find(Query(automationId: "SaveBtn"));
        var saveRef = found.Elements!.Single().Ref;

        // Act - the visible one is gone, a look-alike appears
        save.Vanish();
        page.With(new FakeUiaNode("Button", "Save") { AutomationId = "SaveBtn" });
        var resolution = _registry.Resolve(saveRef!, CancellationToken.None);

        // Assert
        found.MatchCount.Should().Be(1);
        resolution.ErrorCode.Should().Be(UIElementErrorCode.ElementStale);
    }

    [Fact]
    public async Task Find_ARowsTemplatedButton_IsNeverMovedToAnotherRowsButton()
    {
        // Arrange - every row has a Delete button with the same automation id
        var deleteA = new FakeUiaNode("Button", "Delete") { AutomationId = "DeleteButton" };
        var rowA = new FakeUiaNode("DataItem", "Row A") { AutomationId = "RowA" }.With(deleteA);
        var rowB = new FakeUiaNode("DataItem", "Row B") { AutomationId = "RowB" }.With(
            new FakeUiaNode("Button", "Delete") { AutomationId = "DeleteButton" });
        _tree.WithWindow(Handle, new FakeUiaNode("Window", "Orders").With(new FakeUiaNode("DataGrid", "Rows").With(rowA, rowB)));
        var rowARef = _registry.Register(Handle, _tree.ReadElement(rowA), new ElementOrigin(1));
        var scoped = (await Find(Query(automationId: "DeleteButton", withinRef: rowARef))).Elements!.Single().Ref;
        var all = await Find(Query(automationId: "DeleteButton"));

        // Act - row A was deleted (by the agent's click): only row B's button is left
        rowA.Vanish();
        deleteA.Vanish();
        var resolution = _registry.Resolve(scoped!, CancellationToken.None);

        // Assert - the click is never repeated on row B
        all.Elements!.Select(e => e.Ref).Should().Contain(scoped);
        resolution.ErrorCode.Should().Be(UIElementErrorCode.ElementStale);
    }

    /// <summary>
    /// Window "Invoices" > DataGrid "Rows" > DataItem "Invoice 1041" (no automation id) > Button "Delete": a grid that
    /// recycles its rows. Returns the row, its button and the button's ref from a search below the row.
    /// </summary>
    private async Task<(FakeUiaNode Grid, FakeUiaNode Row, FakeUiaNode Delete, string DeleteRef)> BuildRecyclingGrid()
    {
        var delete = new FakeUiaNode("Button", "Delete") { AutomationId = "DeleteButton" };
        var row = new FakeUiaNode("DataItem", "Invoice 1041").With(delete);
        var grid = new FakeUiaNode("DataGrid", "Rows").With(row);
        _tree.WithWindow(Handle, new FakeUiaNode("Window", "Invoices").With(grid));
        var rowRef = _registry.Register(Handle, _tree.ReadElement(row), new ElementOrigin(1));
        var deleteRef = (await Find(Query(name: "Delete", withinRef: rowRef))).Elements!.Single().Ref!;
        return (grid, row, delete, deleteRef);
    }

    [Fact]
    public async Task Find_ARecycledRowsButton_IsStale_OnceItsRowShowsAnotherItem()
    {
        // Arrange - the grid scrolled and reused the row for another invoice: the row and its button keep their runtime ids,
        // their types, automation ids and classes; only the row's name tells
        var (_, row, _, deleteRef) = await BuildRecyclingGrid();
        row.Name = "Invoice 1077";

        // Act
        var resolution = _registry.Resolve(deleteRef, CancellationToken.None);

        // Assert - never Invoice 1077's Delete button
        resolution.ErrorCode.Should().Be(UIElementErrorCode.ElementStale);
        resolution.Error.Should().Contain("another item").And.Contain("Invoice 1077");
    }

    [Fact]
    public async Task Find_ARowsButtonMovedToAnotherRowElement_IsStale()
    {
        // Arrange - the button's container now belongs to another row element (another runtime id), as WPF's item peers do
        var (grid, _, delete, deleteRef) = await BuildRecyclingGrid();
        var other = new FakeUiaNode("DataItem", "Invoice 1041");
        grid.With(other);
        other.With(delete);

        // Act
        var resolution = _registry.Resolve(deleteRef, CancellationToken.None);

        // Assert - the same name, but not the same row
        resolution.ErrorCode.Should().Be(UIElementErrorCode.ElementStale);
    }

    [Fact]
    public async Task Find_ARowsButtonInTheSameRow_ResolvesLive_ReadingOnlyTheButtonAndItsRow()
    {
        // Arrange
        var (_, _, _, deleteRef) = await BuildRecyclingGrid();
        var readsBefore = _tree.Reads.Count;

        // Act
        var resolution = _registry.Resolve(deleteRef, CancellationToken.None);

        // Assert - one read of the element, one of its row (and one parent call)
        resolution.Element!.Via.Should().Be(ElementResolutionPath.Live);
        _tree.Reads.Skip(readsBefore).Should().Equal("Element:Delete", "Element:Invoice 1041");
    }

    [Fact]
    public async Task Find_ARowsButtonFoundAgainInAnotherItem_GetsANewRef_AndTheOldOneIsDropped()
    {
        // Arrange
        var (_, row, _, deleteRef) = await BuildRecyclingGrid();
        row.Name = "Invoice 1077";

        // Act
        var again = (await Find(Query(name: "Delete"))).Elements!.Single().Ref;

        // Assert - the ref of Invoice 1041's button never names Invoice 1077's
        again.Should().NotBe(deleteRef);
        _registry.Lookup(deleteRef).ErrorCode.Should().Be(UIElementErrorCode.ElementStale);
        _registry.Resolve(again!, CancellationToken.None).Element!.Via.Should().Be(ElementResolutionPath.Live);
    }

    [Fact]
    public async Task Find_ReadsEachAncestorOnce_ForTheRowsOfAllItsMatches()
    {
        // Arrange - three buttons in one toolbar, in a pane: no row above them
        _tree.WithWindow(Handle, new FakeUiaNode("Window", "Editor").With(new FakeUiaNode("Pane", "Body").With(
            new FakeUiaNode("ToolBar", "Tools").With(
                new FakeUiaNode("Button", "Cut"), new FakeUiaNode("Button", "Copy"), new FakeUiaNode("Button", "Paste")))));

        // Act
        var result = await Find(Query(controlTypes: ["Button"]));

        // Assert - the toolbar, the pane and the window once each, up to the window (where no row can be above)
        result.MatchCount.Should().Be(3);
        _tree.Reads.Where(r => r.StartsWith("Element:", StringComparison.Ordinal)).Should().Equal(
            "Element:Tools", "Element:Body", "Element:Editor");
    }

    [Fact]
    public async Task Find_LooksForAMatchsRowAtMostEightLevelsUp()
    {
        // Arrange - a button ten levels below a list item
        FakeUiaNode inner = new FakeUiaNode("Button", "Deep");
        for (var i = 0; i < 9; i++)
        {
            inner = new FakeUiaNode("Group", $"G{i}").With(inner);
        }

        _tree.WithWindow(Handle, new FakeUiaNode("Window", "W").With(new FakeUiaNode("List", "L").With(
            new FakeUiaNode("ListItem", "Item").With(inner))));

        // Act
        await Find(Query(name: "Deep"));

        // Assert
        _tree.Reads.Count(r => r.StartsWith("Element:", StringComparison.Ordinal)).Should().Be(ElementRows.MaxLevels);
    }

    [Fact]
    public async Task Find_WithinARefFoundAgainUnderItsOwnRef_ReportsTheRefItHasNow()
    {
        // Arrange - the Options group was re-created and the new one already has a ref of its own
        var (window, _, options) = BuildEditor();
        var oldRef = (await Find(Query(name: "Options", controlTypes: ["Group"]))).Elements!.Single().Ref;
        options.Vanish();
        var recreated = new FakeUiaNode("Group", "Options").With(new FakeUiaNode("Edit", "Search"));
        window.Children[1].With(recreated);
        var newRef = _registry.Register(Handle, _tree.ReadElement(recreated), new ElementOrigin(2));

        // Act
        var result = await Find(Query(controlTypes: ["Edit"], withinRef: oldRef));

        // Assert - the asked-for ref was dropped; the result names the one to keep using
        result.Success.Should().BeTrue();
        result.WithinRef.Should().Be(newRef).And.NotBe(oldRef);
        _registry.Lookup(oldRef!).ErrorCode.Should().Be(UIElementErrorCode.ElementStale);
        result.RefMoved.Should().BeTrue("the group the ref was given for is gone");
    }

    [Fact]
    public async Task Find_WithinARefMovedToAReCreatedElement_SearchesIt_AndSaysSo()
    {
        // Arrange - the Options group was re-created: the ref follows the new group, which alone fits its description
        var (window, _, options) = BuildEditor();
        var optionsRef = (await Find(Query(name: "Options", controlTypes: ["Group"]))).Elements!.Single().Ref!;
        options.Vanish();
        window.Children[1].With(new FakeUiaNode("Group", "Options").With(new FakeUiaNode("Edit", "Search")));

        // Act
        var result = await Find(Query(controlTypes: ["Edit"], withinRef: optionsRef));
        var unmoved = await Find(Query(controlTypes: ["Edit"], withinRef: optionsRef));

        // Assert - reported on every search below it, until a read returns the group again
        result.Success.Should().BeTrue();
        result.WithinRef.Should().Be(optionsRef);
        result.Elements!.Select(e => e.Name).Should().Equal("Search");
        result.RefMoved.Should().BeTrue();
        result.Warning.Should().StartWith("withinRef: ").And.Contain($"Ref '{optionsRef}' was moved");
        unmoved.RefMoved.Should().BeTrue();
    }

    [Fact]
    public async Task Find_WithinALiveRef_HasNoMoveToReport()
    {
        var (_, _, options) = BuildEditor();
        var optionsRef = _registry.Register(Handle, _tree.ReadElement(options), new ElementOrigin(1));

        var result = await Find(Query(controlTypes: ["Edit"], withinRef: optionsRef));

        result.RefMoved.Should().BeNull();
        result.Warning.Should().BeNull();
    }

    #endregion

    #region Detail

    [Fact]
    public async Task Find_DetailStandard_ReadsValueStateAndFocus_ButNeverAPasswordsValue()
    {
        BuildEditor();

        var result = await Find(Query(controlTypes: ["Edit", "CheckBox"], detail: ElementDetail.Standard));

        var byName = result.Elements!.ToDictionary(e => e.Name!);
        byName["Wrap"].ToggleState.Should().Be(UiaToggleState.On);
        byName["Search"].Should().Match<FoundElement>(e =>
            e.Value!.Length == 200 && e.ValueTruncated == true && e.HasKeyboardFocus == true && e.IsKeyboardFocusable == true);
        byName["Password"].Should().Match<FoundElement>(e => e.IsPassword == true && e.Value == null);
        _tree.Reads.Should().NotContain("Value:Password", "a password field's value is never read, not even to mask it");
        byName["Search"].FrameworkId.Should().BeNull("framework and process are detail full");
    }

    [Fact]
    public async Task Find_DetailFull_AddsFrameworkProcessHelpTextAndKeys()
    {
        BuildEditor();

        var result = await Find(Query(automationId: "SearchBox", detail: ElementDetail.Full));

        result.Elements!.Single().Should().Match<FoundElement>(e =>
            e.FrameworkId == "WPF" && e.ProcessId == 300 && e.HelpText == "Type to search" && e.AccessKey == "Alt+S" &&
            e.LocalizedControlType == "edit" && e.NativeWindowHandle == null);
    }

    [Fact]
    public async Task Find_DetailMinimal_MakesNoReadButTheClickPoint_AndTheRowLookup()
    {
        BuildEditor();

        await Find(Query(name: "Save"));

        // The ancestors up to the window, read for the list row the button could be in (it is in none)
        _tree.Reads.Where(r => !r.StartsWith("Read:", StringComparison.Ordinal))
            .Should().Equal("Element:Standard", "Element:Editor", "Clickable:Save");
    }

    #endregion

    #region Failures

    [Fact]
    public async Task Find_InAHungWindow_ReadsNothing()
    {
        BuildEditor();
        _windows.Setup(w => w.IsHungAppWindow(Handle)).Returns(true);

        var result = await Find(Query(name: "Save"));

        result.ErrorCode.Should().Be(UIElementErrorCode.NotResponding);
        _tree.Reads.Should().BeEmpty();
    }

    [Fact]
    public async Task Find_WithAZeroHandle_OrAWindowUiAutomationCannotRead_Fails()
    {
        (await CreateSut().FindAsync(Query() with { WindowHandle = 0, Name = "x" })).ErrorCode.Should().Be(UIElementErrorCode.InvalidHandle);
        (await Find(Query(name: "Save"))).ErrorCode.Should().Be(UIElementErrorCode.WindowNotFound);
        _tree.Available = false;
        (await Find(Query(name: "Save"))).ErrorCode.Should().Be(UIElementErrorCode.AutomationUnavailable);
    }

    [Fact]
    public async Task Find_WhenTheTimeoutPassesBeforeAnyMatch_FailsWithOperationTimedOut()
    {
        // Arrange - the deadline passes during the first part's search: its provider call returns only once the search's
        // deadline (the token the worker runs it with) is cancelled, however slow the machine is
        var lists = Enumerable.Range(1, 3).Select(i => new FakeUiaNode("List", $"L{i}").With(
            Enumerable.Range(1, 60).Select(j => new FakeUiaNode("Text", $"T{j}")).ToArray())).ToArray();
        _tree.WithWindow(Handle, new FakeUiaNode("Window", "W").With(lists));
        var sut = CreateSut();
        var deadlinePassed = false;
        lists[0].OnFindAll = _ => deadlinePassed = _worker.LastToken.WaitHandle.WaitOne(TimeSpan.FromSeconds(30));

        // Act
        var result = await sut.FindAsync(Query(controlTypes: ["Button"]) with { Timeout = TimeSpan.FromMilliseconds(1000) });

        // Assert - the search stopped after that part
        deadlinePassed.Should().BeTrue();
        result.ErrorCode.Should().Be(UIElementErrorCode.OperationTimedOut);
        result.Error.Should().Contain("before any element");
        _tree.Finds.Should().Equal("L1:Descendants");
    }

    [Fact]
    public async Task Find_WhenTheTimeoutPassesAfterSomeMatches_ReturnsThemTruncated()
    {
        // Arrange - the deadline passes during the second part's search (its provider call returns once it has)
        var lists = Enumerable.Range(1, 3).Select(i => new FakeUiaNode("List", $"L{i}").With(
            Enumerable.Range(1, 60).Select(j => new FakeUiaNode("Button", $"B{i}.{j}")).ToArray())).ToArray();
        _tree.WithWindow(Handle, new FakeUiaNode("Window", "W").With(lists));
        lists[1].OnFindAll = _ => _worker.LastToken.WaitHandle.WaitOne(TimeSpan.FromSeconds(30));

        // Act
        var result = await CreateSut().FindAsync(Query(controlTypes: ["Button"]) with { Timeout = TimeSpan.FromMilliseconds(1000) });

        // Assert
        result.Success.Should().BeTrue();
        result.TruncationReason.Should().Be(ElementSearchTruncation.Timeout);
        result.MatchCount.Should().Be(120);
        result.Elements.Should().HaveCount(20);
        result.Elements!.Should().OnlyContain(e => e.Ref != null);
    }

    [Fact]
    public async Task Find_WhenTheWorkerAbandonsTheSearch_ReturnsWhatItFound_WithoutReadingFurther()
    {
        // Arrange - the search is stuck in the second list's call (a provider that does not answer) when the worker gives up
        var lists = Enumerable.Range(1, 2).Select(i => new FakeUiaNode("List", $"L{i}").With(
            Enumerable.Range(1, 60).Select(j => new FakeUiaNode("Button", $"B{i}.{j}")).ToArray())).ToArray();
        _tree.WithWindow(Handle, new FakeUiaNode("Window", "W").With(lists));
        using var stuck = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        lists[1].OnFindAll = _ =>
        {
            stuck.Set();
            release.Wait(TimeSpan.FromSeconds(10));
        };
        var sut = new ElementSearchService(_tree, new AbandoningWorker(stuck), _registry, _windows.Object);

        try
        {
            // Act
            var result = await sut.FindAsync(Query(controlTypes: ["Button"]));

            // Assert
            result.Success.Should().BeTrue();
            result.TruncationReason.Should().Be(ElementSearchTruncation.Timeout);
            result.MatchCount.Should().Be(60);
            result.Elements!.Should().HaveCount(20).And.OnlyContain(e => e.Ref != null && e.ClickPoint != null);
            _tree.Reads.Should().NotContain(r => r.StartsWith("Clickable:", StringComparison.Ordinal),
                "nothing is read on the caller's thread");
        }
        finally
        {
            release.Set();
        }
    }

    /// <summary>
    /// Runs the read on a thread of its own and gives up once <paramref name="stuck"/> is set, as the real worker gives up on
    /// a read stuck past its deadline: the read is abandoned, and the caller gets a TimeoutException.
    /// </summary>
    private sealed class AbandoningWorker(ManualResetEventSlim stuck) : IUiaWorker
    {
        public async Task<T> RunAsync<T>(int targetProcessId, Func<CancellationToken, T> read, CancellationToken cancellationToken, TimeSpan grace)
        {
            var reading = Task.Run(() => read(cancellationToken));
            var gaveUp = Task.Run(() => stuck.Wait(TimeSpan.FromSeconds(10)));
            if (await Task.WhenAny(reading, gaveUp) == reading)
            {
                return await reading;
            }

            throw new TimeoutException("UI Automation did not answer in time.");
        }
    }

    #endregion

    #region Target policy

    [Fact]
    public async Task Find_InAWindowOfADeniedApp_IsRefused_BeforeAnyWarmupOrRead()
    {
        // Arrange - the window named belongs to a password manager
        BuildEditor();
        _windows.Setup(w => w.GetProcessName(300u)).Returns("Bitwarden");
        var warmup = new Mock<IAccessibilityWarmup>();
        var sut = new ElementSearchService(_tree, _worker, _registry, _windows.Object, warmup.Object,
            policy: new TargetPolicy(TargetPolicySettings.Default));

        // Act
        var result = await sut.FindAsync(Query(name: "Search", detail: ElementDetail.Standard));

        // Assert
        result.Success.Should().BeFalse();
        result.ErrorCode.Should().Be(UIElementErrorCode.TargetDeniedByPolicy);
        result.Error.Should().Contain("Bitwarden");
        result.Elements.Should().BeNull();
        _tree.Reads.Should().BeEmpty();
        _worker.Calls.Should().Be(0);
        warmup.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task Find_BelowARefOfAWindowOfADeniedApp_IsRefused()
    {
        // Arrange - a ref an earlier read returned; the user has denied the app since (the policy is read at start, so in
        // practice the ref came from an earlier session's server, or the app was not denied then)
        var (_, _, options) = BuildEditor();
        var withinRef = _registry.Register(Handle, _tree.ReadElement(options), new ElementOrigin(_registry.NextGeneration()))!;
        _windows.Setup(w => w.GetProcessName(300u)).Returns("Bitwarden");
        var sut = new ElementSearchService(_tree, _worker, _registry, _windows.Object,
            policy: new TargetPolicy(TargetPolicySettings.Default));
        var readsBefore = _tree.Reads.Count;

        // Act
        var result = await sut.FindAsync(Query(name: "Search", withinRef: withinRef));

        // Assert
        result.ErrorCode.Should().Be(UIElementErrorCode.TargetDeniedByPolicy);
        _tree.Reads.Should().HaveCount(readsBefore);
    }

    [Fact]
    public async Task Find_InAWindowOfAnAllowedApp_Searches()
    {
        BuildEditor();
        _windows.Setup(w => w.GetProcessName(300u)).Returns("notepad");
        var sut = new ElementSearchService(_tree, _worker, _registry, _windows.Object,
            policy: new TargetPolicy(TargetPolicySettings.Default));

        var result = await sut.FindAsync(Query(name: "Save"));

        result.Success.Should().BeTrue();
        result.MatchCount.Should().Be(1);
    }

    #endregion

    #region Web content warm-up (roadmap M3.3)

    private static readonly AccessibilityWarmupInfo Warmed =
        new(AccessibilityWarmupStatus.Ready, WebContentHost.Chromium, 400, 3, 12);

    [Fact]
    public async Task Find_WarmsUpTheWindowBeforeTheSearch_WithinAThirdOfTheTimeout_AndReportsIt()
    {
        // Arrange
        BuildEditor();
        var warmup = new Mock<IAccessibilityWarmup>();
        warmup.Setup(w => w.WarmUp(Handle, It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
            .Callback(() => _tree.Finds.Should().BeEmpty("the warm-up comes before the search"))
            .Returns(Warmed);
        var sut = new ElementSearchService(_tree, _worker, _registry, _windows.Object, warmup.Object);

        // Act
        var result = await sut.FindAsync(Query(name: "Save"));

        // Assert
        result.Success.Should().BeTrue();
        result.Elements!.Single().Ref.Should().NotBeNull();
        result.AccessibilityWarmup.Should().Be(Warmed);
        warmup.Verify(w => w.WarmUp(Handle, AccessibilityWarmup.BudgetFor(TimeSpan.FromSeconds(5)), _worker.LastToken), Times.Once);
    }

    [Fact]
    public async Task Find_WhenNothingMatches_StillReportsTheWarmup()
    {
        // Arrange - the page stayed empty, so the element was not found
        BuildEditor();
        var notReady = Warmed with { Status = AccessibilityWarmupStatus.NotReady, DocumentChildCount = 0, Warning = "Read again." };
        var warmup = new Mock<IAccessibilityWarmup>();
        warmup.Setup(w => w.WarmUp(Handle, It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>())).Returns(notReady);
        var sut = new ElementSearchService(_tree, _worker, _registry, _windows.Object, warmup.Object);

        // Act
        var result = await sut.FindAsync(Query(name: "Compose"));

        // Assert
        result.MatchCount.Should().Be(0);
        result.AccessibilityWarmup.Should().Be(notReady);
    }

    [Fact]
    public async Task Find_InAWindowThatIsNotFound_DoesNotWarmItUp()
    {
        // Arrange - no window registered in the tree
        var warmup = new Mock<IAccessibilityWarmup>();
        var sut = new ElementSearchService(_tree, _worker, _registry, _windows.Object, warmup.Object);

        // Act
        var result = await sut.FindAsync(Query(name: "Save"));

        // Assert
        result.ErrorCode.Should().Be(UIElementErrorCode.WindowNotFound);
        result.AccessibilityWarmup.Should().BeNull();
        warmup.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task Find_WithWarmUpOff_SearchesWithoutWarmingUp()
    {
        // Arrange - wait_for_element's checks after its first one (roadmap M3.8)
        BuildEditor();
        var warmup = new Mock<IAccessibilityWarmup>();
        var sut = new ElementSearchService(_tree, _worker, _registry, _windows.Object, warmup.Object);

        // Act
        var result = await sut.FindAsync(Query(name: "Save") with { WarmUp = false });

        // Assert
        result.MatchCount.Should().Be(1);
        result.AccessibilityWarmup.Should().BeNull();
        warmup.VerifyNoOtherCalls();
    }

    #endregion
}
