using ModelContextProtocol.Protocol;
using WindowsAppManagerMcp.Infrastructure;
using WindowsAppManagerMcp.Tools;

namespace WindowsAppManagerMcp.Tests.Unit.Tools;

/// <summary>
/// get_desktop_state's arguments and result (roadmap M3.12): the defaults, the ranges, what is refused before anything is
/// read, and the capture as image content before the JSON. The observation itself is DesktopStateServiceTests'.
/// </summary>
public class DesktopStateToolsTests
{
    private static readonly nint Notepad = 0x1A0B2C;

    private readonly Mock<IDesktopStateService> _state = new();
    private readonly Mock<IWindowService> _windows = new();
    private DesktopStateRequest? _request;
    private EncodedImage? _image;

    public DesktopStateToolsTests()
    {
        _state.Setup(s => s.GetStateAsync(It.IsAny<DesktopStateRequest>(), It.IsAny<CancellationToken>()))
            .Callback((DesktopStateRequest request, CancellationToken _) => _request = request)
            .ReturnsAsync(() => new DesktopStateSnapshot(State(), _image));
        _windows.Setup(w => w.ResolveWindow(It.IsAny<WindowSelector>()))
            .Returns(WindowResolution.Found(new WindowInfo(Notepad, "notes.txt - Notepad", "notepad", 4242, new WindowRect(0, 0, 800, 600),
                WindowState.Normal, true, 0)));
    }

    private DesktopStateTools CreateSut() => new(_state.Object, _windows.Object);

    private static DesktopState State(bool success = true) => new(success, Notepad, "notes.txt - Notepad", null, SessionLockState.Unlocked, null, null,
        new DesktopInputState(false), null, null, null, null, null, null, 12);

    private static async Task<ToolErrorException> RefusedAsync(Func<Task> call) =>
        (await FluentActions.Awaiting(call).Should().ThrowAsync<ToolErrorException>()).Which;

    [Fact]
    public async Task TheDefaults_ReadTheWindowInFront_WithEverySectionButTheScreenshot()
    {
        var result = await CreateSut().GetDesktopState();

        _request!.Window.Should().BeNull();
        _request.Include.Should().BeEquivalentTo(DesktopStateRequest.DefaultInclude);
        _request.Include.Should().NotContain(DesktopStateSection.Screenshot);
        _request.MaxElements.Should().Be(80);
        _request.InteractableOnly.Should().BeTrue();
        _request.MaxWindows.Should().Be(20);
        _request.ScreenshotMaxLongEdge.Should().Be(1280);
        _request.Timeout.Should().Be(TimeSpan.FromSeconds(5));
        result.IsError.Should().NotBe(true);
        result.Content.Should().ContainSingle().Which.Should().BeOfType<TextContentBlock>();
        result.Payload<DesktopState>().WindowHandle.Should().Be(Notepad);
    }

    [Fact]
    public async Task AWindowSelector_NamesTheWindow()
    {
        await CreateSut().GetDesktopState(window: new WindowSelector { ProcessName = "notepad" });

        _request!.Window.Should().Be(Notepad);
    }

    [Fact]
    public async Task ForegroundAlone_NamesNoWindow_SoTheWindowInFrontIsChosenAsWithoutOne()
    {
        // {"foreground": true} is whatever window is in front, not a window the agent chose: the rules for the window in
        // front apply (never the agent's own host, never a denied app), not those of a window named
        await CreateSut().GetDesktopState(window: new WindowSelector { Foreground = true });

        _request!.Window.Should().BeNull();
        _windows.Verify(w => w.ResolveWindow(It.IsAny<WindowSelector>()), Times.Never);
    }

    [Fact]
    public async Task ForegroundWithAFilter_NamesTheWindowItResolvesTo()
    {
        await CreateSut().GetDesktopState(window: new WindowSelector { Foreground = true, ProcessName = "notepad" });

        _request!.Window.Should().Be(Notepad);
    }

    [Fact]
    public async Task TheNumbers_AreClampedToTheirRanges()
    {
        await CreateSut().GetDesktopState(maxElements: 100_000, maxWindows: 0, screenshotMaxLongEdge: 10, timeoutMs: 999_999);

        _request!.MaxElements.Should().Be(500);
        _request.MaxWindows.Should().Be(1);
        _request.ScreenshotMaxLongEdge.Should().Be(256);
        _request.Timeout.Should().Be(TimeSpan.FromSeconds(30));
    }

    [Fact]
    public async Task TheScreenshot_IsTheImageBlock_BeforeTheJson()
    {
        _image = new EncodedImage([0xFF, 0xD8, 0xFF], "image/jpeg");

        var result = await CreateSut().GetDesktopState(include: [DesktopStateSection.Screenshot, DesktopStateSection.Elements]);

        _request!.Include.Should().BeEquivalentTo([DesktopStateSection.Screenshot, DesktopStateSection.Elements]);
        result.Content.Should().HaveCount(2);
        result.Content[0].Should().BeOfType<ImageContentBlock>().Which.MimeType.Should().Be("image/jpeg");
        result.Content[1].Should().BeOfType<TextContentBlock>();
    }

    [Fact]
    public async Task AScreenshot_IsRefusedBeforeAnythingIsRead_WhenCapturesAreOff()
    {
        _state.Setup(s => s.ScreenshotUnavailable()).Returns("A screenshot needs capture_window.");

        var error = await RefusedAsync(() => CreateSut().GetDesktopState(include: [DesktopStateSection.Screenshot]));

        error.ErrorCode.Should().Be(ToolErrorReporting.InvalidArgumentsErrorCode);
        error.Message.Should().Contain("capture_window").And.EndWith("Nothing was read.");
        _state.Verify(s => s.GetStateAsync(It.IsAny<DesktopStateRequest>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ScreenshotMarks_AreOffByDefault_AndAskedForWithTheScreenshotAndTheElements()
    {
        // roadmap M3.F32
        await CreateSut().GetDesktopState(include: [DesktopStateSection.Screenshot, DesktopStateSection.Elements]);
        _request!.ScreenshotMarks.Should().BeFalse();

        await CreateSut().GetDesktopState(include: [DesktopStateSection.Screenshot, DesktopStateSection.Elements], screenshotMarks: true);
        _request.ScreenshotMarks.Should().BeTrue();
    }

    [Theory]
    [InlineData(DesktopStateSection.Elements)]
    [InlineData(DesktopStateSection.Screenshot)]
    public async Task ScreenshotMarks_AreRefusedBeforeAnythingIsRead_WithoutTheScreenshotOrTheElements(DesktopStateSection only)
    {
        var error = await RefusedAsync(() => CreateSut().GetDesktopState(include: [only, DesktopStateSection.Windows], screenshotMarks: true));

        error.ErrorCode.Should().Be(ToolErrorReporting.InvalidArgumentsErrorCode);
        error.Message.Should().Contain("'screenshot'").And.Contain("'elements'").And.EndWith("Nothing was read.");
        _state.Verify(s => s.GetStateAsync(It.IsAny<DesktopStateRequest>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ScreenshotMarks_AreRefused_WithTheDefaultSections_WhichHaveNoScreenshot()
    {
        var error = await RefusedAsync(() => CreateSut().GetDesktopState(screenshotMarks: true));

        error.Message.Should().Contain("'screenshot'");
    }

    [Theory]
    [InlineData(DesktopStateScope.Window, ElementSearchScope.Window)]
    [InlineData(DesktopStateScope.WindowAndPopups, ElementSearchScope.WindowAndPopups)]
    public async Task TheScope_IsTheSearchScopeOfTheSameName(DesktopStateScope scope, ElementSearchScope expected)
    {
        // desktop is not one of get_desktop_state's values: the schema leaves it out, and binding refuses it (InvalidArguments)
        await CreateSut().GetDesktopState(scope: scope);

        _request!.Scope.Should().Be(expected);
    }

    [Fact]
    public async Task ANamedWindowThatIsGone_IsAnError()
    {
        _state.Setup(s => s.GetStateAsync(It.IsAny<DesktopStateRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DesktopStateSnapshot(State(success: false) with { ErrorCode = "InvalidHandle" }, null));

        var result = await CreateSut().GetDesktopState(handle: 0x999);

        result.IsError.Should().BeTrue();
        result.Payload<DesktopState>().ErrorCode.Should().Be("InvalidHandle");
    }
}
