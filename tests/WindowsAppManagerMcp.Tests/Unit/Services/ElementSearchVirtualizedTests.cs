namespace WindowsAppManagerMcp.Tests.Unit.Services;

/// <summary>
/// find_ui_elements' searchVirtualized (roadmap M3.10) over an in-memory virtualized list (<see cref="FakeVirtualList"/>): items
/// the list has not realized are found through its ItemContainer pattern, with refs and virtualized=true, and nothing is
/// realized or scrolled, since the search only reads. No real window is read.
/// </summary>
public class ElementSearchVirtualizedTests
{
    private static readonly nint Handle = 0x7300;
    private const int Pid = 300;

    private readonly FakeUiaTree _tree = new();
    private readonly InlineUiaWorker _worker = new();
    private readonly Mock<INativeWindowWrapper> _windows = new();
    private readonly ElementRegistry _registry;
    private readonly FakeUiaNode _window;
    private readonly FakeVirtualList _list;

    public ElementSearchVirtualizedTests()
    {
        _windows.Setup(w => w.IsWindow(Handle)).Returns(true);
        _windows.Setup(w => w.GetWindowThreadProcessId(Handle)).Returns((uint)Pid);
        _windows.Setup(w => w.RespondsWithin(Handle, It.IsAny<uint>())).Returns(true);
        _registry = new ElementRegistry(_tree, _worker, _windows.Object, ElementRegistry.DefaultCapacity, 1, TimeProvider.System);

        // 10,000 items, the first 30 realized, as the fixture app's list; items 7000 and 8000 share a name
        _list = new FakeVirtualList(10_000, realized: 30, i => i is 7000 or 8000 ? "Duplicate" : $"Item {i:D5}", Pid, i => $"row-{i}");
        _window = new FakeUiaNode("Window", "Explorer") { ProcessId = Pid }.With(
            new FakeUiaNode("Button", "Back") { ActionPatterns = ["Invoke"] },
            _list.List);
        _tree.WithWindow(Handle, _window);
    }

    private Task<FindElementsResult> Find(
        string? name = null, string? automationId = null, string[]? controlTypes = null, bool searchVirtualized = true,
        int maxResults = 20, string? withinRef = null, ElementDetail detail = ElementDetail.Minimal,
        ElementNameMatch nameMatch = ElementNameMatch.Exact, TimeSpan? timeout = null) =>
        new ElementSearchService(_tree, _worker, _registry, _windows.Object).FindAsync(
            new ElementQuery(Handle, withinRef, name, nameMatch, automationId, controlTypes, MaxResults: maxResults, Detail: detail,
                Timeout: timeout ?? TimeSpan.FromSeconds(5)) { SearchVirtualized = searchVirtualized });

    [Fact]
    public async Task WithoutSearchVirtualized_AnItemTheListHasNotRealized_IsNotFound_AndTheListIsNotAsked()
    {
        var result = await Find(name: "Item 05000", searchVirtualized: false);

        result.Success.Should().BeTrue();
        result.MatchCount.Should().Be(0);
        result.Virtualized.Should().BeNull();
        _list.Probes.Should().BeEmpty();
    }

    [Fact]
    public async Task AnItemTheListHasNotRealized_IsFoundThroughItemContainer_WithARef_AndNothingIsRealizedOrScrolled()
    {
        // Act
        var result = await Find(name: "Item 05000");

        // Assert
        result.Success.Should().BeTrue(result.Error);
        result.MatchCount.Should().Be(1);
        result.Truncated.Should().BeFalse();
        var item = result.Elements.Should().ContainSingle().Subject;
        item.Name.Should().Be("Item 05000");
        item.Virtualized.Should().BeTrue();
        item.IsOffscreen.Should().BeTrue();
        item.ClickPoint.Should().BeNull("a placeholder has no point on screen");
        item.Ref.Should().NotBeNull();
        item.Patterns.Should().Contain(["ScrollItem", "VirtualizedItem"]);

        var info = result.Virtualized!;
        info.ItemsFound.Should().Be(1);
        info.Note.Should().BeNull();
        var list = info.Containers.Should().ContainSingle().Subject;
        (list.ControlType, list.Name, list.ItemContainer, list.Scrollable).Should().Be(("List", "Files", true, true));
        list.Ref.Should().NotBeNull();

        // Only reads: the item stays where it was
        _list.Probes.Should().Equal("Name=Item 05000 after start", "Name=Item 05000 after Item 05000");
        _list.VirtualizedOf(5000).Realizes.Should().Be(0);
        _list.ScrollItemOf(5000).Calls.Should().Be(0);
        _list.IsRealized(5000).Should().BeFalse();
    }

    [Fact]
    public async Task TheItemsRef_ResolvesToThePlaceholder()
    {
        var result = await Find(name: "Item 05000");

        var resolved = _registry.Resolve(result.Elements!.Single().Ref!, CancellationToken.None);

        resolved.Element.Should().NotBeNull(resolved.Error);
        resolved.Element!.Node.Should().BeSameAs(_list.Items[5000]);
    }

    [Fact]
    public async Task ARealizedItem_TheTreeSearchFound_IsNotReportedTwice()
    {
        var result = await Find(name: "Item 00003");

        result.MatchCount.Should().Be(1);
        var item = result.Elements.Should().ContainSingle().Subject;
        item.Virtualized.Should().BeNull("the tree search found it");
        item.ClickPoint.Should().NotBeNull();
        result.Virtualized!.ItemsFound.Should().Be(0);
    }

    [Fact]
    public async Task ByAutomationId_TheListIsAskedForIt_AndTheOtherCriteriaAreTestedOnTheItem()
    {
        (await Find(automationId: "row-6000", controlTypes: ["Button"])).MatchCount.Should().Be(0);
        _list.Probes.Should().Contain("AutomationId=row-6000 after start");

        var result = await Find(automationId: "row-6000", controlTypes: ["ListItem"]);

        result.Elements.Should().ContainSingle().Which.Name.Should().Be("Item 06000");
    }

    [Fact]
    public async Task SeveralItemsWithTheName_AreKeptUpToMaxResults_AndAnotherIsCountedAsTruncation()
    {
        var all = await Find(name: "Duplicate");
        all.Elements!.Select(e => e.Ref).Should().HaveCount(2).And.OnlyHaveUniqueItems();
        all.Truncated.Should().BeFalse();

        var one = await Find(name: "Duplicate", maxResults: 1);

        one.Elements.Should().ContainSingle();
        one.MatchCount.Should().Be(2);
        one.Truncated.Should().BeTrue();
        one.TruncationReason.Should().Be(ElementSearchTruncation.MaxResults);
    }

    [Fact]
    public async Task WithinRef_OfTheListItself_SearchesThatList()
    {
        var listRef = (await Find(controlTypes: ["List"], searchVirtualized: false)).Elements!.Single().Ref!;

        var result = await Find(name: "Item 09999", withinRef: listRef);

        result.Elements.Should().ContainSingle().Which.Virtualized.Should().BeTrue();
        result.Virtualized!.Containers.Should().ContainSingle().Which.Ref.Should().Be(listRef);
    }

    [Fact]
    public async Task AListWithoutTheItemContainerPattern_IsReported_AndSaysToScrollIt()
    {
        // Arrange - a web list: its rows come and go as it scrolls, and it cannot be asked for the others
        var web = new FakeUiaNode("List", "Messages") { ProcessId = Pid, OtherPatterns = UiaPatterns.Scroll }
            .With(new FakeUiaNode("ListItem", "Hello") { ProcessId = Pid });
        _tree.WithWindow(Handle, new FakeUiaNode("Window", "Chat") { ProcessId = Pid }.With(web));

        // Act
        var result = await Find(name: "Old message");

        // Assert
        result.Success.Should().BeTrue();
        result.MatchCount.Should().Be(0);
        result.Virtualized!.Note.Should().Contain("ItemContainer").And.Contain("maxScrolls");
        result.Virtualized.Containers.Should().ContainSingle().Which.Should().Match<VirtualizedContainer>(c => !c.ItemContainer && c.Scrollable);
    }

    [Fact]
    public async Task AWindowWithoutAList_SaysSo()
    {
        _tree.WithWindow(Handle, new FakeUiaNode("Window", "Plain").With(new FakeUiaNode("Button", "OK")));

        var result = await Find(name: "Item 05000");

        result.Virtualized!.Containers.Should().BeEmpty();
        result.Virtualized.Note.Should().Contain("No list");
    }

    [Fact]
    public async Task AListWhoseProviderFails_IsReportedAsPartsSkipped_AndTheTreesMatchesAreStillReturned()
    {
        _list.FindException = new InvalidOperationException("provider failed");

        var result = await Find(name: "Item 00003");

        result.Success.Should().BeTrue();
        result.Elements.Should().ContainSingle().Which.Virtualized.Should().BeNull();
        result.Truncated.Should().BeTrue("the list's items that are not realized were not searched");
        result.TruncationReason.Should().Be(ElementSearchTruncation.PartsSkipped);
        result.Virtualized!.Note.Should().Contain("provider failed");
    }

    [Fact]
    public async Task AProviderThatFailsAfterTheFirstItem_IsPartsSkipped_NotAComplete_Search()
    {
        // Arrange - a provider that rejects startAfter: only the first look-alike is seen
        _list.FindException = new ArgumentException("E_INVALIDARG");
        _list.FailFromProbe = 1;

        // Act
        var result = await Find(name: "Duplicate");

        // Assert - one item found, but whether it is the only one is not known
        result.Elements.Should().ContainSingle().Which.Name.Should().Be("Duplicate");
        result.MatchCount.Should().Be(1);
        result.Truncated.Should().BeTrue();
        result.TruncationReason.Should().Be(ElementSearchTruncation.PartsSkipped);
    }

    [Fact]
    public async Task AProviderTimeoutAfterTheFirstItem_IsTimeout()
    {
        _list.FindException = new TimeoutException("UI Automation did not answer");
        _list.FailFromProbe = 1;

        var result = await Find(name: "Duplicate");

        result.Elements.Should().ContainSingle();
        result.Truncated.Should().BeTrue();
        result.TruncationReason.Should().Be(ElementSearchTruncation.Timeout);
        result.Virtualized!.Note.Should().Contain("did not answer in time");
    }

    [Fact]
    public async Task TheDeadlinePassingBetweenProbes_IsTimeout()
    {
        // Arrange - the first probe answers after the search's timeout has passed
        _list.OnProbe = probe =>
        {
            if (probe == 1)
            {
                Thread.Sleep(600);
            }
        };

        // Act
        var result = await Find(name: "Duplicate", timeout: TimeSpan.FromMilliseconds(300));

        // Assert
        result.Success.Should().BeTrue(result.Error);
        result.Elements.Should().ContainSingle();
        result.TruncationReason.Should().Be(ElementSearchTruncation.Timeout);
        result.Virtualized!.Note.Should().Contain("timeout");
        _list.Probes.Should().ContainSingle("nothing more is asked after the deadline");
    }

    [Fact]
    public async Task AnItemWhosePropertiesCannotBeRead_IsPartsSkipped()
    {
        _list.Items[7000].ReadException = new InvalidOperationException("placeholder");

        var result = await Find(name: "Duplicate");

        result.Elements.Should().ContainSingle().Which.Ref.Should().NotBeNull();
        result.TruncationReason.Should().Be(ElementSearchTruncation.PartsSkipped, "the provider matched an item that could not be checked");
    }

    [Fact]
    public async Task AListThatKeepsMatchingPastTheProbeLimit_IsPartsSkipped()
    {
        // Arrange - every item has the name, but none is a Button: each is asked for and skipped
        var same = new FakeVirtualList(ElementSearchService.MaxVirtualizedProbes + 10, realized: 0, _ => "Same", Pid);
        _tree.WithWindow(Handle, new FakeUiaNode("Window", "Explorer") { ProcessId = Pid }.With(same.List));

        // Act
        var result = await Find(name: "Same", controlTypes: ["Button"]);

        // Assert
        result.MatchCount.Should().Be(0);
        same.Probes.Should().HaveCount(ElementSearchService.MaxVirtualizedProbes);
        result.TruncationReason.Should().Be(ElementSearchTruncation.PartsSkipped);
        result.Virtualized!.Note.Should().Contain("items were asked for");
    }

    [Fact]
    public async Task MoreListsThanAreSearched_IsPartsSkipped_AndTheVisibleOnesAreSearchedFirst()
    {
        // Arrange - offscreen lists first in the tree, then the visible one holding the item
        var hidden = Enumerable.Range(0, ElementSearchService.MaxVirtualizedContainers)
            .Select(i => new FakeVirtualList(3, realized: 0, j => $"Hidden {i}.{j}", Pid))
            .ToList();
        foreach (var list in hidden)
        {
            list.List.IsOffscreen = true;
        }

        _tree.WithWindow(Handle, new FakeUiaNode("Window", "Explorer") { ProcessId = Pid }
            .With([.. hidden.Select(h => h.List), _list.List]));

        // Act
        var result = await Find(name: "Item 05000");

        // Assert
        result.Elements.Should().ContainSingle().Which.Name.Should().Be("Item 05000");
        result.Virtualized!.Containers.Should().HaveCount(ElementSearchService.MaxVirtualizedContainers);
        result.TruncationReason.Should().Be(ElementSearchTruncation.PartsSkipped);
        result.Virtualized.Note.Should().Contain("lists");
    }

    [Fact]
    public async Task WhenTheTreeSearchFilledMaxResults_TheListIsNotAsked()
    {
        var result = await Find(controlTypes: ["ListItem"], automationId: null, name: null, searchVirtualized: false, maxResults: 5);
        result.MatchCount.Should().Be(30);

        // name "Item 00001" is in the tree, so with maxResults 1 there is no room for more
        var full = await Find(name: "Item 00001", maxResults: 1);

        full.Elements.Should().ContainSingle();
        full.Virtualized!.Note.Should().Contain("maxResults");
        _list.Probes.Should().BeEmpty();
    }

    [Theory]
    [InlineData("Item", ElementNameMatch.Contains, null)]
    [InlineData("^Item", ElementNameMatch.Regex, null)]
    [InlineData(null, ElementNameMatch.Exact, null)]
    public void SearchVirtualized_WithoutAWholeNameOrAnAutomationId_IsAnArgumentError(string? name, ElementNameMatch nameMatch, string? automationId)
    {
        var query = new ElementQuery(Handle, Name: name, NameMatch: nameMatch, AutomationId: automationId, ControlTypes: ["ListItem"])
        {
            SearchVirtualized = true
        };

        ElementSearchService.VirtualizedArgumentError(query).Should().Contain("automationId, or name with nameMatch exact");
        ElementSearchService.VirtualizedArgumentError(query with { SearchVirtualized = false }).Should().BeNull();
        FluentActions.Awaiting(() => new ElementSearchService(_tree, _worker, _registry).FindAsync(query))
            .Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public void SearchVirtualized_WithoutAWindow_IsAnArgumentError()
    {
        var query = new ElementQuery(nint.Zero, Name: "Item 05000", Scope: ElementSearchScope.Desktop) { SearchVirtualized = true };

        ElementSearchService.VirtualizedArgumentError(query).Should().Contain("one window");
    }
}
