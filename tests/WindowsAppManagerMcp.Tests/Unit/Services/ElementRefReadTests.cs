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

    private UIElementService Walk() => new(_tree, _worker, _windows.Object, _registry);

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
    public async Task Walk_WithoutARegistry_GivesNoRefs()
    {
        _tree.WithWindow(Handle, new FakeUiaNode("Window", "Main").With(new FakeUiaNode("Button", "Save")));

        var elements = (await new UIElementService(_tree, _worker).GetUIElementsAsync(Handle)).Elements!;

        elements.Single().Ref.Should().BeNull();
    }

    #endregion

    #region Single elements

    private ElementInspectionService Inspection(Mock<INativeHitTestWrapper> hitTest)
    {
        var monitors = new Mock<IMonitorService>();
        monitors.Setup(m => m.GetAllMonitors()).Returns(
            [new MonitorInfo(0, @"\\.\DISPLAY1", true, new MonitorRect(0, 0, 1920, 1080), new MonitorRect(0, 0, 1920, 1040), 1.0)]);
        return new ElementInspectionService(_tree, _worker, hitTest.Object, _windows.Object, monitors.Object, _registry);
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
}
