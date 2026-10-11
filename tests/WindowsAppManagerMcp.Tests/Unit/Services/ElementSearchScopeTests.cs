namespace WindowsAppManagerMcp.Tests.Unit.Services;

/// <summary>
/// find_ui_elements' scope (roadmap M3.5) over an in-memory tree and a mocked popup service: the default scope, popups
/// searched front first under one maxResults, candidate limit and deadline, refs registered under each popup's own
/// window, skipped popups, and the desktop scope with its exclusions and window limit. No real window is read.
/// </summary>
public class ElementSearchScopeTests
{
    private static readonly nint Main = 0x7100;
    private static readonly nint Menu = 0x7200;
    private static readonly nint Dialog = 0x7300;

    private readonly FakeUiaTree _tree = new();
    private readonly InlineUiaWorker _worker = new();
    private readonly Mock<INativeWindowWrapper> _windows = new();
    private readonly Mock<IPopupService> _popups = new();
    private readonly ElementRegistry _registry;

    public ElementSearchScopeTests()
    {
        foreach (var (handle, pid) in new[] { (Main, 300u), (Menu, 300u), (Dialog, 300u) })
        {
            _windows.Setup(w => w.IsWindow(handle)).Returns(true);
            _windows.Setup(w => w.GetWindowThreadProcessId(handle)).Returns(pid);
            _windows.Setup(w => w.RespondsWithin(handle, It.IsAny<uint>())).Returns(true);
        }

        _popups.Setup(p => p.GetPopups(Main)).Returns([Popup(Menu, PopupKind.Menu), Popup(Dialog, PopupKind.Dialog)]);
        _registry = new ElementRegistry(_tree, _worker, _windows.Object, ElementRegistry.DefaultCapacity, 1, TimeProvider.System);

        // Editor > Button "Delete" (DeleteBtn), Button "Save"; the context menu > MenuItem "Delete", MenuItem "Rename";
        // the dialog > Button "Save"
        _tree.WithWindow(Main, new FakeUiaNode("Window", "Editor").With(
            new FakeUiaNode("Button", "Delete") { AutomationId = "DeleteBtn" },
            new FakeUiaNode("Button", "Save")));
        _tree.WithWindow(Menu, new FakeUiaNode("Menu", "Context").With(
            new FakeUiaNode("MenuItem", "Delete"),
            new FakeUiaNode("MenuItem", "Rename")));
        _tree.WithWindow(Dialog, new FakeUiaNode("Window", "Save As").With(new FakeUiaNode("Button", "Save")));
    }

    private static PopupWindow Popup(nint handle, PopupKind kind) =>
        new(handle, kind, null, "Popup", 300, Main, PopupRelation.Owned, null, false);

    private ElementSearchService CreateSut() => new(_tree, _worker, _registry, _windows.Object, popups: _popups.Object);

    private Task<FindElementsResult> Find(
        string? name = null, string[]? controlTypes = null, ElementSearchScope? scope = null, int maxResults = 20,
        nint? handle = null, string? withinRef = null) =>
        CreateSut().FindAsync(new ElementQuery(handle ?? Main, withinRef, name, ControlTypes: controlTypes, MaxResults: maxResults,
            Timeout: TimeSpan.FromSeconds(5), Scope: scope));

    #region The default scope

    [Fact]
    public async Task Find_ByDefault_WhenTheWindowIsInFront_SearchesItsPopupsToo_FrontFirst()
    {
        // Arrange
        _popups.Setup(p => p.IsInForeground(Main)).Returns(true);

        // Act
        var result = await Find(name: "Delete");

        // Assert - the menu item first (the menu is in front), then the window's button
        result.Success.Should().BeTrue();
        result.Scope.Should().Be(ElementSearchScope.WindowAndPopups);
        result.WindowsSearched.Should().Be(3);
        result.MatchCount.Should().Be(2);
        result.Elements!.Select(e => (e.ControlType, e.WindowHandle)).Should().Equal(
            ("MenuItem", (long?)Menu), ("Button", (long?)null));
        result.WindowHandle.Should().Be((long)Main);
        result.WindowTitle.Should().Be("Editor");
    }

    [Fact]
    public async Task Find_ByDefault_WhenTheWindowIsNotInFront_SearchesTheWindowAlone()
    {
        _popups.Setup(p => p.IsInForeground(Main)).Returns(false);

        var result = await Find(name: "Delete");

        result.Scope.Should().Be(ElementSearchScope.Window);
        result.WindowsSearched.Should().BeNull();
        result.Elements!.Should().ContainSingle().Which.ControlType.Should().Be("Button");
        _popups.Verify(p => p.GetPopups(It.IsAny<nint>()), Times.Never);
    }

    [Fact]
    public async Task Find_WithScopeWindow_SearchesTheWindowAlone_EvenInFront()
    {
        _popups.Setup(p => p.IsInForeground(Main)).Returns(true);

        var result = await Find(name: "Delete", scope: ElementSearchScope.Window);

        result.Scope.Should().Be(ElementSearchScope.Window);
        result.Elements!.Should().ContainSingle().Which.WindowHandle.Should().BeNull();
    }

    [Fact]
    public async Task Find_WithScopeWindowAndPopups_SearchesThePopups_EvenWhenNotInFront()
    {
        var result = await Find(name: "Rename", scope: ElementSearchScope.WindowAndPopups);

        result.Elements!.Should().ContainSingle().Which.WindowHandle.Should().Be((long)Menu);
        _popups.Verify(p => p.IsInForeground(It.IsAny<nint>()), Times.Never);
    }

    [Fact]
    public async Task Find_BelowWithinRef_SearchesTheWindowAlone()
    {
        // Arrange - a ref of the window's element
        _popups.Setup(p => p.IsInForeground(Main)).Returns(true);
        var editor = await Find(name: "Delete", scope: ElementSearchScope.Window);
        var deleteRef = editor.Elements!.Single().Ref!;

        // Act - its descendants: none, and no popup is searched
        var result = await Find(controlTypes: ["MenuItem"], withinRef: deleteRef, scope: ElementSearchScope.WindowAndPopups);

        // Assert
        result.Scope.Should().Be(ElementSearchScope.Window);
        result.MatchCount.Should().Be(0);
        _popups.Verify(p => p.GetPopups(It.IsAny<nint>()), Times.Never);
    }

    #endregion

    #region Refs and limits

    [Fact]
    public async Task Find_RegistersAPopupsElementsUnderThePopupsOwnWindow()
    {
        var result = await Find(controlTypes: ["MenuItem"], scope: ElementSearchScope.WindowAndPopups);

        result.Elements!.Select(e => _registry.Lookup(e.Ref!).WindowHandle).Should().Equal(Menu, Menu);
    }

    [Fact]
    public async Task Find_KeepsOneMaxResultsForAllTheWindows_AndCountsEveryMatch()
    {
        // Act - Save is in the dialog (in front) and in the window
        var result = await Find(name: "Save", scope: ElementSearchScope.WindowAndPopups, maxResults: 1);

        // Assert
        result.MatchCount.Should().Be(2);
        result.Truncated.Should().BeTrue();
        result.TruncationReason.Should().Be(ElementSearchTruncation.MaxResults);
        result.Elements!.Should().ContainSingle().Which.WindowHandle.Should().Be((long)Dialog);
    }

    [Fact]
    public async Task Find_SkipsAPopupThatDoesNotRespondOrClosed_AndSearchesTheRest()
    {
        // Arrange - the menu is hung; the dialog has no UI Automation element any more
        _windows.Setup(w => w.IsHungAppWindow(Menu)).Returns(true);
        _popups.Setup(p => p.GetPopups(Main)).Returns([Popup(Menu, PopupKind.Menu), Popup(0x7400, PopupKind.Dialog)]);

        // Act
        var result = await Find(name: "Delete", scope: ElementSearchScope.WindowAndPopups);

        // Assert
        result.Success.Should().BeTrue();
        result.Elements!.Should().ContainSingle().Which.WindowHandle.Should().BeNull();
        result.Truncated.Should().BeTrue();
        result.TruncationReason.Should().Be(ElementSearchTruncation.PartsSkipped);
        result.WindowsSearched.Should().Be(1);
    }

    [Fact]
    public async Task Find_WhenTheNamedWindowDoesNotRespond_FailsAsBefore_WithoutSearchingItsPopups()
    {
        _windows.Setup(w => w.IsHungAppWindow(Main)).Returns(true);

        var result = await Find(name: "Delete", scope: ElementSearchScope.WindowAndPopups);

        result.ErrorCode.Should().Be(UIElementErrorCode.NotResponding);
        _popups.Verify(p => p.GetPopups(It.IsAny<nint>()), Times.Never);
    }

    [Fact]
    public async Task Find_SharesOneCandidateLimitAcrossTheWindows()
    {
        // Arrange - each window holds 1500 buttons that the provider matches before a contains filter: the first window's
        // search uses 1500 of the 2000, the second stops at 500
        var big = Enumerable.Range(0, 1500).Select(i => new FakeUiaNode("Button", $"B{i}")).ToArray();
        _tree.WithWindow(Menu, new FakeUiaNode("Pane", "Popup").With(new FakeUiaNode("Pane", "Inner").With(big)));
        _tree.WithWindow(Main, new FakeUiaNode("Window", "Editor").With(new FakeUiaNode("Pane", "Inner").With(
            Enumerable.Range(0, 1500).Select(i => new FakeUiaNode("Button", $"C{i}")).ToArray())));
        _popups.Setup(p => p.GetPopups(Main)).Returns([Popup(Menu, PopupKind.Popup)]);

        // Act
        var result = await CreateSut().FindAsync(new ElementQuery(Main, Name: "B1", NameMatch: ElementNameMatch.Contains,
            ControlTypes: ["Button"], Timeout: TimeSpan.FromSeconds(5), Scope: ElementSearchScope.WindowAndPopups));

        // Assert
        result.Truncated.Should().BeTrue();
        result.TruncationReason.Should().Be(ElementSearchTruncation.SearchLimit);
        result.WindowsSearched.Should().Be(2);
    }

    [Fact]
    public async Task Find_KeepsOneDeadlineForAllTheWindows()
    {
        // Arrange - the menu's 60 items are one provider call, which takes longer than the whole timeout
        _tree.WithWindow(Menu, new FakeUiaNode("Menu", "Context").With(Enumerable.Range(0, 60)
            .Select(i => new FakeUiaNode("MenuItem", i == 7 ? "Delete" : $"Item {i}")).ToArray()));
        _tree.OnFindAll = (_, _) => Thread.Sleep(250);

        // Act
        var result = await CreateSut().FindAsync(new ElementQuery(Main, Name: "Delete", Timeout: TimeSpan.FromMilliseconds(100),
            Scope: ElementSearchScope.WindowAndPopups));

        // Assert - what the menu gave is returned; the window was not searched after the deadline
        result.Success.Should().BeTrue(result.Error);
        result.Elements!.Should().ContainSingle().Which.WindowHandle.Should().Be((long)Menu);
        result.TruncationReason.Should().Be(ElementSearchTruncation.Timeout);
        result.WindowsSearched.Should().Be(1);
    }

    #endregion

    #region Desktop

    [Fact]
    public async Task Find_WithScopeDesktop_AndNoWindow_SearchesTheDesktopWindows_AndReportsTheExcluded()
    {
        // Arrange
        _popups.Setup(p => p.GetDesktopWindows(nint.Zero, PopupService.DesktopWindowLimit))
            .Returns(new DesktopWindowSet([Dialog, Main], Excluded: 2, OverLimit: 0));

        // Act
        var result = await Find(name: "Save", scope: ElementSearchScope.Desktop, handle: nint.Zero);

        // Assert
        result.Success.Should().BeTrue();
        result.Scope.Should().Be(ElementSearchScope.Desktop);
        result.WindowHandle.Should().Be(0);
        result.WindowTitle.Should().BeNull();
        result.WindowsSearched.Should().Be(2);
        result.WindowsExcluded.Should().Be(2);
        result.Elements!.Select(e => e.WindowHandle).Should().Equal((long)Dialog, (long)Main);
        _registry.Lookup(result.Elements![1].Ref!).WindowHandle.Should().Be(Main);
        _worker.LastTargetProcessId.Should().Be(0);
    }

    [Fact]
    public async Task Find_WithScopeDesktop_OverTheWindowLimit_IsTruncatedWithWindowLimit()
    {
        _popups.Setup(p => p.GetDesktopWindows(Main, PopupService.DesktopWindowLimit))
            .Returns(new DesktopWindowSet([Main], Excluded: 0, OverLimit: 7));

        var result = await Find(name: "Save", scope: ElementSearchScope.Desktop);

        result.Truncated.Should().BeTrue();
        result.TruncationReason.Should().Be(ElementSearchTruncation.WindowLimit);
        result.WindowsExcluded.Should().BeNull();
        result.Elements!.Should().ContainSingle().Which.WindowHandle.Should().BeNull("it is the named window");
    }

    [Fact]
    public async Task Find_WithoutAWindow_AndAnotherScope_IsAnInvalidHandle()
    {
        var result = await Find(name: "Save", scope: ElementSearchScope.WindowAndPopups, handle: nint.Zero);

        result.ErrorCode.Should().Be(UIElementErrorCode.InvalidHandle);
        _worker.Calls.Should().Be(0);
    }

    #endregion

    #region Uniqueness per window

    [Fact]
    public async Task Find_ProvesAMatchUniqueInItsOwnWindow_SoTheRefCanFollowTheElement()
    {
        // Arrange - Rename is the only match; its menu item is re-created with another runtime id
        var rename = new FakeUiaNode("MenuItem", "Rename");
        _tree.WithWindow(Menu, new FakeUiaNode("Menu", "Context").With(new FakeUiaNode("MenuItem", "Delete"), rename));
        var result = await Find(name: "Rename", scope: ElementSearchScope.WindowAndPopups);
        var renameRef = result.Elements!.Single().Ref!;
        rename.Vanish();
        _tree.WithWindow(Menu, new FakeUiaNode("Menu", "Context").With(new FakeUiaNode("MenuItem", "Rename")));

        // Act
        var resolved = _registry.Resolve(renameRef, CancellationToken.None);

        // Assert
        resolved.Success.Should().BeTrue();
        resolved.Element!.Via.Should().Be(ElementResolutionPath.Selector);
        resolved.Element.WindowHandle.Should().Be(Menu);
    }

    #endregion
}
