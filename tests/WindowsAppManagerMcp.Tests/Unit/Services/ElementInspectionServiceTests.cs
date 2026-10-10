namespace WindowsAppManagerMcp.Tests.Unit.Services;

/// <summary>
/// Unit tests for ElementInspectionService (get_focused_element and get_element_at_point), over an
/// in-memory UI Automation tree and mocked window wrappers. No real window is read.
/// </summary>
public class ElementInspectionServiceTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);
    private static readonly nint TopLevel = 0x5000;
    private static readonly nint ChildWindow = 0x5001;

    private readonly FakeUiaTree _tree = new();
    private readonly InlineUiaWorker _worker = new();
    private readonly Mock<INativeHitTestWrapper> _hitTest = new();
    private readonly Mock<INativeWindowWrapper> _windows = new();
    private readonly Mock<IMonitorService> _monitors = new();

    public ElementInspectionServiceTests()
    {
        _monitors.Setup(m => m.GetAllMonitors()).Returns(
        [
            new MonitorInfo(0, @"\\.\DISPLAY1", true, new MonitorRect(0, 0, 1920, 1080), new MonitorRect(0, 0, 1920, 1040), 1.0),
            new MonitorInfo(1, @"\\.\DISPLAY2", false, new MonitorRect(-2560, -200, 2560, 1440), new MonitorRect(-2560, -200, 2560, 1400), 1.5),
        ]);
        _hitTest.Setup(h => h.GetRootWindow(ChildWindow)).Returns(TopLevel);
        _hitTest.Setup(h => h.GetRootWindow(TopLevel)).Returns(TopLevel);
        _windows.Setup(w => w.GetWindowText(TopLevel)).Returns("Sign in - App");
        _windows.Setup(w => w.GetWindowThreadProcessId(TopLevel)).Returns(4242u);
        _windows.Setup(w => w.GetProcessName(4242u)).Returns("app");
        _windows.Setup(w => w.GetProcessName(777u)).Returns("webview");
    }

    private ElementInspectionService CreateSut(IUiaWorker? worker = null) =>
        new(_tree, worker ?? _worker, _hitTest.Object, _windows.Object, _monitors.Object);

    /// <summary>
    /// Window "Sign in" (hwnd TopLevel) > Pane "Form" (hwnd ChildWindow) > Group "Credentials" > Edit "Email"
    /// </summary>
    private FakeUiaNode BuildForm(FakeUiaNode? field = null)
    {
        field ??= new FakeUiaNode("Edit", "Email")
        {
            AutomationId = "EmailBox",
            ClassName = "TextBox",
            FrameworkId = "WPF",
            ProcessId = 4242,
            Bounds = new UiaRect(100.5, 200, 300, 24),
            IsKeyboardFocusable = true,
            HasKeyboardFocus = true,
            ActionPatterns = ["Value"],
            Value = "user@example.com",
        };

        new FakeUiaNode("Window", "Sign in") { NativeWindowHandle = TopLevel, ClassName = "Window", ProcessId = 4242 }.With(
            new FakeUiaNode("Pane", "Form") { NativeWindowHandle = ChildWindow, ClassName = "FormPane", ProcessId = 4242 }.With(
                new FakeUiaNode("Group", "Credentials") { ClassName = "GroupBox", ProcessId = 4242 }.With(field)));
        return field;
    }

    #region get_focused_element

    [Fact]
    public void GetFocusedElement_ReturnsTheElementWithItsWindowAndProcess()
    {
        // Arrange
        _tree.Focused = BuildForm();

        // Act
        var result = CreateSut().GetFocusedElement(Timeout);

        // Assert
        result.Success.Should().BeTrue();
        result.ErrorCode.Should().Be(UIElementErrorCode.None);
        result.Element.Should().Be(new UIElementDetails(
            Name: "Email",
            ControlType: "Edit",
            AutomationId: "EmailBox",
            ClassName: "TextBox",
            FrameworkId: "WPF",
            Bounds: new BoundsDto(100, 200, 301, 24),
            IsEnabled: true,
            IsOffscreen: false,
            IsKeyboardFocusable: true,
            HasKeyboardFocus: true,
            IsPassword: false,
            Patterns: result.Element!.Patterns,
            Value: "user@example.com",
            ValueTruncated: false));
        result.Element.Patterns.Should().Equal("Value");
        result.WindowHandle.Should().Be(TopLevel, "the nearest window above the element is the Form pane, whose root is the top-level window");
        result.WindowTitle.Should().Be("Sign in - App");
        result.ProcessId.Should().Be(4242);
        result.ProcessName.Should().Be("app");
    }

    [Fact]
    public void GetFocusedElement_RunsOnTheWorkerWithTheTimeout()
    {
        // Arrange
        _tree.Focused = BuildForm();

        // Act
        CreateSut().GetFocusedElement(Timeout);

        // Assert
        _worker.Calls.Should().Be(1);
        _worker.LastToken.CanBeCanceled.Should().BeTrue("the read has a deadline");
    }

    [Fact]
    public void GetFocusedElement_CountsTheReadAgainstTheForegroundWindowsProcess()
    {
        // Arrange - keyboard focus is in the foreground window
        _tree.Focused = BuildForm();
        _windows.Setup(w => w.GetForegroundWindow()).Returns(TopLevel);

        // Act
        CreateSut().GetFocusedElement(Timeout);

        // Assert
        _worker.LastTargetProcessId.Should().Be(4242);
    }

    [Fact]
    public void GetFocusedElement_WhileEarlierReadsOfTheApplicationAreStuck_ReturnsOperationTimedOutWithoutWaiting()
    {
        // Arrange
        var worker = new Mock<IUiaWorker>();
        worker.Setup(w => w.Run(It.IsAny<int>(), It.IsAny<Func<CancellationToken, FocusedElementResult>>(), It.IsAny<CancellationToken>(), It.IsAny<TimeSpan>()))
            .Throws(new UiaReadRefusedException("UI Automation is not responding for the application (process 4242)."));

        // Act
        var result = CreateSut(worker.Object).GetFocusedElement(Timeout);

        // Assert - nothing was waited for, so the message does not claim a wait
        result.ErrorCode.Should().Be(UIElementErrorCode.OperationTimedOut);
        result.Error.Should().Contain("process 4242").And.NotContain("Waited");
    }

    [Fact]
    public void GetFocusedElement_PasswordField_NeverReadsTheValue()
    {
        // Arrange
        _tree.Focused = BuildForm(new FakeUiaNode("Edit", "Password")
        {
            IsPassword = true,
            IsKeyboardFocusable = true,
            ActionPatterns = ["Value"],
            Value = "hunter2",
        });

        // Act
        var result = CreateSut().GetFocusedElement(Timeout);

        // Assert
        result.Success.Should().BeTrue();
        result.Element!.IsPassword.Should().BeTrue();
        result.Element.Value.Should().BeNull();
        _tree.Reads.Should().NotContain(r => r.StartsWith("Value:"));
    }

    [Fact]
    public void GetFocusedElement_WithoutTheValuePattern_DoesNotReadTheValue()
    {
        // Arrange
        _tree.Focused = BuildForm(new FakeUiaNode("Button", "Next") { ActionPatterns = ["Invoke"], Value = "ignored" });

        // Act
        var result = CreateSut().GetFocusedElement(Timeout);

        // Assert
        result.Element!.Value.Should().BeNull();
        _tree.Reads.Should().NotContain(r => r.StartsWith("Value:"));
    }

    [Fact]
    public void GetFocusedElement_LongValue_IsCutTo200Characters()
    {
        // Arrange
        _tree.Focused = BuildForm(new FakeUiaNode("Document", "Body") { ActionPatterns = ["Value"], Value = new string('a', 500) });

        // Act
        var result = CreateSut().GetFocusedElement(Timeout);

        // Assert
        result.Element!.Value.Should().HaveLength(ElementInspectionService.MaxValueLength);
        result.Element.ValueTruncated.Should().BeTrue();
    }

    [Fact]
    public void GetFocusedElement_LongValue_IsNotCutInsideASurrogatePair()
    {
        // Arrange - an emoji (two UTF-16 code units) straddles the 200-character limit
        var value = new string('a', ElementInspectionService.MaxValueLength - 1) + "😀" + "tail";
        _tree.Focused = BuildForm(new FakeUiaNode("Edit", "Chat") { ActionPatterns = ["Value"], Value = value });

        // Act
        var result = CreateSut().GetFocusedElement(Timeout);

        // Assert
        result.Element!.Value.Should().Be(new string('a', ElementInspectionService.MaxValueLength - 1));
        result.Element.ValueTruncated.Should().BeTrue();
    }

    [Fact]
    public void GetFocusedElement_ElementInsideATopLevelWindowElement_UsesThatWindow()
    {
        // Arrange - the element is directly in the window, which has the handle itself
        var field = new FakeUiaNode("Edit", "Search") { ProcessId = 4242 };
        new FakeUiaNode("Window", "Sign in") { NativeWindowHandle = TopLevel }.With(field);
        _tree.Focused = field;

        // Act
        var result = CreateSut().GetFocusedElement(Timeout);

        // Assert
        result.WindowHandle.Should().Be(TopLevel);
    }

    [Fact]
    public void GetFocusedElement_WithoutAWindowAbove_ReportsNoWindowButTheElementsProcess()
    {
        // Arrange - no element up the chain has a window handle
        _tree.Focused = new FakeUiaNode("Edit", "Floating") { ProcessId = 777 };

        // Act
        var result = CreateSut().GetFocusedElement(Timeout);

        // Assert
        result.Success.Should().BeTrue();
        result.WindowHandle.Should().BeNull();
        result.WindowTitle.Should().BeNull();
        result.ProcessId.Should().Be(777);
        result.ProcessName.Should().Be("webview");
    }

    [Fact]
    public void GetFocusedElement_WhenTheWindowSearchFails_StillReturnsTheElement()
    {
        // Arrange
        var field = BuildForm();
        _tree.Focused = field;
        field.ParentException = FakeUiaTree.Vanished();

        // Act
        var result = CreateSut().GetFocusedElement(Timeout);

        // Assert
        result.Success.Should().BeTrue();
        result.Element!.Name.Should().Be("Email");
        result.WindowHandle.Should().BeNull();
    }

    [Fact]
    public void GetFocusedElement_WhenNothingHasFocus_ReturnsElementNotFound()
    {
        // Act
        var result = CreateSut().GetFocusedElement(Timeout);

        // Assert
        result.Success.Should().BeFalse();
        result.ErrorCode.Should().Be(UIElementErrorCode.ElementNotFound);
        result.Element.Should().BeNull();
    }

    [Fact]
    public void GetFocusedElement_WhenTheElementVanishesWhileRead_ReturnsElementAccessFailed()
    {
        // Arrange
        _tree.Focused = BuildForm(new FakeUiaNode("Edit", "Gone") { ReadException = FakeUiaTree.Vanished() });

        // Act
        var result = CreateSut().GetFocusedElement(Timeout);

        // Assert
        result.Success.Should().BeFalse();
        result.ErrorCode.Should().Be(UIElementErrorCode.ElementAccessFailed);
    }

    [Fact]
    public void GetFocusedElement_WhenTheProviderFails_ReturnsOperationException()
    {
        // Arrange
        _tree.FocusedException = new System.Runtime.InteropServices.COMException("provider failed");

        // Act
        var result = CreateSut().GetFocusedElement(Timeout);

        // Assert
        result.Success.Should().BeFalse();
        result.ErrorCode.Should().Be(UIElementErrorCode.OperationException);
        result.Error.Should().Contain("provider failed");
    }

    [Fact]
    public void GetFocusedElement_WhenUiAutomationIsUnavailable_ReturnsAutomationUnavailable()
    {
        // Arrange
        _tree.Available = false;

        // Act
        var result = CreateSut().GetFocusedElement(Timeout);

        // Assert
        result.ErrorCode.Should().Be(UIElementErrorCode.AutomationUnavailable);
    }

    [Fact]
    public void GetFocusedElement_WhenUiAutomationDoesNotAnswerInTime_ReturnsOperationTimedOut()
    {
        // Arrange
        var worker = new Mock<IUiaWorker>();
        worker.Setup(w => w.Run(It.IsAny<int>(), It.IsAny<Func<CancellationToken, FocusedElementResult>>(), It.IsAny<CancellationToken>(), It.IsAny<TimeSpan>()))
            .Throws(new TimeoutException("UI Automation did not answer in time."));

        // Act
        var result = CreateSut(worker.Object).GetFocusedElement(Timeout);

        // Assert
        result.Success.Should().BeFalse();
        result.ErrorCode.Should().Be(UIElementErrorCode.OperationTimedOut);
        result.Error.Should().Contain("did not answer");
    }

    [Fact]
    public void GetFocusedElement_WhenTheCallIsCancelled_ThrowsInsteadOfReportingATimeout()
    {
        // Arrange
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        var worker = new Mock<IUiaWorker>();
        worker.Setup(w => w.Run(It.IsAny<int>(), It.IsAny<Func<CancellationToken, FocusedElementResult>>(), It.IsAny<CancellationToken>(), It.IsAny<TimeSpan>()))
            .Throws(new TimeoutException());

        // Act
        var act = () => CreateSut(worker.Object).GetFocusedElement(Timeout, cancelled.Token);

        // Assert - the SDK reports a cancelled request itself
        act.Should().Throw<OperationCanceledException>();
    }

    #endregion

    #region get_element_at_point

    [Fact]
    public void GetElementAtPoint_ReturnsTheElementAncestorsAndWindowAtThePoint()
    {
        // Arrange
        var field = BuildForm();
        _tree.AtPoint = (x, y) => (x, y) == (150, 210) ? field : null;
        _hitTest.Setup(h => h.WindowFromPoint(150, 210)).Returns(ChildWindow);

        // Act
        var result = CreateSut().GetElementAtPoint(150, 210, ancestors: 3, Timeout);

        // Assert
        result.Success.Should().BeTrue();
        result.X.Should().Be(150);
        result.Y.Should().Be(210);
        result.Element!.Name.Should().Be("Email");
        result.Element.Value.Should().Be("user@example.com");
        result.Ancestors!.Select(a => $"{a.ControlType} {a.Name} {a.ClassName}").Should().Equal(
            "Group Credentials GroupBox", "Pane Form FormPane", "Window Sign in Window");
        result.WindowHandle.Should().Be(TopLevel);
        result.WindowTitle.Should().Be("Sign in - App");
        result.ProcessId.Should().Be(4242);
        result.ProcessName.Should().Be("app");
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(1, 1)]
    [InlineData(2, 2)]
    [InlineData(10, 3)]
    [InlineData(-1, 0)]
    public void GetElementAtPoint_ReturnsAtMostTheRequestedAncestorsAndNeverAboveTheTopLevelWindow(int requested, int expected)
    {
        // Arrange
        var field = BuildForm();
        _tree.AtPoint = (_, _) => field;

        // Act
        var result = CreateSut().GetElementAtPoint(150, 210, requested, Timeout);

        // Assert
        result.Ancestors.Should().HaveCount(expected);
    }

    [Fact]
    public void GetElementAtPoint_ClampsAncestorsToTen()
    {
        // Arrange - a chain of 15 groups
        var leaf = new FakeUiaNode("Button", "Deep");
        var current = leaf;
        for (var i = 0; i < 15; i++)
        {
            var parent = new FakeUiaNode("Group", $"G{i}");
            parent.With(current);
            current = parent;
        }

        _tree.AtPoint = (_, _) => leaf;

        // Act
        var result = CreateSut().GetElementAtPoint(10, 10, 50, Timeout);

        // Assert
        result.Ancestors.Should().HaveCount(ElementInspectionService.MaxAncestors);
    }

    [Fact]
    public void GetElementAtPoint_WhenAnAncestorCannotBeRead_ReturnsTheAncestorsReadSoFar()
    {
        // Arrange
        var field = BuildForm();
        field.Parent!.Parent!.ReadException = FakeUiaTree.Vanished();
        _tree.AtPoint = (_, _) => field;

        // Act
        var result = CreateSut().GetElementAtPoint(150, 210, 3, Timeout);

        // Assert
        result.Success.Should().BeTrue();
        result.Ancestors!.Select(a => a.Name).Should().Equal("Credentials");
    }

    [Fact]
    public void GetElementAtPoint_OnASecondMonitorWithNegativeCoordinates_Works()
    {
        // Arrange
        var field = BuildForm();
        _tree.AtPoint = (x, y) => (x, y) == (-1000, -100) ? field : null;

        // Act
        var result = CreateSut().GetElementAtPoint(-1000, -100, 0, Timeout);

        // Assert
        result.Success.Should().BeTrue();
        result.Element!.Name.Should().Be("Email");
    }

    [Theory]
    [InlineData(5000, 100)]
    [InlineData(1920, 0)]
    [InlineData(100, -1)]
    [InlineData(-2561, 0)]
    public void GetElementAtPoint_OffEveryMonitor_ReturnsCoordinatesOutOfBoundsWithoutReadingUiAutomation(int x, int y)
    {
        // Act
        var result = CreateSut().GetElementAtPoint(x, y, 3, Timeout);

        // Assert
        result.Success.Should().BeFalse();
        result.ErrorCode.Should().Be(UIElementErrorCode.CoordinatesOutOfBounds);
        result.X.Should().Be(x);
        result.Y.Should().Be(y);
        _worker.Calls.Should().Be(0);
    }

    [Fact]
    public void GetElementAtPoint_WhenNoWindowIsHit_FallsBackToTheElementsWindow()
    {
        // Arrange - a defensive path: WindowFromPoint practically always hits a window, at least the desktop
        var field = BuildForm();
        _tree.AtPoint = (_, _) => field;
        _hitTest.Setup(h => h.WindowFromPoint(It.IsAny<int>(), It.IsAny<int>())).Returns(nint.Zero);

        // Act
        var result = CreateSut().GetElementAtPoint(150, 210, 0, Timeout);

        // Assert
        result.WindowHandle.Should().Be(TopLevel);
    }

    [Fact]
    public void GetElementAtPoint_WhenTheHitTestFails_FallsBackToTheElementsWindow()
    {
        // Arrange
        var field = BuildForm();
        _tree.AtPoint = (_, _) => field;
        _hitTest.Setup(h => h.WindowFromPoint(It.IsAny<int>(), It.IsAny<int>())).Throws(new InvalidOperationException("hit test"));

        // Act
        var result = CreateSut().GetElementAtPoint(150, 210, 0, Timeout);

        // Assert
        result.Success.Should().BeTrue();
        result.WindowHandle.Should().Be(TopLevel);
        _worker.LastTargetProcessId.Should().Be(0);
    }

    [Fact]
    public void GetElementAtPoint_CountsTheReadAgainstTheProcessOfTheWindowAtThePoint()
    {
        // Arrange - the window is hit-tested before the read, which can hang in that window's application
        var field = BuildForm();
        _tree.AtPoint = (_, _) => field;
        _hitTest.Setup(h => h.WindowFromPoint(150, 210)).Returns(ChildWindow);

        // Act
        CreateSut().GetElementAtPoint(150, 210, 0, Timeout);

        // Assert
        _worker.LastTargetProcessId.Should().Be(4242);
    }

    [Fact]
    public void GetElementAtPoint_WithNoElementThere_ReturnsElementNotFound()
    {
        // Act
        var result = CreateSut().GetElementAtPoint(150, 210, 3, Timeout);

        // Assert
        result.Success.Should().BeFalse();
        result.ErrorCode.Should().Be(UIElementErrorCode.ElementNotFound);
    }

    [Fact]
    public void GetElementAtPoint_WhenUiAutomationDoesNotAnswerInTime_ReturnsOperationTimedOut()
    {
        // Arrange - FromPoint can hang on an unresponsive provider
        var worker = new Mock<IUiaWorker>();
        worker.Setup(w => w.Run(It.IsAny<int>(), It.IsAny<Func<CancellationToken, ElementAtPointResult>>(), It.IsAny<CancellationToken>(), It.IsAny<TimeSpan>()))
            .Throws(new TimeoutException("UI Automation did not answer in time."));

        // Act
        var result = CreateSut(worker.Object).GetElementAtPoint(150, 210, 3, Timeout);

        // Assert
        result.Success.Should().BeFalse();
        result.ErrorCode.Should().Be(UIElementErrorCode.OperationTimedOut);
        result.X.Should().Be(150);
    }

    [Fact]
    public void GetElementAtPoint_WhenUiAutomationIsUnavailable_ReturnsAutomationUnavailable()
    {
        // Arrange
        _tree.Available = false;

        // Act
        var result = CreateSut().GetElementAtPoint(150, 210, 3, Timeout);

        // Assert
        result.ErrorCode.Should().Be(UIElementErrorCode.AutomationUnavailable);
    }

    #endregion
}
