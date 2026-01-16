namespace WindowsAppManagerMcp.Tests.Unit.Services;

/// <summary>
/// Unit tests for BrowserReadingService.
/// Tests the service layer that coordinates browser URL and content extraction.
/// </summary>
public class BrowserReadingServiceTests
{
    private readonly Mock<IUIAutomationWrapper> _mockUIAutomation;
    private readonly Mock<INativeWindowWrapper> _mockNativeWrapper;
    private readonly Mock<IBrowserDetectionService> _mockBrowserDetection;
    private readonly Mock<ICdpService> _mockCdpService;

    public BrowserReadingServiceTests()
    {
        _mockUIAutomation = new Mock<IUIAutomationWrapper>();
        _mockNativeWrapper = new Mock<INativeWindowWrapper>();
        _mockBrowserDetection = new Mock<IBrowserDetectionService>();
        _mockCdpService = new Mock<ICdpService>();

        // Default setup: UI Automation is available
        _mockUIAutomation.Setup(u => u.IsAvailable()).Returns(true);
    }

    private BrowserReadingService CreateSut() => new(
        _mockUIAutomation.Object,
        _mockNativeWrapper.Object,
        _mockBrowserDetection.Object,
        _mockCdpService.Object);

    #region GetBrowserUrl Tests

    [Fact]
    public void GetBrowserUrl_ReturnsError_WhenHandleIsZero()
    {
        // Arrange
        var sut = CreateSut();

        // Act
        var result = sut.GetBrowserUrl(nint.Zero);

        // Assert
        result.Success.Should().BeFalse();
        result.ErrorCode.Should().Be(BrowserErrorCode.InvalidHandle);
        result.Error.Should().Contain("Invalid window handle");
    }

    [Fact]
    public void GetBrowserUrl_ReturnsError_WhenWindowIsInvalid()
    {
        // Arrange
        _mockNativeWrapper.Setup(n => n.IsWindow(It.IsAny<nint>())).Returns(false);
        var sut = CreateSut();

        // Act
        var result = sut.GetBrowserUrl(new nint(12345));

        // Assert
        result.Success.Should().BeFalse();
        result.ErrorCode.Should().Be(BrowserErrorCode.InvalidHandle);
    }

    [Fact]
    public void GetBrowserUrl_ReturnsError_WhenUIAutomationNotAvailable()
    {
        // Arrange
        var handle = new nint(12345);
        _mockNativeWrapper.Setup(n => n.IsWindow(handle)).Returns(true);
        _mockNativeWrapper.Setup(n => n.GetWindowThreadProcessId(handle)).Returns(1000u);
        _mockBrowserDetection.Setup(b => b.IsBrowser(It.IsAny<string>())).Returns(true);
        _mockUIAutomation.Setup(u => u.IsAvailable()).Returns(false);
        var sut = CreateSut();

        // Act
        var result = sut.GetBrowserUrl(handle);

        // Assert
        result.Success.Should().BeFalse();
        result.ErrorCode.Should().Be(BrowserErrorCode.AutomationUnavailable);
    }

    [Fact]
    public void GetBrowserUrl_ReturnsError_WhenNotABrowser()
    {
        // Arrange
        var handle = new nint(12345);
        _mockNativeWrapper.Setup(n => n.IsWindow(handle)).Returns(true);
        _mockNativeWrapper.Setup(n => n.GetWindowThreadProcessId(handle)).Returns(1000u);
        _mockBrowserDetection.Setup(b => b.IsBrowser(It.IsAny<string>())).Returns(false);
        var sut = CreateSut();

        // Act
        var result = sut.GetBrowserUrl(handle);

        // Assert
        result.Success.Should().BeFalse();
        result.ErrorCode.Should().Be(BrowserErrorCode.NotABrowser);
    }

    [Fact]
    public void GetBrowserUrl_ReturnsError_WhenUrlBarNotFound()
    {
        // Arrange
        var handle = new nint(12345);
        SetupValidBrowserWindow(handle, "chrome", "chrome");
        _mockUIAutomation.Setup(u => u.GetBrowserUrl(handle, "chrome")).Returns((string?)null);
        var sut = CreateSut();

        // Act
        var result = sut.GetBrowserUrl(handle);

        // Assert
        result.Success.Should().BeFalse();
        result.ErrorCode.Should().Be(BrowserErrorCode.UrlBarNotFound);
        result.BrowserType.Should().Be("chrome");
    }

    [Fact]
    public void GetBrowserUrl_ReturnsSuccess_WithValidUrl()
    {
        // Arrange
        var handle = new nint(12345);
        var expectedUrl = "https://example.com";
        var expectedTitle = "Example Page - Google Chrome";
        SetupValidBrowserWindow(handle, "chrome", "chrome");
        _mockUIAutomation.Setup(u => u.GetBrowserUrl(handle, "chrome")).Returns(expectedUrl);
        _mockUIAutomation.Setup(u => u.GetWindowTitle(handle)).Returns(expectedTitle);
        var sut = CreateSut();

        // Act
        var result = sut.GetBrowserUrl(handle);

        // Assert
        result.Success.Should().BeTrue();
        result.Url.Should().Be(expectedUrl);
        result.BrowserType.Should().Be("chrome");
        result.PageTitle.Should().Be(expectedTitle);
        result.ErrorCode.Should().Be(BrowserErrorCode.None);
    }

    // Note: Browser type detection is tested separately in GetBrowserType tests.
    // Full integration testing with actual browser windows requires real browsers.

    #endregion

    #region GetBrowserContent Tests

    [Fact]
    public void GetBrowserContent_ReturnsError_WhenHandleIsInvalid()
    {
        // Arrange
        var sut = CreateSut();

        // Act
        var result = sut.GetBrowserContent(nint.Zero);

        // Assert
        result.Success.Should().BeFalse();
        result.ErrorCode.Should().Be(BrowserErrorCode.InvalidHandle);
    }

    [Fact]
    public void GetBrowserContent_ReturnsError_WhenNotABrowser()
    {
        // Arrange
        var handle = new nint(12345);
        _mockNativeWrapper.Setup(n => n.IsWindow(handle)).Returns(true);
        _mockNativeWrapper.Setup(n => n.GetWindowThreadProcessId(handle)).Returns(1000u);
        _mockBrowserDetection.Setup(b => b.IsBrowser(It.IsAny<string>())).Returns(false);
        var sut = CreateSut();

        // Act
        var result = sut.GetBrowserContent(handle);

        // Assert
        result.Success.Should().BeFalse();
        result.ErrorCode.Should().Be(BrowserErrorCode.NotABrowser);
    }

    [Fact]
    public void GetBrowserContent_ReturnsError_WhenContentExtractionFails()
    {
        // Arrange
        var handle = new nint(12345);
        SetupValidBrowserWindow(handle, "chrome", "chrome");
        _mockUIAutomation.Setup(u => u.GetBrowserContent(handle, "chrome", null)).Returns((string?)null);
        var sut = CreateSut();

        // Act
        var result = sut.GetBrowserContent(handle);

        // Assert
        result.Success.Should().BeFalse();
        result.ErrorCode.Should().Be(BrowserErrorCode.ContentExtractionFailed);
    }

    [Fact]
    public void GetBrowserContent_ReturnsSuccess_WithContent()
    {
        // Arrange
        var handle = new nint(12345);
        var expectedContent = "Page content here";
        var expectedUrl = "https://example.com";
        SetupValidBrowserWindow(handle, "chrome", "chrome");
        _mockUIAutomation.Setup(u => u.GetBrowserUrl(handle, "chrome")).Returns(expectedUrl);
        _mockUIAutomation.Setup(u => u.GetBrowserContent(handle, "chrome", null)).Returns(expectedContent);
        var sut = CreateSut();

        // Act
        var result = sut.GetBrowserContent(handle);

        // Assert
        result.Success.Should().BeTrue();
        result.Content.Should().Be(expectedContent);
        result.ContentLength.Should().Be(expectedContent.Length);
        result.Url.Should().Be(expectedUrl);
    }

    [Fact]
    public void GetBrowserContent_RespectsMaxLength()
    {
        // Arrange
        var handle = new nint(12345);
        var options = new BrowserContentOptions(MaxLength: 100);
        SetupValidBrowserWindow(handle, "chrome", "chrome");
        _mockUIAutomation.Setup(u => u.GetBrowserContent(handle, "chrome", 100)).Returns("truncated content");
        var sut = CreateSut();

        // Act
        var result = sut.GetBrowserContent(handle, options);

        // Assert
        result.Success.Should().BeTrue();
        _mockUIAutomation.Verify(u => u.GetBrowserContent(handle, "chrome", 100), Times.Once);
    }

    #endregion

    #region GetActiveBrowserUrl Tests

    [Fact]
    public void GetActiveBrowserUrl_ReturnsError_WhenNoForegroundWindow()
    {
        // Arrange
        _mockNativeWrapper.Setup(n => n.GetForegroundWindow()).Returns(nint.Zero);
        var sut = CreateSut();

        // Act
        var result = sut.GetActiveBrowserUrl();

        // Assert
        result.Success.Should().BeFalse();
        result.ErrorCode.Should().Be(BrowserErrorCode.NoBrowserInForeground);
    }

    [Fact]
    public void GetActiveBrowserUrl_ReturnsError_WhenForegroundIsNotBrowser()
    {
        // Arrange
        var foregroundHandle = new nint(12345);
        _mockNativeWrapper.Setup(n => n.GetForegroundWindow()).Returns(foregroundHandle);
        _mockNativeWrapper.Setup(n => n.GetWindowThreadProcessId(foregroundHandle)).Returns(1000u);
        _mockBrowserDetection.Setup(b => b.IsBrowser(It.IsAny<string>())).Returns(false);
        var sut = CreateSut();

        // Act
        var result = sut.GetActiveBrowserUrl();

        // Assert
        result.Success.Should().BeFalse();
        result.ErrorCode.Should().Be(BrowserErrorCode.NoBrowserInForeground);
    }

    [Fact]
    public void GetActiveBrowserUrl_ReturnsSuccess_WhenForegroundIsBrowser()
    {
        // Arrange
        var foregroundHandle = new nint(12345);
        var expectedUrl = "https://example.com";
        _mockNativeWrapper.Setup(n => n.GetForegroundWindow()).Returns(foregroundHandle);
        SetupValidBrowserWindow(foregroundHandle, "chrome", "chrome");
        _mockUIAutomation.Setup(u => u.GetBrowserUrl(foregroundHandle, "chrome")).Returns(expectedUrl);
        var sut = CreateSut();

        // Act
        var result = sut.GetActiveBrowserUrl();

        // Assert
        result.Success.Should().BeTrue();
        result.Url.Should().Be(expectedUrl);
    }

    #endregion

    #region GetBrowserType Tests

    [Theory]
    [InlineData("chrome", "chrome")]
    [InlineData("Chrome", "chrome")]
    [InlineData("msedge", "edge")]
    [InlineData("firefox", "firefox")]
    [InlineData("brave", "brave")]
    [InlineData("vivaldi", "vivaldi")]
    [InlineData("opera", "opera")]
    [InlineData("chromium", "chromium")]
    public void GetBrowserType_ReturnsCorrectType_ForKnownProcessNames(string processName, string expectedType)
    {
        // Arrange
        var sut = CreateSut();

        // Act
        var result = sut.GetBrowserType(new nint(12345), processName);

        // Assert
        result.Should().Be(expectedType);
    }

    [Theory]
    [InlineData("chrome-dev", "chrome")]
    [InlineData("firefox-nightly", "firefox")]
    [InlineData("edge-beta", "edge")]
    [InlineData("brave-browser", "brave")]
    public void GetBrowserType_HandlesVariants_Correctly(string processName, string expectedType)
    {
        // Arrange
        var sut = CreateSut();

        // Act
        var result = sut.GetBrowserType(new nint(12345), processName);

        // Assert
        result.Should().Be(expectedType);
    }

    [Fact]
    public void GetBrowserType_ReturnsNull_WhenNotABrowser()
    {
        // Arrange
        _mockBrowserDetection.Setup(b => b.IsBrowser("notepad")).Returns(false);
        var sut = CreateSut();

        // Act
        var result = sut.GetBrowserType(new nint(12345), "notepad");

        // Assert
        result.Should().BeNull();
    }

    #endregion

    #region IsBrowserWindow Tests

    [Fact]
    public void IsBrowserWindow_ReturnsFalse_WhenHandleIsZero()
    {
        // Arrange
        var sut = CreateSut();

        // Act
        var result = sut.IsBrowserWindow(nint.Zero);

        // Assert
        result.Should().BeFalse();
    }

    [Fact]
    public void IsBrowserWindow_ReturnsFalse_WhenWindowIsInvalid()
    {
        // Arrange
        _mockNativeWrapper.Setup(n => n.IsWindow(It.IsAny<nint>())).Returns(false);
        var sut = CreateSut();

        // Act
        var result = sut.IsBrowserWindow(new nint(12345));

        // Assert
        result.Should().BeFalse();
    }

    [Fact]
    public void IsBrowserWindow_ReturnsTrue_WhenWindowIsBrowser()
    {
        // Arrange
        var handle = new nint(12345);
        _mockNativeWrapper.Setup(n => n.IsWindow(handle)).Returns(true);
        _mockNativeWrapper.Setup(n => n.GetWindowThreadProcessId(handle)).Returns(1000u);
        _mockBrowserDetection.Setup(b => b.IsBrowser(It.IsAny<string>())).Returns(true);
        var sut = CreateSut();

        // Act
        var result = sut.IsBrowserWindow(handle);

        // Assert
        result.Should().BeTrue();
    }

    #endregion

    #region Exception Handling Tests

    [Fact]
    public void GetBrowserUrl_ReturnsError_WhenExceptionOccurs()
    {
        // Arrange
        var handle = new nint(12345);
        _mockNativeWrapper.Setup(n => n.IsWindow(handle)).Throws(new InvalidOperationException("Test exception"));
        var sut = CreateSut();

        // Act
        var result = sut.GetBrowserUrl(handle);

        // Assert
        result.Success.Should().BeFalse();
        result.ErrorCode.Should().Be(BrowserErrorCode.OperationException);
        result.Error.Should().Contain("Exception");
    }

    [Fact]
    public void GetBrowserContent_ReturnsError_WhenExceptionOccurs()
    {
        // Arrange
        var handle = new nint(12345);
        _mockNativeWrapper.Setup(n => n.IsWindow(handle)).Throws(new InvalidOperationException("Test exception"));
        var sut = CreateSut();

        // Act
        var result = sut.GetBrowserContent(handle);

        // Assert
        result.Success.Should().BeFalse();
        result.ErrorCode.Should().Be(BrowserErrorCode.OperationException);
    }

    [Fact]
    public void GetActiveBrowserUrl_ReturnsError_WhenExceptionOccurs()
    {
        // Arrange
        _mockNativeWrapper.Setup(n => n.GetForegroundWindow()).Throws(new InvalidOperationException("Test exception"));
        var sut = CreateSut();

        // Act
        var result = sut.GetActiveBrowserUrl();

        // Assert
        result.Success.Should().BeFalse();
        result.ErrorCode.Should().Be(BrowserErrorCode.OperationException);
    }

    #endregion

    #region Helper Methods

    private void SetupValidBrowserWindow(nint handle, string processName, string expectedBrowserType)
    {
        _mockNativeWrapper.Setup(n => n.IsWindow(handle)).Returns(true);
        _mockNativeWrapper.Setup(n => n.GetWindowThreadProcessId(handle)).Returns(1000u);
        _mockNativeWrapper.Setup(n => n.GetWindowText(handle)).Returns($"Test Page - {processName}");
        _mockBrowserDetection.Setup(b => b.IsBrowser(It.IsAny<string>())).Returns(true);
    }

    #endregion
}
