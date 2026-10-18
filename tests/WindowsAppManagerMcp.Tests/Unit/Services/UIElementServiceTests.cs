namespace WindowsAppManagerMcp.Tests.Unit.Services;

/// <summary>
/// Unit tests for UIElementService's tree walk and filters, over an in-memory UI Automation tree
/// (<see cref="FakeUiaTree"/>). No real window is read.
/// </summary>
public class UIElementServiceTests
{
    private static readonly nint Handle = 0x1234;

    private readonly FakeUiaTree _tree = new();
    private readonly InlineUiaWorker _worker = new();

    private UIElementService CreateSut() => new(_tree, _worker);

    private static FakeUiaNode Node(string controlType, string name) => new(controlType, name);

    private FakeUiaNode Window(params FakeUiaNode[] children)
    {
        var window = new FakeUiaNode("Window", "Main window").With(children);
        _tree.WithWindow(Handle, window);
        return window;
    }

    /// <summary>The returned tree as "A(B,C),D": names, with children in parentheses.</summary>
    private static string Shape(IReadOnlyList<UIElementInfo>? elements) =>
        elements == null
            ? "<null>"
            : string.Join(",", elements.Select(e =>
                e.Name + (e.Children is { Count: > 0 } children ? $"({Shape(children)})" : string.Empty)));

    private static int CountAll(IReadOnlyList<UIElementInfo>? elements) =>
        elements?.Sum(e => 1 + CountAll(e.Children)) ?? 0;

    /// <summary>
    /// Window
    ///   Pane "Host" > ToolBar "Tools" > Button "Save", Button "Open"
    ///   Edit "Name"
    ///   Pane "Footer" > Group "Box" > Button "OK"
    /// </summary>
    private void BuildToolbarWindow() =>
        Window(
            Node("Pane", "Host").With(
                Node("ToolBar", "Tools").With(
                    Node("Button", "Save"),
                    Node("Button", "Open"))),
            Node("Edit", "Name"),
            Node("Pane", "Footer").With(
                Node("Group", "Box").With(
                    Node("Button", "OK"))));

    #region Validation and window lookup

    [Fact]
    public async Task GetUIElements_WithZeroHandle_ReturnsInvalidHandleWithoutReadingTheTree()
    {
        // Act
        var result = await CreateSut().GetUIElementsAsync(nint.Zero);

        // Assert
        result.Success.Should().BeFalse();
        result.ErrorCode.Should().Be(UIElementErrorCode.InvalidHandle);
        result.Error.Should().Contain("Invalid window handle");
        result.Elements.Should().BeNull();
        result.Depth.Should().Be(0);
        _worker.Calls.Should().Be(0);
    }

    [Fact]
    public async Task GetUIElements_WhenUiAutomationIsUnavailable_ReturnsAutomationUnavailable()
    {
        // Arrange
        BuildToolbarWindow();
        _tree.Available = false;

        // Act
        var result = await CreateSut().GetUIElementsAsync(Handle);

        // Assert
        result.Success.Should().BeFalse();
        result.ErrorCode.Should().Be(UIElementErrorCode.AutomationUnavailable);
    }

    [Fact]
    public async Task GetUIElements_WithUnknownHandle_ReturnsWindowNotFound()
    {
        // Act
        var result = await CreateSut().GetUIElementsAsync(new nint(999));

        // Assert
        result.Success.Should().BeFalse();
        result.ErrorCode.Should().Be(UIElementErrorCode.WindowNotFound);
        result.WindowHandle.Should().Be(999);
    }

    [Fact]
    public async Task GetUIElements_WhenTheWindowVanishesDuringLookup_ReturnsWindowNotFound()
    {
        // Arrange
        _tree.FromHandleException = FakeUiaTree.Vanished("window");

        // Act
        var result = await CreateSut().GetUIElementsAsync(Handle);

        // Assert
        result.Success.Should().BeFalse();
        result.ErrorCode.Should().Be(UIElementErrorCode.WindowNotFound);
    }

    [Fact]
    public async Task GetUIElements_WhenLookupFailsUnexpectedly_ReturnsOperationException()
    {
        // Arrange
        _tree.FromHandleException = new InvalidOperationException("provider exploded");

        // Act
        var result = await CreateSut().GetUIElementsAsync(Handle);

        // Assert
        result.Success.Should().BeFalse();
        result.ErrorCode.Should().Be(UIElementErrorCode.OperationException);
        result.Error.Should().Contain("provider exploded");
    }

    [Theory]
    [InlineData("Buton")]
    [InlineData("Button,Textbox")]
    [InlineData("Btn, Edit")]
    public async Task GetUIElements_WithUnknownControlType_ReturnsInvalidControlTypeListingTheValidNames(string controlTypes)
    {
        // Arrange
        BuildToolbarWindow();
        var filter = new UIElementFilter(ControlTypes: controlTypes.Split(',', StringSplitOptions.TrimEntries));

        // Act
        var result = await CreateSut().GetUIElementsAsync(Handle, filter: filter);

        // Assert
        result.Success.Should().BeFalse();
        result.ErrorCode.Should().Be(UIElementErrorCode.InvalidControlType);
        result.Error.Should().Contain("Button").And.Contain("MenuItem").And.Contain("AppBar").And.Contain("Unknown");
        foreach (var unknown in filter.ControlTypes!.Where(t => UiaControlTypes.Canonicalize(t) == null))
        {
            result.Error.Should().Contain($"'{unknown}'");
        }

        _tree.Reads.Should().BeEmpty("an invalid filter is rejected before anything is read");
    }

    [Theory]
    [InlineData("button")]
    [InlineData("BUTTON")]
    [InlineData(" Button ")]
    public async Task GetUIElements_ControlTypeFilter_IgnoresCaseAndSurroundingSpace(string controlType)
    {
        // Arrange
        BuildToolbarWindow();

        // Act
        var result = await CreateSut().GetUIElementsAsync(Handle, filter: new UIElementFilter(ControlTypes: [controlType]));

        // Assert
        result.Success.Should().BeTrue();
        Shape(result.Elements).Should().Be("Save,Open,OK");
    }

    [Theory]
    [InlineData("AppBar")]
    [InlineData("SemanticZoom")]
    [InlineData("Unknown")]
    public async Task GetUIElements_AcceptsTheFallbackControlTypeNames(string controlType)
    {
        // Arrange
        Window(Node(controlType, "Special"), Node("Button", "Other"));

        // Act
        var result = await CreateSut().GetUIElementsAsync(Handle, filter: new UIElementFilter(ControlTypes: [controlType]));

        // Assert
        result.Success.Should().BeTrue();
        Shape(result.Elements).Should().Be("Special");
    }

    #endregion

    #region controlTypes: matches inside other containers are returned

    [Fact]
    public async Task GetUIElements_ControlTypeFilter_ReturnsMatchesNestedInNonMatchingContainers()
    {
        // Arrange - the reported bug: Buttons inside Pane/ToolBar/Group came back as []
        BuildToolbarWindow();

        // Act
        var result = await CreateSut().GetUIElementsAsync(Handle, filter: new UIElementFilter(ControlTypes: ["Button", "Edit"]));

        // Assert - each match takes the place of its left-out containers, in tree order
        result.Success.Should().BeTrue();
        Shape(result.Elements).Should().Be("Save,Open,Name,OK");
        result.TotalElementCount.Should().Be(4);
    }

    [Fact]
    public async Task GetUIElements_ControlTypeFilter_KeepsMatchesNestedUnderTheirNearestMatchingAncestor()
    {
        // Arrange
        BuildToolbarWindow();

        // Act
        var result = await CreateSut().GetUIElementsAsync(Handle, filter: new UIElementFilter(ControlTypes: ["ToolBar", "Button"]));

        // Assert
        Shape(result.Elements).Should().Be("Tools(Save,Open),OK");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("Button")]
    [InlineData("Pane,Button")]
    [InlineData("Group,Edit")]
    [InlineData("Window")]
    public async Task GetUIElements_TotalElementCount_CountsEachReturnedElementOnce(string? controlTypes)
    {
        // Arrange
        BuildToolbarWindow();
        var filter = new UIElementFilter(ControlTypes: controlTypes?.Split(','));

        // Act
        var result = await CreateSut().GetUIElementsAsync(Handle, filter: filter);

        // Assert
        result.Success.Should().BeTrue();
        result.TotalElementCount.Should().Be(CountAll(result.Elements));
    }

    [Fact]
    public async Task GetUIElements_WithoutFilters_ReturnsTheWholeTreeNested()
    {
        // Arrange
        BuildToolbarWindow();

        // Act
        var result = await CreateSut().GetUIElementsAsync(Handle);

        // Assert
        Shape(result.Elements).Should().Be("Host(Tools(Save,Open)),Name,Footer(Box(OK))");
        result.TotalElementCount.Should().Be(8);
    }

    #endregion

    #region maxDepth counts levels below the window

    [Fact]
    public async Task GetUIElements_WithMaxDepthOne_ReturnsOnlyTheWindowsChildrenAndReadsNothingDeeper()
    {
        // Arrange
        BuildToolbarWindow();

        // Act
        var result = await CreateSut().GetUIElementsAsync(Handle, maxDepth: 1);

        // Assert
        Shape(result.Elements).Should().Be("Host,Name,Footer");
        result.Depth.Should().Be(1);
        result.DepthReached.Should().Be(1);
        _tree.ReadNodes.Should().NotContain(["Tools", "Box", "Save", "OK"]);
    }

    [Fact]
    public async Task GetUIElements_WithMaxDepthTwo_ReturnsExactlyTwoLevels()
    {
        // Arrange
        BuildToolbarWindow();

        // Act
        var result = await CreateSut().GetUIElementsAsync(Handle, maxDepth: 2);

        // Assert
        Shape(result.Elements).Should().Be("Host(Tools),Name,Footer(Box)");
        result.DepthReached.Should().Be(2);
    }

    [Fact]
    public async Task GetUIElements_WhenTheTreeIsShallowerThanMaxDepth_ReportsTheDepthReached()
    {
        // Arrange
        BuildToolbarWindow();

        // Act
        var result = await CreateSut().GetUIElementsAsync(Handle, maxDepth: 10);

        // Assert
        result.Depth.Should().Be(10);
        result.DepthReached.Should().Be(3);
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(-5, 1)]
    [InlineData(15, 10)]
    [InlineData(100, 10)]
    public async Task GetUIElements_ClampsMaxDepthToOneThroughTen(int requested, int applied)
    {
        // Arrange
        BuildToolbarWindow();

        // Act
        var result = await CreateSut().GetUIElementsAsync(Handle, maxDepth: requested);

        // Assert
        result.Success.Should().BeTrue();
        result.Depth.Should().Be(applied);
    }

    [Fact]
    public async Task GetUIElements_MaxDepthBoundsTheWalkEvenWhenFiltersLeaveContainersOut()
    {
        // Arrange - the Buttons are at level 3
        BuildToolbarWindow();

        // Act
        var result = await CreateSut().GetUIElementsAsync(Handle, maxDepth: 2, filter: new UIElementFilter(ControlTypes: ["Button"]));

        // Assert
        result.Success.Should().BeTrue();
        result.Elements.Should().BeEmpty();
        _tree.ReadNodes.Should().NotContain(["Save", "Open", "OK"]);
    }

    #endregion

    #region Size and interactable filters apply to each element only

    [Fact]
    public async Task GetUIElements_MinWidth_LeavesOutANarrowContainerButReturnsItsWideChildren()
    {
        // Arrange - a narrow sidebar with a wide button inside
        Window(
            new FakeUiaNode("Pane", "Sidebar") { Bounds = new UiaRect(0, 0, 5, 400) }.With(
                new FakeUiaNode("Button", "Wide") { Bounds = new UiaRect(0, 0, 80, 30) },
                new FakeUiaNode("Button", "Tiny") { Bounds = new UiaRect(0, 40, 4, 4) }));

        // Act
        var result = await CreateSut().GetUIElementsAsync(Handle, filter: new UIElementFilter(MinWidth: 20));

        // Assert
        Shape(result.Elements).Should().Be("Wide");
    }

    [Fact]
    public async Task GetUIElements_MinHeight_ReturnsChildrenOfAContainerWithoutBounds()
    {
        // Arrange - layout containers often report an empty rectangle
        Window(
            new FakeUiaNode("Group", "Layout") { Bounds = UiaRect.Empty }.With(
                new FakeUiaNode("Edit", "Field") { Bounds = new UiaRect(10, 10, 200, 24) }));

        // Act
        var result = await CreateSut().GetUIElementsAsync(Handle, filter: new UIElementFilter(MinHeight: 10));

        // Assert
        Shape(result.Elements).Should().Be("Field");
    }

    [Fact]
    public async Task GetUIElements_InteractableOnly_KeepsEnabledFocusableOrActionableElementsAtAnyDepth()
    {
        // Arrange
        Window(
            Node("Pane", "Panel").With(
                Node("Text", "Label"),
                new FakeUiaNode("Button", "Go") { ActionPatterns = ["Invoke"] },
                new FakeUiaNode("Edit", "Field") { IsKeyboardFocusable = true },
                new FakeUiaNode("Button", "Off") { IsEnabled = false, ActionPatterns = ["Invoke"] }),
            new FakeUiaNode("Group", "Disabled group") { IsEnabled = false }.With(
                new FakeUiaNode("CheckBox", "Option") { ActionPatterns = ["Toggle"] }));

        // Act
        var result = await CreateSut().GetUIElementsAsync(Handle, filter: new UIElementFilter(InteractableOnly: true));

        // Assert - Pane, Text and the disabled elements are left out; their qualifying children are not
        Shape(result.Elements).Should().Be("Go,Field,Option");
        result.Elements!.Should().OnlyContain(e => e.IsEnabled);
    }

    [Fact]
    public async Task GetUIElements_InteractableOnly_UsesTheFocusabilityAndPatternsFetchedWithTheLevel()
    {
        // Arrange
        Window(
            new FakeUiaNode("Button", "Off") { IsEnabled = false },
            new FakeUiaNode("Text", "Label"),
            new FakeUiaNode("Button", "Go") { ActionPatterns = ["Invoke"] });

        // Act
        var result = await CreateSut().GetUIElementsAsync(Handle, filter: new UIElementFilter(ControlTypes: ["Button"], InteractableOnly: true));

        // Assert - the cache request read them with each element (roadmap M3.1): no call per element, and focus is never read
        Shape(result.Elements).Should().Be("Go");
        _tree.Reads.Should().NotContain(r => r.StartsWith("Interactable:") || r.StartsWith("Interaction:"));
    }

    [Fact]
    public async Task GetUIElements_InteractableOnly_ReadOneAtATime_ReadsFocusAndPatternsOnlyForCandidates()
    {
        // Arrange - the cached fetch fails, so the children are read one at a time
        Window(
            new FakeUiaNode("Button", "Off") { IsEnabled = false },
            new FakeUiaNode("Text", "Label"),
            new FakeUiaNode("Button", "Go") { ActionPatterns = ["Invoke"] })
            .CachedChildrenException = new System.Runtime.InteropServices.COMException("cache request failed");

        // Act
        var result = await CreateSut().GetUIElementsAsync(Handle, filter: new UIElementFilter(ControlTypes: ["Button"], InteractableOnly: true));

        // Assert - each extra read is a cross-process call per element; focus itself is never read
        Shape(result.Elements).Should().Be("Go");
        _tree.Reads.Where(r => r.StartsWith("Interactable:")).Should().Equal("Interactable:Go");
        _tree.Reads.Should().NotContain(r => r.StartsWith("Interaction:"));
    }

    [Fact]
    public async Task GetUIElements_ActionableOnly_KeepsElementsWithAnActionPattern_EnabledOrNot()
    {
        // Arrange - capture_with_elements' marks (roadmap M3.11): what an agent can act on, not what can take focus
        Window(
            Node("Pane", "Panel").With(
                Node("Text", "Label"),
                new FakeUiaNode("Button", "Go") { ActionPatterns = ["Invoke"] },
                new FakeUiaNode("Pane", "Focusable pane") { IsKeyboardFocusable = true },
                new FakeUiaNode("Button", "Off") { IsEnabled = false, ActionPatterns = ["Invoke"] },
                new FakeUiaNode("Pane", "Scroller") { OtherPatterns = UiaPatterns.Scroll | UiaPatterns.LegacyIAccessible }),
            new FakeUiaNode("Edit", "Field") { ActionPatterns = ["Value"] });

        // Act
        var result = await CreateSut().GetUIElementsAsync(Handle, filter: new UIElementFilter(ActionableOnly: true));

        // Assert - a disabled button is still a mark (shown dimmed); focusable-only and container patterns are not
        Shape(result.Elements).Should().Be("Go,Off,Field");
        _tree.Reads.Should().NotContain(r => r.StartsWith("Interactable:") || r.StartsWith("Interaction:"),
            "the patterns came with the level's cached read");
    }

    [Fact]
    public async Task GetUIElements_InteractableOnlyAndActionableOnly_KeepOnlyTheElementsThatPassBoth()
    {
        // Arrange - capture_with_elements(interactableOnly: true) also turns actionableOnly on by default
        Window(
            new FakeUiaNode("Pane", "Focusable pane") { IsKeyboardFocusable = true },
            new FakeUiaNode("Button", "Go") { ActionPatterns = ["Invoke"] },
            new FakeUiaNode("Button", "Off") { IsEnabled = false, ActionPatterns = ["Invoke"] },
            new FakeUiaNode("Edit", "Field") { IsKeyboardFocusable = true, ActionPatterns = ["Value"] });

        // Act
        var result = await CreateSut().GetUIElementsAsync(Handle, filter: new UIElementFilter(InteractableOnly: true, ActionableOnly: true));

        // Assert - the focusable pane has no action pattern and the disabled button is not interactable
        Shape(result.Elements).Should().Be("Go,Field");
    }

    [Fact]
    public async Task GetUIElements_InteractableOnlyAndActionableOnly_ReadOneAtATime_KeepOnlyTheElementsThatPassBoth()
    {
        // Arrange - the cached fetch fails, so each element's focus and patterns are read on their own
        Window(
            new FakeUiaNode("Pane", "Focusable pane") { IsKeyboardFocusable = true },
            new FakeUiaNode("Button", "Go") { ActionPatterns = ["Invoke"] },
            new FakeUiaNode("Button", "Off") { IsEnabled = false, ActionPatterns = ["Invoke"] })
            .CachedChildrenException = new System.Runtime.InteropServices.COMException("cache request failed");

        // Act
        var result = await CreateSut().GetUIElementsAsync(Handle, filter: new UIElementFilter(InteractableOnly: true, ActionableOnly: true));

        // Assert
        Shape(result.Elements).Should().Be("Go");
    }

    [Fact]
    public async Task GetUIElements_ActionableOnly_ReadOneAtATime_ReadsThePatternsOfTheCandidatesOnly()
    {
        // Arrange - the cached fetch fails, so the children are read one at a time
        Window(
            new FakeUiaNode("Text", "Label"),
            new FakeUiaNode("Button", "Go") { ActionPatterns = ["Invoke"] },
            new FakeUiaNode("Edit", "Field") { IsKeyboardFocusable = true })
            .CachedChildrenException = new System.Runtime.InteropServices.COMException("cache request failed");

        // Act
        var result = await CreateSut().GetUIElementsAsync(Handle, filter: new UIElementFilter(ControlTypes: ["Button", "Edit"], ActionableOnly: true));

        // Assert - one fresh read per element that passed the control types; the label is never asked
        Shape(result.Elements).Should().Be("Go");
        _tree.Reads.Where(r => r.StartsWith("Element:")).Should().Equal("Element:Go", "Element:Field");
    }

    [Fact]
    public async Task GetUIElements_WithoutInteractableOnly_DoesNotReadFocusOrPatterns()
    {
        // Arrange
        BuildToolbarWindow();

        // Act
        await CreateSut().GetUIElementsAsync(Handle);

        // Assert
        _tree.Reads.Should().OnlyContain(r => r.StartsWith("Read:"));
    }

    #endregion

    #region visibleOnly prunes offscreen subtrees

    [Fact]
    public async Task GetUIElements_VisibleOnly_LeavesOutAnOffscreenElementWithEverythingInsideIt()
    {
        // Arrange
        Window(
            new FakeUiaNode("List", "Scrolled away") { IsOffscreen = true }.With(Node("ListItem", "Item")),
            Node("Button", "Visible"));

        // Act
        var result = await CreateSut().GetUIElementsAsync(Handle, filter: new UIElementFilter(VisibleOnly: true));

        // Assert
        Shape(result.Elements).Should().Be("Visible");
        _tree.ReadNodes.Should().NotContain("Item", "an offscreen element's subtree is not walked");
    }

    [Fact]
    public async Task GetUIElements_WithVisibleOnlyFalse_ReturnsOffscreenElements()
    {
        // Arrange
        Window(
            new FakeUiaNode("List", "Scrolled away") { IsOffscreen = true }.With(Node("ListItem", "Item")),
            Node("Button", "Visible"));

        // Act
        var result = await CreateSut().GetUIElementsAsync(Handle, filter: new UIElementFilter(VisibleOnly: false));

        // Assert
        Shape(result.Elements).Should().Be("Scrolled away(Item),Visible");
        result.Elements![0].IsOffscreen.Should().BeTrue();
    }

    #endregion

    #region Bounds

    [Fact]
    public async Task GetUIElements_ElementWithoutGeometry_HasNullBounds()
    {
        // Arrange
        Window(
            new FakeUiaNode("Text", "Nowhere") { Bounds = UiaRect.Empty },
            new FakeUiaNode("Text", "Broken") { Bounds = new UiaRect(double.NaN, 0, 10, 10) },
            new FakeUiaNode("Button", "Somewhere") { Bounds = new UiaRect(-1920.5, 10.25, 80, 30) });

        // Act
        var result = await CreateSut().GetUIElementsAsync(Handle);

        // Assert
        result.Elements!.Select(e => e.Bounds).Should().Equal(null, null, new BoundsDto(-1921, 10, 81, 31));
    }

    #endregion

    #region Failures while walking

    [Fact]
    public async Task GetUIElements_WhenAnElementVanishes_SkipsItAndKeepsItsSiblings()
    {
        // Arrange
        Window(
            Node("Button", "A"),
            new FakeUiaNode("Button", "Gone") { ReadException = FakeUiaTree.Vanished() }.With(Node("Text", "Inside")),
            Node("Button", "C"));

        // Act
        var result = await CreateSut().GetUIElementsAsync(Handle);

        // Assert
        result.Success.Should().BeTrue();
        Shape(result.Elements).Should().Be("A,C");
        result.SkippedElementCount.Should().Be(1);
        result.Truncated.Should().BeTrue();
        result.TruncationReason.Should().Be(UIElementTruncationReason.ElementsSkipped);
    }

    [Fact]
    public async Task GetUIElements_WhenAProviderFails_SkipsTheElementLikeAVanishedOne()
    {
        // Arrange
        Window(
            new FakeUiaNode("Button", "Bad") { ReadException = new System.Runtime.InteropServices.COMException("RPC failed") },
            Node("Button", "Good"));

        // Act
        var result = await CreateSut().GetUIElementsAsync(Handle);

        // Assert
        Shape(result.Elements).Should().Be("Good");
        result.SkippedElementCount.Should().Be(1);
    }

    [Fact]
    public async Task GetUIElements_WhenAnElementsChildrenCannotBeRead_ReturnsTheElementAndMarksTheTreeTruncated()
    {
        // Arrange
        Window(
            new FakeUiaNode("Pane", "Half") { ChildrenException = FakeUiaTree.Vanished() }.With(Node("Button", "Lost")),
            Node("Button", "Next"));

        // Act
        var result = await CreateSut().GetUIElementsAsync(Handle);

        // Assert
        Shape(result.Elements).Should().Be("Half,Next");
        result.SkippedElementCount.Should().Be(1);
        result.Truncated.Should().BeTrue();
        result.TruncationReason.Should().Be(UIElementTruncationReason.ElementsSkipped);
    }

    [Fact]
    public async Task GetUIElements_WhenInteractionPropertiesCannotBeRead_LeavesOutOnlyThatElement()
    {
        // Arrange - a container whose provider fails a pattern query
        Window(
            new FakeUiaNode("Pane", "Bad") { InteractionException = new System.Runtime.InteropServices.COMException("pattern query failed") }.With(
                new FakeUiaNode("Button", "Inside") { ActionPatterns = ["Invoke"] }),
            new FakeUiaNode("Button", "Good") { ActionPatterns = ["Invoke"] });

        // Act
        var result = await CreateSut().GetUIElementsAsync(Handle, filter: new UIElementFilter(InteractableOnly: true));

        // Assert - the filters decide about the element itself, so its interactable children stay
        Shape(result.Elements).Should().Be("Inside,Good");
        result.SkippedElementCount.Should().Be(1);
        result.Truncated.Should().BeTrue();
        result.TruncationReason.Should().Be(UIElementTruncationReason.ElementsSkipped);
    }

    [Fact]
    public async Task GetUIElements_WhenAnElementVanishesWhileItsInteractionIsRead_SkipsItsSubtree()
    {
        // Arrange - the element is gone, and the elements inside it with it
        Window(
            new FakeUiaNode("Pane", "Gone") { InteractionException = FakeUiaTree.Vanished() }.With(
                new FakeUiaNode("Button", "Inside") { ActionPatterns = ["Invoke"] }),
            new FakeUiaNode("Button", "Good") { ActionPatterns = ["Invoke"] });

        // Act
        var result = await CreateSut().GetUIElementsAsync(Handle, filter: new UIElementFilter(InteractableOnly: true));

        // Assert
        Shape(result.Elements).Should().Be("Good");
        result.SkippedElementCount.Should().Be(1);
        _tree.ReadNodes.Should().NotContain("Inside");
    }

    [Fact]
    public async Task GetUIElements_WhenTheWindowsChildrenVanish_ReturnsElementAccessFailed()
    {
        // Arrange
        Window().ChildrenException = FakeUiaTree.Vanished("window");

        // Act
        var result = await CreateSut().GetUIElementsAsync(Handle);

        // Assert - an empty tree would look like a window without controls
        result.Success.Should().BeFalse();
        result.ErrorCode.Should().Be(UIElementErrorCode.ElementAccessFailed);
        result.Elements.Should().BeNull();
    }

    [Fact]
    public async Task GetUIElements_WhenTheWindowsChildrenCannotBeRead_ReturnsOperationException()
    {
        // Arrange
        Window().ChildrenException = new System.Runtime.InteropServices.COMException("boom");

        // Act
        var result = await CreateSut().GetUIElementsAsync(Handle);

        // Assert
        result.Success.Should().BeFalse();
        result.ErrorCode.Should().Be(UIElementErrorCode.OperationException);
        result.Error.Should().Contain("boom");
    }

    [Fact]
    public async Task GetUIElements_WhenTheWindowsProviderTimesOut_ReturnsOperationTimedOut()
    {
        // Arrange
        Window().ChildrenException = new TimeoutException("provider timeout");

        // Act
        var result = await CreateSut().GetUIElementsAsync(Handle);

        // Assert
        result.Success.Should().BeFalse();
        result.ErrorCode.Should().Be(UIElementErrorCode.OperationTimedOut);
    }

    [Fact]
    public async Task GetUIElements_WhenTheWindowTitleCannotBeRead_StillReturnsTheTree()
    {
        // Arrange
        var window = new FakeUiaNode("Window", "Title") { ReadException = FakeUiaTree.Vanished() }.With(Node("Button", "A"));
        _tree.WithWindow(Handle, window);

        // Act
        var result = await CreateSut().GetUIElementsAsync(Handle);

        // Assert
        result.Success.Should().BeTrue();
        result.WindowTitle.Should().BeNull();
        Shape(result.Elements).Should().Be("A");
    }

    #endregion

    #region Timeouts return what was read

    [Fact]
    public async Task GetUIElements_WhenTheTimeoutPassesDuringTheWalk_ReturnsThePartialTree()
    {
        // Arrange - the timeout fires while "B" is read, in the call that fetches the window's children
        using var cts = new CancellationTokenSource();
        Window(
            Node("Button", "A"),
            new FakeUiaNode("Pane", "B") { OnRead = cts.Cancel }.With(Node("Button", "B1")),
            Node("Button", "C"));

        // Act
        var result = await CreateSut().GetUIElementsAsync(Handle, cancellationToken: cts.Token);

        // Assert - the level that call fetched is kept whole (it cost no further call), and nothing more is read
        result.Success.Should().BeTrue();
        result.ErrorCode.Should().Be(UIElementErrorCode.None);
        result.Truncated.Should().BeTrue();
        result.TruncationReason.Should().Be(UIElementTruncationReason.Timeout);
        Shape(result.Elements).Should().Be("A,B,C");
        result.TotalElementCount.Should().Be(3);
        _tree.ReadNodes.Should().NotContain("B1");
        _tree.CachedFetches.Should().Equal("Main window");
    }

    [Fact]
    public async Task GetUIElements_WhenTheTimeoutPassesWhileElementsAreReadOneAtATime_StopsBeforeTheNext()
    {
        // Arrange - the cached fetch fails, so the children are read one at a time; the timeout fires while "B" is read
        using var cts = new CancellationTokenSource();
        Window(
            Node("Button", "A"),
            new FakeUiaNode("Pane", "B") { OnRead = cts.Cancel }.With(Node("Button", "B1")),
            Node("Button", "C"))
            .CachedChildrenException = new System.Runtime.InteropServices.COMException("cache request failed");

        // Act
        var result = await CreateSut().GetUIElementsAsync(Handle, cancellationToken: cts.Token);

        // Assert
        result.TruncationReason.Should().Be(UIElementTruncationReason.Timeout);
        Shape(result.Elements).Should().Be("A,B");
        _tree.ReadNodes.Should().NotContain(["B1", "C"]);
    }

    [Fact]
    public async Task GetUIElements_WhenTheTimeoutPassesBeforeAnyElementIsRead_ReturnsOperationTimedOut()
    {
        // Arrange
        BuildToolbarWindow();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        // Act
        var result = await CreateSut().GetUIElementsAsync(Handle, cancellationToken: cts.Token);

        // Assert
        result.Success.Should().BeFalse();
        result.ErrorCode.Should().Be(UIElementErrorCode.OperationTimedOut);
        result.Error.Should().Contain("before any element was read");
        result.Elements.Should().BeNull();
    }

    [Fact]
    public async Task GetUIElements_WhenTheTimeoutPassesBeforeAnElementMatches_SaysHowManyWereRead()
    {
        // Arrange - two elements are read, neither is a Button, then the timeout passes before B's children are read
        using var cts = new CancellationTokenSource();
        Window(
            Node("Text", "A"),
            new FakeUiaNode("Pane", "B") { OnRead = cts.Cancel }.With(Node("Button", "B1")));

        // Act
        var result = await CreateSut().GetUIElementsAsync(
            Handle, filter: new UIElementFilter(ControlTypes: ["Button"]), cancellationToken: cts.Token);

        // Assert - elements were read, so "before any element was read" would be wrong
        result.Success.Should().BeFalse();
        result.ErrorCode.Should().Be(UIElementErrorCode.OperationTimedOut);
        result.Error.Should().Contain("before any element matching the filters was found (2 elements were read)");
    }

    [Fact]
    public async Task GetUIElements_RunsTheWalkOnTheWorkerWithTheCallersTokenAndAGracePeriod()
    {
        // Arrange
        BuildToolbarWindow();
        using var cts = new CancellationTokenSource();

        // Act
        await CreateSut().GetUIElementsAsync(Handle, cancellationToken: cts.Token);

        // Assert - the grace lets a walk that stops at the timeout hand back its partial tree
        _worker.Calls.Should().Be(1);
        _worker.LastToken.Should().Be(cts.Token);
        _worker.LastGrace.Should().BePositive();
    }

    [Fact]
    public async Task GetUIElements_CountsTheWalkAgainstTheProcessThatOwnsTheWindow()
    {
        // Arrange
        BuildToolbarWindow();
        var windows = new Mock<INativeWindowWrapper>();
        windows.Setup(w => w.GetWindowThreadProcessId(Handle)).Returns(4242u);

        // Act
        await new UIElementService(_tree, _worker, windows.Object).GetUIElementsAsync(Handle);

        // Assert - a hung application then blocks only walks of its own windows
        _worker.LastTargetProcessId.Should().Be(4242);
    }

    [Fact]
    public async Task GetUIElements_OfAUwpFrame_CountsTheWalkAgainstTheAppNotItsFrameHost()
    {
        // Arrange - ApplicationFrameHost owns the frame of every UWP app; the app runs in its CoreWindow's process
        BuildToolbarWindow();
        var coreWindow = new nint(0x6001);
        var windows = new Mock<INativeWindowWrapper>();
        windows.Setup(w => w.GetClassName(Handle)).Returns("ApplicationFrameWindow");
        windows.Setup(w => w.GetWindowThreadProcessId(Handle)).Returns(6000u);
        windows.Setup(w => w.GetClassName(coreWindow)).Returns("Windows.UI.Core.CoreWindow");
        windows.Setup(w => w.GetWindowThreadProcessId(coreWindow)).Returns(7000u);
        windows.Setup(w => w.EnumChildWindows(Handle, It.IsAny<Func<nint, bool>>()))
            .Callback<nint, Func<nint, bool>>((_, callback) => callback(coreWindow));

        // Act
        await new UIElementService(_tree, _worker, windows.Object).GetUIElementsAsync(Handle);

        // Assert - a hung UWP app then blocks only walks of itself, not of every other UWP app
        _worker.LastTargetProcessId.Should().Be(7000);
    }

    [Fact]
    public async Task GetUIElements_OfAMinimizedWindow_ReportsWindowStateMinimized()
    {
        // Arrange - a minimized window's elements are offscreen, so visibleOnly leaves them all out (roadmap M2.9)
        BuildToolbarWindow();
        var windows = new Mock<INativeWindowWrapper>();
        windows.Setup(w => w.IsWindow(Handle)).Returns(true);
        windows.Setup(w => w.RespondsWithin(Handle, It.IsAny<uint>())).Returns(true);
        windows.Setup(w => w.IsIconic(Handle)).Returns(true);

        // Act
        var result = await new UIElementService(_tree, _worker, windows.Object).GetUIElementsAsync(Handle);

        // Assert - sent by name, as the tool results serialize it
        result.WindowState.Should().Be(WindowState.Minimized);
        System.Text.Json.JsonSerializer.Serialize(result, ModelContextProtocol.McpJsonUtilities.DefaultOptions)
            .Should().Contain("\"windowState\":\"Minimized\"");
    }

    [Fact]
    public async Task GetUIElements_OfAControlInAMaximizedWindow_ReportsTheTopLevelWindowsState()
    {
        // Arrange
        BuildToolbarWindow();
        var root = new nint(0x7000);
        var windows = new Mock<INativeWindowWrapper>();
        windows.Setup(w => w.IsWindow(Handle)).Returns(true);
        windows.Setup(w => w.RespondsWithin(Handle, It.IsAny<uint>())).Returns(true);
        windows.Setup(w => w.GetAncestor(Handle, WindowAncestor.Root)).Returns(root);
        windows.Setup(w => w.IsZoomed(root)).Returns(true);

        // Act
        var result = await new UIElementService(_tree, _worker, windows.Object).GetUIElementsAsync(Handle);

        // Assert
        result.WindowState.Should().Be(WindowState.Maximized);
    }

    [Fact]
    public async Task GetUIElements_OfANormalWindow_ReportsWindowStateNormal_AndOfNoWindowNone()
    {
        // Arrange
        BuildToolbarWindow();
        var windows = new Mock<INativeWindowWrapper>();
        windows.Setup(w => w.IsWindow(Handle)).Returns(true);
        windows.Setup(w => w.RespondsWithin(Handle, It.IsAny<uint>())).Returns(true);

        // Act
        var normal = await new UIElementService(_tree, _worker, windows.Object).GetUIElementsAsync(Handle);
        var unknown = await CreateSut().GetUIElementsAsync(Handle);

        // Assert
        normal.WindowState.Should().Be(WindowState.Normal);
        unknown.WindowState.Should().BeNull("without window reads the state is not known");
    }

    [Fact]
    public async Task GetUIElements_WhenTheWindowsProcessCannotBeRead_RunsTheWalkForAnUnknownTarget()
    {
        // Arrange
        BuildToolbarWindow();
        var windows = new Mock<INativeWindowWrapper>();
        windows.Setup(w => w.GetWindowThreadProcessId(Handle)).Throws(new InvalidOperationException("gone"));

        // Act
        var result = await new UIElementService(_tree, _worker, windows.Object).GetUIElementsAsync(Handle);

        // Assert
        result.Success.Should().BeTrue();
        _worker.LastTargetProcessId.Should().Be(0);
    }

    [Fact]
    public async Task GetUIElements_WhileEarlierWalksOfTheApplicationAreStuck_ReturnsOperationTimedOut()
    {
        // Arrange - the worker refuses to start another read of the hung application
        BuildToolbarWindow();
        var worker = new Mock<IUiaWorker>();
        worker.Setup(w => w.RunAsync(It.IsAny<int>(), It.IsAny<Func<CancellationToken, UIElementResult>>(), It.IsAny<CancellationToken>(), It.IsAny<TimeSpan>()))
            .ThrowsAsync(new UiaReadRefusedException("UI Automation is not responding for the application (process 4242)."));

        // Act
        var result = await new UIElementService(_tree, worker.Object).GetUIElementsAsync(Handle);

        // Assert
        result.Success.Should().BeFalse();
        result.ErrorCode.Should().Be(UIElementErrorCode.OperationTimedOut);
        result.Error.Should().Contain("process 4242");
    }

    [Fact]
    public async Task GetUIElements_WhenUiAutomationDoesNotAnswerByTheDeadline_ReturnsOperationTimedOut()
    {
        // Arrange - the worker gave up on a read stuck in a hung application
        BuildToolbarWindow();
        var worker = new Mock<IUiaWorker>();
        worker.Setup(w => w.RunAsync(It.IsAny<int>(), It.IsAny<Func<CancellationToken, UIElementResult>>(), It.IsAny<CancellationToken>(), It.IsAny<TimeSpan>()))
            .ThrowsAsync(new TimeoutException("UI Automation did not answer in time."));
        var sut = new UIElementService(_tree, worker.Object);

        // Act
        var result = await sut.GetUIElementsAsync(Handle);

        // Assert
        result.Success.Should().BeFalse();
        result.ErrorCode.Should().Be(UIElementErrorCode.OperationTimedOut);
        result.Error.Should().Contain("did not answer");
    }

    [Fact]
    public async Task GetUIElements_WhenTheProviderHangsMidWalk_ReturnsWhatWasReadBeforeIt()
    {
        // Arrange - B2's provider stops answering while the walk fetches B's children; the worker abandons the walk
        using var worker = new AbandoningUiaWorker();
        Window(
            Node("Button", "A"),
            Node("Pane", "B").With(
                Node("Button", "B1"),
                new FakeUiaNode("Button", "B2") { OnRead = worker.Hang }),
            Node("Pane", "C").With(Node("Button", "C1")));

        // Act
        var result = await new UIElementService(_tree, worker).GetUIElementsAsync(Handle);

        // Assert - the elements read so far, as after a cooperative timeout: the window's level; the fetch of B's
        // children never returned, so B1 is not known
        result.Success.Should().BeTrue(result.Error);
        result.ErrorCode.Should().Be(UIElementErrorCode.None);
        result.Truncated.Should().BeTrue();
        result.TruncationReason.Should().Be(UIElementTruncationReason.Timeout);
        Shape(result.Elements).Should().Be("A,B,C");
        result.TotalElementCount.Should().Be(3);
        result.WindowTitle.Should().Be("Main window");
        result.DepthReached.Should().Be(1);
    }

    [Fact]
    public async Task GetUIElements_WhenTheAbandonedWalkGoesOnLater_TheReturnedTreeDoesNotChange()
    {
        // Arrange
        using var worker = new AbandoningUiaWorker();
        Window(
            Node("Pane", "B").With(
                Node("Button", "B1"),
                new FakeUiaNode("Button", "B2") { OnRead = worker.Hang },
                Node("Button", "B3")),
            Node("Pane", "C").With(Node("Button", "C1")));
        var result = await new UIElementService(_tree, worker).GetUIElementsAsync(Handle);

        // Act - the provider answers again, and the abandoned walk reads the rest
        worker.ReleaseAndWait();

        // Assert
        _tree.ReadNodes.Should().Contain("C1", "the walk went on after it was abandoned");
        Shape(result.Elements).Should().Be("B,C");
        CountAll(result.Elements).Should().Be(2);
    }

    [Fact]
    public async Task GetUIElements_WhenTheProviderHangsInAFilteredOutElement_ReturnsItsMatchingChildrenInItsPlace()
    {
        // Arrange - the Panes are not returned (controlTypes), so the Buttons read inside them take their place; the
        // walk hangs one level further down
        using var worker = new AbandoningUiaWorker();
        Window(
            Node("Button", "A"),
            Node("Pane", "P").With(
                Node("Button", "P1"),
                Node("Pane", "Q").With(new FakeUiaNode("Button", "Q1") { OnRead = worker.Hang })));

        // Act
        var result = await new UIElementService(_tree, worker).GetUIElementsAsync(
            Handle, filter: new UIElementFilter(ControlTypes: ["Button"]));

        // Assert
        result.Success.Should().BeTrue(result.Error);
        Shape(result.Elements).Should().Be("A,P1");
        result.TotalElementCount.Should().Be(2);
    }

    [Fact]
    public async Task GetUIElements_WhenTheProviderHangsBeforeAnyElementMatches_ReturnsOperationTimedOut()
    {
        // Arrange
        using var worker = new AbandoningUiaWorker();
        Window(new FakeUiaNode("Button", "A") { OnRead = worker.Hang });

        // Act
        var result = await new UIElementService(_tree, worker).GetUIElementsAsync(Handle);

        // Assert
        result.Success.Should().BeFalse();
        result.ErrorCode.Should().Be(UIElementErrorCode.OperationTimedOut);
        result.Error.Should().Contain("did not answer");
        result.Elements.Should().BeNull();
    }

    /// <summary>
    /// A worker that runs the read on its own thread and gives up on it, as <see cref="UiaWorker"/> does past the
    /// deadline, as soon as the read is stuck in a node's provider (<see cref="Hang"/>). The stuck read goes on
    /// when <see cref="ReleaseAndWait"/> is called, or when the test ends.
    /// </summary>
    private sealed class AbandoningUiaWorker : IUiaWorker, IDisposable
    {
        private readonly ManualResetEventSlim _stuck = new();
        private readonly ManualResetEventSlim _release = new();
        private Thread? _thread;

        /// <summary>Called from inside a provider call: blocks it, as a hung application does.</summary>
        public void Hang()
        {
            _stuck.Set();
            _release.Wait(TimeSpan.FromSeconds(30));
        }

        public void ReleaseAndWait()
        {
            _release.Set();
            _thread?.Join(TimeSpan.FromSeconds(30));
        }

        public Task<T> RunAsync<T>(int targetProcessId, Func<CancellationToken, T> read, CancellationToken cancellationToken, TimeSpan grace)
        {
            _thread = new Thread(() =>
            {
                try
                {
                    read(cancellationToken);
                }
                catch (Exception)
                {
                    // An abandoned read's outcome is never read
                }
            })
            { IsBackground = true };
            _thread.Start();

            _stuck.Wait(TimeSpan.FromSeconds(30)).Should().BeTrue("the read reaches the hanging provider");
            return Task.FromException<T>(new TimeoutException("UI Automation did not answer in time. The application may not be responding."));
        }

        public void Dispose()
        {
            ReleaseAndWait();
            _stuck.Dispose();
            _release.Dispose();
        }
    }

    #endregion

    #region Breadth-first, cached walks (roadmap M3.1)

    [Fact]
    public async Task GetUIElements_FetchesEachElementsChildrenWithTheirProperties_LevelByLevel()
    {
        // Arrange
        BuildToolbarWindow();

        // Act
        var result = await CreateSut().GetUIElementsAsync(Handle);

        // Assert - the whole of a level before the next: each element's children are listed (one cheap call), and a
        // level that has any is read in one more call with their properties; no element is read on its own
        Shape(result.Elements).Should().Be("Host(Tools(Save,Open)),Name,Footer(Box(OK))");
        _tree.ChildListings.Should().Equal("Main window", "Host", "Name", "Footer", "Tools", "Box", "Save", "Open", "OK");
        _tree.CachedFetches.Should().Equal("Main window", "Host", "Footer", "Tools", "Box");
        _tree.ReadNodes.Should().Equal("Main window", "Host", "Name", "Footer", "Tools", "Box", "Save", "Open", "OK");
    }

    [Fact]
    public async Task GetUIElements_WhenTheCachedFetchFails_ReadsThatElementsChildrenOneAtATime()
    {
        // Arrange - a provider rejects the cache request for the toolbar's children only
        var tools = Node("ToolBar", "Tools").With(Node("Button", "Save"), Node("Button", "Open"));
        tools.CachedChildrenException = new System.Runtime.InteropServices.COMException("cache request failed");
        Window(Node("Pane", "Host").With(tools));

        // Act
        var result = await CreateSut().GetUIElementsAsync(Handle);

        // Assert - nothing is lost, and nothing is counted as skipped
        Shape(result.Elements).Should().Be("Host(Tools(Save,Open))");
        result.SkippedElementCount.Should().Be(0);
        result.Truncated.Should().BeFalse();
    }

    [Fact]
    public async Task GetUIElements_WhenTheCachedFetchTimesOut_SkipsThoseChildrenWithoutWaitingTwice()
    {
        // Arrange - the provider does not answer within the transaction timeout: reading the same children one at a time
        // would wait again
        var slow = Node("Pane", "Slow").With(Node("Button", "Inside"));
        slow.CachedChildrenException = new TimeoutException("UIA_E_TIMEOUT");
        Window(slow, Node("Button", "Next"));

        // Act
        var result = await CreateSut().GetUIElementsAsync(Handle);

        // Assert
        Shape(result.Elements).Should().Be("Slow,Next");
        result.SkippedElementCount.Should().Be(1);
        result.TruncationReason.Should().Be(UIElementTruncationReason.ElementsSkipped);
        _tree.ReadNodes.Should().NotContain("Inside");
    }

    [Fact]
    public async Task GetUIElements_MaxElements_StopsTheWalk_KeepingTheLevelsNearestTheWindow()
    {
        // Arrange
        BuildToolbarWindow();

        // Act
        var result = await CreateSut().GetUIElementsAsync(Handle, filter: new UIElementFilter(MaxElements: 4));

        // Assert - the window's three children, then the first element of level 2
        result.Success.Should().BeTrue();
        Shape(result.Elements).Should().Be("Host(Tools),Name,Footer");
        result.TotalElementCount.Should().Be(4);
        result.Truncated.Should().BeTrue();
        result.TruncationReason.Should().Be(UIElementTruncationReason.MaxElements);
        _tree.ReadNodes.Should().NotContain(["Save", "Open", "OK"]);
    }

    [Fact]
    public async Task GetUIElements_MaxElements_CountsOnlyReturnedElements()
    {
        // Arrange
        BuildToolbarWindow();

        // Act - three Buttons, at most two
        var result = await CreateSut().GetUIElementsAsync(
            Handle, filter: new UIElementFilter(ControlTypes: ["Button"], MaxElements: 2));

        // Assert
        Shape(result.Elements).Should().Be("Save,Open");
        result.TruncationReason.Should().Be(UIElementTruncationReason.MaxElements);
    }

    [Fact]
    public async Task GetUIElements_OfAVeryWideLevel_ReadsOnlyUpToMaxElements()
    {
        // Arrange - 5000 children on one level (a list without virtualization): reading them all with their properties
        // in one call could outlast the deadline and lose the level
        Window(Enumerable.Range(1, 5000).Select(i => Node("ListItem", $"I{i}")).ToArray());

        // Act
        var result = await CreateSut().GetUIElementsAsync(Handle, filter: new UIElementFilter(MaxElements: 10));

        // Assert - listed in one cheap call, then read one at a time until maxElements
        result.Success.Should().BeTrue();
        result.TotalElementCount.Should().Be(10);
        result.Truncated.Should().BeTrue();
        result.TruncationReason.Should().Be(UIElementTruncationReason.MaxElements);
        _tree.CachedFetches.Should().BeEmpty();
        _tree.ReadNodes.Where(n => n != "Main window").Should().HaveCount(10);
    }

    [Fact]
    public async Task GetUIElements_OfALevelJustWithinTheLimit_ReadsItInOneCall()
    {
        // Arrange
        Window(Enumerable.Range(1, UIElementService.CachedLevelLimit).Select(i => Node("ListItem", $"I{i}")).ToArray());

        // Act
        var result = await CreateSut().GetUIElementsAsync(Handle);

        // Assert
        result.TotalElementCount.Should().Be(UIElementService.CachedLevelLimit);
        _tree.CachedFetches.Should().Equal("Main window");
    }

    [Fact]
    public async Task GetUIElements_WhenTheTimeoutPassesInAVeryWideLevel_ReturnsWhatWasReadOfIt()
    {
        // Arrange - the timeout passes while the 50th of 1000 children is read
        using var cts = new CancellationTokenSource();
        var items = Enumerable.Range(1, 1000)
            .Select(i => new FakeUiaNode("ListItem", $"I{i}") { OnRead = i == 50 ? cts.Cancel : null })
            .ToArray();
        Window(items);

        // Act
        var result = await CreateSut().GetUIElementsAsync(Handle, cancellationToken: cts.Token);

        // Assert - a partial tree, not OperationTimedOut with nothing
        result.Success.Should().BeTrue();
        result.TruncationReason.Should().Be(UIElementTruncationReason.Timeout);
        result.TotalElementCount.Should().Be(50);
        _tree.ReadNodes.Should().NotContain("I51");
    }

    [Fact]
    public async Task GetUIElements_MaxElementsReachedByTheLastElement_AtTheDefaultDepth_IsNoTruncation()
    {
        // Arrange - exactly three elements; their children are still to be read when the limit is reached
        Window(Node("Button", "A"), Node("Pane", "B").With(Node("Button", "B1")));

        // Act
        var result = await CreateSut().GetUIElementsAsync(Handle, filter: new UIElementFilter(MaxElements: 3));

        // Assert - the elements still waiting were asked for a child: none has one, so nothing was left unread
        Shape(result.Elements).Should().Be("A,B(B1)");
        result.Truncated.Should().BeFalse();
        result.TruncationReason.Should().BeNull();
        _tree.ChildProbes.Should().Contain("B1");
        _tree.ChildListings.Should().NotContain("B1");
    }

    [Fact]
    public async Task GetUIElements_MaxElementsReached_AsksAWideElementForOneChild_WithoutListingItsLevel()
    {
        // Arrange - the limit falls on the last element of level 1; the first one holds ten thousand rows
        var rows = Enumerable.Range(1, 10_000).Select(i => Node("ListItem", $"Row {i}")).ToArray();
        Window(Node("List", "A").With(rows), Node("Button", "B"));

        // Act
        var result = await CreateSut().GetUIElementsAsync(Handle, filter: new UIElementFilter(MaxElements: 2));

        // Assert - one element at most was read to tell, not the whole level
        Shape(result.Elements).Should().Be("A,B");
        result.TruncationReason.Should().Be(UIElementTruncationReason.MaxElements);
        _tree.ChildProbes.Should().Equal("A");
        _tree.ChildListings.Should().NotContain("A");
    }

    [Fact]
    public async Task GetUIElements_MaxElementsReached_AsksOnlySoManyWaitingElements_ThenAssumesSomethingWasLeft()
    {
        // Arrange - the limit falls on the last of many elements without children
        Window(Enumerable.Range(1, UIElementService.MaxChildProbes + 10).Select(i => Node("Button", $"B{i}")).ToArray());

        // Act
        var result = await CreateSut().GetUIElementsAsync(Handle,
            filter: new UIElementFilter(MaxElements: UIElementService.MaxChildProbes + 10));

        // Assert
        result.TruncationReason.Should().Be(UIElementTruncationReason.MaxElements);
        _tree.ChildProbes.Should().HaveCount(UIElementService.MaxChildProbes);
    }

    [Fact]
    public async Task GetUIElements_MaxElementsReached_WhenAWaitingElementCannotBeListed_AssumesTruncation()
    {
        // Arrange
        var b1 = Node("Button", "B1");
        b1.ChildrenException = new System.Runtime.InteropServices.COMException("provider failed");
        Window(Node("Button", "A"), Node("Pane", "B").With(b1));

        // Act
        var result = await CreateSut().GetUIElementsAsync(Handle, filter: new UIElementFilter(MaxElements: 3));

        // Assert
        result.TruncationReason.Should().Be(UIElementTruncationReason.MaxElements);
        result.SkippedElementCount.Should().Be(0, "the check reads nothing that is returned");
    }

    [Fact]
    public async Task GetUIElements_MaxElementsReachedByTheLastElement_IsNoTruncation()
    {
        // Arrange - exactly two elements, nothing below them
        Window(Node("Button", "A"), Node("Button", "B"));

        // Act
        var result = await CreateSut().GetUIElementsAsync(Handle, maxDepth: 1, filter: new UIElementFilter(MaxElements: 2));

        // Assert
        Shape(result.Elements).Should().Be("A,B");
        result.Truncated.Should().BeFalse();
        result.TruncationReason.Should().BeNull();
    }

    [Fact]
    public async Task GetUIElements_WithoutMaxElements_HasNoLimit()
    {
        // Arrange
        Window(Enumerable.Range(1, 400).Select(i => Node("Button", $"B{i}")).ToArray());

        // Act
        var result = await CreateSut().GetUIElementsAsync(Handle);

        // Assert - capture_with_elements walks without a limit
        result.TotalElementCount.Should().Be(400);
        result.Truncated.Should().BeFalse();
    }

    #endregion

    #region Hung windows (roadmap M3.1)

    private Mock<INativeWindowWrapper> RespondingWindow()
    {
        var windows = new Mock<INativeWindowWrapper>();
        windows.Setup(w => w.IsWindow(Handle)).Returns(true);
        windows.Setup(w => w.RespondsWithin(Handle, It.IsAny<uint>())).Returns(true);
        return windows;
    }

    [Fact]
    public async Task GetUIElements_OfAWindowWindowsReportsHung_ReturnsNotResponding_WithoutReadingIt()
    {
        // Arrange
        BuildToolbarWindow();
        var windows = RespondingWindow();
        windows.Setup(w => w.IsHungAppWindow(Handle)).Returns(true);

        // Act
        var result = await new UIElementService(_tree, _worker, windows.Object).GetUIElementsAsync(Handle);

        // Assert - every UI Automation call would block until the application recovers
        result.Success.Should().BeFalse();
        result.ErrorCode.Should().Be(UIElementErrorCode.NotResponding);
        result.Error.Should().Contain("hung");
        result.Elements.Should().BeNull();
        _tree.Reads.Should().BeEmpty();
        windows.Verify(w => w.RespondsWithin(It.IsAny<nint>(), It.IsAny<uint>()), Times.Never);
    }

    [Fact]
    public async Task GetUIElements_OfAUwpFrameWhoseAppIsHung_ReturnsNotResponding_WithoutReadingIt()
    {
        // Arrange - the frame host answers for the frame; the app behind its CoreWindow is hung
        BuildToolbarWindow();
        var coreWindow = new nint(0x6001);
        var windows = RespondingWindow();
        windows.Setup(w => w.GetClassName(Handle)).Returns("ApplicationFrameWindow");
        windows.Setup(w => w.GetWindowThreadProcessId(Handle)).Returns(6000u);
        windows.Setup(w => w.GetClassName(coreWindow)).Returns("Windows.UI.Core.CoreWindow");
        windows.Setup(w => w.GetWindowThreadProcessId(coreWindow)).Returns(7000u);
        windows.Setup(w => w.EnumChildWindows(Handle, It.IsAny<Func<nint, bool>>()))
            .Callback<nint, Func<nint, bool>>((_, callback) => callback(coreWindow));
        windows.Setup(w => w.IsHungAppWindow(coreWindow)).Returns(true);

        // Act
        var result = await new UIElementService(_tree, _worker, windows.Object).GetUIElementsAsync(Handle);

        // Assert
        result.ErrorCode.Should().Be(UIElementErrorCode.NotResponding);
        _tree.Reads.Should().BeEmpty();
        _tree.ChildListings.Should().BeEmpty();
    }

    [Fact]
    public async Task GetUIElements_OfAWindowThatDoesNotAnswerAMessageInTime_ReturnsNotResponding()
    {
        // Arrange - not hung long enough for IsHungAppWindow, but busy
        BuildToolbarWindow();
        var windows = RespondingWindow();
        windows.Setup(w => w.RespondsWithin(Handle, It.IsAny<uint>())).Returns(false);

        // Act
        var result = await new UIElementService(_tree, _worker, windows.Object).GetUIElementsAsync(Handle);

        // Assert
        result.ErrorCode.Should().Be(UIElementErrorCode.NotResponding);
        result.Error.Should().Contain("500 ms");
        _tree.Reads.Should().BeEmpty();
        windows.Verify(w => w.RespondsWithin(Handle, UIElementService.RespondTimeoutMs), Times.Once);
    }

    [Fact]
    public async Task GetUIElements_WhenTheMessageCannotBeSent_ReadsTheWindowAnyway()
    {
        // Arrange - SendMessageTimeout failed for another reason than a timeout (User Interface Privilege Isolation
        // refuses it for a window of a more privileged process): nothing is known about the thread, so it is not
        // reported hung; the walk's own deadline still bounds the read
        BuildToolbarWindow();
        var windows = RespondingWindow();
        windows.Setup(w => w.RespondsWithin(Handle, It.IsAny<uint>())).Returns((bool?)null);

        // Act
        var result = await new UIElementService(_tree, _worker, windows.Object).GetUIElementsAsync(Handle);

        // Assert
        result.Success.Should().BeTrue();
        result.TotalElementCount.Should().Be(8);
    }

    [Fact]
    public async Task GetUIElements_OfAWindowThatClosedDuringTheCheck_ReportsWindowNotFound()
    {
        // Arrange - SendMessageTimeout fails because the window is gone, not because it is busy
        var windows = new Mock<INativeWindowWrapper>();
        windows.SetupSequence(w => w.IsWindow(Handle)).Returns(true).Returns(false);

        // Act
        var result = await new UIElementService(_tree, _worker, windows.Object).GetUIElementsAsync(Handle);

        // Assert
        result.ErrorCode.Should().Be(UIElementErrorCode.WindowNotFound);
    }

    [Fact]
    public async Task GetUIElements_OfAHandleThatIsNoWindow_SkipsTheCheck_AndReportsWindowNotFound()
    {
        // Arrange
        var windows = new Mock<INativeWindowWrapper>();

        // Act
        var result = await new UIElementService(_tree, _worker, windows.Object).GetUIElementsAsync(Handle);

        // Assert
        result.ErrorCode.Should().Be(UIElementErrorCode.WindowNotFound);
        windows.Verify(w => w.RespondsWithin(It.IsAny<nint>(), It.IsAny<uint>()), Times.Never);
    }

    [Fact]
    public async Task GetUIElements_OfAWindowThatResponds_ReadsIt()
    {
        // Arrange
        BuildToolbarWindow();

        // Act
        var result = await new UIElementService(_tree, _worker, RespondingWindow().Object).GetUIElementsAsync(Handle);

        // Assert
        result.Success.Should().BeTrue();
        result.TotalElementCount.Should().Be(8);
    }

    [Fact]
    public async Task GetUIElements_WhenTheCheckItselfFails_ReadsTheWindowAnyway()
    {
        // Arrange - the walk's own deadline still bounds the read
        BuildToolbarWindow();
        var windows = RespondingWindow();
        windows.Setup(w => w.IsHungAppWindow(Handle)).Throws(new InvalidOperationException("boom"));

        // Act
        var result = await new UIElementService(_tree, _worker, windows.Object).GetUIElementsAsync(Handle);

        // Assert
        result.Success.Should().BeTrue();
    }

    #endregion

    #region Progress

    [Fact]
    public async Task GetUIElementsAsync_ReportsProgress_EveryHundredElementsRead()
    {
        // Arrange - 250 buttons
        Window(Enumerable.Range(1, 250).Select(i => Node("Button", $"B{i}")).ToArray());
        var progress = new RecordingProgress<OperationProgress>();

        // Act
        var result = await CreateSut().GetUIElementsAsync(Handle, progress: progress);

        // Assert - the size of the tree is not known in advance, so there is no total
        result.TotalElementCount.Should().Be(250);
        progress.Reports.Select(p => p.Completed).Should().Equal(UIElementService.ProgressInterval, 2 * UIElementService.ProgressInterval);
        progress.Reports.Should().OnlyContain(p => p.Total == null);
        progress.Reports[0].Message.Should().StartWith("Read 100 UI elements so far").And.EndWith("down to level 1 of 5");
    }

    [Fact]
    public async Task GetUIElementsAsync_OnceTheWalkIsAskedToStop_ReportsNoMoreProgress()
    {
        // Arrange - the walk is asked to stop while the second pane's children are read in one call, after 152 elements
        // were read: the caller may already have its result, and a progress notification must not follow the result of
        // the call, so the 200th element read (in the level that call returns) reports nothing
        using var cts = new CancellationTokenSource();
        Window(
            Node("Pane", "P1").With(Enumerable.Range(1, 150).Select(i => Node("Button", $"A{i}")).ToArray()),
            Node("Pane", "P2").With(
                Enumerable.Range(1, 100).Select(i => new FakeUiaNode("Button", $"B{i}") { OnRead = i == 1 ? cts.Cancel : null }).ToArray()));
        var progress = new RecordingProgress<OperationProgress>();

        // Act
        var result = await CreateSut().GetUIElementsAsync(Handle, progress: progress, cancellationToken: cts.Token);

        // Assert
        result.Truncated.Should().BeTrue();
        progress.Reports.Select(p => p.Completed).Should().Equal(UIElementService.ProgressInterval);
    }

    [Fact]
    public async Task GetUIElementsAsync_WithoutProgress_WalksTheSameTree()
    {
        // Arrange
        Window(Enumerable.Range(1, 150).Select(i => Node("Button", $"B{i}")).ToArray());

        // Act
        var result = await CreateSut().GetUIElementsAsync(Handle, progress: null);

        // Assert
        result.TotalElementCount.Should().Be(150);
    }

    #endregion

    #region Result

    [Fact]
    public async Task GetUIElements_CompleteWalk_ReportsTheWindowAndNoTruncation()
    {
        // Arrange
        BuildToolbarWindow();

        // Act
        var result = await CreateSut().GetUIElementsAsync(Handle, maxDepth: 5);

        // Assert
        result.Success.Should().BeTrue();
        result.WindowHandle.Should().Be(Handle);
        result.WindowTitle.Should().Be("Main window");
        result.ErrorCode.Should().Be(UIElementErrorCode.None);
        result.Error.Should().BeNull();
        result.Truncated.Should().BeFalse();
        result.TruncationReason.Should().BeNull();
        result.SkippedElementCount.Should().Be(0);
        result.ElapsedMilliseconds.Should().BeGreaterOrEqualTo(0);
    }

    [Fact]
    public async Task GetUIElements_CopiesTheElementProperties()
    {
        // Arrange
        Window(new FakeUiaNode("Button", "Save")
        {
            AutomationId = "SaveBtn",
            Bounds = new UiaRect(10, 20, 80, 24),
            IsEnabled = false,
        });

        // Act
        var element = (await CreateSut().GetUIElementsAsync(Handle)).Elements!.Single();

        // Assert
        element.Should().Be(new UIElementInfo("Save", "Button", "SaveBtn", new BoundsDto(10, 20, 80, 24), IsEnabled: false, IsOffscreen: false));
    }

    [Fact]
    public async Task GetUIElements_EmptyNameAndAutomationId_AreNull()
    {
        // Arrange
        Window(new FakeUiaNode("Pane", "") { AutomationId = "" });

        // Act
        var element = (await CreateSut().GetUIElementsAsync(Handle)).Elements!.Single();

        // Assert
        element.Name.Should().BeNull();
        element.AutomationId.Should().BeNull();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void IsAvailable_AsksTheTree(bool available)
    {
        // Arrange
        _tree.Available = available;

        // Act & Assert
        CreateSut().IsAvailable().Should().Be(available);
    }

    #endregion

    #region Target policy

    [Fact]
    public async Task GetUIElements_OfAWindowOfADeniedApp_IsRefused_BeforeAnyWarmupOrRead()
    {
        // Arrange - a password manager's window, named by its handle
        BuildToolbarWindow();
        var windows = RespondingWindow();
        windows.Setup(w => w.GetWindowThreadProcessId(Handle)).Returns(3001u);
        windows.Setup(w => w.GetProcessName(3001u)).Returns("KeePass");
        var warmup = new Mock<IAccessibilityWarmup>();
        var sut = new UIElementService(_tree, _worker, windows.Object, warmup.Object,
            policy: new TargetPolicy(TargetPolicySettings.Default));

        // Act
        var result = await sut.GetUIElementsAsync(Handle);

        // Assert
        result.Success.Should().BeFalse();
        result.ErrorCode.Should().Be(UIElementErrorCode.TargetDeniedByPolicy);
        result.Error.Should().Contain("KeePass").And.Contain("Nothing was read");
        result.Elements.Should().BeNull();
        _tree.Reads.Should().BeEmpty();
        _worker.Calls.Should().Be(0);
        warmup.VerifyNoOtherCalls();
        windows.Verify(w => w.RespondsWithin(It.IsAny<nint>(), It.IsAny<uint>()), Times.Never);
    }

    [Fact]
    public async Task GetUIElements_OfAWindowOfAnAllowedApp_IsRead()
    {
        // Arrange
        BuildToolbarWindow();
        var windows = RespondingWindow();
        windows.Setup(w => w.GetWindowThreadProcessId(Handle)).Returns(2001u);
        windows.Setup(w => w.GetProcessName(2001u)).Returns("notepad");

        // Act
        var result = await new UIElementService(_tree, _worker, windows.Object, policy: new TargetPolicy(TargetPolicySettings.Default))
            .GetUIElementsAsync(Handle);

        // Assert
        result.Success.Should().BeTrue();
        result.TotalElementCount.Should().Be(8);
    }

    #endregion

    #region Web content warm-up and frameworks (roadmap M3.3)

    private static readonly AccessibilityWarmupInfo NotReady =
        new(AccessibilityWarmupStatus.NotReady, WebContentHost.Chromium, 500, 3, 0, "Read again in a moment.");

    [Fact]
    public async Task GetUIElements_WarmsUpTheWindowBeforeTheWalk_WithTheFiltersBudget_AndReportsIt()
    {
        // Arrange
        BuildToolbarWindow();
        var warmup = new Mock<IAccessibilityWarmup>();
        var order = new List<string>();
        warmup.Setup(w => w.WarmUp(Handle, It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
            .Callback(() => order.Add($"warm-up after {_tree.ChildListings.Count} listings"))
            .Returns(NotReady);
        var sut = new UIElementService(_tree, _worker, warmup: warmup.Object);

        // Act
        var result = await sut.GetUIElementsAsync(Handle, filter: new UIElementFilter(AccessibilityWarmupBudget: TimeSpan.FromMilliseconds(700)));

        // Assert
        result.Success.Should().BeTrue();
        result.AccessibilityWarmup.Should().Be(NotReady);
        order.Should().Equal("warm-up after 0 listings");
        warmup.Verify(w => w.WarmUp(Handle, TimeSpan.FromMilliseconds(700), _worker.LastToken), Times.Once);
    }

    [Fact]
    public async Task GetUIElements_WithoutABudget_GivesTheWarmupItsLongestWait()
    {
        // Arrange
        BuildToolbarWindow();
        var warmup = new Mock<IAccessibilityWarmup>();
        var sut = new UIElementService(_tree, _worker, warmup: warmup.Object);

        // Act
        var result = await sut.GetUIElementsAsync(Handle);

        // Assert
        result.AccessibilityWarmup.Should().BeNull("nothing was warmed up");
        warmup.Verify(w => w.WarmUp(Handle, AccessibilityWarmup.MaxWait, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task GetUIElements_OfAWindowThatIsNotFound_DoesNotWarmItUp()
    {
        // Arrange
        var warmup = new Mock<IAccessibilityWarmup>();
        var sut = new UIElementService(_tree, _worker, warmup: warmup.Object);

        // Act
        var result = await sut.GetUIElementsAsync(Handle);

        // Assert
        result.ErrorCode.Should().Be(UIElementErrorCode.WindowNotFound);
        warmup.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task GetUIElements_WhenTheTimeoutPassesWithNothingRead_StillReportsTheWarmup()
    {
        // Arrange - the warm-up used the time up
        BuildToolbarWindow();
        using var deadline = new CancellationTokenSource();
        var warmup = new Mock<IAccessibilityWarmup>();
        warmup.Setup(w => w.WarmUp(Handle, It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
            .Callback(deadline.Cancel)
            .Returns(NotReady);
        var sut = new UIElementService(_tree, _worker, warmup: warmup.Object);

        // Act
        var result = await sut.GetUIElementsAsync(Handle, cancellationToken: deadline.Token);

        // Assert
        result.ErrorCode.Should().Be(UIElementErrorCode.OperationTimedOut);
        result.AccessibilityWarmup.Should().Be(NotReady);
    }

    [Fact]
    public async Task GetUIElements_NamesAnElementsFrameworkOnlyWhereItChanges()
    {
        // Arrange - a WPF window hosting a WebView2 pane (Win32 host window, Chrome content)
        var window = new FakeUiaNode("Window", "Mail") { FrameworkId = "WPF" }.With(
            new FakeUiaNode("Button", "New") { FrameworkId = "WPF" },
            new FakeUiaNode("Pane", "WebView host") { FrameworkId = "Win32" }.With(
                new FakeUiaNode("Document", "Inbox") { FrameworkId = "Chrome" }.With(
                    new FakeUiaNode("Hyperlink", "Message") { FrameworkId = "Chrome" })),
            new FakeUiaNode("Text", "Status"));
        _tree.WithWindow(Handle, window);

        // Act
        var result = await CreateSut().GetUIElementsAsync(Handle);

        // Assert
        result.FrameworkId.Should().Be("WPF");
        var elements = result.Elements!;
        elements[0].FrameworkId.Should().BeNull("the same framework as the window");
        elements[1].FrameworkId.Should().Be("Win32");
        var document = elements[1].Children!.Single();
        document.FrameworkId.Should().Be("Chrome");
        document.Children!.Single().FrameworkId.Should().BeNull("the same framework as the document it is inside");
        elements[2].FrameworkId.Should().BeNull("a provider that reports no framework names none");
    }

    [Fact]
    public async Task GetUIElements_TheTitleBarOfAWpfWindow_NamesItsWin32Framework()
    {
        // Arrange - a WPF window as UI Automation shows it: the non-client title bar (with its buttons) comes from the
        // window's Win32 proxy, not from WPF, so not every element of a WPF window inherits the window's framework
        var window = new FakeUiaNode("Window", "Fixture") { FrameworkId = "WPF" }.With(
            new FakeUiaNode("TitleBar", "Fixture") { AutomationId = "TitleBar", FrameworkId = "Win32" }.With(
                new FakeUiaNode("Button", "Close") { AutomationId = "Close", FrameworkId = "Win32" }),
            new FakeUiaNode("Button", "Open") { AutomationId = "openButton", FrameworkId = "WPF" });
        _tree.WithWindow(Handle, window);

        // Act
        var result = await CreateSut().GetUIElementsAsync(Handle);

        // Assert
        result.FrameworkId.Should().Be("WPF");
        var titleBar = result.Elements![0];
        titleBar.FrameworkId.Should().Be("Win32");
        titleBar.Children!.Single().FrameworkId.Should().BeNull("the same framework as the title bar it is inside");
        result.Elements[1].FrameworkId.Should().BeNull("the same framework as the window");
    }

    [Fact]
    public async Task GetUIElements_AnElementLeftOutByAFilter_PassesItsFrameworkOnToNoOne()
    {
        // Arrange - the Chrome document is left out by controlTypes; its link is compared with the window's framework
        var window = new FakeUiaNode("Window", "Browser") { FrameworkId = "Win32" }.With(
            new FakeUiaNode("Document", "Page") { FrameworkId = "Chrome" }.With(
                new FakeUiaNode("Hyperlink", "Home") { FrameworkId = "Chrome" }));
        _tree.WithWindow(Handle, window);

        // Act
        var result = await CreateSut().GetUIElementsAsync(Handle, filter: new UIElementFilter(ControlTypes: ["Hyperlink"]));

        // Assert
        result.Elements!.Single().FrameworkId.Should().Be("Chrome");
    }

    #endregion

    #region The pages of render widgets (roadmap M3.F35)

    private static readonly nint WebViewHost = 0x2002;
    private static readonly nint RenderWidget = 0x2003;
    private const uint AppProcess = 7000;
    private const uint WebViewProcess = 7100;

    /// <summary>A window as Win32 reads show it: its class, parent, process and visibility.</summary>
    private sealed record Win32Window(string ClassName, nint Parent, uint ProcessId, bool Visible = true);

    /// <summary>The Win32 windows of the tests: the walked window and the windows inside it.</summary>
    private readonly Dictionary<nint, Win32Window> _win32 = new() { [Handle] = new("HwndWrapper[Fixture;;1]", 0, AppProcess) };

    /// <summary>The window reads of the warm-up and the walk, over <see cref="_win32"/>.</summary>
    private Mock<INativeWindowWrapper> Win32Windows()
    {
        var windows = new Mock<INativeWindowWrapper>();
        windows.Setup(w => w.IsWindow(It.IsAny<nint>())).Returns((nint h) => _win32.ContainsKey(h));
        windows.Setup(w => w.RespondsWithin(Handle, It.IsAny<uint>())).Returns(true);
        windows.Setup(w => w.GetClassName(It.IsAny<nint>())).Returns((nint h) => _win32.TryGetValue(h, out var w) ? w.ClassName : string.Empty);
        windows.Setup(w => w.IsWindowVisible(It.IsAny<nint>())).Returns((nint h) => _win32.TryGetValue(h, out var w) && w.Visible);
        windows.Setup(w => w.GetWindowThreadProcessId(It.IsAny<nint>()))
            .Returns((nint h) => _win32.TryGetValue(h, out var w) ? w.ProcessId : 0u);
        windows.Setup(w => w.GetProcessName(WebViewProcess)).Returns("msedgewebview2");
        windows.Setup(w => w.GetAncestor(It.IsAny<nint>(), WindowAncestor.Parent))
            .Returns((nint h, WindowAncestor _) => _win32.TryGetValue(h, out var w) ? w.Parent : nint.Zero);
        windows.Setup(w => w.EnumChildWindows(It.IsAny<nint>(), It.IsAny<Func<nint, bool>>()))
            .Callback((nint parent, Func<nint, bool> callback) =>
            {
                foreach (var child in Win32Descendants(parent))
                {
                    if (!callback(child))
                    {
                        return;
                    }
                }
            });
        return windows;
    }

    private IEnumerable<nint> Win32Descendants(nint parent) =>
        _win32.Where(w => w.Value.Parent == parent && w.Key != parent).ToList()
            .SelectMany(w => Win32Descendants(w.Key).Prepend(w.Key));

    /// <summary>The walk with the real warm-up and the element registry, over <see cref="_win32"/>.</summary>
    private (UIElementService Sut, ElementRegistry Registry) WebSut()
    {
        var windows = Win32Windows();
        var warmup = new AccessibilityWarmup(_tree, windows.Object, new Mock<INativeProcessWrapper>().Object, wait: (_, _) => { });
        var registry = new ElementRegistry(_tree, _worker, windows.Object, ElementRegistry.DefaultCapacity, 1, TimeProvider.System);
        return (new UIElementService(_tree, _worker, windows.Object, warmup, registry), registry);
    }

    /// <summary>
    /// A WPF window hosting a WebView2 as runtime 155 shows it on a real desktop (roadmap M3.F33). Win32: the render widget of
    /// msedgewebview2.exe in a Chrome_WidgetWin_1 window inside the WPF window. UI Automation, from the window:
    ///   Pane "HwndHost" > Pane "WAM fixture web content" (the Chrome_WidgetWin_1 window) > Group "BrowserRootView" >
    ///     Pane "NonClientView" > Pane "EmbeddedBrowserFrameView" > Pane "BrowserView" > ... (Chromium's views, the page far
    ///     below)
    ///   Button "Native"
    /// and from the render widget: Pane "Chrome Legacy Window" > Text "Heading", Button "Web button".
    /// </summary>
    private (FakeUiaNode Host, FakeUiaNode Page) WpfWithWebView2(bool visible = true)
    {
        _win32[WebViewHost] = new Win32Window("Chrome_WidgetWin_1", Handle, WebViewProcess);
        _win32[RenderWidget] = new Win32Window("Chrome_RenderWidgetHostHWND", WebViewHost, WebViewProcess, visible);
        var host = new FakeUiaNode("Pane", "WAM fixture web content") { FrameworkId = "Win32", NativeWindowHandle = WebViewHost }.With(
            new FakeUiaNode("Group", "BrowserRootView") { FrameworkId = "Chrome" }.With(
                new FakeUiaNode("Pane", "NonClientView") { FrameworkId = "Chrome" }.With(
                    new FakeUiaNode("Pane", "EmbeddedBrowserFrameView") { FrameworkId = "Chrome" }.With(
                        new FakeUiaNode("Pane", "BrowserView") { FrameworkId = "Chrome" }.With(
                            new FakeUiaNode("Pane", "MultiContentsView") { FrameworkId = "Chrome" })))));
        Window(
            new FakeUiaNode("Pane", "HwndHost") { FrameworkId = "WPF" }.With(host),
            new FakeUiaNode("Button", "Native") { ActionPatterns = ["Invoke"] });
        var page = new FakeUiaNode("Pane", "Chrome Legacy Window") { FrameworkId = "Win32", NativeWindowHandle = RenderWidget }.With(
            new FakeUiaNode("Text", "Heading") { FrameworkId = "Chrome" },
            new FakeUiaNode("Button", "Web button")
            {
                FrameworkId = "Chrome", ActionPatterns = ["Invoke"], Bounds = new UiaRect(400, 300, 120, 32)
            });
        _tree.WithWindow(RenderWidget, page);
        return (host, page);
    }

    [Fact]
    public async Task GetUIElements_OfAWindowHostingAWebView2_ListsThePageFirst_UnderTheRenderWidgetsParent()
    {
        // Arrange
        WpfWithWebView2();
        var (sut, _) = WebSut();

        // Act
        var result = await sut.GetUIElementsAsync(Handle, maxDepth: 5);

        // Assert - the page at level 3, its elements at level 4; Chromium's own views after it, as far as maxDepth reaches
        result.Success.Should().BeTrue(result.Error);
        Shape(result.Elements).Should().Be(
            "HwndHost(WAM fixture web content(Chrome Legacy Window(Heading,Web button)," +
            "BrowserRootView(NonClientView(EmbeddedBrowserFrameView)))),Native");
        result.DepthReached.Should().Be(5);
        result.AccessibilityWarmup.Should().NotBeNull();
        result.AccessibilityWarmup!.Host.Should().Be(WebContentHost.WebView2);
    }

    [Fact]
    public async Task GetUIElements_GivesThePagesElementsRefsAndBounds_UnderTheWalkedWindow()
    {
        // Arrange
        WpfWithWebView2();
        var (sut, registry) = WebSut();

        // Act
        var result = await sut.GetUIElementsAsync(Handle, maxDepth: 4);

        // Assert - numbered breadth first like any walked element, the page ahead of Chromium's views
        var page = result.Elements![0].Children![0].Children![0];
        page.Name.Should().Be("Chrome Legacy Window");
        var button = page.Children![1];
        button.Name.Should().Be("Web button");
        button.FrameworkId.Should().Be("Chrome", "the page starts there");
        button.Bounds.Should().Be(new BoundsDto(400, 300, 120, 32));
        CountAll(result.Elements).Should().Be(8);
        page.Ref.Should().Be("w1e4");
        button.Ref.Should().Be("w1e7");
        var lookup = registry.Lookup(button.Ref!);
        lookup.WindowHandle.Should().Be(Handle, "the page's elements are the walked window's");
        lookup.Fingerprint!.Name.Should().Be("Web button");
    }

    [Fact]
    public async Task GetUIElements_WithActionableOnly_AsCaptureWithElementsMarksThem_ReturnsThePagesButton()
    {
        // Arrange
        WpfWithWebView2();
        var (sut, _) = WebSut();

        // Act
        var result = await sut.GetUIElementsAsync(Handle, maxDepth: 5, filter: new UIElementFilter(ActionableOnly: true));

        // Assert
        Shape(result.Elements).Should().Be("Web button,Native");
        result.Elements![0].Bounds.Should().Be(new BoundsDto(400, 300, 120, 32));
        result.Elements[0].Ref.Should().NotBeNull();
    }

    [Fact]
    public async Task GetUIElements_OfAWindowWithoutWebContent_MakesTheSameCallsAsWithoutTheWarmUp()
    {
        // Arrange
        BuildToolbarWindow();
        var plain = await new UIElementService(_tree, _worker, Win32Windows().Object).GetUIElementsAsync(Handle);
        var (reads, fetches, listings) = (_tree.Reads.Count, _tree.CachedFetches.Count, _tree.ChildListings.Count);
        var (sut, _) = WebSut();

        // Act
        var result = await sut.GetUIElementsAsync(Handle);

        // Assert
        Shape(result.Elements).Should().Be(Shape(plain.Elements));
        _tree.Reads.Skip(reads).Should().Equal(_tree.Reads.Take(reads));
        _tree.CachedFetches.Skip(fetches).Should().Equal(_tree.CachedFetches.Take(fetches));
        _tree.ChildListings.Skip(listings).Should().Equal(_tree.ChildListings.Take(listings));
    }

    [Fact]
    public async Task GetUIElements_DoesNotListThePageOfAHiddenRenderWidget()
    {
        // Arrange - a background tab
        WpfWithWebView2(visible: false);
        var (sut, _) = WebSut();

        // Act
        var result = await sut.GetUIElementsAsync(Handle, maxDepth: 5);

        // Assert
        Shape(result.Elements).Should().Be(
            "HwndHost(WAM fixture web content(BrowserRootView(NonClientView(EmbeddedBrowserFrameView)))),Native");
    }

    [Theory]
    [InlineData(2, "HwndHost(WAM fixture web content),Native")]
    [InlineData(3, "HwndHost(WAM fixture web content(Chrome Legacy Window,BrowserRootView)),Native")]
    public async Task GetUIElements_CountsThePageTowardMaxDepth_AtTheLevelBelowItsHost(int maxDepth, string shape)
    {
        // Arrange
        WpfWithWebView2();
        var (sut, _) = WebSut();

        // Act
        var result = await sut.GetUIElementsAsync(Handle, maxDepth);

        // Assert
        Shape(result.Elements).Should().Be(shape);
        result.DepthReached.Should().Be(maxDepth);
    }

    [Fact]
    public async Task GetUIElements_CountsThePageTowardMaxElements()
    {
        // Arrange
        WpfWithWebView2();
        var (sut, _) = WebSut();

        // Act
        var result = await sut.GetUIElementsAsync(Handle, maxDepth: 5, filter: new UIElementFilter(MaxElements: 4));

        // Assert - breadth first: the host's level, then the page ahead of Chromium's views
        Shape(result.Elements).Should().Be("HwndHost(WAM fixture web content(Chrome Legacy Window)),Native");
        result.TotalElementCount.Should().Be(4);
        result.TruncationReason.Should().Be(UIElementTruncationReason.MaxElements);
    }

    [Fact]
    public async Task GetUIElements_FindsTheHostInALevelReadOneElementAtATime()
    {
        // Arrange - the host's level cannot be read in one cached call, so its window handle is read on its own
        var (host, _) = WpfWithWebView2();
        host.Parent!.CachedChildrenException = new InvalidOperationException("one child failed the cached read");
        var (sut, _) = WebSut();

        // Act
        var result = await sut.GetUIElementsAsync(Handle, maxDepth: 4);

        // Assert
        Shape(result.Elements).Should().Be(
            "HwndHost(WAM fixture web content(Chrome Legacy Window(Heading,Web button),BrowserRootView(NonClientView))),Native");
    }

    [Fact]
    public async Task GetUIElements_OfABrowserWindow_ListsThePageFirstAmongTheWindowsChildren_AndOnlyOnce()
    {
        // Arrange - Chrome's render widget is a child of its own window, and its Document is also among Chromium's views
        _win32[Handle] = new Win32Window("Chrome_WidgetWin_1", 0, WebViewProcess);
        _win32[RenderWidget] = new Win32Window("Chrome_RenderWidgetHostHWND", Handle, WebViewProcess);
        var document = new FakeUiaNode("Document", "Page") { FrameworkId = "Chrome" }.With(
            new FakeUiaNode("Hyperlink", "Home") { FrameworkId = "Chrome" });
        Window(
            new FakeUiaNode("Pane", "BrowserView") { FrameworkId = "Chrome" }.With(
                new FakeUiaNode("Pane", "ContentsView") { FrameworkId = "Chrome" }.With(document)));
        _tree.WithWindow(RenderWidget, document);
        var (sut, _) = WebSut();

        // Act
        var result = await sut.GetUIElementsAsync(Handle, maxDepth: 5);

        // Assert
        Shape(result.Elements).Should().Be("Page(Home),BrowserView(ContentsView)");
    }

    #endregion

    #region A WebView2 hosted as a visual, and elements met again (roadmap M3.F43)

    /// <summary>
    /// The walk with the real warm-up and the element registry over a WinUI 3 window with a WebView2 hosted as a visual
    /// (<see cref="FakeWinUi3WebView2"/>), whose process started an msedgewebview2.exe.
    /// </summary>
    /// <param name="policy">The target policy the walk checks the page's process with; none by default.</param>
    private (UIElementService Sut, FakeWinUi3WebView2 App) WinUi3Sut(bool withDocument = true, ITargetPolicy? policy = null)
    {
        _win32[Handle] = new Win32Window("WinUIDesktopWin32WindowClass", 0, AppProcess);
        _win32[0x2010] = new Win32Window("Microsoft.UI.Content.DesktopChildSiteBridge", Handle, AppProcess);
        _win32[0x2011] = new Win32Window("Chrome_WidgetWin_0", 0x2010, AppProcess);
        var app = new FakeWinUi3WebView2(withDocument);
        _tree.WithWindow(Handle, app.Window);
        var windows = Win32Windows();
        windows.Setup(w => w.GetProcessName(AppProcess)).Returns("WamWebCheck");
        var processes = new Mock<INativeProcessWrapper>();
        processes.Setup(p => p.GetProcessList()).Returns(new Dictionary<int, (int ParentProcessId, string ImageName)>
        {
            [(int)AppProcess] = (1, "WamWebCheck.exe"),
            [(int)WebViewProcess] = ((int)AppProcess, "msedgewebview2.exe")
        });
        var warmup = new AccessibilityWarmup(_tree, windows.Object, processes.Object, wait: (_, _) => { });
        var registry = new ElementRegistry(_tree, _worker, windows.Object, ElementRegistry.DefaultCapacity, 1, TimeProvider.System);
        return (new UIElementService(_tree, _worker, windows.Object, warmup, registry, policy), app);
    }

    private const string WinUi3Shape =
        "Bridge(WAM page(Intermediate D3D Window,WAM page - Web content(WAM page(WAM WinUI 3 page,Page button,Amounts)))," +
        "XAML(Native button,WebView2))";

    [Fact]
    public async Task GetUIElements_OfAWinUi3WebView2_ListsThePageInPlaceOfChromiumsViews_WithinMaxDepth()
    {
        // Arrange - the page's Document is at level 11 of the window's tree
        var (sut, _) = WinUi3Sut();

        // Act
        var result = await sut.GetUIElementsAsync(Handle, maxDepth: 10);

        // Assert - the Document a level below the root of Chromium's views (level 4), its elements at level 5; the page and
        // the XAML island the WebView2 pane lists again are left out
        result.Success.Should().BeTrue(result.Error);
        Shape(result.Elements).Should().Be(WinUi3Shape);
        result.DepthReached.Should().Be(5);
        result.Truncated.Should().BeFalse();
        result.AccessibilityWarmup!.Status.Should().Be(AccessibilityWarmupStatus.Ready);
        result.AccessibilityWarmup.Host.Should().Be(WebContentHost.WebView2);
    }

    [Fact]
    public async Task GetUIElements_OfAWinUi3WebView2_GivesThePagesElementsRefsOfTheWindow_EachOnce()
    {
        // Arrange
        var (sut, _) = WinUi3Sut();

        // Act
        var result = await sut.GetUIElementsAsync(Handle, maxDepth: 10);

        // Assert
        var refs = All(result.Elements).Select(e => e.Ref).ToList();
        refs.Should().OnlyHaveUniqueItems().And.NotContainNulls();
        var button = All(result.Elements).Single(e => e.Name == "Page button");
        button.FrameworkId.Should().BeNull("the page's framework is named where it starts, at the root of Chromium's views");
        button.Bounds.Should().Be(new BoundsDto(40, 120, 90, 24));
    }

    [Fact]
    public async Task GetUIElements_OfAWinUi3WebView2ReadAgain_ListsThePageAgain_WithoutAWarmUp()
    {
        // Arrange
        var (sut, _) = WinUi3Sut();
        (await sut.GetUIElementsAsync(Handle, maxDepth: 10)).AccessibilityWarmup.Should().NotBeNull();

        // Act
        var result = await sut.GetUIElementsAsync(Handle, maxDepth: 10);

        // Assert
        Shape(result.Elements).Should().Be(WinUi3Shape);
        result.AccessibilityWarmup.Should().BeNull();
    }

    [Fact]
    public async Task GetUIElements_OfAWinUi3WebView2WithoutItsPageYet_ListsChromiumsViews()
    {
        // Arrange - the page's tree is not built: nothing to put in place of Chromium's views
        var (sut, _) = WinUi3Sut(withDocument: false);

        // Act
        var result = await sut.GetUIElementsAsync(Handle, maxDepth: 4);

        // Assert
        Shape(result.Elements).Should().Be(
            "Bridge(WAM page(Intermediate D3D Window,WAM page - Web content(NonClientView,TopContainerOverlayView))," +
            "XAML(Native button,WebView2))");
        result.AccessibilityWarmup!.Status.Should().Be(AccessibilityWarmupStatus.NotReady);
    }

    [Fact]
    public async Task GetUIElements_ListsAnElementMetAgainOnlyWhereItWasFirstMet()
    {
        // Arrange - a provider lists one element (the same runtime id) under two parents, and one of them below itself
        var sharedId = FakeUiaNode.NewRuntimeId();
        var loopId = FakeUiaNode.NewRuntimeId();
        Window(
            Node("Pane", "First").With(new FakeUiaNode("Button", "Shared") { RuntimeId = sharedId }),
            new FakeUiaNode("Pane", "Loop") { RuntimeId = loopId }.With(
                new FakeUiaNode("Button", "Shared") { RuntimeId = sharedId },
                Node("Group", "Inside").With(
                    new FakeUiaNode("Pane", "Loop") { RuntimeId = loopId }.With(Node("Button", "Again")))));

        // Act
        var result = await CreateSut().GetUIElementsAsync(Handle, maxDepth: 10);

        // Assert
        Shape(result.Elements).Should().Be("First(Shared),Loop(Inside)");
        result.DepthReached.Should().Be(2);
    }

    [Fact]
    public async Task GetUIElements_ListsElementsWithoutARuntimeIdEachTime()
    {
        // Arrange - they cannot be told apart
        Window(new FakeUiaNode("Button", "Same") { RuntimeId = null }, new FakeUiaNode("Button", "Same") { RuntimeId = null });

        // Act
        var result = await CreateSut().GetUIElementsAsync(Handle);

        // Assert
        Shape(result.Elements).Should().Be("Same,Same");
    }

    private static IEnumerable<UIElementInfo> All(IReadOnlyList<UIElementInfo>? elements) =>
        elements?.SelectMany(e => All(e.Children).Prepend(e)) ?? [];

    #endregion

    #region Elements of another process that the target policy refuses

    /// <summary>The default deny list, and the WebView2 runtime on it too.</summary>
    private static readonly TargetPolicy DeniesWebView2 =
        new(new TargetPolicySettings([.. TargetPolicySettings.DefaultDeniedProcesses, "msedgewebview2"], null, "test"));

    /// <summary>The walk with the real warm-up and <paramref name="policy"/>, over <see cref="_win32"/>; the window's app is allowed.</summary>
    private UIElementService PolicySut(ITargetPolicy policy, Action<Mock<INativeWindowWrapper>>? setup = null)
    {
        var windows = Win32Windows();
        windows.Setup(w => w.GetProcessName(AppProcess)).Returns("WamFixture");
        setup?.Invoke(windows);
        var warmup = new AccessibilityWarmup(_tree, windows.Object, new Mock<INativeProcessWrapper>().Object, wait: (_, _) => { });
        return new UIElementService(_tree, _worker, windows.Object, warmup, policy: policy);
    }

    [Fact]
    public async Task GetUIElements_LeavesOutAWebView2OfADeniedProcess_ItsPageAndItsWindow_AndSaysSo()
    {
        // Arrange - the user denies the WebView2 runtime; the window's own app is allowed
        WpfWithWebView2();

        // Act
        var result = await PolicySut(DeniesWebView2).GetUIElementsAsync(Handle, maxDepth: 5);

        // Assert - the page listed under its host, and the host's own subtree, are left out unread and counted
        result.Success.Should().BeTrue(result.Error);
        Shape(result.Elements).Should().Be("HwndHost,Native");
        result.SkippedElementCount.Should().Be(2);
        result.TruncationReason.Should().Be(UIElementTruncationReason.ElementsSkipped);
        result.Warnings.Should().ContainSingle(w => w.StartsWith("TargetDeniedByPolicy: ") && w.Contains("msedgewebview2"));
        var json = System.Text.Json.JsonSerializer.Serialize(result.Elements);
        json.Should().NotContain("Web button").And.NotContain("Heading").And.NotContain("BrowserRootView");
    }

    [Fact]
    public async Task GetUIElements_ListsAWebView2OfAnAllowedProcess_AsBefore()
    {
        WpfWithWebView2();

        var result = await PolicySut(new TargetPolicy(TargetPolicySettings.Default)).GetUIElementsAsync(Handle, maxDepth: 5);

        Shape(result.Elements).Should().Be(
            "HwndHost(WAM fixture web content(Chrome Legacy Window(Heading,Web button)," +
            "BrowserRootView(NonClientView(EmbeddedBrowserFrameView)))),Native");
        result.SkippedElementCount.Should().Be(0);
        result.Warnings.Should().BeNull();
    }

    [Fact]
    public async Task GetUIElements_LeavesOutAChildWindowOfADeniedApp_WithEverythingInsideIt()
    {
        // Arrange - a password manager's window embedded as a child window of the allowed app
        const nint vault = 0x2100;
        const uint vaultProcess = 7300;
        _win32[vault] = new Win32Window("KeePassEntryView", Handle, vaultProcess);
        Window(
            new FakeUiaNode("Pane", "Entry") { NativeWindowHandle = vault }.With(new FakeUiaNode("Text", "Recovery code 4829-1337")),
            new FakeUiaNode("Button", "OK"));

        // Act
        var result = await PolicySut(new TargetPolicy(TargetPolicySettings.Default),
            windows => windows.Setup(w => w.GetProcessName(vaultProcess)).Returns("KeePass")).GetUIElementsAsync(Handle);

        // Assert
        Shape(result.Elements).Should().Be("OK");
        result.Warnings.Should().ContainSingle(w => w.Contains("KeePass") && w.Contains("process 7300"));
        _tree.ChildListings.Should().NotContain("Entry", "nothing inside it is read");
        System.Text.Json.JsonSerializer.Serialize(result).Should().NotContain("4829");
    }

    [Fact]
    public async Task GetUIElements_OfAWinUi3WebView2OfADeniedProcess_LeavesOutItsViewsAndItsPage()
    {
        // Arrange - the WebView2's window, Chromium's views and the page hosted as a visual are all served by msedgewebview2;
        // none of the views has a window of its own, so each is checked by the process read with it
        var (sut, app) = WinUi3Sut(policy: DeniesWebView2);
        app.ServeWebViewFrom((int)WebViewProcess);

        // Act
        var result = await sut.GetUIElementsAsync(Handle, maxDepth: 10);

        // Assert - only the app's own elements are listed; each copy of the WebView2's window is left out with its subtree
        Shape(result.Elements).Should().Be("Bridge(XAML(Native button,WebView2))");
        result.Warnings.Should().ContainSingle(w => w.StartsWith("TargetDeniedByPolicy: ") && w.Contains("msedgewebview2"));
        result.SkippedElementCount.Should().BeGreaterThan(0);
        var json = System.Text.Json.JsonSerializer.Serialize(result.Elements);
        json.Should().NotContain("WAM page").And.NotContain("Page button").And.NotContain("NonClientView");
    }

    [Fact]
    public async Task GetUIElements_OfAWinUi3WebView2OfAnAllowedProcess_ListsItsPage()
    {
        var (sut, app) = WinUi3Sut(policy: new TargetPolicy(TargetPolicySettings.Default));
        app.ServeWebViewFrom((int)WebViewProcess);

        var result = await sut.GetUIElementsAsync(Handle, maxDepth: 10);

        Shape(result.Elements).Should().Be(WinUi3Shape);
        result.Warnings.Should().BeNull();
    }

    [Fact]
    public async Task GetUIElements_LeavesOutAnElementOfADeniedProcess_WithNoWindowOfItsOwn_WithEverythingInsideIt()
    {
        // Arrange - a mail's body shown by an embedded app (no window of its own: only the process read with it says whose)
        Window(
            Node("Pane", "Reading pane").With(
                new FakeUiaNode("Document", "Message body") { ProcessId = 7300 }.With(
                    new FakeUiaNode("Text", "Recovery code 4829-1337") { ProcessId = 7300 })),
            Node("Button", "Reply"));

        // Act
        var result = await PolicySut(new TargetPolicy(TargetPolicySettings.Default),
            windows => windows.Setup(w => w.GetProcessName(7300u)).Returns("KeePass")).GetUIElementsAsync(Handle);

        // Assert
        Shape(result.Elements).Should().Be("Reading pane,Reply");
        result.SkippedElementCount.Should().Be(1);
        result.Warnings.Should().ContainSingle(w => w.StartsWith("TargetDeniedByPolicy: ") && w.Contains("KeePass"));
        _tree.ChildListings.Should().NotContain("Message body", "nothing inside it is read");
        System.Text.Json.JsonSerializer.Serialize(result).Should().NotContain("4829").And.NotContain("Message body");
    }

    [Fact]
    public async Task GetUIElements_ReadsAnElementReadOnItsOwn_WithItsProcess_AndLeavesItOutWhenDenied()
    {
        // Arrange - the level's cached read fails, so its elements are read one at a time
        Window(Node("Pane", "Body").With(
                Node("Button", "Reply"),
                new FakeUiaNode("Text", "Recovery code 4829-1337") { ProcessId = 7300 }))
            .Children[0].CachedChildrenException = new System.Runtime.InteropServices.COMException("cache request failed");

        // Act
        var result = await PolicySut(new TargetPolicy(TargetPolicySettings.Default),
            windows => windows.Setup(w => w.GetProcessName(7300u)).Returns("KeePass")).GetUIElementsAsync(Handle);

        // Assert - each is read once, with the walk's properties (its process among them), not with its properties alone
        Shape(result.Elements).Should().Be("Body(Reply)");
        result.SkippedElementCount.Should().Be(1);
        _tree.Reads.Should().Contain(["Element:Reply", "Element:Recovery code 4829-1337"])
            .And.NotContain(["Read:Reply", "Read:Recovery code 4829-1337"]);
        System.Text.Json.JsonSerializer.Serialize(result).Should().NotContain("4829");
    }

    [Fact]
    public async Task GetUIElements_ReadsAnElementReadOnItsOwn_AsBefore_WithoutAPolicyThatDeniesAnything()
    {
        Window(Node("Pane", "Body").With(Node("Button", "Reply")))
            .Children[0].CachedChildrenException = new System.Runtime.InteropServices.COMException("cache request failed");

        var result = await PolicySut(new TargetPolicy(new TargetPolicySettings([], null, "test"))).GetUIElementsAsync(Handle);

        Shape(result.Elements).Should().Be("Body(Reply)");
        _tree.Reads.Should().Contain("Read:Reply").And.NotContain("Element:Reply");
    }

    #endregion

    #region Owned windows (roadmap M3.F9)

    private static readonly nint Dialog = 0x5678;

    /// <summary>GetWindow's GW_OWNER.</summary>
    private const uint GwOwner = 4;

    /// <summary>
    /// An owned dialog as UI Automation's HWND proxy shows it: a window element with its own window handle, listed among the
    /// children of the window that owns it, ahead of that window's own children.
    /// </summary>
    private static FakeUiaNode OwnedDialog(Mock<INativeWindowWrapper> windows, string title, params FakeUiaNode[] children)
    {
        windows.Setup(w => w.GetWindow(Dialog, GwOwner)).Returns(Handle);
        return new FakeUiaNode("Window", title) { NativeWindowHandle = Dialog }.With(children);
    }

    [Fact]
    public async Task GetUIElements_LeavesAnOwnedDialogListedInTheWindowsTreeOut_AndNamesIt()
    {
        // Arrange - Explorer's Delete File dialog, owned by the window read
        var windows = RespondingWindow();
        Window(
            OwnedDialog(windows, "Delete File", Node("Button", "Yes"), Node("Button", "No")),
            Node("Edit", "Name"),
            Node("Button", "Save"));

        // Act
        var result = await new UIElementService(_tree, _worker, windows.Object).GetUIElementsAsync(Handle);

        // Assert - the dialog is a window of its own: read by its handle, not as part of its owner
        result.Success.Should().BeTrue();
        Shape(result.Elements).Should().Be("Name,Save");
        result.TotalElementCount.Should().Be(2);
        result.OwnedWindows.Should().Equal((long)Dialog);
        _tree.ReadNodes.Should().NotContain(["Yes", "No"], "nothing inside the dialog is read");
    }

    [Fact]
    public async Task GetUIElements_KeepsAChildWindowWithAHandleOfItsOwn()
    {
        // Arrange - an edit control is a child window (WS_CHILD): it has a handle, but no owner
        var windows = RespondingWindow();
        Window(new FakeUiaNode("Edit", "Text") { NativeWindowHandle = 0x9ABC }, Node("Button", "Save"));

        // Act
        var result = await new UIElementService(_tree, _worker, windows.Object).GetUIElementsAsync(Handle);

        // Assert
        Shape(result.Elements).Should().Be("Text,Save");
        result.OwnedWindows.Should().BeNull();
    }

    [Fact]
    public async Task GetUIElements_LeavesOwnedWindowsOutOnlyDirectlyBelowTheWindow()
    {
        // Arrange - a pane deeper in the tree whose element has the dialog's handle is not one of the window's children
        var windows = RespondingWindow();
        windows.Setup(w => w.GetWindow(Dialog, GwOwner)).Returns(Handle);
        Window(Node("Pane", "Host").With(new FakeUiaNode("Pane", "Embedded") { NativeWindowHandle = Dialog }));

        // Act
        var result = await new UIElementService(_tree, _worker, windows.Object).GetUIElementsAsync(Handle);

        // Assert
        Shape(result.Elements).Should().Be("Host(Embedded)");
        result.OwnedWindows.Should().BeNull();
    }

    [Fact]
    public async Task GetUIElements_InALevelReadOneElementAtATime_StillLeavesTheOwnedDialogOut_ReadingEachChildOnce()
    {
        // Arrange - wider than one cached read, the owned dialog listed first as the HWND proxy lists it
        var windows = RespondingWindow();
        Window(new[] { OwnedDialog(windows, "Find", Node("Button", "Find Next")) }
            .Concat(Enumerable.Range(0, UIElementService.CachedLevelLimit + 1).Select(i => Node("Edit", $"Field {i}")))
            .ToArray());

        // Act
        var result = await new UIElementService(_tree, _worker, windows.Object).GetUIElementsAsync(Handle);

        // Assert - the first field, read to tell it is no owned window, is not read again
        result.TotalElementCount.Should().Be(UIElementService.CachedLevelLimit + 1);
        result.Elements!.Select(e => e.Name).Should().NotContain("Find");
        result.OwnedWindows.Should().Equal((long)Dialog);
        _tree.Reads.Count(r => r.EndsWith(":Field 0", StringComparison.Ordinal)).Should().Be(1);
        _tree.Reads.Should().NotContain("Element:Field 1", "only the children listed first are read to tell an owned window");
    }

    [Fact]
    public async Task GetUIElements_WhenTheWindowsLevelFailsItsCachedRead_StillLeavesTheOwnedDialogOut()
    {
        // Arrange
        var windows = RespondingWindow();
        var window = Window(OwnedDialog(windows, "Delete File", Node("Button", "Yes")), Node("Button", "Save"));
        window.CachedChildrenException = new System.Runtime.InteropServices.COMException("cache request failed");

        // Act
        var result = await new UIElementService(_tree, _worker, windows.Object).GetUIElementsAsync(Handle);

        // Assert
        Shape(result.Elements).Should().Be("Save");
        result.OwnedWindows.Should().Equal((long)Dialog);
    }

    [Fact]
    public async Task GetUIElements_OfTheOwnedDialogItself_ReadsItsElements()
    {
        // Arrange - the dialog read by its own handle
        var windows = RespondingWindow();
        windows.Setup(w => w.IsWindow(Dialog)).Returns(true);
        windows.Setup(w => w.RespondsWithin(Dialog, It.IsAny<uint>())).Returns(true);
        var dialog = OwnedDialog(windows, "Delete File", Node("Button", "Yes"), Node("Button", "No"));
        Window(dialog, Node("Button", "Save"));
        _tree.WithWindow(Dialog, dialog);

        // Act
        var result = await new UIElementService(_tree, _worker, windows.Object).GetUIElementsAsync(Dialog);

        // Assert
        Shape(result.Elements).Should().Be("Yes,No");
        result.OwnedWindows.Should().BeNull();
    }

    [Fact]
    public async Task GetUIElements_WithoutTheWin32Reads_LeavesNothingOut()
    {
        // Arrange - no window wrapper: whether a child is owned cannot be told
        Window(new FakeUiaNode("Window", "Delete File") { NativeWindowHandle = Dialog }.With(Node("Button", "Yes")), Node("Button", "Save"));

        // Act
        var result = await CreateSut().GetUIElementsAsync(Handle);

        // Assert
        Shape(result.Elements).Should().Be("Delete File(Yes),Save");
        result.OwnedWindows.Should().BeNull();
    }

    #endregion

    #region Minimized and elevated targets (roadmap M3.F16)

    private static readonly ProcessIntegrity MediumServer = new(1, IntegrityLevel.Medium, ProcessIntegrity.MediumRid, false, null);

    /// <summary>A window wrapper for a window that responds, of process <paramref name="processId"/>.</summary>
    private static Mock<INativeWindowWrapper> WindowOfProcess(uint processId = 4242)
    {
        var windows = new Mock<INativeWindowWrapper>();
        windows.Setup(w => w.IsWindow(It.IsAny<nint>())).Returns(true);
        windows.Setup(w => w.RespondsWithin(It.IsAny<nint>(), It.IsAny<uint>())).Returns(true);
        windows.Setup(w => w.GetWindowThreadProcessId(Handle)).Returns(processId);
        return windows;
    }

    /// <summary>A security probe for a medium-integrity server, which reads <paramref name="processId"/> at <paramref name="level"/>.</summary>
    private static Mock<IProcessSecurityProbe> Security(int processId, IntegrityLevel level, int? rid)
    {
        var security = new Mock<IProcessSecurityProbe>();
        security.SetupGet(s => s.ServerIntegrity).Returns(MediumServer);
        security.Setup(s => s.GetIntegrity(It.IsAny<int>()))
            .Returns<int>(id => new ProcessIntegrity(id, IntegrityLevel.Medium, ProcessIntegrity.MediumRid, false, null));
        security.Setup(s => s.GetIntegrity(processId)).Returns(new ProcessIntegrity(processId, level, rid, null, null));
        return security;
    }

    private static string Json(UIElementResult result) =>
        System.Text.Json.JsonSerializer.Serialize(result, ModelContextProtocol.McpJsonUtilities.DefaultOptions);

    [Fact]
    public async Task GetUIElements_OfAMinimizedWindow_WarnsThatVisibleOnlyLeftItsElementsOut()
    {
        // Arrange - a minimized window's elements are offscreen, so visibleOnly leaves them all out (D-UIA-07)
        Window(new FakeUiaNode("Button", "Save") { IsOffscreen = true });
        var windows = WindowOfProcess();
        windows.Setup(w => w.IsIconic(Handle)).Returns(true);

        // Act
        var result = await new UIElementService(_tree, _worker, windows.Object)
            .GetUIElementsAsync(Handle, filter: new UIElementFilter(VisibleOnly: true));

        // Assert - one warning with a stable code, naming the window to restore and how
        result.Success.Should().BeTrue();
        result.Elements.Should().BeEmpty();
        result.Warnings.Should().ContainSingle().Which.Should()
            .StartWith("WindowMinimized: ").And.Contain("visibleOnly").And.Contain("set_window_state")
            .And.Contain($"handle {(long)Handle}").And.Contain("focus_window");
        Json(result).Should().Contain("\"warnings\":[\"WindowMinimized: ");
    }

    [Fact]
    public async Task GetUIElements_OfAMinimizedWindowWithVisibleOnlyOff_WarnsThatItsElementsAreOffscreen()
    {
        // Arrange
        Window(new FakeUiaNode("Button", "Save") { IsOffscreen = true });
        var windows = WindowOfProcess();
        windows.Setup(w => w.IsIconic(Handle)).Returns(true);

        // Act
        var result = await new UIElementService(_tree, _worker, windows.Object)
            .GetUIElementsAsync(Handle, filter: new UIElementFilter(VisibleOnly: false));

        // Assert - the elements are returned, but their bounds are not where they will be once the window is restored
        Shape(result.Elements).Should().Be("Save");
        result.Warnings.Should().ContainSingle().Which.Should()
            .StartWith("WindowMinimized: ").And.Contain("offscreen").And.Contain("ElementOffscreen").And.NotContain("visibleOnly");
    }

    [Fact]
    public async Task GetUIElements_OfAControlInAMinimizedWindow_NamesTheTopLevelWindowToRestore()
    {
        // Arrange
        BuildToolbarWindow();
        var root = new nint(0x7000);
        var windows = WindowOfProcess();
        windows.Setup(w => w.GetAncestor(Handle, WindowAncestor.Root)).Returns(root);
        windows.Setup(w => w.IsIconic(root)).Returns(true);

        // Act
        var result = await new UIElementService(_tree, _worker, windows.Object).GetUIElementsAsync(Handle);

        // Assert - set_window_state restores a top-level window
        result.WindowState.Should().Be(WindowState.Minimized);
        result.Warnings.Should().ContainSingle().Which.Should().Contain($"handle {(long)root}");
    }

    [Fact]
    public async Task GetUIElements_OfANormalWindowAtTheServersLevel_HasNoWarnings()
    {
        // Arrange
        BuildToolbarWindow();
        var security = Security(4242, IntegrityLevel.Medium, ProcessIntegrity.MediumRid);

        // Act
        var result = await new UIElementService(_tree, _worker, WindowOfProcess().Object, security: security.Object)
            .GetUIElementsAsync(Handle);

        // Assert - left out of the result, so an ordinary walk costs no tokens for it
        result.Warnings.Should().BeNull();
        Json(result).Should().NotContain("warnings");
    }

    [Theory]
    [InlineData(IntegrityLevel.High, ProcessIntegrity.HighRid)]
    [InlineData(IntegrityLevel.System, ProcessIntegrity.SystemRid)]
    [InlineData(IntegrityLevel.LikelyElevated, null)]
    public async Task GetUIElements_OfAnElevatedWindow_WarnsTargetElevated(IntegrityLevel level, int? rid)
    {
        // Arrange - UIPI keeps a client below the window's integrity level out of its provider: the tree comes back empty
        Window();
        var security = Security(4242, level, rid);

        // Act
        var result = await new UIElementService(_tree, _worker, WindowOfProcess().Object, security: security.Object)
            .GetUIElementsAsync(Handle);

        // Assert - still a success, with the cause and what to do
        result.Success.Should().BeTrue();
        result.Warnings.Should().ContainSingle().Which.Should()
            .StartWith("TargetElevated: ").And.Contain(level.ToString()).And.Contain("Medium").And.Contain("administrator");
    }

    [Theory]
    [InlineData(IntegrityLevel.Restricted)]
    [InlineData(IntegrityLevel.Unknown)]
    [InlineData(IntegrityLevel.Low)]
    public async Task GetUIElements_OfAWindowNotKnownToBeMorePrivileged_HasNoElevationWarning(IntegrityLevel level)
    {
        // Arrange - Restricted may be another user's ordinary process, so it is not called elevated (as the input guard)
        Window();
        var security = Security(4242, level, level == IntegrityLevel.Low ? ProcessIntegrity.LowRid : null);

        // Act
        var result = await new UIElementService(_tree, _worker, WindowOfProcess().Object, security: security.Object)
            .GetUIElementsAsync(Handle);

        // Assert
        result.Warnings.Should().BeNull();
    }

    [Fact]
    public async Task GetUIElements_OfAnElevatedWindowWhoseChildrenCannotBeRead_StillWarnsTargetElevated()
    {
        // Arrange - the failure is what UIPI does, so the warning says why
        Window().ChildrenException = new System.Runtime.InteropServices.COMException("access denied");
        var security = Security(4242, IntegrityLevel.High, ProcessIntegrity.HighRid);

        // Act
        var result = await new UIElementService(_tree, _worker, WindowOfProcess().Object, security: security.Object)
            .GetUIElementsAsync(Handle);

        // Assert
        result.Success.Should().BeFalse();
        result.ErrorCode.Should().Be(UIElementErrorCode.OperationException);
        result.Warnings.Should().ContainSingle().Which.Should().StartWith("TargetElevated: ");
    }

    [Fact]
    public async Task GetUIElements_OfAMinimizedElevatedWindow_GivesBothWarnings()
    {
        // Arrange
        Window();
        var windows = WindowOfProcess();
        windows.Setup(w => w.IsIconic(Handle)).Returns(true);
        var security = Security(4242, IntegrityLevel.High, ProcessIntegrity.HighRid);

        // Act
        var result = await new UIElementService(_tree, _worker, windows.Object, security: security.Object).GetUIElementsAsync(Handle);

        // Assert - the elevation first: restoring the window does not make its tree readable
        result.Warnings.Should().HaveCount(2);
        result.Warnings![0].Should().StartWith("TargetElevated: ");
        result.Warnings[1].Should().StartWith("WindowMinimized: ");
    }

    [Fact]
    public async Task GetUIElements_OfAUwpFrame_ChecksTheElevationOfTheAppItShows()
    {
        // Arrange - the frame belongs to ApplicationFrameHost; the app runs in its CoreWindow's process
        Window();
        var coreWindow = new nint(0x6001);
        var windows = WindowOfProcess(processId: 6000);
        windows.Setup(w => w.GetClassName(Handle)).Returns("ApplicationFrameWindow");
        windows.Setup(w => w.GetClassName(coreWindow)).Returns("Windows.UI.Core.CoreWindow");
        windows.Setup(w => w.GetWindowThreadProcessId(coreWindow)).Returns(7000u);
        windows.Setup(w => w.EnumChildWindows(Handle, It.IsAny<Func<nint, bool>>()))
            .Callback<nint, Func<nint, bool>>((_, callback) => callback(coreWindow));
        var security = Security(7000, IntegrityLevel.High, ProcessIntegrity.HighRid);

        // Act
        var result = await new UIElementService(_tree, _worker, windows.Object, security: security.Object).GetUIElementsAsync(Handle);

        // Assert
        result.Warnings.Should().ContainSingle().Which.Should().StartWith("TargetElevated: ");
    }

    [Fact]
    public async Task GetUIElements_OfAnUnknownWindow_HasNoWarnings()
    {
        // Arrange - nothing was read, so there is nothing to explain
        var windows = WindowOfProcess();
        windows.Setup(w => w.IsIconic(It.IsAny<nint>())).Returns(true);
        var security = Security(4242, IntegrityLevel.High, ProcessIntegrity.HighRid);

        // Act
        var result = await new UIElementService(_tree, _worker, windows.Object, security: security.Object)
            .GetUIElementsAsync(new nint(999));

        // Assert
        result.ErrorCode.Should().Be(UIElementErrorCode.WindowNotFound);
        result.Warnings.Should().BeNull();
    }

    [Fact]
    public async Task GetUIElements_WhenTheWindowsProcessCannotBeRead_ReturnsTheTreeWithoutAnElevationWarning()
    {
        // Arrange
        BuildToolbarWindow();
        var windows = WindowOfProcess();
        windows.Setup(w => w.GetWindowThreadProcessId(Handle)).Throws(new InvalidOperationException("gone"));
        var security = Security(4242, IntegrityLevel.High, ProcessIntegrity.HighRid);

        // Act
        var result = await new UIElementService(_tree, _worker, windows.Object, security: security.Object).GetUIElementsAsync(Handle);

        // Assert
        result.Success.Should().BeTrue();
        result.Warnings.Should().BeNull();
        security.Verify(s => s.GetIntegrity(It.IsAny<int>()), Times.Never);
    }

    #endregion
}
