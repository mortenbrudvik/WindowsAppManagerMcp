using WindowsAppManagerMcp.Tools;

namespace WindowsAppManagerMcp.Tests.Unit.Tools;

/// <summary>
/// Unit tests for BrowserReadingTools.
/// Tests the MCP tool layer that exposes browser reading functionality.
/// </summary>
public class BrowserReadingToolsTests
{
    private readonly Mock<IBrowserReadingService> _mockBrowserReadingService;
    private readonly Mock<IProcessService> _mockProcessService;

    public BrowserReadingToolsTests()
    {
        _mockBrowserReadingService = new Mock<IBrowserReadingService>();
        _mockProcessService = new Mock<IProcessService>();
    }

    private BrowserReadingTools CreateSut() => new(_mockBrowserReadingService.Object, _mockProcessService.Object);

    #region GetBrowserUrl Tests

    [Fact]
    public async Task GetBrowserUrl_CallsServiceWithCorrectHandle()
    {
        // Arrange
        var handle = 12345L;
        _mockBrowserReadingService.Setup(s => s.GetBrowserUrlAsync(new nint(handle), It.IsAny<CdpOptions?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new BrowserUrlResult(
                Success: true,
                Url: "https://example.com",
                BrowserType: "chrome",
                PageTitle: "Example",
                Error: null,
                ErrorCode: BrowserErrorCode.None));
        var sut = CreateSut();

        // Act
        await sut.GetBrowserUrl(handle);

        // Assert
        _mockBrowserReadingService.Verify(s => s.GetBrowserUrlAsync(new nint(handle), It.IsAny<CdpOptions?>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task GetBrowserUrl_ReturnsMappedDto_OnSuccess()
    {
        // Arrange
        var handle = 12345L;
        _mockBrowserReadingService.Setup(s => s.GetBrowserUrlAsync(new nint(handle), It.IsAny<CdpOptions?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new BrowserUrlResult(
                Success: true,
                Url: "https://example.com",
                BrowserType: "chrome",
                PageTitle: "Example Page",
                Error: null,
                ErrorCode: BrowserErrorCode.None));
        var sut = CreateSut();

        // Act
        var result = await sut.GetBrowserUrl(handle);

        // Assert
        result.Success.Should().BeTrue();
        result.Url.Should().Be("https://example.com");
        result.BrowserType.Should().Be("chrome");
        result.PageTitle.Should().Be("Example Page");
        result.Error.Should().BeNull();
        result.ErrorCode.Should().Be("None");
    }

    [Fact]
    public async Task GetBrowserUrl_ReturnsMappedDto_OnError()
    {
        // Arrange
        var handle = 12345L;
        _mockBrowserReadingService.Setup(s => s.GetBrowserUrlAsync(new nint(handle), It.IsAny<CdpOptions?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new BrowserUrlResult(
                Success: false,
                Url: null,
                BrowserType: null,
                PageTitle: null,
                Error: "Invalid window handle",
                ErrorCode: BrowserErrorCode.InvalidHandle));
        var sut = CreateSut();

        // Act
        var result = await sut.GetBrowserUrl(handle);

        // Assert
        result.Success.Should().BeFalse();
        result.Url.Should().BeNull();
        result.Error.Should().Be("Invalid window handle");
        result.ErrorCode.Should().Be("InvalidHandle");
    }

    #endregion

    #region GetBrowserContent Tests

    [Fact]
    public async Task GetBrowserContent_CallsServiceWithCorrectParameters()
    {
        // Arrange
        var handle = 12345L;
        var maxLength = 1000;
        _mockBrowserReadingService.Setup(s => s.GetBrowserContentAsync(
                new nint(handle),
                It.Is<BrowserContentOptions>(o => o.MaxLength == maxLength && o.TextOnly),
                It.IsAny<CdpOptions?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new BrowserContentResult(
                Success: true,
                Content: "Page content",
                Url: "https://example.com",
                BrowserType: "chrome",
                PageTitle: "Example",
                ContentLength: 12,
                Error: null,
                ErrorCode: BrowserErrorCode.None));
        var sut = CreateSut();

        // Act
        await sut.GetBrowserContent(handle, maxLength, textOnly: true);

        // Assert
        _mockBrowserReadingService.Verify(s => s.GetBrowserContentAsync(
            new nint(handle),
            It.Is<BrowserContentOptions>(o => o.MaxLength == maxLength && o.TextOnly),
            It.IsAny<CdpOptions?>(),
            It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task GetBrowserContent_ReturnsMappedDto_OnSuccess()
    {
        // Arrange
        var handle = 12345L;
        var content = "This is the page content";
        _mockBrowserReadingService.Setup(s => s.GetBrowserContentAsync(
                new nint(handle),
                It.IsAny<BrowserContentOptions>(),
                It.IsAny<CdpOptions?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new BrowserContentResult(
                Success: true,
                Content: content,
                Url: "https://example.com",
                BrowserType: "firefox",
                PageTitle: "Example Page",
                ContentLength: content.Length,
                Error: null,
                ErrorCode: BrowserErrorCode.None));
        var sut = CreateSut();

        // Act
        var result = await sut.GetBrowserContent(handle);

        // Assert
        result.Success.Should().BeTrue();
        result.Content.Should().Be(content);
        result.ContentLength.Should().Be(content.Length);
        result.Url.Should().Be("https://example.com");
        result.BrowserType.Should().Be("firefox");
        result.PageTitle.Should().Be("Example Page");
        result.Error.Should().BeNull();
        result.ErrorCode.Should().Be("None");
    }

    [Fact]
    public async Task GetBrowserContent_ReturnsMappedDto_OnError()
    {
        // Arrange
        var handle = 12345L;
        _mockBrowserReadingService.Setup(s => s.GetBrowserContentAsync(
                new nint(handle),
                It.IsAny<BrowserContentOptions>(),
                It.IsAny<CdpOptions?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new BrowserContentResult(
                Success: false,
                Content: null,
                Url: null,
                BrowserType: "chrome",
                PageTitle: null,
                ContentLength: null,
                Error: "Could not extract content",
                ErrorCode: BrowserErrorCode.ContentExtractionFailed));
        var sut = CreateSut();

        // Act
        var result = await sut.GetBrowserContent(handle);

        // Assert
        result.Success.Should().BeFalse();
        result.Content.Should().BeNull();
        result.ContentLength.Should().BeNull();
        result.Error.Should().Be("Could not extract content");
        result.ErrorCode.Should().Be("ContentExtractionFailed");
    }

    [Fact]
    public async Task GetBrowserContent_DefaultsToNullMaxLength()
    {
        // Arrange
        var handle = 12345L;
        _mockBrowserReadingService.Setup(s => s.GetBrowserContentAsync(
                new nint(handle),
                It.Is<BrowserContentOptions>(o => o.MaxLength == null),
                It.IsAny<CdpOptions?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new BrowserContentResult(
                Success: true,
                Content: "Content",
                Url: null,
                BrowserType: null,
                PageTitle: null,
                ContentLength: 7,
                Error: null,
                ErrorCode: BrowserErrorCode.None));
        var sut = CreateSut();

        // Act
        await sut.GetBrowserContent(handle);

        // Assert
        _mockBrowserReadingService.Verify(s => s.GetBrowserContentAsync(
            new nint(handle),
            It.Is<BrowserContentOptions>(o => o.MaxLength == null),
            It.IsAny<CdpOptions?>(),
            It.IsAny<CancellationToken>()),
            Times.Once);
    }

    #endregion

    #region GetActiveBrowserUrl Tests

    [Fact]
    public async Task GetActiveBrowserUrl_CallsService()
    {
        // Arrange
        _mockBrowserReadingService.Setup(s => s.GetActiveBrowserUrlAsync(It.IsAny<CdpOptions?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new BrowserUrlResult(
                Success: true,
                Url: "https://example.com",
                BrowserType: "edge",
                PageTitle: "Example",
                Error: null,
                ErrorCode: BrowserErrorCode.None));
        var sut = CreateSut();

        // Act
        await sut.GetActiveBrowserUrl();

        // Assert
        _mockBrowserReadingService.Verify(s => s.GetActiveBrowserUrlAsync(It.IsAny<CdpOptions?>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task GetActiveBrowserUrl_ReturnsMappedDto_OnSuccess()
    {
        // Arrange
        _mockBrowserReadingService.Setup(s => s.GetActiveBrowserUrlAsync(It.IsAny<CdpOptions?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new BrowserUrlResult(
                Success: true,
                Url: "https://example.com",
                BrowserType: "edge",
                PageTitle: "Microsoft Edge",
                Error: null,
                ErrorCode: BrowserErrorCode.None));
        var sut = CreateSut();

        // Act
        var result = await sut.GetActiveBrowserUrl();

        // Assert
        result.Success.Should().BeTrue();
        result.Url.Should().Be("https://example.com");
        result.BrowserType.Should().Be("edge");
        result.PageTitle.Should().Be("Microsoft Edge");
        result.ErrorCode.Should().Be("None");
    }

    [Fact]
    public async Task GetActiveBrowserUrl_ReturnsMappedDto_WhenNoBrowser()
    {
        // Arrange
        _mockBrowserReadingService.Setup(s => s.GetActiveBrowserUrlAsync(It.IsAny<CdpOptions?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new BrowserUrlResult(
                Success: false,
                Url: null,
                BrowserType: null,
                PageTitle: null,
                Error: "No browser in foreground",
                ErrorCode: BrowserErrorCode.NoBrowserInForeground));
        var sut = CreateSut();

        // Act
        var result = await sut.GetActiveBrowserUrl();

        // Assert
        result.Success.Should().BeFalse();
        result.Url.Should().BeNull();
        result.Error.Should().Be("No browser in foreground");
        result.ErrorCode.Should().Be("NoBrowserInForeground");
    }

    #endregion

    #region DTO Mapping Tests

    [Theory]
    [InlineData(BrowserErrorCode.None, "None")]
    [InlineData(BrowserErrorCode.InvalidHandle, "InvalidHandle")]
    [InlineData(BrowserErrorCode.NotABrowser, "NotABrowser")]
    [InlineData(BrowserErrorCode.UrlBarNotFound, "UrlBarNotFound")]
    [InlineData(BrowserErrorCode.ContentExtractionFailed, "ContentExtractionFailed")]
    [InlineData(BrowserErrorCode.AutomationUnavailable, "AutomationUnavailable")]
    [InlineData(BrowserErrorCode.NoBrowserInForeground, "NoBrowserInForeground")]
    [InlineData(BrowserErrorCode.OperationException, "OperationException")]
    [InlineData(BrowserErrorCode.CdpTimeout, "CdpTimeout")]
    [InlineData(BrowserErrorCode.CdpDialogOpen, "CdpDialogOpen")]
    [InlineData(BrowserErrorCode.CdpTargetAmbiguous, "CdpTargetAmbiguous")]
    [InlineData(BrowserErrorCode.CdpPortOwnerMismatch, "CdpPortOwnerMismatch")]
    public async Task GetBrowserUrl_MapsErrorCodeToString_Correctly(BrowserErrorCode errorCode, string expectedString)
    {
        // Arrange
        _mockBrowserReadingService.Setup(s => s.GetBrowserUrlAsync(It.IsAny<nint>(), It.IsAny<CdpOptions?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new BrowserUrlResult(
                Success: false,
                Url: null,
                BrowserType: null,
                PageTitle: null,
                Error: "Test error",
                ErrorCode: errorCode));
        var sut = CreateSut();

        // Act
        var result = await sut.GetBrowserUrl(12345);

        // Assert
        result.ErrorCode.Should().Be(expectedString);
    }

    #endregion

    #region CDP Options and Source Tests

    private static readonly BrowserUrlResult AnyUrlResult = new(
        Success: true, Url: "https://example.com", BrowserType: "chrome", PageTitle: "Example", Error: null, ErrorCode: BrowserErrorCode.None);

    private static readonly BrowserContentResult AnyContentResult = new(
        Success: true, Content: "c", Url: "https://example.com", BrowserType: "chrome", PageTitle: "Example",
        ContentLength: 1, Error: null, ErrorCode: BrowserErrorCode.None);

    [Fact]
    public async Task GetBrowserUrl_PassesCdpOptionsAndCancellationToken()
    {
        using var cts = new CancellationTokenSource();
        _mockBrowserReadingService
            .Setup(s => s.GetBrowserUrlAsync(It.IsAny<nint>(), It.IsAny<CdpOptions?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(AnyUrlResult);
        var sut = CreateSut();

        await sut.GetBrowserUrl(12345, cdpPort: 9333, requireCdp: true, timeoutMs: 4000, cancellationToken: cts.Token);

        _mockBrowserReadingService.Verify(s => s.GetBrowserUrlAsync(
            new nint(12345), new CdpOptions(9333, true, 4000), cts.Token), Times.Once);
    }

    [Fact]
    public async Task GetBrowserUrl_DefaultsToNoCdpAndTenSecondTimeout()
    {
        _mockBrowserReadingService
            .Setup(s => s.GetBrowserUrlAsync(It.IsAny<nint>(), It.IsAny<CdpOptions?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(AnyUrlResult);
        var sut = CreateSut();

        await sut.GetBrowserUrl(12345);

        _mockBrowserReadingService.Verify(s => s.GetBrowserUrlAsync(
            new nint(12345), new CdpOptions(null, false, 10000), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task GetBrowserContent_PassesCdpOptionsAndCancellationToken()
    {
        using var cts = new CancellationTokenSource();
        _mockBrowserReadingService
            .Setup(s => s.GetBrowserContentAsync(
                It.IsAny<nint>(), It.IsAny<BrowserContentOptions?>(), It.IsAny<CdpOptions?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(AnyContentResult);
        var sut = CreateSut();

        await sut.GetBrowserContent(12345, maxLength: 10, textOnly: false, cdpPort: 9222, requireCdp: true, timeoutMs: 2500, cancellationToken: cts.Token);

        _mockBrowserReadingService.Verify(s => s.GetBrowserContentAsync(
            new nint(12345), new BrowserContentOptions(10, false), new CdpOptions(9222, true, 2500), cts.Token), Times.Once);
    }

    [Fact]
    public async Task GetActiveBrowserUrl_PassesCdpOptionsAndCancellationToken()
    {
        using var cts = new CancellationTokenSource();
        _mockBrowserReadingService
            .Setup(s => s.GetActiveBrowserUrlAsync(It.IsAny<CdpOptions?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(AnyUrlResult);
        var sut = CreateSut();

        await sut.GetActiveBrowserUrl(cdpPort: 9222, requireCdp: false, timeoutMs: 1500, cancellationToken: cts.Token);

        _mockBrowserReadingService.Verify(s => s.GetActiveBrowserUrlAsync(
            new CdpOptions(9222, false, 1500), cts.Token), Times.Once);
    }

    [Fact]
    public async Task GetBrowserUrl_MapsSourceTargetIdAndCdpError()
    {
        _mockBrowserReadingService
            .Setup(s => s.GetBrowserUrlAsync(It.IsAny<nint>(), It.IsAny<CdpOptions?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(AnyUrlResult with { Source = "uia", TargetId = null, CdpError = "CdpTimeout: no answer" });
        var sut = CreateSut();

        var result = await sut.GetBrowserUrl(12345, cdpPort: 9222);

        result.Source.Should().Be("uia");
        result.TargetId.Should().BeNull();
        result.CdpError.Should().Be("CdpTimeout: no answer");
    }

    [Fact]
    public async Task GetBrowserContent_MapsSourceTargetIdAndCdpError()
    {
        _mockBrowserReadingService
            .Setup(s => s.GetBrowserContentAsync(
                It.IsAny<nint>(), It.IsAny<BrowserContentOptions?>(), It.IsAny<CdpOptions?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(AnyContentResult with { Source = "cdp", TargetId = "TAB1" });
        var sut = CreateSut();

        var result = await sut.GetBrowserContent(12345, cdpPort: 9222);

        result.Source.Should().Be("cdp");
        result.TargetId.Should().Be("TAB1");
        result.CdpError.Should().BeNull();
    }

    #endregion

    #region LaunchBrowserWithDebug Tests

    [Fact]
    public void LaunchBrowserWithDebug_CallsServiceWithDefaultParameters()
    {
        // Arrange
        _mockProcessService.Setup(s => s.LaunchBrowserWithDebug(
                "chrome", null, 9222, null, true, 10000))
            .Returns(new DebugBrowserLaunchResult(
                Success: true,
                ProcessId: 1234,
                WindowHandle: new nint(5678),
                CdpPort: 9222,
                UserDataDir: @"C:\Temp\browser-debug-test",
                Error: null,
                ErrorCode: null));
        var sut = CreateSut();

        // Act
        sut.LaunchBrowserWithDebug();

        // Assert
        _mockProcessService.Verify(s => s.LaunchBrowserWithDebug(
            "chrome", null, 9222, null, true, 10000), Times.Once);
    }

    [Fact]
    public void LaunchBrowserWithDebug_CallsServiceWithCustomParameters()
    {
        // Arrange
        var browser = "edge";
        var url = "https://example.com";
        var port = 9224;
        var userDataDir = @"C:\CustomProfile";

        _mockProcessService.Setup(s => s.LaunchBrowserWithDebug(
                browser, url, port, userDataDir, true, 10000))
            .Returns(new DebugBrowserLaunchResult(
                Success: true,
                ProcessId: 1234,
                WindowHandle: new nint(5678),
                CdpPort: port,
                UserDataDir: userDataDir,
                Error: null,
                ErrorCode: null));
        var sut = CreateSut();

        // Act
        sut.LaunchBrowserWithDebug(browser, url, port, userDataDir);

        // Assert
        _mockProcessService.Verify(s => s.LaunchBrowserWithDebug(
            browser, url, port, userDataDir, true, 10000), Times.Once);
    }

    [Fact]
    public void LaunchBrowserWithDebug_ReturnsMappedDto_OnSuccess()
    {
        // Arrange
        var processId = 1234;
        var windowHandle = new nint(5678);
        var cdpPort = 9222;
        var userDataDir = @"C:\Temp\browser-debug-abc123";

        _mockProcessService.Setup(s => s.LaunchBrowserWithDebug(
                It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<int>(), It.IsAny<string?>(), It.IsAny<bool>(), It.IsAny<int>()))
            .Returns(new DebugBrowserLaunchResult(
                Success: true,
                ProcessId: processId,
                WindowHandle: windowHandle,
                CdpPort: cdpPort,
                UserDataDir: userDataDir,
                Error: null,
                ErrorCode: null));
        var sut = CreateSut();

        // Act
        var result = sut.LaunchBrowserWithDebug();

        // Assert
        result.Success.Should().BeTrue();
        result.ProcessId.Should().Be(processId);
        result.WindowHandle.Should().Be(windowHandle.ToInt64());
        result.CdpPort.Should().Be(cdpPort);
        result.UserDataDir.Should().Be(userDataDir);
        result.Error.Should().BeNull();
        result.ErrorCode.Should().BeNull();
    }

    [Fact]
    public void LaunchBrowserWithDebug_ReturnsMappedDto_OnUnsupportedBrowser()
    {
        // Arrange
        _mockProcessService.Setup(s => s.LaunchBrowserWithDebug(
                It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<int>(), It.IsAny<string?>(), It.IsAny<bool>(), It.IsAny<int>()))
            .Returns(new DebugBrowserLaunchResult(
                Success: false,
                ProcessId: null,
                WindowHandle: null,
                CdpPort: null,
                UserDataDir: null,
                Error: "Unsupported browser 'firefox'. Supported browsers: chrome, edge, brave.",
                ErrorCode: "UnsupportedBrowser"));
        var sut = CreateSut();

        // Act
        var result = sut.LaunchBrowserWithDebug("firefox");

        // Assert
        result.Success.Should().BeFalse();
        result.ProcessId.Should().BeNull();
        result.WindowHandle.Should().BeNull();
        result.CdpPort.Should().BeNull();
        result.UserDataDir.Should().BeNull();
        result.Error.Should().Contain("Unsupported browser");
        result.ErrorCode.Should().Be("UnsupportedBrowser");
    }

    [Fact]
    public void LaunchBrowserWithDebug_ReturnsMappedDto_OnBrowserNotFound()
    {
        // Arrange
        _mockProcessService.Setup(s => s.LaunchBrowserWithDebug(
                It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<int>(), It.IsAny<string?>(), It.IsAny<bool>(), It.IsAny<int>()))
            .Returns(new DebugBrowserLaunchResult(
                Success: false,
                ProcessId: null,
                WindowHandle: null,
                CdpPort: null,
                UserDataDir: null,
                Error: "Browser 'chrome' not found. Please ensure it is installed.",
                ErrorCode: "BrowserNotFound"));
        var sut = CreateSut();

        // Act
        var result = sut.LaunchBrowserWithDebug("chrome");

        // Assert
        result.Success.Should().BeFalse();
        result.Error.Should().Contain("not found");
        result.ErrorCode.Should().Be("BrowserNotFound");
    }

    [Fact]
    public void LaunchBrowserWithDebug_ReturnsMappedDto_OnPortInUse()
    {
        // Arrange
        _mockProcessService.Setup(s => s.LaunchBrowserWithDebug(
                It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<int>(), It.IsAny<string?>(), It.IsAny<bool>(), It.IsAny<int>()))
            .Returns(new DebugBrowserLaunchResult(
                Success: false,
                ProcessId: null,
                WindowHandle: null,
                CdpPort: null,
                UserDataDir: null,
                Error: "Port 9222 is already in use. Choose a different port or close the application using it.",
                ErrorCode: "PortInUse"));
        var sut = CreateSut();

        // Act
        var result = sut.LaunchBrowserWithDebug(port: 9222);

        // Assert
        result.Success.Should().BeFalse();
        result.Error.Should().Contain("already in use");
        result.ErrorCode.Should().Be("PortInUse");
    }

    [Fact]
    public void LaunchBrowserWithDebug_ReturnsMappedDto_OnDebugLaunchFailed()
    {
        // Arrange
        _mockProcessService.Setup(s => s.LaunchBrowserWithDebug(
                It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<int>(), It.IsAny<string?>(), It.IsAny<bool>(), It.IsAny<int>()))
            .Returns(new DebugBrowserLaunchResult(
                Success: false,
                ProcessId: null,
                WindowHandle: null,
                CdpPort: null,
                UserDataDir: null,
                Error: "Failed to start process",
                ErrorCode: "DebugLaunchFailed"));
        var sut = CreateSut();

        // Act
        var result = sut.LaunchBrowserWithDebug();

        // Assert
        result.Success.Should().BeFalse();
        result.Error.Should().Be("Failed to start process");
        result.ErrorCode.Should().Be("DebugLaunchFailed");
    }

    [Theory]
    [InlineData("chrome")]
    [InlineData("edge")]
    [InlineData("brave")]
    public void LaunchBrowserWithDebug_AcceptsSupportedBrowsers(string browser)
    {
        // Arrange
        _mockProcessService.Setup(s => s.LaunchBrowserWithDebug(
                browser, It.IsAny<string?>(), It.IsAny<int>(), It.IsAny<string?>(), It.IsAny<bool>(), It.IsAny<int>()))
            .Returns(new DebugBrowserLaunchResult(
                Success: true,
                ProcessId: 1234,
                WindowHandle: new nint(5678),
                CdpPort: 9222,
                UserDataDir: @"C:\Temp\test",
                Error: null,
                ErrorCode: null));
        var sut = CreateSut();

        // Act
        var result = sut.LaunchBrowserWithDebug(browser);

        // Assert
        result.Success.Should().BeTrue();
        _mockProcessService.Verify(s => s.LaunchBrowserWithDebug(
            browser, It.IsAny<string?>(), It.IsAny<int>(), It.IsAny<string?>(), It.IsAny<bool>(), It.IsAny<int>()), Times.Once);
    }

    [Fact]
    public void LaunchBrowserWithDebug_PassesUrlToService()
    {
        // Arrange
        var url = "https://github.com";
        _mockProcessService.Setup(s => s.LaunchBrowserWithDebug(
                It.IsAny<string>(), url, It.IsAny<int>(), It.IsAny<string?>(), It.IsAny<bool>(), It.IsAny<int>()))
            .Returns(new DebugBrowserLaunchResult(
                Success: true,
                ProcessId: 1234,
                WindowHandle: new nint(5678),
                CdpPort: 9222,
                UserDataDir: @"C:\Temp\test",
                Error: null,
                ErrorCode: null));
        var sut = CreateSut();

        // Act
        sut.LaunchBrowserWithDebug(url: url);

        // Assert
        _mockProcessService.Verify(s => s.LaunchBrowserWithDebug(
            It.IsAny<string>(), url, It.IsAny<int>(), It.IsAny<string?>(), It.IsAny<bool>(), It.IsAny<int>()), Times.Once);
    }

    [Fact]
    public void LaunchBrowserWithDebug_PassesCustomPortToService()
    {
        // Arrange
        var customPort = 9999;
        _mockProcessService.Setup(s => s.LaunchBrowserWithDebug(
                It.IsAny<string>(), It.IsAny<string?>(), customPort, It.IsAny<string?>(), It.IsAny<bool>(), It.IsAny<int>()))
            .Returns(new DebugBrowserLaunchResult(
                Success: true,
                ProcessId: 1234,
                WindowHandle: new nint(5678),
                CdpPort: customPort,
                UserDataDir: @"C:\Temp\test",
                Error: null,
                ErrorCode: null));
        var sut = CreateSut();

        // Act
        var result = sut.LaunchBrowserWithDebug(port: customPort);

        // Assert
        result.CdpPort.Should().Be(customPort);
        _mockProcessService.Verify(s => s.LaunchBrowserWithDebug(
            It.IsAny<string>(), It.IsAny<string?>(), customPort, It.IsAny<string?>(), It.IsAny<bool>(), It.IsAny<int>()), Times.Once);
    }

    [Fact]
    public void LaunchBrowserWithDebug_PassesUserDataDirToService()
    {
        // Arrange
        var customDir = @"D:\BrowserProfiles\Debug";
        _mockProcessService.Setup(s => s.LaunchBrowserWithDebug(
                It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<int>(), customDir, It.IsAny<bool>(), It.IsAny<int>()))
            .Returns(new DebugBrowserLaunchResult(
                Success: true,
                ProcessId: 1234,
                WindowHandle: new nint(5678),
                CdpPort: 9222,
                UserDataDir: customDir,
                Error: null,
                ErrorCode: null));
        var sut = CreateSut();

        // Act
        var result = sut.LaunchBrowserWithDebug(userDataDir: customDir);

        // Assert
        result.UserDataDir.Should().Be(customDir);
        _mockProcessService.Verify(s => s.LaunchBrowserWithDebug(
            It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<int>(), customDir, It.IsAny<bool>(), It.IsAny<int>()), Times.Once);
    }

    #endregion
}
