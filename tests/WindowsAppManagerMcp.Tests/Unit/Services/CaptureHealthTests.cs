namespace WindowsAppManagerMcp.Tests.Unit.Services;

/// <summary>
/// Capture health (roadmap M2.9): a window that shows nothing is refused with its own code before any pixel is
/// read, a successful capture says why its image may not show what the agent expects, frame captures leave out the
/// invisible resize borders, regions are cut to the screen, and restoreIfMinimized restores and minimizes without
/// activating anything.
/// </summary>
public class CaptureHealthTests
{
    private const int SW_SHOWNOACTIVATE = 4;
    private const int SW_MINIMIZE = 6;
    private const int SW_SHOWMINNOACTIVE = 7;
    private const int SW_RESTORE = 9;

    private static readonly CaptureOptions FullResolution = new() { MaxDimension = 0, Format = "png" };

    private readonly Mock<IScreenCaptureWrapper> _capture = TestDataFactory.CreateMockScreenCaptureWrapper(captureWidth: 100, captureHeight: 80);
    private readonly Mock<IMonitorService> _monitors = TestDataFactory.CreateMockMonitorService();
    private readonly Mock<INativeWindowWrapper> _windows = new();
    private readonly Mock<IWindowCallRunner> _calls = new();

    private static readonly nint Window = new(0x5000);

    public CaptureHealthTests()
    {
        _windows.Setup(w => w.IsWindowVisible(It.IsAny<nint>())).Returns(true);
        _windows.Setup(w => w.IsWindow(It.IsAny<nint>())).Returns(true);

        // The runner runs the call inline, like a window that answers at once
        _calls.Setup(c => c.Run(It.IsAny<nint>(), It.IsAny<Func<CaptureResult>>()))
            .Returns((nint _, Func<CaptureResult> call) => call());
        _calls.Setup(c => c.Run(It.IsAny<nint>(), It.IsAny<Func<bool>>()))
            .Returns((nint _, Func<bool> call) => call());
    }

    private ScreenshotService CreateSut(TimeSpan? settle = null) =>
        new(_capture.Object, _monitors.Object, _windows.Object, _calls.Object,
            restoreSettleTimeout: settle ?? TimeSpan.FromMilliseconds(200),
            restoreStateTimeout: TimeSpan.FromMilliseconds(100));

    #region Window pre-checks

    [Fact]
    public void CaptureWindow_OfAMinimizedWindow_IsWindowMinimized_AndReadsNoPixels()
    {
        // Arrange
        _windows.Setup(w => w.IsIconic(Window)).Returns(true);

        // Act
        var capture = CreateSut().CaptureWindow(Window, includeFrame: true, FullResolution);

        // Assert
        capture.Metadata.ErrorCode.Should().Be("WindowMinimized");
        capture.Metadata.Error.Should().Contain("restoreIfMinimized");
        VerifyNoWindowPixelsRead();
    }

    [Fact]
    public void CaptureWindow_OfAHiddenWindow_IsWindowHidden_AndReadsNoPixels()
    {
        // Arrange - a window hidden in the notification area draws nothing
        _windows.Setup(w => w.IsWindowVisible(Window)).Returns(false);

        // Act
        var capture = CreateSut().CaptureWindow(Window, includeFrame: true, FullResolution);

        // Assert
        capture.Metadata.Success.Should().BeFalse();
        capture.Metadata.ErrorCode.Should().Be("WindowHidden");
        VerifyNoWindowPixelsRead();
    }

    [Fact]
    public void CaptureWindow_OfACloakedWindow_IsCloaked_AndReadsNoPixels()
    {
        // Arrange - on another virtual desktop: IsWindowVisible is still true
        _windows.Setup(w => w.IsCloaked(Window)).Returns(true);

        // Act
        var capture = CreateSut().CaptureWindow(Window, includeFrame: true, FullResolution);

        // Assert
        capture.Metadata.ErrorCode.Should().Be("Cloaked");
        capture.Metadata.Error.Should().Contain("virtual desktop");
        VerifyNoWindowPixelsRead();
    }

    [Fact]
    public void CaptureWindow_OfAControlInACloakedWindow_IsCloaked()
    {
        // Arrange
        var root = new nint(0x5100);
        _windows.Setup(w => w.GetAncestor(Window, WindowAncestor.Root)).Returns(root);
        _windows.Setup(w => w.IsCloaked(root)).Returns(true);

        // Act
        var capture = CreateSut().CaptureWindow(Window, includeFrame: false, FullResolution);

        // Assert
        capture.Metadata.ErrorCode.Should().Be("Cloaked");
    }

    [Fact]
    public void CaptureWithElements_OfAHiddenWindow_IsWindowHidden()
    {
        // Arrange
        _windows.Setup(w => w.IsWindowVisible(Window)).Returns(false);

        // Act
        var capture = CreateSut().CaptureWindowWithElements(Window, "Tray app", [], includeFrame: true,
            ElementOverlayOptions.Default, FullResolution);

        // Assert
        capture.Metadata.ErrorCode.Should().Be("WindowHidden");
        capture.Metadata.WindowTitle.Should().Be("Tray app");
        VerifyNoWindowPixelsRead();
    }

    [Fact]
    public void CheckWindow_OfAVisibleWindow_IsNull()
    {
        CreateSut().CheckWindow(Window).Should().BeNull();
    }

    [Fact]
    public void CheckWindow_OfAMinimizedHiddenWindow_IsWindowHidden_WithoutTheRestoreAdvice()
    {
        // Arrange - an app minimized to the notification area: iconic, and hidden with SW_HIDE
        _windows.Setup(w => w.IsIconic(Window)).Returns(true);
        _windows.Setup(w => w.IsWindowVisible(Window)).Returns(false);

        // Act
        var refusal = CreateSut().CheckWindow(Window);

        // Assert - restoreIfMinimized would show a window its app hid
        refusal!.Code.Should().Be(ScreenshotErrorCode.WindowHidden);
        refusal.Message.Should().NotContain("restoreIfMinimized");
    }

    [Fact]
    public void CheckWindow_OfAMinimizedCloakedWindow_IsCloaked()
    {
        // Arrange - a minimized window on another virtual desktop
        _windows.Setup(w => w.IsIconic(Window)).Returns(true);
        _windows.Setup(w => w.IsCloaked(Window)).Returns(true);

        // Act
        var refusal = CreateSut().CheckWindow(Window);

        // Assert
        refusal!.Code.Should().Be(ScreenshotErrorCode.Cloaked);
    }

    #endregion

    #region Hung windows

    [Fact]
    public void CaptureWindow_AsksTheWindowToDrawItself_ThroughTheRunnerWithADeadline()
    {
        // Act
        var capture = CreateSut().CaptureWindow(Window, includeFrame: true, FullResolution);

        // Assert - PrintWindow waits for the window's thread
        capture.Metadata.Success.Should().BeTrue();
        _calls.Verify(c => c.Run(Window, It.IsAny<Func<CaptureResult>>()), Times.Once);
        _capture.Verify(c => c.CaptureWindowFromScreen(It.IsAny<nint>(), It.IsAny<bool>()), Times.Never);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void CaptureWindow_OfAWindowThatDoesNotAnswer_CopiesTheScreen_WithAWarning(bool includeFrame)
    {
        // Arrange - Windows reports the window hung, or PrintWindow missed its deadline
        _calls.Setup(c => c.Run(Window, It.IsAny<Func<CaptureResult>>()))
            .Throws(new WindowNotRespondingException(Window, "not responding"));
        _capture.Setup(c => c.CaptureWindowFromScreen(Window, includeFrame))
            .Returns(new CaptureResult(Pattern(100, 80), 100, 80, 300, 200, "ScreenBitBlt"));

        // Act
        var metadata = CreateSut().CaptureWindow(Window, includeFrame, FullResolution).Metadata;

        // Assert
        metadata.Success.Should().BeTrue();
        metadata.CaptureMethod.Should().Be("ScreenBitBlt");
        metadata.CapturedRegion.Should().Be(new CapturedRegion(300, 200, 100, 80));
        metadata.Warnings.Should().ContainSingle().Which.Should().StartWith("WindowNotResponding:").And.Contain("covers");
    }

    [Fact]
    public void CaptureWindow_WhenTheRunnerHasTooManyStuckCalls_GivesTheRunnersReason_NotABlameOnTheWindow()
    {
        // Arrange - the runner refuses at once because calls to other applications are stuck
        _calls.Setup(c => c.Run(Window, It.IsAny<Func<CaptureResult>>()))
            .Throws(new WindowNotRespondingException(Window, "8 earlier window calls are still waiting for applications that do not answer."));
        _capture.Setup(c => c.CaptureWindowFromScreen(Window, true))
            .Returns(new CaptureResult(Pattern(100, 80), 100, 80, 300, 200, "ScreenBitBlt"));

        // Act
        var metadata = CreateSut().CaptureWindow(Window, includeFrame: true, FullResolution).Metadata;

        // Assert
        var warning = metadata.Warnings.Should().ContainSingle().Which;
        warning.Should().Contain("could not be asked to draw itself").And.Contain("8 earlier window calls");
        warning.Should().NotContain("application is not responding");
    }

    [Fact]
    public void CaptureWithElements_OfAWindowThatDoesNotAnswer_CopiesTheScreen_WithAWarning()
    {
        // Arrange
        _calls.Setup(c => c.Run(Window, It.IsAny<Func<CaptureResult>>()))
            .Throws(new WindowNotRespondingException(Window, "not responding"));
        _capture.Setup(c => c.CaptureWindowFromScreen(Window, true))
            .Returns(new CaptureResult(Pattern(100, 80), 100, 80, 0, 0, "ScreenBitBlt"));

        // Act
        var metadata = CreateSut().CaptureWindowWithElements(Window, null, [], includeFrame: true,
            ElementOverlayOptions.Default, FullResolution).Metadata;

        // Assert
        metadata.Success.Should().BeTrue();
        metadata.CaptureMethod.Should().Be("ScreenBitBlt");
        metadata.Warnings.Should().ContainSingle(w => w.StartsWith("WindowNotResponding:"));
    }

    #endregion

    #region Frame cropping

    [Fact]
    public void CaptureWindow_WithFrame_IsCutToTheVisibleFrame_AndReportsItsOrigin()
    {
        // Arrange - GetWindowRect (100, 50, 100x80) includes 7 invisible border pixels left, right and bottom
        _capture.Setup(c => c.CaptureWindow(Window, true))
            .Returns(new CaptureResult(Pattern(100, 80), 100, 80, 100, 50, "PrintWindow"));
        _windows.Setup(w => w.GetExtendedFrameBounds(Window)).Returns((107, 50, 193, 123));

        // Act
        var capture = CreateSut().CaptureWindow(Window, includeFrame: true, FullResolution);

        // Assert
        var metadata = capture.Metadata;
        metadata.CapturedRegion.Should().Be(new CapturedRegion(107, 50, 86, 73));
        metadata.ImageToScreen.Should().Be(new ImageToScreenTransform(107, 50, 1, 1));
        metadata.Width.Should().Be(86);
        metadata.Height.Should().Be(73);

        // Image pixel (0, 0) is window pixel (7, 0), and the last one is window pixel (92, 72)
        using var image = Decode(capture);
        image.GetPixel(0, 0).Should().Be(PatternColor(7, 0));
        image.GetPixel(85, 72).Should().Be(PatternColor(92, 72));
    }

    [Fact]
    public void CaptureWindow_WithoutFrame_IsNotCut()
    {
        // Arrange
        _windows.Setup(w => w.GetExtendedFrameBounds(Window)).Returns((7, 0, 93, 73));

        // Act
        var metadata = CreateSut().CaptureWindow(Window, includeFrame: false, FullResolution).Metadata;

        // Assert
        metadata.CapturedRegion.Should().Be(new CapturedRegion(0, 0, 100, 80));
        _windows.Verify(w => w.GetExtendedFrameBounds(It.IsAny<nint>()), Times.Never);
    }

    [Fact]
    public void CaptureWindow_WithoutAFrameAttribute_KeepsTheWindowRectangle()
    {
        // Arrange - a child window has no DWM frame
        _windows.Setup(w => w.GetExtendedFrameBounds(Window)).Returns(((int, int, int, int)?)null);

        // Act
        var metadata = CreateSut().CaptureWindow(Window, includeFrame: true, FullResolution).Metadata;

        // Assert
        metadata.CapturedRegion.Should().Be(new CapturedRegion(0, 0, 100, 80));
    }

    [Fact]
    public void CaptureWithElements_WithFrame_PlacesOverlaysFromTheVisibleFrame()
    {
        // Arrange - the visible frame starts 7 pixels right of the window rectangle
        _capture.Setup(c => c.CaptureWindow(Window, true))
            .Returns(new CaptureResult(Pattern(100, 80), 100, 80, 0, 0, "PrintWindow"));
        _windows.Setup(w => w.GetExtendedFrameBounds(Window)).Returns((7, 0, 93, 73));
        var button = new UIElementInfo("OK", "Button", "ok", new BoundsDto(17, 10, 20, 10), true, false);

        // Act
        var metadata = CreateSut().CaptureWindowWithElements(Window, null, [button], includeFrame: true,
            ElementOverlayOptions.Default, FullResolution).Metadata;

        // Assert
        metadata.CapturedRegion.Should().Be(new CapturedRegion(7, 0, 86, 73));
        metadata.Elements.Should().ContainSingle().Which.ImageBounds.Should().Be(new BoundsDto(10, 10, 20, 10));
    }

    #endregion

    #region Blank images

    [Fact]
    public void CaptureMonitor_OfOneSolidColor_WarnsThatTheImageIsBlank()
    {
        // Act - the mock's pixels are all zero
        var metadata = CreateSut().CaptureMonitor(0, FullResolution).Metadata;

        // Assert
        metadata.Success.Should().BeTrue();
        metadata.Warnings.Should().ContainSingle().Which.Should().StartWith("ImageBlank:").And.Contain("#000000");
    }

    [Fact]
    public void CaptureWindow_OfOneSolidColor_NamesTheColor()
    {
        // Arrange - every pixel is RGB (0x12, 0x34, 0x56), stored blue first
        var pixels = new byte[100 * 80 * 4];
        for (var i = 0; i < pixels.Length; i += 4)
        {
            pixels[i] = 0x56;
            pixels[i + 1] = 0x34;
            pixels[i + 2] = 0x12;
        }

        _capture.Setup(c => c.CaptureWindow(Window, false)).Returns(new CaptureResult(pixels, 100, 80, 0, 0, "PrintWindow"));

        // Act
        var metadata = CreateSut().CaptureWindow(Window, includeFrame: false, FullResolution).Metadata;

        // Assert
        metadata.Warnings.Should().ContainSingle().Which.Should().Contain("#123456");
    }

    [Fact]
    public void CaptureWindow_WithContent_HasNoWarnings()
    {
        // Arrange
        _capture.Setup(c => c.CaptureWindow(Window, false)).Returns(new CaptureResult(Pattern(100, 80), 100, 80, 0, 0, "PrintWindow"));

        // Act
        var metadata = CreateSut().CaptureWindow(Window, includeFrame: false, FullResolution).Metadata;

        // Assert - left out of the JSON when there is nothing to say
        metadata.Warnings.Should().BeNull();
    }

    [Fact]
    public void CaptureWithElements_ChecksForBlankBeforeTheOverlaysAreDrawn()
    {
        // Arrange - a black window with one button: the overlay must not hide that the window itself is blank
        _capture.Setup(c => c.CaptureWindow(Window, false)).Returns(new CaptureResult(new byte[100 * 80 * 4], 100, 80, 0, 0, "PrintWindow"));
        var button = new UIElementInfo("OK", "Button", "ok", new BoundsDto(0, 0, 100, 80), true, false);

        // Act
        var metadata = CreateSut().CaptureWindowWithElements(Window, null, [button], includeFrame: false,
            ElementOverlayOptions.Default, FullResolution).Metadata;

        // Assert
        metadata.Warnings.Should().ContainSingle(w => w.StartsWith("ImageBlank:"));
    }

    #endregion

    #region Regions

    [Fact]
    public void CaptureRegion_EntirelyOffTheScreen_IsRegionOffScreen_AndCapturesNothing()
    {
        // Arrange - the virtual screen is (0, 0, 1920x1080)

        // Act
        var metadata = CreateSut().CaptureRegion(5000, 100, 400, 300, FullResolution).Metadata;

        // Assert
        metadata.Success.Should().BeFalse();
        metadata.ErrorCode.Should().Be("RegionOffScreen");
        metadata.Error.Should().Contain("get_monitors", "list_screens is deprecated (roadmap M6.1)");
        _capture.Verify(c => c.CaptureScreenRegion(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<int>()), Times.Never);
    }

    [Fact]
    public void CaptureRegion_PartlyOffTheScreen_CapturesThePartOnIt_WithAWarning()
    {
        // Act - 100 pixels hang off the left edge
        var metadata = CreateSut().CaptureRegion(-100, 50, 400, 300, FullResolution).Metadata;

        // Assert
        metadata.Success.Should().BeTrue();
        _capture.Verify(c => c.CaptureScreenRegion(0, 50, 300, 300), Times.Once);
        metadata.CapturedRegion.Should().Be(new CapturedRegion(0, 50, 300, 300));
        metadata.ImageToScreen.Should().Be(new ImageToScreenTransform(0, 50, 1, 1));
        metadata.Warnings.Should().Contain(w => w.StartsWith("RegionClipped:"));
    }

    [Fact]
    public void CaptureRegion_InTheGapBetweenMonitors_WarnsAndReportsNoMonitor()
    {
        // Arrange - a 1920x1080 primary and a 1280x720 monitor right of it: (1920..3200, 720..1080) is on no monitor
        _monitors.Setup(m => m.GetAllMonitors()).Returns(
        [
            TestDataFactory.CreateMonitorInfo(index: 0, boundsWidth: 1920, boundsHeight: 1080),
            TestDataFactory.CreateMonitorInfo(index: 1, isPrimary: false, boundsX: 1920, boundsWidth: 1280, boundsHeight: 720, scaleFactor: 1.5)
        ]);
        _capture.Setup(c => c.GetVirtualScreenBounds()).Returns((0, 0, 3200, 1080));

        // Act - centered at (2400, 900), in the gap
        var metadata = CreateSut().CaptureRegion(2300, 800, 200, 200, FullResolution).Metadata;

        // Assert
        metadata.Success.Should().BeTrue();
        metadata.MonitorIndex.Should().BeNull("MONITOR_DEFAULTTONULL: no monitor contains the center");
        metadata.ScaleFactor.Should().BeNull();
        metadata.Warnings.Should().Contain(w => w.StartsWith("RegionPartlyOffMonitors:"));
    }

    [Fact]
    public void CaptureRegion_OnOneMonitor_ReportsThatMonitor()
    {
        // Arrange
        _monitors.Setup(m => m.GetAllMonitors()).Returns(
        [
            TestDataFactory.CreateMonitorInfo(index: 0, boundsWidth: 1920, boundsHeight: 1080),
            TestDataFactory.CreateMonitorInfo(index: 1, isPrimary: false, boundsX: 1920, boundsWidth: 1280, boundsHeight: 720, scaleFactor: 1.5)
        ]);
        _capture.Setup(c => c.GetVirtualScreenBounds()).Returns((0, 0, 3200, 1080));

        // Act
        var metadata = CreateSut().CaptureRegion(2000, 100, 200, 200, FullResolution).Metadata;

        // Assert
        metadata.MonitorIndex.Should().Be(1);
        metadata.ScaleFactor.Should().Be(1.5);
        metadata.Warnings.Should().NotContain(w => w.StartsWith("RegionPartlyOffMonitors:") || w.StartsWith("RegionClipped:"));
    }

    [Fact]
    public void CaptureRegion_OverThePixelBudget_IsInvalidRegion_AndAllocatesNothing()
    {
        // Arrange - three 8K monitors side by side: 23040 x 4320 = 99.5 megapixels on screen
        _capture.Setup(c => c.GetVirtualScreenBounds()).Returns((0, 0, 23040, 4320));

        // Act
        var metadata = CreateSut().CaptureRegion(0, 0, 23040, 4320, FullResolution).Metadata;

        // Assert
        metadata.ErrorCode.Should().Be("InvalidRegion");
        metadata.Error.Should().Contain("64 megapixels");
        _capture.Verify(c => c.CaptureScreenRegion(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<int>()), Times.Never);
    }

    [Fact]
    public void CaptureRegion_WithAnEdgeOverTheLimit_IsInvalidRegion_AndCapturesNothing()
    {
        // Arrange - five 4K monitors side by side: 19200 x 2160 is under 64 megapixels, but the edge is over 16384
        _capture.Setup(c => c.GetVirtualScreenBounds()).Returns((0, 0, 19200, 2160));

        // Act
        var metadata = CreateSut().CaptureRegion(0, 0, 19200, 2160, FullResolution).Metadata;

        // Assert
        metadata.ErrorCode.Should().Be("InvalidRegion");
        metadata.Error.Should().Contain("16384");
        _capture.Verify(c => c.CaptureScreenRegion(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<int>()), Times.Never);
    }

    [Fact]
    public void Wrapper_RefusesACaptureOverThePixelBudget_BeforeAllocatingAnything()
    {
        // 16384 x 4097 is within the per-edge limit but over 64 megapixels; nothing is captured, so this reads no screen
        var act = () => new ScreenCaptureWrapper().CaptureScreenRegion(0, 0, 16384, 4097);

        act.Should().Throw<ArgumentException>().WithMessage("*64 megapixels*");
    }

    #endregion

    #region DPI

    [Fact]
    public void CaptureWindow_OfAWindowScaledByWindows_WarnsAboutDpi()
    {
        // Arrange - a DPI-unaware window (96 DPI) on a 150% monitor (144 DPI)
        _monitors.Setup(m => m.GetMonitorForWindow(Window)).Returns(TestDataFactory.CreateMonitorInfo(scaleFactor: 1.5));
        _windows.Setup(w => w.GetDpiForWindow(Window)).Returns(96u);
        _capture.Setup(c => c.CaptureWindow(Window, false)).Returns(new CaptureResult(Pattern(100, 80), 100, 80, 0, 0, "PrintWindow"));

        // Act
        var metadata = CreateSut().CaptureWindow(Window, includeFrame: false, FullResolution).Metadata;

        // Assert
        metadata.Warnings.Should().ContainSingle().Which.Should().StartWith("DpiScaledWindow:").And.Contain("96").And.Contain("144");
    }

    [Fact]
    public void CaptureWindow_OfAPerMonitorAwareWindow_HasNoDpiWarning()
    {
        // Arrange
        _monitors.Setup(m => m.GetMonitorForWindow(Window)).Returns(TestDataFactory.CreateMonitorInfo(scaleFactor: 1.5));
        _windows.Setup(w => w.GetDpiForWindow(Window)).Returns(144u);
        _capture.Setup(c => c.CaptureWindow(Window, false)).Returns(new CaptureResult(Pattern(100, 80), 100, 80, 0, 0, "PrintWindow"));

        // Act
        var metadata = CreateSut().CaptureWindow(Window, includeFrame: false, FullResolution).Metadata;

        // Assert
        metadata.Warnings.Should().BeNull();
    }

    #endregion

    #region Encoding

    [Fact]
    public void CaptureWindow_WhenTheImageCannotBeEncoded_IsEncodeFailed()
    {
        // Arrange - JPEG cannot hold an edge longer than 65535 pixels; the pixels themselves were captured (the real
        // wrapper refuses such an edge, and capture_region refuses it before capturing, so only a fake gets this far)
        _capture.Setup(c => c.CaptureWindow(Window, false)).Returns(new CaptureResult(Pattern(70000, 1), 70000, 1, 0, 0, "PrintWindow"));

        // Act
        var metadata = CreateSut().CaptureWindow(Window, includeFrame: false, new CaptureOptions { MaxDimension = 0, Format = "jpeg" }).Metadata;

        // Assert
        metadata.Success.Should().BeFalse();
        metadata.ErrorCode.Should().Be("EncodeFailed");
        metadata.Error.Should().Contain("were captured");
    }

    #endregion

    #region restoreIfMinimized

    [Fact]
    public void FindMinimizedWindow_PrefersTheMinimizedTopLevelWindow()
    {
        // Arrange - a control inside a minimized window
        var root = new nint(0x5100);
        _windows.Setup(w => w.GetAncestor(Window, WindowAncestor.Root)).Returns(root);
        _windows.Setup(w => w.IsIconic(root)).Returns(true);

        // Act + Assert
        CreateSut().FindMinimizedWindow(Window).Should().Be(root);
    }

    [Fact]
    public void FindMinimizedWindow_OfAWindowThatIsNotMinimized_IsNull()
    {
        CreateSut().FindMinimizedWindow(Window).Should().BeNull();
    }

    [Fact]
    public void WindowsShownByRestore_AreTheWindowAndTheWindowsItOwns()
    {
        // Arrange - a dialog owned by the window, and an unrelated window
        var dialog = new nint(0x5200);
        var other = new nint(0x5300);
        _windows.Setup(w => w.EnumWindows(It.IsAny<Func<nint, bool>>()))
            .Callback((Func<nint, bool> callback) =>
            {
                foreach (var window in new[] { Window, dialog, other })
                {
                    if (!callback(window))
                    {
                        break;
                    }
                }
            });
        _windows.Setup(w => w.GetWindow(dialog, 4u)).Returns(Window); // GW_OWNER

        // Act
        var shown = CreateSut().WindowsShownByRestore(Window);

        // Assert
        shown.Should().BeEquivalentTo([Window, dialog]);
    }

    [Fact]
    public void RestoreForCapture_ShowsTheWindowWithoutActivation_AndMinimizesItWithoutActivatingAnother()
    {
        // Arrange
        var commands = SimulateMinimizedWindow();

        // Act
        var restore = CreateSut().RestoreForCapture(Window, Window, includeFrame: true);
        var restoredDuringTheCapture = !_windows.Object.IsIconic(Window);
        restore.Dispose();

        // Assert - never SW_RESTORE or SW_MINIMIZE, which activate a window
        restore.Failure.Should().BeNull();
        restoredDuringTheCapture.Should().BeTrue();
        _windows.Object.IsIconic(Window).Should().BeTrue("it is minimized again afterwards");
        commands.Should().Equal(SW_SHOWNOACTIVATE, SW_SHOWMINNOACTIVE);
        commands.Should().NotContain([SW_RESTORE, SW_MINIMIZE]);
        restore.Warnings.Should().ContainSingle().Which.Should().StartWith("WindowRestored:")
            .And.Contain("captureId", "the window is minimized again, so a click with the captureId is refused until it is back");
        _calls.Verify(c => c.Run(Window, It.IsAny<Func<bool>>()), Times.Exactly(2));
    }

    [Fact]
    public void ACapture_OfAWindowRestoredForIt_KeepsNoVisualSignature_UntilTheRestoreIsOver()
    {
        // Arrange - the window is minimized again after the capture, so wait_for_visual's since on it would read a
        // minimized window at once and report a change that nothing made (roadmap M6.4)
        SimulateMinimizedWindow();
        _windows.Setup(w => w.GetWindowRect(Window)).Returns((0, 0, 100, 80));
        _windows.Setup(w => w.GetWindowThreadProcessId(Window)).Returns(4242u);
        var registry = new CaptureRegistry(keepSignatures: true);
        var sut = new ScreenshotService(_capture.Object, _monitors.Object, _windows.Object, _calls.Object,
            restoreSettleTimeout: TimeSpan.FromMilliseconds(200), restoreStateTimeout: TimeSpan.FromMilliseconds(100), captureRegistry: registry);

        // Act
        string? restored;
        using (sut.RestoreForCapture(Window, Window, includeFrame: true))
        {
            restored = sut.CaptureWindow(Window, includeFrame: true, FullResolution).Metadata.CaptureId;
        }

        _windows.Object.ShowWindow(Window, SW_SHOWNOACTIVATE); // the user shows it later
        var shown = sut.CaptureWindow(Window, includeFrame: true, FullResolution).Metadata.CaptureId;

        // Assert
        registry.Find(restored!)!.Signature.Should().BeNull();
        registry.Find(shown!)!.Signature.Should().NotBeNull();
    }

    [Fact]
    public void RestoreForCapture_WaitsForTwoEqualFrames()
    {
        // Arrange - the first two frames differ (still painting), then the window is stable
        SimulateMinimizedWindow();
        var frames = new Queue<byte[]>([Pattern(100, 80, seed: 1), Pattern(100, 80, seed: 2), Pattern(100, 80, seed: 3), Pattern(100, 80, seed: 3)]);
        _capture.Setup(c => c.CaptureWindow(Window, true))
            .Returns(() => new CaptureResult(frames.Count > 1 ? frames.Dequeue() : frames.Peek(), 100, 80, 0, 0, "PrintWindow"));

        // Act
        using var restore = CreateSut(settle: TimeSpan.FromSeconds(5)).RestoreForCapture(Window, Window, includeFrame: true);

        // Assert
        _capture.Verify(c => c.CaptureWindow(Window, true), Times.Exactly(4));
        restore.Warnings.Should().NotContain(w => w.StartsWith("WindowStillPainting:"));
    }

    [Fact]
    public void RestoreForCapture_WhenTheWindowKeepsChanging_WarnsThatItMayStillBePainting()
    {
        // Arrange - every frame differs
        SimulateMinimizedWindow();
        var seed = 0;
        _capture.Setup(c => c.CaptureWindow(Window, true))
            .Returns(() => new CaptureResult(Pattern(100, 80, seed: ++seed), 100, 80, 0, 0, "PrintWindow"));

        // Act
        using var restore = CreateSut(settle: TimeSpan.FromMilliseconds(120)).RestoreForCapture(Window, Window, includeFrame: true);

        // Assert
        restore.Failure.Should().BeNull();
        restore.Warnings.Should().Contain(w => w.StartsWith("WindowStillPainting:"));
    }

    [Fact]
    public void RestoreForCapture_WhenTheWindowStaysMinimized_FailsWithWindowMinimized()
    {
        // Arrange - the application ignores the restore
        _windows.Setup(w => w.IsIconic(Window)).Returns(true);

        // Act
        using var restore = CreateSut().RestoreForCapture(Window, Window, includeFrame: true);

        // Assert
        restore.Failure.Should().NotBeNull();
        restore.Failure!.Code.Should().Be(ScreenshotErrorCode.WindowMinimized);
        _capture.Verify(c => c.CaptureWindow(It.IsAny<nint>(), It.IsAny<bool>()), Times.Never);
    }

    [Fact]
    public void RestoreForCapture_WhenTheWindowDoesNotAnswer_FailsWithWindowMinimized()
    {
        // Arrange
        _windows.Setup(w => w.IsIconic(Window)).Returns(true);
        _calls.Setup(c => c.Run(Window, It.IsAny<Func<bool>>())).Throws(new WindowNotRespondingException(Window, "hung"));

        // Act
        using var restore = CreateSut().RestoreForCapture(Window, Window, includeFrame: true);

        // Assert
        restore.Failure!.Code.Should().Be(ScreenshotErrorCode.WindowMinimized);
        restore.Failure.Message.Should().Contain("not responding");
    }

    [Fact]
    public void RestoreForCapture_OfAHiddenWindow_ShowsNothing_AndFailsWithWindowHidden()
    {
        // Arrange - minimized to the notification area: SW_SHOWNOACTIVATE would show it, and SW_SHOWMINNOACTIVE
        // would leave it visible with a taskbar button its app removed
        var commands = SimulateMinimizedWindow();
        _windows.Setup(w => w.IsWindowVisible(Window)).Returns(false);

        // Act
        var restore = CreateSut().RestoreForCapture(Window, Window, includeFrame: true);
        restore.Dispose();

        // Assert
        restore.Failure!.Code.Should().Be(ScreenshotErrorCode.WindowHidden);
        commands.Should().BeEmpty();
        _capture.Verify(c => c.CaptureWindow(It.IsAny<nint>(), It.IsAny<bool>()), Times.Never);
    }

    [Fact]
    public void RestoreForCapture_OfACloakedWindow_ShowsNothing_AndFailsWithCloaked()
    {
        // Arrange - minimized on another virtual desktop
        var commands = SimulateMinimizedWindow();
        _windows.Setup(w => w.IsCloaked(Window)).Returns(true);

        // Act
        var restore = CreateSut().RestoreForCapture(Window, Window, includeFrame: true);
        restore.Dispose();

        // Assert
        restore.Failure!.Code.Should().Be(ScreenshotErrorCode.Cloaked);
        commands.Should().BeEmpty();
    }

    [Fact]
    public void RestoreForCapture_SaysThatAMaximizedWindowIsShownAtItsNormalSize()
    {
        // Arrange
        SimulateMinimizedWindow();

        // Act
        using var restore = CreateSut().RestoreForCapture(Window, Window, includeFrame: true);

        // Assert - SW_SHOWNOACTIVATE restores to the normal size and clears restore-to-maximized
        restore.Warnings.Should().ContainSingle().Which.Should().Contain("maximized");
    }

    [Fact]
    public void RestoreForCapture_WhenTheWindowCannotBeMinimizedAgain_Warns()
    {
        // Arrange - the restore works, the minimize afterwards is ignored
        var minimized = true;
        _windows.Setup(w => w.IsIconic(Window)).Returns(() => minimized);
        _windows.Setup(w => w.ShowWindow(Window, SW_SHOWNOACTIVATE)).Callback(() => minimized = false).Returns(true);

        // Act
        var restore = CreateSut().RestoreForCapture(Window, Window, includeFrame: true);
        restore.Dispose();

        // Assert
        restore.Warnings.Should().Contain(w => w.StartsWith("ReminimizeFailed:"));
    }

    #endregion

    private List<int> SimulateMinimizedWindow()
    {
        var commands = new List<int>();
        var minimized = true;
        _windows.Setup(w => w.IsIconic(Window)).Returns(() => minimized);
        _windows.Setup(w => w.ShowWindow(Window, It.IsAny<int>())).Returns((nint _, int command) =>
        {
            commands.Add(command);
            minimized = command is SW_SHOWMINNOACTIVE or SW_MINIMIZE;
            return true;
        });
        return commands;
    }

    private void VerifyNoWindowPixelsRead()
    {
        _capture.Verify(c => c.CaptureWindow(It.IsAny<nint>(), It.IsAny<bool>()), Times.Never);
        _capture.Verify(c => c.CaptureWindowFromScreen(It.IsAny<nint>(), It.IsAny<bool>()), Times.Never);
    }

    /// <summary>Pixels whose color encodes their position, so a crop can be checked pixel by pixel.</summary>
    private static byte[] Pattern(int width, int height, int seed = 0)
    {
        var pixels = new byte[width * height * 4];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var color = PatternColor(x, y, seed);
                var i = (y * width + x) * 4;
                pixels[i] = color.B;
                pixels[i + 1] = color.G;
                pixels[i + 2] = color.R;
            }
        }

        return pixels;
    }

    private static System.Drawing.Color PatternColor(int x, int y, int seed = 0) =>
        System.Drawing.Color.FromArgb(255, x * 2 % 256, y * 3 % 256, seed * 40 % 256);

    private static System.Drawing.Bitmap Decode(ScreenCapture<ScreenshotResult> capture) =>
        new(new MemoryStream(capture.Image!.Data));
}
