namespace WindowsAppManagerMcp.Tests.Unit.Services;

/// <summary>
/// The read tools give their elements refs (roadmap M3.4): get_ui_elements' walk (and so capture_with_elements),
/// get_focused_element and get_element_at_point, through the element registry. Over an in-memory UI Automation tree.
/// </summary>
public class ElementRefReadTests
{
    private static readonly nint Handle = 0x6100;

    private readonly FakeUiaTree _tree = new();
    private readonly InlineUiaWorker _worker = new();
    private readonly Mock<INativeWindowWrapper> _windows = new();
    private readonly ElementRegistry _registry;

    public ElementRefReadTests()
    {
        _windows.Setup(w => w.IsWindow(Handle)).Returns(true);
        _windows.Setup(w => w.GetWindowThreadProcessId(Handle)).Returns(55u);
        _windows.Setup(w => w.RespondsWithin(Handle, It.IsAny<uint>())).Returns(true);
        _registry = new ElementRegistry(_tree, _worker, _windows.Object, ElementRegistry.DefaultCapacity, 1, TimeProvider.System);
    }

    private UIElementService Walk() => new(_tree, _worker, _windows.Object, registry: _registry);

    private static IEnumerable<UIElementInfo> Flatten(IEnumerable<UIElementInfo> elements) =>
        elements.SelectMany(e => Flatten(e.Children ?? []).Prepend(e));

    #region The walk

    [Fact]
    public async Task Walk_GivesEveryReturnedElementARef_AndKeepsThemOnTheNextWalk()
    {
        // Arrange
        _tree.WithWindow(Handle, new FakeUiaNode("Window", "Main").With(
            new FakeUiaNode("ToolBar", "Tools").With(new FakeUiaNode("Button", "Save"), new FakeUiaNode("Button", "Open")),
            new FakeUiaNode("Edit", "Name")));

        // Act
        var first = Flatten((await Walk().GetUIElementsAsync(Handle)).Elements!).ToList();
        var second = Flatten((await Walk().GetUIElementsAsync(Handle)).Elements!).ToList();

        // Assert - breadth first: the window's children are numbered before their children
        first.Select(e => $"{e.Ref} {e.Name}").Should().Equal("w1e1 Tools", "w1e3 Save", "w1e4 Open", "w1e2 Name");
        second.Select(e => e.Ref).Should().Equal(first.Select(e => e.Ref));
    }

    [Fact]
    public async Task Walk_GivesNoRefToAnElementLeftOut_OrWithoutARuntimeId()
    {
        // Arrange
        _tree.WithWindow(Handle, new FakeUiaNode("Window", "Main").With(
            new FakeUiaNode("Pane", "Layout").With(new FakeUiaNode("Button", "OK")),
            new FakeUiaNode("Button", "Anonymous") { RuntimeId = null }));

        // Act
        var elements = (await Walk().GetUIElementsAsync(Handle, filter: new UIElementFilter(ControlTypes: ["Button"]))).Elements!;

        // Assert
        elements.Select(e => $"{e.Name}={e.Ref ?? "none"}").Should().Equal("OK=w1e1", "Anonymous=none");
        _registry.Count.Should().Be(1, "the pane the filter left out has no ref");
    }

    [Fact]
    public async Task Walk_ReadingAWideLevelOneElementAtATime_StillGivesRefs()
    {
        // Arrange - wider than one cached read: each element is read on its own, its runtime id asked for separately
        _tree.WithWindow(Handle, new FakeUiaNode("Window", "Main").With(Enumerable.Range(1, UIElementService.CachedLevelLimit + 1)
            .Select(i => new FakeUiaNode("ListItem", $"Item {i}")).ToArray()));

        // Act
        var elements = (await Walk().GetUIElementsAsync(Handle, filter: new UIElementFilter(MaxElements: 5))).Elements!;

        // Assert
        elements.Select(e => e.Ref).Should().Equal("w1e1", "w1e2", "w1e3", "w1e4", "w1e5");
        _tree.CachedFetches.Should().BeEmpty();
    }

    [Fact]
    public async Task Walk_RecordsEachElementsIndexPath_InItsFingerprint()
    {
        // Arrange
        _tree.WithWindow(Handle, new FakeUiaNode("Window", "Main").With(
            new FakeUiaNode("Text", "Label"),
            new FakeUiaNode("Pane", "Body").With(new FakeUiaNode("Text", "Hint"), new FakeUiaNode("Button", "Go"))));

        // Act
        var go = Flatten((await Walk().GetUIElementsAsync(Handle)).Elements!).Single(e => e.Name == "Go");

        // Assert
        _registry.Lookup(go.Ref!).Fingerprint!.IndexPath.Should().Equal(1, 1);
    }

    [Fact]
    public async Task Walk_GivesTheElementsOfAnOwnedDialogNoRefUnderItsOwner_OnlyUnderTheDialogsOwnWindow()
    {
        // Arrange - a dialog the window owns, which the HWND proxy lists first among the window's children (roadmap M3.F9)
        var dialogHandle = (nint)0x6200;
        _windows.Setup(w => w.IsWindow(dialogHandle)).Returns(true);
        _windows.Setup(w => w.GetWindowThreadProcessId(dialogHandle)).Returns(55u);
        _windows.Setup(w => w.RespondsWithin(dialogHandle, It.IsAny<uint>())).Returns(true);
        _windows.Setup(w => w.GetWindow(dialogHandle, 4)).Returns(Handle);
        var yes = new FakeUiaNode("Button", "Yes") { AutomationId = "6" };
        var dialog = new FakeUiaNode("Window", "Delete File") { NativeWindowHandle = dialogHandle }.With(yes);
        _tree.WithWindow(Handle, new FakeUiaNode("Window", "Main").With(dialog, new FakeUiaNode("Text", "Label"), new FakeUiaNode("Button", "Go")));
        _tree.WithWindow(dialogHandle, dialog);

        // Act
        var owner = Flatten((await Walk().GetUIElementsAsync(Handle)).Elements!).ToList();
        var ownDialog = Flatten((await Walk().GetUIElementsAsync(dialogHandle)).Elements!).ToList();

        // Assert - the owner's elements keep their places in its own tree; the Yes button's ref is the dialog's
        owner.Select(e => e.Name).Should().Equal("Label", "Go");
        _registry.Lookup(owner[1].Ref!).Fingerprint!.IndexPath.Should().Equal(1);
        var yesRef = ownDialog.Should().ContainSingle().Which.Ref!;
        _registry.Lookup(yesRef).WindowHandle.Should().Be(dialogHandle);
        _registry.Count.Should().Be(3);
    }

    [Fact]
    public async Task TheDesktopStateWalk_AndTheMarksWalk_GiveAnElementTheSameRef_InEitherOrder()
    {
        // Arrange - get_desktop_state's elements (roadmap M3.12: interactableOnly, visibleOnly) and capture_with_elements'
        // marks (roadmap M3.11: actionableOnly, at most 80) are two filters over one walk and one registry
        _tree.WithWindow(Handle, new FakeUiaNode("Window", "Main").With(
            new FakeUiaNode("Pane", "Body").With(
                new FakeUiaNode("Text", "Label"),
                new FakeUiaNode("Button", "Save") { IsKeyboardFocusable = true, ActionPatterns = ["Invoke"] },
                new FakeUiaNode("Edit", "Name") { IsKeyboardFocusable = true, ActionPatterns = ["Value"] }),
            new FakeUiaNode("CheckBox", "Wrap") { IsKeyboardFocusable = true, ActionPatterns = ["Toggle"] }));
        var desktopState = new UIElementFilter(InteractableOnly: true, VisibleOnly: true, MaxElements: 120);
        var marks = new UIElementFilter(ActionableOnly: true, VisibleOnly: true, MaxElements: 80);

        // Act - marks first, then the observation, then marks again
        var marked = Flatten((await Walk().GetUIElementsAsync(Handle, filter: marks)).Elements!).ToList();
        var observed = Flatten((await Walk().GetUIElementsAsync(Handle, filter: desktopState)).Elements!).ToList();
        var markedAgain = Flatten((await Walk().GetUIElementsAsync(Handle, filter: marks)).Elements!).ToList();

        // Assert
        marked.Select(e => e.Name).Should().BeEquivalentTo("Save", "Name", "Wrap");
        observed.Where(e => e.Ref != null).Select(e => $"{e.Name}={e.Ref}").Should()
            .Contain(marked.Select(e => $"{e.Name}={e.Ref}"), "an element observed by get_desktop_state carries the ref its mark shows");
        markedAgain.Select(e => e.Ref).Should().Equal(marked.Select(e => e.Ref));
    }

    [Fact]
    public async Task Walk_WithoutARegistry_GivesNoRefs()
    {
        _tree.WithWindow(Handle, new FakeUiaNode("Window", "Main").With(new FakeUiaNode("Button", "Save")));

        var elements = (await new UIElementService(_tree, _worker).GetUIElementsAsync(Handle)).Elements!;

        elements.Single().Ref.Should().BeNull();
    }

    [Fact]
    public async Task Walk_RecordsTheRowOfAControlInAListItem_SoItsRefIsStaleOnceTheRowShowsAnotherItem()
    {
        // Arrange - a recycling list: the row's check box keeps its runtime id when the row shows another file; the toolbar's
        // button is in no row
        var row = new FakeUiaNode("ListItem", "report.docx");
        _tree.WithWindow(Handle, new FakeUiaNode("Window", "Files").With(
            new FakeUiaNode("ToolBar", "Tools").With(new FakeUiaNode("Button", "Delete")),
            new FakeUiaNode("List", "Items").With(row.With(new FakeUiaNode("CheckBox", "Select") { AutomationId = "Pick" }))));
        var elements = Flatten((await Walk().GetUIElementsAsync(Handle)).Elements!).ToList();
        var pickRef = elements.Single(e => e.Name == "Select").Ref!;
        var deleteRef = elements.Single(e => e.Name == "Delete").Ref!;

        // Act
        row.Name = "budget.xlsx";
        var pick = _registry.Resolve(pickRef, CancellationToken.None);
        var delete = _registry.Resolve(deleteRef, CancellationToken.None);

        // Assert
        pick.ErrorCode.Should().Be(UIElementErrorCode.ElementStale);
        pick.Error.Should().Contain("another item");
        delete.Element!.Via.Should().Be(ElementResolutionPath.Live);
    }

    #endregion

    #region Single elements

    private ElementInspectionService Inspection(Mock<INativeHitTestWrapper> hitTest, IAccessibilityWarmup? warmup = null)
    {
        var monitors = new Mock<IMonitorService>();
        monitors.Setup(m => m.GetAllMonitors()).Returns(
            [new MonitorInfo(0, @"\\.\DISPLAY1", true, new MonitorRect(0, 0, 1920, 1080), new MonitorRect(0, 0, 1920, 1040), 1.0)]);
        return new ElementInspectionService(_tree, _worker, hitTest.Object, _windows.Object, monitors.Object, warmup, _registry);
    }

    [Fact]
    public async Task FocusedElement_AndTheSameElementAtAPoint_HaveOneRef_TheWalksToo()
    {
        // Arrange
        var email = new FakeUiaNode("Edit", "Email") { AutomationId = "EmailBox", ClassName = "TextBox", IsKeyboardFocusable = true };
        _tree.WithWindow(Handle, new FakeUiaNode("Window", "Sign in") { NativeWindowHandle = Handle }.With(email));
        _tree.Focused = email;
        _tree.AtPoint = (_, _) => email;
        var hitTest = new Mock<INativeHitTestWrapper>();
        hitTest.Setup(h => h.GetRootWindow(It.IsAny<nint>())).Returns(Handle);
        hitTest.Setup(h => h.WindowFromPoint(It.IsAny<int>(), It.IsAny<int>())).Returns(Handle);
        var sut = Inspection(hitTest);

        // Act
        var focused = await sut.GetFocusedElementAsync(TimeSpan.FromSeconds(5));
        var atPoint = await sut.GetElementAtPointAsync(20, 20, 0, TimeSpan.FromSeconds(5));
        var walked = (await Walk().GetUIElementsAsync(Handle)).Elements!.Single();

        // Assert
        focused.Element!.Ref.Should().Be("w1e1");
        atPoint.Element!.Ref.Should().Be("w1e1");
        walked.Ref.Should().Be("w1e1");
        (await _registry.ResolveAsync("w1e1", TimeSpan.FromSeconds(5), CancellationToken.None)).Element!.Node.Should().BeSameAs(email);
    }

    [Fact]
    public async Task FocusedElement_WithoutAWindow_HasNoRef()
    {
        // Arrange - no element above it has a window handle
        _tree.Focused = new FakeUiaNode("Edit", "Floating");
        var sut = Inspection(new Mock<INativeHitTestWrapper>());

        // Act
        var focused = await sut.GetFocusedElementAsync(TimeSpan.FromSeconds(5));

        // Assert
        focused.Success.Should().BeTrue();
        focused.Element!.Ref.Should().BeNull();
    }

    #endregion

    #region Together with the web content warm-up (roadmap M3.3)

    private static readonly AccessibilityWarmupInfo Warmed =
        new(AccessibilityWarmupStatus.Ready, WebContentHost.WebView2, 300, 2, 4);

    [Fact]
    public async Task Walk_GivesEveryElementItsRef_AndItsFrameworkOnlyWhereItChanges_AfterTheWarmup()
    {
        // Arrange - a WPF window hosting web content
        _tree.WithWindow(Handle, new FakeUiaNode("Window", "Mail") { FrameworkId = "WPF" }.With(
            new FakeUiaNode("Button", "New") { FrameworkId = "WPF" },
            new FakeUiaNode("Document", "Inbox") { FrameworkId = "Chrome" }.With(
                new FakeUiaNode("Hyperlink", "Message") { FrameworkId = "Chrome" })));
        var warmup = new Mock<IAccessibilityWarmup>();
        warmup.Setup(w => w.WarmUp(Handle, It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
            .Callback(() => _registry.Count.Should().Be(0, "the warm-up comes before any element is read and registered"))
            .Returns(Warmed);
        var sut = new UIElementService(_tree, _worker, _windows.Object, warmup.Object, _registry);

        // Act
        var result = await sut.GetUIElementsAsync(Handle);

        // Assert
        result.FrameworkId.Should().Be("WPF");
        result.AccessibilityWarmup.Should().Be(Warmed);
        Flatten(result.Elements!).Select(e => $"{e.Ref} {e.Name} {e.FrameworkId ?? "-"}")
            .Should().Equal("w1e1 New -", "w1e2 Inbox Chrome", "w1e3 Message -");
        WindowsAppManagerMcp.Tools.UIElementTextFormat.Line(result.Elements![1])
            .Should().StartWith("w1e2 Document 'Inbox' fw=Chrome @");
    }

    [Fact]
    public async Task FocusedElement_AndTheElementAtAPoint_CarryTheirRef_AndTheWarmup()
    {
        // Arrange
        var link = new FakeUiaNode("Hyperlink", "Message") { FrameworkId = "Chrome" };
        _tree.WithWindow(Handle, new FakeUiaNode("Window", "Mail") { NativeWindowHandle = Handle }.With(link));
        _tree.Focused = link;
        _tree.AtPoint = (_, _) => link;
        _windows.Setup(w => w.GetForegroundWindow()).Returns(Handle);
        var hitTest = new Mock<INativeHitTestWrapper>();
        hitTest.Setup(h => h.GetRootWindow(It.IsAny<nint>())).Returns(Handle);
        hitTest.Setup(h => h.WindowFromPoint(It.IsAny<int>(), It.IsAny<int>())).Returns(Handle);
        var warmup = new Mock<IAccessibilityWarmup>();
        warmup.Setup(w => w.WarmUp(Handle, It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>())).Returns(Warmed);
        var sut = Inspection(hitTest, warmup.Object);

        // Act
        var focused = await sut.GetFocusedElementAsync(TimeSpan.FromSeconds(5));
        var atPoint = await sut.GetElementAtPointAsync(20, 20, 0, TimeSpan.FromSeconds(5));

        // Assert
        focused.Element!.Ref.Should().Be("w1e1");
        focused.Element.FrameworkId.Should().Be("Chrome");
        focused.AccessibilityWarmup.Should().Be(Warmed);
        atPoint.Element!.Ref.Should().Be("w1e1");
        atPoint.AccessibilityWarmup.Should().Be(Warmed);
    }

    #endregion
}
