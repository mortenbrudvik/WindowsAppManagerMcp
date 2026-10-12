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
            ElementOverlayOptions.Default, new CaptureOptions { Format = "png" });

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
