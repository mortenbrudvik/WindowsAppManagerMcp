namespace WindowsAppManagerMcp.Tests.Unit.Services;

using WindowsAppManagerMcp.Tools;

/// <summary>
/// The scope of the tools that name one element by find_ui_elements' criteria (roadmap M3.5 with M3.6, M3.8 and M3.9):
/// read_element_text, wait_for_element and element_action pass ElementQuery.Scope to the real ElementSearchService, so an
/// element of the window's open menu is found with windowAndPopups (and named by a ref of the menu's own window), not with
/// window; and a search that left popups out is no proof of one match. An in-memory tree and a mocked popup service; no
/// real window is read and nothing is acted on.
/// </summary>
public class CriteriaScopeTests
{
    private static readonly nint Main = 0x7100;
    private static readonly nint Menu = 0x7200;
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    private readonly FakeUiaTree _tree = new();
    private readonly InlineUiaWorker _worker = new();
    private readonly Mock<INativeWindowWrapper> _windows = new();
    private readonly Mock<IPopupService> _popups = new();
    private readonly ElementRegistry _registry;
    private readonly ElementSearchService _search;

    public CriteriaScopeTests()
    {
        foreach (var handle in new[] { Main, Menu })
        {
            _windows.Setup(w => w.IsWindow(handle)).Returns(true);
            _windows.Setup(w => w.GetWindowThreadProcessId(handle)).Returns(300u);
            _windows.Setup(w => w.RespondsWithin(handle, It.IsAny<uint>())).Returns(true);
        }

        SetPopups(truncated: false);
        _registry = new ElementRegistry(_tree, _worker, _windows.Object, ElementRegistry.DefaultCapacity, 1, TimeProvider.System);
        _search = new ElementSearchService(_tree, _worker, _registry, _windows.Object, popups: _popups.Object);

        // Editor > Button "Delete"; its open context menu (a window of its own) > MenuItem "Delete", MenuItem "Rename"
        _tree.WithWindow(Main, new FakeUiaNode("Window", "Editor").With(new FakeUiaNode("Button", "Delete")));
        _tree.WithWindow(Menu, new FakeUiaNode("Menu", "Context").With(
            new FakeUiaNode("MenuItem", "Delete"),
            new FakeUiaNode("MenuItem", "Rename")));
    }

    private void SetPopups(bool truncated) =>
        _popups.Setup(p => p.GetPopupList(Main)).Returns(new PopupList(
            [new PopupWindow(Menu, PopupKind.Menu, null, "#32768", 300, nint.Zero, PopupRelation.SameProcess, null, false)],
            truncated));

    private static ElementQuery MenuItem(string name, ElementSearchScope? scope) =>
        new(Main, Name: name, ControlTypes: ["MenuItem"], MaxResults: 5, Timeout: Timeout, Scope: scope);

    #region read_element_text

    private Task<ElementTextResult> ReadText(ElementQuery query) =>
        new ElementReadService(_tree, _worker, _registry, _search, _windows.Object)
            .ReadTextAsync(new ElementTextRequest(null, query, 0, 1000, Timeout));

    [Fact]
    public async Task ReadText_WithScopeWindowAndPopups_ReadsTheMenuItem_UnderTheMenusWindow()
    {
        var result = await ReadText(MenuItem("Rename", ElementSearchScope.WindowAndPopups));

        result.Success.Should().BeTrue();
        result.Text.Should().Be("Rename");
        result.Source.Should().Be(ElementTextSource.Name);
        result.WindowHandle.Should().Be((long)Menu, "a popup's element is registered under the popup's own window");
        _registry.Lookup(result.Ref!).WindowHandle.Should().Be(Menu);
    }

    [Fact]
    public async Task ReadText_WithScopeWindow_DoesNotSearchTheMenu()
    {
        // The window is in front, so without a scope its popups would be searched too
        _popups.Setup(p => p.IsInForeground(Main)).Returns(true);

        var result = await ReadText(MenuItem("Rename", ElementSearchScope.Window));

        result.Success.Should().BeFalse();
        result.ErrorCode.Should().Be(UIElementErrorCode.ElementNotFound);
        _popups.Verify(p => p.GetPopupList(It.IsAny<nint>()), Times.Never);
    }

    [Fact]
    public async Task ReadText_ByDefault_SearchesThePopups_OfAWindowInFront()
    {
        _popups.Setup(p => p.IsInForeground(Main)).Returns(true);

        var result = await ReadText(MenuItem("Rename", scope: null));

        result.Success.Should().BeTrue();
        result.Text.Should().Be("Rename");
    }

    [Fact]
    public async Task ReadText_WhenMorePopupsWereOpenThanSearched_ReadsNothing_AnotherMatchMayBeInTheRest()
    {
        SetPopups(truncated: true);

        var result = await ReadText(MenuItem("Rename", ElementSearchScope.WindowAndPopups));

        result.Success.Should().BeFalse();
        result.ErrorCode.Should().Be(UIElementErrorCode.OperationTimedOut);
        result.Error.Should().Contain("every window of its scope").And.Contain("WindowLimit");
        result.Text.Should().BeNull();
    }

    [Fact]
    public async Task ReadText_WithScopeWindowAndPopups_CountsTheWindowsAndThePopupsMatches()
    {
        // "Delete" is a button of the window and an item of its menu: two elements, so none is read
        var result = await ReadText(new ElementQuery(Main, Name: "Delete", MaxResults: 5, Timeout: Timeout,
            Scope: ElementSearchScope.WindowAndPopups));

        result.Success.Should().BeFalse();
        result.ErrorCode.Should().Be(UIElementErrorCode.AmbiguousElement);
        result.Candidates!.Select(c => c.ControlType).Should().Equal("MenuItem", "Button");
    }

    #endregion

    #region wait_for_element

    private Task<WaitForElementResult> Wait(ElementSearchScope? scope)
    {
        var windowWait = new Mock<IWindowWaitService>();
        windowWait.Setup(w => w.WaitForConditionAsync(It.IsAny<WindowConditionRequest>(), It.IsAny<IProgress<OperationProgress>?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new WindowConditionResult
            {
                Satisfied = true, MatchCount = 1, Window = TestDataFactory.CreateWindowInfo((int)Main, "Editor", "editor", 300)
            });
        var sut = new ElementWaitService(_search, _registry, windowWait.Object, _tree, _worker, _windows.Object,
            delay: (_, _) => Task.CompletedTask);

        // timeoutMs 0: one check
        return sut.WaitAsync(new ElementWaitRequest
        {
            Window = new WindowSelector { ProcessName = "editor" },
            Criteria = new ElementQuery(nint.Zero, Name: "Rename", ControlTypes: ["MenuItem"], Scope: scope),
            TimeoutMs = 0
        }, null, CancellationToken.None);
    }

    [Fact]
    public async Task Wait_WithScopeWindowAndPopups_FindsTheMenuItem()
    {
        var result = await Wait(ElementSearchScope.WindowAndPopups);

        result.Satisfied.Should().BeTrue();
        result.Element!.Name.Should().Be("Rename");
        result.Element.WindowHandle.Should().Be((long)Menu);
    }

    [Fact]
    public async Task Wait_WithScopeWindow_DoesNotSearchTheMenu()
    {
        _popups.Setup(p => p.IsInForeground(Main)).Returns(true);

        var result = await Wait(ElementSearchScope.Window);

        result.Satisfied.Should().BeFalse();
        result.ErrorCode.Should().Be("WaitTimedOut");
        _popups.Verify(p => p.GetPopupList(It.IsAny<nint>()), Times.Never);
    }

    #endregion

    #region element_action

    [Theory]
    [InlineData(ElementSearchScope.WindowAndPopups, true)]
    [InlineData(ElementSearchScope.Window, false)]
    public async Task ElementAction_ActsOnTheMenuItem_OnlyWhenTheScopeCoversTheMenu(ElementSearchScope scope, bool found)
    {
        // Arrange - the action service is a mock: what reaches it is the ref the criteria resolved to
        var actions = new Mock<IElementActionService>();
        ElementActionRequest? acted = null;
        actions.Setup(a => a.ActAsync(It.IsAny<ElementActionRequest>(), It.IsAny<CancellationToken>()))
            .Callback((ElementActionRequest request, CancellationToken _) => acted = request)
            .ReturnsAsync((ElementActionRequest request, CancellationToken _) =>
                new ElementActionResult(true, "invoke", request.Ref, Menu, ElementActionStatus.Done, ElementActionMethod.Invoke));
        var sut = new ElementActionTools(actions.Object, _search, _registry);

        // Act
        var result = await sut.ElementAction(ElementActionKind.Invoke, handle: Main, name: "Rename", controlType: "MenuItem", scope: scope).Payload<ElementActionResult>();

        // Assert
        result.Success.Should().Be(found);
        if (found)
        {
            _registry.Lookup(acted!.Ref).WindowHandle.Should().Be(Menu, "the action is checked against the menu's own window");
        }
        else
        {
            result.ErrorCode.Should().Be(nameof(UIElementErrorCode.ElementNotFound));
            result.Error.Should().Contain("No element of the window matches");
            actions.VerifyNoOtherCalls();
        }
    }

    #endregion
}
