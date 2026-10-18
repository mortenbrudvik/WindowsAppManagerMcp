namespace WindowsAppManagerMcp.Tests.Unit.Services;

/// <summary>
/// element_action's menu_path (roadmap M3.5): each item found as the only MenuItem of its name where it can be (the window
/// for the first, then the submenu the previous item opened, below that item, the window and its popups), every item but
/// the last expanded and the last invoked, each only through element_action's service; a failure stops the path and says
/// what is left open. The search, the action and the popups are fakes.
/// </summary>
public class MenuPathServiceTests
{
    private static readonly nint Window = 0x4000;
    private static readonly nint FileMenu = 0x7000;

    private readonly Mock<IElementSearchService> _search = new();
    private readonly Mock<IElementActionService> _actions = new();
    private readonly Mock<IPopupService> _popups = new();
    private readonly List<ElementQuery> _queries = [];
    private readonly List<ElementActionRequest> _acted = [];
    private Func<ElementQuery, FindElementsResult> _find;
    private Func<ElementActionRequest, ElementActionResult> _act;

    public MenuPathServiceTests()
    {
        // The window's menu bar has File; File's submenu window (0x7000) has Save As...
        _find = query => query.Name switch
        {
            _ when Matches(query, "File") && query.WindowHandle == Window => Found(query, Item("w1e1", "File")),
            _ when Matches(query, "Save As...") && query.WindowHandle == FileMenu => Found(query, Item("w2e1", "Save As...", FileMenu)),
            _ => Found(query)
        };
        _act = request => request.Action switch
        {
            ElementActionKind.Expand => Done(request, ElementActionMethod.ExpandCollapse,
                new OpenedWindow((long)FileMenu, string.Empty, "#32768", 4242, "notepad")),
            _ => Done(request, ElementActionMethod.Invoke)
        };
        _search.Setup(s => s.FindAsync(It.IsAny<ElementQuery>(), It.IsAny<CancellationToken>()))
            .Callback((ElementQuery query, CancellationToken _) => _queries.Add(query))
            .ReturnsAsync((ElementQuery query, CancellationToken _) => _find(query));
        _actions.Setup(a => a.ActAsync(It.IsAny<ElementActionRequest>(), It.IsAny<CancellationToken>()))
            .Callback((ElementActionRequest request, CancellationToken _) => _acted.Add(request))
            .ReturnsAsync((ElementActionRequest request, CancellationToken _) => _act(request));
        _popups.Setup(p => p.GetPopups(It.IsAny<nint>())).Returns([]);
    }

    private MenuPathService CreateSut() => new(_search.Object, _actions.Object, _popups.Object) { SubmenuWait = TimeSpan.FromMilliseconds(20) };

    private static bool Matches(ElementQuery query, string name) =>
        query.NameMatch == ElementNameMatch.Regex && System.Text.RegularExpressions.Regex.IsMatch(name, query.Name!,
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    private static FoundElement Item(string elementRef, string name, nint window = 0) =>
        new(elementRef, name, "MenuItem", null, null, null, null, true, false, ["ExpandCollapse", "Invoke"],
            WindowHandle: window == 0 ? null : (long)window);

    private static FindElementsResult Found(ElementQuery query, params FoundElement[] items) =>
        new(true, (long)query.WindowHandle, "Notepad", items.Length, false, items, 1, Scope: query.Scope ?? ElementSearchScope.Window);

    private static ElementActionResult Done(ElementActionRequest request, ElementActionMethod method, OpenedWindow? opened = null) =>
        new(true, ToolEnumName(request.Action), request.Ref, (long)Window, ElementActionStatus.Done, method, OpenedPopup: opened);

    private static string ToolEnumName(ElementActionKind kind) => kind.ToString().ToLowerInvariant();

    private Task<ElementActionResult> RunAsync(params string[] items) =>
        CreateSut().RunAsync(new MenuPathRequest(Window, null, items, Timeout: TimeSpan.FromSeconds(2)));

    [Theory]
    [InlineData("File>Save As", new[] { "File", "Save As" })]
    [InlineData(" File > Save As ", new[] { "File", "Save As" })]
    [InlineData("Edit", new[] { "Edit" })]
    public void Parse_SplitsAtTheSeparator(string value, string[] expected)
    {
        MenuPaths.Parse(value, out var error).Should().Equal(expected);
        error.Should().BeNull();
    }

    [Theory]
    [InlineData(null)]
    [InlineData(" ")]
    [InlineData("File>>Save")]
    [InlineData("File>")]
    [InlineData("a>b>c>d>e>f>g>h>i")]
    public void Parse_RefusesAnEmptyItemOrTooManyItems(string? value)
    {
        MenuPaths.Parse(value, out var error).Should().BeNull();
        error.Should().NotBeNullOrEmpty();
    }

    [Theory]
    [InlineData("Save As", "Save As", true)]
    [InlineData("Save As", "save as...", true)]
    [InlineData("Save As", "Save As…", true)]
    [InlineData("Save As", "Save As...\tCtrl+Shift+S", true)]
    [InlineData("Save", "Save As...", false)]
    [InlineData("Save (A)", "Save (A)", true)]
    public void NamePattern_IgnoresCaseAndATrailingEllipsisOrAccelerator(string item, string name, bool matches)
    {
        System.Text.RegularExpressions.Regex.IsMatch(name, MenuPaths.NamePattern(item),
            System.Text.RegularExpressions.RegexOptions.IgnoreCase).Should().Be(matches);
    }

    [Fact]
    public async Task APath_ExpandsEachItemButTheLast_InvokesTheLast_ThroughElementAction_InTheSubmenuItOpened()
    {
        var result = await RunAsync("File", "Save As");

        result.Success.Should().BeTrue(result.Error);
        result.Action.Should().Be("menu_path");
        result.Ref.Should().Be("w2e1");
        _acted.Select(a => (a.Ref, a.Action)).Should().Equal(("w1e1", ElementActionKind.Expand), ("w2e1", ElementActionKind.Invoke));
        result.Steps!.Select(s => (s.Item, s.Ref, s.Method, s.OpenedPopup)).Should().Equal(
            ("File", "w1e1", ElementActionMethod.ExpandCollapse, (long)FileMenu), ("Save As", "w2e1", ElementActionMethod.Invoke, null));
        _queries[0].Should().Match<ElementQuery>(q => q.WindowHandle == Window && q.Scope == null && q.ControlTypes!.Single() == "MenuItem" &&
                                                      q.VisibleOnly && q.MaxResults == 2);
        _queries[1].Should().Match<ElementQuery>(q => q.WindowHandle == FileMenu && q.Scope == ElementSearchScope.Window);
    }

    [Fact]
    public async Task AnItemBeforeTheLastWithoutExpandCollapse_StopsThePath_AndIsNotInvoked()
    {
        // 'Edit>Delete>All' where Delete is a command: invoking it on the way would delete
        _act = request => request.Action == ElementActionKind.Expand
            ? new ElementActionResult(false, "expand", request.Ref, Error: "No pattern.", ErrorCode: nameof(UIElementErrorCode.PatternNotSupported))
            : Done(request, ElementActionMethod.Invoke);

        var result = await RunAsync("File", "Save As");

        result.Success.Should().BeFalse();
        result.ErrorCode.Should().Be(nameof(UIElementErrorCode.PatternNotSupported));
        result.Error.Should().Contain("'File' (item 1 of 2)").And.Contain("no submenu").And.Contain("was not");
        _acted.Select(a => a.Action).Should().Equal(ElementActionKind.Expand);
    }

    [Fact]
    public async Task ALastItemWithoutInvoke_IsClicked()
    {
        _act = request => request.Action switch
        {
            ElementActionKind.Expand => Done(request, ElementActionMethod.ExpandCollapse,
                new OpenedWindow((long)FileMenu, string.Empty, "#32768", 4242, "notepad")),
            ElementActionKind.Invoke => new ElementActionResult(false, "invoke", request.Ref, Error: "No pattern.",
                ErrorCode: nameof(UIElementErrorCode.PatternNotSupported)),
            _ => Done(request, ElementActionMethod.Toggle)
        };

        var result = await RunAsync("File", "Save As");

        result.Success.Should().BeTrue(result.Error);
        _acted.Select(a => a.Action).Should().Equal(ElementActionKind.Expand, ElementActionKind.Invoke, ElementActionKind.Click);
        result.Method.Should().Be(ElementActionMethod.Toggle);
    }

    [Fact]
    public async Task ThePath_IsBoundedAsAWhole_ByItsTimeoutPerItem()
    {
        // Two items of 100 ms each: the expand of the first takes longer than the whole path may
        _act = request =>
        {
            Thread.Sleep(250);
            return Done(request, ElementActionMethod.ExpandCollapse, new OpenedWindow((long)FileMenu, string.Empty, "#32768", 4242, "notepad"));
        };

        var result = await CreateSut().RunAsync(new MenuPathRequest(Window, null, ["File", "Save As"], Timeout: TimeSpan.FromMilliseconds(100)));

        result.ErrorCode.Should().Be(nameof(UIElementErrorCode.OperationTimedOut));
        result.Error.Should().Contain("ran out of its time").And.Contain("item 2 of 2");
        result.Steps.Should().ContainSingle().Which.Item.Should().Be("File");
        _acted.Should().ContainSingle();
        _acted[0].Timeout.Should().BeLessThanOrEqualTo(TimeSpan.FromMilliseconds(100));
    }

    [Fact]
    public async Task ASubmenuWithoutAWindowOfItsOwn_IsSearchedBelowItsItem_ThenInTheWindowAndPopups()
    {
        // WPF: the submenu's items are under their item; no new window was reported
        _act = request => Done(request, request.Action == ElementActionKind.Expand ? ElementActionMethod.ExpandCollapse : ElementActionMethod.Invoke);
        _find = query => query switch
        {
            { WithinRef: null } when Matches(query, "File") => Found(query, Item("w1e1", "File")),
            { WithinRef: "w1e1" } when Matches(query, "Save As") => Found(query, Item("w1e2", "Save As...")),
            _ => Found(query)
        };

        var result = await RunAsync("File", "Save As");

        result.Success.Should().BeTrue(result.Error);
        _queries[1].Should().Match<ElementQuery>(q => q.WithinRef == "w1e1" && q.WindowHandle == Window);
        _popups.Verify(p => p.WaitForNewPopupAsync(Window, It.IsAny<IReadOnlySet<nint>>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task AnItemNotFound_StopsThePath_AndSaysWhatIsLeftOpen()
    {
        var result = await RunAsync("File", "Export");

        result.Success.Should().BeFalse();
        result.ErrorCode.Should().Be(nameof(UIElementErrorCode.ElementNotFound));
        result.Error.Should().Contain("'Export' (item 2 of 2)").And.Contain("may still be open").And.Contain("w1e1");
        result.Steps.Should().ContainSingle().Which.Item.Should().Be("File");

        // The submenu File opened, and below File: not the window's other menus, where another 'Export' may be
        _queries.Skip(1).Select(q => (q.WindowHandle, q.WithinRef, q.Scope)).Should().Equal(
            (FileMenu, null, ElementSearchScope.Window), (Window, "w1e1", ElementSearchScope.Window));
        _acted.Should().ContainSingle();
    }

    [Fact]
    public async Task WithoutASubmenuWindow_AnItemIsAlsoSearchedInTheWindowAndItsPopups()
    {
        _act = request => Done(request, request.Action == ElementActionKind.Expand ? ElementActionMethod.ExpandCollapse : ElementActionMethod.Invoke);

        var result = await RunAsync("File", "Export");

        result.ErrorCode.Should().Be(nameof(UIElementErrorCode.ElementNotFound));
        _queries.Skip(1).Select(q => (q.WithinRef, q.Scope)).Should().Equal(
            ("w1e1", ElementSearchScope.Window), (null, ElementSearchScope.WindowAndPopups));
    }

    [Fact]
    public async Task TwoItemsOfTheName_AreAmbiguous_AndNothingIsActedOn()
    {
        _find = query => Found(query, Item("w1e1", "File"), Item("w1e7", "File"));

        var result = await RunAsync("File", "Save As");

        result.ErrorCode.Should().Be(nameof(UIElementErrorCode.AmbiguousElement));
        _acted.Should().BeEmpty();
        result.Steps.Should().BeNull();
    }

    [Fact]
    public async Task ALongItemNameQuotedInAMessage_IsNeverCutBetweenTheHalvesOfASurrogatePair()
    {
        // Arrange - an emoji (two UTF-16 units) across the quoted name's cut at 100 characters
        var start = new string('f', 99);
        _find = query => Found(query, Item("w1e1", start + "\U0001F600 one"), Item("w1e7", start + "\U0001F600 two"));

        // Act
        var result = await RunAsync("File", "Save As");

        // Assert
        result.ErrorCode.Should().Be(nameof(UIElementErrorCode.AmbiguousElement));
        result.Error.Should().Contain($"w1e1 '{start}'");
        System.Text.Encoding.UTF8.GetString(System.Text.Encoding.UTF8.GetBytes(result.Error!)).Should().Be(result.Error,
            "no half of a surrogate pair is left in the message");
    }

    [Theory]
    [InlineData(ElementSearchTruncation.PartsSkipped)]
    [InlineData(ElementSearchTruncation.Timeout)]
    [InlineData(ElementSearchTruncation.WindowLimit)]
    public async Task OneMatchOfASearchThatDidNotFinish_IsNotActedOn(ElementSearchTruncation reason)
    {
        // A popup of the window did not answer: another visible 'Delete' may be in it
        _find = query => Found(query, Item("w1e1", "Delete")) with { Truncated = true, TruncationReason = reason };

        var result = await RunAsync("Delete");

        result.Success.Should().BeFalse();
        result.ErrorCode.Should().Be(nameof(UIElementErrorCode.OperationTimedOut));
        result.Error.Should().Contain("'Delete' (item 1 of 1)").And.Contain(reason.ToString()).And.Contain("only one");
        _acted.Should().BeEmpty();
    }

    [Fact]
    public async Task ATruncatedMatchInTheSubmenu_GivesWayToTheOneFoundBelowItsItem()
    {
        // The submenu window's search stopped early; below File, the search finished with one Save As
        _find = query => query switch
        {
            { WithinRef: null } when Matches(query, "File") && query.WindowHandle == Window => Found(query, Item("w1e1", "File")),
            { WithinRef: null } when Matches(query, "Save As") => Found(query, Item("w2e1", "Save As...", FileMenu)) with
            {
                Truncated = true, TruncationReason = ElementSearchTruncation.Timeout
            },
            { WithinRef: "w1e1" } when Matches(query, "Save As") => Found(query, Item("w1e2", "Save As...")),
            _ => Found(query)
        };

        var result = await RunAsync("File", "Save As");

        result.Success.Should().BeTrue(result.Error);
        _acted.Select(a => a.Ref).Should().Equal("w1e1", "w1e2");
    }

    [Fact]
    public async Task ARefusedAction_StopsThePath_WithItsCode()
    {
        _act = request => new ElementActionResult(false, "expand", request.Ref, Error: "Input is paused.", ErrorCode: "PausedByUser");

        var result = await RunAsync("File", "Save As");

        result.ErrorCode.Should().Be("PausedByUser");
        result.Action.Should().Be("menu_path");
        result.Error.Should().Contain("'File' (item 1 of 2)");
        _acted.Should().ContainSingle();
    }

    [Fact]
    public async Task AStartRef_IsWhereTheFirstItemIsSearched()
    {
        await CreateSut().RunAsync(new MenuPathRequest(Window, "w1e30", ["File"], Scope: null, Timeout: TimeSpan.FromSeconds(1)));

        _queries[0].Should().Match<ElementQuery>(q => q.WithinRef == "w1e30" && q.Scope == ElementSearchScope.Window);
    }

    [Fact]
    public async Task AStartRefMovedToAReCreatedMenu_IsElementStale_AndNothingIsOpened()
    {
        // Arrange - the menu the ref was given for closed; the ref now names a look-alike menu, where File is found
        _find = query => Found(query, Item("w1e31", "File")) with
        {
            RefMoved = true,
            Warning = "withinRef: Ref 'w1e30' was moved: the element it was given for is gone."
        };

        // Act
        var result = await CreateSut().RunAsync(new MenuPathRequest(Window, "w1e30", ["File", "Save As"], Scope: null,
            Timeout: TimeSpan.FromSeconds(1)));

        // Assert
        result.Success.Should().BeFalse();
        result.ErrorCode.Should().Be(nameof(UIElementErrorCode.ElementStale));
        result.RefMoved.Should().BeTrue();
        result.Error.Should().Contain("was moved");
        _acted.Should().BeEmpty();
    }
}
