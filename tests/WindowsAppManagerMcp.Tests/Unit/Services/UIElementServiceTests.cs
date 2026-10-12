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
}
