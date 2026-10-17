namespace WindowsAppManagerMcp.Tests.Unit.Services;

/// <summary>
/// Windows.Graphics.Capture in window captures (roadmap M7.1): auto falls back to it when PrintWindow's image is one solid
/// color and the compositor's is not, and when the window does not answer; printwindow never uses it; wgc only uses it and
/// fails with the reason; screen copies the screen. The capture layers are mocks, so nothing is captured.
/// </summary>
public class ScreenshotGraphicsCaptureTests
{
    private static readonly CaptureOptions FullResolution = new() { MaxDimension = 0, Format = "png" };
    private static readonly nint Window = new(0x5000);

    private readonly Mock<IScreenCaptureWrapper> _capture = TestDataFactory.CreateMockScreenCaptureWrapper(captureWidth: 100, captureHeight: 80);
    private readonly Mock<IGraphicsCaptureWrapper> _graphics = new();
    private readonly Mock<IMonitorService> _monitors = TestDataFactory.CreateMockMonitorService();
    private readonly Mock<INativeWindowWrapper> _windows = new();
    private readonly Mock<IWindowCallRunner> _calls = new();

    public ScreenshotGraphicsCaptureTests()
    {
        _windows.Setup(w => w.IsWindowVisible(It.IsAny<nint>())).Returns(true);
        _windows.Setup(w => w.IsWindow(It.IsAny<nint>())).Returns(true);
        _calls.Setup(c => c.Run(It.IsAny<nint>(), It.IsAny<Func<CaptureResult>>()))
            .Returns((nint _, Func<CaptureResult> call) => call());
        _graphics.Setup(g => g.IsSupported).Returns(true);
        _graphics.Setup(g => g.CaptureWindow(Window, It.IsAny<bool>()))
            .Returns(new CaptureResult(Pattern(100, 80), 100, 80, 300, 200, IGraphicsCaptureWrapper.Method));
    }

    private ScreenshotService CreateSut(bool withGraphics = true) =>
        new(_capture.Object, _monitors.Object, _windows.Object, _calls.Object, graphicsCapture: withGraphics ? _graphics.Object : null);

    private static CaptureOptions With(WindowCaptureMethod method) => FullResolution with { CaptureMethod = method };

    /// <summary>Pixels that are not one color: a gradient.</summary>
    private static byte[] Pattern(int width, int height)
    {
        var pixels = new byte[width * height * 4];
        for (var i = 0; i < pixels.Length; i++)
        {
            pixels[i] = (byte)(i * 7 % 251);
        }

        return pixels;
    }

    private void PrintWindowReturns(byte[] pixels) =>
        _capture.Setup(c => c.CaptureWindow(Window, It.IsAny<bool>()))
            .Returns(new CaptureResult(pixels, 100, 80, 300, 200, "PrintWindow"));

    [Fact]
    public void Auto_APrintedImageOfOneColor_IsReplacedByTheCompositorsFrame()
    {
        // Arrange - PrintWindow returns black (the mock's pixels are all zero), the compositor has the real ones
        // Act
        var metadata = CreateSut().CaptureWindow(Window, includeFrame: false, FullResolution).Metadata;

        // Assert
        metadata.Success.Should().BeTrue();
        metadata.CaptureMethod.Should().Be(IGraphicsCaptureWrapper.Method);
        metadata.Warnings.Should().BeNullOrEmpty("the compositor's image is not blank");
        _graphics.Verify(g => g.CaptureWindow(Window, false), Times.Once);
    }

    [Fact]
    public void Auto_WhenTheCompositorsFrameIsBlankToo_KeepsThePrintedImage_WithTheBlankWarning()
    {
        // Arrange - a window that really is one color
        _graphics.Setup(g => g.CaptureWindow(Window, It.IsAny<bool>()))
            .Returns(new CaptureResult(new byte[100 * 80 * 4], 100, 80, 300, 200, IGraphicsCaptureWrapper.Method));

        // Act
        var metadata = CreateSut().CaptureWindow(Window, includeFrame: false, FullResolution).Metadata;

        // Assert
        metadata.CaptureMethod.Should().Be("PrintWindow");
        metadata.Warnings.Should().ContainSingle().Which.Should().StartWith("ImageBlank:");
    }

    [Fact]
    public void Auto_APrintedImageThatIsNotBlank_NeverAsksTheCompositor()
    {
        PrintWindowReturns(Pattern(100, 80));

        var metadata = CreateSut().CaptureWindow(Window, includeFrame: false, FullResolution).Metadata;

        metadata.CaptureMethod.Should().Be("PrintWindow");
        _graphics.Verify(g => g.CaptureWindow(It.IsAny<nint>(), It.IsAny<bool>()), Times.Never);
    }

    [Fact]
    public void Auto_WhenTheCompositorCannotCapture_KeepsThePrintedImage()
    {
        _graphics.Setup(g => g.CaptureWindow(Window, It.IsAny<bool>())).Throws(new GraphicsCaptureException("no frame"));

        var metadata = CreateSut().CaptureWindow(Window, includeFrame: false, FullResolution).Metadata;

        metadata.Success.Should().BeTrue();
        metadata.CaptureMethod.Should().Be("PrintWindow");
    }

    [Fact]
    public void Auto_AWindowThatDoesNotAnswer_IsTakenFromTheCompositor_NotFromTheScreen()
    {
        // Arrange - the compositor still has the window as it was last drawn, without the windows over it
        _calls.Setup(c => c.Run(Window, It.IsAny<Func<CaptureResult>>()))
            .Throws(new WindowNotRespondingException(Window, "not responding"));

        // Act
        var metadata = CreateSut().CaptureWindow(Window, includeFrame: true, FullResolution).Metadata;

        // Assert
        metadata.Success.Should().BeTrue();
        metadata.CaptureMethod.Should().Be(IGraphicsCaptureWrapper.Method);
        metadata.Warnings.Should().ContainSingle().Which.Should().StartWith("WindowNotResponding:").And.Contain("compositor");
        _capture.Verify(c => c.CaptureWindowFromScreen(It.IsAny<nint>(), It.IsAny<bool>()), Times.Never);
    }

    [Fact]
    public void Auto_WithoutWindowsGraphicsCapture_ADoesNotAnswerWindowIsCopiedFromTheScreen_AsBefore()
    {
        _calls.Setup(c => c.Run(Window, It.IsAny<Func<CaptureResult>>()))
            .Throws(new WindowNotRespondingException(Window, "not responding"));
        _capture.Setup(c => c.CaptureWindowFromScreen(Window, true))
            .Returns(new CaptureResult(Pattern(100, 80), 100, 80, 300, 200, "ScreenBitBlt"));
        _graphics.Setup(g => g.IsSupported).Returns(false);

        var metadata = CreateSut().CaptureWindow(Window, includeFrame: true, FullResolution).Metadata;

        metadata.CaptureMethod.Should().Be("ScreenBitBlt");
        metadata.Warnings.Should().ContainSingle().Which.Should().Contain("covers");
    }

    [Fact]
    public void PrintWindow_NeverAsksTheCompositor_EvenForABlankImage()
    {
        var metadata = CreateSut().CaptureWindow(Window, includeFrame: false, With(WindowCaptureMethod.PrintWindow)).Metadata;

        metadata.CaptureMethod.Should().Be("PrintWindow");
        metadata.Warnings.Should().ContainSingle().Which.Should().StartWith("ImageBlank:");
        _graphics.Verify(g => g.CaptureWindow(It.IsAny<nint>(), It.IsAny<bool>()), Times.Never);
    }

    [Fact]
    public void Wgc_UsesOnlyTheCompositor()
    {
        PrintWindowReturns(Pattern(100, 80));

        var metadata = CreateSut().CaptureWindow(Window, includeFrame: true, With(WindowCaptureMethod.Wgc)).Metadata;

        metadata.CaptureMethod.Should().Be(IGraphicsCaptureWrapper.Method);
        metadata.CapturedRegion.Should().Be(new CapturedRegion(300, 200, 100, 80));
        _capture.Verify(c => c.CaptureWindow(It.IsAny<nint>(), It.IsAny<bool>()), Times.Never);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Wgc_WhenItCannotCapture_IsCaptureFailed_WithTheReason(bool supported)
    {
        // Arrange
        _graphics.Setup(g => g.IsSupported).Returns(supported);
        _graphics.Setup(g => g.CaptureWindow(Window, It.IsAny<bool>())).Throws(new GraphicsCaptureException("the frame did not come"));

        // Act
        var metadata = CreateSut().CaptureWindow(Window, includeFrame: true, With(WindowCaptureMethod.Wgc)).Metadata;

        // Assert
        metadata.Success.Should().BeFalse();
        metadata.ErrorCode.Should().Be(nameof(ScreenshotErrorCode.CaptureFailed));
        metadata.Error.Should().Contain(supported ? "the frame did not come" : "not available").And.Contain("captureMethod wgc");
    }

    [Fact]
    public void Screen_CopiesTheScreenOverTheWindow()
    {
        _capture.Setup(c => c.CaptureWindowFromScreen(Window, false))
            .Returns(new CaptureResult(Pattern(100, 80), 100, 80, 300, 200, "ScreenBitBlt"));

        var metadata = CreateSut().CaptureWindow(Window, includeFrame: false, With(WindowCaptureMethod.Screen)).Metadata;

        metadata.CaptureMethod.Should().Be("ScreenBitBlt");
        _capture.Verify(c => c.CaptureWindow(It.IsAny<nint>(), It.IsAny<bool>()), Times.Never);
        _graphics.Verify(g => g.CaptureWindow(It.IsAny<nint>(), It.IsAny<bool>()), Times.Never);
    }

    [Fact]
    public void WithoutAGraphicsCaptureWrapper_AutoBehavesAsBefore()
    {
        var metadata = CreateSut(withGraphics: false).CaptureWindow(Window, includeFrame: false, FullResolution).Metadata;

        metadata.CaptureMethod.Should().Be("PrintWindow");
        metadata.Warnings.Should().ContainSingle().Which.Should().StartWith("ImageBlank:");
    }
}
