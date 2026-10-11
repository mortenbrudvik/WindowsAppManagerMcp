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
        // Arrange - record whether the gate was free while the selector was resolved, and held while the window moved
        bool? gateFreeWhileResolving = null;
        bool? gateHeldWhileMoving = null;
        _windows.Setup(w => w.ResolveWindow(NotepadSelector)).Returns(() =>
        {
            using (var gate = InputGate.TryEnter(TimeSpan.FromSeconds(5), CancellationToken.None))
            {
                gateFreeWhileResolving = gate != null;
            }

            return WindowResolution.Found(NotepadWindow);
        });
        _windows.Setup(w => w.MoveWindow((nint)Notepad, 10, 20, true)).Returns(() =>
        {
            gateHeldWhileMoving = InputGate.IsHeld;
            return WindowChangeResult.Done((nint)Notepad);
        });

        // Act
        await CreateWindowTools().MoveWindow(10, 20, window: NotepadSelector);

        // Assert
        gateFreeWhileResolving.Should().BeTrue("a slow resolution must not hold up every input and window call");
        gateHeldWhileMoving.Should().BeTrue("the window changes inside the gate, as when the gate filter ran the tool");
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
    public void Click_WithASelector_TargetsTheResolvedWindow_KeepingAllowHostTarget()
    {
        // Arrange - the resolved window goes to the input service as windowHandle would: its guard checks it there
        var input = new Mock<IInputSimulationService>();
        input.Setup(i => i.Click(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<InputTarget>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .Returns(new ClickResult(true, 5, 6, "left"));
        var tools = new InputSimulationTools(input.Object, _windows.Object);

        // Act
        tools.Click(5, 6, window: NotepadSelector, allowHostTarget: true);

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
