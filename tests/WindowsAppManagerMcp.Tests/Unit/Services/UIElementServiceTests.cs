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
    public void GetUIElements_WithZeroHandle_ReturnsInvalidHandleWithoutReadingTheTree()
    {
        // Act
        var result = CreateSut().GetUIElements(nint.Zero);

        // Assert
        result.Success.Should().BeFalse();
        result.ErrorCode.Should().Be(UIElementErrorCode.InvalidHandle);
        result.Error.Should().Contain("Invalid window handle");
        result.Elements.Should().BeNull();
        result.Depth.Should().Be(0);
        _worker.Calls.Should().Be(0);
    }

    [Fact]
    public void GetUIElements_WhenUiAutomationIsUnavailable_ReturnsAutomationUnavailable()
    {
        // Arrange
        BuildToolbarWindow();
        _tree.Available = false;

        // Act
        var result = CreateSut().GetUIElements(Handle);

        // Assert
        result.Success.Should().BeFalse();
        result.ErrorCode.Should().Be(UIElementErrorCode.AutomationUnavailable);
    }

    [Fact]
    public void GetUIElements_WithUnknownHandle_ReturnsWindowNotFound()
    {
        // Act
        var result = CreateSut().GetUIElements(new nint(999));

        // Assert
        result.Success.Should().BeFalse();
        result.ErrorCode.Should().Be(UIElementErrorCode.WindowNotFound);
        result.WindowHandle.Should().Be(999);
    }

    [Fact]
    public void GetUIElements_WhenTheWindowVanishesDuringLookup_ReturnsWindowNotFound()
    {
        // Arrange
        _tree.FromHandleException = FakeUiaTree.Vanished("window");

        // Act
        var result = CreateSut().GetUIElements(Handle);

        // Assert
        result.Success.Should().BeFalse();
        result.ErrorCode.Should().Be(UIElementErrorCode.WindowNotFound);
    }

    [Fact]
    public void GetUIElements_WhenLookupFailsUnexpectedly_ReturnsOperationException()
    {
        // Arrange
        _tree.FromHandleException = new InvalidOperationException("provider exploded");

        // Act
        var result = CreateSut().GetUIElements(Handle);

        // Assert
        result.Success.Should().BeFalse();
        result.ErrorCode.Should().Be(UIElementErrorCode.OperationException);
        result.Error.Should().Contain("provider exploded");
    }

    [Theory]
    [InlineData("Buton")]
    [InlineData("Button,Textbox")]
    [InlineData("Btn, Edit")]
    public void GetUIElements_WithUnknownControlType_ReturnsInvalidControlTypeListingTheValidNames(string controlTypes)
    {
        // Arrange
        BuildToolbarWindow();
        var filter = new UIElementFilter(ControlTypes: controlTypes.Split(',', StringSplitOptions.TrimEntries));

        // Act
        var result = CreateSut().GetUIElements(Handle, filter: filter);

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
    public void GetUIElements_ControlTypeFilter_IgnoresCaseAndSurroundingSpace(string controlType)
    {
        // Arrange
        BuildToolbarWindow();

        // Act
        var result = CreateSut().GetUIElements(Handle, filter: new UIElementFilter(ControlTypes: [controlType]));

        // Assert
        result.Success.Should().BeTrue();
        Shape(result.Elements).Should().Be("Save,Open,OK");
    }

    [Theory]
    [InlineData("AppBar")]
    [InlineData("SemanticZoom")]
    [InlineData("Unknown")]
    public void GetUIElements_AcceptsTheFallbackControlTypeNames(string controlType)
    {
        // Arrange
        Window(Node(controlType, "Special"), Node("Button", "Other"));

        // Act
        var result = CreateSut().GetUIElements(Handle, filter: new UIElementFilter(ControlTypes: [controlType]));

        // Assert
        result.Success.Should().BeTrue();
        Shape(result.Elements).Should().Be("Special");
    }

    #endregion

    #region controlTypes: matches inside other containers are returned

    [Fact]
    public void GetUIElements_ControlTypeFilter_ReturnsMatchesNestedInNonMatchingContainers()
    {
        // Arrange - the reported bug: Buttons inside Pane/ToolBar/Group came back as []
        BuildToolbarWindow();

        // Act
        var result = CreateSut().GetUIElements(Handle, filter: new UIElementFilter(ControlTypes: ["Button", "Edit"]));

        // Assert - each match takes the place of its left-out containers, in tree order
        result.Success.Should().BeTrue();
        Shape(result.Elements).Should().Be("Save,Open,Name,OK");
        result.TotalElementCount.Should().Be(4);
    }

    [Fact]
    public void GetUIElements_ControlTypeFilter_KeepsMatchesNestedUnderTheirNearestMatchingAncestor()
    {
        // Arrange
        BuildToolbarWindow();

        // Act
        var result = CreateSut().GetUIElements(Handle, filter: new UIElementFilter(ControlTypes: ["ToolBar", "Button"]));

        // Assert
        Shape(result.Elements).Should().Be("Tools(Save,Open),OK");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("Button")]
    [InlineData("Pane,Button")]
    [InlineData("Group,Edit")]
    [InlineData("Window")]
    public void GetUIElements_TotalElementCount_CountsEachReturnedElementOnce(string? controlTypes)
    {
        // Arrange
        BuildToolbarWindow();
        var filter = new UIElementFilter(ControlTypes: controlTypes?.Split(','));

        // Act
        var result = CreateSut().GetUIElements(Handle, filter: filter);

        // Assert
        result.Success.Should().BeTrue();
        result.TotalElementCount.Should().Be(CountAll(result.Elements));
    }

    [Fact]
    public void GetUIElements_WithoutFilters_ReturnsTheWholeTreeNested()
    {
        // Arrange
        BuildToolbarWindow();

        // Act
        var result = CreateSut().GetUIElements(Handle);

        // Assert
        Shape(result.Elements).Should().Be("Host(Tools(Save,Open)),Name,Footer(Box(OK))");
        result.TotalElementCount.Should().Be(8);
    }

    #endregion

    #region maxDepth counts levels below the window

    [Fact]
    public void GetUIElements_WithMaxDepthOne_ReturnsOnlyTheWindowsChildrenAndReadsNothingDeeper()
    {
        // Arrange
        BuildToolbarWindow();

        // Act
        var result = CreateSut().GetUIElements(Handle, maxDepth: 1);

        // Assert
        Shape(result.Elements).Should().Be("Host,Name,Footer");
        result.Depth.Should().Be(1);
        result.DepthReached.Should().Be(1);
        _tree.ReadNodes.Should().NotContain(["Tools", "Box", "Save", "OK"]);
    }

    [Fact]
    public void GetUIElements_WithMaxDepthTwo_ReturnsExactlyTwoLevels()
    {
        // Arrange
        BuildToolbarWindow();

        // Act
        var result = CreateSut().GetUIElements(Handle, maxDepth: 2);

        // Assert
        Shape(result.Elements).Should().Be("Host(Tools),Name,Footer(Box)");
        result.DepthReached.Should().Be(2);
    }

    [Fact]
    public void GetUIElements_WhenTheTreeIsShallowerThanMaxDepth_ReportsTheDepthReached()
    {
        // Arrange
        BuildToolbarWindow();

        // Act
        var result = CreateSut().GetUIElements(Handle, maxDepth: 10);

        // Assert
        result.Depth.Should().Be(10);
        result.DepthReached.Should().Be(3);
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(-5, 1)]
    [InlineData(15, 10)]
    [InlineData(100, 10)]
    public void GetUIElements_ClampsMaxDepthToOneThroughTen(int requested, int applied)
    {
        // Arrange
        BuildToolbarWindow();

        // Act
        var result = CreateSut().GetUIElements(Handle, maxDepth: requested);

        // Assert
        result.Success.Should().BeTrue();
        result.Depth.Should().Be(applied);
    }

    [Fact]
    public void GetUIElements_MaxDepthBoundsTheWalkEvenWhenFiltersLeaveContainersOut()
    {
        // Arrange - the Buttons are at level 3
        BuildToolbarWindow();

        // Act
        var result = CreateSut().GetUIElements(Handle, maxDepth: 2, filter: new UIElementFilter(ControlTypes: ["Button"]));

        // Assert
        result.Success.Should().BeTrue();
        result.Elements.Should().BeEmpty();
        _tree.ReadNodes.Should().NotContain(["Save", "Open", "OK"]);
    }

    #endregion

    #region Size and interactable filters apply to each element only

    [Fact]
    public void GetUIElements_MinWidth_LeavesOutANarrowContainerButReturnsItsWideChildren()
    {
        // Arrange - a narrow sidebar with a wide button inside
        Window(
            new FakeUiaNode("Pane", "Sidebar") { Bounds = new UiaRect(0, 0, 5, 400) }.With(
                new FakeUiaNode("Button", "Wide") { Bounds = new UiaRect(0, 0, 80, 30) },
                new FakeUiaNode("Button", "Tiny") { Bounds = new UiaRect(0, 40, 4, 4) }));

        // Act
        var result = CreateSut().GetUIElements(Handle, filter: new UIElementFilter(MinWidth: 20));

        // Assert
        Shape(result.Elements).Should().Be("Wide");
    }

    [Fact]
    public void GetUIElements_MinHeight_ReturnsChildrenOfAContainerWithoutBounds()
    {
        // Arrange - layout containers often report an empty rectangle
        Window(
            new FakeUiaNode("Group", "Layout") { Bounds = UiaRect.Empty }.With(
                new FakeUiaNode("Edit", "Field") { Bounds = new UiaRect(10, 10, 200, 24) }));

        // Act
        var result = CreateSut().GetUIElements(Handle, filter: new UIElementFilter(MinHeight: 10));

        // Assert
        Shape(result.Elements).Should().Be("Field");
    }

    [Fact]
    public void GetUIElements_InteractableOnly_KeepsEnabledFocusableOrActionableElementsAtAnyDepth()
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
        var result = CreateSut().GetUIElements(Handle, filter: new UIElementFilter(InteractableOnly: true));

        // Assert - Pane, Text and the disabled elements are left out; their qualifying children are not
        Shape(result.Elements).Should().Be("Go,Field,Option");
        result.Elements!.Should().OnlyContain(e => e.IsEnabled);
    }

    [Fact]
    public void GetUIElements_InteractableOnly_ReadsFocusAndPatternsOnlyForCandidates()
    {
        // Arrange
        Window(
            new FakeUiaNode("Button", "Off") { IsEnabled = false },
            new FakeUiaNode("Text", "Label"),
            new FakeUiaNode("Button", "Go") { ActionPatterns = ["Invoke"] });

        // Act
        CreateSut().GetUIElements(Handle, filter: new UIElementFilter(ControlTypes: ["Button"], InteractableOnly: true));

        // Assert - each extra read is a cross-process call per element
        _tree.Reads.Where(r => r.StartsWith("Interaction:")).Should().Equal("Interaction:Go");
    }

    [Fact]
    public void GetUIElements_WithoutInteractableOnly_DoesNotReadFocusOrPatterns()
    {
        // Arrange
        BuildToolbarWindow();

        // Act
        CreateSut().GetUIElements(Handle);

        // Assert
        _tree.Reads.Should().OnlyContain(r => r.StartsWith("Read:"));
    }

    #endregion

    #region visibleOnly prunes offscreen subtrees

    [Fact]
    public void GetUIElements_VisibleOnly_LeavesOutAnOffscreenElementWithEverythingInsideIt()
    {
        // Arrange
        Window(
            new FakeUiaNode("List", "Scrolled away") { IsOffscreen = true }.With(Node("ListItem", "Item")),
            Node("Button", "Visible"));

        // Act
        var result = CreateSut().GetUIElements(Handle, filter: new UIElementFilter(VisibleOnly: true));

        // Assert
        Shape(result.Elements).Should().Be("Visible");
        _tree.ReadNodes.Should().NotContain("Item", "an offscreen element's subtree is not walked");
    }

    [Fact]
    public void GetUIElements_WithVisibleOnlyFalse_ReturnsOffscreenElements()
    {
        // Arrange
        Window(
            new FakeUiaNode("List", "Scrolled away") { IsOffscreen = true }.With(Node("ListItem", "Item")),
            Node("Button", "Visible"));

        // Act
        var result = CreateSut().GetUIElements(Handle, filter: new UIElementFilter(VisibleOnly: false));

        // Assert
        Shape(result.Elements).Should().Be("Scrolled away(Item),Visible");
        result.Elements![0].IsOffscreen.Should().BeTrue();
    }

    #endregion

    #region Bounds

    [Fact]
    public void GetUIElements_ElementWithoutGeometry_HasNullBounds()
    {
        // Arrange
        Window(
            new FakeUiaNode("Text", "Nowhere") { Bounds = UiaRect.Empty },
            new FakeUiaNode("Text", "Broken") { Bounds = new UiaRect(double.NaN, 0, 10, 10) },
            new FakeUiaNode("Button", "Somewhere") { Bounds = new UiaRect(-1920.5, 10.25, 80, 30) });

        // Act
        var result = CreateSut().GetUIElements(Handle);

        // Assert
        result.Elements!.Select(e => e.Bounds).Should().Equal(null, null, new BoundsDto(-1921, 10, 81, 31));
    }

    #endregion

    #region Failures while walking

    [Fact]
    public void GetUIElements_WhenAnElementVanishes_SkipsItAndKeepsItsSiblings()
    {
        // Arrange
        Window(
            Node("Button", "A"),
            new FakeUiaNode("Button", "Gone") { ReadException = FakeUiaTree.Vanished() }.With(Node("Text", "Inside")),
            Node("Button", "C"));

        // Act
        var result = CreateSut().GetUIElements(Handle);

        // Assert
        result.Success.Should().BeTrue();
        Shape(result.Elements).Should().Be("A,C");
        result.SkippedElementCount.Should().Be(1);
        result.Truncated.Should().BeTrue();
        result.TruncationReason.Should().Be(UIElementTruncationReason.ElementsSkipped);
    }

    [Fact]
    public void GetUIElements_WhenAProviderFails_SkipsTheElementLikeAVanishedOne()
    {
        // Arrange
        Window(
            new FakeUiaNode("Button", "Bad") { ReadException = new System.Runtime.InteropServices.COMException("RPC failed") },
            Node("Button", "Good"));

        // Act
        var result = CreateSut().GetUIElements(Handle);

        // Assert
        Shape(result.Elements).Should().Be("Good");
        result.SkippedElementCount.Should().Be(1);
    }

    [Fact]
    public void GetUIElements_WhenAnElementsChildrenCannotBeRead_ReturnsTheElementAndMarksTheTreeTruncated()
    {
        // Arrange
        Window(
            new FakeUiaNode("Pane", "Half") { ChildrenException = FakeUiaTree.Vanished() }.With(Node("Button", "Lost")),
            Node("Button", "Next"));

        // Act
        var result = CreateSut().GetUIElements(Handle);

        // Assert
        Shape(result.Elements).Should().Be("Half,Next");
        result.SkippedElementCount.Should().Be(1);
        result.Truncated.Should().BeTrue();
        result.TruncationReason.Should().Be(UIElementTruncationReason.ElementsSkipped);
    }

    [Fact]
    public void GetUIElements_WhenInteractionPropertiesCannotBeRead_SkipsTheElement()
    {
        // Arrange
        Window(
            new FakeUiaNode("Button", "Bad") { InteractionException = FakeUiaTree.Vanished() },
            new FakeUiaNode("Button", "Good") { ActionPatterns = ["Invoke"] });

        // Act
        var result = CreateSut().GetUIElements(Handle, filter: new UIElementFilter(InteractableOnly: true));

        // Assert
        Shape(result.Elements).Should().Be("Good");
        result.SkippedElementCount.Should().Be(1);
    }

    [Fact]
    public void GetUIElements_WhenTheWindowsChildrenVanish_ReturnsElementAccessFailed()
    {
        // Arrange
        Window().ChildrenException = FakeUiaTree.Vanished("window");

        // Act
        var result = CreateSut().GetUIElements(Handle);

        // Assert - an empty tree would look like a window without controls
        result.Success.Should().BeFalse();
        result.ErrorCode.Should().Be(UIElementErrorCode.ElementAccessFailed);
        result.Elements.Should().BeNull();
    }

    [Fact]
    public void GetUIElements_WhenTheWindowsChildrenCannotBeRead_ReturnsOperationException()
    {
        // Arrange
        Window().ChildrenException = new System.Runtime.InteropServices.COMException("boom");

        // Act
        var result = CreateSut().GetUIElements(Handle);

        // Assert
        result.Success.Should().BeFalse();
        result.ErrorCode.Should().Be(UIElementErrorCode.OperationException);
        result.Error.Should().Contain("boom");
    }

    [Fact]
    public void GetUIElements_WhenTheWindowsProviderTimesOut_ReturnsOperationTimedOut()
    {
        // Arrange
        Window().ChildrenException = new TimeoutException("provider timeout");

        // Act
        var result = CreateSut().GetUIElements(Handle);

        // Assert
        result.Success.Should().BeFalse();
        result.ErrorCode.Should().Be(UIElementErrorCode.OperationTimedOut);
    }

    [Fact]
    public void GetUIElements_WhenTheWindowTitleCannotBeRead_StillReturnsTheTree()
    {
        // Arrange
        var window = new FakeUiaNode("Window", "Title") { ReadException = FakeUiaTree.Vanished() }.With(Node("Button", "A"));
        _tree.WithWindow(Handle, window);

        // Act
        var result = CreateSut().GetUIElements(Handle);

        // Assert
        result.Success.Should().BeTrue();
        result.WindowTitle.Should().BeNull();
        Shape(result.Elements).Should().Be("A");
    }

    #endregion

    #region Timeouts return what was read

    [Fact]
    public void GetUIElements_WhenTheTimeoutPassesDuringTheWalk_ReturnsThePartialTree()
    {
        // Arrange - the timeout fires while "B" is read
        using var cts = new CancellationTokenSource();
        Window(
            Node("Button", "A"),
            new FakeUiaNode("Pane", "B") { OnRead = cts.Cancel }.With(Node("Button", "B1")),
            Node("Button", "C"));

        // Act
        var result = CreateSut().GetUIElements(Handle, cancellationToken: cts.Token);

        // Assert
        result.Success.Should().BeTrue();
        result.ErrorCode.Should().Be(UIElementErrorCode.None);
        result.Truncated.Should().BeTrue();
        result.TruncationReason.Should().Be(UIElementTruncationReason.Timeout);
        Shape(result.Elements).Should().Be("A,B");
        result.TotalElementCount.Should().Be(2);
        _tree.ReadNodes.Should().NotContain(["B1", "C"]);
    }

    [Fact]
    public void GetUIElements_WhenTheTimeoutPassesBeforeAnyElementIsRead_ReturnsOperationTimedOut()
    {
        // Arrange
        BuildToolbarWindow();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        // Act
        var result = CreateSut().GetUIElements(Handle, cancellationToken: cts.Token);

        // Assert
        result.Success.Should().BeFalse();
        result.ErrorCode.Should().Be(UIElementErrorCode.OperationTimedOut);
        result.Elements.Should().BeNull();
    }

    [Fact]
    public void GetUIElements_RunsTheWalkOnTheWorkerWithTheCallersTokenAndAGracePeriod()
    {
        // Arrange
        BuildToolbarWindow();
        using var cts = new CancellationTokenSource();

        // Act
        CreateSut().GetUIElements(Handle, cancellationToken: cts.Token);

        // Assert - the grace lets a walk that stops at the timeout hand back its partial tree
        _worker.Calls.Should().Be(1);
        _worker.LastToken.Should().Be(cts.Token);
        _worker.LastGrace.Should().BePositive();
    }

    [Fact]
    public void GetUIElements_WhenUiAutomationDoesNotAnswerByTheDeadline_ReturnsOperationTimedOut()
    {
        // Arrange - the worker gave up on a read stuck in a hung application
        BuildToolbarWindow();
        var worker = new Mock<IUiaWorker>();
        worker.Setup(w => w.Run(It.IsAny<Func<CancellationToken, UIElementResult>>(), It.IsAny<CancellationToken>(), It.IsAny<TimeSpan>()))
            .Throws(new TimeoutException("UI Automation did not answer in time."));
        var sut = new UIElementService(_tree, worker.Object);

        // Act
        var result = sut.GetUIElements(Handle);

        // Assert
        result.Success.Should().BeFalse();
        result.ErrorCode.Should().Be(UIElementErrorCode.OperationTimedOut);
        result.Error.Should().Contain("did not answer");
    }

    #endregion

    #region Result

    [Fact]
    public void GetUIElements_CompleteWalk_ReportsTheWindowAndNoTruncation()
    {
        // Arrange
        BuildToolbarWindow();

        // Act
        var result = CreateSut().GetUIElements(Handle, maxDepth: 5);

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
    public void GetUIElements_CopiesTheElementProperties()
    {
        // Arrange
        Window(new FakeUiaNode("Button", "Save")
        {
            AutomationId = "SaveBtn",
            Bounds = new UiaRect(10, 20, 80, 24),
            IsEnabled = false,
        });

        // Act
        var element = CreateSut().GetUIElements(Handle).Elements!.Single();

        // Assert
        element.Should().Be(new UIElementInfo("Save", "Button", "SaveBtn", new BoundsDto(10, 20, 80, 24), IsEnabled: false, IsOffscreen: false));
    }

    [Fact]
    public void GetUIElements_EmptyNameAndAutomationId_AreNull()
    {
        // Arrange
        Window(new FakeUiaNode("Pane", "") { AutomationId = "" });

        // Act
        var element = CreateSut().GetUIElements(Handle).Elements!.Single();

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
}
