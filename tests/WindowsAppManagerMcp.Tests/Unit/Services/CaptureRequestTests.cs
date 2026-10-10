namespace WindowsAppManagerMcp.Tests.Unit.Services;

/// <summary>
/// How CaptureRequest resolves the capture tools' options: defaults, legacy aliases and rejections.
/// Pure: only the output-path checks touch the file system, in a temporary folder.
/// </summary>
public class CaptureRequestTests
{
    [Fact]
    public void Resolve_Defaults_AreAutoFormatQuality80Image1568()
    {
        // Act
        var request = CaptureRequest.Resolve(new CaptureOptions());

        // Assert
        request.Should().Be(new CaptureRequest(
            Format: null,
            Quality: 80,
            MaxDimension: 1568,
            MaxWidth: null,
            Output: CaptureOutputMode.Image,
            OutputPath: null,
            Overwrite: false));
        request.ReturnsImage.Should().BeTrue();
        request.WritesFile.Should().BeFalse();
    }

    [Theory]
    [InlineData(null, null, 1568, null)]
    [InlineData(0, null, null, null)]
    [InlineData(1000, null, 1000, null)]
    [InlineData(null, 1280, null, 1280)]
    [InlineData(1000, 1280, 1000, 1280)]
    [InlineData(0, 1280, null, 1280)]
    public void Resolve_SizeLimits(int? maxDimension, int? maxWidth, int? expectedMaxDimension, int? expectedMaxWidth)
    {
        // Act
        var request = CaptureRequest.Resolve(new CaptureOptions { MaxDimension = maxDimension, MaxWidth = maxWidth });

        // Assert
        request.MaxDimension.Should().Be(expectedMaxDimension);
        request.MaxWidth.Should().Be(expectedMaxWidth);
    }

    [Theory]
    [InlineData("image", false, CaptureOutputMode.Image)]
    [InlineData(null, false, CaptureOutputMode.Image)]
    [InlineData("FILE", false, CaptureOutputMode.File)]
    [InlineData("both", false, CaptureOutputMode.Both)]
    [InlineData("metadata", false, CaptureOutputMode.Metadata)]
    [InlineData("image", true, CaptureOutputMode.File)]
    [InlineData("metadata", true, CaptureOutputMode.File)]
    [InlineData("file", true, CaptureOutputMode.File)]
    [InlineData("both", true, CaptureOutputMode.Both)]
    public void Resolve_OutputAndTheSaveToFileAlias(string? output, bool saveToFile, CaptureOutputMode expected)
    {
        // Act
        var request = CaptureRequest.Resolve(new CaptureOptions { Output = output, SaveToFile = saveToFile });

        // Assert
        request.Output.Should().Be(expected);
    }

    [Theory]
    [InlineData(1568, 882, 1568, 882)]
    [InlineData(3840, 2160, 1568, 882)]
    [InlineData(2160, 3840, 882, 1568)]
    [InlineData(4, 5000, 1, 1568)]
    public void OutputSize_KeepsTheAspectRatioWithinTheLongEdge(int width, int height, int expectedWidth, int expectedHeight)
    {
        CaptureRequest.Resolve(new CaptureOptions()).OutputSize(width, height).Should().Be((expectedWidth, expectedHeight));
    }

    [Fact]
    public void OutputSize_ThatWouldRoundAnEdgeToZero_ThrowsInvalidImageSize()
    {
        // Arrange - 1 x 5000 scaled to a 1568 long edge is 0.31 pixels wide
        var request = CaptureRequest.Resolve(new CaptureOptions());

        // Act
        var act = () => request.OutputSize(1, 5000);

        // Assert
        act.Should().Throw<CaptureException>().Which.Code.Should().Be(ScreenshotErrorCode.InvalidImageSize);
    }

    [Theory]
    [InlineData(100, 100, ImageEncoding.Png)]
    [InlineData(512, 512, ImageEncoding.Png)]
    [InlineData(513, 10, ImageEncoding.Jpeg)]
    [InlineData(1568, 882, ImageEncoding.Jpeg)]
    public void EncodingFor_Auto_DependsOnTheOutputLongEdge(int width, int height, ImageEncoding expected)
    {
        CaptureRequest.Resolve(new CaptureOptions()).EncodingFor(width, height).Should().Be(expected);
    }

    [Fact]
    public void EncodingFor_ExplicitFormat_IgnoresTheSize()
    {
        CaptureRequest.Resolve(new CaptureOptions { Format = "png" }).EncodingFor(4000, 3000).Should().Be(ImageEncoding.Png);
        CaptureRequest.Resolve(new CaptureOptions { Format = "jpeg" }).EncodingFor(10, 10).Should().Be(ImageEncoding.Jpeg);
    }

    [Theory]
    [InlineData("webp", ScreenshotErrorCode.InvalidFormat)]
    [InlineData("tiff", ScreenshotErrorCode.InvalidFormat)]
    public void Validate_RejectsUnknownFormats(string format, ScreenshotErrorCode expected)
    {
        CaptureRequest.Validate(new CaptureOptions { Format = format })!.Code.Should().Be(expected);
    }

    [Fact]
    public void Validate_ReturnsNullForValidOptions()
    {
        CaptureRequest.Validate(new CaptureOptions { Format = "jpg", Output = "both", MaxDimension = 0 }).Should().BeNull();
    }

    [Theory]
    [InlineData(-1, null)]
    [InlineData(null, 0)]
    [InlineData(null, -10)]
    public void Validate_RejectsInvalidSizeLimits(int? maxDimension, int? maxWidth)
    {
        CaptureRequest.Validate(new CaptureOptions { MaxDimension = maxDimension, MaxWidth = maxWidth })!
            .Code.Should().Be(ScreenshotErrorCode.InvalidImageSize);
    }
}
