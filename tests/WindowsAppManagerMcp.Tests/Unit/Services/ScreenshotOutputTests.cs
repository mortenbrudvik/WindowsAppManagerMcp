namespace WindowsAppManagerMcp.Tests.Unit.Services;

/// <summary>
/// The output modes (image, file, both, metadata, and the legacy saveToFile alias) and the file
/// safety rules: absolute paths only, an extension that matches the format, no folder creation and
/// no silent overwrite. Files go to a temporary folder that is deleted afterwards.
/// </summary>
public sealed class ScreenshotOutputTests : IDisposable
{
    private readonly Mock<IScreenCaptureWrapper> _mockCaptureWrapper;
    private readonly Mock<IMonitorService> _mockMonitorService;
    private readonly string _folder;
    private readonly List<string> _defaultFolderFiles = [];

    public ScreenshotOutputTests()
    {
        _mockCaptureWrapper = TestDataFactory.CreateMockScreenCaptureWrapper();
        _mockMonitorService = TestDataFactory.CreateMockMonitorService();
        _folder = Path.Combine(Path.GetTempPath(), "wam-screenshot-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_folder);
    }

    public void Dispose()
    {
        Directory.Delete(_folder, recursive: true);
        foreach (var file in _defaultFolderFiles.Where(File.Exists))
        {
            File.Delete(file);
        }
    }

    private ScreenshotService CreateSut() =>
        new(_mockCaptureWrapper.Object, _mockMonitorService.Object, Mock.Of<INativeWindowWrapper>(w => w.IsWindowVisible(It.IsAny<nint>()) == true));

    #region Output modes

    [Fact]
    public void Output_Image_ReturnsTheImageAndWritesNoFile()
    {
        // Arrange
        var sut = CreateSut();

        // Act
        var capture = sut.CaptureRegion(0, 0, 200, 100, new CaptureOptions { Output = CaptureOutputMode.Image });

        // Assert
        capture.Image.Should().NotBeNull();
        capture.Metadata.FilePath.Should().BeNull();
        Directory.EnumerateFileSystemEntries(_folder).Should().BeEmpty();
    }

    [Fact]
    public void Output_Metadata_ReturnsNoImageButReportsItsSize()
    {
        // Arrange
        var sut = CreateSut();

        // Act
        var capture = sut.CaptureRegion(0, 0, 200, 100, new CaptureOptions { Output = CaptureOutputMode.Metadata });

        // Assert
        capture.Metadata.Success.Should().BeTrue();
        capture.Image.Should().BeNull();
        capture.Metadata.FilePath.Should().BeNull();
        capture.Metadata.ByteSize.Should().BePositive();
        capture.Metadata.ImageToScreen.Should().NotBeNull();
    }

    [Fact]
    public void Output_FileWithoutPath_WritesANewFileInTheCapturesFolder()
    {
        // Arrange
        var sut = CreateSut();

        // Act
        var capture = sut.CaptureRegion(0, 0, 200, 100, new CaptureOptions { Output = CaptureOutputMode.File });
        TrackDefaultFile(capture.Metadata.FilePath);

        // Assert
        capture.Image.Should().BeNull();
        var path = capture.Metadata.FilePath!;
        Path.GetDirectoryName(path).Should().Be(ScreenshotService.DefaultCaptureFolder);
        Path.GetFileName(path).Should().StartWith("screenshot_").And.EndWith(".png");
        File.ReadAllBytes(path).Length.Should().Be((int)capture.Metadata.ByteSize!.Value);
    }

    [Fact]
    public void Output_FileWithoutPath_UsesTheJpgExtensionForJpeg()
    {
        // Arrange
        var sut = CreateSut();

        // Act
        var capture = sut.CaptureMonitor(0, new CaptureOptions { Output = CaptureOutputMode.File });
        TrackDefaultFile(capture.Metadata.FilePath);

        // Assert
        capture.Metadata.ImageFormat.Should().Be("jpeg");
        capture.Metadata.FilePath.Should().EndWith(".jpg");
    }

    [Fact]
    public void Output_BothWithPath_ReturnsTheImageAndWritesTheSameBytes()
    {
        // Arrange
        var path = Path.Combine(_folder, "shot.png");
        var sut = CreateSut();

        // Act
        var capture = sut.CaptureRegion(0, 0, 200, 100, new CaptureOptions { Output = CaptureOutputMode.Both, OutputPath = path });

        // Assert
        capture.Image.Should().NotBeNull();
        capture.Metadata.FilePath.Should().Be(path);
        File.ReadAllBytes(path).Should().Equal(capture.Image!.Data);
    }

    [Fact]
    public void SaveToFile_IsAnAliasForOutputFile()
    {
        // Arrange
        var path = Path.Combine(_folder, "legacy.png");
        var sut = CreateSut();

        // Act
        var capture = sut.CaptureRegion(0, 0, 200, 100, new CaptureOptions { SaveToFile = true, OutputPath = path });

        // Assert
        capture.Image.Should().BeNull();
        capture.Metadata.FilePath.Should().Be(path);
        File.Exists(path).Should().BeTrue();
    }

    [Fact]
    public void SaveToFile_WithOutputBoth_KeepsTheImage()
    {
        // Arrange
        var path = Path.Combine(_folder, "both.png");
        var sut = CreateSut();

        // Act
        var capture = sut.CaptureRegion(0, 0, 200, 100,
            new CaptureOptions { SaveToFile = true, Output = CaptureOutputMode.Both, OutputPath = path });

        // Assert
        capture.Image.Should().NotBeNull();
        File.Exists(path).Should().BeTrue();
    }

    [Fact]
    public void Output_NoToolCanBind_ReturnsInvalidOutputMode()
    {
        // Arrange - the tools refuse an unknown name when they bind it
        var sut = CreateSut();

        // Act
        var metadata = sut.CaptureRegion(0, 0, 200, 100, new CaptureOptions { Output = (CaptureOutputMode)42 }).Metadata;

        // Assert
        metadata.ErrorCode.Should().Be("InvalidOutputMode");
        metadata.Error.Should().Contain("42");
    }

    [Fact]
    public void OutputPath_WithImageOutput_IsRejectedInsteadOfIgnored()
    {
        // Arrange - the agent would otherwise believe the file was written
        var path = Path.Combine(_folder, "ignored.png");
        var sut = CreateSut();

        // Act
        var metadata = sut.CaptureRegion(0, 0, 200, 100, new CaptureOptions { OutputPath = path }).Metadata;

        // Assert
        metadata.ErrorCode.Should().Be("InvalidOutputMode");
        File.Exists(path).Should().BeFalse();
        VerifyNothingCaptured();
    }

    #endregion

    #region File safety

    [Theory]
    [InlineData(@"shots\a.png")]
    [InlineData("a.png")]
    [InlineData(@"\shots\a.png")]
    [InlineData(@"C:a.png")]
    public void OutputPath_ThatIsNotAbsolute_ReturnsInvalidOutputPath(string path)
    {
        // Arrange
        var sut = CreateSut();

        // Act
        var metadata = sut.CaptureRegion(0, 0, 200, 100, new CaptureOptions { Output = CaptureOutputMode.File, OutputPath = path }).Metadata;

        // Assert
        metadata.ErrorCode.Should().Be("InvalidOutputPath");
        metadata.Error.Should().Contain("absolute");
        VerifyNothingCaptured();
    }

    [Theory]
    [InlineData("thesis.docx")]
    [InlineData("shot.bmp")]
    [InlineData("shot")]
    public void OutputPath_WithANonImageExtension_ReturnsInvalidOutputPath(string fileName)
    {
        // Arrange
        var path = Path.Combine(_folder, fileName);
        var sut = CreateSut();

        // Act
        var metadata = sut.CaptureRegion(0, 0, 200, 100, new CaptureOptions { Output = CaptureOutputMode.File, OutputPath = path }).Metadata;

        // Assert
        metadata.ErrorCode.Should().Be("InvalidOutputPath");
        File.Exists(path).Should().BeFalse();
    }

    [Fact]
    public void OutputPath_ThatDoesNotMatchTheFormat_ReturnsInvalidOutputPath()
    {
        // Arrange - JPEG bytes in a .png file would mislead whoever opens it
        var path = Path.Combine(_folder, "shot.png");
        var sut = CreateSut();

        // Act
        var metadata = sut.CaptureRegion(0, 0, 200, 100,
            new CaptureOptions { Format = CaptureFormat.Jpeg, Output = CaptureOutputMode.File, OutputPath = path }).Metadata;

        // Assert
        metadata.ErrorCode.Should().Be("InvalidOutputPath");
        metadata.Error.Should().Contain(".png").And.Contain("jpeg");
        File.Exists(path).Should().BeFalse();
    }

    [Theory]
    [InlineData("big.png", "png")]
    [InlineData("big.jpg", "jpeg")]
    [InlineData("big.JPEG", "jpeg")]
    public void OutputPath_WithAutoFormat_FollowsTheExtension(string fileName, string expectedFormat)
    {
        // Arrange - a monitor capture, which would be JPEG by default
        var path = Path.Combine(_folder, fileName);
        var sut = CreateSut();

        // Act
        var metadata = sut.CaptureMonitor(0, new CaptureOptions { Output = CaptureOutputMode.File, OutputPath = path }).Metadata;

        // Assert
        metadata.Success.Should().BeTrue();
        metadata.ImageFormat.Should().Be(expectedFormat);
        File.ReadAllBytes(path).Take(2).Should().Equal(expectedFormat == "png" ? new byte[] { 0x89, 0x50 } : new byte[] { 0xFF, 0xD8 });
    }

    [Fact]
    public void OutputPath_InAMissingFolder_IsRejectedAndTheFolderIsNotCreated()
    {
        // Arrange
        var missing = Path.Combine(_folder, "missing", "deeper");
        var path = Path.Combine(missing, "shot.png");
        var sut = CreateSut();

        // Act
        var metadata = sut.CaptureRegion(0, 0, 200, 100, new CaptureOptions { Output = CaptureOutputMode.File, OutputPath = path }).Metadata;

        // Assert
        metadata.ErrorCode.Should().Be("InvalidOutputPath");
        metadata.Error.Should().Contain("does not exist");
        Directory.Exists(Path.Combine(_folder, "missing")).Should().BeFalse();
    }

    [Fact]
    public void OutputPath_ThatExists_IsNotOverwrittenWithoutOverwrite()
    {
        // Arrange
        var path = Path.Combine(_folder, "existing.png");
        File.WriteAllText(path, "keep me");
        var sut = CreateSut();

        // Act
        var metadata = sut.CaptureRegion(0, 0, 200, 100, new CaptureOptions { Output = CaptureOutputMode.File, OutputPath = path }).Metadata;

        // Assert
        metadata.Success.Should().BeFalse();
        metadata.ErrorCode.Should().Be("FileExists");
        metadata.Error.Should().Contain("overwrite");
        File.ReadAllText(path).Should().Be("keep me");
        VerifyNothingCaptured();
    }

    [Fact]
    public void OutputPath_ThatExists_IsReplacedWithOverwrite()
    {
        // Arrange
        var path = Path.Combine(_folder, "existing.png");
        File.WriteAllText(path, "replace me");
        var sut = CreateSut();

        // Act
        var metadata = sut.CaptureRegion(0, 0, 200, 100,
            new CaptureOptions { Output = CaptureOutputMode.File, OutputPath = path, Overwrite = true }).Metadata;

        // Assert
        metadata.Success.Should().BeTrue();
        File.ReadAllBytes(path).Take(2).Should().Equal(0x89, 0x50);
    }

    [Fact]
    public void OutputPath_ThatIsAFolder_ReturnsInvalidOutputPath()
    {
        // Arrange
        var folder = Path.Combine(_folder, "looks-like.png");
        Directory.CreateDirectory(folder);
        var sut = CreateSut();

        // Act
        var metadata = sut.CaptureRegion(0, 0, 200, 100,
            new CaptureOptions { Output = CaptureOutputMode.File, OutputPath = folder, Overwrite = true }).Metadata;

        // Assert
        metadata.ErrorCode.Should().Be("InvalidOutputPath");
    }

    [Fact]
    public void OutputPath_IsReturnedNormalized()
    {
        // Arrange
        Directory.CreateDirectory(Path.Combine(_folder, "sub"));
        var path = Path.Combine(_folder, "sub", "..", "shot.png");
        var sut = CreateSut();

        // Act
        var metadata = sut.CaptureRegion(0, 0, 200, 100, new CaptureOptions { Output = CaptureOutputMode.File, OutputPath = path }).Metadata;

        // Assert
        metadata.FilePath.Should().Be(Path.Combine(_folder, "shot.png"));
        File.Exists(metadata.FilePath).Should().BeTrue();
    }

    [Fact]
    public void OutputPath_NamingAnAlternateDataStream_ReturnsInvalidOutputPath()
    {
        // Arrange
        var host = Path.Combine(_folder, "notes.txt");
        File.WriteAllText(host, "notes");
        var sut = CreateSut();

        // Act
        var metadata = sut.CaptureRegion(0, 0, 200, 100,
            new CaptureOptions { Output = CaptureOutputMode.File, OutputPath = host + ":hidden.png" }).Metadata;

        // Assert
        metadata.ErrorCode.Should().Be("InvalidOutputPath");
        File.ReadAllText(host).Should().Be("notes");
    }

    #endregion

    private void TrackDefaultFile(string? path)
    {
        if (path != null)
        {
            _defaultFolderFiles.Add(path);
        }
    }

    private void VerifyNothingCaptured()
    {
        _mockCaptureWrapper.Verify(
            c => c.CaptureScreenRegion(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<int>()),
            Times.Never);
        _mockCaptureWrapper.Verify(
            c => c.CaptureWindow(It.IsAny<nint>(), It.IsAny<bool>()),
            Times.Never);
    }
}
