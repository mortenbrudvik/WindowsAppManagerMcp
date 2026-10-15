using WindowsAppManagerMcp.Infrastructure;
using WindowsAppManagerMcp.Tools;

namespace WindowsAppManagerMcp.Tests.Unit.Tools;

/// <summary>
/// The argument rules of the DOM bridge (roadmap M5.8), checked before the page service is asked anything: one way to
/// name an element, time limits and sizes refused (not clamped) outside their ranges, which browser and tab a call
/// names, and the pause, which refuses the tools that change a page at once (browser_screenshot only when it would bring
/// a tab to the front, which its service's gate decides).
/// </summary>
public class BrowserPageToolsTests
{
    private const long Handle = 2821181;
    private const string TargetId = "2B7E4F1A9C3D5E6F7A8B9C0D1E2F3A4B";

    private readonly Mock<IBrowserPageService> _pages = new();
    private readonly Mock<IBrowserDialogService> _dialogs = new();
    private readonly InputSession _session = new(new FakeInputWrapper());

    public BrowserPageToolsTests()
    {
        _pages.Setup(p => p.EvaluateAsync(It.IsAny<BrowserTabTarget>(), It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new BrowserEvaluateResult(true));
        _dialogs.Setup(d => d.HandleAsync(It.IsAny<nint>(), It.IsAny<bool?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new BrowserDialogResult(true));
        _pages.Setup(p => p.GetElementRectAsync(It.IsAny<BrowserTabTarget>(), It.IsAny<BrowserElementLocator>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new BrowserElementRectResult(true));
        _pages.Setup(p => p.ClickAsync(It.IsAny<BrowserTabTarget>(), It.IsAny<BrowserElementLocator>(), It.IsAny<MouseButton>(), It.IsAny<int>(),
                It.IsAny<CancellationToken>(), It.IsAny<BrowserDialogAnswer?>()))
            .ReturnsAsync(new BrowserInputResult(true));
        _pages.Setup(p => p.TypeAsync(It.IsAny<BrowserTabTarget>(), It.IsAny<BrowserTypeOptions>(), It.IsAny<CancellationToken>(), It.IsAny<BrowserDialogAnswer?>()))
            .ReturnsAsync(new BrowserInputResult(true));
        _pages.Setup(p => p.CaptureAsync(It.IsAny<BrowserTabTarget>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new BrowserScreenshot(new BrowserScreenshotResult(true, Width: 10, Height: 10), new EncodedImage([1, 2, 3], "image/png")));
    }

    private BrowserPageTools CreateSut() => new(_pages.Object, windowService: null, _session, _dialogs.Object);

    private static async Task<ToolErrorException> RefusedAsync(Func<Task> act) =>
        (await act.Should().ThrowAsync<ToolErrorException>()).Which;

    #region Naming an element

    [Fact]
    public async Task BrowserClick_TakesOneWayToNameTheElement_AndPassesItOn()
    {
        var result = await CreateSut().BrowserClick(role: "button", name: "Save draft", index: 1, handle: Handle);

        result.Success.Should().BeTrue();
        _pages.Verify(p => p.ClickAsync(new BrowserTabTarget((nint)Handle, null, null), new BrowserElementLocator(null, null, "button", "Save draft", 1),
            MouseButton.Primary, 1, It.IsAny<CancellationToken>(), It.IsAny<BrowserDialogAnswer?>()));
    }

    [Fact]
    public async Task BrowserClick_WithANameAlone_MatchesAnyRole()
    {
        await CreateSut().BrowserClick(name: "Save draft", handle: Handle);

        _pages.Verify(p => p.ClickAsync(It.IsAny<BrowserTabTarget>(), new BrowserElementLocator(null, null, null, "Save draft", null),
            It.IsAny<MouseButton>(), It.IsAny<int>(), It.IsAny<CancellationToken>(), It.IsAny<BrowserDialogAnswer?>()));
    }

    [Fact]
    public async Task BrowserClick_WithoutAnElement_IsInvalidArguments()
    {
        var refused = await RefusedAsync(() => CreateSut().BrowserClick(handle: Handle));

        refused.ErrorCode.Should().Be(ToolErrorReporting.InvalidArgumentsErrorCode);
        refused.Message.Should().Contain("selector, text, or role");
        _pages.VerifyNoOtherCalls();
    }

    [Theory]
    [InlineData("#save", "Save", null)]
    [InlineData("#save", null, "button")]
    [InlineData(null, "Save", "button")]
    public async Task BrowserClick_WithTwoWaysToNameTheElement_IsInvalidArguments(string? selector, string? text, string? role)
    {
        var refused = await RefusedAsync(() => CreateSut().BrowserClick(selector, text, role, handle: Handle));

        refused.ErrorCode.Should().Be(ToolErrorReporting.InvalidArgumentsErrorCode);
        refused.Message.Should().Contain("one way only");
        _pages.VerifyNoOtherCalls();
    }

    [Theory]
    [InlineData(-1, 1)]
    [InlineData(0, 0)]
    [InlineData(0, 4)]
    public async Task BrowserClick_ANegativeIndexOrAClickCountOutside1To3_IsInvalidArguments(int index, int clickCount)
    {
        var refused = await RefusedAsync(() => CreateSut().BrowserClick("#save", index: index, clickCount: clickCount, handle: Handle));

        refused.ErrorCode.Should().Be(ToolErrorReporting.InvalidArgumentsErrorCode);
        _pages.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task BrowserClick_AWhitespaceOrOverlongSelector_IsInvalidArguments()
    {
        await RefusedAsync(() => CreateSut().BrowserClick("   ", handle: Handle));
        await RefusedAsync(() => CreateSut().BrowserClick(new string('a', BrowserElementLocator.MaxLength + 1), handle: Handle));

        _pages.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task BrowserElementRect_NeedsTheWindow_NotAPortAlone()
    {
        var refused = await RefusedAsync(() => CreateSut().BrowserElementRect("#save", cdpPort: 9222, targetId: TargetId));

        refused.ErrorCode.Should().Be(ToolErrorReporting.InvalidArgumentsErrorCode);
        refused.Message.Should().Contain("window");
        _pages.VerifyNoOtherCalls();
    }

    #endregion

    #region Which browser and tab

    [Fact]
    public async Task APortAlone_NeedsATargetId()
    {
        var refused = await RefusedAsync(() => CreateSut().BrowserEvaluate("1", cdpPort: 9222));

        refused.ErrorCode.Should().Be(ToolErrorReporting.InvalidArgumentsErrorCode);
        refused.Message.Should().Contain("targetId");
    }

    [Fact]
    public async Task APortAloneWithATargetId_NamesThatTab()
    {
        await CreateSut().BrowserScreenshot(cdpPort: 9222, targetId: TargetId);

        _pages.Verify(p => p.CaptureAsync(new BrowserTabTarget(null, 9222, TargetId), BrowserPageTools.DefaultMaxDimension, It.IsAny<CancellationToken>()));
    }

    [Theory]
    [InlineData("not an id!")]
    [InlineData("")]
    public async Task AMalformedTargetId_IsInvalidArguments(string targetId)
    {
        var refused = await RefusedAsync(() => CreateSut().BrowserClick("#save", handle: Handle, targetId: targetId));

        refused.ErrorCode.Should().Be(ToolErrorReporting.InvalidArgumentsErrorCode);
        _pages.VerifyNoOtherCalls();
    }

    #endregion

    #region Limits

    [Theory]
    [InlineData(99)]
    [InlineData(60_001)]
    public async Task BrowserEvaluate_ATimeLimitOutside100To60000_IsRefused_NotClamped(int timeoutMs)
    {
        var refused = await RefusedAsync(() => CreateSut().BrowserEvaluate("1", timeoutMs: timeoutMs, handle: Handle));

        refused.ErrorCode.Should().Be(ToolErrorReporting.InvalidArgumentsErrorCode);
        _pages.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task BrowserEvaluate_AnEmptyOrOverlongExpression_IsInvalidArguments()
    {
        await RefusedAsync(() => CreateSut().BrowserEvaluate(" ", handle: Handle));
        await RefusedAsync(() => CreateSut().BrowserEvaluate(new string('1', BrowserPageTools.MaxExpressionLength + 1), handle: Handle));

        _pages.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task BrowserEvaluate_PassesTheScriptAndItsLimitOn()
    {
        await CreateSut().BrowserEvaluate("document.title", awaitPromise: false, timeoutMs: 2500, handle: Handle);

        _pages.Verify(p => p.EvaluateAsync(new BrowserTabTarget((nint)Handle, null, null), "document.title", false, 2500, It.IsAny<CancellationToken>()));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(null)]
    public async Task BrowserHandleDialog_APromptTextWithoutAccept_IsInvalidArguments(bool? accept)
    {
        var refused = await RefusedAsync(() => CreateSut().BrowserHandleDialog(accept: accept, promptText: "x", handle: Handle));

        refused.Message.Should().Contain("accept true");
        _dialogs.VerifyNoOtherCalls();
    }

    [Theory]
    [InlineData(null)]
    [InlineData(true)]
    [InlineData(false)]
    public async Task BrowserHandleDialog_ReadsOrAnswersTheDialogOfTheWindowItIsGiven(bool? accept)
    {
        var result = await CreateSut().BrowserHandleDialog(accept, handle: Handle);

        result.Success.Should().BeTrue();
        _dialogs.Verify(d => d.HandleAsync((nint)Handle, accept, null, It.IsAny<CancellationToken>()));
        _pages.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task BrowserHandleDialog_NeedsTheWindow()
    {
        var refused = await RefusedAsync(() => CreateSut().BrowserHandleDialog(true));

        refused.ErrorCode.Should().Be(ToolErrorReporting.InvalidArgumentsErrorCode);
        refused.Message.Should().Contain("handle or window");
        _dialogs.VerifyNoOtherCalls();
    }

    [Theory]
    [InlineData(BrowserDialogChoice.Accept, "Kari", true)]
    [InlineData(BrowserDialogChoice.Dismiss, null, false)]
    public async Task BrowserClickAndType_PassTheDialogOptionOn(BrowserDialogChoice choice, string? promptText, bool accept)
    {
        await CreateSut().BrowserClick("#delete", dialog: choice, promptText: promptText, handle: Handle);
        await CreateSut().BrowserType("x", pressEnter: true, dialog: choice, promptText: promptText, handle: Handle);

        var answer = new BrowserDialogAnswer(accept, promptText);
        _pages.Verify(p => p.ClickAsync(It.IsAny<BrowserTabTarget>(), It.IsAny<BrowserElementLocator>(), MouseButton.Primary, 1,
            It.IsAny<CancellationToken>(), answer));
        _pages.Verify(p => p.TypeAsync(It.IsAny<BrowserTabTarget>(), It.IsAny<BrowserTypeOptions>(), It.IsAny<CancellationToken>(), answer));
    }

    [Theory]
    [InlineData(null)]
    [InlineData(BrowserDialogChoice.Dismiss)]
    public async Task BrowserClick_APromptTextWithoutDialogAccept_IsInvalidArguments(BrowserDialogChoice? choice)
    {
        var refused = await RefusedAsync(() => CreateSut().BrowserClick("#delete", dialog: choice, promptText: "x", handle: Handle));

        refused.Message.Should().Contain("dialog accept");
        _pages.VerifyNoOtherCalls();
    }

    [Theory]
    [InlineData("", false, false, false)]
    [InlineData("", true, false, true)]
    [InlineData("", false, true, true)]
    [InlineData("abc", false, false, true)]
    public async Task BrowserType_AnEmptyTextIsTakenOnlyToClearOrPressEnter(string text, bool replace, bool pressEnter, bool taken)
    {
        var act = () => CreateSut().BrowserType(text, replace: replace, pressEnter: pressEnter, handle: Handle);

        if (taken)
            (await act()).Success.Should().BeTrue();
        else
            (await RefusedAsync(act)).ErrorCode.Should().Be(ToolErrorReporting.InvalidArgumentsErrorCode);
    }

    [Fact]
    public async Task BrowserType_AnOverlongText_IsInvalidArguments()
    {
        await RefusedAsync(() => CreateSut().BrowserType(new string('x', BrowserTypeOptions.MaxTextLength + 1), handle: Handle));

        _pages.VerifyNoOtherCalls();
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(63)]
    [InlineData(BrowserPageTools.MaxDimensionLimit + 1)]
    public async Task BrowserScreenshot_AMaxDimensionOutsideItsRange_IsInvalidArguments(int maxDimension)
    {
        await RefusedAsync(() => CreateSut().BrowserScreenshot(maxDimension, handle: Handle));

        _pages.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task BrowserScreenshot_ReturnsTheImageFirst_ThenItsMetadata()
    {
        var result = await CreateSut().BrowserScreenshot(0, handle: Handle);

        result.IsError.Should().BeFalse();
        result.Content.Should().HaveCount(2);
        result.Content[0].Should().BeOfType<ModelContextProtocol.Protocol.ImageContentBlock>();
        result.Content[1].Should().BeOfType<ModelContextProtocol.Protocol.TextContentBlock>().Which.Text.Should().Contain("\"width\":10");
    }

    #endregion

    #region The pause

    [Fact]
    public async Task WhileInputIsPaused_TheToolsThatChangeAPage_RefuseAtOnce()
    {
        _session.Pause(InputControlSources.Hotkey);
        var sut = CreateSut();

        (await RefusedAsync(() => sut.BrowserClick("#save", handle: Handle))).ErrorCode.Should().Be(nameof(WindowErrorCode.PausedByUser));
        (await RefusedAsync(() => sut.BrowserType("x", handle: Handle))).ErrorCode.Should().Be(nameof(WindowErrorCode.PausedByUser));
        (await RefusedAsync(() => sut.BrowserHandleDialog(true, handle: Handle))).ErrorCode.Should().Be(nameof(WindowErrorCode.PausedByUser));
        (await RefusedAsync(() => sut.BrowserHandleDialog(false, handle: Handle))).ErrorCode.Should().Be(nameof(WindowErrorCode.PausedByUser));
        (await RefusedAsync(() => sut.BrowserEvaluate("1", handle: Handle))).ErrorCode.Should().Be(nameof(WindowErrorCode.PausedByUser));
        _pages.VerifyNoOtherCalls();
        _dialogs.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task WhileInputIsPaused_TheReads_StillWork()
    {
        _session.Pause(InputControlSources.Hotkey);

        (await CreateSut().BrowserElementRect("#save", handle: Handle)).Success.Should().BeTrue();
        (await CreateSut().BrowserHandleDialog(handle: Handle)).Success.Should().BeTrue("reading a dialog answers nothing");
        (await CreateSut().BrowserScreenshot(handle: Handle)).IsError.Should().BeFalse(
            "a capture of a shown tab changes nothing; one that must bring a tab to the front is refused by the gate");
    }

    #endregion
}
