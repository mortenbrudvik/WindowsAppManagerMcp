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

    /// <summary>GetWindow's GW_OWNER.</summary>
    private const uint GwOwner = 4;

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

        SetPopups(Popup(Menu, PopupKind.Menu), Popup(Dialog, PopupKind.Dialog));
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

    private void SetPopups(params PopupWindow[] popups) => SetPopups(false, popups);

    private void SetPopups(bool truncated, params PopupWindow[] popups) =>
        _popups.Setup(p => p.GetPopupList(Main)).Returns(new PopupList(popups, truncated));

    private ElementSearchService CreateSut(IUiaWorker? worker = null, ITargetPolicy? policy = null) =>
        new(_tree, worker ?? _worker, _registry, _windows.Object, popups: _popups.Object, policy: policy);

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
        _popups.Verify(p => p.GetPopupList(It.IsAny<nint>()), Times.Never);
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
        _popups.Verify(p => p.GetPopupList(It.IsAny<nint>()), Times.Never);
    }

    [Fact]
    public async Task Find_WithPopups_LeavesOutAPopupOfADeniedApp_AndCountsIt()
    {
        // Arrange - the dialog in front is a credential prompt of another process that the app owns
        SetPopups(Popup(Menu, PopupKind.Menu), Popup(Dialog, PopupKind.Dialog) with { IsDeniedByPolicy = true });

        // Act - only the prompt has a Save button besides the window
        var result = await Find(name: "Save", scope: ElementSearchScope.WindowAndPopups);

        // Assert - a popup is never named: it is not read, as the desktop scope leaves such a window out
        result.Success.Should().BeTrue();
        result.WindowsSearched.Should().Be(2);
        result.WindowsExcluded.Should().Be(1);
        result.Elements!.Should().ContainSingle().Which.WindowHandle.Should().BeNull("the match is the window's own button");
    }

    [Fact]
    public async Task Find_WithPopups_LeavesOutAPopupOfTheAgentsHost_UnlessAnActionsSearchAsks()
    {
        // Arrange - the menu belongs to the agent's host (a terminal's context menu over the app)
        SetPopups(Popup(Menu, PopupKind.Menu) with { IsAgentHost = true }, Popup(Dialog, PopupKind.Dialog));
        var query = new ElementQuery(Main, Name: "Rename", MaxResults: 5, Timeout: TimeSpan.FromSeconds(5),
            Scope: ElementSearchScope.WindowAndPopups);

        // Act
        var read = await CreateSut().FindAsync(query);
        var action = await CreateSut().FindAsync(query with { IncludeHostPopups = true });

        // Assert - element_action's search keeps it: the input guard decides there (allowHostTarget)
        read.MatchCount.Should().Be(0);
        read.WindowsExcluded.Should().Be(1);
        action.Elements!.Should().ContainSingle().Which.WindowHandle.Should().Be((long)Menu);
        action.WindowsExcluded.Should().BeNull();
    }

    [Fact]
    public async Task Find_WithPopups_LeavesOutAPopupOfADeniedApp_EvenForAnAction()
    {
        SetPopups(Popup(Menu, PopupKind.Menu) with { IsDeniedByPolicy = true, IsAgentHost = true });

        var result = await CreateSut().FindAsync(new ElementQuery(Main, Name: "Rename", Timeout: TimeSpan.FromSeconds(5),
            Scope: ElementSearchScope.WindowAndPopups) { IncludeHostPopups = true });

        result.MatchCount.Should().Be(0);
        result.WindowsExcluded.Should().Be(1);
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
        SetPopups(Popup(Menu, PopupKind.Menu), Popup(0x7400, PopupKind.Dialog));

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
        _popups.Verify(p => p.GetPopupList(It.IsAny<nint>()), Times.Never);
    }

    [Fact]
    public async Task Find_SharesOneCandidateLimitAcrossTheWindows()
    {
        // Arrange - each window holds 1500 buttons that the provider matches before a contains filter: the popup's search
        // stops at the 1000 it may use (the named window's share is kept for it), the window's at the other 1000
        var big = Enumerable.Range(0, 1500).Select(i => new FakeUiaNode("Button", $"B{i}")).ToArray();
        _tree.WithWindow(Menu, new FakeUiaNode("Pane", "Popup").With(new FakeUiaNode("Pane", "Inner").With(big)));
        _tree.WithWindow(Main, new FakeUiaNode("Window", "Editor").With(new FakeUiaNode("Pane", "Inner").With(
            Enumerable.Range(0, 1500).Select(i => new FakeUiaNode("Button", $"C{i}")).ToArray())));
        SetPopups(Popup(Menu, PopupKind.Popup));

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
        // Arrange - the menu's 60 items are one provider call, which returns only once the whole timeout has passed (the
        // deadline is the token the worker runs the search with)
        _tree.WithWindow(Menu, new FakeUiaNode("Menu", "Context").With(Enumerable.Range(0, 60)
            .Select(i => new FakeUiaNode("MenuItem", i == 7 ? "Delete" : $"Item {i}")).ToArray()));
        _tree.OnFindAll = (_, _) => _worker.LastToken.WaitHandle.WaitOne(TimeSpan.FromSeconds(30));

        // Act
        var result = await CreateSut().FindAsync(new ElementQuery(Main, Name: "Delete", Timeout: TimeSpan.FromMilliseconds(1000),
            Scope: ElementSearchScope.WindowAndPopups));

        // Assert - what the menu gave is returned; the window was not searched after the deadline
        result.Success.Should().BeTrue(result.Error);
        result.Elements!.Should().ContainSingle().Which.WindowHandle.Should().Be((long)Menu);
        result.TruncationReason.Should().Be(ElementSearchTruncation.Timeout);
        result.WindowsSearched.Should().Be(1);
    }

    [Fact]
    public async Task Find_APopupWithThousandsOfCandidates_LeavesTheNamedWindowItsShare_SoItsMatchIsFound()
    {
        // Arrange - a palette in front with 3000 buttons the provider matches before the contains filter
        _tree.WithWindow(Menu, new FakeUiaNode("Pane", "Palette").With(new FakeUiaNode("Pane", "Inner").With(
            Enumerable.Range(0, 3000).Select(i => new FakeUiaNode("Button", $"Tool {i}")).ToArray())));
        SetPopups(Popup(Menu, PopupKind.Window));

        // Act
        var result = await CreateSut().FindAsync(new ElementQuery(Main, Name: "Delete", NameMatch: ElementNameMatch.Contains,
            ControlTypes: ["Button"], Timeout: TimeSpan.FromSeconds(5), Scope: ElementSearchScope.WindowAndPopups));

        // Assert - the palette's search stopped at its share; the window was still searched
        result.Success.Should().BeTrue(result.Error);
        result.WindowsSearched.Should().Be(2);
        result.Elements!.Should().ContainSingle().Which.Should().Match<FoundElement>(e => e.Name == "Delete" && e.WindowHandle == null);
        result.TruncationReason.Should().Be(ElementSearchTruncation.SearchLimit, "the palette was not searched to the end");
    }

    [Fact]
    public async Task Find_APopupWhoseProviderDoesNotAnswerInTime_IsSkipped_AndTheWindowIsStillSearched()
    {
        // Arrange - the menu's provider times out (a slow WebView2 popup), with the call's deadline still far off
        _tree.WithWindow(Menu, new FakeUiaNode("Menu", "Context")
        {
            ChildrenException = new TimeoutException("the provider did not answer"),
            FindAllException = new TimeoutException("the provider did not answer")
        }.With(new FakeUiaNode("MenuItem", "Delete")));
        SetPopups(Popup(Menu, PopupKind.Menu));

        // Act
        var result = await Find(name: "Delete", scope: ElementSearchScope.WindowAndPopups);

        // Assert
        result.Success.Should().BeTrue(result.Error);
        result.Elements!.Should().ContainSingle().Which.WindowHandle.Should().BeNull();
        result.TruncationReason.Should().Be(ElementSearchTruncation.PartsSkipped);
        result.WindowsSearched.Should().Be(2);
    }

    [Fact]
    public async Task Find_APopupThatTimesOutWhenOpened_IsSkipped_InsteadOfFailingTheCall()
    {
        // Arrange - UI Automation times out getting the dialog's element; the menu was already searched
        _tree.FromHandleFailure = h => h == Dialog ? new TimeoutException("the provider did not answer") : null;

        // Act
        var result = await Find(name: "Delete", scope: ElementSearchScope.WindowAndPopups);

        // Assert - the menu's and the window's matches are both returned
        result.Success.Should().BeTrue(result.Error);
        result.Elements!.Select(e => e.WindowHandle).Should().Equal((long)Menu, null);
        result.TruncationReason.Should().Be(ElementSearchTruncation.PartsSkipped);
    }

    [Fact]
    public async Task Find_WhenMorePopupsWereOpenThanListed_IsTruncatedWithWindowLimit()
    {
        SetPopups(true, Popup(Menu, PopupKind.Menu));

        var result = await Find(name: "Rename", scope: ElementSearchScope.WindowAndPopups);

        result.Elements!.Should().ContainSingle();
        result.Truncated.Should().BeTrue();
        result.TruncationReason.Should().Be(ElementSearchTruncation.WindowLimit);
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

    [Fact]
    public async Task Find_ProvesTheNamedWindowsSingleMatchUnique_EvenWhenAPopupHasAMatchToo()
    {
        // Arrange - Delete is once in the menu and once in the window; the window's button is re-created
        var delete = new FakeUiaNode("Button", "Delete") { AutomationId = "DeleteBtn" };
        _tree.WithWindow(Main, new FakeUiaNode("Window", "Editor").With(delete, new FakeUiaNode("Button", "Save")));
        var result = await Find(name: "Delete", scope: ElementSearchScope.WindowAndPopups);
        result.MatchCount.Should().Be(2);
        var deleteRef = result.Elements!.Single(e => e.WindowHandle == null).Ref!;
        delete.Vanish();
        _tree.WithWindow(Main, new FakeUiaNode("Window", "Editor").With(new FakeUiaNode("Button", "Delete") { AutomationId = "DeleteBtn" }));

        // Act
        var resolved = _registry.Resolve(deleteRef, CancellationToken.None);

        // Assert - proven unique in its own window, so the ref follows the re-created button
        resolved.Success.Should().BeTrue(resolved.Error);
        resolved.Element!.Via.Should().Be(ElementResolutionPath.Selector);
        resolved.Element.WindowHandle.Should().Be(Main);
    }

    #endregion

    #region Reads per window (roadmap M3.F13)

    private static ElementQuery Query(string name, ElementSearchScope scope, nint? handle = null) =>
        new(handle ?? Main, Name: name, Detail: ElementDetail.Standard, Timeout: TimeSpan.FromSeconds(5), Scope: scope);

    [Fact]
    public async Task Find_WithPopups_ReadsEachWindowAgainstItsOwnApplication()
    {
        // Arrange - the context menu belongs to another process (a WebView2 child process, a shell extension's host)
        _windows.Setup(w => w.GetWindowThreadProcessId(Menu)).Returns(400u);
        var worker = new PerTargetWorker(_tree);

        // Act
        var result = await CreateSut(worker).FindAsync(Query("Rename", ElementSearchScope.WindowAndPopups));

        // Assert - the menu's search, the proof that its item is alone and the item's details were read in reads counted
        // against the menu's own process, the window's in reads counted against the window's
        result.Success.Should().BeTrue(result.Error);
        result.Elements!.Should().ContainSingle().Which.WindowHandle.Should().Be((long)Menu);
        worker.CallsIn(400).Should().Contain(c => c.Contains("Context")).And.Contain("Clickable:Rename")
            .And.NotContain(c => c.Contains("Editor") || c.Contains("Save As"));
        worker.CallsIn(300).Should().Contain(c => c.Contains("Editor"))
            .And.NotContain(c => c.Contains("Context") || c.Contains("Rename"));
    }

    [Fact]
    public async Task Find_WithScopeDesktop_ReadsEachWindowAgainstItsOwnApplication()
    {
        // Arrange - no window named: the dialog of one app and the editor of another
        _windows.Setup(w => w.GetWindowThreadProcessId(Dialog)).Returns(500u);
        _popups.Setup(p => p.GetDesktopWindows(nint.Zero, PopupService.DesktopWindowLimit))
            .Returns(new DesktopWindowSet([Dialog, Main], Excluded: 0, OverLimit: 0));
        var worker = new PerTargetWorker(_tree);

        // Act
        var result = await CreateSut(worker).FindAsync(Query("Save", ElementSearchScope.Desktop, handle: nint.Zero));

        // Assert - the desktop's list is read first, counted against no application; then each window against its own
        result.Elements!.Select(e => e.WindowHandle).Should().Equal((long)Dialog, (long)Main);
        worker.Targets[0].Should().Be(0);
        worker.CallsIn(0).Should().BeEmpty("no window's tree is read before the windows are known");
        worker.CallsIn(500).Should().Contain(c => c.Contains("Save As")).And.NotContain(c => c.Contains("Editor"));
        worker.CallsIn(300).Should().Contain(c => c.Contains("Editor")).And.NotContain(c => c.Contains("Save As"));
    }

    [Fact]
    public async Task Find_WithPopups_SkipsAPopupWhoseApplicationHasTooManyStuckReads_AndSearchesTheRest()
    {
        // Arrange - earlier reads of the menu's process are still stuck
        _windows.Setup(w => w.GetWindowThreadProcessId(Menu)).Returns(400u);
        var worker = new PerTargetWorker(_tree) { Refused = { 400 } };

        // Act
        var result = await CreateSut(worker).FindAsync(Query("Delete", ElementSearchScope.WindowAndPopups));

        // Assert - the menu is skipped as a popup that does not respond is; the window's button is still found
        result.Success.Should().BeTrue(result.Error);
        result.Elements!.Should().ContainSingle().Which.WindowHandle.Should().BeNull();
        result.MatchCount.Should().Be(1);
        result.Truncated.Should().BeTrue();
        result.TruncationReason.Should().Be(ElementSearchTruncation.PartsSkipped);
        result.WindowsSearched.Should().Be(2);
        worker.CallsIn(400).Should().BeEmpty();
    }

    [Fact]
    public async Task Find_WithPopups_WhenThePopupsApplicationRefusesAfterItsSearch_DescribesItsMatchFromWhatTheSearchRead()
    {
        // Arrange - the menu's search is read; by the time its match is described, another call's read of the menu's
        // process is stuck
        _windows.Setup(w => w.GetWindowThreadProcessId(Menu)).Returns(400u);
        var worker = new PerTargetWorker(_tree) { RefusedAfter = { [400] = 1 } };

        // Act
        var result = await CreateSut(worker).FindAsync(Query("Rename", ElementSearchScope.WindowAndPopups));

        // Assert - returned with its ref and the centre of its bounds, nothing more read of it
        result.Success.Should().BeTrue(result.Error);
        var rename = result.Elements!.Should().ContainSingle().Subject;
        rename.WindowHandle.Should().Be((long)Menu);
        rename.Ref.Should().NotBeNull();
        rename.ClickPoint.Should().Be(new ScreenPoint(50, 15));
        _tree.Reads.Should().NotContain(r => r.EndsWith(":Rename", StringComparison.Ordinal) &&
                                             (r.StartsWith("Clickable", StringComparison.Ordinal) ||
                                              r.StartsWith("Identity", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task Find_WithPopups_WhenTheNamedWindowsApplicationHasTooManyStuckReads_FailsAtOnce()
    {
        // Arrange
        var worker = new PerTargetWorker(_tree) { Refused = { 300 } };

        // Act
        var result = await CreateSut(worker).FindAsync(Query("Delete", ElementSearchScope.WindowAndPopups));

        // Assert - as before: nothing of it or its popups is read
        result.Success.Should().BeFalse();
        result.ErrorCode.Should().Be(UIElementErrorCode.OperationTimedOut);
        result.Error.Should().Contain("process 300");
        worker.Targets.Should().BeEmpty();
        _popups.Verify(p => p.GetPopupList(It.IsAny<nint>()), Times.Never);
    }

    [Fact]
    public async Task Find_WithPopups_WhenTheNamedWindowsApplicationRefusesItsSearch_FailsAsAtTheStart()
    {
        // Arrange - the popups belong to other processes; the window's application is refused after the search's start
        _windows.Setup(w => w.GetWindowThreadProcessId(Menu)).Returns(400u);
        _windows.Setup(w => w.GetWindowThreadProcessId(Dialog)).Returns(500u);
        var worker = new PerTargetWorker(_tree) { RefusedAfter = { [300] = 1 } };

        // Act
        var result = await CreateSut(worker).FindAsync(Query("Delete", ElementSearchScope.WindowAndPopups));

        // Assert - the window named is never skipped
        result.Success.Should().BeFalse();
        result.ErrorCode.Should().Be(UIElementErrorCode.OperationTimedOut);
        worker.Targets.Should().Equal(300, 400, 500);
    }

    [Fact]
    public async Task Find_WithPopups_WhenTheListsLookupIsRefused_ReturnsTheTreesMatches_WithANote()
    {
        // Arrange - the window's application is refused after its own search, before searchVirtualized asks its lists
        _windows.Setup(w => w.GetWindowThreadProcessId(Menu)).Returns(400u);
        _windows.Setup(w => w.GetWindowThreadProcessId(Dialog)).Returns(500u);
        var worker = new PerTargetWorker(_tree) { RefusedAfter = { [300] = 2 } };

        // Act
        var result = await CreateSut(worker).FindAsync(
            Query("Delete", ElementSearchScope.WindowAndPopups) with { SearchVirtualized = true });

        // Assert - the lookup did not run, and the result says that not everything was searched
        result.Success.Should().BeTrue(result.Error);
        result.Elements!.Select(e => e.WindowHandle).Should().Equal((long)Menu, null);
        result.Elements!.Should().OnlyContain(e => e.Ref != null);
        result.Truncated.Should().BeTrue();
        result.TruncationReason.Should().Be(ElementSearchTruncation.PartsSkipped);
        result.Virtualized!.Note.Should().Contain("process 300");
    }

    [Fact]
    public async Task Find_APopupStuckInItsProvider_CountsAgainstThePopupsApplication_SoTheWindowIsStillSearched()
    {
        // Arrange - the menu belongs to another process, whose provider does not return from its search (one call, past
        // the deadline and the grace after it); the worker refuses an application after one stuck read
        _windows.Setup(w => w.GetWindowThreadProcessId(Menu)).Returns(400u);
        _tree.WithWindow(Menu, new FakeUiaNode("Menu", "Context").With(Enumerable.Range(0, 60)
            .Select(i => new FakeUiaNode("MenuItem", i == 7 ? "Delete" : $"Item {i}")).ToArray()));
        using var release = new ManualResetEventSlim();
        _tree.OnFindAll = (node, _) =>
        {
            if (node.Name == "Context")
            {
                release.Wait(TimeSpan.FromSeconds(30));
            }
        };
        var worker = new UiaWorker(maxStuckCallsPerTarget: 1, maxStuckCalls: 16);
        var query = new ElementQuery(Main, Name: "Delete", Timeout: TimeSpan.FromMilliseconds(300),
            Scope: ElementSearchScope.WindowAndPopups);

        try
        {
            // Act - the first search is stuck in the menu; the second comes while that read is still stuck
            var stuck = await CreateSut(worker).FindAsync(query);
            var next = await CreateSut(worker).FindAsync(query with { Timeout = TimeSpan.FromSeconds(5) });

            // Assert - the stuck read counts against the menu's application, not the window's: the window is still searched
            stuck.ErrorCode.Should().Be(UIElementErrorCode.OperationTimedOut);
            worker.StuckCallsFor(400).Should().Be(1);
            worker.StuckCallsFor(300).Should().Be(0);
            next.Success.Should().BeTrue(next.Error);
            next.Elements!.Should().ContainSingle().Which.WindowHandle.Should().BeNull();
            next.TruncationReason.Should().Be(ElementSearchTruncation.PartsSkipped);
        }
        finally
        {
            release.Set();
        }
    }

    /// <summary>
    /// Runs each read on the calling thread, as <see cref="InlineUiaWorker"/> does, and records which application it was
    /// counted against with the tree calls made during it. Refuses the reads of the processes in <see cref="Refused"/>, and
    /// those of a process in <see cref="RefusedAfter"/> once it had that many, as <see cref="UiaWorker"/> refuses an
    /// application with too many stuck reads.
    /// </summary>
    private sealed class PerTargetWorker(FakeUiaTree tree) : IUiaWorker
    {
        private readonly List<(int Target, List<string> Calls)> _reads = [];

        public HashSet<int> Refused { get; } = [];

        public Dictionary<int, int> RefusedAfter { get; } = [];

        /// <summary>The application each read that ran was counted against, in order.</summary>
        public IReadOnlyList<int> Targets => _reads.Select(r => r.Target).ToList();

        /// <summary>The tree calls made in the reads counted against <paramref name="target"/>.</summary>
        public IReadOnlyList<string> CallsIn(int target) => _reads.Where(r => r.Target == target).SelectMany(r => r.Calls).ToList();

        public Task<T> RunAsync<T>(int targetProcessId, Func<CancellationToken, T> read, CancellationToken cancellationToken, TimeSpan grace)
        {
            if (Refused.Contains(targetProcessId) ||
                (RefusedAfter.TryGetValue(targetProcessId, out var allowed) && _reads.Count(r => r.Target == targetProcessId) >= allowed))
            {
                return Task.FromException<T>(new UiaReadRefusedException(
                    $"UI Automation is not responding for the application (process {targetProcessId})."));
            }

            var before = Calls();
            try
            {
                return Task.FromResult(read(cancellationToken));
            }
            catch (Exception ex)
            {
                return Task.FromException<T>(ex);
            }
            finally
            {
                var after = Calls();
                _reads.Add((targetProcessId, after.Zip(before, (a, b) => a.Skip(b.Count)).SelectMany(c => c).ToList()));
            }
        }

        private IReadOnlyList<string>[] Calls() => [tree.Reads, tree.Finds, tree.CachedFetches, tree.ChildListings, tree.ChildProbes];
    }

    #endregion

    #region Owned windows in their owner's tree

    /// <summary>
    /// An owned dialog as UI Automation's HWND proxy shows it: the dialog's window element (with its own window handle) is
    /// the root of the dialog's window, and also the first child of the window that owns it, ahead of that window's own
    /// children. The dialog is in the popup list as an owned window.
    /// </summary>
    private FakeUiaNode OwnedDialog(nint handle, string title, params FakeUiaNode[] children)
    {
        var dialog = new FakeUiaNode("Window", title) { NativeWindowHandle = handle }.With(children);
        _tree.WithWindow(handle, dialog);
        ((FakeUiaNode)_tree.FromHandle(Main)!).Insert(0, dialog);
        _windows.Setup(w => w.GetWindow(handle, GwOwner)).Returns(Main);
        return dialog;
    }

    [Fact]
    public async Task Find_AnOwnedDialogListedUnderItsOwner_IsSearchedOnlyAsItsOwnWindow()
    {
        // Arrange - Explorer's Delete File dialog in front, owned by the window named
        _popups.Setup(p => p.IsInForeground(Main)).Returns(true);
        OwnedDialog(Dialog, "Delete File", new FakeUiaNode("Button", "Yes") { AutomationId = "6" });

        // Act
        var withPopups = await Find(name: "Yes");
        var windowOnly = await Find(name: "Yes", scope: ElementSearchScope.Window);

        // Assert - once, under the dialog's own window; the window alone does not hold it
        withPopups.Scope.Should().Be(ElementSearchScope.WindowAndPopups);
        withPopups.MatchCount.Should().Be(1);
        withPopups.Elements!.Should().ContainSingle().Which.WindowHandle.Should().Be((long)Dialog);
        _registry.Lookup(withPopups.Elements![0].Ref!).WindowHandle.Should().Be(Dialog);
        windowOnly.MatchCount.Should().Be(0);
    }

    [Fact]
    public async Task Find_ProvesUniquenessInTheWindowsOwnTree_AndAResolutionNeverMovesARefIntoAnOwnedDialog()
    {
        // Arrange - the window's OK button, and a twin in a dialog the window owns; the window's button is found and then
        // re-created only in the dialog
        var ok = new FakeUiaNode("Button", "OK") { AutomationId = "OKBtn" };
        _tree.WithWindow(Main, new FakeUiaNode("Window", "Editor").With(ok));
        OwnedDialog(Dialog, "Options", new FakeUiaNode("Button", "OK") { AutomationId = "OKBtn" });
        var found = await Find(name: "OK", scope: ElementSearchScope.Window);
        found.MatchCount.Should().Be(1);
        var okRef = found.Elements!.Single().Ref!;
        ok.Vanish();

        // Act
        var resolved = _registry.Resolve(okRef, CancellationToken.None);

        // Assert - unique in the window's own tree (so it has a selector), but the dialog's button is the dialog's
        resolved.ErrorCode.Should().Be(UIElementErrorCode.ElementStale);
    }

    [Fact]
    public async Task Find_NoRefOfAnOwnedDialogsButton_OutlivesTheDialog()
    {
        // Arrange - the Yes button found while the dialog is open; the dialog closes, and the window opens another one with
        // a Yes button of the same automation id
        _popups.Setup(p => p.IsInForeground(Main)).Returns(true);
        var yes = new FakeUiaNode("Button", "Yes") { AutomationId = "6" };
        var first = OwnedDialog(Dialog, "Delete File", yes);
        var refs = (await Find(name: "Yes")).Elements!.Select(e => e.Ref!).ToList();
        yes.Vanish();
        first.Vanish();
        _windows.Setup(w => w.IsWindow(Dialog)).Returns(false);
        OwnedDialog(0x7400, "Confirm", new FakeUiaNode("Button", "Yes") { AutomationId = "6" });

        // Act
        var resolved = refs.Select(r => _registry.Resolve(r, CancellationToken.None)).ToList();

        // Assert - each ref belonged to the closed dialog: none moves to the new dialog's button
        resolved.Should().OnlyContain(r => r.ErrorCode == UIElementErrorCode.ElementStale);
    }

    [Fact]
    public async Task Find_AWindowTooWideToSplit_StillLeavesItsOwnedDialogOut()
    {
        // Arrange - a form with 60 controls directly on it (searched whole) and an owned dialog listed first
        _popups.Setup(p => p.IsInForeground(Main)).Returns(true);
        _tree.WithWindow(Main, new FakeUiaNode("Window", "Form").With(
            Enumerable.Range(0, 60).Select(i => new FakeUiaNode("Edit", $"Field {i}")).ToArray()));
        OwnedDialog(Dialog, "Find", new FakeUiaNode("Button", "Find Next"), new FakeUiaNode("Edit", "Find what"));

        // Act
        var withPopups = await Find(controlTypes: ["Edit"]);
        var windowOnly = await Find(name: "Find what", scope: ElementSearchScope.Window);

        // Assert - the dialog's edit box once, under the dialog; the form's 60 under the form
        withPopups.MatchCount.Should().Be(61);
        withPopups.Elements!.Count(e => e.WindowHandle == (long)Dialog).Should().Be(1);
        windowOnly.MatchCount.Should().Be(0);
    }

    [Fact]
    public async Task Find_WhenAnOwnedWindowIsStillSearchedWithItsOwner_KeepsOnlyThePopupsCopy()
    {
        // Arrange - the window's tree cannot be split (the level fails to read), so its search covers the owned dialog too
        _popups.Setup(p => p.IsInForeground(Main)).Returns(true);
        OwnedDialog(Dialog, "Delete File", new FakeUiaNode("Button", "Yes") { AutomationId = "6" });
        ((FakeUiaNode)_tree.FromHandle(Main)!).CachedChildrenException = new InvalidOperationException("provider failed");

        // Act
        var result = await Find(name: "Yes");

        // Assert
        result.MatchCount.Should().Be(1);
        result.Elements!.Should().ContainSingle().Which.WindowHandle.Should().Be((long)Dialog);
    }

    [Fact]
    public async Task Find_WhenAnOwnedWindowIsStillSearchedWithItsOwner_CountsAMatchThePolicyRefusesOnce()
    {
        // Arrange - the dialog shows a password manager's field (process 777, on the default deny list) next to its own OK
        // button; the window's tree cannot be split, so its search meets both again
        _popups.Setup(p => p.IsInForeground(Main)).Returns(true);
        _windows.Setup(w => w.GetProcessName(300u)).Returns("notepad");
        _windows.Setup(w => w.GetProcessName(777u)).Returns("KeePass");
        OwnedDialog(Dialog, "Unlock",
            new FakeUiaNode("Edit", "Master password") { ProcessId = 777 },
            new FakeUiaNode("Edit", "Comment") { ProcessId = 300 });
        ((FakeUiaNode)_tree.FromHandle(Main)!).CachedChildrenException = new InvalidOperationException("provider failed");

        // Act
        var result = await CreateSut(policy: new TargetPolicy(TargetPolicySettings.Default)).FindAsync(new ElementQuery(
            Main, null, null, ControlTypes: ["Edit"], MaxResults: 20, Timeout: TimeSpan.FromSeconds(5)));

        // Assert - the field is left out once, by the dialog that met it first; the allowed edit box is the dialog's once
        result.Success.Should().BeTrue();
        result.MatchCount.Should().Be(1);
        result.Elements!.Should().ContainSingle().Which.Name.Should().Be("Comment");
        result.PolicyLeftOut!.Count.Should().Be(1);
        result.Warning.Should().Contain($"1 element of window {(long)Dialog} belongs to process 777 ('KeePass')")
            .And.NotContain($"of window {(long)Main} belong");
    }

    #endregion
}
