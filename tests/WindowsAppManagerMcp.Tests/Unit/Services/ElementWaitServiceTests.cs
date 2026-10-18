namespace WindowsAppManagerMcp.Tests.Unit.Services;

/// <summary>
/// wait_for_element's service (roadmap M3.8) over an in-memory UI Automation tree, with the real search
/// (ElementSearchService) and element registry, and a mocked window wait: each condition, criteria and refs, the window
/// wait first, the early ends, the timeout, the backoff, the warm-up once, progress and cancellation, and that a password
/// field's value is never read. The tree changes at given times on a manual clock; nothing reads the real desktop.
/// </summary>
public class ElementWaitServiceTests
{
    private static readonly nint Handle = 0x7200;
    private static readonly WindowSelector Editor = new() { ProcessName = "editor" };

    private readonly FakeUiaTree _tree = new();
    private readonly InlineUiaWorker _worker = new();
    private readonly Mock<INativeWindowWrapper> _windows = new();
    private readonly Mock<IWindowWaitService> _windowWait = new();
    private readonly Mock<IAccessibilityWarmup> _warmup = new();
    private readonly ElementRegistry _registry;
    private readonly ElementSearchService _search;
    private readonly ManualClock _clock = new();
    private readonly long _start;
    private readonly List<(long AtMs, Action Change)> _changes = [];
    private WindowConditionRequest? _windowRequest;

    public ElementWaitServiceTests()
    {
        _start = _clock.GetTimestamp();
        _windows.Setup(w => w.IsWindow(Handle)).Returns(true);
        _windows.Setup(w => w.GetWindowThreadProcessId(Handle)).Returns(300u);
        _windows.Setup(w => w.RespondsWithin(Handle, It.IsAny<uint>())).Returns(true);
        _registry = new ElementRegistry(_tree, _worker, _windows.Object, ElementRegistry.DefaultCapacity, 1, TimeProvider.System);
        _search = new ElementSearchService(_tree, _worker, _registry, _windows.Object, _warmup.Object);
        WindowWaitReturns(new WindowConditionResult
        {
            Satisfied = true, MatchCount = 1, ElapsedMs = 0, Window = TestDataFactory.CreateWindowInfo((int)Handle, "Editor", "editor", 300)
        });
    }

    /// <summary>The delays the wait asked for, in order.</summary>
    private List<TimeSpan> Delays { get; } = [];

    private long NowMs => (long)_clock.GetElapsedTime(_start).TotalMilliseconds;

    private void At(long atMs, Action change) => _changes.Add((atMs, change));

    private void WindowWaitReturns(WindowConditionResult result) =>
        _windowWait.Setup(w => w.WaitForConditionAsync(It.IsAny<WindowConditionRequest>(), It.IsAny<IProgress<OperationProgress>?>(), It.IsAny<CancellationToken>()))
            .Callback((WindowConditionRequest request, IProgress<OperationProgress>? _, CancellationToken _) => _windowRequest = request)
            .ReturnsAsync(result);

    private ElementWaitService CreateSut() =>
        new(_search, _registry, _windowWait.Object, _tree, _worker, _windows.Object, _clock, DelayAsync);

    private Task<WaitForElementResult> WaitAsync(
        ElementQuery? criteria = null,
        ElementWaitCondition condition = ElementWaitCondition.Exists,
        string? expected = null,
        int timeoutMs = 5000,
        string? elementRef = null,
        IProgress<OperationProgress>? progress = null,
        CancellationToken cancellationToken = default,
        int stableMs = 0) =>
        CreateSut().WaitAsync(new ElementWaitRequest
        {
            Window = elementRef == null && criteria?.WithinRef == null ? Editor : null,
            Ref = elementRef,
            Criteria = elementRef == null ? criteria ?? Criteria(name: "OK") : null,
            Condition = condition,
            Expected = expected,
            TimeoutMs = timeoutMs,
            StableMs = stableMs
        }, progress, cancellationToken);

    private static ElementQuery Criteria(string? name = null, string? automationId = null, string[]? controlTypes = null, string? withinRef = null) =>
        new(nint.Zero, withinRef, name, AutomationId: automationId, ControlTypes: controlTypes);

    /// <summary>A request for criteria in <paramref name="window"/> (default: the editor's selector).</summary>
    private static ElementWaitRequest Request(
        ElementQuery criteria, ElementWaitCondition condition, int timeoutMs = 5000, WindowSelector? window = null) => new()
    {
        Window = window ?? Editor, Criteria = criteria, Condition = condition, TimeoutMs = timeoutMs
    };

    /// <summary>Lets the time pass and applies the changes due by then.</summary>
    private Task DelayAsync(TimeSpan wait, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Delays.Add(wait);
        _clock.Advance(wait);
        foreach (var change in _changes.Where(c => c.AtMs <= NowMs).ToList())
        {
            _changes.Remove(change);
            change.Change();
        }

        return Task.CompletedTask;
    }

    /// <summary>Window "Editor" with a Pane "Body"; the nodes the tests add go into the pane.</summary>
    private FakeUiaNode BuildWindow(params FakeUiaNode[] children)
    {
        var body = new FakeUiaNode("Pane", "Body").With(children);
        _tree.WithWindow(Handle, new FakeUiaNode("Window", "Editor").With(body));
        return body;
    }

    private static FakeUiaNode Button(string name, bool enabled = true) =>
        new("Button", name) { AutomationId = name + "Btn", Bounds = new UiaRect(100, 40, 80, 24), ActionPatterns = ["Invoke"], IsEnabled = enabled };

    /// <summary>A ref for the node, as find_ui_elements would give it.</summary>
    private async Task<string> RefOf(string automationId)
    {
        var found = await _search.FindAsync(new ElementQuery(Handle, AutomationId: automationId, VisibleOnly: false));
        return found.Elements!.Single().Ref!;
    }

    #region The window

    [Fact]
    public async Task TheWindow_IsWaitedForFirst_WithWaitForWindowsExists_AndTheWholeTimeout()
    {
        BuildWindow(Button("OK"));

        var result = await WaitAsync(timeoutMs: 7000);

        result.Satisfied.Should().BeTrue();
        result.WindowHandle.Should().Be(Handle);
        _windowRequest.Should().BeEquivalentTo(new WindowConditionRequest
        {
            Window = Editor, Condition = WindowWaitCondition.Exists, StableMs = 0, TimeoutMs = 7000
        });
    }

    [Fact]
    public async Task AWindowThatNeverAppears_IsWaitTimedOut_WithNoElementChecked()
    {
        WindowWaitReturns(new WindowConditionResult { TimedOut = true, ErrorCode = "WaitTimedOut", ElapsedMs = 5000 });

        var result = await WaitAsync();

        result.Success.Should().BeFalse();
        result.TimedOut.Should().BeTrue();
        result.ErrorCode.Should().Be("WaitTimedOut");
        result.WindowHandle.Should().BeNull();
        result.Polls.Should().Be(0);
        result.Error.Should().Contain("No window matching processName 'editor' appeared");
        result.LastObservedState.Should().Contain("no window matching");
    }

    [Fact]
    public async Task SeveralWindows_AreAmbiguousWindow_WithCandidates()
    {
        var first = TestDataFactory.CreateWindowInfo(0x10, "a - Editor", "editor", 300);
        var second = TestDataFactory.CreateWindowInfo(0x20, "b - Editor", "editor", 301);
        WindowWaitReturns(new WindowConditionResult { Satisfied = true, MatchCount = 2, Candidates = [first, second] });

        var result = await WaitAsync();

        result.Success.Should().BeFalse();
        result.TimedOut.Should().BeFalse();
        result.ErrorCode.Should().Be("AmbiguousWindow");
        result.Candidates!.Select(c => c.Handle).Should().Equal(0x10, 0x20);
        result.Polls.Should().Be(0);
    }

    [Fact]
    public async Task AWindowWaitThatEndsEarly_PassesItsCodeOn()
    {
        WindowWaitReturns(new WindowConditionResult { ErrorCode = "InvalidHandle", Error = "window.handle 42 is no window" });

        var result = await WaitAsync();

        result.ErrorCode.Should().Be("InvalidHandle");
        result.Error.Should().Contain("is no window");
        result.TimedOut.Should().BeFalse();
    }

    [Fact]
    public async Task Gone_DoesNotWaitForTheWindow_AndNoMatchingWindowSatisfiesIt_WithAWarning()
    {
        WindowWaitReturns(new WindowConditionResult { TimedOut = true, ErrorCode = "WaitTimedOut" });

        var result = await WaitAsync(condition: ElementWaitCondition.Gone);

        _windowRequest!.TimeoutMs.Should().Be(0, "gone checks the window once");
        result.Success.Should().BeTrue();
        result.Satisfied.Should().BeTrue();
        result.Warning.Should().Contain("No window matching processName 'editor' is open");
    }

    [Fact]
    public async Task Gone_ByAHandleThatHadAlreadyClosed_IsSatisfied_WithAWarning()
    {
        // Arrange - the window wait ends with InvalidHandle, and Win32 confirms that handle 42 is no window
        WindowWaitReturns(new WindowConditionResult
        {
            ErrorCode = "InvalidHandle", Error = "window.handle 42 is no window (it closed), so it cannot become exists."
        });

        // Act
        var result = await CreateSut().WaitAsync(Request(Criteria(name: "Loading"), ElementWaitCondition.Gone, window: new WindowSelector { Handle = 42 }));

        // Assert
        result.Success.Should().BeTrue();
        result.Satisfied.Should().BeTrue();
        result.ErrorCode.Should().BeNull();
        result.Polls.Should().Be(0);
        result.Warning.Should().Contain("Window 42 had already closed when the wait started").And.NotContain("exists");
    }

    [Fact]
    public async Task Gone_ByAHandleThatWin32StillKnows_IsNotTakenAsClosed()
    {
        // The window wait's InvalidHandle is checked against IsWindow before it counts as a closed window
        _windows.Setup(w => w.IsWindow(42)).Returns(true);
        WindowWaitReturns(new WindowConditionResult { ErrorCode = "InvalidHandle", Error = "window.handle 42 is no window" });

        var result = await CreateSut().WaitAsync(Request(Criteria(name: "Loading"), ElementWaitCondition.Gone, window: new WindowSelector { Handle = 42 }));

        result.Satisfied.Should().BeFalse();
        result.ErrorCode.Should().Be("InvalidHandle");
    }

    [Fact]
    public async Task Gone_ByAHandleThatNoLongerMatchesItsFilters_IsSatisfied_WithAWarningAboutThatWindow()
    {
        // The handle names a window that does not match the filters: the window wait times out at once
        WindowWaitReturns(new WindowConditionResult { TimedOut = true, ErrorCode = "WaitTimedOut" });

        var result = await CreateSut().WaitAsync(Request(Criteria(name: "Loading"), ElementWaitCondition.Gone,
            window: new WindowSelector { Handle = 42, TitleContains = "Exporting" }));

        result.Satisfied.Should().BeTrue();
        result.Warning.Should().Contain("Window 42").And.Contain("get_window_info").And.NotContain("includeUntitled");
    }

    #endregion

    #region exists and gone

    [Fact]
    public async Task Exists_AnElementThatAppearsLater_IsReturnedWithItsRef()
    {
        // Arrange - the OK button appears 300 ms into the wait
        var body = BuildWindow();
        At(300, () => body.With(Button("OK")));

        // Act
        var result = await WaitAsync();

        // Assert
        result.Success.Should().BeTrue();
        result.Satisfied.Should().BeTrue();
        result.TimedOut.Should().BeFalse();
        result.Element!.Name.Should().Be("OK");
        result.Element.Ref.Should().MatchRegex("^w1e[0-9]+$");
        result.Element.ClickPoint.Should().Be(new ScreenPoint(140, 52));
        result.MatchCount.Should().Be(1);
        result.ElapsedMs.Should().BeGreaterThanOrEqualTo(300);
        result.Polls.Should().BeGreaterThan(1);
        result.LastObservedState.Should().BeNull();
        result.ErrorCode.Should().BeNull();
    }

    [Fact]
    public async Task Exists_AnElementAlreadyThere_IsSatisfiedAtTheFirstCheck_WithoutADelay()
    {
        BuildWindow(Button("OK"));

        var result = await WaitAsync();

        result.Satisfied.Should().BeTrue();
        result.Polls.Should().Be(1);
        Delays.Should().BeEmpty();
    }

    [Fact]
    public async Task Exists_CountsAnOffscreenOrDisabledElement_SinceTheConditionDecidesWhatCounts()
    {
        BuildWindow(new FakeUiaNode("Button", "OK") { IsOffscreen = true, IsEnabled = false });

        var result = await WaitAsync();

        result.Satisfied.Should().BeTrue();
        result.Element!.IsOffscreen.Should().BeTrue();
    }

    [Fact]
    public async Task Exists_TimesOut_AsWaitTimedOut_DistinctFromTheElementNotBeingThere()
    {
        BuildWindow(Button("Cancel"));

        var result = await WaitAsync(timeoutMs: 1000);

        result.Success.Should().BeFalse();
        result.TimedOut.Should().BeTrue();
        result.ErrorCode.Should().Be("WaitTimedOut");
        result.MatchCount.Should().Be(0);
        result.Element.Should().BeNull();
        result.LastObservedState.Should().Be("no element matches name 'OK'");
        result.Error.Should().StartWith("No element matching name 'OK' appeared within 1000 ms").And.Contain("Nothing was changed");
        result.ElapsedMs.Should().BeGreaterThanOrEqualTo(1000);
    }

    [Fact]
    public async Task Gone_AnElementThatDisappears_SatisfiesIt()
    {
        var spinner = new FakeUiaNode("ProgressBar", "Loading");
        BuildWindow(spinner);
        At(450, spinner.Vanish);

        var result = await WaitAsync(Criteria(name: "Loading"), ElementWaitCondition.Gone);

        result.Satisfied.Should().BeTrue();
        result.Element.Should().BeNull();
        result.MatchCount.Should().Be(0);
        result.ElapsedMs.Should().BeGreaterThanOrEqualTo(450);
        result.Warning.Should().BeNull();
    }

    [Fact]
    public async Task Gone_TimesOut_WithTheElementStillThere()
    {
        BuildWindow(new FakeUiaNode("ProgressBar", "Loading"));

        var result = await WaitAsync(Criteria(name: "Loading"), ElementWaitCondition.Gone, timeoutMs: 800);

        result.TimedOut.Should().BeTrue();
        result.Element!.Name.Should().Be("Loading");
        result.LastObservedState.Should().StartWith("1 element still matches name 'Loading': ProgressBar 'Loading'");
        result.Error.Should().StartWith("Elements matching name 'Loading' were still there");
    }

    [Fact]
    public async Task Gone_IsNotSatisfiedByASearchThatDidNotCoverTheWindow()
    {
        // Arrange - a wide list (one part of the search) whose provider fails, so the search finds nothing but did not
        // cover the window: the element may still be in the list
        var list = new FakeUiaNode("List", "Rows").With(Enumerable.Range(1, 60).Select(i => new FakeUiaNode("ListItem", $"Row {i}")).ToArray());
        list.FindAllException = new InvalidOperationException("provider failed");
        BuildWindow(list);

        // Act
        var result = await WaitAsync(Criteria(name: "Loading"), ElementWaitCondition.Gone, timeoutMs: 500);

        // Assert
        result.Satisfied.Should().BeFalse();
        result.TimedOut.Should().BeTrue();
        result.LastObservedState.Should().Contain("did not cover the whole window");
    }

    /// <summary>The window closes: UI Automation can no longer open it, and Win32 no longer knows its handle.</summary>
    private void CloseWindow()
    {
        _tree.FromHandleException = FakeUiaTree.Vanished("window");
        _windows.Setup(w => w.IsWindow(Handle)).Returns(false);
    }

    [Fact]
    public async Task Gone_AWindowThatCloses_SatisfiesIt_WithAWarning()
    {
        BuildWindow(new FakeUiaNode("ProgressBar", "Loading"));
        At(300, CloseWindow);

        var result = await WaitAsync(Criteria(name: "Loading"), ElementWaitCondition.Gone);

        result.Satisfied.Should().BeTrue();
        result.Warning.Should().Contain("closed");
    }

    [Fact]
    public async Task Gone_AWindowUIAutomationCannotOpen_ThatWin32StillKnows_IsNotGone()
    {
        // Arrange - UI Automation fails to open the window (a provider that restarts disconnects), but it is still there
        BuildWindow(new FakeUiaNode("ProgressBar", "Loading"));
        At(300, () => _tree.FromHandleException = FakeUiaTree.Vanished("window"));

        // Act
        var result = await WaitAsync(Criteria(name: "Loading"), ElementWaitCondition.Gone, timeoutMs: 800);

        // Assert
        result.Satisfied.Should().BeFalse();
        result.TimedOut.Should().BeTrue();
        result.LastObservedState.Should().StartWith("UI Automation could not read the window");
    }

    [Fact]
    public async Task Gone_AWindowWhoseHandleNowNamesAWindowOfAnotherProcess_HasClosed()
    {
        BuildWindow(new FakeUiaNode("ProgressBar", "Loading"));
        At(300, () =>
        {
            _tree.FromHandleException = FakeUiaTree.Vanished("window");
            _windows.Setup(w => w.GetWindowThreadProcessId(Handle)).Returns(999u);
        });

        var result = await WaitAsync(Criteria(name: "Loading"), ElementWaitCondition.Gone);

        result.Satisfied.Should().BeTrue();
        result.Warning.Should().Contain("closed");
    }

    [Fact]
    public async Task Gone_AWindowWhoseHandleNowNamesAWindowOfAnotherClass_HasClosed_WhileAFailedClassReadTellsNothing()
    {
        // Arrange - one wait where the handle comes to name a dialog of the same process, one where the class cannot be read
        BuildWindow(new FakeUiaNode("ProgressBar", "Loading"));
        _windows.Setup(w => w.GetClassName(Handle)).Returns("EditorWindow");
        At(300, () =>
        {
            _tree.FromHandleException = FakeUiaTree.Vanished("window");
            _windows.Setup(w => w.GetClassName(Handle)).Returns("#32770");
        });
        var reused = await WaitAsync(Criteria(name: "Loading"), ElementWaitCondition.Gone);
        _tree.FromHandleException = null;
        _windows.Setup(w => w.GetClassName(Handle)).Returns("EditorWindow");
        At(300, () =>
        {
            _tree.FromHandleException = FakeUiaTree.Vanished("window");
            _windows.Setup(w => w.GetClassName(Handle)).Returns(string.Empty);
        });

        // Act
        var unreadable = await WaitAsync(Criteria(name: "Loading"), ElementWaitCondition.Gone, timeoutMs: 800);

        // Assert
        reused.Satisfied.Should().BeTrue();
        reused.Warning.Should().Contain("closed");
        unreadable.Satisfied.Should().BeFalse();
        unreadable.LastObservedState.Should().StartWith("UI Automation could not read the window");
    }

    [Fact]
    public async Task AWindowThatCloses_EndsAnyOtherCondition_WithWindowNotFound_NotATimeout()
    {
        BuildWindow(Button("OK", enabled: false));
        At(300, CloseWindow);

        var result = await WaitAsync(condition: ElementWaitCondition.Enabled);

        result.Success.Should().BeFalse();
        result.TimedOut.Should().BeFalse();
        result.ErrorCode.Should().Be("WindowNotFound");
        result.Error.Should().Contain("closed");
        result.ElapsedMs.Should().BeLessThan(1000);
    }

    [Fact]
    public async Task AWindowUIAutomationCannotOpen_ThatWin32StillKnows_DoesNotEndAnyOtherCondition()
    {
        BuildWindow(Button("OK", enabled: false));
        At(300, () => _tree.FromHandleException = FakeUiaTree.Vanished("window"));

        var result = await WaitAsync(condition: ElementWaitCondition.Enabled, timeoutMs: 800);

        result.TimedOut.Should().BeTrue();
        result.ErrorCode.Should().Be("WaitTimedOut");
        result.LastObservedState.Should().StartWith("UI Automation could not read the window");
    }

    private static readonly nint Popup = 0x7300;

    /// <summary>
    /// The service over a search that covers the window's popups: the editor's process has an unowned 'Exporting' dialog,
    /// a window of its own (outside the editor's UI Automation tree) with a Cancel button.
    /// </summary>
    private ElementWaitService CreateSutWithADialog(Mock<IPopupService> popups, bool deniedByPolicy = false)
    {
        _windows.Setup(w => w.IsWindow(Popup)).Returns(true);
        _windows.Setup(w => w.GetWindowThreadProcessId(Popup)).Returns(300u);
        _windows.Setup(w => w.RespondsWithin(Popup, It.IsAny<uint>())).Returns(true);
        _tree.WithWindow(Popup, new FakeUiaNode("Window", "Exporting").With(Button("Cancel")));
        popups.Setup(p => p.GetPopupList(Handle)).Returns(new PopupList(
            [new PopupWindow(Popup, PopupKind.Dialog, "Exporting", "#32770", 300, nint.Zero, PopupRelation.SameProcess, null, false,
                IsDeniedByPolicy: deniedByPolicy)],
            false));
        var search = new ElementSearchService(_tree, _worker, _registry, _windows.Object, _warmup.Object, popups.Object);
        return new ElementWaitService(search, _registry, _windowWait.Object, _tree, _worker, _windows.Object, _clock, DelayAsync);
    }

    [Fact]
    public async Task Gone_SearchesTheWindowsPopupsOnEveryCheck_AlsoOnceTheAppIsNoLongerInFront()
    {
        // Arrange - the app loses the foreground 300 ms into the wait, while its dialog stays open
        BuildWindow();
        var popups = new Mock<IPopupService>();
        popups.Setup(p => p.IsInForeground(Handle)).Returns(true);
        var sut = CreateSutWithADialog(popups);
        At(300, () => popups.Setup(p => p.IsInForeground(Handle)).Returns(false));

        // Act
        var result = await sut.WaitAsync(Request(Criteria(name: "Cancel"), ElementWaitCondition.Gone, timeoutMs: 1000));

        // Assert - a search of the window alone would not have found the dialog's Cancel button
        result.Satisfied.Should().BeFalse();
        result.TimedOut.Should().BeTrue();
        result.LastObservedState.Should().Contain("Cancel");
    }

    [Fact]
    public async Task Gone_IsNotSatisfiedByASearchThatLeftAPopupOut()
    {
        // Arrange - the dialog belongs to an app the target policy denies, so the search leaves it out unread
        BuildWindow();
        var sut = CreateSutWithADialog(new Mock<IPopupService>(), deniedByPolicy: true);

        // Act
        var result = await sut.WaitAsync(Request(Criteria(name: "Cancel"), ElementWaitCondition.Gone, timeoutMs: 600));

        // Assert
        result.Satisfied.Should().BeFalse();
        result.TimedOut.Should().BeTrue();
        result.LastObservedState.Should().Contain("left out");
        _tree.Reads.Should().NotContain(r => r.Contains("Exporting"), "a popup of a denied app is never read");
    }

    [Fact]
    public async Task Gone_WithScopeWindow_SearchesTheWindowAlone_AsAsked()
    {
        BuildWindow();
        var sut = CreateSutWithADialog(new Mock<IPopupService>());

        var result = await sut.WaitAsync(Request(Criteria(name: "Cancel") with { Scope = ElementSearchScope.Window }, ElementWaitCondition.Gone));

        result.Satisfied.Should().BeTrue();
        result.Polls.Should().Be(1);
    }

    #endregion

    #region The state conditions

    [Fact]
    public async Task Enabled_AButtonThatEnablesAfterValidation_IsReturned()
    {
        var ok = Button("OK", enabled: false);
        BuildWindow(ok);
        At(600, () => ok.IsEnabled = true);

        var result = await WaitAsync(condition: ElementWaitCondition.Enabled);

        result.Satisfied.Should().BeTrue();
        result.Element!.IsEnabled.Should().BeTrue();
        result.ElapsedMs.Should().BeGreaterThanOrEqualTo(600);
    }

    [Fact]
    public async Task Enabled_TimesOut_WithTheElementAsLastSeen_AndWhatWasObserved()
    {
        BuildWindow(Button("OK", enabled: false));

        var result = await WaitAsync(condition: ElementWaitCondition.Enabled, timeoutMs: 1500);

        result.TimedOut.Should().BeTrue();
        result.ErrorCode.Should().Be("WaitTimedOut");
        result.Element!.Name.Should().Be("OK");
        result.Element.IsEnabled.Should().BeFalse();
        result.LastObservedState.Should().MatchRegex(@"^Button 'OK' \(w1e[0-9]+\) is disabled$");
        result.Error.Should().StartWith("No element matching name 'OK' became enabled within 1500 ms");
    }

    [Fact]
    public async Task AnyMatchSatisfiesIt_AndTheOneThatDoesIsReturned()
    {
        BuildWindow(Button("OK", enabled: false), new FakeUiaNode("Button", "OK") { AutomationId = "Second" });

        var result = await WaitAsync(condition: ElementWaitCondition.Enabled);

        result.Satisfied.Should().BeTrue();
        result.Element!.AutomationId.Should().Be("Second");
        result.MatchCount.Should().Be(2);
    }

    [Fact]
    public async Task Visible_AnElementScrolledIntoView_IsReturned()
    {
        var row = new FakeUiaNode("ListItem", "Row 40") { IsOffscreen = true };
        BuildWindow(row);
        At(300, () => row.IsOffscreen = false);

        var result = await WaitAsync(Criteria(name: "Row 40"), ElementWaitCondition.Visible);

        result.Satisfied.Should().BeTrue();
        result.Element!.IsOffscreen.Should().BeFalse();
    }

    [Fact]
    public async Task Visible_AnElementWithoutASize_IsNotVisible()
    {
        BuildWindow(new FakeUiaNode("Text", "Status") { Bounds = UiaRect.Empty });

        var result = await WaitAsync(Criteria(name: "Status"), ElementWaitCondition.Visible, timeoutMs: 300);

        result.TimedOut.Should().BeTrue();
        result.LastObservedState.Should().EndWith("has no size on screen");
    }

    [Fact]
    public async Task Focused_AnEditThatTakesFocus_IsReturned()
    {
        var edit = new FakeUiaNode("Edit", "Search") { IsKeyboardFocusable = true };
        BuildWindow(edit);
        At(300, () => edit.HasKeyboardFocus = true);

        var result = await WaitAsync(Criteria(name: "Search"), ElementWaitCondition.Focused);

        result.Satisfied.Should().BeTrue();
        result.Element!.HasKeyboardFocus.Should().BeTrue();
    }

    [Fact]
    public async Task NameEquals_ComparesTheWholeNameExactly_CaseIncluded()
    {
        var status = new FakeUiaNode("Text", "Saving...") { AutomationId = "Status" };
        BuildWindow(status);
        At(200, () => status.Name = "saved");
        At(500, () => status.Name = "Saved");

        var result = await WaitAsync(Criteria(automationId: "Status"), ElementWaitCondition.NameEquals, "Saved");

        result.Satisfied.Should().BeTrue();
        result.Element!.Name.Should().Be("Saved");
        result.ElapsedMs.Should().BeGreaterThanOrEqualTo(500, "'saved' differs in case");
    }

    [Fact]
    public async Task NameEquals_TimesOut_SayingWhatTheNameWas()
    {
        BuildWindow(new FakeUiaNode("Text", "Saving...") { AutomationId = "Status" });

        var result = await WaitAsync(Criteria(automationId: "Status"), ElementWaitCondition.NameEquals, "Saved", timeoutMs: 400);

        result.LastObservedState.Should().EndWith("its name is 'Saving...', not the expected one");
    }

    [Fact]
    public async Task ValueEquals_AnEditThatIsFilled_IsReturned()
    {
        var edit = new FakeUiaNode("Edit", "Path") { ActionPatterns = ["Value"], Value = "" };
        BuildWindow(edit);
        At(300, () => edit.Value = @"C:\temp\report.txt");

        var result = await WaitAsync(Criteria(name: "Path"), ElementWaitCondition.ValueEquals, @"C:\temp\report.txt");

        result.Satisfied.Should().BeTrue();
        result.Element!.Value.Should().Be(@"C:\temp\report.txt");
    }

    [Fact]
    public async Task ValueEquals_ALongValue_IsComparedInFull_NotByItsFirst200Characters()
    {
        var value = new string('a', 230) + "end";
        BuildWindow(new FakeUiaNode("Edit", "Notes") { ActionPatterns = ["Value"], Value = value });

        var same = await WaitAsync(Criteria(name: "Notes"), ElementWaitCondition.ValueEquals, value);
        var other = await WaitAsync(Criteria(name: "Notes"), ElementWaitCondition.ValueEquals, new string('a', 230) + "END", timeoutMs: 300);
        var shorter = await WaitAsync(Criteria(name: "Notes"), ElementWaitCondition.ValueEquals, new string('a', 210), timeoutMs: 300);

        same.Satisfied.Should().BeTrue();
        same.Element!.ValueTruncated.Should().BeTrue();
        other.Satisfied.Should().BeFalse();
        other.LastObservedState.Should().Contain("not the expected one");
        shorter.Satisfied.Should().BeFalse();
    }

    [Fact]
    public async Task ValueEquals_OnAPasswordField_EndsAtOnce_AndNeverReadsOrEchoesTheValue()
    {
        BuildWindow(new FakeUiaNode("Edit", "Password") { ActionPatterns = ["Value"], IsPassword = true, Value = "hunter2" });

        var result = await WaitAsync(Criteria(name: "Password"), ElementWaitCondition.ValueEquals, "hunter2");

        result.Success.Should().BeFalse();
        result.TimedOut.Should().BeFalse();
        result.ErrorCode.Should().Be("InvalidArguments");
        result.Polls.Should().Be(1);
        result.Element!.IsPassword.Should().BeTrue();
        result.Element.Value.Should().BeNull();
        _tree.Reads.Should().NotContain(r => r.StartsWith("Value:"), "a password field's value is never read");
        System.Text.Json.JsonSerializer.Serialize(result).Should().NotContain("hunter2");
    }

    [Fact]
    public async Task ValueEquals_SkipsAPasswordField_AmongOtherMatches()
    {
        BuildWindow(
            new FakeUiaNode("Edit", "Secret") { AutomationId = "Field", ActionPatterns = ["Value"], IsPassword = true, Value = "abc" },
            new FakeUiaNode("Edit", "Plain") { AutomationId = "Field", ActionPatterns = ["Value"], Value = "abc" });

        var result = await WaitAsync(Criteria(automationId: "Field"), ElementWaitCondition.ValueEquals, "abc");

        result.Satisfied.Should().BeTrue();
        result.Element!.Name.Should().Be("Plain");
        _tree.Reads.Should().NotContain("Value:Secret");
    }

    [Fact]
    public async Task ToggleState_ACheckBoxThatIsTicked_IsReturned()
    {
        var toggle = new FakeTogglePattern(UiaToggleState.Off);
        BuildWindow(new FakeUiaNode("CheckBox", "Wrap") { ActionPatterns = ["Toggle"] }.WithPattern<IUiaTogglePattern>(toggle));
        At(300, () => toggle.ToggleState = UiaToggleState.On);

        var result = await WaitAsync(Criteria(name: "Wrap"), ElementWaitCondition.ToggleState, "ON");

        result.Satisfied.Should().BeTrue();
        result.Element!.ToggleState.Should().Be(UiaToggleState.On);
    }

    [Fact]
    public async Task ToggleState_OnAnElementWithoutTheTogglePattern_SaysSo()
    {
        BuildWindow(Button("OK"));

        var result = await WaitAsync(condition: ElementWaitCondition.ToggleState, expected: "on", timeoutMs: 300);

        result.LastObservedState.Should().Contain("has no Toggle pattern");
    }

    #endregion

    #region Refs

    [Fact]
    public async Task Ref_Enabled_WaitsForTheElementTheRefNames_WithoutWaitingForAWindow()
    {
        var ok = Button("OK", enabled: false);
        BuildWindow(ok);
        var okRef = await RefOf("OKBtn");
        At(400, () => ok.IsEnabled = true);

        var result = await WaitAsync(condition: ElementWaitCondition.Enabled, elementRef: okRef);

        result.Satisfied.Should().BeTrue();
        result.Element!.Ref.Should().Be(okRef);
        result.Element.IsEnabled.Should().BeTrue();
        result.WindowHandle.Should().Be(Handle);
        _windowWait.Verify(w => w.WaitForConditionAsync(It.IsAny<WindowConditionRequest>(), It.IsAny<IProgress<OperationProgress>?>(),
            It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Ref_WhoseChecksRanOutOfTimeBeforeTheirReadWasAsked_TimesOut_WithoutBlamingTheApplication()
    {
        // Arrange - every check's deadline passed before its read was asked for: the worker starts none
        BuildWindow(Button("OK", enabled: false));
        var okRef = await RefOf("OKBtn");
        var worker = new ExpiringUiaWorker { Expired = true };
        var sut = new ElementWaitService(_search, _registry, _windowWait.Object, _tree, worker, _windows.Object, _clock, DelayAsync);

        // Act
        var result = await sut.WaitAsync(new ElementWaitRequest { Ref = okRef, Condition = ElementWaitCondition.Enabled, TimeoutMs = 600 });

        // Assert - the last state says the check could not tell, and why: nothing claims the application did not answer
        result.TimedOut.Should().BeTrue();
        worker.ExpiredCalls.Should().BeGreaterThan(0);
        result.LastObservedState.Should().StartWith("the last check could not tell (OperationTimedOut)")
            .And.Contain("was not asked").And.NotContain("did not answer");
    }

    [Fact]
    public async Task Ref_Gone_IsSatisfiedWhenItsElementIsGone()
    {
        var dialog = Button("Busy");
        BuildWindow(dialog);
        var busyRef = await RefOf("BusyBtn");
        At(300, dialog.Vanish);

        var result = await WaitAsync(condition: ElementWaitCondition.Gone, elementRef: busyRef);

        result.Satisfied.Should().BeTrue();
        result.Element.Should().BeNull();
    }

    [Fact]
    public async Task Ref_Gone_IsSatisfied_WhenALookAlikeTookTheElementsPlace_NotStillThere()
    {
        // Arrange - a status label whose search proved it the only one is re-rendered: the label the ref was given for is
        // gone, and a new one fits its description
        var body = BuildWindow();
        var loading = new FakeUiaNode("Text", "Loading") { AutomationId = "Status" };
        body.With(loading);
        var loadingRef = await RefOf("Status");
        At(300, () =>
        {
            loading.Vanish();
            body.With(new FakeUiaNode("Text", "Loading") { AutomationId = "Status" });
        });

        // Act
        var result = await WaitAsync(condition: ElementWaitCondition.Gone, elementRef: loadingRef);

        // Assert
        result.Satisfied.Should().BeTrue();
        result.Element.Should().BeNull();
        result.RefMoved.Should().BeTrue();
        result.Warning.Should().Contain("took its place").And.Contain("re-created");
    }

    [Fact]
    public async Task Ref_Enabled_FollowsARefMovedToAReCreatedElement_AndSaysSo()
    {
        // Arrange
        var body = BuildWindow();
        var ok = Button("OK", enabled: false);
        body.With(ok);
        var okRef = await RefOf("OKBtn");
        At(300, () =>
        {
            ok.Vanish();
            body.With(Button("OK"));
        });

        // Act
        var result = await WaitAsync(condition: ElementWaitCondition.Enabled, elementRef: okRef);

        // Assert
        result.Satisfied.Should().BeTrue();
        result.Element!.Ref.Should().Be(okRef);
        result.RefMoved.Should().BeTrue();
        result.Warning.Should().Contain("element_action does not act on it");
    }

    [Fact]
    public async Task Ref_ThatGoesStale_EndsAnyOtherCondition_WithElementStale_NotATimeout()
    {
        var ok = Button("OK", enabled: false);
        BuildWindow(ok);
        var okRef = await RefOf("OKBtn");
        At(300, ok.Vanish);

        var result = await WaitAsync(condition: ElementWaitCondition.Enabled, elementRef: okRef, timeoutMs: 10_000);

        result.Success.Should().BeFalse();
        result.TimedOut.Should().BeFalse();
        result.ErrorCode.Should().Be("ElementStale");
        result.ElapsedMs.Should().BeLessThan(1000);
        result.Element!.Name.Should().Be("OK", "the element as last seen");
    }

    [Fact]
    public async Task Ref_NeverIssued_IsElementStale_ForGoneToo()
    {
        // A ref this server session never issued (one from before a restart, or a made-up one) says nothing about any element
        BuildWindow();

        var enabled = await WaitAsync(condition: ElementWaitCondition.Enabled, elementRef: "w1e99");
        var gone = await WaitAsync(condition: ElementWaitCondition.Gone, elementRef: "w1e99");

        enabled.ErrorCode.Should().Be("ElementStale");
        enabled.Polls.Should().Be(0);
        gone.Success.Should().BeFalse();
        gone.Satisfied.Should().BeFalse();
        gone.ErrorCode.Should().Be("ElementStale");
        gone.Polls.Should().Be(0);
        gone.Error.Should().Contain("not issued");
    }

    [Fact]
    public async Task Ref_WhoseWindowHadClosed_SatisfiesGoneAtTheStart_WithAWarning()
    {
        BuildWindow(Button("Busy"));
        var busyRef = await RefOf("BusyBtn");
        _windows.Setup(w => w.IsWindow(Handle)).Returns(false);

        var result = await WaitAsync(condition: ElementWaitCondition.Gone, elementRef: busyRef);

        result.Satisfied.Should().BeTrue();
        result.Polls.Should().Be(0);
        result.Warning.Should().Contain("already gone when the wait started");
    }

    [Fact]
    public async Task Ref_ValueEquals_ReadsTheWholeValue()
    {
        var edit = new FakeUiaNode("Edit", "Path") { AutomationId = "PathBox", ActionPatterns = ["Value"], Value = "C:" };
        BuildWindow(edit);
        var pathRef = await RefOf("PathBox");
        var full = @"C:\" + new string('x', 300);
        At(250, () => edit.Value = full);

        var result = await WaitAsync(condition: ElementWaitCondition.ValueEquals, expected: full, elementRef: pathRef);

        result.Satisfied.Should().BeTrue();
        result.Element!.Value.Should().HaveLength(200);
        result.Element.ValueTruncated.Should().BeTrue();
    }

    [Fact]
    public async Task Ref_ValueEquals_OnAPasswordField_EndsAtOnce_WithoutReadingTheValue()
    {
        BuildWindow(new FakeUiaNode("Edit", "Password") { AutomationId = "Pwd", ActionPatterns = ["Value"], IsPassword = true, Value = "hunter2" });
        var pwdRef = await RefOf("Pwd");

        var result = await WaitAsync(condition: ElementWaitCondition.ValueEquals, expected: "hunter2", elementRef: pwdRef);

        result.ErrorCode.Should().Be("InvalidArguments");
        result.Polls.Should().Be(1);
        _tree.Reads.Should().NotContain(r => r.StartsWith("Value:"));
        System.Text.Json.JsonSerializer.Serialize(result).Should().NotContain("hunter2");
    }

    [Fact]
    public async Task Ref_Focused_And_ToggleState_ReadWhatTheyNeed()
    {
        var toggle = new FakeTogglePattern(UiaToggleState.Off);
        var box = new FakeUiaNode("CheckBox", "Wrap") { AutomationId = "Wrap", ActionPatterns = ["Toggle"], HasKeyboardFocus = true }
            .WithPattern<IUiaTogglePattern>(toggle);
        BuildWindow(box);
        var wrapRef = await RefOf("Wrap");
        At(200, () => toggle.ToggleState = UiaToggleState.Indeterminate);

        var focused = await WaitAsync(condition: ElementWaitCondition.Focused, elementRef: wrapRef);
        var toggled = await WaitAsync(condition: ElementWaitCondition.ToggleState, expected: "indeterminate", elementRef: wrapRef);

        focused.Satisfied.Should().BeTrue();
        focused.Element!.HasKeyboardFocus.Should().BeTrue();
        toggled.Satisfied.Should().BeTrue();
        toggled.Element!.ToggleState.Should().Be(UiaToggleState.Indeterminate);
    }

    [Fact]
    public async Task WithinRef_SearchesBelowItsElement_InItsWindow_WithoutWaitingForAWindow()
    {
        var list = new FakeUiaNode("List", "Files") { AutomationId = "Files" };
        BuildWindow(list, new FakeUiaNode("ListItem", "report.txt"));
        var listRef = await RefOf("Files");
        At(300, () => list.With(new FakeUiaNode("ListItem", "report.txt") { AutomationId = "Inside" }));

        var result = await WaitAsync(Criteria(name: "report.txt", withinRef: listRef));

        result.Satisfied.Should().BeTrue();
        result.Element!.AutomationId.Should().Be("Inside", "the item outside the list does not count");
        _windowWait.Verify(w => w.WaitForConditionAsync(It.IsAny<WindowConditionRequest>(), It.IsAny<IProgress<OperationProgress>?>(),
            It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task WithinRef_MovedToAReCreatedElement_IsFollowed_AndSaysSo()
    {
        // Arrange - the list was rebuilt: the ref now names the new list, which alone fits its description
        var list = new FakeUiaNode("List", "Files") { AutomationId = "Files" };
        var body = BuildWindow(list);
        var listRef = await RefOf("Files");
        list.Vanish();
        body.With(new FakeUiaNode("List", "Files") { AutomationId = "Files" }.With(new FakeUiaNode("ListItem", "report.txt")));

        // Act
        var result = await WaitAsync(Criteria(name: "report.txt", withinRef: listRef));

        // Assert
        result.Satisfied.Should().BeTrue();
        result.RefMoved.Should().BeTrue();
        result.Warning.Should().StartWith("withinRef:");
    }

    [Fact]
    public async Task AStaleWithinRef_EndsTheWait()
    {
        BuildWindow();

        var result = await WaitAsync(Criteria(name: "report.txt", withinRef: "w1e77"));

        result.ErrorCode.Should().Be("ElementStale");
        result.Error.Should().StartWith("withinRef:");
    }

    [Fact]
    public async Task Gone_BelowAWithinRefThatWasNeverIssued_IsElementStale()
    {
        BuildWindow();

        var result = await WaitAsync(Criteria(name: "report.txt", withinRef: "w1e77"), ElementWaitCondition.Gone);

        result.Satisfied.Should().BeFalse();
        result.ErrorCode.Should().Be("ElementStale");
        result.Error.Should().StartWith("withinRef:").And.Contain("not issued");
    }

    [Fact]
    public async Task Gone_BelowAWithinRefWhoseWindowHadClosed_IsSatisfiedAtTheStart_WithAWarning()
    {
        BuildWindow(new FakeUiaNode("List", "Files") { AutomationId = "Files" });
        var listRef = await RefOf("Files");
        _windows.Setup(w => w.IsWindow(Handle)).Returns(false);

        var result = await WaitAsync(Criteria(name: "report.txt", withinRef: listRef), ElementWaitCondition.Gone);

        result.Satisfied.Should().BeTrue();
        result.Polls.Should().Be(0);
        result.Warning.Should().Contain("withinRef").And.Contain("already gone when the wait started");
    }

    [Fact]
    public async Task Gone_BelowAWithinRefWhoseElementGoes_IsSatisfied_WithAWarning()
    {
        // Arrange - the list itself goes away, taking its items with it, while the window stays
        var list = new FakeUiaNode("List", "Files") { AutomationId = "Files" }.With(new FakeUiaNode("ListItem", "report.txt"));
        BuildWindow(list);
        var listRef = await RefOf("Files");
        At(300, list.Vanish);

        // Act
        var result = await WaitAsync(Criteria(name: "report.txt", withinRef: listRef), ElementWaitCondition.Gone);

        // Assert
        result.Satisfied.Should().BeTrue();
        result.ErrorCode.Should().BeNull();
        result.ElapsedMs.Should().BeGreaterThanOrEqualTo(300);
        result.Warning.Should().Contain("withinRef");
    }

    /// <summary>
    /// A virtualized list in the window, and the ref find_ui_elements' searchVirtualized gives its item 5000, which it has not
    /// realized: a placeholder that fails a whole read, as WPF's does.
    /// </summary>
    private async Task<(FakeVirtualList List, string ItemRef)> UnrealizedItemRef()
    {
        var list = new FakeVirtualList(10_000, realized: 30, i => $"Item {i:D5}", 300);
        BuildWindow(list.List);
        var found = await _search.FindAsync(new ElementQuery(Handle, Name: "Item 05000", Timeout: TimeSpan.FromSeconds(5)) { SearchVirtualized = true });
        return (list, found.Elements!.Single().Ref!);
    }

    [Fact]
    public async Task Ref_OfAnItemTheListHasNotRealized_Exists_AsAVirtualizedItem_NotAsAStaleRef()
    {
        var (_, itemRef) = await UnrealizedItemRef();

        var result = await WaitAsync(condition: ElementWaitCondition.Exists, elementRef: itemRef);

        result.Satisfied.Should().BeTrue(result.Error);
        result.Element!.Ref.Should().Be(itemRef);
        result.Element.Virtualized.Should().BeTrue();
        result.Element.IsOffscreen.Should().BeTrue();
    }

    [Fact]
    public async Task Ref_Enabled_OfAnItemTheListHasNotRealized_IsNotMetByTheDefault_ButOnceTheItemIsRealized()
    {
        // Arrange - the placeholder does not say whether it is enabled; the list realizes the item later
        var (list, itemRef) = await UnrealizedItemRef();
        var never = await WaitAsync(condition: ElementWaitCondition.Enabled, elementRef: itemRef, timeoutMs: 1000);
        At(NowMs + 300, () => list.VirtualizedOf(5000).Realize());

        // Act
        var result = await WaitAsync(condition: ElementWaitCondition.Enabled, elementRef: itemRef);

        // Assert
        never.Satisfied.Should().BeFalse();
        never.ErrorCode.Should().Be("WaitTimedOut");
        never.LastObservedState.Should().Contain("not realized");
        result.Satisfied.Should().BeTrue(result.Error);
        result.Element!.Virtualized.Should().BeNull("it is real now");
    }

    #endregion

    #region stableMs

    /// <summary>A spinner that goes at 200 ms, a re-created one that is there from 300 ms (the check at 250 ms falls in the gap), and that one going for good at 1000 ms.</summary>
    private void ASpinnerThatIsReCreated()
    {
        var body = BuildWindow();
        var first = new FakeUiaNode("ProgressBar", "Loading");
        var second = new FakeUiaNode("ProgressBar", "Loading");
        body.With(first);
        At(200, first.Vanish);
        At(300, () => body.With(second));
        At(1000, second.Vanish);
    }

    [Fact]
    public async Task Gone_WithoutStableMs_IsSatisfiedInTheGapWhileAListIsReCreated()
    {
        ASpinnerThatIsReCreated();

        var result = await WaitAsync(Criteria(name: "Loading"), ElementWaitCondition.Gone);

        result.Satisfied.Should().BeTrue();
        result.ElapsedMs.Should().BeLessThan(300, "one check that finds nothing satisfies gone, as before stableMs");
    }

    [Fact]
    public async Task Gone_WithStableMs_IsNotSatisfiedByTheGapWhileAListIsReCreated()
    {
        ASpinnerThatIsReCreated();

        var result = await WaitAsync(Criteria(name: "Loading"), ElementWaitCondition.Gone, stableMs: 300);

        result.Satisfied.Should().BeTrue();
        result.ElapsedMs.Should().BeGreaterThanOrEqualTo(1300, "gone must hold for 300 ms after the re-created spinner went");
        result.Element.Should().BeNull();
        result.LastObservedState.Should().BeNull();
    }

    [Fact]
    public async Task Enabled_WithStableMs_MustHoldAtEveryCheckForThatLong()
    {
        // Arrange - OK is enabled at 200 ms (the check at 250 ms sees it), disabled again at 300 ms, and enabled for good at 1000 ms
        var ok = Button("OK", enabled: false);
        BuildWindow(ok);
        At(200, () => ok.IsEnabled = true);
        At(300, () => ok.IsEnabled = false);
        At(1000, () => ok.IsEnabled = true);

        // Act
        var result = await WaitAsync(condition: ElementWaitCondition.Enabled, stableMs: 400);

        // Assert
        result.Satisfied.Should().BeTrue();
        result.ElapsedMs.Should().BeGreaterThanOrEqualTo(1400);
        result.Element!.IsEnabled.Should().BeTrue();
        result.LastObservedState.Should().BeNull();
    }

    [Fact]
    public async Task Exists_WithStableMs_IsCheckedAgainWhenTheTimeIsUp_NotABackOffIntervalLater()
    {
        BuildWindow(Button("OK"));

        var result = await WaitAsync(stableMs: 200);

        result.Satisfied.Should().BeTrue();
        result.ElapsedMs.Should().Be(200);
        Delays.Select(d => (int)d.TotalMilliseconds).Should().Equal(100, 100);
        result.Polls.Should().Be(3);
    }

    [Fact]
    public async Task StableMs_LongerThanTheTimeout_IsCutToIt()
    {
        BuildWindow(Button("OK"));

        var result = await WaitAsync(timeoutMs: 1000, stableMs: 5000);

        result.Satisfied.Should().BeTrue();
        result.ElapsedMs.Should().Be(1000);
    }

    [Fact]
    public async Task StableMs_WithTimeoutZero_ChecksOnce()
    {
        BuildWindow(Button("OK"));

        var result = await WaitAsync(timeoutMs: 0, stableMs: 300);

        result.Satisfied.Should().BeTrue();
        result.Polls.Should().Be(1);
        Delays.Should().BeEmpty();
    }

    [Fact]
    public async Task StableMs_AConditionThatDoesNotHoldLongEnough_TimesOut_SayingHowLongItHeld()
    {
        // Arrange - OK is enabled from 700 ms on, so it has held for less than 500 ms when the wait ends at 1000 ms
        var ok = Button("OK", enabled: false);
        BuildWindow(ok);
        At(700, () => ok.IsEnabled = true);

        // Act
        var result = await WaitAsync(condition: ElementWaitCondition.Enabled, timeoutMs: 1000, stableMs: 500);

        // Assert
        result.Success.Should().BeFalse();
        result.TimedOut.Should().BeTrue();
        result.ErrorCode.Should().Be("WaitTimedOut");
        result.Element!.IsEnabled.Should().BeTrue("the element as last seen");
        result.LastObservedState.Should().MatchRegex(@"^Button 'OK' \(w1e[0-9]+\) is enabled \(for [0-9]+ of 500 ms\)$");
        result.Error.Should().StartWith("No element matching name 'OK' stayed enabled for 500 ms within 1000 ms");
    }

    [Fact]
    public async Task StableMs_GoneThatDoesNotHoldLongEnough_TimesOut_SayingSo()
    {
        var spinner = new FakeUiaNode("ProgressBar", "Loading");
        BuildWindow(spinner);
        At(700, spinner.Vanish);

        var result = await WaitAsync(Criteria(name: "Loading"), ElementWaitCondition.Gone, timeoutMs: 1000, stableMs: 500);

        result.TimedOut.Should().BeTrue();
        result.LastObservedState.Should().MatchRegex(@"^no element matches name 'Loading' \(for [0-9]+ of 500 ms\)$");
        result.Error.Should().StartWith("Elements matching name 'Loading' did not stay gone for 500 ms within 1000 ms");
    }

    [Fact]
    public async Task StableMs_ARefThatDoesNotStayEnabled_TimesOut_SayingSo()
    {
        var ok = Button("OK", enabled: false);
        BuildWindow(ok);
        var okRef = await RefOf("OKBtn");
        At(300, () => ok.IsEnabled = true);
        At(500, () => ok.IsEnabled = false);

        var result = await WaitAsync(condition: ElementWaitCondition.Enabled, elementRef: okRef, timeoutMs: 1000, stableMs: 400);

        result.TimedOut.Should().BeTrue();
        result.LastObservedState.Should().EndWith("is disabled");
        result.Error.Should().StartWith($"Element {okRef} did not stay enabled for 400 ms within 1000 ms");
    }

    [Fact]
    public async Task StableMs_ARefWhoseElementIsGone_SatisfiesGoneAtOnce_SinceItNeverComesBack()
    {
        var busy = Button("Busy");
        BuildWindow(busy);
        var busyRef = await RefOf("BusyBtn");
        At(300, busy.Vanish);

        var result = await WaitAsync(condition: ElementWaitCondition.Gone, elementRef: busyRef, stableMs: 2000);

        result.Satisfied.Should().BeTrue();
        result.ElapsedMs.Should().BeLessThan(1000);
    }

    [Fact]
    public async Task StableMs_AWindowThatCloses_SatisfiesGoneAtOnce()
    {
        BuildWindow(new FakeUiaNode("ProgressBar", "Loading"));
        At(300, CloseWindow);

        var result = await WaitAsync(Criteria(name: "Loading"), ElementWaitCondition.Gone, stableMs: 2000);

        result.Satisfied.Should().BeTrue();
        result.ElapsedMs.Should().BeLessThan(1000);
        result.Warning.Should().Contain("closed");
    }

    [Fact]
    public async Task StableMs_ACheckThatCannotTell_StartsTheTimeAgain()
    {
        // Arrange - OK is there throughout, but the window does not respond from 200 to 400 ms
        BuildWindow(Button("OK"));
        At(200, () => _windows.Setup(w => w.IsHungAppWindow(Handle)).Returns(true));
        At(400, () => _windows.Setup(w => w.IsHungAppWindow(Handle)).Returns(false));

        // Act
        var result = await WaitAsync(stableMs: 300);

        // Assert - the condition was seen to hold at every check of the last 300 ms
        result.Satisfied.Should().BeTrue();
        result.ElapsedMs.Should().BeGreaterThanOrEqualTo(700);
    }

    #endregion

    #region Timing, warm-up, failures, progress

    [Fact]
    public async Task TheChecks_BackOffFrom100To500Ms()
    {
        BuildWindow();

        await WaitAsync(timeoutMs: 3000);

        Delays.Take(6).Select(d => (int)d.TotalMilliseconds).Should().Equal(100, 150, 225, 337, 500, 500);
        Delays.Sum(d => d.TotalMilliseconds).Should().Be(3000, "the last delay is cut to the time left");
    }

    [Fact]
    public async Task TimeoutZero_ChecksOnce()
    {
        BuildWindow(Button("OK", enabled: false));

        var result = await WaitAsync(condition: ElementWaitCondition.Enabled, timeoutMs: 0);

        result.TimedOut.Should().BeTrue();
        result.Polls.Should().Be(1);
        Delays.Should().BeEmpty();
    }

    [Fact]
    public async Task TheWarmUp_RunsOnTheFirstCheckOnly_AndIsReported()
    {
        var info = new AccessibilityWarmupInfo(AccessibilityWarmupStatus.Ready, WebContentHost.Chromium, 300, 2, 5, null);
        _warmup.Setup(w => w.WarmUpForSearch(Handle, It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>())).Returns(info);
        var body = BuildWindow();
        At(900, () => body.With(Button("OK")));

        var result = await WaitAsync();

        result.Satisfied.Should().BeTrue();
        result.Polls.Should().BeGreaterThan(3);
        result.AccessibilityWarmup.Should().Be(info);
        _warmup.Verify(w => w.WarmUpForSearch(Handle, It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task TheWarmUp_RunsOnTheNextCheck_WhenTheFirstStoppedBeforeIt()
    {
        // Arrange - the window does not respond at the first check, which so ends before the warm-up
        var info = new AccessibilityWarmupInfo(AccessibilityWarmupStatus.Ready, WebContentHost.Chromium, 300, 2, 5, null);
        _warmup.Setup(w => w.WarmUpForSearch(Handle, It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>())).Returns(info);
        BuildWindow(Button("OK"));
        _windows.Setup(w => w.IsHungAppWindow(Handle)).Returns(true);
        At(200, () => _windows.Setup(w => w.IsHungAppWindow(Handle)).Returns(false));

        // Act
        var result = await WaitAsync();

        // Assert
        result.Satisfied.Should().BeTrue();
        result.Polls.Should().BeGreaterThan(1);
        result.AccessibilityWarmup.Should().Be(info);
        _warmup.Verify(w => w.WarmUpForSearch(Handle, It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ValueEquals_DoesNotReadLongValuesInFull_OnceTheCheckHasUsedItsTime()
    {
        // Arrange - two long values that start like the expected one, in a pane wide enough to be searched by one FindAll,
        // which takes longer than the check's second: the timeout has passed when the search returns
        var start = new string('a', 230);
        BuildWindow(Enumerable.Range(1, 60).Select(i => new FakeUiaNode("Text", $"Line {i}"))
            .Append(new FakeUiaNode("Edit", "Notes") { AutomationId = "First", ActionPatterns = ["Value"], Value = start + "1" })
            .Append(new FakeUiaNode("Edit", "Notes") { AutomationId = "Second", ActionPatterns = ["Value"], Value = start + "2" })
            .ToArray());
        _tree.OnFindAll = (_, _) => _clock.Advance(TimeSpan.FromMilliseconds(1100));

        // Act
        var result = await WaitAsync(Criteria(name: "Notes"), ElementWaitCondition.ValueEquals, start + "3", timeoutMs: 500);

        // Assert - one worker read, the search's: no value was read in full after the check's time ran out
        result.TimedOut.Should().BeTrue();
        result.Polls.Should().Be(1);
        _worker.Calls.Should().Be(1);
        result.LastObservedState.Should().Contain("before it could be read in full");
    }

    [Fact]
    public async Task AWindowThatDoesNotRespondForAWhile_DoesNotEndTheWait()
    {
        BuildWindow(Button("OK"));
        _windows.Setup(w => w.IsHungAppWindow(Handle)).Returns(true);
        At(400, () => _windows.Setup(w => w.IsHungAppWindow(Handle)).Returns(false));

        var result = await WaitAsync();

        result.Satisfied.Should().BeTrue();
        result.ElapsedMs.Should().BeGreaterThanOrEqualTo(400);
    }

    [Fact]
    public async Task AWindowThatNeverResponds_TimesOut_SayingTheChecksCouldNotTell()
    {
        BuildWindow(Button("OK"));
        _windows.Setup(w => w.IsHungAppWindow(Handle)).Returns(true);

        var result = await WaitAsync(timeoutMs: 600);

        result.TimedOut.Should().BeTrue();
        result.LastObservedState.Should().StartWith("the last check could not tell (NotResponding)");
    }

    [Fact]
    public async Task AnUnknownControlType_EndsTheWait()
    {
        BuildWindow();

        var result = await WaitAsync(Criteria(controlTypes: ["Buton"]));

        result.ErrorCode.Should().Be("InvalidControlType");
        result.TimedOut.Should().BeFalse();
    }

    [Fact]
    public async Task Progress_IsReportedAboutOnceASecond()
    {
        BuildWindow();
        var progress = new RecordingProgress<OperationProgress>();

        await WaitAsync(timeoutMs: 3000, progress: progress);

        progress.Reports.Should().HaveCountGreaterThanOrEqualTo(2).And.HaveCountLessThanOrEqualTo(4);
        progress.Reports.Should().AllSatisfy(r => r.Total.Should().Be(3000));
        progress.Reports[0].Message.Should().StartWith("waiting for exists:");
    }

    [Fact]
    public async Task Cancellation_EndsTheWait()
    {
        BuildWindow();
        using var cancellation = new CancellationTokenSource();
        At(300, cancellation.Cancel);

        var wait = () => WaitAsync(cancellationToken: cancellation.Token);

        await wait.Should().ThrowAsync<OperationCanceledException>();
    }

    #endregion

    #region Target policy

    /// <summary>The service with the default target policy, where the window belongs to the password manager 1Password.</summary>
    private ElementWaitService CreateSutInADeniedApp()
    {
        _windows.Setup(w => w.GetProcessName(300u)).Returns("1Password");
        var policy = new TargetPolicy(TargetPolicySettings.Default);
        var search = new ElementSearchService(_tree, _worker, _registry, _windows.Object, _warmup.Object, policy: policy);
        return new ElementWaitService(search, _registry, _windowWait.Object, _tree, _worker, _windows.Object, _clock, DelayAsync, policy);
    }

    [Theory]
    [InlineData(ElementWaitCondition.Exists)]
    [InlineData(ElementWaitCondition.Gone)]
    public async Task Criteria_InAWindowOfADeniedApp_EndTheWaitAtOnce_WithNothingRead(ElementWaitCondition condition)
    {
        // Arrange - the window the selector names belongs to a password manager
        BuildWindow(Button("OK"));
        var sut = CreateSutInADeniedApp();

        // Act
        var result = await sut.WaitAsync(new ElementWaitRequest
        {
            Window = Editor, Criteria = Criteria(name: "OK"), Condition = condition, TimeoutMs = 5000
        });

        // Assert
        result.Success.Should().BeFalse();
        result.Satisfied.Should().BeFalse();
        result.TimedOut.Should().BeFalse();
        result.ErrorCode.Should().Be(nameof(UIElementErrorCode.TargetDeniedByPolicy));
        result.Error.Should().Contain("1Password");
        result.Polls.Should().Be(0);
        _tree.Reads.Should().BeEmpty();
        _warmup.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task Ref_InAWindowOfADeniedApp_EndsTheWaitAtOnce_WithNothingRead()
    {
        // Arrange
        BuildWindow(Button("OK"));
        var okRef = await RefOf("OKBtn");
        var sut = CreateSutInADeniedApp();
        var readsBefore = _tree.Reads.Count;

        // Act
        var result = await sut.WaitAsync(new ElementWaitRequest { Ref = okRef, Condition = ElementWaitCondition.Enabled, TimeoutMs = 5000 });

        // Assert
        result.ErrorCode.Should().Be(nameof(UIElementErrorCode.TargetDeniedByPolicy));
        result.Element.Should().BeNull();
        _tree.Reads.Should().HaveCount(readsBefore);
    }

    #endregion
}
