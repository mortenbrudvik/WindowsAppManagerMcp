namespace WindowsAppManagerMcp.Tests.Unit.Services;

/// <summary>
/// Unit tests for ScreenshotService: what is captured, the reported screen origin and transform,
/// and the error codes. Tests T9.2 from BACKLOG-TECHNICAL.md.
/// </summary>
public class ScreenshotServiceTests
{
    private static readonly CaptureOptions Defaults = new();

    private readonly Mock<IScreenCaptureWrapper> _mockCaptureWrapper;
    private readonly Mock<IMonitorService> _mockMonitorService;
    private readonly Mock<INativeWindowWrapper> _windows = new();

    public ScreenshotServiceTests()
    {
        _mockCaptureWrapper = TestDataFactory.CreateMockScreenCaptureWrapper();
        _mockMonitorService = TestDataFactory.CreateMockMonitorService();
        _windows.Setup(w => w.IsWindowVisible(It.IsAny<nint>())).Returns(true);
    }

    private ScreenshotService CreateSut() =>
        new ScreenshotService(_mockCaptureWrapper.Object, _mockMonitorService.Object, _windows.Object);

    #region T9.2.1-T9.2.2: ListScreens Tests

    [Fact]
    public void ListScreens_ReturnsAllMonitors()
    {
        // Arrange
        var monitors = TestDataFactory.CreateDualMonitorSetup();
        _mockMonitorService.Setup(m => m.GetAllMonitors()).Returns(monitors);
        _mockCaptureWrapper.Setup(c => c.GetVirtualScreenBounds())
            .Returns((0, 0, 4480, 1440));
        var sut = CreateSut();

        // Act
        var result = sut.ListScreens();

        // Assert
        result.Screens.Should().HaveCount(2);
    }

    [Fact]
    public void ListScreens_ReturnsPrimaryIndex()
    {
        // Arrange
        var monitors = TestDataFactory.CreateDualMonitorSetup();
        _mockMonitorService.Setup(m => m.GetAllMonitors()).Returns(monitors);
        var sut = CreateSut();

        // Act
        var result = sut.ListScreens();

        // Assert
        result.PrimaryIndex.Should().Be(0);
    }

    [Fact]
    public void ListScreens_IncludesVirtualScreenBounds()
    {
        // Arrange
        var monitors = new List<MonitorInfo> { TestDataFactory.CreateMonitorInfo() };
        _mockMonitorService.Setup(m => m.GetAllMonitors()).Returns(monitors);
        _mockCaptureWrapper.Setup(c => c.GetVirtualScreenBounds())
            .Returns((-1920, 0, 3840, 1080));
        var sut = CreateSut();

        // Act
        var result = sut.ListScreens();

        // Assert
        result.VirtualScreen.Should().NotBeNull();
        result.VirtualScreen.X.Should().Be(-1920);
        result.VirtualScreen.Width.Should().Be(3840);
    }

    [Fact]
    public void ListScreens_MapsScreenInfoCorrectly()
    {
        // Arrange
        var monitor = TestDataFactory.CreateMonitorInfo(
            index: 0,
            deviceName: @"\\.\DISPLAY1",
            isPrimary: true,
            boundsX: 0, boundsY: 0,
            boundsWidth: 1920, boundsHeight: 1080,
            workAreaX: 0, workAreaY: 0,
            workAreaWidth: 1920, workAreaHeight: 1040,
            scaleFactor: 1.25);
        _mockMonitorService.Setup(m => m.GetAllMonitors())
            .Returns(new List<MonitorInfo> { monitor });
        var sut = CreateSut();

        // Act
        var result = sut.ListScreens();

        // Assert
        var screen = result.Screens[0];
        screen.Index.Should().Be(0);
        screen.Name.Should().Be(@"\\.\DISPLAY1");
        screen.IsPrimary.Should().BeTrue();
        screen.X.Should().Be(0);
        screen.Y.Should().Be(0);
        screen.Width.Should().Be(1920);
        screen.Height.Should().Be(1080);
        screen.WorkAreaWidth.Should().Be(1920);
        screen.WorkAreaHeight.Should().Be(1040);
        screen.ScaleFactor.Should().Be(1.25);
    }

    #endregion

    #region T9.2.3-T9.2.6: CaptureMonitor Tests

    [Fact]
    public void CaptureMonitor_WithValidIndex_ReturnsSuccessWithImage()
    {
        // Arrange
        var sut = CreateSut();

        // Act
        var capture = sut.CaptureMonitor(0, Defaults);

        // Assert
        capture.Metadata.Success.Should().BeTrue();
        capture.Metadata.ErrorCode.Should().BeNull();
        capture.Image.Should().NotBeNull();
        capture.Image!.Data.Should().NotBeEmpty();
        capture.Metadata.ByteSize.Should().Be(capture.Image.Data.Length);
        capture.Metadata.CoordinateSpace.Should().Be("physical-virtual-screen");
        capture.Metadata.CaptureMethod.Should().Be("ScreenBitBlt");
    }

    [Fact]
    public void CaptureMonitor_WithInvalidIndex_ReturnsInvalidMonitorIndex()
    {
        // Arrange
        var sut = CreateSut();

        // Act
        var capture = sut.CaptureMonitor(5, Defaults);

        // Assert
        capture.Metadata.Success.Should().BeFalse();
        capture.Metadata.ErrorCode.Should().Be("InvalidMonitorIndex");
        capture.Metadata.Error.Should().Contain("out of range");
        capture.Image.Should().BeNull();
        _mockCaptureWrapper.Verify(
            c => c.CaptureScreenRegion(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<int>()),
            Times.Never);
    }

    [Fact]
    public void CaptureMonitor_CallsCaptureWrapperWithCorrectBounds()
    {
        // Arrange
        UseMonitor(TestDataFactory.CreateMonitorInfo(boundsX: 100, boundsY: 50, boundsWidth: 1920, boundsHeight: 1080));
        var sut = CreateSut();

        // Act
        sut.CaptureMonitor(0, Defaults);

        // Assert
        _mockCaptureWrapper.Verify(c => c.CaptureScreenRegion(100, 50, 1920, 1080), Times.Once);
    }

    [Fact]
    public void CaptureMonitor_LeftOfPrimary_ReportsNegativeOriginAndTransform()
    {
        // Arrange - a monitor left of and below the primary one, scaled down to the 1568 default
        UseMonitor(TestDataFactory.CreateMonitorInfo(boundsX: -2560, boundsY: 365, boundsWidth: 2560, boundsHeight: 1440));
        var sut = CreateSut();

        // Act
        var metadata = sut.CaptureMonitor(0, Defaults).Metadata;

        // Assert
        metadata.CapturedRegion.Should().Be(new CapturedRegion(-2560, 365, 2560, 1440));
        metadata.Width.Should().Be(1568);
        metadata.Height.Should().Be(882);
        metadata.ImageToScreen!.OffsetX.Should().Be(-2560);
        metadata.ImageToScreen.OffsetY.Should().Be(365);
        metadata.ImageToScreen.ScaleX.Should().BeApproximately(2560.0 / 1568, 1e-6);
        metadata.ImageToScreen.ScaleY.Should().BeApproximately(1440.0 / 882, 1e-6);

        // The image's bottom-right pixel maps back onto the monitor's bottom-right corner.
        metadata.ImageToScreen.ToScreen(1568, 882).Should().Be((0, 365 + 1440));
    }

    [Fact]
    public void CaptureMonitor_AtFullResolution_ReportsCaptureSize()
    {
        // Arrange
        UseMonitor(TestDataFactory.CreateMonitorInfo(boundsWidth: 2560, boundsHeight: 1440));
        var sut = CreateSut();

        // Act
        var metadata = sut.CaptureMonitor(0, new CaptureOptions { MaxDimension = 0 }).Metadata;

        // Assert
        metadata.Width.Should().Be(2560);
        metadata.Height.Should().Be(1440);
        metadata.OriginalWidth.Should().BeNull();
        metadata.ImageToScreen.Should().Be(new ImageToScreenTransform(0, 0, 1, 1));
    }

    [Fact]
    public void CaptureMonitor_IncludesScaleFactor()
    {
        // Arrange
        UseMonitor(TestDataFactory.CreateMonitorInfo(scaleFactor: 1.5));
        var sut = CreateSut();

        // Act
        var metadata = sut.CaptureMonitor(0, Defaults).Metadata;

        // Assert - informational only: the transform does not use it
        metadata.ScaleFactor.Should().Be(1.5);
        metadata.ImageToScreen!.ScaleX.Should().BeApproximately(1920.0 / 1568, 1e-6);
    }

    [Fact]
    public void CaptureMonitor_IncludesMonitorIndex()
    {
        // Arrange
        var monitors = TestDataFactory.CreateDualMonitorSetup();
        _mockMonitorService.Setup(m => m.GetAllMonitors()).Returns(monitors);
        var sut = CreateSut();

        // Act
        var metadata = sut.CaptureMonitor(1, Defaults).Metadata;

        // Assert
        metadata.MonitorIndex.Should().Be(1);
        metadata.CapturedRegion!.X.Should().Be(1920);
    }

    #endregion

    #region T9.2.7-T9.2.8: CaptureRegion Tests

    [Fact]
    public void CaptureRegion_WithValidDimensions_ReturnsSuccess()
    {
        // Arrange
        var sut = CreateSut();

        // Act
        var capture = sut.CaptureRegion(100, 100, 800, 600, Defaults);

        // Assert
        capture.Metadata.Success.Should().BeTrue();
        capture.Image.Should().NotBeNull();
    }

    [Fact]
    public void CaptureRegion_CallsCaptureWrapperWithCorrectCoordinates()
    {
        // Arrange
        var sut = CreateSut();

        // Act
        sut.CaptureRegion(150, 200, 640, 480, Defaults);

        // Assert
        _mockCaptureWrapper.Verify(c => c.CaptureScreenRegion(150, 200, 640, 480), Times.Once);
    }

    [Theory]
    [InlineData(0, 600)]
    [InlineData(800, -100)]
    public void CaptureRegion_WithNonPositiveSize_ReturnsInvalidRegion(int width, int height)
    {
        // Arrange
        var sut = CreateSut();

        // Act
        var capture = sut.CaptureRegion(100, 100, width, height, Defaults);

        // Assert
        capture.Metadata.Success.Should().BeFalse();
        capture.Metadata.ErrorCode.Should().Be("InvalidRegion");
        capture.Metadata.Error.Should().Contain("positive");
        _mockCaptureWrapper.Verify(
            c => c.CaptureScreenRegion(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<int>()),
            Times.Never);
    }

    [Fact]
    public void CaptureRegion_ReportsTheRegionAsOriginAndTransform()
    {
        // Arrange - a monitor left of the primary one
        _mockCaptureWrapper.Setup(c => c.GetVirtualScreenBounds()).Returns((-1920, 0, 3840, 1080));
        var sut = CreateSut();

        // Act
        var metadata = sut.CaptureRegion(-50, 75, 400, 300, Defaults).Metadata;

        // Assert
        metadata.CapturedRegion.Should().Be(new CapturedRegion(-50, 75, 400, 300));
        metadata.ImageToScreen.Should().Be(new ImageToScreenTransform(-50, 75, 1, 1));
    }

    [Fact]
    public void CaptureRegion_DeterminesMonitorFromCenter()
    {
        // Arrange
        _mockMonitorService.Setup(m => m.GetAllMonitors()).Returns(TestDataFactory.CreateDualMonitorSetup());
        var sut = CreateSut();

        // Act - the center (300, 250) is on the primary monitor
        var metadata = sut.CaptureRegion(100, 100, 400, 300, Defaults).Metadata;

        // Assert - the monitor that contains it, never just the nearest one
        metadata.MonitorIndex.Should().Be(0);
        _mockMonitorService.Verify(m => m.GetMonitorAt(It.IsAny<int>(), It.IsAny<int>()), Times.Never);
    }

    #endregion

    #region T9.2.9-T9.2.10: CaptureWindow Tests

    [Fact]
    public void CaptureWindow_WithValidHandle_ReturnsSuccess()
    {
        // Arrange
        var sut = CreateSut();

        // Act
        var capture = sut.CaptureWindow(new nint(12345), includeFrame: true, Defaults);

        // Assert
        capture.Metadata.Success.Should().BeTrue();
        capture.Image.Should().NotBeNull();
        capture.Metadata.CaptureMethod.Should().Be("PrintWindow");
    }

    [Fact]
    public void CaptureWindow_WithInvalidHandle_ReturnsInvalidHandle()
    {
        // Arrange
        var sut = CreateSut();

        // Act
        var capture = sut.CaptureWindow(nint.Zero, includeFrame: true, Defaults);

        // Assert
        capture.Metadata.Success.Should().BeFalse();
        capture.Metadata.ErrorCode.Should().Be("InvalidHandle");
        capture.Metadata.Error.Should().Contain("Invalid window handle");
        _mockCaptureWrapper.Verify(c => c.CaptureWindow(It.IsAny<nint>(), It.IsAny<bool>()), Times.Never);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void CaptureWindow_PassesIncludeFrame(bool includeFrame)
    {
        // Arrange
        var handle = new nint(12345);
        var sut = CreateSut();

        // Act
        sut.CaptureWindow(handle, includeFrame, Defaults);

        // Assert
        _mockCaptureWrapper.Verify(c => c.CaptureWindow(handle, includeFrame), Times.Once);
    }

    [Fact]
    public void CaptureWindow_ReportsTheWrappersScreenOriginInsteadOfZero()
    {
        // Arrange - a window on a monitor left of the primary one (the old result said 0, 0)
        var handle = new nint(12345);
        _mockCaptureWrapper.Setup(c => c.CaptureWindow(handle, false))
            .Returns(new CaptureResult(new byte[1024 * 768 * 4], 1024, 768, -1200, 340, "PrintWindow"));
        var sut = CreateSut();

        // Act
        var metadata = sut.CaptureWindow(handle, includeFrame: false, Defaults).Metadata;

        // Assert
        metadata.Width.Should().Be(1024);
        metadata.Height.Should().Be(768);
        metadata.CapturedRegion.Should().Be(new CapturedRegion(-1200, 340, 1024, 768));
        metadata.ImageToScreen.Should().Be(new ImageToScreenTransform(-1200, 340, 1, 1));
        metadata.ImageToScreen!.ToScreen(10, 20).Should().Be((-1190, 360));
    }

    [Fact]
    public void CaptureWindow_GetsMonitorForWindow()
    {
        // Arrange
        var handle = new nint(12345);
        var monitor = TestDataFactory.CreateMonitorInfo(index: 1, scaleFactor: 1.25);
        _mockMonitorService.Setup(m => m.GetMonitorForWindow(handle))
            .Returns(monitor);
        var sut = CreateSut();

        // Act
        var metadata = sut.CaptureWindow(handle, includeFrame: true, Defaults).Metadata;

        // Assert
        metadata.MonitorIndex.Should().Be(1);
        metadata.ScaleFactor.Should().Be(1.25);
    }

    #endregion

    #region Error Handling Tests

    [Fact]
    public void CaptureMonitor_WhenCaptureThrows_ReturnsCaptureFailed()
    {
        // Arrange
        _mockCaptureWrapper.Setup(c => c.CaptureScreenRegion(
                It.IsAny<int>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<int>()))
            .Throws(new InvalidOperationException("Capture failed"));
        var sut = CreateSut();

        // Act
        var capture = sut.CaptureMonitor(0, Defaults);

        // Assert
        capture.Metadata.Success.Should().BeFalse();
        capture.Metadata.ErrorCode.Should().Be("CaptureFailed");
        capture.Metadata.Error.Should().Contain("Capture failed");
        capture.Image.Should().BeNull();
    }

    [Fact]
    public void CaptureWindow_WhenCaptureThrows_ReturnsCaptureFailed()
    {
        // Arrange
        _mockCaptureWrapper.Setup(c => c.CaptureWindow(It.IsAny<nint>(), It.IsAny<bool>()))
            .Throws(new InvalidOperationException("Window has invalid dimensions (possibly minimized)"));
        var sut = CreateSut();

        // Act
        var capture = sut.CaptureWindow(new nint(12345), includeFrame: true, Defaults);

        // Assert
        capture.Metadata.Success.Should().BeFalse();
        capture.Metadata.ErrorCode.Should().Be("CaptureFailed");
        capture.Metadata.Error.Should().Contain("minimized");
    }

    [Fact]
    public void CaptureWindow_WhenTheWindowIsMinimized_ReturnsWindowMinimized_AndCapturesNothing()
    {
        // Arrange - a minimized window's rectangle is the icon's at (-32000, -32000): the capture would succeed
        // with a tiny or black image
        var handle = new nint(12345);
        _windows.Setup(w => w.IsIconic(handle)).Returns(true);
        var sut = CreateSut();

        // Act
        var capture = sut.CaptureWindow(handle, includeFrame: true, Defaults);

        // Assert
        capture.Metadata.Success.Should().BeFalse();
        capture.Metadata.ErrorCode.Should().Be("WindowMinimized");
        capture.Metadata.Error.Should().Contain("minimized").And.Contain("set_window_state");
        capture.Image.Should().BeNull();
        _mockCaptureWrapper.Verify(c => c.CaptureWindow(It.IsAny<nint>(), It.IsAny<bool>()), Times.Never);
    }

    [Fact]
    public void CaptureWindow_OfAChildOfAMinimizedWindow_ReturnsWindowMinimized()
    {
        // Arrange - a control inside a minimized window is offscreen with it, though it is not iconic itself
        var child = new nint(0x2001);
        var root = new nint(0x2000);
        _windows.Setup(w => w.GetAncestor(child, WindowAncestor.Root)).Returns(root);
        _windows.Setup(w => w.IsIconic(root)).Returns(true);

        // Act
        var capture = CreateSut().CaptureWindow(child, includeFrame: false, Defaults);

        // Assert
        capture.Metadata.ErrorCode.Should().Be("WindowMinimized");
        _mockCaptureWrapper.Verify(c => c.CaptureWindow(It.IsAny<nint>(), It.IsAny<bool>()), Times.Never);
    }

    [Fact]
    public void CaptureWindowWithElements_WhenTheWindowIsMinimized_ReturnsWindowMinimized()
    {
        // Arrange
        var handle = new nint(12345);
        _windows.Setup(w => w.IsIconic(handle)).Returns(true);

        // Act
        var capture = CreateSut().CaptureWindowWithElements(handle, "Notepad", [], includeFrame: true,
            ElementOverlayOptions.Default, Defaults);

        // Assert
        capture.Metadata.Success.Should().BeFalse();
        capture.Metadata.ErrorCode.Should().Be("WindowMinimized");
        capture.Metadata.Error.Should().Contain("minimized");
        _mockCaptureWrapper.Verify(c => c.CaptureWindow(It.IsAny<nint>(), It.IsAny<bool>()), Times.Never);
    }

    [Fact]
    public void CaptureWindow_WhenWrapperReturnsTooFewBytes_ReturnsCaptureFailed()
    {
        // Arrange
        _mockCaptureWrapper.Setup(c => c.CaptureWindow(It.IsAny<nint>(), It.IsAny<bool>()))
            .Returns(new CaptureResult(new byte[16], 100, 100, 0, 0, "PrintWindow"));
        var sut = CreateSut();

        // Act
        var capture = sut.CaptureWindow(new nint(12345), includeFrame: true, Defaults);

        // Assert
        capture.Metadata.ErrorCode.Should().Be("CaptureFailed");
    }

    #endregion

    #region CaptureWindowWithElements

    [Fact]
    public void CaptureWindowWithElements_WithoutFrame_PlacesOverlaysFromTheClientOrigin()
    {
        // Arrange - client area at (110, 140); before M1.2 the overlays were measured from the frame corner
        var handle = new nint(12345);
        _mockCaptureWrapper.Setup(c => c.CaptureWindow(handle, false))
            .Returns(new CaptureResult(new byte[200 * 100 * 4], 200, 100, 110, 140, "PrintWindow"));
        var element = ElementAt(120, 150, 40, 30);
        var sut = CreateSut();

        // Act
        var metadata = sut.CaptureWindowWithElements(handle, "Dialog", [element], includeFrame: false,
            ElementOverlayOptions.Default, Defaults).Metadata;

        // Assert
        metadata.Success.Should().BeTrue();
        metadata.CapturedRegion.Should().Be(new CapturedRegion(110, 140, 200, 100));
        metadata.ImageToScreen.Should().Be(new ImageToScreenTransform(110, 140, 1, 1));
        var annotated = metadata.Elements.Should().ContainSingle().Subject;
        annotated.ImageBounds.Should().Be(new BoundsDto(10, 10, 40, 30));
        annotated.ScreenBounds.Should().Be(element.Bounds);
        metadata.WindowTitle.Should().Be("Dialog");
    }

    [Fact]
    public void CaptureWindowWithElements_ClipsElementsThatStartOutsideTheImage()
    {
        // Arrange - the element starts 20 px left of the image, so 30 of its 50 px are visible
        var handle = new nint(12345);
        _mockCaptureWrapper.Setup(c => c.CaptureWindow(handle, true))
            .Returns(new CaptureResult(new byte[200 * 100 * 4], 200, 100, 100, 100, "PrintWindow"));
        var sut = CreateSut();

        // Act
        var metadata = sut.CaptureWindowWithElements(handle, null, [ElementAt(80, 120, 50, 20)], includeFrame: true,
            ElementOverlayOptions.Default, Defaults).Metadata;

        // Assert
        metadata.Elements.Should().ContainSingle()
            .Which.ImageBounds.Should().Be(new BoundsDto(0, 20, 30, 20));
    }

    [Fact]
    public void CaptureWindowWithElements_SkipsElementsOutsideTheImageButKeepsTheirChildren()
    {
        // Arrange
        var handle = new nint(12345);
        _mockCaptureWrapper.Setup(c => c.CaptureWindow(handle, true))
            .Returns(new CaptureResult(new byte[200 * 100 * 4], 200, 100, 0, 0, "PrintWindow"));
        var child = ElementAt(10, 10, 20, 20);
        var parent = ElementAt(500, 500, 40, 40) with { Children = [child] };
        var sut = CreateSut();

        // Act
        var metadata = sut.CaptureWindowWithElements(handle, null, [parent], includeFrame: true,
            ElementOverlayOptions.Default, Defaults).Metadata;

        // Assert
        metadata.Elements.Should().ContainSingle().Which.ScreenBounds.Should().Be(child.Bounds);
    }

    [Fact]
    public void CaptureWindowWithElements_ScaledDown_KeepsTheOverlayLinesAboutTwoPixelsWide()
    {
        // Arrange - a black 3136 x 400 window, scaled by half to the default 1568 long edge, with a
        // button whose left edge is at image x 400 (output x 200)
        var handle = new nint(12345);
        _mockCaptureWrapper.Setup(c => c.CaptureWindow(handle, true))
            .Returns(new CaptureResult(new byte[3136 * 400 * 4], 3136, 400, 0, 0, "PrintWindow"));
        var sut = CreateSut();

        // Act
        var capture = sut.CaptureWindowWithElements(handle, null, [ElementAt(400, 100, 800, 200)], includeFrame: true,
            ElementOverlayOptions.Default, new CaptureOptions { Format = CaptureFormat.Png });

        // Assert - a 2-pixel line drawn at full resolution would end up one faint pixel wide
        capture.Metadata.Width.Should().Be(1568);
        using var image = new System.Drawing.Bitmap(new MemoryStream(capture.Image!.Data));
        var greenAcrossTheLeftEdge = Enumerable.Range(190, 21).Select(x => (int)image.GetPixel(x, 100).G).ToList();
        greenAcrossTheLeftEdge.Count(g => g >= 100).Should().BeGreaterThanOrEqualTo(2,
            "the overlay must stay visible after scaling (green values across the edge: {0})", string.Join(",", greenAcrossTheLeftEdge));
    }

    [Fact]
    public void CaptureWindowWithElements_WithInvalidHandle_ReturnsInvalidHandle()
    {
        // Arrange
        var sut = CreateSut();

        // Act
        var capture = sut.CaptureWindowWithElements(nint.Zero, null, [], includeFrame: true,
            ElementOverlayOptions.Default, Defaults);

        // Assert
        capture.Metadata.Success.Should().BeFalse();
        capture.Metadata.ErrorCode.Should().Be("InvalidHandle");
        capture.Image.Should().BeNull();
    }

    [Fact]
    public void CaptureWindowWithElements_WhenCaptureThrows_ReturnsCaptureFailed()
    {
        // Arrange
        _mockCaptureWrapper.Setup(c => c.CaptureWindow(It.IsAny<nint>(), It.IsAny<bool>()))
            .Throws(new InvalidOperationException("PrintWindow failed"));
        var sut = CreateSut();

        // Act
        var metadata = sut.CaptureWindowWithElements(new nint(12345), "Title", [], includeFrame: true,
            ElementOverlayOptions.Default, Defaults).Metadata;

        // Assert
        metadata.ErrorCode.Should().Be("CaptureFailed");
        metadata.Error.Should().Contain("PrintWindow failed");
        metadata.WindowTitle.Should().Be("Title");
    }

    #endregion

    #region CapturePixels and RegisterRaw (roadmap M6: the raw pixels of OCR and visual change detection)

    private static readonly nint PixelWindow = new(0x5000);

    /// <summary>The capture tools at full resolution, so their metadata is comparable with the raw pixels.</summary>
    private static readonly CaptureOptions FullResolution = new() { MaxDimension = 0, Format = CaptureFormat.Png };

    private readonly Mock<IWindowCallRunner> _calls = new();
    private readonly Mock<ICaptureTargetPolicy> _policy = new();
    private readonly CaptureRegistry _registry = new();

    /// <summary>
    /// A service as the server builds it: the runner runs window calls inline, the target policy allows every window,
    /// and captures are registered.
    /// </summary>
    private ScreenshotService CreatePixelSut(bool withPolicy = true, bool withRegistry = true)
    {
        _calls.Setup(c => c.Run(It.IsAny<nint>(), It.IsAny<Func<CaptureResult>>()))
            .Returns((nint _, Func<CaptureResult> call) => call());
        _windows.Setup(w => w.IsWindow(It.IsAny<nint>())).Returns(true);
        _windows.Setup(w => w.GetWindowRect(PixelWindow)).Returns((100, 50, 200, 130));
        _windows.Setup(w => w.GetWindowThreadProcessId(PixelWindow)).Returns(4242u);
        return new ScreenshotService(_mockCaptureWrapper.Object, _mockMonitorService.Object, _windows.Object, _calls.Object,
            captureRegistry: withRegistry ? _registry : null, capturePolicy: withPolicy ? _policy.Object : null);
    }

    private void UseWindowPixels(bool includeFrame, byte[] pixels, int width, int height, int originX, int originY) =>
        _mockCaptureWrapper.Setup(c => c.CaptureWindow(PixelWindow, includeFrame))
            .Returns(new CaptureResult(pixels, width, height, originX, originY, "PrintWindow"));

    private static CaptureException RefusalOf(Action capture) =>
        capture.Should().Throw<CaptureException>().Which;

    [Fact]
    public void CapturePixels_OfAMonitor_IsWhatTakeScreenshotCaptures_WithTheWrappersOwnPixels()
    {
        // Arrange
        UseMonitor(TestDataFactory.CreateMonitorInfo(boundsX: -1920, boundsWidth: 1920, boundsHeight: 1080, scaleFactor: 1.25));
        var pixels = Pattern(1920, 1080);
        _mockCaptureWrapper.Setup(c => c.CaptureScreenRegion(-1920, 0, 1920, 1080))
            .Returns(new CaptureResult(pixels, 1920, 1080, -1920, 0, "ScreenBitBlt"));
        var sut = CreatePixelSut();

        // Act
        var raw = sut.CapturePixels(new CaptureTarget.Monitor(0));
        var tool = sut.CaptureMonitor(0, FullResolution).Metadata;

        // Assert - the very array the wrapper filled: nothing was scaled, encoded or copied
        raw.Pixels.Bgra.Should().BeSameAs(pixels);
        raw.CapturedRegion.Should().Be(tool.CapturedRegion);
        raw.MonitorIndex.Should().Be(tool.MonitorIndex);
        raw.ScaleFactor.Should().Be(tool.ScaleFactor);
        raw.Pixels.Method.Should().Be(tool.CaptureMethod);
        raw.Warnings.Should().BeEmpty();
        tool.Warnings.Should().BeNull();
        raw.Window.Should().BeNull();
    }

    [Fact]
    public void CapturePixels_OfTheVirtualScreen_WarnsAsTakeScreenshotDoes()
    {
        // Arrange - two monitors of different heights leave part of the bounding rectangle on no screen
        _mockMonitorService.Setup(m => m.GetAllMonitors()).Returns(new List<MonitorInfo>
        {
            TestDataFactory.CreateMonitorInfo(index: 0, boundsWidth: 1920, boundsHeight: 1080),
            TestDataFactory.CreateMonitorInfo(index: 1, isPrimary: false, boundsX: 1920, boundsWidth: 1280, boundsHeight: 720)
        });
        _mockCaptureWrapper.Setup(c => c.GetVirtualScreenBounds()).Returns((0, 0, 3200, 1080));
        _mockCaptureWrapper.Setup(c => c.CaptureScreenRegion(0, 0, 3200, 1080))
            .Returns(new CaptureResult(Pattern(3200, 1080), 3200, 1080, 0, 0, "ScreenBitBlt"));
        var sut = CreatePixelSut();

        // Act
        var raw = sut.CapturePixels(new CaptureTarget.Monitor(ScreenshotService.VirtualScreenIndex));
        var tool = sut.CaptureMonitor(ScreenshotService.VirtualScreenIndex, FullResolution).Metadata;

        // Assert
        raw.Warnings.Should().Equal(tool.Warnings);
        raw.Warnings.Should().ContainSingle().Which.Should().StartWith("RegionPartlyOffMonitors:");
        raw.MonitorIndex.Should().BeNull();
        raw.CapturedRegion.Should().Be(tool.CapturedRegion);
    }

    [Theory]
    [InlineData("monitor out of range")]
    [InlineData("empty region")]
    [InlineData("region off the screen")]
    [InlineData("region over the pixel budget")]
    [InlineData("region edge over the limit")]
    public void CapturePixels_RefusesAScreenTarget_AsTheCaptureToolsDo(string target)
    {
        // Arrange
        _mockCaptureWrapper.Setup(c => c.GetVirtualScreenBounds()).Returns((-20000, -20000, 40000, 40000));
        var sut = CreatePixelSut();
        CaptureTarget pixels;
        ScreenshotResult tool;
        switch (target)
        {
            case "monitor out of range":
                pixels = new CaptureTarget.Monitor(5);
                tool = sut.CaptureMonitor(5, FullResolution).Metadata;
                break;
            case "empty region":
                pixels = new CaptureTarget.Region(0, 0, 0, 10);
                tool = sut.CaptureRegion(0, 0, 0, 10, FullResolution).Metadata;
                break;
            case "region off the screen":
                pixels = new CaptureTarget.Region(30000, 30000, 10, 10);
                tool = sut.CaptureRegion(30000, 30000, 10, 10, FullResolution).Metadata;
                break;
            case "region over the pixel budget":
                pixels = new CaptureTarget.Region(0, 0, 9000, 9000);
                tool = sut.CaptureRegion(0, 0, 9000, 9000, FullResolution).Metadata;
                break;
            default:
                pixels = new CaptureTarget.Region(0, 0, 17000, 10);
                tool = sut.CaptureRegion(0, 0, 17000, 10, FullResolution).Metadata;
                break;
        }

        // Act
        var refusal = RefusalOf(() => sut.CapturePixels(pixels));

        // Assert
        tool.Success.Should().BeFalse();
        refusal.Code.ToString().Should().Be(tool.ErrorCode);
        refusal.Message.Should().Be(tool.Error);
        _mockCaptureWrapper.Verify(c => c.CaptureScreenRegion(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<int>()), Times.Never);
    }

    [Fact]
    public void CapturePixels_OfARegionPartlyOffTheScreen_IsCutToIt_WithCaptureRegionsWarning()
    {
        // Arrange
        var sut = CreatePixelSut();

        // Act
        var raw = sut.CapturePixels(new CaptureTarget.Region(1800, 1000, 300, 200));
        var tool = sut.CaptureRegion(1800, 1000, 300, 200, FullResolution).Metadata;

        // Assert
        raw.CapturedRegion.Should().Be(new CapturedRegion(1800, 1000, 120, 80)).And.Be(tool.CapturedRegion);
        raw.Warnings.Should().Equal(tool.Warnings);
        raw.Warnings.Should().Contain(w => w.StartsWith("RegionClipped:"));
        raw.MonitorIndex.Should().Be(tool.MonitorIndex);
    }

    [Theory]
    [InlineData("minimized", "WindowMinimized")]
    [InlineData("hidden", "WindowHidden")]
    [InlineData("cloaked", "Cloaked")]
    [InlineData("no window", "InvalidHandle")]
    public void CapturePixels_RefusesAWindow_AsCaptureWindowDoes_AndReadsNoPixels(string state, string code)
    {
        // Arrange
        var sut = CreatePixelSut();
        switch (state)
        {
            case "minimized":
                _windows.Setup(w => w.IsIconic(PixelWindow)).Returns(true);
                break;
            case "hidden":
                _windows.Setup(w => w.IsWindowVisible(PixelWindow)).Returns(false);
                break;
            case "cloaked":
                _windows.Setup(w => w.IsCloaked(PixelWindow)).Returns(true);
                break;
            default:
                _mockCaptureWrapper.Setup(c => c.IsValidWindow(PixelWindow)).Returns(false);
                break;
        }

        // Act
        var refusal = RefusalOf(() => sut.CapturePixels(new CaptureTarget.Window(PixelWindow, IncludeFrame: true)));
        var tool = sut.CaptureWindow(PixelWindow, includeFrame: true, FullResolution).Metadata;

        // Assert
        refusal.Code.ToString().Should().Be(code).And.Be(tool.ErrorCode);
        refusal.Message.Should().Be(tool.Error);
        _mockCaptureWrapper.Verify(c => c.CaptureWindow(It.IsAny<nint>(), It.IsAny<bool>()), Times.Never);
        _mockCaptureWrapper.Verify(c => c.CaptureWindowFromScreen(It.IsAny<nint>(), It.IsAny<bool>()), Times.Never);
    }

    [Theory]
    [InlineData(VirtualDesktopPlacement.OtherDesktop, ScreenshotService.OtherDesktopMessage)]
    [InlineData(VirtualDesktopPlacement.NoDesktop, ScreenshotService.NoDesktopMessage)]
    [InlineData(VirtualDesktopPlacement.Unknown, ScreenshotService.CloakedMessage)]
    public void CaptureWindow_OfACloakedWindow_SaysWhetherItIsOnAnotherVirtualDesktop(
        VirtualDesktopPlacement placement, string message)
    {
        // Arrange - roadmap M7.3: the virtual desktop manager tells a window on another desktop from a hidden one
        _windows.Setup(w => w.IsWindow(It.IsAny<nint>())).Returns(true);
        _windows.Setup(w => w.IsCloaked(PixelWindow)).Returns(true);
        var desktops = new Mock<INativeVirtualDesktopWrapper>();
        desktops.Setup(d => d.GetPlacements(It.IsAny<IReadOnlyList<nint>>())).Returns([placement]);
        var sut = new ScreenshotService(_mockCaptureWrapper.Object, _mockMonitorService.Object, _windows.Object, _calls.Object,
            virtualDesktops: desktops.Object);

        // Act
        var result = sut.CaptureWindow(PixelWindow, includeFrame: true, FullResolution).Metadata;

        // Assert
        result.ErrorCode.Should().Be(nameof(ScreenshotErrorCode.Cloaked));
        result.Error.Should().Be(message);
        _mockCaptureWrapper.Verify(c => c.CaptureWindow(It.IsAny<nint>(), It.IsAny<bool>()), Times.Never);
    }

    [Fact]
    public void CapturePixels_OfAWindowWithFrame_IsCutToTheVisibleFrame_AsCaptureWindowIs()
    {
        // Arrange - GetWindowRect (100, 50, 100x80) includes 7 invisible border pixels left, right and bottom
        UseWindowPixels(includeFrame: true, Pattern(100, 80), 100, 80, 100, 50);
        _windows.Setup(w => w.GetExtendedFrameBounds(PixelWindow)).Returns((107, 50, 193, 123));
        var sut = CreatePixelSut();

        // Act
        var raw = sut.CapturePixels(new CaptureTarget.Window(PixelWindow, IncludeFrame: true));
        var tool = sut.CaptureWindow(PixelWindow, includeFrame: true, FullResolution).Metadata;

        // Assert
        raw.CapturedRegion.Should().Be(new CapturedRegion(107, 50, 86, 73)).And.Be(tool.CapturedRegion);
        raw.Pixels.Bgra.Should().HaveCount(86 * 73 * 4);
        PixelAt(raw.Pixels, 0, 0).Should().Be(PatternPixel(7, 0));
        PixelAt(raw.Pixels, 85, 72).Should().Be(PatternPixel(92, 72));
        raw.Pixels.Method.Should().Be("PrintWindow").And.Be(tool.CaptureMethod);
        raw.Window.Should().Be(new CapturedWindow((long)PixelWindow, 4242, WindowRect.FromLTRB(100, 50, 200, 130), IncludeFrame: true));
    }

    [Fact]
    public void CapturePixels_OfAWindowWithoutFrame_KeepsItsClientArea_AsCaptureWindowDoes()
    {
        // Arrange
        UseWindowPixels(includeFrame: false, Pattern(90, 60), 90, 60, 108, 81);
        var sut = CreatePixelSut();

        // Act
        var raw = sut.CapturePixels(new CaptureTarget.Window(PixelWindow, IncludeFrame: false));

        // Assert
        raw.CapturedRegion.Should().Be(new CapturedRegion(108, 81, 90, 60));
        raw.Window!.ClientArea.Should().Be(new WindowRect(108, 81, 90, 60));
        _windows.Verify(w => w.GetExtendedFrameBounds(It.IsAny<nint>()), Times.Never);
    }

    [Fact]
    public void CapturePixels_OfAWindowThatDoesNotAnswer_CopiesTheScreen_WithCaptureWindowsWarning()
    {
        // Arrange
        var sut = CreatePixelSut();
        _calls.Setup(c => c.Run(PixelWindow, It.IsAny<Func<CaptureResult>>()))
            .Throws(new WindowNotRespondingException(PixelWindow, "not responding"));
        _mockCaptureWrapper.Setup(c => c.CaptureWindowFromScreen(PixelWindow, false))
            .Returns(new CaptureResult(Pattern(100, 80), 100, 80, 300, 200, "ScreenBitBlt"));

        // Act
        var raw = sut.CapturePixels(new CaptureTarget.Window(PixelWindow, IncludeFrame: false));
        var tool = sut.CaptureWindow(PixelWindow, includeFrame: false, FullResolution).Metadata;

        // Assert
        raw.Pixels.Method.Should().Be("ScreenBitBlt");
        raw.Warnings.Should().ContainSingle().Which.Should().StartWith("WindowNotResponding:");
        raw.Warnings.Should().Equal(tool.Warnings);
    }

    [Fact]
    public void CapturePixels_OfAWindowScaledByWindows_WarnsAboutDpi_AsCaptureWindowDoes()
    {
        // Arrange - a DPI-unaware window (96 DPI) on a 150% monitor (144 DPI)
        _mockMonitorService.Setup(m => m.GetMonitorForWindow(PixelWindow))
            .Returns(TestDataFactory.CreateMonitorInfo(index: 1, scaleFactor: 1.5));
        _windows.Setup(w => w.GetDpiForWindow(PixelWindow)).Returns(96u);
        UseWindowPixels(includeFrame: false, Pattern(100, 80), 100, 80, 0, 0);
        var sut = CreatePixelSut();

        // Act
        var raw = sut.CapturePixels(new CaptureTarget.Window(PixelWindow, IncludeFrame: false));
        var tool = sut.CaptureWindow(PixelWindow, includeFrame: false, FullResolution).Metadata;

        // Assert
        raw.Warnings.Should().ContainSingle().Which.Should().StartWith("DpiScaledWindow:");
        raw.Warnings.Should().Equal(tool.Warnings);
        raw.MonitorIndex.Should().Be(1).And.Be(tool.MonitorIndex);
        raw.ScaleFactor.Should().Be(1.5).And.Be(tool.ScaleFactor);
    }

    [Fact]
    public void CapturePixels_OfOneSolidColor_WarnsThatTheImageIsBlank_AsCaptureWindowDoes()
    {
        // Arrange - blank pixels (all zero) from the default wrapper
        UseWindowPixels(includeFrame: false, new byte[100 * 80 * 4], 100, 80, 0, 0);
        var sut = CreatePixelSut();

        // Act
        var raw = sut.CapturePixels(new CaptureTarget.Window(PixelWindow, IncludeFrame: false));
        var tool = sut.CaptureWindow(PixelWindow, includeFrame: false, FullResolution).Metadata;

        // Assert
        raw.Warnings.Should().ContainSingle().Which.Should().StartWith("ImageBlank:");
        raw.Warnings.Should().Equal(tool.Warnings);
    }

    [Fact]
    public void CapturePixels_EncodesNothing_SoPixelsNoImageCanHoldStillComeBack()
    {
        // Arrange - JPEG cannot hold an edge longer than 65535 pixels: capture_window fails to encode these pixels
        var pixels = Pattern(70000, 1);
        UseWindowPixels(includeFrame: false, pixels, 70000, 1, 0, 0);
        var registry = new Mock<ICaptureRegistry>();
        _calls.Setup(c => c.Run(It.IsAny<nint>(), It.IsAny<Func<CaptureResult>>()))
            .Returns((nint _, Func<CaptureResult> call) => call());
        var sut = new ScreenshotService(_mockCaptureWrapper.Object, _mockMonitorService.Object, _windows.Object, _calls.Object,
            captureRegistry: registry.Object, capturePolicy: _policy.Object);

        // Act
        var tool = sut.CaptureWindow(PixelWindow, includeFrame: false, new CaptureOptions { MaxDimension = 0, Format = CaptureFormat.Jpeg }).Metadata;
        var raw = sut.CapturePixels(new CaptureTarget.Window(PixelWindow, IncludeFrame: false));

        // Assert - and the raw capture is not registered either until RegisterRaw
        tool.ErrorCode.Should().Be("EncodeFailed");
        raw.Pixels.Bgra.Should().BeSameAs(pixels);
        registry.Verify(r => r.Register(It.IsAny<CaptureGeometry>()), Times.Never);
    }

    [Fact]
    public void CapturePixels_WhenTheNativeCaptureFails_IsCaptureFailed_AsCaptureWindowIs()
    {
        // Arrange
        _mockCaptureWrapper.Setup(c => c.CaptureWindow(PixelWindow, true)).Throws(new InvalidOperationException("PrintWindow failed"));
        var sut = CreatePixelSut();

        // Act
        var refusal = RefusalOf(() => sut.CapturePixels(new CaptureTarget.Window(PixelWindow, IncludeFrame: true)));
        var tool = sut.CaptureWindow(PixelWindow, includeFrame: true, FullResolution).Metadata;

        // Assert
        refusal.Code.Should().Be(ScreenshotErrorCode.CaptureFailed);
        refusal.Message.Should().Be(tool.Error);
    }

    [Fact]
    public void CapturePixels_OfAWindowThePolicyDenies_IsTargetDeniedByPolicy_BeforeAnythingOfItIsRead()
    {
        // Arrange
        var denied = new CaptureRefusal(ScreenshotErrorCode.TargetDeniedByPolicy, "The window is refused by the server's target policy: KeePass.");
        _policy.Setup(p => p.Check(PixelWindow)).Returns(denied);
        var sut = CreatePixelSut();

        // Act
        var refusal = RefusalOf(() => sut.CapturePixels(new CaptureTarget.Window(PixelWindow, IncludeFrame: true)));

        // Assert
        refusal.Code.Should().Be(ScreenshotErrorCode.TargetDeniedByPolicy);
        refusal.Message.Should().Be(denied.Message);
        _mockCaptureWrapper.Verify(c => c.CaptureWindow(It.IsAny<nint>(), It.IsAny<bool>()), Times.Never);
        _mockCaptureWrapper.Verify(c => c.CaptureWindowFromScreen(It.IsAny<nint>(), It.IsAny<bool>()), Times.Never);
        _windows.Verify(w => w.GetWindowRect(It.IsAny<nint>()), Times.Never);
    }

    [Fact]
    public void CapturePixels_AsksThePolicyAboutTheWindow_AndOnlyAboutWindows()
    {
        // Arrange
        var sut = CreatePixelSut();

        // Act
        sut.CapturePixels(new CaptureTarget.Window(PixelWindow, IncludeFrame: true));
        sut.CapturePixels(new CaptureTarget.Monitor(0));
        sut.CapturePixels(new CaptureTarget.Region(0, 0, 10, 10));

        // Assert - region and monitor captures are not policy-checked, as capture_region and take_screenshot are not
        _policy.Verify(p => p.Check(PixelWindow), Times.Once);
        _policy.Verify(p => p.Check(It.IsAny<nint>()), Times.Once);
    }

    [Fact]
    public void CapturePixels_WithoutATargetPolicy_RefusesEveryWindow_ButStillCapturesTheScreen()
    {
        // Arrange - a service built without the policy check cannot tell a denied window from an allowed one
        var sut = CreatePixelSut(withPolicy: false);

        // Act
        var refusal = RefusalOf(() => sut.CapturePixels(new CaptureTarget.Window(PixelWindow, IncludeFrame: true)));
        var monitor = sut.CapturePixels(new CaptureTarget.Monitor(0));

        // Assert
        refusal.Code.Should().Be(ScreenshotErrorCode.TargetDeniedByPolicy);
        _mockCaptureWrapper.Verify(c => c.CaptureWindow(It.IsAny<nint>(), It.IsAny<bool>()), Times.Never);
        monitor.Pixels.Width.Should().Be(1920);
    }

    [Fact]
    public void CaptureWindow_DoesNotAskTheCapturePolicy_TheToolDoesBeforeItRestoresAnything()
    {
        // Arrange - capture_window checks the policy itself, before restoreIfMinimized shows the window
        var sut = CreatePixelSut();

        // Act
        sut.CaptureWindow(PixelWindow, includeFrame: true, FullResolution);

        // Assert
        _policy.Verify(p => p.Check(It.IsAny<nint>()), Times.Never);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void RegisterRaw_RegistersAWindowCapture_OneToOne_WithTheWindow(bool includeFrame)
    {
        // Arrange
        UseWindowPixels(includeFrame, Pattern(90, 60), 90, 60, 108, 81);
        var sut = CreatePixelSut();
        var raw = sut.CapturePixels(new CaptureTarget.Window(PixelWindow, includeFrame));

        // Act
        var captureId = sut.RegisterRaw(raw);

        // Assert
        var geometry = _registry.Find(captureId!)!.Geometry;
        geometry.ImageWidth.Should().Be(90);
        geometry.ImageHeight.Should().Be(60);
        geometry.ImageToScreen.Should().Be(new ImageToScreenTransform(108, 81, 1, 1));
        geometry.CapturedRegion.Should().Be(new CapturedRegion(108, 81, 90, 60));
        geometry.Window.Should().Be(raw.Window);
        geometry.Window!.IncludeFrame.Should().Be(includeFrame);
        geometry.VirtualScreen.Should().BeNull();
    }

    [Fact]
    public void RegisterRaw_RegistersAScreenCapture_WithTheVirtualScreenAndTheMonitors()
    {
        // Arrange
        _mockCaptureWrapper.Setup(c => c.GetVirtualScreenBounds()).Returns((-1920, 0, 3840, 1080));
        var sut = CreatePixelSut();
        var raw = sut.CapturePixels(new CaptureTarget.Region(10, 20, 300, 200));

        // Act
        var geometry = _registry.Find(sut.RegisterRaw(raw)!)!.Geometry;

        // Assert
        geometry.ImageToScreen.Should().Be(new ImageToScreenTransform(10, 20, 1, 1));
        geometry.Window.Should().BeNull();
        geometry.VirtualScreen.Should().Be(new VirtualScreenBounds(-1920, 0, 3840, 1080));
        geometry.Monitors.Should().ContainSingle();
    }

    public static TheoryData<CaptureTarget> ScreenTargets => new()
    {
        new CaptureTarget.Region(10, 20, 300, 200),
        new CaptureTarget.Monitor(0),
        new CaptureTarget.Monitor(ScreenshotService.VirtualScreenIndex)
    };

    [Theory]
    [MemberData(nameof(ScreenTargets))]
    public void RegisterRaw_AfterTheMonitorsChangedSinceThePixels_RegistersTheLayoutOfThePixels_SoAClickIsStaleCapture(
        CaptureTarget target)
    {
        // Arrange - the pixels are read with two monitors side by side
        var monitors = new List<MonitorInfo>
        {
            TestDataFactory.CreateMonitorInfo(0, @"\\.\DISPLAY1", true, 0, 0, 1920, 1080),
            TestDataFactory.CreateMonitorInfo(1, @"\\.\DISPLAY2", false, -1920, 0, 1920, 1080)
        };
        var virtualScreen = (X: -1920, Y: 0, Width: 3840, Height: 1080);
        _mockMonitorService.Setup(m => m.GetAllMonitors()).Returns(() => monitors);
        _mockCaptureWrapper.Setup(c => c.GetVirtualScreenBounds()).Returns(() => virtualScreen);
        var sut = CreatePixelSut();
        var raw = sut.CapturePixels(target);
        var atCapture = CapturedMonitors.Of(monitors);

        // ... and during the slow work after them (an OCR pass) the left monitor is undocked
        monitors = [TestDataFactory.CreateMonitorInfo(0, @"\\.\DISPLAY1", true, 0, 0, 1920, 1080)];
        virtualScreen = (0, 0, 1920, 1080);

        // Act
        var captureId = sut.RegisterRaw(raw)!;

        // Assert - the capture keeps the layout its pixels were read in, so the click sees the change
        var geometry = _registry.Find(captureId)!.Geometry;
        geometry.VirtualScreen.Should().Be(new VirtualScreenBounds(-1920, 0, 3840, 1080));
        geometry.Monitors.Should().Equal(atCapture);
        var point = new CoordinateSpaceResolver(_registry, _windows.Object, new Mock<INativeHitTestWrapper>().Object,
            _mockCaptureWrapper.Object, _mockMonitorService.Object).FromImage(captureId, 5, 5);
        point.ErrorCode.Should().Be(InputErrorCode.StaleCapture);
    }

    [Fact]
    public void RegisterRaw_AfterAMonitorMovedInsideTheSameVirtualScreen_IsStaleCapture()
    {
        // Arrange - two monitors swap places after the pixels: the virtual screen stays the same
        var monitors = new List<MonitorInfo>
        {
            TestDataFactory.CreateMonitorInfo(0, @"\\.\DISPLAY1", true, 0, 0, 1920, 1080),
            TestDataFactory.CreateMonitorInfo(1, @"\\.\DISPLAY2", false, -1920, 0, 1920, 1080)
        };
        _mockMonitorService.Setup(m => m.GetAllMonitors()).Returns(() => monitors);
        _mockCaptureWrapper.Setup(c => c.GetVirtualScreenBounds()).Returns((-1920, 0, 3840, 1080));
        var sut = CreatePixelSut();
        var raw = sut.CapturePixels(new CaptureTarget.Region(-1900, 20, 300, 200));
        monitors =
        [
            TestDataFactory.CreateMonitorInfo(0, @"\\.\DISPLAY1", true, -1920, 0, 1920, 1080),
            TestDataFactory.CreateMonitorInfo(1, @"\\.\DISPLAY2", false, 0, 0, 1920, 1080)
        ];

        // Act
        var captureId = sut.RegisterRaw(raw)!;

        // Assert
        var point = new CoordinateSpaceResolver(_registry, _windows.Object, new Mock<INativeHitTestWrapper>().Object,
            _mockCaptureWrapper.Object, _mockMonitorService.Object).FromImage(captureId, 5, 5);
        point.ErrorCode.Should().Be(InputErrorCode.StaleCapture);
    }

    [Fact]
    public void RegisterRaw_WithoutARegistry_OrForAWindowThatCouldNotBeRead_GivesNoCaptureId()
    {
        // Arrange
        var unregistered = CreatePixelSut(withRegistry: false);
        var raw = unregistered.CapturePixels(new CaptureTarget.Window(PixelWindow, IncludeFrame: true));
        var registered = CreatePixelSut();
        _windows.Setup(w => w.GetWindowRect(PixelWindow)).Returns(((int, int, int, int)?)null);
        var unreadWindow = registered.CapturePixels(new CaptureTarget.Window(PixelWindow, IncludeFrame: true));

        // Act + Assert
        raw.Window.Should().BeNull();
        unregistered.RegisterRaw(raw).Should().BeNull();
        unreadWindow.Window.Should().BeNull();
        registered.RegisterRaw(unreadWindow).Should().BeNull();
    }

    /// <summary>Pixels whose color encodes their position, so a crop can be checked pixel by pixel.</summary>
    private static byte[] Pattern(int width, int height)
    {
        var pixels = new byte[width * height * 4];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var (b, g, r) = PatternPixel(x, y);
                var i = (y * width + x) * 4;
                pixels[i] = b;
                pixels[i + 1] = g;
                pixels[i + 2] = r;
            }
        }

        return pixels;
    }

    private static (byte B, byte G, byte R) PatternPixel(int x, int y) => ((byte)(x * 7 % 256), (byte)(y * 3 % 256), (byte)((x + y) % 256));

    private static (byte B, byte G, byte R) PixelAt(CaptureResult capture, int x, int y)
    {
        var i = (y * capture.Width + x) * 4;
        return (capture.Bgra[i], capture.Bgra[i + 1], capture.Bgra[i + 2]);
    }

    #endregion

    private void UseMonitor(MonitorInfo monitor) =>
        _mockMonitorService.Setup(m => m.GetAllMonitors()).Returns(new List<MonitorInfo> { monitor });

    private static UIElementInfo ElementAt(int x, int y, int width, int height) =>
        new(
            Name: "OK",
            ControlType: "Button",
            AutomationId: "ok",
            Bounds: new BoundsDto(x, y, width, height),
            IsEnabled: true,
            IsOffscreen: false);
}
