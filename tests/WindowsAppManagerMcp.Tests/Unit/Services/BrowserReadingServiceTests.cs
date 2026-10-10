namespace WindowsAppManagerMcp.Tests.Unit.Services;

/// <summary>
/// Unit tests for BrowserReadingService.
/// Tests the service layer that coordinates browser URL and content extraction.
/// The window's process name comes from the mocked INativeWindowWrapper, so the results do not
/// depend on which real process happens to own the fake process ID.
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
    public async Task GetBrowserUrl_ReturnsError_WhenHandleIsZero()
    {
        // Arrange
        var sut = CreateSut();

        // Act
        var result = await sut.GetBrowserUrlAsync(nint.Zero);

        // Assert
        result.Success.Should().BeFalse();
        result.ErrorCode.Should().Be(BrowserErrorCode.InvalidHandle);
        result.Error.Should().Contain("Invalid window handle");
    }

    [Fact]
    public async Task GetBrowserUrl_ReturnsError_WhenWindowIsInvalid()
    {
        // Arrange
        _mockNativeWrapper.Setup(n => n.IsWindow(It.IsAny<nint>())).Returns(false);
        var sut = CreateSut();

        // Act
        var result = await sut.GetBrowserUrlAsync(new nint(12345));

        // Assert
        result.Success.Should().BeFalse();
        result.ErrorCode.Should().Be(BrowserErrorCode.InvalidHandle);
    }

    [Fact]
    public async Task GetBrowserUrl_ReturnsError_WhenUIAutomationNotAvailable()
    {
        // Arrange
        var handle = new nint(12345);
        _mockNativeWrapper.Setup(n => n.IsWindow(handle)).Returns(true);
        _mockNativeWrapper.Setup(n => n.GetWindowThreadProcessId(handle)).Returns(1000u);
        _mockBrowserDetection.Setup(b => b.IsBrowser(It.IsAny<string>())).Returns(true);
        _mockUIAutomation.Setup(u => u.IsAvailable()).Returns(false);
        var sut = CreateSut();

        // Act
        var result = await sut.GetBrowserUrlAsync(handle);

        // Assert
        result.Success.Should().BeFalse();
        result.ErrorCode.Should().Be(BrowserErrorCode.AutomationUnavailable);
    }

    [Fact]
    public async Task GetBrowserUrl_ReturnsError_WhenNotABrowser()
    {
        // Arrange
        var handle = new nint(12345);
        _mockNativeWrapper.Setup(n => n.IsWindow(handle)).Returns(true);
        _mockNativeWrapper.Setup(n => n.GetWindowThreadProcessId(handle)).Returns(1000u);
        _mockBrowserDetection.Setup(b => b.IsBrowser(It.IsAny<string>())).Returns(false);
        var sut = CreateSut();

        // Act
        var result = await sut.GetBrowserUrlAsync(handle);

        // Assert
        result.Success.Should().BeFalse();
        result.ErrorCode.Should().Be(BrowserErrorCode.NotABrowser);
    }

    [Fact]
    public async Task GetBrowserUrl_ReturnsError_WhenUrlBarNotFound()
    {
        // Arrange
        var handle = new nint(12345);
        SetupValidBrowserWindow(handle, "chrome", "chrome");
        _mockUIAutomation.Setup(u => u.GetBrowserUrl(handle, "chrome")).Returns((string?)null);
        var sut = CreateSut();

        // Act
        var result = await sut.GetBrowserUrlAsync(handle);

        // Assert
        result.Success.Should().BeFalse();
        result.ErrorCode.Should().Be(BrowserErrorCode.UrlBarNotFound);
        result.BrowserType.Should().Be("chrome");
    }

    [Fact]
    public async Task GetBrowserUrl_ReturnsSuccess_WithValidUrl()
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
        var result = await sut.GetBrowserUrlAsync(handle);

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
    public async Task GetBrowserContent_ReturnsError_WhenHandleIsInvalid()
    {
        // Arrange
        var sut = CreateSut();

        // Act
        var result = await sut.GetBrowserContentAsync(nint.Zero);

        // Assert
        result.Success.Should().BeFalse();
        result.ErrorCode.Should().Be(BrowserErrorCode.InvalidHandle);
    }

    [Fact]
    public async Task GetBrowserContent_ReturnsError_WhenNotABrowser()
    {
        // Arrange
        var handle = new nint(12345);
        _mockNativeWrapper.Setup(n => n.IsWindow(handle)).Returns(true);
        _mockNativeWrapper.Setup(n => n.GetWindowThreadProcessId(handle)).Returns(1000u);
        _mockBrowserDetection.Setup(b => b.IsBrowser(It.IsAny<string>())).Returns(false);
        var sut = CreateSut();

        // Act
        var result = await sut.GetBrowserContentAsync(handle);

        // Assert
        result.Success.Should().BeFalse();
        result.ErrorCode.Should().Be(BrowserErrorCode.NotABrowser);
    }

    [Fact]
    public async Task GetBrowserContent_ReturnsError_WhenContentExtractionFails()
    {
        // Arrange
        var handle = new nint(12345);
        SetupValidBrowserWindow(handle, "chrome", "chrome");
        _mockUIAutomation.Setup(u => u.GetBrowserContent(handle, "chrome", null)).Returns((string?)null);
        var sut = CreateSut();

        // Act
        var result = await sut.GetBrowserContentAsync(handle);

        // Assert
        result.Success.Should().BeFalse();
        result.ErrorCode.Should().Be(BrowserErrorCode.ContentExtractionFailed);
    }

    [Fact]
    public async Task GetBrowserContent_ReturnsSuccess_WithContent()
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
        var result = await sut.GetBrowserContentAsync(handle);

        // Assert
        result.Success.Should().BeTrue();
        result.Content.Should().Be(expectedContent);
        result.ContentLength.Should().Be(expectedContent.Length);
        result.Url.Should().Be(expectedUrl);
    }

    [Fact]
    public async Task GetBrowserContent_RespectsMaxLength()
    {
        // Arrange
        var handle = new nint(12345);
        var options = new BrowserContentOptions(MaxLength: 100);
        SetupValidBrowserWindow(handle, "chrome", "chrome");
        _mockUIAutomation.Setup(u => u.GetBrowserContent(handle, "chrome", 100)).Returns("truncated content");
        var sut = CreateSut();

        // Act
        var result = await sut.GetBrowserContentAsync(handle, options);

        // Assert
        result.Success.Should().BeTrue();
        _mockUIAutomation.Verify(u => u.GetBrowserContent(handle, "chrome", 100), Times.Once);
    }

    #endregion

    #region GetActiveBrowserUrl Tests

    [Fact]
    public async Task GetActiveBrowserUrl_ReturnsError_WhenNoForegroundWindow()
    {
        // Arrange
        _mockNativeWrapper.Setup(n => n.GetForegroundWindow()).Returns(nint.Zero);
        var sut = CreateSut();

        // Act
        var result = await sut.GetActiveBrowserUrlAsync();

        // Assert
        result.Success.Should().BeFalse();
        result.ErrorCode.Should().Be(BrowserErrorCode.NoBrowserInForeground);
    }

    [Fact]
    public async Task GetActiveBrowserUrl_ReturnsError_WhenForegroundIsNotBrowser()
    {
        // Arrange
        var foregroundHandle = new nint(12345);
        _mockNativeWrapper.Setup(n => n.GetForegroundWindow()).Returns(foregroundHandle);
        _mockNativeWrapper.Setup(n => n.GetWindowThreadProcessId(foregroundHandle)).Returns(1000u);
        _mockBrowserDetection.Setup(b => b.IsBrowser(It.IsAny<string>())).Returns(false);
        var sut = CreateSut();

        // Act
        var result = await sut.GetActiveBrowserUrlAsync();

        // Assert
        result.Success.Should().BeFalse();
        result.ErrorCode.Should().Be(BrowserErrorCode.NoBrowserInForeground);
    }

    [Fact]
    public async Task GetActiveBrowserUrl_ReturnsSuccess_WhenForegroundIsBrowser()
    {
        // Arrange
        var foregroundHandle = new nint(12345);
        var expectedUrl = "https://example.com";
        _mockNativeWrapper.Setup(n => n.GetForegroundWindow()).Returns(foregroundHandle);
        SetupValidBrowserWindow(foregroundHandle, "chrome", "chrome");
        _mockUIAutomation.Setup(u => u.GetBrowserUrl(foregroundHandle, "chrome")).Returns(expectedUrl);
        var sut = CreateSut();

        // Act
        var result = await sut.GetActiveBrowserUrlAsync();

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

    #region Process Name Tests

    [Fact]
    public async Task GetBrowserUrl_TakesTheProcessNameFromTheNativeWrapper()
    {
        // Arrange
        var handle = new nint(12345);
        _mockNativeWrapper.Setup(n => n.IsWindow(handle)).Returns(true);
        _mockNativeWrapper.Setup(n => n.GetWindowThreadProcessId(handle)).Returns(1000u);
        _mockNativeWrapper.Setup(n => n.GetProcessName(1000u)).Returns("msedge");
        _mockBrowserDetection.Setup(b => b.IsBrowser("msedge")).Returns(true);
        _mockUIAutomation.Setup(u => u.GetBrowserUrl(handle, "edge")).Returns("https://example.com");
        var sut = CreateSut();

        // Act
        var result = await sut.GetBrowserUrlAsync(handle);

        // Assert
        result.Success.Should().BeTrue();
        result.BrowserType.Should().Be("edge");
        _mockNativeWrapper.Verify(n => n.GetProcessName(1000u), Times.Once);
    }

    [Fact]
    public void IsBrowserWindow_WhenTheProcessNameCannotBeRead_ChecksUnknown()
    {
        // Arrange - the process exited or cannot be opened
        var handle = new nint(12345);
        _mockNativeWrapper.Setup(n => n.IsWindow(handle)).Returns(true);
        _mockNativeWrapper.Setup(n => n.GetWindowThreadProcessId(handle)).Returns(1000u);
        _mockNativeWrapper.Setup(n => n.GetProcessName(1000u)).Returns((string?)null);
        var sut = CreateSut();

        // Act
        var result = sut.IsBrowserWindow(handle);

        // Assert
        result.Should().BeFalse();
        _mockBrowserDetection.Verify(b => b.IsBrowser("Unknown"), Times.Once);
    }

    #endregion

    #region Exception Handling Tests

    [Fact]
    public async Task GetBrowserUrl_ReturnsError_WhenExceptionOccurs()
    {
        // Arrange
        var handle = new nint(12345);
        _mockNativeWrapper.Setup(n => n.IsWindow(handle)).Throws(new InvalidOperationException("Test exception"));
        var sut = CreateSut();

        // Act
        var result = await sut.GetBrowserUrlAsync(handle);

        // Assert
        result.Success.Should().BeFalse();
        result.ErrorCode.Should().Be(BrowserErrorCode.OperationException);
        result.Error.Should().Contain("Exception");
    }

    [Fact]
    public async Task GetBrowserContent_ReturnsError_WhenExceptionOccurs()
    {
        // Arrange
        var handle = new nint(12345);
        _mockNativeWrapper.Setup(n => n.IsWindow(handle)).Throws(new InvalidOperationException("Test exception"));
        var sut = CreateSut();

        // Act
        var result = await sut.GetBrowserContentAsync(handle);

        // Assert
        result.Success.Should().BeFalse();
        result.ErrorCode.Should().Be(BrowserErrorCode.OperationException);
    }

    [Fact]
    public async Task GetActiveBrowserUrl_ReturnsError_WhenExceptionOccurs()
    {
        // Arrange
        _mockNativeWrapper.Setup(n => n.GetForegroundWindow()).Throws(new InvalidOperationException("Test exception"));
        var sut = CreateSut();

        // Act
        var result = await sut.GetActiveBrowserUrlAsync();

        // Assert
        result.Success.Should().BeFalse();
        result.ErrorCode.Should().Be(BrowserErrorCode.OperationException);
    }

    #endregion

    #region CDP Tests

    private const int CdpPort = 9222;

    private static readonly CdpTarget CdpTab = new(
        Id: "TAB1",
        Type: "page",
        Title: "Test Page",
        Url: "https://example.com/cdp",
        WebSocketDebuggerUrl: "ws://127.0.0.1:9222/devtools/page/TAB1");

    private void SetupCdpMatch(CdpResult<CdpTarget> result) =>
        _mockCdpService
            .Setup(c => c.FindTargetForWindowAsync(
                It.IsAny<int>(), It.IsAny<int>(), It.IsAny<string>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(result);

    private void SetupCdpSnapshot(CdpResult<CdpPageSnapshot> result) =>
        _mockCdpService
            .Setup(c => c.GetPageSnapshotAsync(
                It.IsAny<CdpTarget>(), It.IsAny<bool>(), It.IsAny<int?>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(result);

    [Fact]
    public async Task GetBrowserUrl_ReturnsCdpTargetUrl_AndReportsCdpSource()
    {
        var handle = new nint(12345);
        SetupValidBrowserWindow(handle, "chrome", "chrome");
        SetupCdpMatch(CdpResult<CdpTarget>.Ok(CdpTab));
        var sut = CreateSut();

        var result = await sut.GetBrowserUrlAsync(handle, new CdpOptions(Port: CdpPort));

        result.Success.Should().BeTrue();
        result.Url.Should().Be("https://example.com/cdp");
        result.PageTitle.Should().Be("Test Page");
        result.Source.Should().Be("cdp");
        result.TargetId.Should().Be("TAB1");
        result.CdpError.Should().BeNull();
        _mockUIAutomation.Verify(u => u.GetBrowserUrl(It.IsAny<nint>(), It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task GetBrowserUrl_MatchesCdpTabUsingWindowProcessAndTitle()
    {
        var handle = new nint(12345);
        SetupValidBrowserWindow(handle, "chrome", "chrome");
        SetupCdpMatch(CdpResult<CdpTarget>.Ok(CdpTab));
        var sut = CreateSut();

        await sut.GetBrowserUrlAsync(handle, new CdpOptions(Port: CdpPort));

        _mockCdpService.Verify(c => c.FindTargetForWindowAsync(
            CdpPort, 1000, "Test Page - chrome", It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task GetBrowserUrl_FallsBackToUia_AndReportsWhyCdpWasNotUsed()
    {
        var handle = new nint(12345);
        SetupValidBrowserWindow(handle, "chrome", "chrome");
        SetupCdpMatch(CdpResult<CdpTarget>.Fail(BrowserErrorCode.CdpTargetNotFound, "No tab has the window title"));
        _mockUIAutomation.Setup(u => u.GetBrowserUrl(handle, "chrome")).Returns("https://example.com/uia");
        var sut = CreateSut();

        var result = await sut.GetBrowserUrlAsync(handle, new CdpOptions(Port: CdpPort));

        result.Success.Should().BeTrue();
        result.Url.Should().Be("https://example.com/uia");
        result.Source.Should().Be("uia");
        result.TargetId.Should().BeNull();
        result.CdpError.Should().Be("CdpTargetNotFound: No tab has the window title");
    }

    [Fact]
    public async Task GetBrowserUrl_ReturnsCdpError_InsteadOfFallingBack_WhenCdpIsRequired()
    {
        var handle = new nint(12345);
        SetupValidBrowserWindow(handle, "chrome", "chrome");
        SetupCdpMatch(CdpResult<CdpTarget>.Fail(BrowserErrorCode.CdpPortOwnerMismatch, "CDP port 9222 belongs to process 999"));
        _mockUIAutomation.Setup(u => u.GetBrowserUrl(handle, "chrome")).Returns("https://example.com/uia");
        var sut = CreateSut();

        var result = await sut.GetBrowserUrlAsync(handle, new CdpOptions(Port: CdpPort, Required: true));

        result.Success.Should().BeFalse();
        result.ErrorCode.Should().Be(BrowserErrorCode.CdpPortOwnerMismatch);
        result.Error.Should().Contain("999");
        result.Source.Should().Be("cdp");
        _mockUIAutomation.Verify(u => u.GetBrowserUrl(It.IsAny<nint>(), It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task GetBrowserUrl_ReturnsError_WhenCdpIsRequiredWithoutPort()
    {
        var handle = new nint(12345);
        SetupValidBrowserWindow(handle, "chrome", "chrome");
        var sut = CreateSut();

        var result = await sut.GetBrowserUrlAsync(handle, new CdpOptions(Port: null, Required: true));

        result.Success.Should().BeFalse();
        result.ErrorCode.Should().Be(BrowserErrorCode.CdpUnavailable);
        result.Error.Should().Contain("cdpPort");
        _mockUIAutomation.Verify(u => u.GetBrowserUrl(It.IsAny<nint>(), It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task GetBrowserUrl_ReportsUiaSource_WithoutCdp()
    {
        var handle = new nint(12345);
        SetupValidBrowserWindow(handle, "chrome", "chrome");
        _mockUIAutomation.Setup(u => u.GetBrowserUrl(handle, "chrome")).Returns("https://example.com");
        var sut = CreateSut();

        var result = await sut.GetBrowserUrlAsync(handle);

        result.Source.Should().Be("uia");
        result.CdpError.Should().BeNull();
        _mockCdpService.VerifyNoOtherCalls();
    }

    [Theory]
    [InlineData(1, 1000)]
    [InlineData(5000, 5000)]
    [InlineData(999999, 30000)]
    public async Task GetBrowserUrl_ClampsCdpTimeout(int requestedMs, int expectedMs)
    {
        var handle = new nint(12345);
        SetupValidBrowserWindow(handle, "chrome", "chrome");
        SetupCdpMatch(CdpResult<CdpTarget>.Ok(CdpTab));
        var sut = CreateSut();

        await sut.GetBrowserUrlAsync(handle, new CdpOptions(Port: CdpPort, TimeoutMs: requestedMs));

        _mockCdpService.Verify(c => c.FindTargetForWindowAsync(
            It.IsAny<int>(), It.IsAny<int>(), It.IsAny<string>(), TimeSpan.FromMilliseconds(expectedMs), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task GetBrowserUrl_PropagatesCallerCancellation()
    {
        var handle = new nint(12345);
        SetupValidBrowserWindow(handle, "chrome", "chrome");
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        _mockCdpService
            .Setup(c => c.FindTargetForWindowAsync(
                It.IsAny<int>(), It.IsAny<int>(), It.IsAny<string>(), It.IsAny<TimeSpan>(), cts.Token))
            .ThrowsAsync(new OperationCanceledException(cts.Token));
        var sut = CreateSut();

        var act = () => sut.GetBrowserUrlAsync(handle, new CdpOptions(Port: CdpPort), cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task GetActiveBrowserUrl_PassesCdpOptionsThrough()
    {
        var handle = new nint(12345);
        _mockNativeWrapper.Setup(n => n.GetForegroundWindow()).Returns(handle);
        SetupValidBrowserWindow(handle, "chrome", "chrome");
        SetupCdpMatch(CdpResult<CdpTarget>.Ok(CdpTab));
        var sut = CreateSut();

        var result = await sut.GetActiveBrowserUrlAsync(new CdpOptions(Port: CdpPort));

        result.Source.Should().Be("cdp");
        result.TargetId.Should().Be("TAB1");
    }

    [Fact]
    public async Task GetBrowserContent_ReturnsCdpSnapshot_AndReportsCdpSource()
    {
        var handle = new nint(12345);
        SetupValidBrowserWindow(handle, "chrome", "chrome");
        SetupCdpMatch(CdpResult<CdpTarget>.Ok(CdpTab));
        SetupCdpSnapshot(CdpResult<CdpPageSnapshot>.Ok(
            new CdpPageSnapshot("Live title", "https://example.com/live", "JavaScript-rendered text")));
        var sut = CreateSut();

        var result = await sut.GetBrowserContentAsync(handle, cdp: new CdpOptions(Port: CdpPort));

        result.Success.Should().BeTrue();
        result.Content.Should().Be("JavaScript-rendered text");
        result.ContentLength.Should().Be("JavaScript-rendered text".Length);
        result.Url.Should().Be("https://example.com/live");
        result.PageTitle.Should().Be("Live title");
        result.Source.Should().Be("cdp");
        result.TargetId.Should().Be("TAB1");
        _mockUIAutomation.Verify(u => u.GetBrowserContent(It.IsAny<nint>(), It.IsAny<string>(), It.IsAny<int?>()), Times.Never);
    }

    [Fact]
    public async Task GetBrowserContent_PassesFormatAndMaxLengthToCdp_AndTruncates()
    {
        var handle = new nint(12345);
        SetupValidBrowserWindow(handle, "chrome", "chrome");
        SetupCdpMatch(CdpResult<CdpTarget>.Ok(CdpTab));
        SetupCdpSnapshot(CdpResult<CdpPageSnapshot>.Ok(new CdpPageSnapshot("t", "u", "<html>abcdef")));
        var sut = CreateSut();

        var result = await sut.GetBrowserContentAsync(
            handle, new BrowserContentOptions(MaxLength: 6, TextOnly: false), new CdpOptions(Port: CdpPort));

        result.Content.Should().Be("<html>");
        _mockCdpService.Verify(c => c.GetPageSnapshotAsync(
            CdpTab, false, 6,
            It.Is<TimeSpan>(t => t > TimeSpan.Zero && t <= TimeSpan.FromMilliseconds(CdpOptions.DefaultTimeoutMs)),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task GetBrowserContent_ReturnsEmptyCdpPage_WithoutFallingBack()
    {
        var handle = new nint(12345);
        SetupValidBrowserWindow(handle, "chrome", "chrome");
        SetupCdpMatch(CdpResult<CdpTarget>.Ok(CdpTab));
        SetupCdpSnapshot(CdpResult<CdpPageSnapshot>.Ok(new CdpPageSnapshot("t", "about:blank", "")));
        var sut = CreateSut();

        var result = await sut.GetBrowserContentAsync(handle, cdp: new CdpOptions(Port: CdpPort));

        result.Success.Should().BeTrue();
        result.Content.Should().BeEmpty();
        result.Source.Should().Be("cdp");
        _mockUIAutomation.Verify(u => u.GetBrowserContent(It.IsAny<nint>(), It.IsAny<string>(), It.IsAny<int?>()), Times.Never);
    }

    [Fact]
    public async Task GetBrowserContent_FallsBackToUia_AndReportsCdpTimeout()
    {
        var handle = new nint(12345);
        SetupValidBrowserWindow(handle, "chrome", "chrome");
        SetupCdpMatch(CdpResult<CdpTarget>.Ok(CdpTab));
        SetupCdpSnapshot(CdpResult<CdpPageSnapshot>.Fail(BrowserErrorCode.CdpTimeout, "The page did not answer within 10000 ms."));
        _mockUIAutomation.Setup(u => u.GetBrowserContent(handle, "chrome", null)).Returns("UIA text");
        var sut = CreateSut();

        var result = await sut.GetBrowserContentAsync(handle, cdp: new CdpOptions(Port: CdpPort));

        result.Success.Should().BeTrue();
        result.Content.Should().Be("UIA text");
        result.Source.Should().Be("uia");
        result.TargetId.Should().BeNull();
        result.CdpError.Should().StartWith("CdpTimeout:");
    }

    [Fact]
    public async Task GetBrowserContent_ReturnsCdpError_WhenCdpIsRequired()
    {
        var handle = new nint(12345);
        SetupValidBrowserWindow(handle, "chrome", "chrome");
        SetupCdpMatch(CdpResult<CdpTarget>.Ok(CdpTab));
        SetupCdpSnapshot(CdpResult<CdpPageSnapshot>.Fail(BrowserErrorCode.CdpDialogOpen, "The page has a alert dialog open"));
        var sut = CreateSut();

        var result = await sut.GetBrowserContentAsync(handle, cdp: new CdpOptions(Port: CdpPort, Required: true));

        result.Success.Should().BeFalse();
        result.ErrorCode.Should().Be(BrowserErrorCode.CdpDialogOpen);
        result.Source.Should().Be("cdp");
        result.TargetId.Should().Be("TAB1");
        _mockUIAutomation.Verify(u => u.GetBrowserContent(It.IsAny<nint>(), It.IsAny<string>(), It.IsAny<int?>()), Times.Never);
    }

    [Fact]
    public async Task GetBrowserContent_DoesNotReadAnyTab_WhenWindowIsNotMatched()
    {
        var handle = new nint(12345);
        SetupValidBrowserWindow(handle, "chrome", "chrome");
        SetupCdpMatch(CdpResult<CdpTarget>.Fail(BrowserErrorCode.CdpTargetAmbiguous, "2 tabs have the title"));
        _mockUIAutomation.Setup(u => u.GetBrowserContent(handle, "chrome", null)).Returns("UIA text");
        var sut = CreateSut();

        var result = await sut.GetBrowserContentAsync(handle, cdp: new CdpOptions(Port: CdpPort));

        result.Source.Should().Be("uia");
        result.CdpError.Should().StartWith("CdpTargetAmbiguous:");
        _mockCdpService.Verify(c => c.GetPageSnapshotAsync(
            It.IsAny<CdpTarget>(), It.IsAny<bool>(), It.IsAny<int?>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task GetBrowserContent_ReturnsHtmlRequiresCdp_WhenHtmlIsRequestedWithoutCdp()
    {
        var handle = new nint(12345);
        SetupValidBrowserWindow(handle, "chrome", "chrome");
        _mockUIAutomation.Setup(u => u.GetBrowserContent(handle, "chrome", null)).Returns("UIA text");
        var sut = CreateSut();

        var result = await sut.GetBrowserContentAsync(handle, new BrowserContentOptions(TextOnly: false));

        result.Success.Should().BeFalse();
        result.ErrorCode.Should().Be(BrowserErrorCode.HtmlRequiresCdp);
        result.Error.Should().Contain("textOnly=true");
        _mockUIAutomation.Verify(u => u.GetBrowserContent(It.IsAny<nint>(), It.IsAny<string>(), It.IsAny<int?>()), Times.Never);
    }

    [Fact]
    public async Task GetBrowserContent_ReturnsHtmlRequiresCdp_WhenCdpFailsForHtml()
    {
        var handle = new nint(12345);
        SetupValidBrowserWindow(handle, "chrome", "chrome");
        SetupCdpMatch(CdpResult<CdpTarget>.Fail(BrowserErrorCode.CdpUnavailable, "Could not connect"));
        _mockUIAutomation.Setup(u => u.GetBrowserContent(handle, "chrome", null)).Returns("UIA text");
        var sut = CreateSut();

        var result = await sut.GetBrowserContentAsync(
            handle, new BrowserContentOptions(TextOnly: false), new CdpOptions(Port: CdpPort));

        result.Success.Should().BeFalse();
        result.ErrorCode.Should().Be(BrowserErrorCode.HtmlRequiresCdp);
        result.Error.Should().Contain("CdpUnavailable: Could not connect");
        result.CdpError.Should().Be("CdpUnavailable: Could not connect");
        _mockUIAutomation.Verify(u => u.GetBrowserContent(It.IsAny<nint>(), It.IsAny<string>(), It.IsAny<int?>()), Times.Never);
    }

    [Fact]
    public async Task GetBrowserContent_GivesPageReadOnlyTheRemainingTimeBudget()
    {
        var handle = new nint(12345);
        SetupValidBrowserWindow(handle, "chrome", "chrome");
        var matching = TimeSpan.Zero;
        _mockCdpService
            .Setup(c => c.FindTargetForWindowAsync(
                It.IsAny<int>(), It.IsAny<int>(), It.IsAny<string>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
            .Returns(async () =>
            {
                var watch = System.Diagnostics.Stopwatch.StartNew();
                await Task.Delay(300);
                matching = watch.Elapsed;
                return CdpResult<CdpTarget>.Ok(CdpTab);
            });
        SetupCdpSnapshot(CdpResult<CdpPageSnapshot>.Ok(new CdpPageSnapshot("t", "u", "c")));
        var sut = CreateSut();

        // The largest budget, so a loaded test machine cannot use it up before the page read (a 2 s
        // budget once ran out under a full-solution run); the time the matching took must still be taken off
        await sut.GetBrowserContentAsync(handle, cdp: new CdpOptions(Port: CdpPort, TimeoutMs: CdpOptions.MaxTimeoutMs));

        // The bound is the matching's measured time, not the 300 ms asked for: Task.Delay can end up to a timer
        // tick early (a run once read 29700.1 ms), and the service's own stopwatch runs at least as long as this one
        matching.Should().BeGreaterThan(TimeSpan.FromMilliseconds(250));
        _mockCdpService.Verify(c => c.GetPageSnapshotAsync(
            It.IsAny<CdpTarget>(), It.IsAny<bool>(), It.IsAny<int?>(),
            It.Is<TimeSpan>(t => t > TimeSpan.Zero && t <= TimeSpan.FromMilliseconds(CdpOptions.MaxTimeoutMs) - matching),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task GetBrowserContent_ReturnsCdpTimeout_WhenMatchingUsesUpTheBudget()
    {
        var handle = new nint(12345);
        SetupValidBrowserWindow(handle, "chrome", "chrome");
        _mockCdpService
            .Setup(c => c.FindTargetForWindowAsync(
                It.IsAny<int>(), It.IsAny<int>(), It.IsAny<string>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
            .Returns(async () =>
            {
                await Task.Delay(1100);
                return CdpResult<CdpTarget>.Ok(CdpTab);
            });
        var sut = CreateSut();

        var result = await sut.GetBrowserContentAsync(handle, cdp: new CdpOptions(Port: CdpPort, Required: true, TimeoutMs: 1000));

        result.ErrorCode.Should().Be(BrowserErrorCode.CdpTimeout);
        _mockCdpService.Verify(c => c.GetPageSnapshotAsync(
            It.IsAny<CdpTarget>(), It.IsAny<bool>(), It.IsAny<int?>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    #endregion

    #region Helper Methods

    private void SetupValidBrowserWindow(nint handle, string processName, string expectedBrowserType)
    {
        _mockNativeWrapper.Setup(n => n.IsWindow(handle)).Returns(true);
        _mockNativeWrapper.Setup(n => n.GetWindowThreadProcessId(handle)).Returns(1000u);
        _mockNativeWrapper.Setup(n => n.GetProcessName(1000u)).Returns(processName);
        _mockNativeWrapper.Setup(n => n.GetWindowText(handle)).Returns($"Test Page - {processName}");
        _mockBrowserDetection.Setup(b => b.IsBrowser(It.IsAny<string>())).Returns(true);
    }

    #endregion
}
