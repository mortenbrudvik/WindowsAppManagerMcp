namespace WindowsAppManagerMcp.Tests.Integration;

/// <summary>
/// Integration tests for ScreenCaptureWrapper - the native GDI capture layer.
/// Tests T9.1 from BACKLOG-TECHNICAL.md.
///
/// These are integration tests that run against the actual Windows display system.
/// Validation logic tests (dimensions, handle) are deterministic.
/// Screen capture tests depend on having a display.
/// </summary>
public class ScreenCaptureWrapperIntegrationTests
{
    private readonly ScreenCaptureWrapper _sut;

    public ScreenCaptureWrapperIntegrationTests()
    {
        _sut = new ScreenCaptureWrapper();
    }

    #region T9.1.1: CaptureScreenRegion_WithValidRegion_ReturnsPixelData

    [Fact]
    public void CaptureScreenRegion_WithValidRegion_ReturnsPixelData()
    {
        // Arrange
        const int width = 100;
        const int height = 100;

        // Act
        var result = _sut.CaptureScreenRegion(0, 0, width, height);

        // Assert
        result.Should().NotBeNull();
        result.Bgra.Should().NotBeEmpty();
        result.Method.Should().Be("ScreenBitBlt");
    }

    #endregion

    #region T9.1.2: CaptureScreenRegion_ReturnsCorrectDataSize

    [Fact]
    public void CaptureScreenRegion_ReturnsCorrectDataSize()
    {
        // Arrange
        const int width = 100;
        const int height = 100;
        const int expectedSize = width * height * 4; // BGRA = 4 bytes per pixel

        // Act
        var result = _sut.CaptureScreenRegion(0, 0, width, height);

        // Assert
        result.Bgra.Length.Should().Be(expectedSize);
        result.Width.Should().Be(width);
        result.Height.Should().Be(height);
    }

    // Reads real screen pixels (no input); new desktop reads are opt-in, like every other desktop test.
    [DesktopFact]
    public void CaptureScreenRegion_ReportsTheRegionOriginAsScreenOrigin()
    {
        // Act
        var result = _sut.CaptureScreenRegion(3, 7, 10, 10);

        // Assert
        result.ScreenOriginX.Should().Be(3);
        result.ScreenOriginY.Should().Be(7);
    }

    #endregion

    #region T9.1.3: CaptureScreenRegion_WithZeroWidth_ThrowsArgumentException

    [Fact]
    public void CaptureScreenRegion_WithZeroWidth_ThrowsArgumentException()
    {
        // Act
        var act = () => _sut.CaptureScreenRegion(0, 0, 0, 100);

        // Assert
        act.Should().Throw<ArgumentException>()
            .WithMessage("*Width and height must be positive*");
    }

    #endregion

    #region T9.1.4: CaptureScreenRegion_WithNegativeHeight_ThrowsArgumentException

    [Fact]
    public void CaptureScreenRegion_WithNegativeHeight_ThrowsArgumentException()
    {
        // Act
        var act = () => _sut.CaptureScreenRegion(0, 0, 100, -1);

        // Assert
        act.Should().Throw<ArgumentException>()
            .WithMessage("*Width and height must be positive*");
    }

    #endregion

    #region T9.1.5: CaptureScreenRegion_WithOversizedDimensions_ThrowsArgumentException

    [Theory]
    [InlineData(16385, 100)]
    [InlineData(100, 16385)]
    [InlineData(16385, 16385)]
    public void CaptureScreenRegion_WithOversizedDimensions_ThrowsArgumentException(int width, int height)
    {
        // Act
        var act = () => _sut.CaptureScreenRegion(0, 0, width, height);

        // Assert
        act.Should().Throw<ArgumentException>()
            .WithMessage("*Capture dimensions exceed maximum*16384*");
    }

    #endregion

    #region T9.1.6: CaptureWindow_WithZeroHandle_ThrowsArgumentException

    [Fact]
    public void CaptureWindow_WithZeroHandle_ThrowsArgumentException()
    {
        // Act
        var act = () => _sut.CaptureWindow(nint.Zero, includeFrame: false);

        // Assert
        act.Should().Throw<ArgumentException>()
            .WithMessage("*Invalid window handle*");
    }

    #endregion

    #region T9.1.7: CaptureWindow_WithInvalidHandle_ThrowsArgumentException

    [Fact]
    public void CaptureWindow_WithInvalidHandle_ThrowsArgumentException()
    {
        // Arrange - use an obviously invalid handle
        nint invalidHandle = new(0x12345678);

        // Act
        var act = () => _sut.CaptureWindow(invalidHandle, includeFrame: false);

        // Assert
        act.Should().Throw<ArgumentException>()
            .WithMessage("*Invalid window handle*");
    }

    #endregion

    #region T9.1.8: GetVirtualScreenBounds_ReturnsValidBounds

    [Fact]
    public void GetVirtualScreenBounds_ReturnsValidBounds()
    {
        // Act
        var (x, y, width, height) = _sut.GetVirtualScreenBounds();

        // Assert - width and height must be positive
        width.Should().BeGreaterThan(0);
        height.Should().BeGreaterThan(0);
    }

    #endregion

    #region T9.1.9: GetVirtualScreenBounds_DimensionsAreReasonable

    [Fact]
    public void GetVirtualScreenBounds_DimensionsAreReasonable()
    {
        // Act
        var (x, y, width, height) = _sut.GetVirtualScreenBounds();

        // Assert - sanity check: minimum 640x480, maximum 32000x32000
        // This accounts for very small displays to very large multi-monitor setups
        width.Should().BeGreaterThanOrEqualTo(640);
        height.Should().BeGreaterThanOrEqualTo(480);
        width.Should().BeLessThanOrEqualTo(32000);
        height.Should().BeLessThanOrEqualTo(32000);
    }

    #endregion

    #region T9.1.10: IsValidWindow_WithZeroHandle_ReturnsFalse

    [Fact]
    public void IsValidWindow_WithZeroHandle_ReturnsFalse()
    {
        // Act
        var result = _sut.IsValidWindow(nint.Zero);

        // Assert
        result.Should().BeFalse();
    }

    #endregion

    #region T9.1.11: IsValidWindow_WithInvalidHandle_ReturnsFalse

    [Fact]
    public void IsValidWindow_WithInvalidHandle_ReturnsFalse()
    {
        // Arrange - use an obviously invalid handle
        nint invalidHandle = new(0x12345678);

        // Act
        var result = _sut.IsValidWindow(invalidHandle);

        // Assert
        result.Should().BeFalse();
    }

    #endregion

    #region WithPixels (roadmap M6: the capture members without the copy)

    [Theory]
    [InlineData(0, 100)]
    [InlineData(100, -1)]
    [InlineData(20000, 100)]
    [InlineData(9000, 9000)]
    public void WithPixels_OfARegionTheCaptureRefuses_IsRefusedBeforeAnythingIsRead(int width, int height)
    {
        // Arrange
        var read = false;

        // Act
        var act = () => _sut.WithPixels(new PixelSource.Screen(0, 0, width, height), _ => read = true);

        // Assert
        act.Should().Throw<ArgumentException>();
        read.Should().BeFalse();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void WithPixels_OfAHandleThatIsNoWindow_IsRefusedAsCaptureWindowIs(bool fromScreen)
    {
        // Arrange
        PixelSource source = fromScreen
            ? new PixelSource.WindowFromScreen(new nint(0x12345678), IncludeFrame: true)
            : new PixelSource.Window(new nint(0x12345678), IncludeFrame: true);

        // Act
        var act = () => _sut.WithPixels(source, pixels => pixels.Width);

        // Assert
        act.Should().Throw<ArgumentException>().WithMessage("*Invalid window handle*");
    }

    // Reads real screen pixels (no input); new desktop reads are opt-in, like every other desktop test.
    [DesktopFact]
    public void WithPixels_OfAScreenRegion_LendsWhatCaptureScreenRegionCopies()
    {
        // Act
        var lent = _sut.WithPixels(new PixelSource.Screen(3, 7, 40, 30),
            pixels => (pixels.Width, pixels.Height, pixels.Stride, pixels.ScreenOriginX, pixels.ScreenOriginY, pixels.Method, pixels.Bgra.Length));
        var copied = _sut.CaptureScreenRegion(3, 7, 40, 30);

        // Assert
        lent.Should().Be((40, 30, 160, 3, 7, "ScreenBitBlt", 40 * 30 * 4));
        (copied.Width, copied.Height, copied.ScreenOriginX, copied.ScreenOriginY, copied.Method, copied.Bgra.Length)
            .Should().Be((lent.Width, lent.Height, lent.ScreenOriginX, lent.ScreenOriginY, lent.Method, lent.Length));
    }

    #endregion
}
