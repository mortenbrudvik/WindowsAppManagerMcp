namespace WindowsAppManagerMcp.Tests.Unit.Tools;

using WindowsAppManagerMcp.Infrastructure;
using WindowsAppManagerMcp.Tools;

/// <summary>
/// element_action realize by criteria (roadmap M3.10): items the list has not realized are searched through find_ui_elements'
/// own request model (searchVirtualized), and with maxScrolls the window's one list is scrolled page by page, every page an
/// element_action scroll through the action service (so each passes its checks), until the item is found and realized. The
/// search and the action service are mocks: nothing is read or done.
/// </summary>
public class ElementActionRealizeToolTests
{
    private static readonly nint Handle = 0x8200;

    private readonly Mock<IElementActionService> _actions = new();
    private readonly Mock<IElementSearchService> _search = new();
    private readonly Mock<IElementRegistry> _registry = new();
    private readonly List<ElementQuery> _queries = [];
    private readonly List<ElementActionRequest> _requests = [];
    private int _listCount = 1;
    private Func<int, string[]> _listPatterns = _ => ["Scroll"];
    private string _itemList = "w1e2";
    private int _foundOnPage = 3;
    private Func<int, bool> _atEnd = _ => false;
    private Func<ElementActionRequest, ElementActionResult?> _refuse = _ => null;
    private Func<ElementQuery, FindElementsResult?> _firstSearch = _ => null;
    private double? _startPercent;

    public ElementActionRealizeToolTests()
    {
        var listSearches = 0;
        _search.Setup(s => s.FindAsync(It.IsAny<ElementQuery>(), It.IsAny<CancellationToken>()))
            .Callback((ElementQuery query, CancellationToken _) => _queries.Add(query))
            .ReturnsAsync((ElementQuery query, CancellationToken _) =>
            {
                if (_firstSearch(query) is { } first)
                {
                    return first;
                }

                if (query.ControlTypes is { } types && types.Contains("List"))
                {
                    return Found(_listCount, Enumerable.Range(0, _listCount)
                        .Select(i => new FoundElement($"w1e{2 + i}", "Messages", "List", null, null, null, null, true, false, _listPatterns(i)))
                        .ToArray());
                }

                // Below the list, the item appears on the _foundOnPage-th page; the first search finds nothing
                return query.WithinRef == _itemList && ++listSearches == _foundOnPage
                    ? Found(1, new FoundElement("w1e9", "Old message", "ListItem", null, null, null, null, true, true, []))
                    : Found(0);
            });

        var pages = 0;
        _actions.Setup(a => a.ActAsync(It.IsAny<ElementActionRequest>(), It.IsAny<CancellationToken>()))
            .Callback((ElementActionRequest request, CancellationToken _) => _requests.Add(request))
            .ReturnsAsync((ElementActionRequest request, CancellationToken _) =>
                _refuse(request) ?? (request.Action == ElementActionKind.Scroll
                    ? request.ScrollPercent is { } percent
                        ? new ElementActionResult(true, "scroll", request.Ref, Handle, ElementActionStatus.Done, ElementActionMethod.Scroll,
                            OutcomeVerified: true, Scroll: new ElementScrollPosition(null, percent, false))
                        : new ElementActionResult(true, "scroll", request.Ref, Handle, ElementActionStatus.Done, ElementActionMethod.Scroll,
                            Before: new ElementState("Messages", true, false, VerticalScrollPercent: pages == 0 ? _startPercent : 10),
                            Scroll: new ElementScrollPosition(null, 10, _atEnd(++pages)))
                    : new ElementActionResult(true, "realize", request.Ref, Handle, ElementActionStatus.Done, ElementActionMethod.VirtualizedItem)));
        _registry.Setup(r => r.Lookup(It.IsAny<string>())).Returns((string r) => new ElementLookup(r, Handle, null, 1));
    }

    private ElementActionTools CreateSut() => new(_actions.Object, _search.Object, _registry.Object);

    private static FindElementsResult Found(int matchCount, params FoundElement[] elements) =>
        new(true, Handle, "Chat", matchCount, matchCount > elements.Length, elements, 1);

    private static async Task<ToolErrorException> RefusedAsync(Func<Task> call) =>
        (await FluentActions.Awaiting(call).Should().ThrowAsync<ToolErrorException>()).Which;

    [Theory]
    [InlineData("Item 05000", ElementNameMatch.Exact, null, true)]
    [InlineData("Item", ElementNameMatch.Contains, "row-5000", true)]
    [InlineData("Item", ElementNameMatch.Contains, null, false)]
    public async Task Realize_ByCriteria_AlsoSearchesTheItemsTheListHasNotRealized_WhenTheyNameOneWholeProperty(
        string name, ElementNameMatch nameMatch, string? automationId, bool searchVirtualized)
    {
        _foundOnPage = 0;

        await CreateSut().ElementAction(ElementActionKind.Realize, handle: Handle, name: name, nameMatch: nameMatch, automationId: automationId);

        _queries.Should().ContainSingle().Which.SearchVirtualized.Should().Be(searchVirtualized);
    }

    [Fact]
    public async Task OtherActions_ByCriteria_DoNotSearchTheListsItems()
    {
        await CreateSut().ElementAction(ElementActionKind.Invoke, handle: Handle, name: "Item 05000");

        _queries.Should().ContainSingle().Which.SearchVirtualized.Should().BeFalse("an item that is not realized cannot be invoked: realize it first");
    }

    [Theory]
    [InlineData(ElementActionKind.Collapse, null)]
    [InlineData(ElementActionKind.Realize, "w1e7")]
    public async Task MaxScrolls_IsOnlyForRealizeByCriteria(ElementActionKind action, string? elementRef)
    {
        var error = await RefusedAsync(() => CreateSut().ElementAction(action, @ref: elementRef, handle: elementRef == null ? Handle : null,
            name: elementRef == null ? "Folder" : null, maxScrolls: 5));

        error.ErrorCode.Should().Be(ToolErrorReporting.InvalidArgumentsErrorCode);
        error.Message.Should().Contain("maxScrolls is for realize with element criteria").And.EndWith("Nothing was done.");
        _actions.VerifyNoOtherCalls();
        _search.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task Realize_WithoutMaxScrolls_DoesNotScroll_WhenTheItemIsNotFound()
    {
        var result = await CreateSut().ElementAction(ElementActionKind.Realize, handle: Handle, name: "Old message").Payload<ElementActionResult>();

        result.ErrorCode.Should().Be("ElementNotFound");
        _requests.Should().BeEmpty();
    }

    [Fact]
    public async Task Realize_WithMaxScrolls_ScrollsTheOneList_PageByPage_ThroughTheActionService_UntilTheItemIsFound()
    {
        // Act - a web list without ItemContainer: the item appears on the third page
        var result = await CreateSut().ElementAction(ElementActionKind.Realize, handle: Handle, name: "Old message", maxScrolls: 10,
            allowHostTarget: true, timeoutMs: 20000).Payload<ElementActionResult>();

        // Assert - every page is an element_action scroll of the list, then the item is realized: no pattern call around them
        result.Success.Should().BeTrue(result.Error);
        result.Ref.Should().Be("w1e9");
        result.Warnings.Should().Contain(w => w.Contains("3 pages") && w.Contains("w1e2"));
        _requests.Select(r => (r.Action, r.Ref, r.Direction)).Should().Equal(
            (ElementActionKind.Scroll, "w1e2", ElementScrollDirection.Down),
            (ElementActionKind.Scroll, "w1e2", ElementScrollDirection.Down),
            (ElementActionKind.Scroll, "w1e2", ElementScrollDirection.Down),
            (ElementActionKind.Realize, "w1e9", (ElementScrollDirection?)null));
        _requests.Should().OnlyContain(r => r.AllowHostTarget && r.Timeout <= TimeSpan.FromSeconds(20) && r.Timeout > TimeSpan.FromSeconds(19),
            "each step gets what is left of the one timeout");
        _requests.Where(r => r.Action == ElementActionKind.Scroll).Should().OnlyContain(r => r.Amount == ElementScrollAmount.Page);

        // The lists are the window's own, visible ones; each page is searched below the list, as the criteria say
        var lists = _queries.Single(q => q.ControlTypes != null && q.ControlTypes.Contains("List"));
        (lists.Scope, lists.VisibleOnly).Should().Be((ElementSearchScope.Window, true));
        _queries.Where(q => q.WithinRef == "w1e2").Should().HaveCount(3)
            .And.OnlyContain(q => q.Name == "Old message" && !q.SearchVirtualized && q.Scope == null);
    }

    [Fact]
    public async Task Realize_WithMaxScrolls_TurnsBackUpAtTheEnd_AndStopsAtMaxScrolls()
    {
        // Arrange - the list ends after two pages down; the item is never found
        _foundOnPage = 100;
        _atEnd = page => page == 2;

        // Act
        var result = await CreateSut().ElementAction(ElementActionKind.Realize, handle: Handle, name: "Old message", maxScrolls: 4).Payload<ElementActionResult>();

        // Assert
        result.ErrorCode.Should().Be("ElementNotFound");
        result.Error.Should().Contain("4 pages").And.Contain("maxScrolls 4");
        _requests.Select(r => r.Direction).Should().Equal(
            ElementScrollDirection.Down, ElementScrollDirection.Down, ElementScrollDirection.Up, ElementScrollDirection.Up);
    }

    [Fact]
    public async Task Realize_WithMaxScrolls_PutsTheListBackWhereItWas_WhenTheItemIsNotFound()
    {
        // Arrange - the list was 40% down before the search (roadmap M3.F19); the item is never found
        _foundOnPage = 100;
        _startPercent = 40;
        _atEnd = page => page == 2;

        // Act
        var result = await CreateSut().ElementAction(ElementActionKind.Realize, handle: Handle, name: "Old message", maxScrolls: 4,
            allowHostTarget: true).Payload<ElementActionResult>();

        // Assert - one more element_action scroll, to the position it was at, with every check
        result.ErrorCode.Should().Be("ElementNotFound");
        result.Error.Should().Contain("put back where it was (40% down)").And.NotContain("left where the search ended");
        _requests.Should().HaveCount(5);
        var putBack = _requests[^1];
        (putBack.Action, putBack.Ref, putBack.ScrollPercent, putBack.AllowHostTarget).Should().Be((ElementActionKind.Scroll, "w1e2", 40.0, true));
        putBack.Direction.Should().Be(ElementScrollDirection.Up, "the position is on the axis the search scrolled");
        _requests.Take(4).Should().OnlyContain(r => r.ScrollPercent == null);
    }

    [Fact]
    public async Task Realize_WithMaxScrolls_PutsTheListBack_AlsoWhenTheTimeoutPasses()
    {
        // Arrange - every page takes longer than the whole search may
        _foundOnPage = 100;
        _startPercent = 0;
        _refuse = request =>
        {
            if (request.ScrollPercent == null)
            {
                Thread.Sleep(1100);
            }

            return null;
        };

        // Act
        var result = await CreateSut().ElementAction(ElementActionKind.Realize, handle: Handle, name: "Old message", maxScrolls: 10,
            timeoutMs: 1000).Payload<ElementActionResult>();

        // Assert
        result.ErrorCode.Should().Be("OperationTimedOut");
        result.Error.Should().Contain("put back where it was (0% down)");
        _requests[^1].ScrollPercent.Should().Be(0);
    }

    [Theory]
    [InlineData(ElementSearchTruncation.Timeout)]
    [InlineData(ElementSearchTruncation.PartsSkipped)]
    public async Task Realize_WithMaxScrolls_PutsTheListBack_WhenAPagesSearchDoesNotFinish(ElementSearchTruncation reason)
    {
        // Arrange - the list was 40% down before the search; the second page's search stopped (the deadline passed during it,
        // or a part of the list could not be searched) before it found anything
        _foundOnPage = 100;
        _startPercent = 40;
        var pageSearches = 0;
        _firstSearch = query => query.WithinRef == _itemList && ++pageSearches == 2
            ? new FindElementsResult(true, Handle, "Chat", 0, true, [], 1, TruncationReason: reason)
            : null;

        // Act
        var result = await CreateSut().ElementAction(ElementActionKind.Realize, handle: Handle, name: "Old message", maxScrolls: 10,
            allowHostTarget: true).Payload<ElementActionResult>();

        // Assert - the page search's own code, and the list put back with one more element_action scroll
        result.ErrorCode.Should().Be("OperationTimedOut");
        result.Error.Should().Contain("after scrolling").And.Contain("2 pages").And.Contain("put back where it was (40% down)");
        _requests.Should().HaveCount(3);
        (_requests[^1].Action, _requests[^1].Ref, _requests[^1].ScrollPercent, _requests[^1].AllowHostTarget)
            .Should().Be((ElementActionKind.Scroll, "w1e2", 40.0, true));
    }

    [Fact]
    public async Task Realize_WithMaxScrolls_PutsTheListBack_WhenAPagesOnlyMatchesAreOfAnAppThePolicyRefuses()
    {
        // Arrange - on the second page the criteria match only an element of an app the target policy refuses
        _foundOnPage = 100;
        _startPercent = 40;
        var pageSearches = 0;
        const string leftOut = "1 element of window 33280 belongs to process 777 ('KeePass'), ...";
        _firstSearch = query => query.WithinRef == _itemList && ++pageSearches == 2
            ? new FindElementsResult(true, Handle, "Chat", 0, true, [], 1, TruncationReason: ElementSearchTruncation.PartsSkipped,
                Warning: leftOut)
            {
                PolicyLeftOut = new PolicyLeftOutMatches(1, leftOut, OtherwiseComplete: true)
            }
            : null;

        // Act
        var result = await CreateSut().ElementAction(ElementActionKind.Realize, handle: Handle, name: "Old message", maxScrolls: 10)
            .Payload<ElementActionResult>();

        // Assert
        result.ErrorCode.Should().Be("TargetDeniedByPolicy");
        result.Error.Should().Contain("KeePass").And.Contain("put back where it was (40% down)");
        _requests[^1].ScrollPercent.Should().Be(40);
    }

    [Fact]
    public async Task Realize_WithMaxScrolls_LeavesTheListWhereTwoItemsMatch_SoTheirRefsStayOnScreen()
    {
        // Arrange - the second page shows two matching items
        _foundOnPage = 100;
        _startPercent = 40;
        var pageSearches = 0;
        _firstSearch = query => query.WithinRef == _itemList && ++pageSearches == 2
            ? Found(2,
                new FoundElement("w1e9", "Old message", "ListItem", null, null, null, null, true, false, []),
                new FoundElement("w1e10", "Old message", "ListItem", null, null, null, null, true, false, []))
            : null;

        // Act
        var result = await CreateSut().ElementAction(ElementActionKind.Realize, handle: Handle, name: "Old message", maxScrolls: 10)
            .Payload<ElementActionResult>();

        // Assert
        result.ErrorCode.Should().Be("AmbiguousElement");
        result.Error.Should().Contain("w1e9").And.Contain("w1e10");
        _requests.Should().HaveCount(2).And.OnlyContain(r => r.ScrollPercent == null);
    }

    [Fact]
    public async Task Realize_WithMaxScrolls_SaysWhy_WhenTheListCannotBePutBack()
    {
        // Arrange - the user paused input during the search's last page: the scroll back is refused like any action
        _foundOnPage = 100;
        _startPercent = 40;
        _atEnd = page => page == 1;
        _refuse = request => request.ScrollPercent != null
            ? new ElementActionResult(false, "scroll", request.Ref, Handle, Error: "Input is paused by the user.", ErrorCode: "PausedByUser")
            : null;

        // Act
        var result = await CreateSut().ElementAction(ElementActionKind.Realize, handle: Handle, name: "Old message", maxScrolls: 4).Payload<ElementActionResult>();

        // Assert - the search's own outcome, and why the list stays where the search ended
        result.ErrorCode.Should().Be("ElementNotFound");
        result.Error.Should().Contain("could not be put back").And.Contain("paused").And.Contain("left where the search ended");
    }

    [Fact]
    public async Task Realize_WithMaxScrolls_SaysWhereTheListIs_WhenTheScrollBackStopsNearWhereItWas()
    {
        // Arrange - a list that scrolls by items stops near the position rather than on it
        _foundOnPage = 100;
        _startPercent = 40;
        _atEnd = page => page == 1;
        _refuse = request => request.ScrollPercent != null
            ? new ElementActionResult(true, "scroll", request.Ref, Handle, ElementActionStatus.Done, ElementActionMethod.Scroll,
                OutcomeVerified: false, Scroll: new ElementScrollPosition(null, 37.5, false))
            : null;

        // Act
        var result = await CreateSut().ElementAction(ElementActionKind.Realize, handle: Handle, name: "Old message", maxScrolls: 4).Payload<ElementActionResult>();

        // Assert
        result.Error.Should().Contain("toward where it was (40% down)").And.Contain("37.5% down now");
    }

    [Fact]
    public async Task Realize_WithMaxScrolls_LeavesTheList_WhenWhereItWasIsNotKnown()
    {
        // Arrange - the first page's state has no vertical position (the list did not report one)
        _foundOnPage = 100;
        _startPercent = null;
        _atEnd = page => page == 2;

        // Act
        var result = await CreateSut().ElementAction(ElementActionKind.Realize, handle: Handle, name: "Old message", maxScrolls: 4).Payload<ElementActionResult>();

        // Assert
        result.ErrorCode.Should().Be("ElementNotFound");
        result.Error.Should().Contain("left where the search ended");
        _requests.Should().HaveCount(4).And.OnlyContain(r => r.ScrollPercent == null);
    }

    [Fact]
    public async Task Realize_WithMaxScrolls_DoesNotPutTheListBack_WhenTheItemIsFound()
    {
        _startPercent = 40;

        var result = await CreateSut().ElementAction(ElementActionKind.Realize, handle: Handle, name: "Old message", maxScrolls: 10).Payload<ElementActionResult>();

        result.Success.Should().BeTrue(result.Error);
        _requests.Should().OnlyContain(r => r.ScrollPercent == null);
    }

    [Theory]
    [InlineData(null, ElementSearchScope.Window)]
    [InlineData(ElementSearchScope.Window, ElementSearchScope.Window)]
    [InlineData(ElementSearchScope.Desktop, ElementSearchScope.Window)]
    [InlineData(ElementSearchScope.WindowAndPopups, ElementSearchScope.WindowAndPopups)]
    public async Task Realize_WithMaxScrolls_LooksForTheListInThePopupsToo_OnlyWhenTheScopeSaysSo(
        ElementSearchScope? scope, ElementSearchScope listScope)
    {
        // Act - an open dropdown's list is scrolled only when the agent asked for the window's popups (roadmap M3.F19)
        await CreateSut().ElementAction(ElementActionKind.Realize, handle: Handle, name: "Old message", maxScrolls: 10, scope: scope);

        // Assert
        _queries.Single(q => q.ControlTypes != null && q.ControlTypes.Contains("List")).Scope.Should().Be(listScope);
    }

    [Fact]
    public async Task Realize_WithMaxScrolls_ScrollsAListInAPopup_AndSearchesIt_InThePopupsWindow()
    {
        // Arrange - the one list is in a popup of the window: its ref is registered under the popup's own window
        nint popup = 0x8300;
        _firstSearch = query => query.ControlTypes is { } types && types.Contains("List")
            ? Found(1, new FoundElement("w2e5", "Suggestions", "List", null, null, null, null, true, false, ["Scroll"]))
            : null;
        _itemList = "w2e5";
        _registry.Setup(r => r.Lookup("w2e5")).Returns(new ElementLookup("w2e5", popup, null, 2));

        // Act
        var result = await CreateSut().ElementAction(ElementActionKind.Realize, handle: Handle, name: "Old message", maxScrolls: 10,
            scope: ElementSearchScope.WindowAndPopups).Payload<ElementActionResult>();

        // Assert - each page scrolls the popup's list through the action service, and is searched in the popup's window
        result.Success.Should().BeTrue(result.Error);
        result.Ref.Should().Be("w1e9");
        _requests.Where(r => r.Action == ElementActionKind.Scroll).Should().OnlyContain(r => r.Ref == "w2e5");
        _queries.Where(q => q.WithinRef == "w2e5").Should().HaveCount(3).And.OnlyContain(q => q.WindowHandle == popup);
    }

    [Fact]
    public async Task Realize_WithMaxScrolls_AndSeveralListsThatScroll_IsAmbiguous_AndNothingIsScrolled()
    {
        _listCount = 2;

        var result = await CreateSut().ElementAction(ElementActionKind.Realize, handle: Handle, name: "Old message", maxScrolls: 10).Payload<ElementActionResult>();

        result.ErrorCode.Should().Be("AmbiguousElement");
        result.Error.Should().Contain("w1e2").And.Contain("w1e3").And.Contain("Nothing was scrolled").And.Contain("listRef");
        _requests.Should().BeEmpty();
    }

    [Fact]
    public async Task Realize_WithMaxScrolls_AndSeveralLists_ScrollsTheOneThatScrollsItself()
    {
        // Arrange - a web page's navigation list beside its results (roadmap M3.F18): only the results list has a Scroll
        // pattern of its own; the navigation list scrolls with the page
        _listCount = 2;
        _listPatterns = i => i == 1 ? ["Scroll"] : [];
        _itemList = "w1e3";

        // Act
        var result = await CreateSut().ElementAction(ElementActionKind.Realize, handle: Handle, name: "Old message", maxScrolls: 10).Payload<ElementActionResult>();

        // Assert
        result.Success.Should().BeTrue(result.Error);
        _requests.Where(r => r.Action == ElementActionKind.Scroll).Should().OnlyContain(r => r.Ref == "w1e3");
    }

    [Fact]
    public async Task Realize_WithMaxScrolls_AndSeveralListsOfWhichMoreWereNotReturned_IsAmbiguous()
    {
        // Arrange - the search returned only some of the lists: one of those it left out may scroll too
        _listCount = 2;
        _listPatterns = i => i == 1 ? ["Scroll"] : [];
        _firstSearch = query => query.ControlTypes is { } types && types.Contains("List")
            ? Found(30,
                new FoundElement("w1e2", "Nav", "List", null, null, null, null, true, false, []),
                new FoundElement("w1e3", "Results", "List", null, null, null, null, true, false, ["Scroll"]))
            : null;

        // Act
        var result = await CreateSut().ElementAction(ElementActionKind.Realize, handle: Handle, name: "Old message", maxScrolls: 10).Payload<ElementActionResult>();

        // Assert
        result.ErrorCode.Should().Be("AmbiguousElement");
        _requests.Should().BeEmpty();
    }

    [Fact]
    public async Task Realize_WithMaxScrolls_AndListRef_ScrollsThatList_WithoutLookingForLists()
    {
        // Arrange - Explorer's navigation tree and file list both scroll: the agent names the list
        _listCount = 2;
        _itemList = "w1e3";

        // Act
        var result = await CreateSut().ElementAction(ElementActionKind.Realize, handle: Handle, name: "Old message", maxScrolls: 10,
            listRef: "w1e3").Payload<ElementActionResult>();

        // Assert
        result.Success.Should().BeTrue(result.Error);
        result.Ref.Should().Be("w1e9");
        _requests.Where(r => r.Action == ElementActionKind.Scroll).Should().OnlyContain(r => r.Ref == "w1e3");
        _queries.Should().NotContain(q => q.ControlTypes != null && q.ControlTypes.Contains("List"));
    }

    [Theory]
    [InlineData(0, null)]           // without maxScrolls nothing is scrolled
    [InlineData(10, "w1e7")]        // with a ref there is nothing to search for
    public async Task ListRef_IsOnlyForRealizeByCriteriaWithMaxScrolls(int maxScrolls, string? elementRef)
    {
        var error = await RefusedAsync(() => CreateSut().ElementAction(ElementActionKind.Realize, @ref: elementRef,
            handle: elementRef == null ? Handle : null, name: elementRef == null ? "Old message" : null, maxScrolls: maxScrolls,
            listRef: "w1e3"));

        error.ErrorCode.Should().Be(ToolErrorReporting.InvalidArgumentsErrorCode);
        error.Message.Should().EndWith("Nothing was done.");
        _actions.VerifyNoOtherCalls();
        _search.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task ListRef_OfAnotherWindow_IsRefused_AndNothingIsScrolled()
    {
        // Arrange
        _registry.Setup(r => r.Lookup("w5e3")).Returns(new ElementLookup("w5e3", 0x9900, null, 5));

        // Act
        var error = await RefusedAsync(() => CreateSut().ElementAction(ElementActionKind.Realize, handle: Handle, name: "Old message",
            maxScrolls: 10, listRef: "w5e3"));

        // Assert
        error.ErrorCode.Should().Be(ToolErrorReporting.InvalidArgumentsErrorCode);
        error.Message.Should().Contain("w5e3").And.Contain("another window");
        _requests.Should().BeEmpty();
    }

    [Fact]
    public async Task Realize_WithMaxScrolls_AndNoList_IsElementNotFound_AndSaysSo()
    {
        _listCount = 0;

        var result = await CreateSut().ElementAction(ElementActionKind.Realize, handle: Handle, name: "Old message", maxScrolls: 10).Payload<ElementActionResult>();

        result.ErrorCode.Should().Be("ElementNotFound");
        result.Error.Should().Contain("No visible list");
        _requests.Should().BeEmpty();
    }

    [Fact]
    public async Task Realize_WithMaxScrolls_StopsAtTheFirstScrollTheChecksRefuse()
    {
        // Arrange - the user paused input after the first page
        _foundOnPage = 100;
        _refuse = request => _requests.Count > 1
            ? new ElementActionResult(false, "scroll", request.Ref, Handle, Error: "Input is paused by the user.", ErrorCode: "PausedByUser")
            : null;

        // Act
        var result = await CreateSut().ElementAction(ElementActionKind.Realize, handle: Handle, name: "Old message", maxScrolls: 10).Payload<ElementActionResult>();

        // Assert
        result.ErrorCode.Should().Be("PausedByUser");
        result.Action.Should().Be("realize");
        result.Error.Should().Contain("after 1 pages").And.Contain("paused");
        _requests.Should().HaveCount(2);
    }

    [Theory]
    [InlineData(ElementSearchTruncation.Timeout)]
    [InlineData(ElementSearchTruncation.PartsSkipped)]
    public async Task Realize_ByCriteria_WhenTheListsWereNotAllSearched_IsOperationTimedOut_AndNothingIsDoneOrScrolled(
        ElementSearchTruncation reason)
    {
        // Arrange - the list lookup found one item, then stopped (the deadline, a provider that failed): a look-alike further on
        // was never checked, so the one match is not known to be the only one
        _firstSearch = query => query.SearchVirtualized
            ? new FindElementsResult(true, Handle, "Chat", 1, true,
                [new FoundElement("w1e9", "Old message", "ListItem", null, null, null, null, true, true, [], Virtualized: true)], 1,
                TruncationReason: reason)
            : null;

        // Act
        var result = await CreateSut().ElementAction(ElementActionKind.Realize, handle: Handle, name: "Old message", maxScrolls: 10).Payload<ElementActionResult>();

        // Assert - not ElementNotFound, and the scroll fallback does not start
        result.Success.Should().BeFalse();
        result.ErrorCode.Should().Be("OperationTimedOut");
        result.Error.Should().Contain("Nothing was done");
        _requests.Should().BeEmpty();
        _queries.Should().ContainSingle();
    }

    [Fact]
    public async Task Realize_WithMaxScrolls_GivesEachStepWhatIsLeftOfTheTimeout()
    {
        // Arrange - the first page takes a second and a half
        _refuse = _ =>
        {
            if (_requests.Count == 1)
            {
                Thread.Sleep(1500);
            }

            return null;
        };

        // Act
        var result = await CreateSut().ElementAction(ElementActionKind.Realize, handle: Handle, name: "Old message", maxScrolls: 10,
            timeoutMs: 5000).Payload<ElementActionResult>();

        // Assert - the later pages, their searches and the realize get what is left, not the whole timeout again
        result.Success.Should().BeTrue(result.Error);
        _requests[0].Timeout.Should().BeGreaterThan(TimeSpan.FromSeconds(4.5));
        _requests.Skip(1).Should().OnlyContain(r => r.Timeout <= TimeSpan.FromSeconds(3.5));
        _queries.Where(q => q.WithinRef == "w1e2").Should().OnlyContain(q => q.Timeout <= TimeSpan.FromSeconds(3.5));
    }

    [Fact]
    public async Task Realize_WithMaxScrolls_AndObserveState_IsObservedOnce_AfterTheItemIsRealized()
    {
        // Arrange - observe (roadmap M3.12) wraps the whole scroll search (roadmap M3.10), not each page
        var order = new List<string>();
        var desktopState = new Mock<IDesktopStateService>();
        var windows = new Mock<IWindowService>();
        windows.Setup(w => w.IsValidWindow(Handle)).Returns(true);
        desktopState.Setup(s => s.GetStateAsync(It.IsAny<DesktopStateRequest>(), It.IsAny<CancellationToken>()))
            .Callback((DesktopStateRequest request, CancellationToken _) => order.Add($"observe {request.Window} after {_requests.Count} actions"))
            .ReturnsAsync(new DesktopStateSnapshot(new DesktopState(true, Handle, "Chat", null, SessionLockState.Unlocked, null, null, new DesktopInputState(false),
                null, null, null, null, null, null, 9), null));
        var sut = new ElementActionTools(_actions.Object, _search.Object, _registry.Object, windows.Object, desktopState.Object);

        // Act
        var result = await sut.ElementAction(ElementActionKind.Realize, handle: Handle, name: "Old message", maxScrolls: 10,
            observe: ObserveMode.State);

        // Assert - three pages scrolled and the item realized, then one observation of the list's window
        var payload = result.Payload<ElementActionResult>();
        payload.Success.Should().BeTrue(payload.Error);
        (payload.Action, payload.Ref).Should().Be(("realize", "w1e9"));
        order.Should().Equal($"observe {Handle} after 4 actions");
        payload.Observed!.WindowHandle.Should().Be(Handle);
    }
}
