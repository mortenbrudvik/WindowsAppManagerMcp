namespace WindowsAppManagerMcp.Tests.Unit.Tools;

using System.Text.RegularExpressions;
using WindowsAppManagerMcp.Infrastructure;
using WindowsAppManagerMcp.Tools;

/// <summary>
/// read_screen_text's and find_text_on_screen's arguments (roadmap M6.3): the one target they name, the limits and the text
/// to find, all checked before anything is captured. The reading itself is OcrServiceTests'.
/// </summary>
public class OcrToolsTests
{
    private const long Handle = 0x8100;

    private readonly Mock<IOcrService> _ocr = new();
    private readonly Mock<IScreenshotService> _screenshots = TestDataFactory.CreateMockScreenshotService();
    private readonly Mock<IWindowService> _windows = new();
    private OcrRequest? _request;

    public OcrToolsTests()
    {
        _ocr.Setup(o => o.ReadTextAsync(It.IsAny<OcrRequest>(), It.IsAny<OcrDetail>(), It.IsAny<int>(), It.IsAny<IProgress<OperationProgress>?>(), It.IsAny<CancellationToken>()))
            .Callback((OcrRequest request, OcrDetail _, int _, IProgress<OperationProgress>? _, CancellationToken _) => _request = request)
            .ReturnsAsync(new ScreenTextResult(true, "ocr", "en-US", "c1", new CapturedRegion(0, 0, 10, 10), 1, 0, [], false));
        _ocr.Setup(o => o.FindTextAsync(It.IsAny<OcrRequest>(), It.IsAny<OcrTextMatcher>(), It.IsAny<int>(), It.IsAny<IProgress<OperationProgress>?>(), It.IsAny<CancellationToken>()))
            .Callback((OcrRequest request, OcrTextMatcher _, int _, IProgress<OperationProgress>? _, CancellationToken _) => _request = request)
            .ReturnsAsync(new TextFindResult(true, "ocr", "en-US", "c1", new CapturedRegion(0, 0, 10, 10), 1, 0, [], false));
        _windows.Setup(w => w.ResolveWindow(It.IsAny<WindowSelector>()))
            .Returns(WindowResolution.Found(TestDataFactory.CreateWindowInfo(handleValue: 0x9200)));
    }

    private OcrTools CreateSut() => new(_ocr.Object, _screenshots.Object, _windows.Object);

    private static async Task<ToolErrorException> RefusedAsync(Func<Task> call) =>
        (await FluentActions.Awaiting(call).Should().ThrowAsync<ToolErrorException>()).Which;

    [Fact]
    public async Task ReadScreenText_WithNoTarget_ReadsThePrimaryMonitor()
    {
        _screenshots.Setup(s => s.ListScreens()).Returns(TestDataFactory.CreateScreenListResult(primaryIndex: 1));

        await CreateSut().ReadScreenText(language: "nb");

        _request.Should().Be(new OcrRequest(new CaptureTarget.Monitor(1), "nb"));
    }

    [Fact]
    public async Task ReadScreenText_NamesEachKindOfTarget()
    {
        var sut = CreateSut();

        await sut.ReadScreenText(handle: Handle, includeFrame: false);
        _request!.Target.Should().Be(new CaptureTarget.Window((nint)Handle, false));

        await sut.ReadScreenText(window: new WindowSelector { ProcessName = "mspaint" });
        _request!.Target.Should().Be(new CaptureTarget.Window(0x9200, true));

        await sut.ReadScreenText(x: -1920, y: 100, width: 800, height: 600);
        _request!.Target.Should().Be(new CaptureTarget.Region(-1920, 100, 800, 600));

        await sut.ReadScreenText(monitorIndex: -1);
        _request!.Target.Should().Be(new CaptureTarget.Monitor(-1));
    }

    [Fact]
    public async Task ReadScreenText_PartOfARegion_IsRefused()
    {
        var error = await RefusedAsync(() => CreateSut().ReadScreenText(x: 0, y: 0, width: 100));

        error.ErrorCode.Should().Be(ToolErrorReporting.InvalidArgumentsErrorCode);
        error.Message.Should().Contain("x, y, width and height");
        _request.Should().BeNull();
    }

    [Theory]
    [InlineData(true, true, false)]
    [InlineData(true, false, true)]
    [InlineData(false, true, true)]
    public async Task ReadScreenText_TwoTargets_AreRefused(bool window, bool region, bool monitor)
    {
        var error = await RefusedAsync(() => CreateSut().ReadScreenText(
            handle: window ? Handle : null,
            x: region ? 0 : null, y: region ? 0 : null, width: region ? 10 : null, height: region ? 10 : null,
            monitorIndex: monitor ? 0 : null));

        error.ErrorCode.Should().Be(ToolErrorReporting.InvalidArgumentsErrorCode);
        error.Message.Should().Contain("one target");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(501)]
    public async Task ReadScreenText_MaxLinesOutOfRange_IsRefused(int maxLines)
    {
        var error = await RefusedAsync(() => CreateSut().ReadScreenText(maxLines: maxLines));

        error.Message.Should().Contain("maxLines must be 1-500");
    }

    [Fact]
    public async Task FindTextOnScreen_AnInvalidRegex_IsRefusedBeforeAnythingIsRead()
    {
        var error = await RefusedAsync(() => CreateSut().FindTextOnScreen("(unclosed", OcrTextMatch.Regex));

        error.ErrorCode.Should().Be(ToolErrorReporting.InvalidArgumentsErrorCode);
        error.Message.Should().Contain("not a valid .NET regular expression").And.Contain("Nothing was read");
        _request.Should().BeNull();
    }

    [Fact]
    public async Task FindTextOnScreen_OnlyWhitespace_IsRefused()
    {
        var error = await RefusedAsync(() => CreateSut().FindTextOnScreen("   "));

        error.ErrorCode.Should().Be(ToolErrorReporting.InvalidArgumentsErrorCode);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(101)]
    public async Task FindTextOnScreen_MaxResultsOutOfRange_IsRefused(int maxResults)
    {
        var error = await RefusedAsync(() => CreateSut().FindTextOnScreen("Save", maxResults: maxResults));

        error.Message.Should().Contain("maxResults must be 1-100");
    }

    [Fact]
    public async Task FindTextOnScreen_ARegexThatRunsTooLong_IsInvalidArguments()
    {
        _ocr.Setup(o => o.FindTextAsync(It.IsAny<OcrRequest>(), It.IsAny<OcrTextMatcher>(), It.IsAny<int>(), It.IsAny<IProgress<OperationProgress>?>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new RegexMatchTimeoutException("aaaa!", "^(a+)+$", TimeSpan.FromMilliseconds(100)));

        var error = await RefusedAsync(() => CreateSut().FindTextOnScreen("^(a+)+$", OcrTextMatch.Regex, handle: Handle));

        error.ErrorCode.Should().Be(ToolErrorReporting.InvalidArgumentsErrorCode);
        error.Message.Should().Contain("took longer than 100 ms");
    }

    [Fact]
    public async Task FindTextOnScreen_PassesTheTargetAndLanguage()
    {
        await CreateSut().FindTextOnScreen("Save", handle: Handle, language: "en");

        _request.Should().Be(new OcrRequest(new CaptureTarget.Window((nint)Handle, true), "en"));
    }
}
