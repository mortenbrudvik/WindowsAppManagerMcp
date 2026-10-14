namespace WindowsAppManagerMcp.Tests.Unit.Services;

/// <summary>
/// Roadmap M3.11 in the screenshot service: every successful capture is registered and reports its captureId, with the
/// geometry an image-space click converts through; and take_screenshot's monitorIndex -1 captures the virtual screen.
/// </summary>
public class ScreenshotCaptureIdTests
{
    private static readonly CaptureOptions Defaults = new();
    private static readonly nint Window = 0x1234;

    private readonly Mock<IScreenCaptureWrapper> _capture =
        TestDataFactory.CreateMockScreenCaptureWrapper(captureWidth: 800, captureHeight: 600, windowOriginX: 108, windowOriginY: 231);

    private readonly Mock<IMonitorService> _monitors = TestDataFactory.CreateMockMonitorService();
    private readonly Mock<INativeWindowWrapper> _windows = new();
    private readonly CaptureRegistry _registry = new();

    public ScreenshotCaptureIdTests()
    {
        _windows.Setup(w => w.IsWindowVisible(It.IsAny<nint>())).Returns(true);
        _windows.Setup(w => w.GetWindowRect(Window)).Returns((100, 200, 916, 839));
        _windows.Setup(w => w.GetWindowThreadProcessId(Window)).Returns(4242u);
    }

    private ScreenshotService CreateSut(ICaptureRegistry? registry = null) =>
        new(_capture.Object, _monitors.Object, _windows.Object, captureRegistry: registry ?? _registry);

    [Fact]
    public void CaptureMonitor_RegistersTheCapture_WithItsTransformAndTheVirtualScreen()
    {
        // Arrange
        _capture.Setup(c => c.GetVirtualScreenBounds()).Returns((-1920, 0, 3840, 1080));

        // Act
        var metadata = CreateSut().CaptureMonitor(0, Defaults).Metadata;

        // Assert
        metadata.CaptureId.Should().NotBeNullOrEmpty();
        var record = _registry.Find(metadata.CaptureId!)!;
        record.Geometry.ImageToScreen.Should().Be(metadata.ImageToScreen);
        record.Geometry.CapturedRegion.Should().Be(metadata.CapturedRegion);
        record.Geometry.ImageWidth.Should().Be(metadata.Width);
        record.Geometry.ImageHeight.Should().Be(metadata.Height);
        record.Geometry.Window.Should().BeNull();
        record.Geometry.VirtualScreen.Should().Be(new VirtualScreenBounds(-1920, 0, 3840, 1080));
    }

    [Fact]
    public void CaptureMonitor_RegistersTheMonitors_ByDeviceName()
    {
        // Arrange
        var monitors = new List<MonitorInfo>
        {
            new(0, "DISPLAY2", true, new MonitorRect(0, 0, 1920, 1080), new MonitorRect(0, 0, 1920, 1040), 1.0),
            new(1, "DISPLAY1", false, new MonitorRect(-1920, 0, 1920, 1080), new MonitorRect(-1920, 0, 1920, 1040), 1.0)
        };
        _monitors.Setup(m => m.GetAllMonitors()).Returns(monitors);

        // Act
        var metadata = CreateSut().CaptureMonitor(0, Defaults).Metadata;

        // Assert
        _registry.Find(metadata.CaptureId!)!.Geometry.Monitors.Should().Equal(
            new CapturedMonitor("DISPLAY1", new MonitorRect(-1920, 0, 1920, 1080)),
            new CapturedMonitor("DISPLAY2", new MonitorRect(0, 0, 1920, 1080)));
    }

    [Fact]
    public void CaptureRegion_RegistersTheCapture()
    {
        var metadata = CreateSut().CaptureRegion(10, 20, 300, 200, Defaults).Metadata;

        _registry.Find(metadata.CaptureId!)!.Geometry.ImageToScreen.Should().Be(new ImageToScreenTransform(10, 20, 1, 1));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void CaptureWindow_RegistersTheWindow_AsItWasBeforeTheCapture(bool includeFrame)
    {
        // Act
        var metadata = CreateSut().CaptureWindow(Window, includeFrame, Defaults).Metadata;

        // Assert - a client capture also keeps the client area it shows (800 x 600 at (108, 231))
        var geometry = _registry.Find(metadata.CaptureId!)!.Geometry;
        geometry.Window.Should().Be(new CapturedWindow(0x1234, 4242, WindowRect.FromLTRB(100, 200, 916, 839), includeFrame,
            includeFrame ? null : new WindowRect(108, 231, 800, 600)));
        geometry.ImageToScreen.Should().Be(metadata.ImageToScreen);
        geometry.VirtualScreen.Should().BeNull();
    }

    [Fact]
    public void CaptureWindow_ReadsTheWindowRectangleBeforeThePixels()
    {
        // Arrange - a window that moves while it is captured: the record keeps the rectangle the origin was read with
        var order = new List<string>();
        _windows.Setup(w => w.GetWindowRect(Window)).Callback(() => order.Add("rect")).Returns((100, 200, 916, 839));
        _capture.Setup(c => c.CaptureWindow(Window, true)).Callback(() => order.Add("pixels"))
            .Returns(new CaptureResult(new byte[800 * 600 * 4], 800, 600, 108, 231, "PrintWindow"));

        // Act
        CreateSut().CaptureWindow(Window, true, Defaults);

        // Assert
        order.First().Should().Be("rect");
    }

    [Fact]
    public void CaptureWithElements_RegistersTheWindow()
    {
        var metadata = CreateSut().CaptureWindowWithElements(Window, "App", [], includeFrame: false, ElementOverlayOptions.Default, Defaults).Metadata;

        var geometry = _registry.Find(metadata.CaptureId!)!.Geometry;
        geometry.Window.Should().Be(new CapturedWindow(0x1234, 4242, WindowRect.FromLTRB(100, 200, 916, 839), false,
            new WindowRect(108, 231, 800, 600)));
        geometry.ImageToScreen.Should().Be(metadata.ImageToScreen);
    }

    [Fact]
    public void ReadCapturedWindow_IsTheWindowAsACaptureRegistersIt()
    {
        CreateSut().ReadCapturedWindow(Window, includeFrame: true).Should()
            .Be(new CapturedWindow(0x1234, 4242, WindowRect.FromLTRB(100, 200, 916, 839), true));
    }

    [Fact]
    public void CaptureWithElements_OfAWindowThatMovedSinceItsElementsWereRead_HasNoCaptureId_AndSaysWhy()
    {
        // Arrange - the elements were read while the window was 100 pixels further left
        var walked = new CapturedWindow(0x1234, 4242, WindowRect.FromLTRB(0, 200, 816, 839), false);

        // Act
        var metadata = CreateSut().CaptureWindowWithElements(Window, "App", [], includeFrame: false, ElementOverlayOptions.Default,
            Defaults, walked).Metadata;

        // Assert
        metadata.Success.Should().BeTrue(metadata.Error);
        metadata.CaptureId.Should().BeNull("the marks are offset from what the image shows");
        metadata.Warnings.Should().ContainSingle(w => w.StartsWith(CaptureWarnings.WindowMoved + ":"))
            .Which.Should().Contain("Capture it again");
    }

    [Fact]
    public void CaptureWithElements_ThatMarksNoMovedWindow_LeavesTheMarksOut_AndSaysSo()
    {
        // Arrange - get_desktop_state's marks (roadmap M3.F32) are only a picture of its element lines: none is better than offset ones
        var walked = new CapturedWindow(0x1234, 4242, WindowRect.FromLTRB(0, 200, 816, 839), false);
        var save = new UIElementInfo("Save", "Button", "btnSave", new BoundsDto(150, 260, 80, 24), true, false, Ref: "w4660e7");

        // Act
        var metadata = CreateSut().CaptureWindowWithElements(Window, "App", [save], includeFrame: false,
            new ElementOverlayOptions { MarkMovedWindow = false }, Defaults, walked).Metadata;

        // Assert
        metadata.Success.Should().BeTrue(metadata.Error);
        metadata.Elements.Should().BeEmpty();
        metadata.ElementCount.Should().Be(0);
        metadata.CaptureId.Should().BeNull();
        metadata.Warnings.Should().ContainSingle(w => w.StartsWith(CaptureWarnings.WindowMoved + ":"))
            .Which.Should().Contain("no marks were drawn");
    }

    [Fact]
    public void CaptureWithElements_ThatMarksNoMovedWindow_StillMarksAWindowThatStayed()
    {
        var save = new UIElementInfo("Save", "Button", "btnSave", new BoundsDto(150, 260, 80, 24), true, false, Ref: "w4660e7");

        var metadata = CreateSut().CaptureWindowWithElements(Window, "App", [save], includeFrame: false,
            new ElementOverlayOptions { MarkMovedWindow = false }, Defaults,
            new CapturedWindow(0x1234, 4242, WindowRect.FromLTRB(100, 200, 916, 839), false)).Metadata;

        metadata.Elements.Should().ContainSingle().Which.Ref.Should().Be("w4660e7");
        metadata.CaptureId.Should().NotBeNull();
    }

    [Fact]
    public void CaptureWithElements_OfAWindowThatStayedWithinTheTolerance_IsRegistered()
    {
        var walked = new CapturedWindow(0x1234, 4242, WindowRect.FromLTRB(101, 199, 917, 840), false);

        var metadata = CreateSut().CaptureWindowWithElements(Window, "App", [], includeFrame: false, ElementOverlayOptions.Default,
            Defaults, walked).Metadata;

        metadata.CaptureId.Should().NotBeNull();
        (metadata.Warnings ?? []).Should().NotContain(w => w.StartsWith(CaptureWarnings.WindowMoved));
    }

    [Fact]
    public void AWindowWhoseRectangleCannotBeRead_IsCapturedWithoutACaptureId()
    {
        // Without the rectangle a later click could not tell whether the window moved
        _windows.Setup(w => w.GetWindowRect(Window)).Returns(((int, int, int, int)?)null);

        var metadata = CreateSut().CaptureWindow(Window, true, Defaults).Metadata;

        metadata.Success.Should().BeTrue();
        metadata.CaptureId.Should().BeNull();
    }

    [Fact]
    public void AFailedCapture_IsNotRegistered()
    {
        var registry = new Mock<ICaptureRegistry>();

        var metadata = CreateSut(registry.Object).CaptureMonitor(7, Defaults).Metadata;

        metadata.Success.Should().BeFalse();
        metadata.CaptureId.Should().BeNull();
        registry.Verify(r => r.Register(It.IsAny<CaptureGeometry>()), Times.Never);
    }

    [Fact]
    public void WithoutARegistry_CapturesHaveNoCaptureId()
    {
        var sut = new ScreenshotService(_capture.Object, _monitors.Object, _windows.Object);

        sut.CaptureMonitor(0, Defaults).Metadata.CaptureId.Should().BeNull();
    }

    [Fact]
    public void EveryCapture_GetsItsOwnId()
    {
        var sut = CreateSut();

        var first = sut.CaptureMonitor(0, Defaults).Metadata.CaptureId;
        var second = sut.CaptureMonitor(0, Defaults).Metadata.CaptureId;

        first.Should().NotBe(second);
    }

    [Fact]
    public void CaptureMonitor_MinusOne_CapturesTheVirtualScreen()
    {
        // Arrange - two monitors side by side, the left one at negative coordinates
        _capture.Setup(c => c.GetVirtualScreenBounds()).Returns((-1920, 0, 3840, 1080));
        _monitors.Setup(m => m.GetAllMonitors()).Returns(
        [
            TestDataFactory.CreateMonitorInfo(index: 0, isPrimary: false, boundsX: -1920, boundsY: 0, boundsWidth: 1920, boundsHeight: 1080),
            TestDataFactory.CreateMonitorInfo(index: 1, isPrimary: true, boundsX: 0, boundsY: 0, boundsWidth: 1920, boundsHeight: 1080)
        ]);

        // Act
        var metadata = CreateSut().CaptureMonitor(ScreenshotService.VirtualScreenIndex, new CaptureOptions { MaxDimension = 0 }).Metadata;

        // Assert
        metadata.Success.Should().BeTrue();
        _capture.Verify(c => c.CaptureScreenRegion(-1920, 0, 3840, 1080), Times.Once);
        metadata.CapturedRegion.Should().Be(new CapturedRegion(-1920, 0, 3840, 1080));
        metadata.ImageToScreen.Should().Be(new ImageToScreenTransform(-1920, 0, 1, 1));
        metadata.MonitorIndex.Should().BeNull("the image spans every monitor");
        metadata.ScaleFactor.Should().BeNull();
        (metadata.Warnings ?? []).Should().NotContain(w => w.StartsWith(CaptureWarnings.RegionPartlyOffMonitors), "the monitors fill it");
        metadata.CaptureId.Should().NotBeNull();
    }

    [Fact]
    public void CaptureMonitor_MinusOne_WarnsAboutAreasBetweenMonitors()
    {
        // A 1920 x 1080 monitor beside a 2560 x 1440 one: the virtual screen has a black strip under the smaller one
        _capture.Setup(c => c.GetVirtualScreenBounds()).Returns((0, 0, 4480, 1440));
        _monitors.Setup(m => m.GetAllMonitors()).Returns(TestDataFactory.CreateDualMonitorSetup());

        var metadata = CreateSut().CaptureMonitor(-1, Defaults).Metadata;

        metadata.Warnings.Should().ContainSingle(w => w.StartsWith(CaptureWarnings.RegionPartlyOffMonitors + ":"));
    }

    [Theory]
    [InlineData(16385, 1080)]
    [InlineData(9000, 8000)]
    public void CaptureMonitor_MinusOne_OverTheBudget_FailsBeforeCapturing(int width, int height)
    {
        _capture.Setup(c => c.GetVirtualScreenBounds()).Returns((0, 0, width, height));

        var metadata = CreateSut().CaptureMonitor(-1, Defaults).Metadata;

        metadata.Success.Should().BeFalse();
        metadata.ErrorCode.Should().Be(nameof(ScreenshotErrorCode.CaptureFailed));
        metadata.Error.Should().Contain("one monitor at a time");
        _capture.Verify(c => c.CaptureScreenRegion(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<int>()), Times.Never);
    }

    [Fact]
    public void CaptureMonitor_OtherNegativeIndex_IsInvalidMonitorIndex()
    {
        CreateSut().CaptureMonitor(-2, Defaults).Metadata.ErrorCode.Should().Be(nameof(ScreenshotErrorCode.InvalidMonitorIndex));
    }
}
