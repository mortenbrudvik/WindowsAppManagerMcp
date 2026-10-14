using WindowsAppManagerMcp.Infrastructure;
using WindowsAppManagerMcp.Tools;

namespace WindowsAppManagerMcp.Tests.Unit.Tools;

/// <summary>
/// The window argument of the tools (roadmap M2.4): a tool takes its window as handle or as a window selector, never
/// both; a selector is resolved by IWindowService.ResolveWindow to exactly one window, before the input gate, and the
/// resolved window then goes through every check a handle goes through (policy, close guards, input guard). A selector
/// that names no single window is refused with its errorCode and candidates, and nothing is done.
/// </summary>
public class WindowSelectorToolTests
{
    private const long Notepad = 0x1A0B2C;
    private const long KeePass = 0x2B0C3D;

    private static readonly WindowSelector NotepadSelector = new() { ProcessName = "notepad" };
    private static readonly WindowSelector ChromeSelector = new() { ProcessName = "chrome" };

    private static readonly WindowInfo NotepadWindow =
        TestDataFactory.CreateWindowInfo((int)Notepad, "notes.txt - Notepad", "notepad", 200) with { ClassName = "Notepad" };

    private static readonly IReadOnlyList<WindowInfo> ChromeWindows =
    [
        TestDataFactory.CreateWindowInfo(0x3001, "Inbox - Google Chrome", "chrome", 300) with { ClassName = "Chrome_WidgetWin_1" },
        TestDataFactory.CreateWindowInfo(0x3002, "Docs - Google Chrome", "chrome", 300) with { ClassName = "Chrome_WidgetWin_1" }
    ];

    private readonly Mock<IWindowService> _windows = new();

    public WindowSelectorToolTests()
    {
        _windows.Setup(w => w.IsValidWindow(It.IsAny<nint>())).Returns(true);
        _windows.Setup(w => w.GetWindowInfo(It.IsAny<nint>())).Returns((nint h) => h == (nint)Notepad ? NotepadWindow : null);
        _windows.Setup(w => w.EvaluateTargetPolicy(It.IsAny<nint>())).Returns(new TargetPolicyVerdict(true, "notepad", null));
        _windows.Setup(w => w.ResolveWindow(NotepadSelector)).Returns(WindowResolution.Found(NotepadWindow));
        _windows.Setup(w => w.ResolveWindow(ChromeSelector)).Returns(WindowResolution.Failed("AmbiguousWindow", "2 windows match processName 'chrome'.", ChromeWindows));
        _windows.Setup(w => w.MoveWindow(It.IsAny<nint>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<bool>()))
            .Returns((nint h, int _, int _, bool _) => WindowChangeResult.Done(h));
        _windows.Setup(w => w.SetWindowStateBatchAsync(It.IsAny<IReadOnlyList<(nint, WindowStateRequest)>>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyList<(nint Handle, WindowStateRequest)> changes, int _, CancellationToken _) =>
                changes.Select(c => WindowChangeResult.Done(c.Handle)).ToList());
    }

    private WindowManagerTools CreateWindowTools(IInputSession? session = null) =>
        new(_windows.Object, Mock.Of<IForegroundService>(), session, gateWaitTimeout: TimeSpan.FromSeconds(10));

    #region The single-window tools

    [Fact]
    public async Task MoveWindow_WithASelector_ActsOnTheResolvedWindow_AndReportsItsHandle()
    {
        var result = await CreateWindowTools().MoveWindow(10, 20, window: NotepadSelector);

        result.Success.Should().BeTrue();
        result.Handle.Should().Be(Notepad);
        _windows.Verify(w => w.MoveWindow((nint)Notepad, 10, 20, true), Times.Once);
    }

    [Fact]
    public async Task MoveWindow_WithAnAmbiguousSelector_IsRefusedWithTheCandidates_AndNothingMoves()
    {
        var act = () => CreateWindowTools().MoveWindow(10, 20, window: ChromeSelector);

        var error = (await act.Should().ThrowAsync<ToolErrorException>()).Which;
        error.ErrorCode.Should().Be("AmbiguousWindow");
        error.Candidates!.Select(c => c.Handle).Should().Equal(0x3001, 0x3002);
        error.Candidates![0].Should().Be(new WindowCandidateDto(0x3001, "Inbox - Google Chrome", "chrome", 300, "Chrome_WidgetWin_1"));
        _windows.Verify(w => w.MoveWindow(It.IsAny<nint>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<bool>()), Times.Never);
    }

    [Fact]
    public async Task ACandidatesLongTitle_IsCut()
    {
        // roadmap M3.F14: the selector compared the whole title; the candidate returns it cut
        var title = "Inbox - " + new string('a', 5000);
        _windows.Setup(w => w.ResolveWindow(ChromeSelector)).Returns(WindowResolution.Failed("AmbiguousWindow",
            "2 windows match processName 'chrome'.", [ChromeWindows[0] with { Title = title }, ChromeWindows[1]]));

        var act = () => CreateWindowTools().MoveWindow(10, 20, window: ChromeSelector);

        var error = (await act.Should().ThrowAsync<ToolErrorException>()).Which;
        error.Candidates!.Select(c => c.Title).Should().Equal(title[..(AppText.MaxLength - 1)] + "…", "Docs - Google Chrome");
    }

    [Fact]
    public async Task MoveWindow_WithHandleAndWindow_OrWithNeither_IsInvalidArguments()
    {
        var both = () => CreateWindowTools().MoveWindow(10, 20, handle: Notepad, window: NotepadSelector);
        var neither = () => CreateWindowTools().MoveWindow(10, 20);

        (await both.Should().ThrowAsync<ToolErrorException>()).Which.ErrorCode.Should().Be(ToolErrorReporting.InvalidArgumentsErrorCode);
        (await neither.Should().ThrowAsync<ToolErrorException>()).Which.ErrorCode.Should().Be(ToolErrorReporting.InvalidArgumentsErrorCode);
        _windows.Verify(w => w.ResolveWindow(It.IsAny<WindowSelector>()), Times.Never);
        _windows.Verify(w => w.MoveWindow(It.IsAny<nint>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<bool>()), Times.Never);
    }

    [Fact]
    public async Task ASelectorThatOnlyNamesAHandle_IsThatHandle_WithoutAResolution()
    {
        var result = await CreateWindowTools().MoveWindow(10, 20, window: new WindowSelector { Handle = Notepad });

        result.Handle.Should().Be(Notepad);
        _windows.Verify(w => w.ResolveWindow(It.IsAny<WindowSelector>()), Times.Never);
        _windows.Verify(w => w.MoveWindow((nint)Notepad, 10, 20, true), Times.Once);
    }

    [Fact]
    public async Task TheResolvedWindow_IsCheckedByTheTargetPolicy_AsAHandleIs()
    {
        // Arrange - the selector names a password manager the user's policy denies
        var keePass = new WindowSelector { ProcessName = "KeePass" };
        _windows.Setup(w => w.ResolveWindow(keePass))
            .Returns(WindowResolution.Found(TestDataFactory.CreateWindowInfo((int)KeePass, "Database - KeePass", "KeePass", 400)));
        _windows.Setup(w => w.EvaluateTargetPolicy((nint)KeePass)).Returns(new TargetPolicyVerdict(false, "KeePass", "default deny list"));

        // Act
        var result = await CreateWindowTools().MoveWindow(10, 20, window: keePass);

        // Assert
        result.Success.Should().BeFalse();
        result.ErrorCode.Should().Be(nameof(WindowErrorCode.TargetDeniedByPolicy));
        result.Handle.Should().Be(KeePass);
        _windows.Verify(w => w.MoveWindow(It.IsAny<nint>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<bool>()), Times.Never);
    }

    [Fact]
    public async Task TheSelector_IsResolvedBeforeTheInputGate_AndTheWindowChangesInsideIt()
    {
        // Arrange - record whether this test's call held the gate while the selector was resolved, and while the window
        // moved (other tests take the gate in parallel)
        var flow = InputGate.TagFlow();
        bool? gateFreeWhileResolving = null;
        bool? gateHeldWhileMoving = null;
        _windows.Setup(w => w.ResolveWindow(NotepadSelector)).Returns(() =>
        {
            gateFreeWhileResolving = !InputGate.IsHeldBy(flow);
            return WindowResolution.Found(NotepadWindow);
        });
        _windows.Setup(w => w.MoveWindow((nint)Notepad, 10, 20, true)).Returns(() =>
        {
            gateHeldWhileMoving = InputGate.IsHeldBy(flow);
            return WindowChangeResult.Done((nint)Notepad);
        });

        // Act
        await CreateWindowTools().MoveWindow(10, 20, window: NotepadSelector);

        // Assert
        gateFreeWhileResolving.Should().BeTrue("a slow resolution must not hold up every input and window call");
        gateHeldWhileMoving.Should().BeTrue("the window changes inside the gate, as when the gate filter ran the tool");
    }

    [Fact]
    public async Task AWindowThatNoLongerMatchesTheSelectorOnceInsideTheGate_IsHandleMismatch_AndNothingMoves()
    {
        // Arrange - the selector resolved to Notepad, then Notepad closed while the call waited for the gate and a
        // terminal got its handle
        _windows.Setup(w => w.GetWindowInfo((nint)Notepad))
            .Returns(TestDataFactory.CreateWindowInfo((int)Notepad, "Windows PowerShell", "WindowsTerminal", 300));

        // Act
        var act = () => CreateWindowTools().MoveWindow(10, 20, window: NotepadSelector);

        // Assert
        var error = (await act.Should().ThrowAsync<ToolErrorException>()).Which;
        error.ErrorCode.Should().Be(nameof(WindowErrorCode.HandleMismatch));
        error.Message.Should().Contain("no longer matches").And.Contain("Nothing was done");
        _windows.Verify(w => w.MoveWindow(It.IsAny<nint>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<bool>()), Times.Never);
    }

    [Fact]
    public async Task TheSelectorIsCheckedAgain_InsideTheGate()
    {
        // Arrange - record whether the window was read again while this test's call held the gate
        var flow = InputGate.TagFlow();
        var readInsideTheGate = false;
        _windows.Setup(w => w.GetWindowInfo((nint)Notepad)).Returns(() =>
        {
            readInsideTheGate |= InputGate.IsHeldBy(flow);
            return NotepadWindow;
        });

        // Act
        var result = await CreateWindowTools().MoveWindow(10, 20, window: NotepadSelector);

        // Assert
        result.Success.Should().BeTrue();
        readInsideTheGate.Should().BeTrue("the resolution ran before the gate wait, so the window is checked again before it is changed");
    }

    [Fact]
    public async Task WhileInputIsPaused_TheWindowToolsRefuse_BeforeResolvingAnything()
    {
        using var session = new InputSession(new FakeInputWrapper());
        session.Pause(InputControlSources.Hotkey);

        var act = () => CreateWindowTools(session).FocusWindow(window: NotepadSelector);

        (await act.Should().ThrowAsync<ToolErrorException>()).Which.ErrorCode.Should().Be(nameof(WindowErrorCode.PausedByUser));
        _windows.Verify(w => w.ResolveWindow(It.IsAny<WindowSelector>()), Times.Never);
    }

    [Fact]
    public async Task WhileInputIsPaused_CloseWindow_Refuses_BeforeResolvingOrCheckingTheWindow()
    {
        // Arrange - an ambiguous or stale selector must not turn a pause into AmbiguousWindow or WindowNotFound, which an agent
        // would 'fix' and retry instead of stopping
        using var session = new InputSession(new FakeInputWrapper());
        session.Pause(InputControlSources.Hotkey);

        // Act
        var act = () => CreateWindowTools(session).CloseWindow(window: NotepadSelector);

        // Assert
        (await act.Should().ThrowAsync<ToolErrorException>()).Which.ErrorCode.Should().Be(nameof(WindowErrorCode.PausedByUser));
        _windows.Verify(w => w.ResolveWindow(It.IsAny<WindowSelector>()), Times.Never);
        _windows.Verify(w => w.IsValidWindow(It.IsAny<nint>()), Times.Never);
    }

    [Fact]
    public async Task FocusWindow_WithASelector_ActivatesTheResolvedWindow()
    {
        var foreground = new Mock<IForegroundService>();
        foreground.Setup(f => f.EnsureForeground(It.IsAny<nint>(), It.IsAny<int>()))
            .Returns((nint h, int _) => new FocusOutcome { Target = h, IsForeground = true, ForegroundHandle = h });
        var tools = new WindowManagerTools(_windows.Object, foreground.Object);

        var result = await tools.FocusWindow(window: NotepadSelector);

        result.IsForeground.Should().BeTrue();
        foreground.Verify(f => f.EnsureForeground((nint)Notepad, It.IsAny<int>()), Times.Once);
    }

    #endregion

    #region The batches

    [Fact]
    public async Task ABatchItemWhoseSelectorIsAmbiguous_FailsAlone_WithTheCandidates()
    {
        // Act
        var result = await CreateWindowTools().SetWindowsStateBatch(
        [
            new WindowStateChange("minimize", Window: ChromeSelector),
            new WindowStateChange("minimize", Window: NotepadSelector),
            new WindowStateChange("minimize", Handle: Notepad, Window: NotepadSelector)
        ], delayBetweenMs: 0);

        // Assert
        result.Results.Should().HaveCount(3);
        result.Results[0].Success.Should().BeFalse();
        result.Results[0].ErrorCode.Should().Be("AmbiguousWindow");
        result.Results[0].Candidates!.Select(c => c.Handle).Should().Equal(0x3001, 0x3002);
        result.Results[1].Success.Should().BeTrue();
        result.Results[1].Handle.Should().Be(Notepad);
        result.Results[2].ErrorCode.Should().Be(ToolErrorReporting.InvalidArgumentsErrorCode, "an item takes handle or window, not both");
        _windows.Verify(w => w.SetWindowStateBatchAsync(
            It.Is<IReadOnlyList<(nint, WindowStateRequest)>>(changes => changes.Count == 1 && changes[0].Item1 == (nint)Notepad),
            It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ABatchItemWhoseWindowChangedBeforeTheGate_FailsAlone_WithHandleMismatch()
    {
        // Arrange - the second item's window was another app's by the time the batch held the gate
        const long chrome = 0x3001;
        var chromeSelector = new WindowSelector { ProcessName = "chrome", TitleContains = "Inbox" };
        _windows.Setup(w => w.ResolveWindow(chromeSelector)).Returns(WindowResolution.Found(ChromeWindows[0]));
        _windows.Setup(w => w.GetWindowInfo((nint)chrome))
            .Returns(TestDataFactory.CreateWindowInfo((int)chrome, "Untitled - Paint", "mspaint", 500));

        // Act
        var result = await CreateWindowTools().SetWindowsStateBatch(
        [
            new WindowStateChange("minimize", Window: NotepadSelector),
            new WindowStateChange("minimize", Window: chromeSelector)
        ], delayBetweenMs: 0);

        // Assert
        result.Results[0].Success.Should().BeTrue();
        result.Results[1].ErrorCode.Should().Be(nameof(WindowErrorCode.HandleMismatch));
        result.Results[1].Error.Should().StartWith("Item 1:");
        _windows.Verify(w => w.SetWindowStateBatchAsync(
            It.Is<IReadOnlyList<(nint, WindowStateRequest)>>(changes => changes.Count == 1 && changes[0].Item1 == (nint)Notepad),
            It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ABatchItemWithNoWindow_IsInvalidArguments()
    {
        var result = await CreateWindowTools().SetWindowsStateBatch([new WindowStateChange("minimize")], delayBetweenMs: 0);

        result.Results[0].ErrorCode.Should().Be(ToolErrorReporting.InvalidArgumentsErrorCode);
        result.Results[0].Error.Should().Contain("names no window");
    }

    #endregion

    #region The close tools

    [Fact]
    public async Task CloseWindow_WithASelector_ClosesTheResolvedWindow_ThroughTheServicesCloseGuards()
    {
        // Arrange - the close guards (shell, agent host, policy) are the window service's own CloseWindow checks
        _windows.Setup(w => w.CloseWindow((nint)Notepad, false)).Returns(WindowCloseResult.Sent((nint)Notepad));
        var tools = CreateWindowTools();

        // Act
        var result = await tools.CloseWindow(window: NotepadSelector, waitMs: 0);

        // Assert
        result.Success.Should().BeTrue();
        result.Handle.Should().Be(Notepad);
        _windows.Verify(w => w.CloseWindow((nint)Notepad, false), Times.Once);
    }

    [Fact]
    public async Task CloseWindow_OfAWindowThatChangedWhileTheCallWaitedForTheGate_ClosesNothing()
    {
        // Arrange - resolved to Notepad; by the time the gate is held, its handle belongs to a terminal
        _windows.Setup(w => w.GetWindowInfo((nint)Notepad))
            .Returns(TestDataFactory.CreateWindowInfo((int)Notepad, "Windows PowerShell", "WindowsTerminal", 300));

        // Act
        var act = () => CreateWindowTools().CloseWindow(window: NotepadSelector, waitMs: 0);

        // Assert
        (await act.Should().ThrowAsync<ToolErrorException>()).Which.ErrorCode.Should().Be(nameof(WindowErrorCode.HandleMismatch));
        _windows.Verify(w => w.CloseWindow(It.IsAny<nint>(), It.IsAny<bool>()), Times.Never);
    }

    [Fact]
    public async Task TheCloseTools_RefuseABareForegroundSelector_ItNamesNoWindow()
    {
        // Arrange - {"foreground": true} is whatever is in front: a close needs the window named
        var foreground = new WindowSelector { Foreground = true };
        _windows.Setup(w => w.CloseWindowsBatchAsync(It.IsAny<IReadOnlyList<nint>>(), It.IsAny<bool>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyList<nint> handles, bool _, int _, CancellationToken _) => handles.Select(WindowCloseResult.Sent).ToList());
        var tools = CreateWindowTools();

        // Act
        var closeWindow = () => tools.CloseWindow(window: foreground, force: true, waitMs: 0);
        var closeApplication = () => tools.CloseApplication(window: foreground);
        var batch = await tools.CloseWindowsBatch([new WindowCloseRequest(Window: foreground)], delayBetweenMs: 0, waitMs: 0);

        // Assert
        (await closeWindow.Should().ThrowAsync<ToolErrorException>()).Which.ErrorCode.Should().Be(ToolErrorReporting.InvalidArgumentsErrorCode);
        (await closeApplication.Should().ThrowAsync<ToolErrorException>()).Which.ErrorCode.Should().Be(ToolErrorReporting.InvalidArgumentsErrorCode);
        batch.Results[0].ErrorCode.Should().Be(ToolErrorReporting.InvalidArgumentsErrorCode);
        batch.Results[0].Error.Should().Contain("foreground");
        batch.Results[0].Status.Should().Be("notSent", "the item was refused before its close message");
        _windows.Verify(w => w.ResolveWindow(It.IsAny<WindowSelector>()), Times.Never);
        _windows.Verify(w => w.CloseWindow(It.IsAny<nint>(), It.IsAny<bool>()), Times.Never);
        _windows.Verify(w => w.CloseWindowsBatchAsync(It.Is<IReadOnlyList<nint>>(h => h.Count > 0), It.IsAny<bool>(), It.IsAny<int>(),
            It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task CloseWindow_WithForegroundAndAFilter_ClosesTheWindowInFront_ThatWasNamed()
    {
        // The agent said which window it expects in front: that names a window
        var notepadInFront = new WindowSelector { Foreground = true, ProcessName = "notepad" };
        _windows.Setup(w => w.ResolveWindow(notepadInFront)).Returns(WindowResolution.Found(NotepadWindow));
        _windows.Setup(w => w.CloseWindow((nint)Notepad, false)).Returns(WindowCloseResult.Sent((nint)Notepad));

        var result = await CreateWindowTools().CloseWindow(window: notepadInFront, waitMs: 0);

        result.Success.Should().BeTrue();
        _windows.Verify(w => w.CloseWindow((nint)Notepad, false), Times.Once);
    }

    [Fact]
    public async Task CloseWindow_WithAnAmbiguousSelector_ClosesNothing()
    {
        var act = () => CreateWindowTools().CloseWindow(window: ChromeSelector);

        (await act.Should().ThrowAsync<ToolErrorException>()).Which.ErrorCode.Should().Be("AmbiguousWindow");
        _windows.Verify(w => w.CloseWindow(It.IsAny<nint>(), It.IsAny<bool>()), Times.Never);
    }

    [Theory]
    [InlineData(true, false, null)]
    [InlineData(false, true, null)]
    [InlineData(false, false, "notepad")]
    public async Task CloseApplication_WithAWindowAndAnotherTarget_IsInvalidArguments(bool handle, bool processId, string? processName)
    {
        var act = () => CreateWindowTools().CloseApplication(
            handle: handle ? Notepad : null, window: NotepadSelector, processId: processId ? 200 : null, processName: processName);

        (await act.Should().ThrowAsync<ToolErrorException>()).Which.ErrorCode.Should().Be(ToolErrorReporting.InvalidArgumentsErrorCode);
        _windows.Verify(w => w.ResolveWindow(It.IsAny<WindowSelector>()), Times.Never);
        _windows.Verify(w => w.CloseWindow(It.IsAny<nint>(), It.IsAny<bool>()), Times.Never);
    }

    [Fact]
    public async Task CloseApplication_WithAnAmbiguousSelector_ClosesNothing()
    {
        var act = () => CreateWindowTools().CloseApplication(window: ChromeSelector);

        (await act.Should().ThrowAsync<ToolErrorException>()).Which.ErrorCode.Should().Be("AmbiguousWindow");
        _windows.Verify(w => w.CloseWindow(It.IsAny<nint>(), It.IsAny<bool>()), Times.Never);
        _windows.Verify(w => w.GetApplicationWindows(), Times.Never);
    }

    #endregion

    #region The input tools

    [Fact]
    public void AnInputTool_WithASelector_HandsTheInputServiceARecheck_ForInsideTheGate()
    {
        // Arrange - the input service waits for the gate (up to 30 s) after the selector was resolved
        var input = new Mock<IInputSimulationService>();
        InputTarget? passed = null;
        input.Setup(i => i.Click(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<InputTarget>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .Callback((int _, int _, InputTarget target, bool _, CancellationToken _) => passed = target)
            .Returns(new ClickResult(true, 5, 6, "left"));
        var tools = new InputSimulationTools(input.Object, _windows.Object);
        var selector = new WindowSelector { ProcessName = "notepad", TitleContains = "notes.txt" };
        _windows.Setup(w => w.ResolveWindow(selector)).Returns(WindowResolution.Found(NotepadWindow));

        // Act
        tools.Click(5, 6, window: selector);

        // Assert - still the same document: no refusal; then the user switches the tab, and the recheck says so
        passed!.Value.SelectorRecheck.Should().NotBeNull();
        passed.Value.SelectorRecheck!().Should().BeNull();
        _windows.Setup(w => w.GetWindowInfo((nint)Notepad)).Returns(NotepadWindow with { Title = "draft.txt - Notepad" });
        var refusal = passed.Value.SelectorRecheck!();
        refusal!.ErrorCode.Should().Be("HandleMismatch");
        refusal.Error.Should().Contain("no longer matches").And.Contain("notes.txt");
    }

    [Fact]
    public void AnInputTool_WhoseTitleRegexTimesOutInTheRecheck_GetsInvalidArguments_NotHandleMismatch()
    {
        // Arrange - resolved while the title was short; inside the gate the title makes the pattern time out
        var input = new Mock<IInputSimulationService>();
        InputTarget? passed = null;
        input.Setup(i => i.Click(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<InputTarget>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .Callback((int _, int _, InputTarget target, bool _, CancellationToken _) => passed = target)
            .Returns(new ClickResult(true, 5, 6, "left"));
        var tools = new InputSimulationTools(input.Object, _windows.Object);
        var selector = new WindowSelector { ProcessName = "notepad", TitleRegex = "^(a+)+$" };
        _windows.Setup(w => w.ResolveWindow(selector)).Returns(WindowResolution.Found(NotepadWindow with { Title = "aaa" }));
        tools.Click(5, 6, window: selector);
        _windows.Setup(w => w.GetWindowInfo((nint)Notepad)).Returns(NotepadWindow with { Title = new string('a', 5000) + "!" });

        // Act
        var refusal = passed!.Value.SelectorRecheck!();

        // Assert - the window tools' code for the same timeout
        refusal!.ErrorCode.Should().Be("InvalidArguments");
        refusal.Error.Should().Contain("100 ms");
    }

    [Fact]
    public void AnInputTool_WithAHandleOnly_HasNoSelectorToRecheck()
    {
        var input = new Mock<IInputSimulationService>();
        InputTarget? passed = null;
        input.Setup(i => i.Click(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<InputTarget>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .Callback((int _, int _, InputTarget target, bool _, CancellationToken _) => passed = target)
            .Returns(new ClickResult(true, 5, 6, "left"));

        new InputSimulationTools(input.Object, _windows.Object).Click(5, 6, windowHandle: Notepad);

        passed!.Value.SelectorRecheck.Should().BeNull();
    }

    [Fact]
    public void Click_WithASelector_TargetsTheResolvedWindow_AsWindowHandle()
    {
        // Arrange - the resolved window goes to the input service as windowHandle would: its guard checks it there
        var input = new Mock<IInputSimulationService>();
        input.Setup(i => i.Click(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<InputTarget>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .Returns(new ClickResult(true, 5, 6, "left"));
        var tools = new InputSimulationTools(input.Object, _windows.Object);

        // Act
        tools.Click(5, 6, window: NotepadSelector);

        // Assert
        input.Verify(i => i.Click(5, 6, new InputTarget((nint)Notepad, false), false, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task SendKeys_WithABareForegroundSelector_IsSentAsWithoutWindowHandle_SoTheDestructiveChordRuleApplies()
    {
        // Arrange - {"foreground": true} names no window: the guard must see no windowHandle, and so refuse Alt+F4
        // (DestructiveChordNeedsTarget) as it does without one
        var input = new Mock<IInputSimulationService>();
        input.Setup(i => i.SendKeysAsync(It.IsAny<string>(), It.IsAny<InputTarget>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SendKeysResult(false, "%{F4}", Error: "refused", ErrorCode: InputErrorCode.DestructiveChordNeedsTarget));
        var tools = new InputSimulationTools(input.Object, _windows.Object);

        // Act
        await tools.SendKeys("%{F4}", window: new WindowSelector { Foreground = true });

        // Assert
        input.Verify(i => i.SendKeysAsync("%{F4}", new InputTarget(null, false), false, It.IsAny<CancellationToken>()), Times.Once);
        _windows.Verify(w => w.ResolveWindow(It.IsAny<WindowSelector>()), Times.Never);
    }

    [Fact]
    public async Task TypeText_WithABareForegroundSelectorAndAllowHostTarget_GivesAllowHostTargetNoWindow_SoItHasNoEffect()
    {
        var input = new Mock<IInputSimulationService>();
        var tools = new InputSimulationTools(input.Object, _windows.Object);

        await tools.TypeText("hello", window: new WindowSelector { Foreground = true }, allowHostTarget: true);

        // InputTarget.HasWindow is false: the host guard ignores allowHostTarget without a window named by handle
        input.Verify(i => i.TypeTextAsync("hello", It.IsAny<int>(), new InputTarget(null, true), false, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AllowHostTarget_WithASelectorThatFindsWindowsByName_IsRefused_NothingIsSent(bool foregroundWithFilter)
    {
        // Arrange - a selector by name could resolve to the agent's own terminal without the agent ever seeing it
        var input = new Mock<IInputSimulationService>(MockBehavior.Strict);
        var tools = new InputSimulationTools(input.Object, _windows.Object);
        var selector = foregroundWithFilter ? new WindowSelector { Foreground = true, ProcessName = "WindowsTerminal" } : NotepadSelector;

        // Act
        var act = () => tools.TypeText("hello", window: selector, allowHostTarget: true);

        // Assert
        var error = (await act.Should().ThrowAsync<ToolErrorException>()).Which;
        error.ErrorCode.Should().Be(ToolErrorReporting.InvalidArgumentsErrorCode);
        error.Message.Should().Contain("window.handle").And.Contain("Nothing was sent");
        _windows.Verify(w => w.ResolveWindow(It.IsAny<WindowSelector>()), Times.Never);
    }

    [Fact]
    public void AllowHostTarget_WithASelectorThatNamesTheHandle_IsPassedOn()
    {
        // Arrange - window.handle with the process it must still be: the window was named on purpose
        var named = new WindowSelector { Handle = Notepad, ProcessId = 200 };
        _windows.Setup(w => w.ResolveWindow(named)).Returns(WindowResolution.Found(NotepadWindow));
        var input = new Mock<IInputSimulationService>();
        var tools = new InputSimulationTools(input.Object, _windows.Object);

        // Act
        tools.Click(5, 6, window: named, allowHostTarget: true);

        // Assert
        input.Verify(i => i.Click(5, 6, new InputTarget((nint)Notepad, true), false, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task TypeText_WithAnAmbiguousSelector_SendsNothing()
    {
        var input = new Mock<IInputSimulationService>(MockBehavior.Strict);
        var tools = new InputSimulationTools(input.Object, _windows.Object);

        var act = () => tools.TypeText("hello", window: ChromeSelector);

        (await act.Should().ThrowAsync<ToolErrorException>()).Which.ErrorCode.Should().Be("AmbiguousWindow");
    }

    [Fact]
    public async Task SendKeys_WithWindowHandleAndWindow_IsInvalidArguments()
    {
        var input = new Mock<IInputSimulationService>(MockBehavior.Strict);
        var tools = new InputSimulationTools(input.Object, _windows.Object);

        var act = () => tools.SendKeys("^s", windowHandle: Notepad, window: NotepadSelector);

        (await act.Should().ThrowAsync<ToolErrorException>()).Which.ErrorCode.Should().Be(ToolErrorReporting.InvalidArgumentsErrorCode);
    }

    [Fact]
    public void Scroll_WithASelectorAndNoPoint_ScrollsTheResolvedWindow()
    {
        var input = new Mock<IInputSimulationService>();
        var tools = new InputSimulationTools(input.Object, _windows.Object);

        tools.Scroll(window: NotepadSelector, deltaY: 3);

        input.Verify(i => i.Scroll(null, null, 3, 0, new InputTarget((nint)Notepad, false), false, It.IsAny<CancellationToken>()), Times.Once);
    }

    #endregion

    #region The reading tools

    [Fact]
    public async Task GetUiElements_WithASelector_ReadsTheResolvedWindow()
    {
        var elements = new Mock<IUIElementService>();
        elements.Setup(e => e.GetUIElementsAsync(It.IsAny<nint>(), It.IsAny<int>(), It.IsAny<UIElementFilter?>(),
                It.IsAny<IProgress<OperationProgress>?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new UIElementResult(true, Notepad, "notes.txt - Notepad", [], 0, 5, 1));
        var tools = new UIElementTools(elements.Object, Mock.Of<IElementInspectionService>(), _windows.Object);

        await tools.GetUIElements(window: NotepadSelector);

        elements.Verify(e => e.GetUIElementsAsync((nint)Notepad, It.IsAny<int>(), It.IsAny<UIElementFilter?>(),
            It.IsAny<IProgress<OperationProgress>?>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task GetBrowserUrl_WithASelector_ReadsTheResolvedWindow()
    {
        var browser = new Mock<IBrowserReadingService>();
        browser.Setup(b => b.GetBrowserUrlAsync(It.IsAny<nint>(), It.IsAny<CdpOptions?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new BrowserUrlResult(true, "https://example.com/", "chrome", "Example", null, BrowserErrorCode.None));
        var tools = new BrowserReadingTools(browser.Object, Mock.Of<IProcessService>(), _windows.Object);

        await tools.GetBrowserUrl(window: NotepadSelector);

        browser.Verify(b => b.GetBrowserUrlAsync((nint)Notepad, It.IsAny<CdpOptions?>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task GetOwnedWindows_WithASelector_ListsTheResolvedWindowsOwnedWindows()
    {
        _windows.Setup(w => w.GetOwnedWindows((nint)Notepad, false)).Returns([]);
        var tools = new WindowFinderTools(_windows.Object);

        var result = await Task.FromResult(tools.GetOwnedWindows(window: NotepadSelector));

        result.Success.Should().BeTrue();
        result.Handle.Should().Be(Notepad);
        _windows.Verify(w => w.GetOwnedWindows((nint)Notepad, false), Times.Once);
    }

    #endregion

    #region get_window_info

    [Fact]
    public void GetWindowInfo_Compact_ReportsOnlyTheIdentityAndState()
    {
        var result = new WindowFinderTools(_windows.Object).GetWindowInfo(handle: Notepad, detail: WindowDetail.Compact);

        result.Should().Be(new WindowDetailsResult(true, Notepad, "notes.txt - Notepad", "notepad", 200, "normal"));
    }

    [Fact]
    public void GetWindowInfo_Standard_ReportsWhatFindWindowsReports()
    {
        var result = new WindowFinderTools(_windows.Object).GetWindowInfo(window: NotepadSelector);

        result.Success.Should().BeTrue();
        result.Handle.Should().Be(Notepad);
        result.Bounds.Should().Be(new BoundsDto(100, 100, 800, 600));
        result.IsVisible.Should().BeTrue();
        result.ClassName.Should().Be("Notepad");
        result.IsForeground.Should().BeNull("only detail 'full' reads the extra state");
        result.VisibleBounds.Should().Be(
            NotepadWindow.VisibleBounds is { } visible ? new BoundsDto(visible.X, visible.Y, visible.Width, visible.Height) : null,
            "standard reports what find_windows reports, the visible frame included");
        _windows.Verify(w => w.GetWindowDetails(It.IsAny<nint>()), Times.Never);
    }

    [Fact]
    public void GetWindowInfo_Full_AddsTheStateThatExplainsAFailure()
    {
        // Arrange
        _windows.Setup(w => w.GetWindowDetails((nint)Notepad)).Returns(new WindowDetails(
            NotepadWindow with { VisibleBounds = new WindowRect(107, 100, 786, 593) },
            IsForeground: false, ZOrder: 3, IsTopmost: true, IsEnabled: false, IsResponding: false, Dpi: 144));
        var hitTest = new Mock<INativeHitTestWrapper>();
        hitTest.Setup(h => h.GetClientAreaOnScreen((nint)Notepad)).Returns(new WindowRect(108, 131, 784, 561));

        // Act
        var result = new WindowFinderTools(_windows.Object, hitTest.Object).GetWindowInfo(handle: Notepad, detail: WindowDetail.Full);

        // Assert
        result.Success.Should().BeTrue();
        result.VisibleBounds.Should().Be(new BoundsDto(107, 100, 786, 593));
        result.ClientBounds.Should().Be(new BoundsDto(108, 131, 784, 561));
        result.IsForeground.Should().BeFalse();
        result.ZOrder.Should().Be(3);
        result.IsTopmost.Should().BeTrue();
        result.IsEnabled.Should().BeFalse();
        result.IsResponding.Should().BeFalse();
        result.Dpi.Should().Be(144);
    }

    [Theory]
    [InlineData(WindowDetail.Compact)]
    [InlineData(WindowDetail.Full)]
    public void GetWindowInfo_OfAHandleThatIsNoWindow_IsInvalidHandle(WindowDetail detail)
    {
        var result = new WindowFinderTools(_windows.Object).GetWindowInfo(handle: 0x7777, detail: detail);

        result.Success.Should().BeFalse();
        result.ErrorCode.Should().Be(nameof(WindowErrorCode.InvalidHandle));
        result.Handle.Should().Be(0x7777);
    }

    #endregion
}
