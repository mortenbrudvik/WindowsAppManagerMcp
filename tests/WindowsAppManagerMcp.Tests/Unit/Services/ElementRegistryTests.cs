namespace WindowsAppManagerMcp.Tests.Unit.Services;

/// <summary>
/// The element registry (roadmap M3.4): refs are unique for the session and kept by the same element, they expire and
/// follow their windows, and a resolution finds the live element, finds it again when it was re-created, and never
/// resolves to a different element. Over an in-memory UI Automation tree and a mocked window wrapper.
/// </summary>
public class ElementRegistryTests
{
    private static readonly nint Handle = 0x4100;
    private static readonly nint OtherHandle = 0x4200;
    private static readonly CancellationToken None = CancellationToken.None;

    private readonly FakeUiaTree _tree = new();
    private readonly InlineUiaWorker _worker = new();
    private readonly Mock<INativeWindowWrapper> _windows = new();
    private readonly Dictionary<nint, uint> _processes = new() { [Handle] = 100, [OtherHandle] = 200 };

    public ElementRegistryTests()
    {
        _windows.Setup(w => w.IsWindow(It.IsAny<nint>())).Returns((nint h) => _processes.ContainsKey(h));
        _windows.Setup(w => w.GetWindowThreadProcessId(It.IsAny<nint>()))
            .Returns((nint h) => _processes.GetValueOrDefault(h));
        _windows.Setup(w => w.RespondsWithin(It.IsAny<nint>(), It.IsAny<uint>())).Returns(true);
        _windows.Setup(w => w.GetClassName(It.IsAny<nint>())).Returns((nint h) => _classes.GetValueOrDefault(h, "AppWindow"));
    }

    private readonly Dictionary<nint, string> _classes = [];

    /// <summary>The identity criteria find_ui_elements would store for a button with this automation id.</summary>
    private static ElementCriteria ByAutomationId(string automationId) =>
        new(AutomationId: automationId, ControlTypes: ["Button"]);

    private ElementRegistry CreateSut(int capacity = ElementRegistry.DefaultCapacity) =>
        new(_tree, _worker, _windows.Object, capacity, firstWindowNumber: 1, TimeProvider.System);

    private FakeUiaNode Window(nint handle, params FakeUiaNode[] children)
    {
        var window = new FakeUiaNode("Window", $"Window {handle}").With(children);
        _tree.WithWindow(handle, window);
        return window;
    }

    private static FakeUiaNode Button(string name, string? automationId = null, IReadOnlyList<int>? runtimeId = null) =>
        new("Button", name) { AutomationId = automationId, ClassName = "Button", RuntimeId = runtimeId ?? FakeUiaNode.NewRuntimeId() };

    /// <summary>Registers a node; a selector counts as proven unique unless <paramref name="unique"/> says otherwise.</summary>
    private string? Register(ElementRegistry sut, FakeUiaNode node, nint? handle = null, ElementCriteria? selector = null, bool? unique = true) =>
        sut.Register(handle ?? Handle, _tree.ReadElement(node), new ElementOrigin(1, selector, Unique: selector == null ? null : unique));

    #region Refs

    [Fact]
    public void Register_NumbersWindowsAndTheirElements()
    {
        // Arrange
        var sut = CreateSut();
        var save = Button("Save");
        var open = Button("Open");
        var ok = Button("OK");
        Window(Handle, save, open);
        Window(OtherHandle, ok);

        // Act / Assert
        Register(sut, save).Should().Be("w1e1");
        Register(sut, open).Should().Be("w1e2");
        Register(sut, ok, OtherHandle).Should().Be("w2e1");
        sut.Count.Should().Be(3);
    }

    [Fact]
    public void Register_TheSameElementAgain_KeepsItsRef_EvenWhenItsNameChanged()
    {
        // Arrange - the same runtime id, control type, automation id and class: the same element
        var sut = CreateSut();
        var id = FakeUiaNode.NewRuntimeId();
        var play = Button("Play", "PlayButton", id);
        Window(Handle, play);
        var first = Register(sut, play);

        // Act - read again later, now labelled Pause
        var second = Register(sut, Button("Pause", "PlayButton", id));

        // Assert
        second.Should().Be(first);
        sut.Count.Should().Be(1);
        sut.Lookup(first!).Fingerprint!.Name.Should().Be("Pause");
    }

    [Fact]
    public void Register_ADifferentElementWithAReusedRuntimeId_GetsANewRef_AndTheOldRefIsDropped()
    {
        // Arrange
        var sut = CreateSut();
        var id = FakeUiaNode.NewRuntimeId();
        var oldRef = Register(sut, Button("Save", "SaveButton", id));

        // Act - an edit box now has that runtime id
        var newRef = Register(sut, new FakeUiaNode("Edit", "Name") { AutomationId = "NameBox", RuntimeId = id });

        // Assert - the old ref never names the edit box
        newRef.Should().Be("w1e2");
        sut.Lookup(oldRef!).ErrorCode.Should().Be(UIElementErrorCode.ElementStale);
        sut.Lookup(newRef!).Fingerprint!.ControlType.Should().Be("Edit");
    }

    [Fact]
    public void Register_WithoutARuntimeIdOrAWindow_GivesNoRef()
    {
        var sut = CreateSut();

        Register(sut, new FakeUiaNode("Text", "Label") { RuntimeId = null }).Should().BeNull();
        Register(sut, Button("Save"), handle: 0x9999).Should().BeNull("the handle is no window");
        sut.Count.Should().Be(0);
    }

    [Fact]
    public void Refs_AreNeverGivenTwice_AndExpireLeastRecentlyUsedFirst()
    {
        // Arrange
        var sut = CreateSut(capacity: 2);
        var first = Register(sut, Button("A"));
        var second = Register(sut, Button("B"));
        sut.Lookup(first!).Success.Should().BeTrue(); // a lookup does not count as a use

        // Act
        var third = Register(sut, Button("C"));
        var again = Register(sut, Button("D"));

        // Assert - numbers keep counting; the two oldest expired
        new[] { first, second, third, again }.Should().Equal("w1e1", "w1e2", "w1e3", "w1e4");
        sut.Count.Should().Be(2);
        var expired = sut.Lookup(first!);
        expired.ErrorCode.Should().Be(UIElementErrorCode.ElementStale);
        expired.Error.Should().Contain("expired").And.Contain("find_ui_elements");
    }

    [Fact]
    public void Lookup_IgnoresTheLetterCase_AndRefusesWhatIsNoRef()
    {
        // Arrange
        var sut = CreateSut();
        var save = Register(sut, Button("Save"));

        // Act / Assert
        sut.Lookup("W1E1").Should().Match<ElementLookup>(l => l.Success && l.Ref == save && l.WindowHandle == Handle);
        sut.Lookup("save").Error.Should().Contain("is not an element ref");
        sut.Lookup("w1e0").Error.Should().Contain("is not an element ref");
    }

    [Fact]
    public void Lookup_OfARefThisSessionNeverIssued_SaysRefsDoNotSurviveARestart()
    {
        var sut = CreateSut();
        Register(sut, Button("Save"));

        sut.Lookup("w1e9").Error.Should().Contain("not issued by this server session");
        sut.Lookup("w7e1").Error.Should().Contain("not issued by this server session");
    }

    [Fact]
    public void Lookup_TellsARefThisSessionNeverIssued_FromOneWhoseElementIsGone()
    {
        // Arrange - one ref of a window that closed, one that expired, one still there
        var sut = CreateSut(capacity: 1);
        var expired = Register(sut, Button("Save"));
        var live = Register(sut, Button("Open"));

        // Act
        var neverIssued = sut.Lookup("w1e9");
        var malformed = sut.Lookup("save");
        var dropped = sut.Lookup(expired!);
        var found = sut.Lookup(live!);
        _processes.Remove(Handle);
        var closed = sut.Lookup(live!);

        // Assert - only a ref this session never issued says nothing about any element
        neverIssued.NotIssued.Should().BeTrue();
        malformed.NotIssued.Should().BeTrue();
        dropped.NotIssued.Should().BeFalse();
        found.NotIssued.Should().BeFalse();
        closed.NotIssued.Should().BeFalse();
        closed.ErrorCode.Should().Be(UIElementErrorCode.ElementStale);
    }

    [Fact]
    public void Lookup_OfARefOfAClosedWindow_SaysItIsStale_NotThatItWasNeverIssued()
    {
        var sut = CreateSut();
        var save = Register(sut, Button("Save"));
        _processes.Remove(Handle);
        sut.NextGeneration();

        sut.Lookup(save!).Error.Should().Contain("is stale").And.NotContain("not issued");
        sut.Lookup(save!).NotIssued.Should().BeFalse();
    }

    [Fact]
    public void TheProductionRegistry_StartsWindowNumbersAtARandomValueInEightMillion()
    {
        // A ref kept from before a restart names a window of the new session only when both sessions number that window:
        // the start is drawn from 1,000,000-8,999,999, seven digits that fit the ref form
        var numbers = Enumerable.Range(0, 20)
            .Select(_ => new ElementRegistry(_tree, _worker, _windows.Object))
            .Select(sut => sut.Register(Handle, _tree.ReadElement(Button("Save")), new ElementOrigin(1)))
            .Select(r => ElementRefs.TryParse(r, out var window, out _) ? window : 0)
            .ToList();

        numbers.Should().OnlyContain(n => n >= ElementRegistry.FirstWindowNumberMin && n < ElementRegistry.FirstWindowNumberMax);
        numbers.Distinct().Should().HaveCountGreaterThan(10);
        (ElementRegistry.FirstWindowNumberMax - ElementRegistry.FirstWindowNumberMin).Should().BeGreaterThanOrEqualTo(8_000_000);
        ElementRefs.IsWellFormed(ElementRefs.Format(ElementRegistry.FirstWindowNumberMax + 1_000_000, 99_999)).Should().BeTrue();
    }

    [Fact]
    public void ARefOfAnotherSession_IsNotIssuedByThisOne()
    {
        // Arrange - two sessions whose window numbers do not overlap
        var before = new ElementRegistry(_tree, _worker, _windows.Object, 100, firstWindowNumber: 4_120_035, TimeProvider.System);
        var oldRef = before.Register(Handle, _tree.ReadElement(Button("Save")), new ElementOrigin(1));
        var after = new ElementRegistry(_tree, _worker, _windows.Object, 100, firstWindowNumber: 6_000_000, TimeProvider.System);
        after.Register(Handle, _tree.ReadElement(Button("Delete")), new ElementOrigin(1));

        // Act
        var resolution = after.Resolve(oldRef!, None);

        // Assert
        oldRef.Should().Be("w4120035e1");
        resolution.ErrorCode.Should().Be(UIElementErrorCode.ElementStale);
        resolution.Error.Should().Contain("not issued by this server session");
    }

    [Fact]
    public void TheCapacity_OutlastsTheLargestRead()
    {
        // One get_ui_elements or capture_with_elements call registers at most MaxElementsLimit elements: refs from before
        // it survive
        ElementRegistry.DefaultCapacity.Should().BeGreaterThanOrEqualTo(2 * WindowsAppManagerMcp.Tools.UIElementTools.MaxElementsLimit);
    }

    #endregion

    #region Windows

    [Fact]
    public void Refs_OfAWindowThatClosed_AreStale()
    {
        // Arrange
        var sut = CreateSut();
        var save = Register(sut, Button("Save"));

        // Act
        _processes.Remove(Handle);
        var lookup = sut.Lookup(save!);

        // Assert
        lookup.ErrorCode.Should().Be(UIElementErrorCode.ElementStale);
        lookup.Error.Should().Contain("its window closed");
        sut.Count.Should().Be(0);
    }

    [Fact]
    public void AReusedWindowHandle_GetsANewWindowNumber_AndTheOldRefsAreStale()
    {
        // Arrange
        var sut = CreateSut();
        var save = Register(sut, Button("Save"));

        // Act - the handle now belongs to a window of another process
        _processes[Handle] = 999;
        var next = Register(sut, Button("Save"));

        // Assert
        next.Should().Be("w2e1");
        sut.Lookup(save!).ErrorCode.Should().Be(UIElementErrorCode.ElementStale);
    }

    [Fact]
    public void AWindowHandleReusedByAWindowOfAnotherClass_InTheSameProcess_DropsTheOldRefs()
    {
        // Arrange
        var sut = CreateSut();
        var save = Register(sut, Button("Save"));

        // Act - the app closed the window and opened a dialog that got the same handle
        _classes[Handle] = "#32770";
        var lookup = sut.Lookup(save!);

        // Assert
        lookup.ErrorCode.Should().Be(UIElementErrorCode.ElementStale);
        Register(sut, Button("OK")).Should().Be("w2e1");
    }

    [Fact]
    public void NextGeneration_CountsUp_AndDropsTheRefsOfClosedWindows()
    {
        // Arrange
        var sut = CreateSut();
        Register(sut, Button("Save"));
        Register(sut, Button("OK"), OtherHandle);

        // Act
        _processes.Remove(Handle);
        var first = sut.NextGeneration();
        var second = sut.NextGeneration();

        // Assert
        second.Should().Be(first + 1);
        sut.Count.Should().Be(1);
    }

    #endregion

    #region Resolution

    [Fact]
    public void Resolve_ALiveElement_ReadsItOnce_AndReturnsIt()
    {
        // Arrange
        var sut = CreateSut();
        var save = Button("Save", "SaveButton");
        Window(Handle, new FakeUiaNode("Pane", "Toolbar").With(save));
        var saveRef = Register(sut, save);

        // Act
        var resolution = sut.Resolve(saveRef!, None);

        // Assert - a live read (it asks the provider), no search
        resolution.Success.Should().BeTrue();
        resolution.Element!.Should().Match<ResolvedElement>(r =>
            r.Ref == saveRef && r.WindowHandle == Handle && ReferenceEquals(r.Node, save) && r.Via == ElementResolutionPath.Live);
        _tree.Finds.Should().BeEmpty();
    }

    [Fact]
    public void Resolve_AnElementReCreatedWithTheSameAutomationId_MovesTheRefToIt()
    {
        // Arrange
        var sut = CreateSut();
        var save = Button("Save", "SaveButton");
        var toolbar = new FakeUiaNode("Pane", "Toolbar").With(save, Button("Open", "OpenButton"));
        Window(Handle, toolbar);
        var saveRef = Register(sut, save, selector: ByAutomationId("SaveButton"));

        // Act - the toolbar was rebuilt: a new element, a new runtime id, the same control (its label changed too)
        save.Vanish();
        var recreated = Button("Save all", "SaveButton");
        toolbar.Insert(0, recreated);
        var resolution = sut.Resolve(saveRef!, None);

        // Assert
        resolution.Element!.Should().Match<ResolvedElement>(r =>
            r.Ref == saveRef && ReferenceEquals(r.Node, recreated) && r.Via == ElementResolutionPath.Selector);
        sut.Resolve(saveRef!, None).Element!.Via.Should().Be(ElementResolutionPath.Live, "the ref now holds the new element");
    }

    [Fact]
    public void Resolve_AWalkRefWhoseElementWasReCreated_IsStale_NotMovedToALookAlike()
    {
        // Arrange - a tree walk gave the ref: nothing proved that no other element looks like it
        var sut = CreateSut();
        var save = Button("Save", "SaveButton");
        var toolbar = new FakeUiaNode("Pane", "Toolbar").With(save);
        Window(Handle, toolbar);
        var saveRef = Register(sut, save);

        // Act
        save.Vanish();
        toolbar.With(Button("Save", "SaveButton"));
        var resolution = sut.Resolve(saveRef!, None);

        // Assert
        resolution.ErrorCode.Should().Be(UIElementErrorCode.ElementStale);
        resolution.Error.Should().Contain("not moved to a look-alike").And.Contain("find_ui_elements");
    }

    [Fact]
    public void Resolve_ARowsButtonWhoseRowIsGone_IsStale_NeverTheOtherRowsButton()
    {
        // Arrange - two rows with the same templated Delete button; the ref came from a search that saw both (not unique)
        var sut = CreateSut();
        var deleteA = Button("Delete", "DeleteButton");
        var deleteB = Button("Delete", "DeleteButton");
        var rowA = new FakeUiaNode("DataItem", "Row A") { AutomationId = "RowA" }.With(deleteA);
        var rowB = new FakeUiaNode("DataItem", "Row B") { AutomationId = "RowB" }.With(deleteB);
        var grid = new FakeUiaNode("DataGrid", "Rows").With(rowA, rowB);
        Window(Handle, grid);
        var deleteRef = Register(sut, deleteA, selector: ByAutomationId("DeleteButton"), unique: false);

        // Act - row A was deleted: row B's button is now the only Delete button
        rowA.Vanish();
        deleteA.Vanish();
        var resolution = sut.Resolve(deleteRef!, None);

        // Assert
        resolution.ErrorCode.Should().Be(UIElementErrorCode.ElementStale);
    }

    [Fact]
    public void Register_ASearchThatFoundATwin_DropsTheProvenSelector()
    {
        // Arrange - first found alone, later found next to a twin
        var sut = CreateSut();
        var delete = Button("Delete", "DeleteButton");
        var list = new FakeUiaNode("List", "Rows").With(delete);
        Window(Handle, list);
        var deleteRef = Register(sut, delete, selector: ByAutomationId("DeleteButton"));
        list.With(Button("Delete", "DeleteButton"));
        Register(sut, delete, selector: ByAutomationId("DeleteButton"), unique: false).Should().Be(deleteRef);

        // Act - the first one is gone
        delete.Vanish();
        var resolution = sut.Resolve(deleteRef!, None);

        // Assert - not moved to the twin
        resolution.ErrorCode.Should().Be(UIElementErrorCode.ElementStale);
    }

    [Fact]
    public void Resolve_AnElementGoneWithANewProxyOfTheSameRuntimeId_FindsItByTheId()
    {
        // Arrange - the engine handed out a node that no longer reads, but the element is still there
        var sut = CreateSut();
        var id = FakeUiaNode.NewRuntimeId();
        var stale = Button("Save", "SaveButton", id);
        var saveRef = Register(sut, stale);
        stale.ReadException = FakeUiaTree.Vanished();
        var current = Button("Save", "SaveButton", id);
        Window(Handle, new FakeUiaNode("Pane", "Toolbar").With(current));

        // Act
        var resolution = sut.Resolve(saveRef!, None);

        // Assert
        resolution.Element!.Should().Match<ResolvedElement>(r => ReferenceEquals(r.Node, current) && r.Via == ElementResolutionPath.RuntimeId);
    }

    [Fact]
    public void Resolve_NeverReturnsADifferentControlThatTookTheRuntimeId()
    {
        // Arrange - the button is gone and a check box now has its runtime id
        var sut = CreateSut();
        var id = FakeUiaNode.NewRuntimeId();
        var save = Button("Save", "SaveButton", id);
        Window(Handle, save);
        var saveRef = Register(sut, save);
        save.Vanish();
        _tree.FromHandle(Handle).Should().BeOfType<FakeUiaNode>().Which
            .With(new FakeUiaNode("CheckBox", "Save") { AutomationId = "SaveOption", RuntimeId = id });

        // Act
        var resolution = sut.Resolve(saveRef!, None);

        // Assert
        resolution.Success.Should().BeFalse();
        resolution.ErrorCode.Should().Be(UIElementErrorCode.ElementStale);
        resolution.Error.Should().Contain("is gone").And.Contain("find_ui_elements");
    }

    [Fact]
    public void Resolve_WhenSeveralElementsFitTheGoneOne_IsAmbiguous_AndMovesTheRefNowhere()
    {
        // Arrange - a button without an automation id, found alone by its name; now two buttons are named Delete
        var sut = CreateSut();
        var delete = Button("Delete");
        var list = new FakeUiaNode("List", "Rows").With(delete);
        Window(Handle, list);
        var deleteRef = Register(sut, delete, selector: new ElementCriteria(Name: "Delete", ControlTypes: ["Button"]));
        delete.Vanish();
        list.With(Button("Delete"), Button("Delete"));

        // Act
        var resolution = sut.Resolve(deleteRef!, None);

        // Assert
        resolution.ErrorCode.Should().Be(UIElementErrorCode.AmbiguousElement);
        resolution.CandidateCount.Should().Be(2);
        resolution.Error.Should().Contain("not moved");
        sut.Resolve(deleteRef!, None).ErrorCode.Should().Be(UIElementErrorCode.AmbiguousElement, "the ref stays unresolved");
    }

    [Fact]
    public void Resolve_WithoutAnAutomationId_RequiresTheSameName()
    {
        // Arrange
        var sut = CreateSut();
        var save = Button("Save");
        var pane = new FakeUiaNode("Pane", "Toolbar").With(save);
        Window(Handle, pane);
        var saveRef = Register(sut, save, selector: new ElementCriteria(ControlTypes: ["Button"]));

        // Act - a button with another name took its place
        save.Vanish();
        pane.With(Button("Save as"));
        var resolution = sut.Resolve(saveRef!, None);

        // Assert
        resolution.ErrorCode.Should().Be(UIElementErrorCode.ElementStale);
    }

    [Fact]
    public void Resolve_AnElementWithNeitherAutomationIdNorName_IsStaleOnceItIsGone()
    {
        // Arrange
        var sut = CreateSut();
        var pane = new FakeUiaNode("Pane", null);
        Window(Handle, pane);
        var paneRef = Register(sut, pane);

        // Act
        pane.Vanish();
        var resolution = sut.Resolve(paneRef!, None);

        // Assert
        resolution.ErrorCode.Should().Be(UIElementErrorCode.ElementStale);
        resolution.Error.Should().Contain("is gone");
    }

    [Fact]
    public void Resolve_UsesTheSelectorThatFoundTheElement()
    {
        // Arrange - find_ui_elements found it by a part of its name
        var sut = CreateSut();
        var save = Button("Save document");
        var pane = new FakeUiaNode("Pane", "Toolbar").With(save, Button("Open document"));
        Window(Handle, pane);
        var selector = new ElementCriteria(Name: "save", NameMatch: ElementNameMatch.Contains, ControlTypes: ["Button"]);
        var saveRef = Register(sut, save, selector: selector);

        // Act
        save.Vanish();
        var recreated = Button("Save document");
        pane.With(recreated);
        var resolution = sut.Resolve(saveRef!, None);

        // Assert - the selector ran again, and the fingerprint picked the element (Open document also contains no "save")
        resolution.Element!.Should().Match<ResolvedElement>(r => ReferenceEquals(r.Node, recreated) && r.Via == ElementResolutionPath.Selector);
    }

    [Fact]
    public void Resolve_ToAnElementThatHasARefOfItsOwn_ReturnsThatRef_AndDropsTheOldOne()
    {
        // Arrange
        var sut = CreateSut();
        var save = Button("Save", "SaveButton");
        var pane = new FakeUiaNode("Pane", "Toolbar").With(save);
        Window(Handle, pane);
        var oldRef = Register(sut, save, selector: ByAutomationId("SaveButton"));
        save.Vanish();
        var recreated = Button("Save", "SaveButton");
        pane.With(recreated);
        var newRef = Register(sut, recreated);

        // Act
        var resolution = sut.Resolve(oldRef!, None);

        // Assert - one element never has two refs
        resolution.Element!.Ref.Should().Be(newRef);
        sut.Lookup(oldRef!).ErrorCode.Should().Be(UIElementErrorCode.ElementStale);
    }

    [Fact]
    public void Resolve_WhenTheLiveReadSeesAnotherRuntimeId_FindsTheElementAgain()
    {
        // Arrange - the stored node now answers for another element (a reused proxy)
        var sut = CreateSut();
        var save = Button("Save", "SaveButton");
        var pane = new FakeUiaNode("Pane", "Toolbar").With(save);
        Window(Handle, pane);
        var saveRef = sut.Register(Handle, _tree.ReadElement(save) with { RuntimeId = [42, -1] },
            new ElementOrigin(1, ByAutomationId("SaveButton"), Unique: true));

        // Act
        var resolution = sut.Resolve(saveRef!, None);

        // Assert - not accepted as live; found again by its proven selector
        resolution.Element!.Via.Should().Be(ElementResolutionPath.Selector);
    }

    [Fact]
    public void Resolve_WhenTheLiveReadSeesAnotherControlWithTheStoredRuntimeId_DoesNotAcceptIt()
    {
        // Arrange - the stored proxy now answers as a check box (a reused child window, a recycled container)
        var sut = CreateSut();
        var id = FakeUiaNode.NewRuntimeId();
        var node = new FakeUiaNode("CheckBox", "Save") { AutomationId = "SaveOption", RuntimeId = id };
        Window(Handle, node);
        var stored = _tree.ReadElement(node);
        var saveRef = sut.Register(Handle, stored with
        {
            Properties = stored.Properties with { ControlType = "Button", AutomationId = "SaveButton" }
        }, new ElementOrigin(1));

        // Act
        var resolution = sut.Resolve(saveRef!, None);

        // Assert
        resolution.Success.Should().BeFalse();
        resolution.ErrorCode.Should().Be(UIElementErrorCode.ElementStale);
    }

    [Fact]
    public void Resolve_ARecycledListItemThatShowsAnotherRow_IsNotTheStoredItem()
    {
        // Arrange - a virtualizing list reused Item 3's element, runtime id and all, for Item 47
        var sut = CreateSut();
        var id = FakeUiaNode.NewRuntimeId();
        var container = new FakeUiaNode("ListItem", "Item 47") { RuntimeId = id };
        Window(Handle, new FakeUiaNode("List", "Items").With(container));
        var read = _tree.ReadElement(container);
        var itemRef = sut.Register(Handle, read with { Properties = read.Properties with { Name = "Item 3" } }, new ElementOrigin(1));

        // Act
        var resolution = sut.Resolve(itemRef!, None);
        var nowRef = Register(sut, container);

        // Assert - the ref of Item 3 never names Item 47, and Item 47 gets a ref of its own
        resolution.Success.Should().BeFalse();
        resolution.ErrorCode.Should().Be(UIElementErrorCode.ElementStale);
        nowRef.Should().NotBe(itemRef);
        sut.Lookup(itemRef!).ErrorCode.Should().Be(UIElementErrorCode.ElementStale);
    }

    [Fact]
    public void Resolve_AListItemWithAnAutomationId_KeepsItsRefWhenItsNameChanges()
    {
        // Arrange - an automation id says which item it is, so its name may change
        var sut = CreateSut();
        var id = FakeUiaNode.NewRuntimeId();
        var item = new FakeUiaNode("ListItem", "Inbox (3)") { AutomationId = "Inbox", RuntimeId = id };
        Window(Handle, new FakeUiaNode("List", "Folders").With(item));
        var read = _tree.ReadElement(item);
        var itemRef = sut.Register(Handle, read with { Properties = read.Properties with { Name = "Inbox (2)" } }, new ElementOrigin(1));

        // Act
        var resolution = sut.Resolve(itemRef!, None);

        // Assert
        resolution.Element!.Via.Should().Be(ElementResolutionPath.Live);
    }

    [Fact]
    public void Resolve_WhenTheLiveReadHasNoRuntimeId_DoesNotAcceptItAsLive()
    {
        // Arrange - the stored node now reads without a runtime id: nothing says which element answered
        var sut = CreateSut();
        var node = new FakeUiaNode("Button", "Save") { AutomationId = "SaveButton", RuntimeId = null };
        Window(Handle, node);
        var saveRef = sut.Register(Handle, _tree.ReadElement(node) with { RuntimeId = [42, -7] }, new ElementOrigin(1));

        // Act
        var resolution = sut.Resolve(saveRef!, None);

        // Assert
        resolution.Success.Should().BeFalse();
        resolution.ErrorCode.Should().Be(UIElementErrorCode.ElementStale);
    }

    [Fact]
    public void Resolve_AfterTheDeadline_TimesOutWithoutMovingTheRef()
    {
        // Arrange
        var sut = CreateSut();
        var save = Button("Save", "SaveButton");
        Window(Handle, new FakeUiaNode("Pane", "Toolbar").With(save));
        var saveRef = Register(sut, save);
        save.Vanish();
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();

        // Act
        var resolution = sut.Resolve(saveRef!, cancelled.Token);

        // Assert
        resolution.ErrorCode.Should().Be(UIElementErrorCode.OperationTimedOut, "an unfinished search proves nothing gone");
        sut.Lookup(saveRef!).Success.Should().BeTrue("the ref was not dropped");
    }

    [Fact]
    public async Task ResolveAsync_OfAHungWindow_ReadsNothing()
    {
        // Arrange
        var sut = CreateSut();
        var save = Button("Save");
        Window(Handle, save);
        var saveRef = Register(sut, save);
        _windows.Setup(w => w.IsHungAppWindow(Handle)).Returns(true);
        var readsBefore = _tree.Reads.Count;

        // Act
        var resolution = await sut.ResolveAsync(saveRef!, TimeSpan.FromSeconds(5), None);

        // Assert
        resolution.ErrorCode.Should().Be(UIElementErrorCode.NotResponding);
        _tree.Reads.Should().HaveCount(readsBefore);
    }

    [Fact]
    public async Task ResolveAsync_RunsOnTheWorker_ForTheWindowsApplication()
    {
        // Arrange
        var sut = CreateSut();
        var save = Button("Save");
        Window(Handle, save);
        var saveRef = Register(sut, save);

        // Act
        var resolution = await sut.ResolveAsync(saveRef!, TimeSpan.FromSeconds(5), None);

        // Assert
        resolution.Element!.Node.Should().BeSameAs(save);
        _worker.Calls.Should().Be(1);
        _worker.LastTargetProcessId.Should().Be(100);
    }

    [Fact]
    public async Task ResolveAsync_WhenTheWorkerGivesUp_TimesOut()
    {
        // Arrange
        var worker = new Mock<IUiaWorker>();
        worker.Setup(w => w.RunAsync(It.IsAny<int>(), It.IsAny<Func<CancellationToken, ElementResolution>>(), It.IsAny<CancellationToken>(), It.IsAny<TimeSpan>()))
            .ThrowsAsync(new TimeoutException("UI Automation did not answer in time."));
        var sut = new ElementRegistry(_tree, worker.Object, _windows.Object, 10, 1, TimeProvider.System);
        var saveRef = sut.Register(Handle, _tree.ReadElement(Button("Save")), new ElementOrigin(1));

        // Act
        var resolution = await sut.ResolveAsync(saveRef!, TimeSpan.FromSeconds(1), None);

        // Assert
        resolution.ErrorCode.Should().Be(UIElementErrorCode.OperationTimedOut);
    }

    [Fact]
    public void Registration_FromManyThreads_GivesEveryElementItsOwnRef()
    {
        // Arrange
        var sut = CreateSut();
        var elements = Enumerable.Range(0, 400).Select(i => _tree.ReadElement(Button($"B{i}"))).ToList();

        // Act
        var refs = elements.AsParallel().Select(e => sut.Register(Handle, e, new ElementOrigin(1))).ToList();

        // Assert
        refs.Should().OnlyHaveUniqueItems().And.NotContainNulls();
        sut.Count.Should().Be(400);
    }

    #endregion
}
