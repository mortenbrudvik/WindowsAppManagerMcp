namespace WindowsAppManagerMcp.Tests.Unit.Tools;

using WindowsAppManagerMcp.Infrastructure;
using WindowsAppManagerMcp.Tools;

/// <summary>
/// find_ui_elements' arguments (roadmap M3.4): what the tool refuses before it searches, how it finds the window of a
/// withinRef, and the text format's refs. The search itself is ElementSearchServiceTests'.
/// </summary>
public class FindUIElementsToolTests
{
    private static readonly nint Handle = 0x8100;

    private readonly Mock<IElementSearchService> _search = new();
    private readonly Mock<IElementRegistry> _registry = new();
    private ElementQuery? _query;

    public FindUIElementsToolTests()
    {
        _search.Setup(s => s.FindAsync(It.IsAny<ElementQuery>(), It.IsAny<CancellationToken>()))
            .Callback((ElementQuery query, CancellationToken _) => _query = query)
            .ReturnsAsync((ElementQuery query, CancellationToken _) => new FindElementsResult(true, query.WindowHandle, "W", 0, false, [], 1));
    }

    private UIElementTools CreateSut() =>
        new(new Mock<IUIElementService>().Object, new Mock<IElementInspectionService>().Object, null, _search.Object, _registry.Object);

    private static async Task<ToolErrorException> RefusedAsync(Func<Task> call) =>
        (await FluentActions.Awaiting(call).Should().ThrowAsync<ToolErrorException>()).Which;

    [Fact]
    public async Task Find_WithoutACriterion_IsRefused()
    {
        var error = await RefusedAsync(() => CreateSut().FindUIElements(handle: Handle, controlType: " , "));

        error.ErrorCode.Should().Be(ToolErrorReporting.InvalidArgumentsErrorCode);
        error.Message.Should().Contain("at least one of name, automationId, controlType and className");
        _search.Verify(s => s.FindAsync(It.IsAny<ElementQuery>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Find_WithAnInvalidRegex_IsRefused()
    {
        var error = await RefusedAsync(() => CreateSut().FindUIElements(handle: Handle, name: "(", nameMatch: ElementNameMatch.Regex));

        error.ErrorCode.Should().Be(ToolErrorReporting.InvalidArgumentsErrorCode);
        error.Message.Should().Contain("not a valid .NET regular expression");
    }

    [Fact]
    public async Task Find_WithoutAWindowOrWithinRef_IsRefused()
    {
        var error = await RefusedAsync(() => CreateSut().FindUIElements(name: "Save"));

        error.Message.Should().Contain("handle").And.Contain("withinRef");
    }

    #region searchVirtualized (roadmap M3.10)

    [Fact]
    public async Task SearchVirtualized_ReachesTheSearch_AndIsOffByDefault()
    {
        await CreateSut().FindUIElements(handle: Handle, name: "Item 05000");
        _query!.SearchVirtualized.Should().BeFalse();

        await CreateSut().FindUIElements(handle: Handle, name: "Item 05000", searchVirtualized: true);
        _query!.SearchVirtualized.Should().BeTrue();

        await CreateSut().FindUIElements(handle: Handle, automationId: "row-5000", name: "item", nameMatch: ElementNameMatch.Contains, searchVirtualized: true);
        _query!.SearchVirtualized.Should().BeTrue("the automation id is what the list is asked for");
    }

    [Theory]
    [InlineData("Item", ElementNameMatch.Contains)]
    [InlineData(null, ElementNameMatch.Exact)]
    public async Task SearchVirtualized_WithoutAWholeNameOrAnAutomationId_IsRefused_BeforeAnythingIsSearched(string? name, ElementNameMatch nameMatch)
    {
        var error = await RefusedAsync(() => CreateSut().FindUIElements(
            handle: Handle, name: name, nameMatch: nameMatch, controlType: "ListItem", searchVirtualized: true));

        error.ErrorCode.Should().Be(ToolErrorReporting.InvalidArgumentsErrorCode);
        error.Message.Should().Contain("automationId, or name with nameMatch exact").And.EndWith("Nothing was searched.");
        _search.Verify(s => s.FindAsync(It.IsAny<ElementQuery>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task SearchVirtualized_OverTheDesktopWithoutAWindow_IsRefused()
    {
        var error = await RefusedAsync(() => CreateSut().FindUIElements(name: "Item 05000", scope: ElementSearchScope.Desktop, searchVirtualized: true));

        error.Message.Should().Contain("one window");
    }

    #endregion

    #region scope (roadmap M3.5)

    [Fact]
    public async Task Find_PassesTheScope_AndLeavesItOutWhenNotGiven()
    {
        await CreateSut().FindUIElements(handle: Handle, name: "Delete", scope: ElementSearchScope.WindowAndPopups);
        _query!.Scope.Should().Be(ElementSearchScope.WindowAndPopups);

        await CreateSut().FindUIElements(handle: Handle, name: "Delete");
        _query!.Scope.Should().BeNull("the service chooses the default from the foreground");
    }

    [Fact]
    public async Task Find_WithScopeDesktop_NeedsNoWindow()
    {
        await CreateSut().FindUIElements(name: "Save", scope: ElementSearchScope.Desktop);

        _query!.WindowHandle.Should().Be(nint.Zero);
        _query.Scope.Should().Be(ElementSearchScope.Desktop);
    }

    [Fact]
    public async Task Find_WithoutAWindow_AndAnotherScope_IsRefused_NamingTheDesktopScope()
    {
        var error = await RefusedAsync(() => CreateSut().FindUIElements(name: "Save", scope: ElementSearchScope.WindowAndPopups));

        error.ErrorCode.Should().Be(ToolErrorReporting.InvalidArgumentsErrorCode);
        error.Message.Should().Contain("scope 'desktop'");
    }

    [Theory]
    [InlineData(ElementSearchScope.WindowAndPopups, "windowAndPopups")]
    [InlineData(ElementSearchScope.Desktop, "desktop")]
    public async Task Find_WithinRef_WithAScopeBeyondTheWindow_IsRefused(ElementSearchScope scope, string name)
    {
        var error = await RefusedAsync(() => CreateSut().FindUIElements(name: "Save", withinRef: "w3e7", scope: scope));

        error.ErrorCode.Should().Be(ToolErrorReporting.InvalidArgumentsErrorCode);
        error.Message.Should().Contain($"scope '{name}'").And.Contain("Nothing was searched");
        _registry.Verify(r => r.Lookup(It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task Find_WithinRef_WithScopeWindow_IsAccepted()
    {
        _registry.Setup(r => r.Lookup("w3e7")).Returns(new ElementLookup("w3e7", Handle, null, 1));

        await CreateSut().FindUIElements(name: "Save", withinRef: "w3e7", scope: ElementSearchScope.Window);

        _query!.Scope.Should().Be(ElementSearchScope.Window);
    }

    #endregion

    [Fact]
    public async Task Find_WithAWithinRefThatIsNoRef_IsRefused()
    {
        var error = await RefusedAsync(() => CreateSut().FindUIElements(name: "Save", withinRef: "button 3"));

        error.Message.Should().Contain("is not an element ref");
    }

    [Fact]
    public async Task Find_WithinRef_SearchesItsWindow_WithTheRefNormalized()
    {
        // Arrange
        _registry.Setup(r => r.Lookup("w3e7")).Returns(new ElementLookup("w3e7", Handle, null, 1));

        // Act
        await CreateSut().FindUIElements(name: "Save", withinRef: "W3E7");

        // Assert
        _query!.WindowHandle.Should().Be(Handle);
        _query.WithinRef.Should().Be("w3e7");
    }

    [Fact]
    public async Task Find_WithinRefOfAnotherWindow_IsRefused()
    {
        _registry.Setup(r => r.Lookup("w3e7")).Returns(new ElementLookup("w3e7", 0x9999, null, 1));

        var error = await RefusedAsync(() => CreateSut().FindUIElements(handle: Handle, name: "Save", withinRef: "w3e7"));

        error.Message.Should().Contain("not of the window given");
    }

    [Fact]
    public async Task Find_WithinAStaleRef_ReturnsElementStale()
    {
        _registry.Setup(r => r.Lookup("w3e7")).Returns(
            new ElementLookup("w3e7", 0, null, 0, UIElementErrorCode.ElementStale, "Element ref 'w3e7' is stale: its window closed."));

        var result = await CreateSut().FindUIElements(name: "Save", withinRef: "w3e7");

        result.Success.Should().BeFalse();
        result.ErrorCode.Should().Be(UIElementErrorCode.ElementStale);
        result.Error.Should().Contain("its window closed");
    }

    [Fact]
    public async Task Find_PassesTheCriteria_ClampingMaxResultsAndTheTimeout()
    {
        // Act
        await CreateSut().FindUIElements(
            handle: Handle, name: "sav", nameMatch: ElementNameMatch.Contains, automationId: "SaveBtn", controlType: "Button, SplitButton",
            className: "Btn", visibleOnly: false, enabledOnly: true, maxResults: 500, detail: ElementDetail.Full, timeoutMs: 100);

        // Assert
        _query.Should().BeEquivalentTo(new ElementQuery(
            Handle, null, "sav", ElementNameMatch.Contains, "SaveBtn", ["Button", "SplitButton"], "Btn", VisibleOnly: false,
            EnabledOnly: true, MaxResults: ElementSearchService.MaxResultsLimit, ElementDetail.Full, TimeSpan.FromSeconds(1)));
    }

    [Fact]
    public async Task Find_WhenTheRegexRunsTooLong_IsRefusedAsInvalidArguments()
    {
        _search.Setup(s => s.FindAsync(It.IsAny<ElementQuery>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new System.Text.RegularExpressions.RegexMatchTimeoutException("x", "(a+)+$", TimeSpan.FromMilliseconds(100)));

        var error = await RefusedAsync(() => CreateSut().FindUIElements(handle: Handle, name: "(a+)+$", nameMatch: ElementNameMatch.Regex));

        error.ErrorCode.Should().Be(ToolErrorReporting.InvalidArgumentsErrorCode);
        error.Message.Should().Contain("took longer than 100 ms");
    }

    [Fact]
    public void TextFormat_PutsTheRefFirst()
    {
        var element = new UIElementInfo("Save", "Button", "SaveBtn", new BoundsDto(812, 44, 80, 24), true, false, Ref: "w4120035e17");

        UIElementTextFormat.Line(element).Should().Be("w4120035e17 Button 'Save' aid=SaveBtn @812,44 80x24");
    }

    [Fact]
    public void Refs_AreSerializedFirst()
    {
        var json = System.Text.Json.JsonSerializer.Serialize(
            new UIElementInfo("Save", "Button", null, null, true, false, Ref: "w4120035e17"),
            new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web));

        json.Should().StartWith("{\"ref\":\"w4120035e17\",\"name\":\"Save\"");
    }

    #region Bounded AutomationIds and titles (roadmap M3.F14)

    [Fact]
    public async Task Find_CutsALongAutomationIdAndWindowTitle_InTheResult_ButSearchesForTheWholeAutomationId()
    {
        // An application chooses both: the result is cut, the criterion reaches the provider whole
        var id = "row-" + new string('7', 5000);
        var title = "Report - " + new string('t', 5000);
        _search.Setup(s => s.FindAsync(It.IsAny<ElementQuery>(), It.IsAny<CancellationToken>()))
            .Callback((ElementQuery query, CancellationToken _) => _query = query)
            .ReturnsAsync((ElementQuery query, CancellationToken _) => new FindElementsResult(true, query.WindowHandle, title, 2, false,
            [
                new FoundElement("w1e1", "Row", "ListItem", id, null, null, null, true, false, []),
                new FoundElement("w1e2", "Save", "Button", "SaveBtn", null, null, null, true, false, [])
            ], 1));

        var result = await CreateSut().FindUIElements(handle: Handle, automationId: id);

        _query!.AutomationId.Should().Be(id);
        result.WindowTitle.Should().Be(title[..(AppText.MaxLength - 1)] + "…");
        result.Elements!.Select(e => e.AutomationId).Should().Equal(id[..(AppText.MaxLength - 1)] + "…", "SaveBtn");
    }

    #endregion
}
