using System.Diagnostics;
using WindowsAppManagerMcp.Infrastructure;

namespace WindowsAppManagerMcp.Tests.Unit.Services;

/// <summary>
/// get_desktop_state's observation (roadmap M3.12): the window in front, the cursor, the keys and the pause always; the
/// windows, the focused element, the target's popups and elements (with refs, through the shared reads) and a capture as
/// asked; one deadline for all of it, with partial results and an error per part that was not read.
/// </summary>
public class DesktopStateServiceTests
{
    private const ushort VkCapital = 0x14;
    private const ushort VkLControl = 0xA2;
    private static readonly nint Notepad = 0x1A0B2C;
    private static readonly nint Terminal = 0x2C0D4E;
    private static readonly nint Menu = 0x260A38;

    private readonly Mock<IWindowService> _windows = new();
    private readonly FakeInputWrapper _input = new();
    private readonly InputSession _session;
    private readonly Mock<IElementInspectionService> _inspection = new();
    private readonly Mock<IUIElementService> _elements = new();
    private readonly Mock<IPopupService> _popups = new();
    private readonly Mock<IScreenshotService> _screenshots = new();
    private readonly List<(nint Handle, UIElementFilter? Filter)> _walks = [];
    private ITargetPolicy _policy = new TargetPolicy(TargetPolicySettings.Default);
    private ToolsetSelection? _toolsets;

    public DesktopStateServiceTests()
    {
        _session = new InputSession(_input);
        var notepad = Window(Notepad, "notes.txt - Notepad", "notepad");
        var terminal = Window(Terminal, "pwsh", "WindowsTerminal") with { IsAgentHost = true };
        var all = new List<WindowInfo> { notepad, terminal };
        all.AddRange(Enumerable.Range(1, 30).Select(i => Window(0x400000 + i, $"Window {i}", "app")));

        _windows.Setup(w => w.GetForegroundWindow()).Returns(notepad);
        _windows.Setup(w => w.GetAllWindows(It.IsAny<bool>())).Returns(all);
        _windows.Setup(w => w.IsValidWindow(It.IsAny<nint>())).Returns((nint h) => all.Any(w => w.Handle == h));
        _windows.Setup(w => w.GetWindowInfo(It.IsAny<nint>())).Returns((nint h) => all.FirstOrDefault(w => w.Handle == h));
        _input.MovePointer(640, 360);

        _inspection.Setup(i => i.GetFocusedElementAsync(It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new FocusedElementResult(true,
                new UIElementDetails("Text editor", "Document", "RichEditD2DPT", "RichEditD2DPT", "Win32", new BoundsDto(8, 80, 1200, 700),
                    true, false, true, true, false, ["Value"], Value: "secret draft", Ref: "w4120035e3"),
                Notepad, "notes.txt - Notepad", 4242, "notepad", 3));
        _elements.Setup(e => e.GetUIElementsAsync(It.IsAny<nint>(), It.IsAny<int>(), It.IsAny<UIElementFilter?>(),
                It.IsAny<IProgress<OperationProgress>?>(), It.IsAny<CancellationToken>()))
            .Callback((nint handle, int _, UIElementFilter? filter, IProgress<OperationProgress>? _, CancellationToken _) =>
            {
                lock (_walks)
                {
                    _walks.Add((handle, filter));
                }
            })
            .ReturnsAsync((nint handle, int depth, UIElementFilter? _, IProgress<OperationProgress>? _, CancellationToken _) =>
                handle == Menu
                    ? Walk(handle, Element("w4120036e1", "MenuItem", "Delete"), Element("w4120036e2", "MenuItem", "Rename"))
                    : Walk(handle, Element("w4120035e7", "Button", "Save"), Element("w4120035e8", "Edit", "File name")));
        _popups.Setup(p => p.GetActivePopupsAsync(It.IsAny<nint>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((nint window, TimeSpan _, CancellationToken _) => new ActivePopupsResult(true, window, "notes.txt - Notepad", true,
                [new ActivePopup("w4120036e0", Menu, PopupKind.Menu, null, "#32768", 4242, "notepad", null, new BoundsDto(600, 200, 200, 300), "Menu")],
                2));
        _popups.Setup(p => p.IsInForeground(It.IsAny<nint>())).Returns(false);
        _popups.Setup(p => p.GetPopupList(It.IsAny<nint>())).Returns(new PopupList([], false));
    }

    private DesktopStateService CreateSut() => new(
        _windows.Object, _session, _input, _inspection.Object, _elements.Object, _popups.Object, _screenshots.Object, _policy, _toolsets);

    private static WindowInfo Window(nint handle, string title, string process) =>
        new(handle, title, process, 4000 + (int)(handle % 1000), new WindowRect(0, 0, 1280, 720), WindowState.Normal, true, 0);

    private static UIElementInfo Element(string elementRef, string type, string name) =>
        new(name, type, null, new BoundsDto(10, 20, 80, 24), true, false, Ref: elementRef);

    private static UIElementResult Walk(nint handle, params UIElementInfo[] elements) =>
        new(true, handle, "title", elements, elements.Length, 10, 5);

    private static DesktopStateRequest Request(
        nint? window = null, IReadOnlySet<DesktopStateSection>? include = null, int maxElements = 80, ElementSearchScope? scope = null,
        int maxWindows = 20, TimeSpan? timeout = null) =>
        new(window, include ?? DesktopStateRequest.DefaultInclude, maxElements, InteractableOnly: true, scope, maxWindows,
            ScreenshotMaxLongEdge: 1280, timeout ?? TimeSpan.FromSeconds(5));

    [Fact]
    public async Task TheDefaultObservation_ReadsTheWindowInFront_AndEverySectionButTheScreenshot()
    {
        // Arrange
        _input.Press(VkLControl);
        _input.Toggled.Add(VkCapital);

        // Act
        var (state, image) = await CreateSut().GetStateAsync(Request(), CancellationToken.None);

        // Assert
        state.Success.Should().BeTrue();
        state.Errors.Should().BeNull();
        state.WindowHandle.Should().Be(Notepad);
        state.WindowTitle.Should().Be("notes.txt - Notepad");
        state.Foreground.Should().Be(new DesktopWindow(Notepad, "notes.txt - Notepad", "notepad", 4000 + (int)(Notepad % 1000)));
        state.Cursor.Should().Be(new ScreenPoint(640, 360));
        state.Input.Paused.Should().BeFalse();
        state.Input.ModifiersDown.Should().ContainSingle().Which.Should().Contain("Ctrl");
        state.Input.CapsLock.Should().BeTrue();
        state.Focus!.Ref.Should().Be("w4120035e3");
        state.Focus.ControlType.Should().Be("Document");
        state.Focus.WindowHandle.Should().Be(Notepad);
        state.Popups.Should().ContainSingle().Which.Ref.Should().Be("w4120036e0");
        state.Elements!.Text.Should().Be("w4120035e7 Button 'Save' @10,20 80x24\nw4120035e8 Edit 'File name' @10,20 80x24");
        state.Elements.Count.Should().Be(2);
        state.Elements.Scope.Should().Be(ElementSearchScope.Window);
        state.Screenshot.Should().BeNull();
        image.Should().BeNull();
        _screenshots.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task TheElements_AreReadThroughTheSharedWalk_WithItsFiltersAndWarmUp()
    {
        await CreateSut().GetStateAsync(Request(maxElements: 40), CancellationToken.None);

        var (handle, filter) = _walks.Should().ContainSingle().Subject;
        handle.Should().Be(Notepad);
        filter!.InteractableOnly.Should().BeTrue();
        filter.VisibleOnly.Should().BeTrue();
        filter.MaxElements.Should().Be(40);
        filter.AccessibilityWarmupBudget.Should().NotBeNull("web content is warmed up as get_ui_elements does");
    }

    [Fact]
    public async Task TheFocusedElementsValue_IsNeverReturned()
    {
        var (state, _) = await CreateSut().GetStateAsync(Request(), CancellationToken.None);

        System.Text.Json.JsonSerializer.Serialize(state, ToolJson.SerializerOptions).Should().NotContain("secret draft");
    }

    [Fact]
    public async Task TheWindows_AreCompactRowsFrontFirst_CutAtMaxWindows_WithTheTotal()
    {
        var (state, _) = await CreateSut().GetStateAsync(Request(maxWindows: 5), CancellationToken.None);

        state.Windows.Should().HaveCount(5);
        state.Windows![0].Should().Be(new DesktopWindowRow(Notepad, "notes.txt - Notepad", "notepad", "normal", 0));
        state.Windows[1].IsAgentHost.Should().BeTrue("the agent's own terminal is marked");
        state.WindowCount.Should().Be(32);
    }

    [Fact]
    public async Task OnlyTheSectionsAskedFor_AreRead()
    {
        var (state, _) = await CreateSut().GetStateAsync(Request(include: new HashSet<DesktopStateSection> { DesktopStateSection.Focus }),
            CancellationToken.None);

        state.Focus.Should().NotBeNull();
        state.Windows.Should().BeNull();
        state.Elements.Should().BeNull();
        state.Popups.Should().BeNull();
        state.Cursor.Should().NotBeNull("the cheap parts are always read");
        _elements.VerifyNoOtherCalls();
        _popups.Verify(p => p.GetActivePopupsAsync(It.IsAny<nint>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ThePause_IsReported()
    {
        _session.Pause(InputControlSources.Hotkey);

        var (state, _) = await CreateSut().GetStateAsync(Request(), CancellationToken.None);

        state.Input.Paused.Should().BeTrue();
        state.Input.PausedBy.Should().Be(InputControlSources.Hotkey);
    }

    [Fact]
    public async Task TheAgentsOwnTerminalInFront_IsNotRead_ButTheRestIs()
    {
        // The agent's terminal is in front while it works: its elements are the conversation, not the app it means
        _windows.Setup(w => w.GetForegroundWindow()).Returns(Window(Terminal, "pwsh", "WindowsTerminal") with { IsAgentHost = true });

        var (state, _) = await CreateSut().GetStateAsync(Request(), CancellationToken.None);

        state.Success.Should().BeTrue();
        state.WindowHandle.Should().Be(0);
        state.Foreground!.IsAgentHost.Should().BeTrue();
        state.Windows.Should().NotBeEmpty();
        state.Elements.Should().BeNull();
        state.Popups.Should().BeNull();
        var error = state.Errors.Should().ContainSingle().Subject;
        error.Section.Should().Be("target");
        error.ErrorCode.Should().Be(nameof(InputErrorCode.TargetIsAgentHost));
        error.Error.Should().Contain("window");
        _elements.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task TheAgentsTerminal_IsRead_WhenNamed()
    {
        var (state, _) = await CreateSut().GetStateAsync(Request(window: Terminal), CancellationToken.None);

        state.WindowHandle.Should().Be(Terminal);
        state.Elements.Should().NotBeNull("a named window is read as get_ui_elements reads it");
    }

    [Fact]
    public async Task AWindowThePolicyDenies_IsNotReadUnasked_ButIsWhenNamed()
    {
        _policy = new TargetPolicy(TargetPolicySettings.Default with { DeniedProcesses = ["notepad"] });

        var (unnamed, _) = await CreateSut().GetStateAsync(Request(), CancellationToken.None);
        var (named, _) = await CreateSut().GetStateAsync(Request(window: Notepad), CancellationToken.None);

        unnamed.Elements.Should().BeNull();
        unnamed.Errors.Should().ContainSingle().Which.ErrorCode.Should().Be(nameof(InputErrorCode.TargetDeniedByPolicy));
        named.Elements.Should().NotBeNull();
    }

    [Fact]
    public async Task NoWindowInFront_LeavesTheTargetOut_WithNoForegroundWindow()
    {
        _windows.Setup(w => w.GetForegroundWindow()).Returns((WindowInfo?)null);

        var (state, _) = await CreateSut().GetStateAsync(Request(), CancellationToken.None);

        state.Success.Should().BeTrue();
        state.Foreground.Should().BeNull();
        state.Errors.Should().ContainSingle().Which.ErrorCode.Should().Be(nameof(WindowErrorCode.NoForegroundWindow));
        state.Windows.Should().NotBeEmpty();
    }

    [Fact]
    public async Task ANamedHandleThatIsNoWindow_FailsTheCall()
    {
        var (state, _) = await CreateSut().GetStateAsync(Request(window: 0x999), CancellationToken.None);

        state.Success.Should().BeFalse();
        state.ErrorCode.Should().Be(nameof(UIElementErrorCode.InvalidHandle));
        state.Error.Should().Contain("get_desktop_state");
    }

    [Fact]
    public async Task APartThatDoesNotAnswer_IsReportedAtTheDeadline_AndTheRestIsReturned()
    {
        // Arrange - the walk never returns, not even when it is asked to stop
        var never = new TaskCompletionSource<UIElementResult>();
        _elements.Setup(e => e.GetUIElementsAsync(It.IsAny<nint>(), It.IsAny<int>(), It.IsAny<UIElementFilter?>(),
                It.IsAny<IProgress<OperationProgress>?>(), It.IsAny<CancellationToken>()))
            .Returns(never.Task);
        var stopwatch = Stopwatch.StartNew();

        // Act
        var (state, _) = await CreateSut().GetStateAsync(Request(timeout: TimeSpan.FromSeconds(1)), CancellationToken.None);

        // Assert
        stopwatch.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(3), "one deadline covers the whole call");
        state.Success.Should().BeTrue();
        state.Elements.Should().BeNull();
        var error = state.Errors.Should().ContainSingle().Subject;
        error.Section.Should().Be("elements");
        error.ErrorCode.Should().Be(nameof(UIElementErrorCode.OperationTimedOut));
        state.Focus.Should().NotBeNull();
        state.Windows.Should().NotBeEmpty();
        state.Popups.Should().NotBeNull();
    }

    [Fact]
    public async Task APartThatFails_IsReportedWithItsOwnCode()
    {
        _elements.Setup(e => e.GetUIElementsAsync(It.IsAny<nint>(), It.IsAny<int>(), It.IsAny<UIElementFilter?>(),
                It.IsAny<IProgress<OperationProgress>?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new UIElementResult(false, Notepad, null, null, 0, 0, 1, "The window is not responding.", UIElementErrorCode.NotResponding));
        _inspection.Setup(i => i.GetFocusedElementAsync(It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("provider failed"));

        var (state, _) = await CreateSut().GetStateAsync(Request(), CancellationToken.None);

        state.Success.Should().BeTrue();
        state.Errors.Should().BeEquivalentTo(
        [
            new { Section = "focus", ErrorCode = nameof(UIElementErrorCode.OperationException) },
            new { Section = "elements", ErrorCode = nameof(UIElementErrorCode.NotResponding) }
        ]);
    }

    [Fact]
    public async Task TheElementsWalk_StopsWithTheDeadline_KeepingWhatItRead()
    {
        // A cooperative walk returns what it read when its token is cancelled
        _elements.Setup(e => e.GetUIElementsAsync(It.IsAny<nint>(), It.IsAny<int>(), It.IsAny<UIElementFilter?>(),
                It.IsAny<IProgress<OperationProgress>?>(), It.IsAny<CancellationToken>()))
            .Returns(async (nint handle, int _, UIElementFilter? _, IProgress<OperationProgress>? _, CancellationToken token) =>
            {
                await Task.Delay(Timeout.Infinite, token).ContinueWith(_ => { }, TaskScheduler.Default);
                return Walk(handle, Element("w1e1", "Button", "OK")) with
                {
                    Truncated = true, TruncationReason = UIElementTruncationReason.Timeout
                };
            });

        var (state, _) = await CreateSut().GetStateAsync(Request(timeout: TimeSpan.FromSeconds(1)), CancellationToken.None);

        state.Elements!.Count.Should().Be(1);
        state.Elements.Truncated.Should().BeTrue();
        state.Elements.TruncationReason.Should().Be(UIElementTruncationReason.Timeout);
    }

    [Fact]
    public async Task WithTheTargetInFront_ItsOpenPopupsAreWalkedFirst_UnderHeaders_WithinMaxElements()
    {
        // Arrange - the default scope is find_ui_elements': windowAndPopups while the window or a popup is in front
        _popups.Setup(p => p.IsInForeground(Notepad)).Returns(true);
        _popups.Setup(p => p.GetPopupList(Notepad)).Returns(new PopupList(
        [
            new PopupWindow(Menu, PopupKind.Menu, null, "#32768", 4242, 0, PopupRelation.SameProcess, null, false),
            new PopupWindow(0x260A40, PopupKind.Tooltip, "Save", "tooltips_class32", 4242, 0, PopupRelation.SameProcess, null, false)
        ], false));

        // Act
        var (state, _) = await CreateSut().GetStateAsync(Request(maxElements: 10), CancellationToken.None);

        // Assert
        state.Elements!.Scope.Should().Be(ElementSearchScope.WindowAndPopups);
        state.Elements.Text.Should().Be(
            "# popup 2492984 Menu\n" +
            "w4120036e1 MenuItem 'Delete' @10,20 80x24\nw4120036e2 MenuItem 'Rename' @10,20 80x24\n" +
            "# window 1706796 'notes.txt - Notepad'\n" +
            "w4120035e7 Button 'Save' @10,20 80x24\nw4120035e8 Edit 'File name' @10,20 80x24");
        state.Elements.Count.Should().Be(4);
        _walks.Select(w => w.Handle).Should().Equal([Menu, Notepad], "a tooltip is not walked, and the popups come first");
        _walks[0].Filter!.MaxElements.Should().Be(5, "the popups get at most half of maxElements");
        _walks[1].Filter!.MaxElements.Should().Be(8, "the window gets what the popups left");
    }

    [Fact]
    public async Task APopupWhoseProviderFails_IsSkipped_AndTheWindowIsStillRead()
    {
        _popups.Setup(p => p.IsInForeground(Notepad)).Returns(true);
        _popups.Setup(p => p.GetPopupList(Notepad)).Returns(new PopupList(
            [new PopupWindow(Menu, PopupKind.Menu, null, "#32768", 4242, 0, PopupRelation.SameProcess, null, false)], false));
        _elements.Setup(e => e.GetUIElementsAsync(Menu, It.IsAny<int>(), It.IsAny<UIElementFilter?>(),
                It.IsAny<IProgress<OperationProgress>?>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("provider failed"));

        var (state, _) = await CreateSut().GetStateAsync(Request(), CancellationToken.None);

        state.Errors.Should().BeNull();
        state.Elements!.Count.Should().Be(2, "the window's two elements are read");
        state.Elements.Text.Should().NotContain("# popup");
        state.Elements.Truncated.Should().BeTrue();
        state.Elements.TruncationReason.Should().Be(UIElementTruncationReason.ElementsSkipped);
    }

    [Fact]
    public async Task ScopeWindow_WalksOnlyTheWindow()
    {
        _popups.Setup(p => p.IsInForeground(Notepad)).Returns(true);

        var (state, _) = await CreateSut().GetStateAsync(Request(scope: ElementSearchScope.Window), CancellationToken.None);

        state.Elements!.Scope.Should().Be(ElementSearchScope.Window);
        _popups.Verify(p => p.GetPopupList(It.IsAny<nint>()), Times.Never);
    }

    [Fact]
    public async Task TheScreenshot_IsCaptureWindowsCapture_AtTheLongEdgeAskedFor()
    {
        // Arrange
        CaptureOptions? options = null;
        var metadata = new ScreenshotResult(true, "jpeg", "image/jpeg", 1280, 720, new CapturedRegion(0, 0, 1600, 900),
            new ImageToScreenTransform(0, 0, 1.25, 1.25), 0, 1.25, CaptureId: "c1-ab12");
        _screenshots.Setup(s => s.CaptureWindow(Notepad, true, It.IsAny<CaptureOptions>()))
            .Callback((nint _, bool _, CaptureOptions o) => options = o)
            .Returns(new ScreenCapture<ScreenshotResult>(metadata, new EncodedImage([1, 2, 3], "image/jpeg")));
        var include = new HashSet<DesktopStateSection>(DesktopStateRequest.DefaultInclude) { DesktopStateSection.Screenshot };

        // Act
        var (state, image) = await CreateSut().GetStateAsync(Request(include: include), CancellationToken.None);

        // Assert
        image!.MimeType.Should().Be("image/jpeg");
        state.Screenshot!.CaptureId.Should().Be("c1-ab12");
        options!.MaxDimension.Should().Be(1280);
        options.Output.Should().Be("image", "nothing is written to a file");
    }

    [Fact]
    public async Task TheScreenshot_OfAWindowThePolicyDenies_IsRefused_EvenWhenNamed()
    {
        _policy = new TargetPolicy(TargetPolicySettings.Default with { DeniedProcesses = ["notepad"] });
        var include = new HashSet<DesktopStateSection> { DesktopStateSection.Screenshot };

        var (state, image) = await CreateSut().GetStateAsync(Request(window: Notepad, include: include), CancellationToken.None);

        image.Should().BeNull();
        state.Errors.Should().ContainSingle().Which.Should().BeEquivalentTo(
            new { Section = "screenshot", ErrorCode = nameof(ScreenshotErrorCode.TargetDeniedByPolicy) });
        _screenshots.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task AFailedCapture_IsReportedWithItsCode()
    {
        _screenshots.Setup(s => s.CaptureWindow(Notepad, true, It.IsAny<CaptureOptions>()))
            .Returns(new ScreenCapture<ScreenshotResult>(new ScreenshotResult(false, null, null, 0, 0, null, null, null, null,
                "The window is minimized.", nameof(ScreenshotErrorCode.WindowMinimized)), null));

        var (state, image) = await CreateSut().GetStateAsync(
            Request(include: new HashSet<DesktopStateSection> { DesktopStateSection.Screenshot }), CancellationToken.None);

        image.Should().BeNull();
        state.Screenshot.Should().BeNull();
        state.Errors.Should().ContainSingle().Which.ErrorCode.Should().Be(nameof(ScreenshotErrorCode.WindowMinimized));
    }

    [Fact]
    public void TheScreenshot_IsUnavailable_WhenTheUserTurnedCapturesOff()
    {
        CreateSut().ScreenshotUnavailable().Should().BeNull("every tool is served by default");

        _toolsets = ToolsetSelection.FromEnvironment(
            name => name == ToolsetSelection.ToolsetsVariable ? "-capture" : null, Toolsets.Server);
        CreateSut().ScreenshotUnavailable().Should().Contain("capture_window");

        _toolsets = ToolsetSelection.FromEnvironment(
            name => name == ToolsetSelection.ReadOnlyVariable ? "1" : null, Toolsets.Server);
        CreateSut().ScreenshotUnavailable().Should().NotBeNull("read-only mode serves no capture tool");
    }

    [Fact]
    public async Task ACancelledCall_IsCancelled()
    {
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        var act = () => CreateSut().GetStateAsync(Request(), cancelled.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }
}
