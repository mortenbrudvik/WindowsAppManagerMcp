namespace WindowsAppManagerMcp.Tests.Unit.Services;

/// <summary>
/// Unit tests for BrowserReadingService.
/// Tests the service layer that coordinates browser URL and content extraction.
/// The window's process name comes from the mocked INativeWindowWrapper, so the results do not
/// depend on which real process happens to own the fake process ID. The UI Automation reads run on
/// an in-memory tree (roadmap M3.F24): no real browser or UI Automation client is read.
/// </summary>
public class BrowserReadingServiceTests
{
    private readonly Mock<INativeWindowWrapper> _mockNativeWrapper;
    private readonly Mock<IBrowserDetectionService> _mockBrowserDetection;
    private readonly Mock<ICdpService> _mockCdpService;
    private readonly FakeUiaTree _tree = new();
    private readonly InlineUiaWorker _worker = new();
    private readonly Mock<IAccessibilityWarmup> _warmup = new();
    private ITargetPolicy? _policy;

    public BrowserReadingServiceTests()
    {
        _mockNativeWrapper = new Mock<INativeWindowWrapper>();
        _mockBrowserDetection = new Mock<IBrowserDetectionService>();
        _mockCdpService = new Mock<ICdpService>();
    }

    private BrowserReadingService CreateSut(IUiaWorker? worker = null) => new(
        _mockNativeWrapper.Object,
        _mockBrowserDetection.Object,
        _mockCdpService.Object,
        _tree,
        worker ?? _worker,
        _warmup.Object,
        _policy);

    /// <summary>
    /// Shows a page in the browser window <paramref name="handle"/>: Window <paramref name="title"/> > ToolBar > Edit
    /// "Address and search bar" with <paramref name="url"/> (none when null), and a Document with <paramref name="content"/>
    /// as its text (none when null).
    /// </summary>
    private void ShowPage(nint handle, string? url, string? content = null, string? title = "Example - Google Chrome")
    {
        var window = new FakeUiaNode("Window", title);
        if (url != null)
        {
            window.With(new FakeUiaNode("ToolBar").With(
                new FakeUiaNode("Edit", "Address and search bar") { ActionPatterns = ["Value"] }
                    .WithPattern<IUiaValuePattern>(new FakeValuePattern(url))));
        }

        if (content != null)
        {
            window.With(new FakeUiaNode("Document", "Example").WithPattern<IUiaTextPattern>(new FakeTextPattern(content)));
        }

        _tree.WithWindow(handle, window);
    }

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
        _tree.Available = false;
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
        ShowPage(handle, url: null);
        var sut = CreateSut();

        // Act
        var result = await sut.GetBrowserUrlAsync(handle);

        // Assert
        result.Success.Should().BeFalse();
        result.ErrorCode.Should().Be(BrowserErrorCode.UrlBarNotFound);
        result.BrowserType.Should().Be("chrome");
    }

    [Fact]
    public async Task GetBrowserUrl_ReturnsUrlBarNotFound_WithTheWindowText_WhenTheWindowHasNoElement()
    {
        // Arrange - UI Automation has no element for the handle (the window closed meanwhile)
        var handle = new nint(12345);
        SetupValidBrowserWindow(handle, "chrome", "chrome");
        var sut = CreateSut();

        // Act
        var result = await sut.GetBrowserUrlAsync(handle);

        // Assert
        result.ErrorCode.Should().Be(BrowserErrorCode.UrlBarNotFound);
        result.PageTitle.Should().Be("Test Page - chrome");
        result.Source.Should().Be("uia");
    }

    [Fact]
    public async Task GetBrowserUrl_ReturnsSuccess_WithValidUrl()
    {
        // Arrange
        var handle = new nint(12345);
        var expectedUrl = "https://example.com";
        var expectedTitle = "Example Page - Google Chrome";
        SetupValidBrowserWindow(handle, "chrome", "chrome");
        ShowPage(handle, expectedUrl, title: expectedTitle);
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
        ShowPage(handle, "https://example.com", content: null);
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
        ShowPage(handle, expectedUrl, expectedContent);
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
        ShowPage(handle, "https://example.com", new string('x', 150));
        var sut = CreateSut();

        // Act
        var result = await sut.GetBrowserContentAsync(handle, options);

        // Assert
        result.Success.Should().BeTrue();
        result.Content.Should().Be(new string('x', 100));
        result.ContentLength.Should().Be(100);
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
        ShowPage(foregroundHandle, expectedUrl);
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
        ShowPage(handle, "https://example.com");
        var sut = CreateSut();

        // Act
        var result = await sut.GetBrowserUrlAsync(handle);

        // Assert
        result.Success.Should().BeTrue();
        result.BrowserType.Should().Be("edge");
        // The UI Automation read's checks (a UWP frame's app process, the hung-window check) ask the wrapper too
        _mockNativeWrapper.Verify(n => n.GetProcessName(1000u), Times.AtLeastOnce);
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
        WebSocketDebuggerUrl: "ws://127.0.0.1:9222/devtools/page/TAB1",
        Port: CdpPort);

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
        _worker.Calls.Should().Be(0);
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
        ShowPage(handle, "https://example.com/uia");
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
        ShowPage(handle, "https://example.com/uia");
        var sut = CreateSut();

        var result = await sut.GetBrowserUrlAsync(handle, new CdpOptions(Port: CdpPort, Required: true));

        result.Success.Should().BeFalse();
        result.ErrorCode.Should().Be(BrowserErrorCode.CdpPortOwnerMismatch);
        result.Error.Should().Contain("999");
        result.Source.Should().Be("cdp");
        _worker.Calls.Should().Be(0);
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
        _worker.Calls.Should().Be(0);
    }

    [Fact]
    public async Task GetBrowserUrl_ReportsUiaSource_WithoutCdp()
    {
        var handle = new nint(12345);
        SetupValidBrowserWindow(handle, "chrome", "chrome");
        ShowPage(handle, "https://example.com");
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
        _worker.Calls.Should().Be(0);
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
        _worker.Calls.Should().Be(0);
    }

    [Fact]
    public async Task GetBrowserContent_FallsBackToUia_AndReportsCdpTimeout()
    {
        var handle = new nint(12345);
        SetupValidBrowserWindow(handle, "chrome", "chrome");
        SetupCdpMatch(CdpResult<CdpTarget>.Ok(CdpTab));
        SetupCdpSnapshot(CdpResult<CdpPageSnapshot>.Fail(BrowserErrorCode.CdpTimeout, "The page did not answer within 10000 ms."));
        ShowPage(handle, "https://example.com", "UIA text");
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
        _worker.Calls.Should().Be(0);
    }

    [Fact]
    public async Task GetBrowserContent_DoesNotReadAnyTab_WhenWindowIsNotMatched()
    {
        var handle = new nint(12345);
        SetupValidBrowserWindow(handle, "chrome", "chrome");
        SetupCdpMatch(CdpResult<CdpTarget>.Fail(BrowserErrorCode.CdpTargetAmbiguous, "2 tabs have the title"));
        ShowPage(handle, "https://example.com", "UIA text");
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
        ShowPage(handle, "https://example.com", "UIA text");
        var sut = CreateSut();

        var result = await sut.GetBrowserContentAsync(handle, new BrowserContentOptions(TextOnly: false));

        result.Success.Should().BeFalse();
        result.ErrorCode.Should().Be(BrowserErrorCode.HtmlRequiresCdp);
        result.Error.Should().Contain("textOnly=true");
        _worker.Calls.Should().Be(0);
    }

    [Fact]
    public async Task GetBrowserContent_ReturnsHtmlRequiresCdp_WhenCdpFailsForHtml()
    {
        var handle = new nint(12345);
        SetupValidBrowserWindow(handle, "chrome", "chrome");
        SetupCdpMatch(CdpResult<CdpTarget>.Fail(BrowserErrorCode.CdpUnavailable, "Could not connect"));
        ShowPage(handle, "https://example.com", "UIA text");
        var sut = CreateSut();

        var result = await sut.GetBrowserContentAsync(
            handle, new BrowserContentOptions(TextOnly: false), new CdpOptions(Port: CdpPort));

        result.Success.Should().BeFalse();
        result.ErrorCode.Should().Be(BrowserErrorCode.HtmlRequiresCdp);
        result.Error.Should().Contain("CdpUnavailable: Could not connect");
        result.CdpError.Should().Be("CdpUnavailable: Could not connect");
        _worker.Calls.Should().Be(0);
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

    #region UI Automation path on the seam (roadmap M3.F24)

    private static readonly AccessibilityWarmupInfo Warmed =
        new(AccessibilityWarmupStatus.Ready, WebContentHost.Chromium, WaitedMs: 400, Polls: 3, DocumentChildCount: 12);

    [Fact]
    public async Task GetBrowserUrl_ReadsOnTheUiaWorker_CountedAgainstTheBrowsersProcess()
    {
        // Arrange
        var handle = new nint(12345);
        SetupValidBrowserWindow(handle, "chrome", "chrome");
        ShowPage(handle, "https://example.com");
        var sut = CreateSut();

        // Act
        var result = await sut.GetBrowserUrlAsync(handle);

        // Assert
        result.Success.Should().BeTrue();
        _worker.Calls.Should().Be(1);
        _worker.LastTargetProcessId.Should().Be(1000);
        _worker.LastGrace.Should().Be(TimeSpan.Zero);
    }

    [Fact]
    public async Task GetBrowserUrl_ReturnsOperationTimedOut_WhenTheReadMissesTimeoutMs()
    {
        // Arrange
        var handle = new nint(12345);
        SetupValidBrowserWindow(handle, "chrome", "chrome");
        var worker = new FailingUiaWorker(new TimeoutException("The UI Automation read did not return."));
        var sut = CreateSut(worker);

        // Act
        var result = await sut.GetBrowserUrlAsync(handle, new CdpOptions(TimeoutMs: 3000));

        // Assert
        result.Success.Should().BeFalse();
        result.ErrorCode.Should().Be(BrowserErrorCode.OperationTimedOut);
        result.Error.Should().Contain("3000 ms").And.Contain("did not return");
        result.Source.Should().Be("uia");
        result.BrowserType.Should().Be("chrome");
        result.PageTitle.Should().Be("Test Page - chrome");
    }

    [Fact]
    public async Task GetBrowserUrl_ReturnsOperationTimedOut_WhenTheReadStopsAtItsDeadline()
    {
        var handle = new nint(12345);
        SetupValidBrowserWindow(handle, "chrome", "chrome");
        var sut = CreateSut(new FailingUiaWorker(new OperationCanceledException()));

        var result = await sut.GetBrowserUrlAsync(handle);

        result.ErrorCode.Should().Be(BrowserErrorCode.OperationTimedOut);
    }

    [Fact]
    public async Task GetBrowserContent_ReturnsOperationTimedOut_WhenEarlierReadsOfTheBrowserAreStuck()
    {
        // Arrange
        var handle = new nint(12345);
        SetupValidBrowserWindow(handle, "chrome", "chrome");
        var sut = CreateSut(new FailingUiaWorker(new UiaReadRefusedException("Earlier reads of process 1000 are still stuck.")));

        // Act
        var result = await sut.GetBrowserContentAsync(handle);

        // Assert
        result.ErrorCode.Should().Be(BrowserErrorCode.OperationTimedOut);
        result.Error.Should().Be("Earlier reads of process 1000 are still stuck.");
        result.Source.Should().Be("uia");
    }

    [Fact]
    public async Task GetBrowserUrl_ThrowsOperationCanceled_WhenTheCallerCancelsDuringTheRead()
    {
        // Arrange
        var handle = new nint(12345);
        SetupValidBrowserWindow(handle, "chrome", "chrome");
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var sut = CreateSut(new FailingUiaWorker(new TimeoutException("Abandoned.")));

        // Act
        var act = () => sut.GetBrowserUrlAsync(handle, cancellationToken: cancellation.Token);

        // Assert
        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task GetBrowserUrl_ReturnsUrlBarNotFound_WhenTheWindowsElementIsGone()
    {
        var handle = new nint(12345);
        SetupValidBrowserWindow(handle, "chrome", "chrome");
        _tree.FromHandleException = FakeUiaTree.Vanished("window");

        var result = await CreateSut().GetBrowserUrlAsync(handle);

        result.ErrorCode.Should().Be(BrowserErrorCode.UrlBarNotFound);
        result.PageTitle.Should().Be("Test Page - chrome");
    }

    [Fact]
    public async Task GetBrowserUrl_StillReadsTheAddressBar_WhenTheWindowsNameCannotBeRead()
    {
        // Arrange - the name search starts at the window, so only the edit box lookup can find the bar
        var handle = new nint(12345);
        SetupValidBrowserWindow(handle, "chrome", "chrome");
        var window = new FakeUiaNode("Window", "Example - Google Chrome") { ReadException = FakeUiaTree.Vanished("window's name") }
            .With(new FakeUiaNode("ToolBar").With(
                new FakeUiaNode("Edit", "Omnibox") { Bounds = new UiaRect(200, 40, 800, 28), ActionPatterns = ["Value"] }
                    .WithPattern<IUiaValuePattern>(new FakeValuePattern("example.com/path"))));
        _tree.WithWindow(handle, window);

        // Act
        var result = await CreateSut().GetBrowserUrlAsync(handle);

        // Assert
        result.Success.Should().BeTrue();
        result.Url.Should().Be("example.com/path");
        result.PageTitle.Should().Be("Test Page - chrome");
    }

    [Fact]
    public async Task GetBrowserUrlAndContent_RefuseAWindowThePolicyDenies_BeforeAnyRead()
    {
        // Arrange
        var handle = new nint(12345);
        SetupValidBrowserWindow(handle, "chrome", "chrome");
        ShowPage(handle, "https://example.com", "Page text");
        _policy = new TargetPolicy(TargetPolicySettings.Default with { DeniedProcesses = ["chrome"] });
        var sut = CreateSut();

        // Act
        var url = await sut.GetBrowserUrlAsync(handle);
        var content = await sut.GetBrowserContentAsync(handle);

        // Assert
        url.ErrorCode.Should().Be(BrowserErrorCode.TargetDeniedByPolicy);
        url.Error.Should().Contain("'chrome'");
        url.Source.Should().Be("uia");
        content.ErrorCode.Should().Be(BrowserErrorCode.TargetDeniedByPolicy);
        content.Content.Should().BeNull();
        _worker.Calls.Should().Be(0);
        _tree.Reads.Should().BeEmpty();
        _warmup.Verify(w => w.WarmUp(It.IsAny<nint>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task GetBrowserUrl_StillReadsThroughCdp_WhenThePolicyDeniesTheBrowser()
    {
        // The policy covers the UI Automation reads only; CDP reads the debug browser the agent started
        var handle = new nint(12345);
        SetupValidBrowserWindow(handle, "chrome", "chrome");
        SetupCdpMatch(CdpResult<CdpTarget>.Ok(CdpTab));
        _policy = new TargetPolicy(TargetPolicySettings.Default with { DeniedProcesses = ["chrome"] });

        var result = await CreateSut().GetBrowserUrlAsync(handle, new CdpOptions(Port: CdpPort));

        result.Success.Should().BeTrue();
        result.Source.Should().Be("cdp");
    }

    [Fact]
    public async Task GetBrowserContent_ReturnsNotResponding_WhenTheBrowserIsHung()
    {
        // Arrange
        var handle = new nint(12345);
        SetupValidBrowserWindow(handle, "chrome", "chrome");
        ShowPage(handle, "https://example.com", "Page text");
        _mockNativeWrapper.Setup(n => n.IsHungAppWindow(handle)).Returns(true);

        // Act
        var result = await CreateSut().GetBrowserContentAsync(handle);

        // Assert
        result.ErrorCode.Should().Be(BrowserErrorCode.NotResponding);
        result.Error.Should().Contain("not responding");
        result.Source.Should().Be("uia");
        _tree.Reads.Should().BeEmpty();
    }

    [Fact]
    public async Task GetBrowserUrl_DoesNotWarmUpThePage()
    {
        // The address bar is the browser's own UI, which Chromium serves without the page's tree
        var handle = new nint(12345);
        SetupValidBrowserWindow(handle, "chrome", "chrome");
        ShowPage(handle, "https://example.com");

        await CreateSut().GetBrowserUrlAsync(handle);

        _warmup.Verify(w => w.WarmUp(It.IsAny<nint>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task GetBrowserContent_WarmsUpThePageBeforeReadingIt_AndReportsIt()
    {
        // Arrange
        var handle = new nint(12345);
        SetupValidBrowserWindow(handle, "chrome", "chrome");
        ShowPage(handle, "https://example.com", "Page text");
        IReadOnlyList<string>? readsBeforeWarmup = null;
        _warmup.Setup(w => w.WarmUp(handle, It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
            .Callback(() => readsBeforeWarmup = _tree.Reads)
            .Returns(Warmed);

        // Act
        var result = await CreateSut().GetBrowserContentAsync(handle, cdp: new CdpOptions(TimeoutMs: 3000));

        // Assert
        result.Success.Should().BeTrue();
        result.Content.Should().Be("Page text");
        result.AccessibilityWarmup.Should().Be(Warmed);
        readsBeforeWarmup.Should().BeEmpty();
        _warmup.Verify(w => w.WarmUp(handle, AccessibilityWarmup.BudgetFor(TimeSpan.FromMilliseconds(3000)), _worker.LastToken), Times.Once);
    }

    [Fact]
    public async Task GetBrowserContent_ReportsTheWarmup_WhenNoContentWasFound()
    {
        var handle = new nint(12345);
        SetupValidBrowserWindow(handle, "chrome", "chrome");
        ShowPage(handle, "https://example.com", content: null);
        var notReady = Warmed with { Status = AccessibilityWarmupStatus.NotReady, DocumentChildCount = 0, Warning = "Read again." };
        _warmup.Setup(w => w.WarmUp(handle, It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>())).Returns(notReady);

        var result = await CreateSut().GetBrowserContentAsync(handle);

        result.ErrorCode.Should().Be(BrowserErrorCode.ContentExtractionFailed);
        result.Url.Should().Be("https://example.com");
        result.AccessibilityWarmup.Should().Be(notReady);
    }

    [Fact]
    public async Task GetBrowserContent_ReportsTheWarmup_WhenTheReadIsAbandonedAtItsDeadline()
    {
        // Arrange - the worker gives up after the warm-up and the read ran
        var handle = new nint(12345);
        SetupValidBrowserWindow(handle, "chrome", "chrome");
        ShowPage(handle, "https://example.com", "Page text");
        _warmup.Setup(w => w.WarmUp(handle, It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>())).Returns(Warmed);

        // Act
        var result = await CreateSut(new AbandoningUiaWorker()).GetBrowserContentAsync(handle);

        // Assert
        result.ErrorCode.Should().Be(BrowserErrorCode.OperationTimedOut);
        result.AccessibilityWarmup.Should().Be(Warmed);
    }

    [Fact]
    public async Task GetBrowserContent_DoesNotWarmUp_WhenUiAutomationIsNotAvailable()
    {
        var handle = new nint(12345);
        SetupValidBrowserWindow(handle, "chrome", "chrome");
        _tree.Available = false;

        var result = await CreateSut().GetBrowserContentAsync(handle);

        result.ErrorCode.Should().Be(BrowserErrorCode.AutomationUnavailable);
        _warmup.Verify(w => w.WarmUp(It.IsAny<nint>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    /// <summary>A worker whose every read fails with <paramref name="exception"/> without running.</summary>
    private sealed class FailingUiaWorker(Exception exception) : IUiaWorker
    {
        public Task<T> RunAsync<T>(int targetProcessId, Func<CancellationToken, T> read, CancellationToken cancellationToken, TimeSpan grace) =>
            Task.FromException<T>(exception);
    }

    /// <summary>A worker that runs the read, then reports it abandoned at its deadline, as a read stuck at its end would be.</summary>
    private sealed class AbandoningUiaWorker : IUiaWorker
    {
        public Task<T> RunAsync<T>(int targetProcessId, Func<CancellationToken, T> read, CancellationToken cancellationToken, TimeSpan grace)
        {
            read(cancellationToken);
            return Task.FromException<T>(new TimeoutException("The UI Automation read did not return."));
        }
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
