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
    [InlineData(CaptureOutputMode.Image, false, CaptureOutputMode.Image)]
    [InlineData(CaptureOutputMode.File, false, CaptureOutputMode.File)]
    [InlineData(CaptureOutputMode.Both, false, CaptureOutputMode.Both)]
    [InlineData(CaptureOutputMode.Metadata, false, CaptureOutputMode.Metadata)]
    [InlineData(CaptureOutputMode.Image, true, CaptureOutputMode.File)]
    [InlineData(CaptureOutputMode.Metadata, true, CaptureOutputMode.File)]
    [InlineData(CaptureOutputMode.File, true, CaptureOutputMode.File)]
    [InlineData(CaptureOutputMode.Both, true, CaptureOutputMode.Both)]
    public void Resolve_OutputAndTheSaveToFileAlias(CaptureOutputMode output, bool saveToFile, CaptureOutputMode expected)
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

    [Theory]
    [InlineData(1, 5000, 1, 1568)]
    [InlineData(4000, 1, 1568, 1)]
    public void OutputSize_ThatWouldRoundAnEdgeToZero_KeepsOnePixel(int width, int height, int expectedWidth, int expectedHeight)
    {
        // Arrange - 4000 x 1 scaled to a 1568 long edge would be 0.39 pixels high; the caller only
        // asked for the defaults, so a thin strip (a ruler, a sampled line) must not fail
        var request = CaptureRequest.Resolve(new CaptureOptions());

        // Act & Assert - the transform scales each axis on its own, so points still map exactly
        request.OutputSize(width, height).Should().Be((expectedWidth, expectedHeight));
    }

    [Theory]
    [InlineData(@"\\server\share\shot.png")]
    [InlineData("//server/share/shot.png")]
    [InlineData(@"\\?\C:\captures\shot.png")]
    [InlineData(@"\\.\C:\captures\shot.png")]
    [InlineData(@"\\?\UNC\server\share\shot.png")]
    [InlineData(@"\??\C:\captures\shot.png")]
    public void Resolve_NetworkOrDeviceOutputPath_IsRejectedBeforeTheFileSystemIsTouched(string outputPath)
    {
        // A capture written to \\attacker\share would leak the screen and the user's NTLM hash
        var failure = CaptureRequest.Validate(new CaptureOptions { Output = CaptureOutputMode.File, OutputPath = outputPath });

        failure.Should().NotBeNull();
        failure!.Code.Should().Be(ScreenshotErrorCode.InvalidOutputPath);
        failure.Message.Should().Contain("drive letter");
    }

    [Theory]
    [InlineData("C:/captures/shot.png")]
    [InlineData(@"c:\captures\shot.png")]
    public void Resolve_DriveLetterOutputPath_PassesTheNetworkCheck(string outputPath)
    {
        // The folder does not exist, so it fails later, on the folder check, not as a network path
        var failure = CaptureRequest.Validate(new CaptureOptions { Output = CaptureOutputMode.File, OutputPath = outputPath.Replace("captures", $"no-such-folder-{Guid.NewGuid():N}") });

        failure!.Code.Should().Be(ScreenshotErrorCode.InvalidOutputPath);
        failure.Message.Should().NotContain("drive letter").And.Contain("does not exist");
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
        CaptureRequest.Resolve(new CaptureOptions { Format = CaptureFormat.Png }).EncodingFor(4000, 3000).Should().Be(ImageEncoding.Png);
        CaptureRequest.Resolve(new CaptureOptions { Format = CaptureFormat.Jpeg }).EncodingFor(10, 10).Should().Be(ImageEncoding.Jpeg);
    }

    [Theory]
    [InlineData(CaptureFormat.Auto, null)]
    [InlineData(CaptureFormat.Png, ImageEncoding.Png)]
    [InlineData(CaptureFormat.Jpeg, ImageEncoding.Jpeg)]
    public void Resolve_TakesTheToolsFormat(CaptureFormat format, ImageEncoding? expected)
    {
        // The tools bind format to CaptureFormat ('jpg' and any case included), so the service takes the enum as it is
        CaptureRequest.Resolve(new CaptureOptions { Format = format }).Format.Should().Be(expected);
    }

    [Fact]
    public void Validate_RejectsAFormatOrOutputNoToolCanBind()
    {
        // Fail closed: a value outside the enums is refused, with the codes the tools use for an unknown name
        var format = CaptureRequest.Validate(new CaptureOptions { Format = (CaptureFormat)42 })!;
        format.Code.Should().Be(ScreenshotErrorCode.InvalidFormat);
        format.Message.Should().Contain("'auto', 'png' or 'jpeg'");

        var output = CaptureRequest.Validate(new CaptureOptions { Output = (CaptureOutputMode)42 })!;
        output.Code.Should().Be(ScreenshotErrorCode.InvalidOutputMode);
        output.Message.Should().Contain("'image', 'file', 'both' or 'metadata'");
    }

    [Fact]
    public void Validate_ReturnsNullForValidOptions()
    {
        CaptureRequest.Validate(new CaptureOptions { Format = CaptureFormat.Jpeg, Output = CaptureOutputMode.Both, MaxDimension = 0 }).Should().BeNull();
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
