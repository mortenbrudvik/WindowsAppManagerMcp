using WindowsAppManagerMcp.Infrastructure;
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
        await sut.GetBrowserContent(handle, maxLength: maxLength, textOnly: true);

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
                It.IsAny<nint>(), It.IsAny<BrowserContentOptions?>(), It.IsAny<CdpOptions?>(),
                It.IsAny<CancellationToken>()))
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
                It.IsAny<nint>(), It.IsAny<BrowserContentOptions?>(), It.IsAny<CdpOptions?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(AnyContentResult with { Source = "cdp", TargetId = "TAB1" });
        var sut = CreateSut();

        var result = await sut.GetBrowserContent(12345, cdpPort: 9222);

        result.Source.Should().Be("cdp");
        result.TargetId.Should().Be("TAB1");
        result.CdpError.Should().BeNull();
        result.AccessibilityWarmup.Should().BeNull();
    }

    [Fact]
    public async Task GetBrowserContent_MapsTheAccessibilityWarmup()
    {
        // Arrange - the UI Automation read waited for the page's tree first (roadmap M3.F24)
        var warmup = new AccessibilityWarmupInfo(AccessibilityWarmupStatus.Partial, WebContentHost.Chromium, 1500, 6, 40, "Read again.");
        _mockBrowserReadingService
            .Setup(s => s.GetBrowserContentAsync(
                It.IsAny<nint>(), It.IsAny<BrowserContentOptions?>(), It.IsAny<CdpOptions?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(AnyContentResult with { Source = "uia", AccessibilityWarmup = warmup });
        var sut = CreateSut();

        // Act
        var result = await sut.GetBrowserContent(12345);

        // Assert
        result.AccessibilityWarmup.Should().Be(warmup);
    }

    #endregion

    #region LaunchBrowserWithDebug Tests

    /// <summary>The service returns <paramref name="result"/> and records the request it got.</summary>
    private List<DebugBrowserLaunchRequest> LaunchReturns(DebugBrowserLaunchResult result)
    {
        var requests = new List<DebugBrowserLaunchRequest>();
        _mockProcessService.Setup(s => s.LaunchBrowserWithDebugAsync(It.IsAny<DebugBrowserLaunchRequest>(), It.IsAny<CancellationToken>()))
            .Callback<DebugBrowserLaunchRequest, CancellationToken>((request, _) => requests.Add(request))
            .ReturnsAsync(result);
        return requests;
    }

    private static readonly DebugBrowserLaunchResult Launched = new(
        Success: true,
        ProcessId: 1234,
        WindowHandle: new nint(5678),
        CdpPort: 50123,
        UserDataDir: @"C:\Temp\browser-debug-test",
        Error: null,
        ErrorCode: null,
        LaunchStatus: LaunchStatus.WindowObserved);

    [Fact]
    public async Task LaunchBrowserWithDebug_CallsServiceWithDefaultParameters()
    {
        // Arrange
        var requests = LaunchReturns(Launched);
        var sut = CreateSut();

        // Act
        await sut.LaunchBrowserWithDebug();

        // Assert - port 0 by default: the browser picks a free port (roadmap M5.1)
        requests.Should().Equal(new DebugBrowserLaunchRequest("chrome", Url: null, Port: 0, UserDataDir: null, ExecutablePath: null));
    }

    [Fact]
    public async Task LaunchBrowserWithDebug_CallsServiceWithCustomParameters()
    {
        // Arrange
        var requests = LaunchReturns(Launched);
        var sut = CreateSut();

        // Act
        await sut.LaunchBrowserWithDebug(DebugBrowser.Edge, "https://example.com", 9224, @"C:\CustomProfile",
            @"C:\Edge Beta\Application\msedge.exe");

        // Assert
        requests.Should().Equal(new DebugBrowserLaunchRequest(
            "edge", "https://example.com", 9224, @"C:\CustomProfile", @"C:\Edge Beta\Application\msedge.exe"));
    }

    [Theory]
    [InlineData(DebugBrowser.Chrome, "chrome")]
    [InlineData(DebugBrowser.Edge, "edge")]
    [InlineData(DebugBrowser.Brave, "brave")]
    public async Task LaunchBrowserWithDebug_PassesTheBrowsersNameToTheService(DebugBrowser requested, string browser)
    {
        // Arrange
        var requests = LaunchReturns(Launched);
        var sut = CreateSut();

        // Act
        var result = await sut.LaunchBrowserWithDebug(requested);

        // Assert
        result.Success.Should().BeTrue();
        requests.Should().ContainSingle().Which.Browser.Should().Be(browser);
    }

    [Fact]
    public async Task LaunchBrowserWithDebug_ReturnsMappedDto_OnSuccess()
    {
        // Arrange
        LaunchReturns(Launched with { LaunchStatus = LaunchStatus.TimedOut, Warning = "No window appeared within 10 seconds." });
        var sut = CreateSut();

        // Act
        var result = await sut.LaunchBrowserWithDebug();

        // Assert - the port the browser reported, and the launch's status and warning (roadmap M5.1)
        result.Success.Should().BeTrue();
        result.ProcessId.Should().Be(1234);
        result.WindowHandle.Should().Be(5678);
        result.CdpPort.Should().Be(50123);
        result.UserDataDir.Should().Be(@"C:\Temp\browser-debug-test");
        result.LaunchStatus.Should().Be("timedOut");
        result.Warning.Should().Be("No window appeared within 10 seconds.");
        result.Error.Should().BeNull();
        result.ErrorCode.Should().BeNull();
    }

    [Theory]
    [InlineData("BrowserNotFound", @"Browser 'chrome' was not found. Tried: App Paths\chrome.exe; C:\Program Files\Google\Chrome\Application\chrome.exe.")]
    [InlineData("PortInUse", "Port 9222 is already in use. Nothing was launched.")]
    [InlineData("DebugLaunchFailed", "Failed to start process")]
    public async Task LaunchBrowserWithDebug_ReturnsMappedDto_OnFailure(string errorCode, string error)
    {
        // Arrange
        LaunchReturns(new DebugBrowserLaunchResult(false, null, null, null, null, error, errorCode));
        var sut = CreateSut();

        // Act
        var result = await sut.LaunchBrowserWithDebug(port: 9222);

        // Assert
        result.Success.Should().BeFalse();
        result.Error.Should().Be(error);
        result.ErrorCode.Should().Be(errorCode);
        result.LaunchStatus.Should().BeNull("nothing was started");
    }

    [Fact]
    public async Task LaunchBrowserWithDebug_AHandoff_KeepsItsStatusInTheFailure()
    {
        // Arrange - the request went to a browser already running with the profile (roadmap M5.1)
        LaunchReturns(new DebugBrowserLaunchResult(false, null, null, null, @"C:\Profiles\Debug", "The chrome process that was started exited",
            nameof(BrowserErrorCode.DebugLaunchFailed), LaunchStatus: LaunchStatus.ExitedWithoutWindow));
        var sut = CreateSut();

        // Act
        var result = await sut.LaunchBrowserWithDebug(userDataDir: @"C:\Profiles\Debug");

        // Assert
        result.Success.Should().BeFalse();
        result.ErrorCode.Should().Be("DebugLaunchFailed");
        result.LaunchStatus.Should().Be("exitedWithoutWindow");
        result.UserDataDir.Should().Be(@"C:\Profiles\Debug");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1024)]
    [InlineData(65535)]
    public async Task LaunchBrowserWithDebug_TakesPortZeroAndExplicitPortsFrom1024(int port)
    {
        // Arrange
        var requests = LaunchReturns(Launched);
        var sut = CreateSut();

        // Act
        await sut.LaunchBrowserWithDebug(port: port);

        // Assert
        requests.Should().ContainSingle().Which.Port.Should().Be(port);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(1)]
    [InlineData(80)]
    [InlineData(1023)]
    [InlineData(65536)]
    public async Task LaunchBrowserWithDebug_RefusesOtherPorts_AndLaunchesNothing(int port)
    {
        // Arrange
        var sut = CreateSut();

        // Act
        var act = () => sut.LaunchBrowserWithDebug(port: port);

        // Assert - below 1024 are the well-known service ports (roadmap M5.1)
        (await act.Should().ThrowAsync<ToolErrorException>()).Which.Message.Should()
            .Contain("port must be 0 (the browser picks a free port) or a TCP port from 1024 to 65535").And.Contain("Nothing was launched");
        _mockProcessService.VerifyNoOtherCalls();
    }

    [Theory]
    [InlineData("chrome", @"C:\Program Files\Google\Chrome Beta\Application\chrome.exe")]
    [InlineData("edge", @"D:\Portable\Edge\MSEDGE.EXE")]
    [InlineData("brave", @"\\server\share\Brave\brave.exe")]
    [InlineData("chrome", null)]
    [InlineData("chrome", "  ")]
    public void DebugExecutableRefusal_TakesAFullPathToTheBrowsersOwnExe(string browser, string? executablePath)
    {
        BrowserReadingTools.DebugExecutableRefusal(browser, executablePath).Should().BeNull();
    }

    [Theory]
    [InlineData("chrome", "chrome.exe", "is not a full path")]
    [InlineData("chrome", @"Google\Chrome\Application\chrome.exe", "is not a full path")]
    [InlineData("chrome", @"C:chrome.exe", "is not a full path")]
    [InlineData("edge", @"C:\Program Files\Google\Chrome\Application\chrome.exe", "must name msedge.exe for browser 'edge'")]
    [InlineData("chrome", @"C:\Windows\System32\cmd.exe", "must name chrome.exe")]
    [InlineData("brave", @"C:\Tools\brave.exe.lnk", "must name brave.exe")]
    public void DebugExecutableRefusal_RefusesARelativePathAndAnotherProgram(string browser, string executablePath, string message)
    {
        BrowserReadingTools.DebugExecutableRefusal(browser, executablePath).Should().Contain(message).And.Contain("Nothing was launched");
    }

    #endregion
}
