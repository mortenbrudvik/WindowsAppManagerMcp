using System.Drawing;
using System.Drawing.Imaging;
using System.Text.Json;
using WindowsAppManagerMcp.Infrastructure;

namespace WindowsAppManagerMcp.Tests.Unit.Services;

/// <summary>
/// BrowserPageService (roadmap M5.8): which browser, port and tab the DOM bridge works on (as the tab tools find them),
/// the refusal of the browser's own pages, the gate it hands to CDP, how an element's CSS rectangle is placed on the
/// screen, how a script's value is bounded and how a capture is scaled. CDP is mocked (<see cref="ICdpPageService"/>,
/// <see cref="ICdpService"/>); its fakes are in CdpPageServiceTests.
/// </summary>
public class BrowserPageServiceTests
{
    private const int Port = 9222;
    private const uint BrowserPid = 4242;
    private static readonly nint BrowserWindow = 0x2B0C3D;

    private readonly Mock<INativeWindowWrapper> _windows = new();
    private readonly Mock<IBrowserReadingService> _reading = new();
    private readonly Mock<ICdpService> _cdp = new();
    private readonly Mock<ICdpPageService> _pages = new();
    private readonly Mock<IBrowserViewportLocator> _viewports = new();
    private readonly Mock<IDebugPortLocator> _locator = new();
    private readonly Mock<IBrowserDialogService> _dialogs = new();
    private readonly InputSession _session = new(new FakeInputWrapper());
    private ITargetPolicy _policy = new TargetPolicy(TargetPolicySettings.Default);
    private FakePortOwnerResolver _owners = new(owner: (int)BrowserPid);

    private static readonly CdpTarget Shown = new("SHOWN", "page", "Form", "https://example.com/form", "ws://127.0.0.1:9222/devtools/page/SHOWN", Port);

    private static readonly BrowserElementLocator Save = new(null, null, "button", "Save draft");

    public BrowserPageServiceTests()
    {
        _windows.Setup(w => w.IsWindow(BrowserWindow)).Returns(true);
        _windows.Setup(w => w.GetAncestor(BrowserWindow, WindowAncestor.Root)).Returns(BrowserWindow);
        _windows.Setup(w => w.GetWindowThreadProcessId(BrowserWindow)).Returns(BrowserPid);
        _windows.Setup(w => w.GetProcessName(BrowserPid)).Returns("msedge");
        _windows.Setup(w => w.GetWindowText(BrowserWindow)).Returns("Form - Microsoft Edge");
        _windows.Setup(w => w.GetClassName(BrowserWindow)).Returns("Chrome_WidgetWin_1");
        _windows.Setup(w => w.GetWindowRect(BrowserWindow)).Returns((0, 0, 1920, 1080));
        _windows.Setup(w => w.GetDpiForWindow(BrowserWindow)).Returns(144u);
        _reading.Setup(r => r.GetBrowserType(It.IsAny<nint>(), It.IsAny<string>())).Returns("edge");
        _locator.Setup(l => l.Find((int)BrowserPid)).Returns(new DebugPortLookup(DebugPortLookupStatus.Found, Port, "found"));
        ShowTab(Shown);
    }

    private void ShowTab(CdpTarget tab)
    {
        _cdp.Setup(c => c.FindTargetForWindowAsync(Port, It.IsAny<CdpWindow>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(CdpResult<CdpTarget>.Ok(tab));
        _cdp.Setup(c => c.FindTargetByIdAsync(Port, It.IsAny<int>(), tab.Id, It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(CdpResult<CdpTarget>.Ok(tab));
    }

    private BrowserPageService CreateSut() => new(
        _windows.Object, new BrowserDetectionService(), _reading.Object, _cdp.Object, _pages.Object, _owners, _viewports.Object, _policy,
        _locator.Object, _session, _dialogs.Object);

    private static BrowserTabTarget ByWindow(string? targetId = null) => new(BrowserWindow, null, targetId);

    /// <summary>
    /// The element box CDP reports: 88 x 32 CSS pixels at (920, 180) in a 1280 x 720 viewport at devicePixelRatio 1.5, on a
    /// shown page; its boxes are the rectangle alone unless given.
    /// </summary>
    private void LocateSave(double dpr = 1.5, double scale = 1, CssRect? rect = null, string visibility = "visible", CssRect[]? quads = null,
        CdpTarget? tab = null)
    {
        var box = rect ?? new CssRect(920, 180, 88, 32);
        _pages.Setup(p => p.LocateAsync(tab ?? Shown, Save, It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(CdpPageResult<CdpElementBox>.Ok(new CdpElementBox(new BrowserElementInfo("button", "Save draft"), 1,
                box, dpr, 1280, 720, scale, visibility, quads ?? [box])));
    }

    #region Which browser and tab

    [Fact]
    public async Task AWindowThePolicyDenies_IsRefused_AndNothingIsSent()
    {
        _policy = new TargetPolicy(TargetPolicySettings.Default with { DeniedProcesses = ["msedge"] });

        var result = await CreateSut().ClickAsync(ByWindow(), Save, MouseButton.Primary, 1);

        result.ErrorCode.Should().Be(nameof(BrowserErrorCode.TargetDeniedByPolicy));
        _pages.VerifyNoOtherCalls();
        _locator.Verify(l => l.Find(It.IsAny<int>()), Times.Never);
    }

    [Fact]
    public async Task APortAloneOfAProcessThePolicyDenies_IsRefused()
    {
        _policy = new TargetPolicy(TargetPolicySettings.Default with { DeniedProcesses = ["msedge"] });

        var result = await CreateSut().EvaluateAsync(new BrowserTabTarget(null, Port, "SHOWN"), "1", true, 1000);

        result.ErrorCode.Should().Be(nameof(BrowserErrorCode.TargetDeniedByPolicy));
        _pages.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task AWindowWithoutADebugPort_IsCdpUnavailable_NamingTheDomBridge()
    {
        _locator.Setup(l => l.Find((int)BrowserPid)).Returns(new DebugPortLookup(DebugPortLookupStatus.NoDebugPort, null, "No switch."));

        var result = await CreateSut().TypeAsync(ByWindow(), new BrowserTypeOptions("x", null, false, false));

        result.ErrorCode.Should().Be(nameof(BrowserErrorCode.CdpUnavailable));
        result.Error.Should().Contain("The DOM bridge tools").And.Contain("launch_browser_with_debug");
        _pages.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task ATabTheWindowDoesNotHave_IsItsLookupsFailure()
    {
        _cdp.Setup(c => c.FindTargetByIdAsync(Port, (int)BrowserPid, "GONE", It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(CdpResult<CdpTarget>.Fail(BrowserErrorCode.CdpTargetNotFound, "No tab with that id."));

        var result = await CreateSut().ClickAsync(ByWindow("GONE"), Save, MouseButton.Primary, 1);

        result.ErrorCode.Should().Be(nameof(BrowserErrorCode.CdpTargetNotFound));
        result.CdpPort.Should().Be(Port);
        _pages.VerifyNoOtherCalls();
    }

    [Theory]
    [InlineData("edge://settings/passwords")]
    [InlineData("chrome://flags")]
    [InlineData("chrome-extension://abcdefghijklmnopabcdefghijklmnop/popup.html")]
    [InlineData("devtools://devtools/bundled/inspector.html")]
    [InlineData("about:version")]
    [InlineData("view-source:https://example.com/")]
    [InlineData("blob:chrome-extension://abcdefghijklmnopabcdefghijklmnop/6f1c0e1a")]
    [InlineData("isolated-app://aerugqztij5biqquuk3mfwpsaibuegaqcitgfchwuosuofdjabzqaaic/")]
    public async Task ATabShowingOneOfTheBrowsersOwnPages_IsBrowserInternalPage_AndNothingIsSent(string url)
    {
        ShowTab(Shown with { Url = url });

        var evaluate = await CreateSut().EvaluateAsync(ByWindow(), "document.title", true, 1000);
        var click = await CreateSut().ClickAsync(ByWindow(), Save, MouseButton.Primary, 1);
        var capture = await CreateSut().CaptureAsync(ByWindow(), 1568);

        evaluate.ErrorCode.Should().Be(nameof(BrowserErrorCode.BrowserInternalPage));
        click.ErrorCode.Should().Be(nameof(BrowserErrorCode.BrowserInternalPage));
        capture.Metadata.ErrorCode.Should().Be(nameof(BrowserErrorCode.BrowserInternalPage));
        capture.Image.Should().BeNull();
        _pages.VerifyNoOtherCalls();
    }

    [Theory]
    [InlineData("https://example.com/")]
    [InlineData("http://127.0.0.1:8080/form")]
    [InlineData("file:///C:/Users/me/AppData/Local/Temp/page.html")]
    [InlineData("data:text/html,<p>x</p>")]
    [InlineData("about:blank")]
    public async Task ATabShowingAWebPage_IsWorkedOn(string url)
    {
        ShowTab(Shown with { Url = url });
        _pages.Setup(p => p.EvaluateAsync(It.IsAny<CdpTarget>(), "1", true, It.IsAny<CdpGate>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(CdpPageResult<CdpEvaluation>.Ok(new CdpEvaluation(JsonSerializer.SerializeToElement(1), "number")));

        var result = await CreateSut().EvaluateAsync(ByWindow(), "1", true, 1000);

        result.Success.Should().BeTrue(result.Error);
    }

    #endregion

    #region The gate

    [Fact]
    public async Task TheGateHandedToCdp_RefusesWhileInputIsPaused()
    {
        CdpGate? handed = null;
        _pages.Setup(p => p.ClickAsync(Shown, Save, MouseButton.Primary, 1, It.IsAny<CdpGate>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>(), It.IsAny<BrowserDialogAnswer?>()))
            .Callback((CdpTarget _, BrowserElementLocator _, MouseButton _, int _, CdpGate gate, TimeSpan _, CancellationToken _, BrowserDialogAnswer? _) => handed = gate)
            .ReturnsAsync(CdpPageResult<CdpInputOutcome>.Ok(new CdpInputOutcome()));
        await CreateSut().ClickAsync(ByWindow(), Save, MouseButton.Primary, 1);
        _session.Pause(InputControlSources.Hotkey);

        var enter = () => handed!(CancellationToken.None);

        (await enter.Should().ThrowAsync<ToolErrorException>()).Which.ErrorCode.Should().Be(nameof(WindowErrorCode.PausedByUser));
    }

    [Fact]
    public async Task TheGateHandedToCdp_TellsAPauseWhileItIsHeld()
    {
        CdpGate? handed = null;
        _pages.Setup(p => p.ClickAsync(Shown, Save, MouseButton.Primary, 1, It.IsAny<CdpGate>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>(), It.IsAny<BrowserDialogAnswer?>()))
            .Callback((CdpTarget _, BrowserElementLocator _, MouseButton _, int _, CdpGate gate, TimeSpan _, CancellationToken _, BrowserDialogAnswer? _) => handed = gate)
            .ReturnsAsync(CdpPageResult<CdpInputOutcome>.Ok(new CdpInputOutcome()));
        await CreateSut().ClickAsync(ByWindow(), Save, MouseButton.Primary, 1);

        using var hold = await handed!(CancellationToken.None);
        var before = ((ICdpGateHold)hold).IsPaused;
        _session.Pause(InputControlSources.Hotkey);

        before.Should().BeFalse();
        ((ICdpGateHold)hold).IsPaused.Should().BeTrue("a click or a typing stops between its commands once the user pauses");
    }

    [Fact]
    public async Task TheGateHandedToCdp_TellsHowInputWasPaused_SoThePausedByUserMessageNamesTheCause()
    {
        CdpGate? handed = null;
        _pages.Setup(p => p.ClickAsync(Shown, Save, MouseButton.Primary, 1, It.IsAny<CdpGate>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>(), It.IsAny<BrowserDialogAnswer?>()))
            .Callback((CdpTarget _, BrowserElementLocator _, MouseButton _, int _, CdpGate gate, TimeSpan _, CancellationToken _, BrowserDialogAnswer? _) => handed = gate)
            .ReturnsAsync(CdpPageResult<CdpInputOutcome>.Ok(new CdpInputOutcome()));
        await CreateSut().ClickAsync(ByWindow(), Save, MouseButton.Primary, 1);

        using var hold = await handed!(CancellationToken.None);
        _session.Pause(InputControlSources.SharedPauseUnreadable);

        ((ICdpGateHold)hold).PausedBy.Should().Be(InputControlSources.SharedPauseUnreadable);
        ((ICdpGateHold)hold).PauseChord.Should().Be(_session.PauseChord);
    }

    #endregion

    #region browser_element_rect

    [Fact]
    public async Task ElementRect_PlacesTheCssRectOnTheViewport_AtItsDevicePixelRatio()
    {
        LocateSave();
        _viewports.Setup(v => v.FindAsync(BrowserWindow, Shown.Url, 1920, 1080, It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new BrowserViewportLookup(new BrowserViewport(102, 211, 1920, 1080, BrowserViewport.DocumentSource)));

        var result = await CreateSut().GetElementRectAsync(ByWindow(), Save);

        result.Success.Should().BeTrue(result.Error);
        result.ScreenRect.Should().Be(new BoundsDto(102 + 1380, 211 + 270, 132, 48));
        result.ClickPoint.Should().Be(new ScreenPoint(102 + 1446, 211 + 294));
        result.CssRect.Should().Be(new CssRect(920, 180, 88, 32));
        result.DevicePixelRatio.Should().Be(1.5);
        result.Anchor.Should().Be("document");
        result.Element.Should().Be(new BrowserElementInfo("button", "Save draft"));
    }

    [Fact]
    public async Task ElementRect_OnAMonitorLeftOfThePrimaryOne_AtOneHundredPercent_IsInNegativeCoordinates()
    {
        LocateSave(dpr: 1);
        _viewports.Setup(v => v.FindAsync(BrowserWindow, Shown.Url, 1280, 720, It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new BrowserViewportLookup(new BrowserViewport(-1904, 87, 1280, 720, BrowserViewport.RenderWidgetSource)));

        var result = await CreateSut().GetElementRectAsync(ByWindow(), Save);

        result.ScreenRect.Should().Be(new BoundsDto(-1904 + 920, 87 + 180, 88, 32));
        result.ClickPoint.Should().Be(new ScreenPoint(-1904 + 964, 87 + 196));
        result.Anchor.Should().Be("renderWidget");
    }

    [Fact]
    public async Task ElementRect_ScalesByTheViewportsOwnPixels_WhenTheyDifferALittleFromTheRatio()
    {
        // Browser zoom 110 % on a 125 % monitor: devicePixelRatio 1.375, and a viewport the screen rounds to whole pixels
        LocateSave(dpr: 1.375, rect: new CssRect(100, 100, 100, 100));
        _viewports.Setup(v => v.FindAsync(BrowserWindow, Shown.Url, 1760, 990, It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new BrowserViewportLookup(new BrowserViewport(0, 0, 1761, 990, BrowserViewport.DocumentSource)));

        var result = await CreateSut().GetElementRectAsync(ByWindow(), Save);

        result.ScreenRect!.X.Should().Be((int)Math.Round(100 * 1761 / 1280.0));
        result.ScreenRect.Y.Should().Be((int)Math.Round(100 * 990 / 720.0));
    }

    [Fact]
    public async Task ElementRect_OutsideTheViewport_HasNoClickPoint()
    {
        LocateSave(rect: new CssRect(10, 2000, 50, 20));
        _viewports.Setup(v => v.FindAsync(It.IsAny<nint>(), It.IsAny<string>(), It.IsAny<double>(), It.IsAny<double>(), It.IsAny<TimeSpan>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new BrowserViewportLookup(new BrowserViewport(0, 0, 1920, 1080, BrowserViewport.DocumentSource)));

        var result = await CreateSut().GetElementRectAsync(ByWindow(), Save);

        result.Success.Should().BeTrue();
        result.ClickPoint.Should().BeNull();
        result.ScreenRect!.Y.Should().Be(3000);
    }

    [Fact]
    public async Task ElementRect_ClickPoint_IsTheMiddleOfTheFirstBox_NotOfAWrappedElementsBoxesTogether()
    {
        // A link that wraps from the end of one line to the start of the next: the middle of both boxes together is between
        // the lines, on other text
        LocateSave(dpr: 1, rect: new CssRect(0, 100, 400, 40), quads: [new CssRect(300, 100, 100, 20), new CssRect(0, 120, 50, 20)]);
        _viewports.Setup(v => v.FindAsync(BrowserWindow, Shown.Url, 1280, 720, It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new BrowserViewportLookup(new BrowserViewport(0, 0, 1280, 720, BrowserViewport.DocumentSource)));

        var result = await CreateSut().GetElementRectAsync(ByWindow(), Save);

        result.ClickPoint.Should().Be(new ScreenPoint(350, 110), "browser_click aims at the same point");
        result.ScreenRect.Should().Be(new BoundsDto(0, 100, 400, 40));
    }

    [Theory]
    [InlineData("hidden")]
    [InlineData("prerender")]
    public async Task ElementRect_OfAPageThatIsNotShown_IsContentAreaNotFound_WithoutLookingForTheViewport(string visibility)
    {
        // A tab behind another, or a covered window: what the window shows there is another page's (or nothing)
        LocateSave(visibility: visibility);

        var result = await CreateSut().GetElementRectAsync(ByWindow(), Save);

        result.ErrorCode.Should().Be(nameof(BrowserErrorCode.ContentAreaNotFound));
        result.Error.Should().Contain("not shown").And.Contain("activate_tab").And.Contain("focus_window");
        result.CssRect.Should().Be(new CssRect(920, 180, 88, 32));
        result.ScreenRect.Should().BeNull();
        _viewports.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task ElementRect_OfATabTheWindowDoesNotShow_IsContentAreaNotFound_AndNothingIsRead()
    {
        // Every tab of a window has the shown tab's viewport, so a background tab's element would be placed on the shown page
        var background = Shown with { Id = "BACK", Url = "https://example.com/" };
        _cdp.Setup(c => c.FindTargetByIdAsync(Port, It.IsAny<int>(), "BACK", It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(CdpResult<CdpTarget>.Ok(background));

        var result = await CreateSut().GetElementRectAsync(ByWindow("BACK"), Save);

        result.ErrorCode.Should().Be(nameof(BrowserErrorCode.ContentAreaNotFound));
        result.Error.Should().Contain("it shows tab SHOWN").And.Contain("activate_tab");
        _pages.VerifyNoOtherCalls();
        _viewports.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task ElementRect_OfTheShownTabNamedById_IsPlaced()
    {
        LocateSave();
        _viewports.Setup(v => v.FindAsync(BrowserWindow, Shown.Url, 1920, 1080, It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new BrowserViewportLookup(new BrowserViewport(0, 0, 1920, 1080, BrowserViewport.DocumentSource)));

        var result = await CreateSut().GetElementRectAsync(ByWindow("SHOWN"), Save);

        result.Success.Should().BeTrue(result.Error);
    }

    [Fact]
    public async Task ElementRect_WhenTheViewportIsNotFound_IsContentAreaNotFound_WithTheCssRect()
    {
        LocateSave();
        _viewports.Setup(v => v.FindAsync(It.IsAny<nint>(), It.IsAny<string>(), It.IsAny<double>(), It.IsAny<double>(), It.IsAny<TimeSpan>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new BrowserViewportLookup(null, "UI Automation shows no Document with the tab's address"));

        var result = await CreateSut().GetElementRectAsync(ByWindow(), Save);

        result.ErrorCode.Should().Be(nameof(BrowserErrorCode.ContentAreaNotFound));
        result.Error.Should().Contain("no Document").And.Contain("focus_window");
        result.CssRect.Should().Be(new CssRect(920, 180, 88, 32));
        result.ScreenRect.Should().BeNull();
    }

    [Fact]
    public async Task ElementRect_OnAPinchZoomedPage_IsContentAreaNotFound_WithoutLookingForTheViewport()
    {
        LocateSave(scale: 2);

        var result = await CreateSut().GetElementRectAsync(ByWindow(), Save);

        result.ErrorCode.Should().Be(nameof(BrowserErrorCode.ContentAreaNotFound));
        result.Error.Should().Contain("pinch-zoomed");
        _viewports.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task ElementRect_WithAPortAlone_IsInvalidArguments_SinceNoWindowShowsThePage()
    {
        var result = await CreateSut().GetElementRectAsync(new BrowserTabTarget(null, Port, "SHOWN"), Save);

        result.ErrorCode.Should().Be(ToolErrorReporting.InvalidArgumentsErrorCode);
        _pages.VerifyNoOtherCalls();
    }

    #endregion

    #region browser_evaluate, browser_click, browser_handle_dialog

    [Fact]
    public async Task Evaluate_WhatTheScriptThrew_IsCdpEvaluationFailed_WithTheException()
    {
        _pages.Setup(p => p.EvaluateAsync(Shown, "boom()", true, It.IsAny<CdpGate>(), TimeSpan.FromMilliseconds(5000), It.IsAny<CancellationToken>()))
            .ReturnsAsync(CdpPageResult<CdpEvaluation>.Ok(new CdpEvaluation(null, "exception", new BrowserScriptException("ReferenceError: boom", 0, 0))));

        var result = await CreateSut().EvaluateAsync(ByWindow(), "boom()", true, 5000);

        result.ErrorCode.Should().Be(nameof(BrowserErrorCode.CdpEvaluationFailed));
        result.Exception!.Text.Should().Be("ReferenceError: boom");
        result.TargetId.Should().Be("SHOWN");
    }

    [Fact]
    public void Bounded_AShortValue_IsReturnedAsItIs_WithItsLength()
    {
        var (value, length, truncated) = BrowserPageService.Bounded(JsonSerializer.SerializeToElement(new { a = 1 }));

        value!.Value.GetRawText().Should().Be("{\"a\":1}");
        length.Should().Be(7);
        truncated.Should().BeNull();
    }

    [Fact]
    public void Bounded_ALongValue_IsTheStartOfItsJson_AsAString()
    {
        var (value, length, truncated) = BrowserPageService.Bounded(JsonSerializer.SerializeToElement(new string('x', 30_000)));

        value!.Value.ValueKind.Should().Be(JsonValueKind.String);
        value.Value.GetString()!.Length.Should().Be(BrowserEvaluateResult.MaxValueLength);
        value.Value.GetString().Should().StartWith("\"xxx");
        length.Should().Be(30_002);
        truncated.Should().BeTrue();
    }

    [Fact]
    public void Bounded_NeverCutsASurrogatePairInTwo()
    {
        // As the browser sends it: the JSON text holds the emoji themselves, not escapes. The cut falls inside a pair
        var json = "\"" + new string('x', BrowserEvaluateResult.MaxValueLength - 2) + string.Concat(Enumerable.Repeat("\U0001F600", 10)) + "\"";
        using var document = JsonDocument.Parse(json);

        var (value, _, _) = BrowserPageService.Bounded(document.RootElement);

        var cut = value!.Value.GetString()!;
        cut.Length.Should().Be(BrowserEvaluateResult.MaxValueLength - 1);
        char.IsHighSurrogate(cut[^1]).Should().BeFalse();
    }

    [Fact]
    public async Task Click_ThatOpenedADialog_SaysToAnswerItWithBrowserHandleDialog()
    {
        _pages.Setup(p => p.ClickAsync(Shown, Save, MouseButton.Primary, 1, It.IsAny<CdpGate>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>(), It.IsAny<BrowserDialogAnswer?>()))
            .ReturnsAsync(CdpPageResult<CdpInputOutcome>.Ok(new CdpInputOutcome(new BrowserElementInfo("button", "Delete"), 1, new CssPoint(5, 5),
                Dialog: new CdpDialog("confirm", new string('m', 500), null, Shown.Url))));

        var result = await CreateSut().ClickAsync(ByWindow(), Save, MouseButton.Primary, 1);

        result.Success.Should().BeTrue();
        result.Dialog!.Type.Should().Be("confirm");
        result.Dialog.Message.Length.Should().Be(CdpDialogText.MaxDialogMessageLength);
        result.Warning.Should().Contain("browser_handle_dialog");
    }

    [Fact]
    public async Task Click_ThatOpenedADialog_NotesItsTextForBrowserHandleDialog_ByTheWindow()
    {
        // The agent was shown the dialog's text in the result: browser_handle_dialog may then accept it
        _pages.Setup(p => p.ClickAsync(Shown, Save, MouseButton.Primary, 1, It.IsAny<CdpGate>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>(), It.IsAny<BrowserDialogAnswer?>()))
            .ReturnsAsync(CdpPageResult<CdpInputOutcome>.Ok(new CdpInputOutcome(new BrowserElementInfo("button", "Delete"), 1, new CssPoint(5, 5),
                Dialog: new CdpDialog("confirm", "Delete the draft?", null, Shown.Url))));

        var result = await CreateSut().ClickAsync(ByWindow(), Save, MouseButton.Primary, 1);

        result.Dialog.Should().Be(new BrowserOpenedDialog("confirm", "Delete the draft?"));
        result.Warning.Should().Contain($"handle {(long)BrowserWindow}").And.Contain("dialog option").And.NotContain("respond_to_dialog");
        _dialogs.Verify(d => d.NoteShown(BrowserWindow, "Delete the draft?"));
    }

    [Theory]
    [InlineData(true, BrowserOpenedDialog.Accepted)]
    [InlineData(false, BrowserOpenedDialog.Dismissed)]
    public async Task Click_WithTheDialogOption_PassesItOn_AndSaysHowTheDialogWasAnswered(bool accept, string answer)
    {
        var dialog = new CdpDialog("confirm", "Delete the draft?", null, Shown.Url);
        var asked = new BrowserDialogAnswer(accept);
        _pages.Setup(p => p.ClickAsync(Shown, Save, MouseButton.Primary, 1, It.IsAny<CdpGate>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>(), asked))
            .ReturnsAsync(CdpPageResult<CdpInputOutcome>.Ok(new CdpInputOutcome(new BrowserElementInfo("button", "Delete"), 1, new CssPoint(5, 5),
                Dialog: dialog, Answer: new CdpAnsweredDialog(dialog, accept))));

        var result = await CreateSut().ClickAsync(ByWindow(), Save, MouseButton.Primary, 1, dialog: asked);

        result.Dialog.Should().Be(new BrowserOpenedDialog("confirm", "Delete the draft?", answer));
        result.Warning.Should().BeNull();
        _dialogs.Verify(d => d.NoteShown(It.IsAny<nint>(), It.IsAny<string>()), Times.Never, "an answered dialog is closed");
    }

    [Fact]
    public async Task Click_WhoseDialogCouldNotBeAnswered_SaysItIsStillOpen()
    {
        var dialog = new CdpDialog("confirm", "Delete the draft?", null, Shown.Url);
        _pages.Setup(p => p.ClickAsync(Shown, Save, MouseButton.Primary, 1, It.IsAny<CdpGate>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>(), It.IsAny<BrowserDialogAnswer?>()))
            .ReturnsAsync(CdpPageResult<CdpInputOutcome>.Ok(new CdpInputOutcome(new BrowserElementInfo("button", "Delete"), 1, new CssPoint(5, 5),
                Dialog: dialog, AnswerFailure: "The browser refused Page.handleJavaScriptDialog: No dialog is showing (-32602)")));

        var result = await CreateSut().ClickAsync(ByWindow(), Save, MouseButton.Primary, 1, dialog: new BrowserDialogAnswer(true));

        result.Dialog!.Answer.Should().BeNull();
        result.Warning.Should().Contain("could not be answered").And.Contain("still open").And.Contain("browser_handle_dialog");
    }

    #endregion

    #region browser_screenshot

    [Theory]
    [InlineData(2880, 1620, 1568, 1568, 882, "jpeg")]
    [InlineData(400, 300, 1568, 400, 300, "png")]
    [InlineData(2880, 1620, 0, 2880, 1620, "jpeg")]
    [InlineData(1000, 3000, 512, 171, 512, "png")]
    public async Task Capture_IsScaledToItsLongEdge_AndEncodedAsTheCaptureToolsDo(
        int width, int height, int maxDimension, int expectedWidth, int expectedHeight, string format)
    {
        _pages.Setup(p => p.CaptureAsync(Shown, It.IsAny<CdpGate>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(CdpPageResult<CdpScreenshot>.Ok(new CdpScreenshot(Png(width, height), Shown.Url, "Form", Activated: true)));

        var capture = await CreateSut().CaptureAsync(ByWindow(), maxDimension);

        capture.Metadata.Success.Should().BeTrue(capture.Metadata.Error);
        (capture.Metadata.Width, capture.Metadata.Height).Should().Be((expectedWidth, expectedHeight));
        (capture.Metadata.OriginalWidth, capture.Metadata.OriginalHeight).Should().Be((width, height));
        capture.Metadata.Format.Should().Be(format);
        capture.Metadata.Activated.Should().BeTrue();
        capture.Image!.MimeType.Should().Be("image/" + format);
        using var image = new Bitmap(new MemoryStream(capture.Image.Data));
        (image.Width, image.Height).Should().Be((expectedWidth, expectedHeight));
    }

    [Fact]
    public async Task Capture_AnImageThatCannotBeRead_IsCdpProtocolError_WithoutAnImage()
    {
        _pages.Setup(p => p.CaptureAsync(Shown, It.IsAny<CdpGate>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(CdpPageResult<CdpScreenshot>.Ok(new CdpScreenshot([1, 2, 3], Shown.Url, "Form", false)));

        var capture = await CreateSut().CaptureAsync(ByWindow(), 1568);

        capture.Metadata.ErrorCode.Should().Be(nameof(BrowserErrorCode.CdpProtocolError));
        capture.Image.Should().BeNull();
    }

    private static byte[] Png(int width, int height)
    {
        using var bitmap = new Bitmap(width, height);
        using var stream = new MemoryStream();
        bitmap.Save(stream, ImageFormat.Png);
        return stream.ToArray();
    }

    #endregion
}
