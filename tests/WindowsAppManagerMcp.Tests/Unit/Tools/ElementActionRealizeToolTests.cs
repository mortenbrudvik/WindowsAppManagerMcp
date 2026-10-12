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
    private int _foundOnPage = 3;
    private Func<int, bool> _atEnd = _ => false;
    private Func<ElementActionRequest, ElementActionResult?> _refuse = _ => null;
    private Func<ElementQuery, FindElementsResult?> _firstSearch = _ => null;

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
                        .Select(i => new FoundElement($"w1e{2 + i}", "Messages", "List", null, null, null, null, true, false, ["Scroll"]))
                        .ToArray());
                }

                // Below the list, the item appears on the _foundOnPage-th page; the first search finds nothing
                return query.WithinRef == "w1e2" && ++listSearches == _foundOnPage
                    ? Found(1, new FoundElement("w1e9", "Old message", "ListItem", null, null, null, null, true, true, []))
                    : Found(0);
            });

        var pages = 0;
        _actions.Setup(a => a.ActAsync(It.IsAny<ElementActionRequest>(), It.IsAny<CancellationToken>()))
            .Callback((ElementActionRequest request, CancellationToken _) => _requests.Add(request))
            .ReturnsAsync((ElementActionRequest request, CancellationToken _) =>
                _refuse(request) ?? (request.Action == ElementActionKind.Scroll
                    ? new ElementActionResult(true, "scroll", request.Ref, Handle, ElementActionStatus.Done, ElementActionMethod.Scroll,
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
        var result = await CreateSut().ElementAction(ElementActionKind.Realize, handle: Handle, name: "Old message");

        result.ErrorCode.Should().Be("ElementNotFound");
        _requests.Should().BeEmpty();
    }

    [Fact]
    public async Task Realize_WithMaxScrolls_ScrollsTheOneList_PageByPage_ThroughTheActionService_UntilTheItemIsFound()
    {
        // Act - a web list without ItemContainer: the item appears on the third page
        var result = await CreateSut().ElementAction(ElementActionKind.Realize, handle: Handle, name: "Old message", maxScrolls: 10,
            allowHostTarget: true, timeoutMs: 20000);

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
        var result = await CreateSut().ElementAction(ElementActionKind.Realize, handle: Handle, name: "Old message", maxScrolls: 4);

        // Assert
        result.ErrorCode.Should().Be("ElementNotFound");
        result.Error.Should().Contain("4 pages").And.Contain("maxScrolls 4");
        _requests.Select(r => r.Direction).Should().Equal(
            ElementScrollDirection.Down, ElementScrollDirection.Down, ElementScrollDirection.Up, ElementScrollDirection.Up);
    }

    [Fact]
    public async Task Realize_WithMaxScrolls_AndSeveralLists_IsAmbiguous_AndNothingIsScrolled()
    {
        _listCount = 2;

        var result = await CreateSut().ElementAction(ElementActionKind.Realize, handle: Handle, name: "Old message", maxScrolls: 10);

        result.ErrorCode.Should().Be("AmbiguousElement");
        result.Error.Should().Contain("w1e2").And.Contain("w1e3").And.Contain("Nothing was scrolled");
        _requests.Should().BeEmpty();
    }

    [Fact]
    public async Task Realize_WithMaxScrolls_AndNoList_IsElementNotFound_AndSaysSo()
    {
        _listCount = 0;

        var result = await CreateSut().ElementAction(ElementActionKind.Realize, handle: Handle, name: "Old message", maxScrolls: 10);

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
        var result = await CreateSut().ElementAction(ElementActionKind.Realize, handle: Handle, name: "Old message", maxScrolls: 10);

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
        var result = await CreateSut().ElementAction(ElementActionKind.Realize, handle: Handle, name: "Old message", maxScrolls: 10);

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
            timeoutMs: 5000);

        // Assert - the later pages, their searches and the realize get what is left, not the whole timeout again
        result.Success.Should().BeTrue(result.Error);
        _requests[0].Timeout.Should().BeGreaterThan(TimeSpan.FromSeconds(4.5));
        _requests.Skip(1).Should().OnlyContain(r => r.Timeout <= TimeSpan.FromSeconds(3.5));
        _queries.Where(q => q.WithinRef == "w1e2").Should().OnlyContain(q => q.Timeout <= TimeSpan.FromSeconds(3.5));
    }
}
