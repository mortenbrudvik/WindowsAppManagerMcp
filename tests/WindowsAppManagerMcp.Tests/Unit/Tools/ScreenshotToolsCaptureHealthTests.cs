using System.Text.Json;
using ModelContextProtocol.Protocol;
using WindowsAppManagerMcp.Infrastructure;

namespace WindowsAppManagerMcp.Tests.Unit.Tools;

using WindowsAppManagerMcp.Tools;

/// <summary>
/// The capture tools' side of capture health (roadmap M2.9): capture_with_elements keeps the image when the UI
/// Automation walk fails and reports what the walk left out, and restoreIfMinimized restores a minimized window only
/// inside the input gate, never while input is paused, and never when the policy denies a window it would show.
/// </summary>
public class ScreenshotToolsCaptureHealthTests
{
    private static readonly nint Window = new(0x6000);

    private readonly Mock<IScreenshotService> _screenshots = TestDataFactory.CreateMockScreenshotService();
    private readonly Mock<IUIElementService> _elements = TestDataFactory.CreateMockUIElementService();
    private readonly Mock<IWindowService> _windows = TestDataFactory.CreateMockWindowService();
    private readonly Mock<IInputSession> _session = new();
    private readonly List<string> _events = [];

    private ScreenshotTools CreateSut() =>
        new(_screenshots.Object, _elements.Object, _windows.Object, new TargetPolicy(TargetPolicySettings.Default), _session.Object);

    #region capture_with_elements and the walk

    [Fact]
    public async Task CaptureWithElements_WhenTheWalkWasCutShort_ReportsTruncatedWithTheReason()
    {
        // Arrange - the timeout cut the walk off after it found elements
        WalkReturns(TestDataFactory.CreateUIElementResult(elements: [TestDataFactory.CreateUIElementInfo()]) with
        {
            Truncated = true,
            TruncationReason = UIElementTruncationReason.Timeout
        });

        // Act
        var json = Metadata(await CreateSut().CaptureWithElements(handle: (long)Window));

        // Assert
        json.GetProperty("success").GetBoolean().Should().BeTrue();
        json.GetProperty("truncated").GetBoolean().Should().BeTrue();
        json.GetProperty("truncationReason").GetString().Should().Be("Timeout");
        json.TryGetProperty("elementsError", out _).Should().BeFalse("the walk did not fail");
    }

    [Fact]
    public async Task CaptureWithElements_WithACompleteWalk_LeavesTheWalkFieldsOut()
    {
        // Act
        var json = Metadata(await CreateSut().CaptureWithElements(handle: (long)Window));

        // Assert
        json.TryGetProperty("truncated", out _).Should().BeFalse();
        json.TryGetProperty("elementsErrorCode", out _).Should().BeFalse();
    }

    [Fact]
    public async Task CaptureWithElements_WhenUiAutomationFails_StillReturnsTheImage()
    {
        // Arrange - for example the AppBar crash: the provider throws
        WalkReturns(TestDataFactory.CreateUIElementResult(success: false, error: "The provider failed",
            errorCode: UIElementErrorCode.OperationException));

        // Act
        var result = await CreateSut().CaptureWithElements(handle: (long)Window);

        // Assert
        result.IsError.Should().BeFalse();
        result.Content.OfType<ImageContentBlock>().Should().ContainSingle();
        var json = Metadata(result);
        json.GetProperty("elementsErrorCode").GetString().Should().Be("OperationException");
        json.GetProperty("truncated").GetBoolean().Should().BeTrue();
    }

    [Fact]
    public async Task CaptureWithElements_WithAnUnknownControlType_FailsTheCall()
    {
        // Arrange - the agent's mistake, not a failed walk
        WalkReturns(TestDataFactory.CreateUIElementResult(success: false, error: "Unknown control type 'Buton'",
            errorCode: UIElementErrorCode.InvalidControlType));

        // Act
        var result = await CreateSut().CaptureWithElements(handle: (long)Window, controlTypes: "Buton");

        // Assert
        result.IsError.Should().BeTrue();
        Metadata(result).GetProperty("errorCode").GetString().Should().Be("InvalidControlType");
        VerifyNotCaptured();
    }

    [Fact]
    public async Task CaptureWithElements_OfAnUntitledWindow_TakesTheTitleFromTheWalk()
    {
        // Arrange - GetWindowInfo reports nothing for an untitled popup
        _windows.Setup(w => w.GetWindowInfo(Window)).Returns((WindowInfo?)null);
        WalkReturns(TestDataFactory.CreateUIElementResult(elements: [TestDataFactory.CreateUIElementInfo()]) with { WindowTitle = "Context" });

        // Act
        await CreateSut().CaptureWithElements(handle: (long)Window);

        // Assert
        _screenshots.Verify(s => s.CaptureWindowWithElements(Window, "Context", It.IsAny<IReadOnlyList<UIElementInfo>>(),
            It.IsAny<bool>(), It.IsAny<bool>(), It.IsAny<CaptureOptions>()), Times.Once);
    }

    [Theory]
    [InlineData(ScreenshotErrorCode.WindowHidden)]
    [InlineData(ScreenshotErrorCode.Cloaked)]
    public async Task CaptureWithElements_OfAWindowThatShowsNothing_IsRefusedBeforeTheWalk(ScreenshotErrorCode code)
    {
        // Arrange
        _screenshots.Setup(s => s.CheckWindow(Window)).Returns(new CaptureRefusal(code, "shows nothing"));

        // Act
        var result = await CreateSut().CaptureWithElements(handle: (long)Window);

        // Assert
        result.IsError.Should().BeTrue();
        Metadata(result).GetProperty("errorCode").GetString().Should().Be(code.ToString());
        _elements.Verify(e => e.GetUIElementsAsync(It.IsAny<nint>(), It.IsAny<int>(), It.IsAny<UIElementFilter?>(),
            It.IsAny<IProgress<OperationProgress>?>(), It.IsAny<CancellationToken>()), Times.Never);
        VerifyNotCaptured();
    }

    #endregion

    #region restoreIfMinimized

    [Fact]
    public async Task CaptureWindow_WithoutRestoreIfMinimized_RestoresNothing()
    {
        // Arrange
        Minimized();

        // Act
        await CreateSut().CaptureWindow(handle: (long)Window);

        // Assert - the service refuses the minimized window itself
        _screenshots.Verify(s => s.RestoreForCapture(It.IsAny<nint>(), It.IsAny<nint>(), It.IsAny<bool>()), Times.Never);
        _screenshots.Verify(s => s.CaptureWindow(Window, true, It.IsAny<CaptureOptions>()), Times.Once);
    }

    [Fact]
    public async Task CaptureWindow_WithRestoreIfMinimized_RestoresInsideTheGate_CapturesThenMinimizesAgain()
    {
        // Arrange
        Minimized();
        var flow = InputGate.TagFlow();
        var gateHeldDuringTheCapture = false;
        _screenshots.Setup(s => s.CaptureWindow(Window, true, It.IsAny<CaptureOptions>()))
            .Callback(() =>
            {
                gateHeldDuringTheCapture = InputGate.IsHeldBy(flow);
                _events.Add("capture");
            })
            .Returns(new ScreenCapture<ScreenshotResult>(TestDataFactory.CreateScreenshotResult(), TestDataFactory.CreateEncodedImage()));

        // Act
        var result = await CreateSut().CaptureWindow(handle: (long)Window, restoreIfMinimized: true);

        // Assert
        result.IsError.Should().BeFalse();
        gateHeldDuringTheCapture.Should().BeTrue("the restored window can cover a point another call clicks");
        _events.Should().Equal("restore", "capture", "minimize");
        Metadata(result).GetProperty("warnings")[0].GetString().Should().StartWith("WindowRestored:");
    }

    [Fact]
    public async Task CaptureWithElements_WithRestoreIfMinimized_WalksAndCapturesTheRestoredWindow()
    {
        // Arrange
        Minimized();
        _elements.Setup(e => e.GetUIElementsAsync(Window, It.IsAny<int>(), It.IsAny<UIElementFilter?>(),
                It.IsAny<IProgress<OperationProgress>?>(), It.IsAny<CancellationToken>()))
            .Callback(() => _events.Add("walk"))
            .ReturnsAsync(TestDataFactory.CreateUIElementResult(elements: [TestDataFactory.CreateUIElementInfo()]));
        _screenshots.Setup(s => s.CaptureWindowWithElements(Window, It.IsAny<string?>(), It.IsAny<IReadOnlyList<UIElementInfo>>(),
                It.IsAny<bool>(), It.IsAny<bool>(), It.IsAny<CaptureOptions>()))
            .Callback(() => _events.Add("capture"))
            .Returns(new ScreenCapture<AnnotatedScreenshotResult>(TestDataFactory.CreateAnnotatedScreenshotResult(), TestDataFactory.CreateEncodedImage()));

        // Act
        var result = await CreateSut().CaptureWithElements(handle: (long)Window, restoreIfMinimized: true);

        // Assert
        result.IsError.Should().BeFalse();
        _events.Should().Equal("restore", "walk", "capture", "minimize");
        Metadata(result).GetProperty("warnings")[0].GetString().Should().StartWith("WindowRestored:");
    }

    [Fact]
    public async Task CaptureWindow_WithRestoreIfMinimized_WhilePaused_IsPausedByUser_AndRestoresNothing()
    {
        // Arrange
        Minimized();
        _session.Setup(s => s.IsPaused).Returns(true);

        // Act
        var act = () => CreateSut().CaptureWindow(handle: (long)Window, restoreIfMinimized: true);

        // Assert
        (await act.Should().ThrowAsync<ToolErrorException>()).Which.ErrorCode.Should().Be(nameof(WindowErrorCode.PausedByUser));
        _events.Should().BeEmpty();
        _screenshots.Verify(s => s.CaptureWindow(It.IsAny<nint>(), It.IsAny<bool>(), It.IsAny<CaptureOptions>()), Times.Never);
    }

    [Fact]
    public async Task CaptureWindow_WhilePaused_OfAWindowThatIsNotMinimized_StillCaptures()
    {
        // Arrange - looking is allowed while paused; only the restore changes the desktop
        _session.Setup(s => s.IsPaused).Returns(true);

        // Act
        var result = await CreateSut().CaptureWindow(handle: (long)Window, restoreIfMinimized: true);

        // Assert
        result.IsError.Should().BeFalse();
        _screenshots.Verify(s => s.CaptureWindow(Window, true, It.IsAny<CaptureOptions>()), Times.Once);
    }

    [Fact]
    public async Task CaptureWindow_WithRestoreIfMinimized_WhenThePolicyDeniesAWindowTheRestoreWouldShow_RestoresNothing()
    {
        // Arrange - the minimized window owns a credential prompt of a denied app
        Minimized();
        var prompt = new nint(0x6100);
        _screenshots.Setup(s => s.WindowsShownByRestore(Window)).Returns([Window, prompt]);
        _windows.Setup(w => w.EvaluateTargetPolicy(prompt)).Returns(TargetPolicyVerdict.Deny("keepass", "denied by WAM_DENY_PROCESSES"));

        // Act
        var result = await CreateSut().CaptureWindow(handle: (long)Window, restoreIfMinimized: true);

        // Assert
        result.IsError.Should().BeTrue();
        Metadata(result).GetProperty("errorCode").GetString().Should().Be(nameof(ScreenshotErrorCode.TargetDeniedByPolicy));
        _events.Should().BeEmpty();
        _screenshots.Verify(s => s.CaptureWindow(It.IsAny<nint>(), It.IsAny<bool>(), It.IsAny<CaptureOptions>()), Times.Never);
    }

    [Fact]
    public async Task CaptureWindow_WithRestoreIfMinimized_AndInvalidOptions_RestoresNothing()
    {
        // Arrange
        Minimized();

        // Act
        var result = await CreateSut().CaptureWindow(handle: (long)Window, restoreIfMinimized: true, maxWidth: 0);

        // Assert
        result.IsError.Should().BeTrue();
        Metadata(result).GetProperty("errorCode").GetString().Should().Be("InvalidImageSize");
        _events.Should().BeEmpty();
    }

    [Theory]
    [InlineData(ScreenshotErrorCode.WindowHidden)]
    [InlineData(ScreenshotErrorCode.Cloaked)]
    public async Task CaptureWindow_WithRestoreIfMinimized_OfAMinimizedWindowThatShowsNothing_RestoresNothing(ScreenshotErrorCode code)
    {
        // Arrange - minimized to the notification area (hidden), or minimized on another virtual desktop (cloaked):
        // a restore would show a window its app hid, or work on another desktop
        Minimized();
        _screenshots.Setup(s => s.CheckWindow(Window)).Returns(new CaptureRefusal(code, "shows nothing"));

        // Act
        var result = await CreateSut().CaptureWindow(handle: (long)Window, restoreIfMinimized: true);

        // Assert
        result.IsError.Should().BeTrue();
        Metadata(result).GetProperty("errorCode").GetString().Should().Be(code.ToString());
        _events.Should().BeEmpty();
        _screenshots.Verify(s => s.RestoreForCapture(It.IsAny<nint>(), It.IsAny<nint>(), It.IsAny<bool>()), Times.Never);
        _screenshots.Verify(s => s.CaptureWindow(It.IsAny<nint>(), It.IsAny<bool>(), It.IsAny<CaptureOptions>()), Times.Never);
    }

    [Theory]
    [InlineData(ScreenshotErrorCode.WindowHidden)]
    [InlineData(ScreenshotErrorCode.Cloaked)]
    public async Task CaptureWithElements_WithRestoreIfMinimized_OfAMinimizedWindowThatShowsNothing_RestoresNothing(ScreenshotErrorCode code)
    {
        // Arrange
        Minimized();
        _screenshots.Setup(s => s.CheckWindow(Window)).Returns(new CaptureRefusal(code, "shows nothing"));

        // Act
        var result = await CreateSut().CaptureWithElements(handle: (long)Window, restoreIfMinimized: true);

        // Assert
        result.IsError.Should().BeTrue();
        Metadata(result).GetProperty("errorCode").GetString().Should().Be(code.ToString());
        _events.Should().BeEmpty();
        _screenshots.Verify(s => s.RestoreForCapture(It.IsAny<nint>(), It.IsAny<nint>(), It.IsAny<bool>()), Times.Never);
        _elements.Verify(e => e.GetUIElementsAsync(It.IsAny<nint>(), It.IsAny<int>(), It.IsAny<UIElementFilter?>(),
            It.IsAny<IProgress<OperationProgress>?>(), It.IsAny<CancellationToken>()), Times.Never);
        VerifyNotCaptured();
    }

    [Fact]
    public async Task CaptureWindow_WhenTheRestoreFails_ReportsItsCode_AndCapturesNothing()
    {
        // Arrange
        Minimized();
        _screenshots.Setup(s => s.RestoreForCapture(Window, Window, true))
            .Returns(() => FailedRestore(new CaptureRefusal(ScreenshotErrorCode.WindowMinimized, "did not restore")));

        // Act
        var result = await CreateSut().CaptureWindow(handle: (long)Window, restoreIfMinimized: true);

        // Assert
        result.IsError.Should().BeTrue();
        Metadata(result).GetProperty("errorCode").GetString().Should().Be("WindowMinimized");
        _screenshots.Verify(s => s.CaptureWindow(It.IsAny<nint>(), It.IsAny<bool>(), It.IsAny<CaptureOptions>()), Times.Never);
    }

    #endregion

    /// <summary>
    /// The window is minimized; RestoreForCapture records "restore", and minimizing again records "minimize" (a restore
    /// whose ShowWindow calls go to a fake window).
    /// </summary>
    private void Minimized()
    {
        // CheckWindow says WindowMinimized until the restore, and nothing while the window is restored
        _screenshots.Setup(s => s.CheckWindow(Window)).Returns(() =>
            _events.Contains("restore") && !_events.Contains("minimize")
                ? null
                : new CaptureRefusal(ScreenshotErrorCode.WindowMinimized, ScreenshotService.MinimizedMessage));
        _screenshots.Setup(s => s.FindMinimizedWindow(Window)).Returns(Window);
        _screenshots.Setup(s => s.WindowsShownByRestore(Window)).Returns([Window]);
        _screenshots.Setup(s => s.RestoreForCapture(Window, Window, It.IsAny<bool>()))
            .Returns(() =>
            {
                _events.Add("restore");
                return Restored();
            });
    }

    private MinimizedWindowRestore Restored()
    {
        var minimized = true;
        var native = new Mock<INativeWindowWrapper>();
        native.Setup(n => n.IsWindow(Window)).Returns(true);
        native.Setup(n => n.IsWindowVisible(Window)).Returns(true);
        native.Setup(n => n.IsIconic(Window)).Returns(() => minimized);
        native.Setup(n => n.ShowWindow(Window, It.IsAny<int>())).Returns((nint _, int command) =>
        {
            minimized = command == 7; // SW_SHOWMINNOACTIVE
            if (minimized)
            {
                _events.Add("minimize");
            }

            return true;
        });
        var calls = new Mock<IWindowCallRunner>();
        calls.Setup(c => c.Run(It.IsAny<nint>(), It.IsAny<Func<bool>>())).Returns((nint _, Func<bool> call) => call());

        // The real restore over the fake window: SW_SHOWNOACTIVATE now, SW_SHOWMINNOACTIVE on Dispose
        var service = new ScreenshotService(TestDataFactory.CreateMockScreenCaptureWrapper().Object,
            TestDataFactory.CreateMockMonitorService().Object, native.Object, calls.Object, TimeSpan.Zero, TimeSpan.FromMilliseconds(100));
        return service.RestoreForCapture(Window, Window, includeFrame: true);
    }

    private static MinimizedWindowRestore FailedRestore(CaptureRefusal failure)
    {
        // A window that stays minimized: the restore reports WindowMinimized
        var native = new Mock<INativeWindowWrapper>();
        native.Setup(n => n.IsWindowVisible(Window)).Returns(true);
        native.Setup(n => n.IsIconic(Window)).Returns(true);
        var calls = new Mock<IWindowCallRunner>();
        calls.Setup(c => c.Run(It.IsAny<nint>(), It.IsAny<Func<bool>>())).Returns((nint _, Func<bool> call) => call());
        var service = new ScreenshotService(TestDataFactory.CreateMockScreenCaptureWrapper().Object,
            TestDataFactory.CreateMockMonitorService().Object, native.Object, calls.Object, TimeSpan.Zero, TimeSpan.FromMilliseconds(20));
        var restore = service.RestoreForCapture(Window, Window, includeFrame: true);
        restore.Failure.Should().NotBeNull(failure.Message);
        return restore;
    }

    private void WalkReturns(UIElementResult result) =>
        _elements.Setup(e => e.GetUIElementsAsync(It.IsAny<nint>(), It.IsAny<int>(), It.IsAny<UIElementFilter?>(),
                It.IsAny<IProgress<OperationProgress>?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(result);

    private void VerifyNotCaptured() =>
        _screenshots.Verify(s => s.CaptureWindowWithElements(It.IsAny<nint>(), It.IsAny<string?>(),
            It.IsAny<IReadOnlyList<UIElementInfo>>(), It.IsAny<bool>(), It.IsAny<bool>(), It.IsAny<CaptureOptions>()), Times.Never);

    private static JsonElement Metadata(CallToolResult result) =>
        JsonDocument.Parse(result.Content.OfType<TextContentBlock>().Single().Text).RootElement;
}
