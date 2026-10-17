using WindowsAppManagerMcp.Infrastructure;
using WindowsAppManagerMcp.Tools;

namespace WindowsAppManagerMcp.Tests.Unit.Tools;

/// <summary>
/// The `monitor` parameter of the tools that read a monitor's pixels (roadmap M6.7): take_screenshot, read_screen_text,
/// find_text_on_screen and wait_for_visual take a name or 'all', keep monitorIndex (deprecated), and refuse both
/// together. The services are mocks, so nothing is captured.
/// </summary>
public class CaptureMonitorSelectorTests
{
    private readonly Mock<IMonitorService> _monitors = new(MockBehavior.Strict);
    private readonly Mock<IScreenshotService> _screenshots = TestDataFactory.CreateMockScreenshotService();
    private readonly Mock<IOcrService> _ocr = new();
    private readonly Mock<IVisualChangeService> _visual = new();
    private readonly Mock<IWindowService> _windows = new();
    private OcrRequest? _ocrRequest;
    private VisualWaitRequest? _visualRequest;

    public CaptureMonitorSelectorTests()
    {
        _monitors.Setup(m => m.ResolveMonitor(It.Is<MonitorSelector>(s => s.Name == "right")))
            .Returns(MonitorResolution.Found(1, TestDataFactory.CreateMonitorInfo(1)));
        _monitors.Setup(m => m.ResolveMonitor(It.Is<MonitorSelector>(s => s.Name == "all")))
            .Returns(MonitorResolution.Found(MonitorSelection.AllMonitors, null));
        _monitors.Setup(m => m.ResolveMonitor(It.Is<MonitorSelector>(s => s.Name == "nowhere")))
            .Returns(MonitorResolution.Failed(nameof(WindowErrorCode.MonitorNotFound), "monitor 'nowhere' names no monitor. Nothing was done."));
        _ocr.Setup(o => o.ReadTextAsync(It.IsAny<OcrRequest>(), It.IsAny<OcrDetail>(), It.IsAny<int>(), It.IsAny<IProgress<OperationProgress>?>(), It.IsAny<CancellationToken>()))
            .Callback((OcrRequest request, OcrDetail _, int _, IProgress<OperationProgress>? _, CancellationToken _) => _ocrRequest = request)
            .ReturnsAsync(new ScreenTextResult(true, "ocr", "en-US", "c1", new CapturedRegion(0, 0, 10, 10), 1, 0, [], false));
        _ocr.Setup(o => o.FindTextAsync(It.IsAny<OcrRequest>(), It.IsAny<OcrTextMatcher>(), It.IsAny<int>(), It.IsAny<IProgress<OperationProgress>?>(), It.IsAny<CancellationToken>()))
            .Callback((OcrRequest request, OcrTextMatcher _, int _, IProgress<OperationProgress>? _, CancellationToken _) => _ocrRequest = request)
            .ReturnsAsync(new TextFindResult(true, "ocr", "en-US", "c1", new CapturedRegion(0, 0, 10, 10), 1, 0, [], false));
        _visual.Setup(v => v.WaitAsync(It.IsAny<VisualWaitRequest>(), It.IsAny<IProgress<OperationProgress>?>(), It.IsAny<CancellationToken>()))
            .Callback((VisualWaitRequest request, IProgress<OperationProgress>? _, CancellationToken _) => _visualRequest = request)
            .ReturnsAsync(new VisualWaitResult(Success: true, Satisfied: true, TimedOut: false, Condition: "changed", ElapsedMs: 1, Checks: 1));
    }

    private ScreenshotTools Screenshots() =>
        new(_screenshots.Object, Mock.Of<IUIElementService>(), _windows.Object, Mock.Of<ITargetPolicy>(), monitorService: _monitors.Object);

    private OcrTools Ocr() => new(_ocr.Object, _screenshots.Object, _windows.Object, _monitors.Object);

    private VisualTools Visual() => new(_visual.Object, _windows.Object, _monitors.Object);

    private static async Task<ToolErrorException> RefusedAsync(Func<Task> call) =>
        (await FluentActions.Awaiting(call).Should().ThrowAsync<ToolErrorException>()).Which;

    #region take_screenshot

    [Theory]
    [InlineData("right", 1)]
    [InlineData("all", -1)]
    public void TakeScreenshot_WithAMonitorName_CapturesTheMonitorItNames(string name, int index)
    {
        // Act
        Screenshots().TakeScreenshot(monitor: MonitorSelector.Of(name));

        // Assert
        _screenshots.Verify(s => s.CaptureMonitor(index, It.IsAny<CaptureOptions>()), Times.Once);
    }

    [Fact]
    public void TakeScreenshot_WithAnIndexAsMonitor_CapturesThatIndex()
    {
        // Act
        Screenshots().TakeScreenshot(monitor: MonitorSelector.OfIndex(-1));

        // Assert
        _screenshots.Verify(s => s.CaptureMonitor(-1, It.IsAny<CaptureOptions>()), Times.Once);
    }

    [Fact]
    public async Task TakeScreenshot_WithMonitorAndMonitorIndex_IsInvalidArguments_AndCapturesNothing()
    {
        // Act
        var error = await RefusedAsync(() => Task.FromResult(Screenshots().TakeScreenshot(monitorIndex: 0, monitor: MonitorSelector.Of("right"))));

        // Assert
        error.ErrorCode.Should().Be(ToolErrorReporting.InvalidArgumentsErrorCode);
        _screenshots.Verify(s => s.CaptureMonitor(It.IsAny<int>(), It.IsAny<CaptureOptions>()), Times.Never);
    }

    [Fact]
    public async Task TakeScreenshot_WithAMonitorNameThatNamesNone_IsMonitorNotFound()
    {
        // Act
        var error = await RefusedAsync(() => Task.FromResult(Screenshots().TakeScreenshot(monitor: MonitorSelector.Of("nowhere"))));

        // Assert
        error.ErrorCode.Should().Be(nameof(WindowErrorCode.MonitorNotFound));
        error.Message.Should().EndWith("Nothing was captured.");
    }

    #endregion

    #region OCR

    [Fact]
    public async Task ReadScreenText_WithAMonitorName_ReadsThatMonitor()
    {
        // Act
        await Ocr().ReadScreenText(monitor: MonitorSelector.Of("right"));

        // Assert
        _ocrRequest!.Target.Should().Be(new CaptureTarget.Monitor(1));
    }

    [Fact]
    public async Task FindTextOnScreen_WithMonitorAll_ReadsEveryMonitor()
    {
        // Act
        await Ocr().FindTextOnScreen("Save", monitor: MonitorSelector.Of("all"));

        // Assert
        _ocrRequest!.Target.Should().Be(new CaptureTarget.Monitor(-1));
    }

    [Fact]
    public async Task ReadScreenText_WithAMonitorAndAWindow_IsTwoTargets()
    {
        // Act
        var error = await RefusedAsync(() => Ocr().ReadScreenText(handle: 0x42, monitor: MonitorSelector.Of("right")));

        // Assert
        error.ErrorCode.Should().Be(ToolErrorReporting.InvalidArgumentsErrorCode);
        error.Message.Should().Contain("monitor");
    }

    [Fact]
    public async Task ReadScreenText_WithMonitorAndMonitorIndex_IsInvalidArguments()
    {
        // Act
        var error = await RefusedAsync(() => Ocr().ReadScreenText(monitorIndex: 0, monitor: MonitorSelector.Of("right")));

        // Assert
        error.ErrorCode.Should().Be(ToolErrorReporting.InvalidArgumentsErrorCode);
        _ocrRequest.Should().BeNull();
    }

    #endregion

    #region wait_for_visual

    [Fact]
    public async Task WaitForVisual_WithAMonitorName_WatchesThatMonitor()
    {
        // Act
        await Visual().WaitForVisual(monitor: MonitorSelector.Of("right"));

        // Assert
        _visualRequest!.Target.Should().Be(new VisualTarget.Monitor(1));
    }

    [Fact]
    public async Task WaitForVisual_WithAMonitorAndSince_IsTwoTargets()
    {
        // Act
        var error = await RefusedAsync(() => Visual().WaitForVisual(monitor: MonitorSelector.Of("right"), since: "c1-abc"));

        // Assert
        error.ErrorCode.Should().Be(ToolErrorReporting.InvalidArgumentsErrorCode);
        _visualRequest.Should().BeNull();
    }

    [Fact]
    public async Task WaitForVisual_WithAMonitorNameThatNamesNone_IsMonitorNotFound_AndWaitsForNothing()
    {
        // Act
        var error = await RefusedAsync(() => Visual().WaitForVisual(monitor: MonitorSelector.Of("nowhere")));

        // Assert
        error.ErrorCode.Should().Be(nameof(WindowErrorCode.MonitorNotFound));
        _visualRequest.Should().BeNull();
    }

    #endregion
}
