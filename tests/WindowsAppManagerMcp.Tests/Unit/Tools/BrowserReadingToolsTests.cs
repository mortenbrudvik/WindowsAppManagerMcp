using System.ComponentModel;
using System.Reflection;
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
    public async Task GetBrowserUrl_PassesOnWhereTheUiAutomationReadFoundTheUrl()
    {
        // Arrange - roadmap M5.7
        _mockBrowserReadingService.Setup(s => s.GetBrowserUrlAsync(new nint(12345), It.IsAny<CdpOptions?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new BrowserUrlResult(true, "https://example.com/a", "edge", "A - Microsoft Edge", null, BrowserErrorCode.None,
                Source: BrowserReadSource.Uia, DisplayUrl: "example.com/a", UrlSource: BrowserUrlSource.Document, IsEditing: false));

        // Act
        var result = await CreateSut().GetBrowserUrl(12345);

        // Assert
        result.DisplayUrl.Should().Be("example.com/a");
        result.UrlSource.Should().Be("document");
        result.IsEditing.Should().BeFalse();
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
    public async Task GetBrowserContent_DefaultsToTheFirstPageOf20000Characters_OfTheWholePage()
    {
        // Arrange
        var handle = 12345L;
        _mockBrowserReadingService.Setup(s => s.GetBrowserContentAsync(
                new nint(handle),
                It.Is<BrowserContentOptions>(o => o.MaxLength == 20_000 && o.Offset == 0 && o.Selector == null && o.TextOnly),
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
            It.Is<BrowserContentOptions>(o => o.MaxLength == 20_000 && o.Offset == 0 && o.Selector == null && o.TextOnly),
            It.IsAny<CdpOptions?>(),
            It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task GetBrowserContent_PassesOffsetAndSelector_AndClampsMaxLengthToTheLongestPage()
    {
        // Arrange
        _mockBrowserReadingService
            .Setup(s => s.GetBrowserContentAsync(It.IsAny<nint>(), It.IsAny<BrowserContentOptions?>(), It.IsAny<CdpOptions?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new BrowserContentResult(true, "page", "u", "chrome", "t", 4, null, BrowserErrorCode.None, BrowserReadSource.Cdp,
                Offset: 40_000, TotalLength: 90_000, Truncated: true, NextOffset: 40_004));
        var sut = CreateSut();

        // Act
        var result = await sut.GetBrowserContent(12345, maxLength: 1_000_000, offset: 40_000, selector: "main", cdpPort: 9222);

        // Assert
        _mockBrowserReadingService.Verify(s => s.GetBrowserContentAsync(
            new nint(12345), new BrowserContentOptions(50_000, true, 40_000, "main"), It.IsAny<CdpOptions?>(), It.IsAny<CancellationToken>()),
            Times.Once);
        result.Should().BeEquivalentTo(new { Offset = 40_000, TotalLength = 90_000, Truncated = true, NextOffset = 40_004, ContentLength = 4 });
    }

    [Fact]
    public async Task GetBrowserContent_ReturnsTextSkipped_ForACutPage()
    {
        _mockBrowserReadingService
            .Setup(s => s.GetBrowserContentAsync(It.IsAny<nint>(), It.IsAny<BrowserContentOptions?>(), It.IsAny<CdpOptions?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new BrowserContentResult(true, "\r", "u", "chrome", "t", 1, null, BrowserErrorCode.None, BrowserReadSource.Uia,
                Offset: 0, Truncated: true, NextOffset: 1, TextSkipped: true));
        var sut = CreateSut();

        var result = await sut.GetBrowserContent(12345, maxLength: 1);

        result.TextSkipped.Should().BeTrue();
    }

    [Theory]
    [InlineData(-1, null, null, true, "offset must be 0 or more")]
    [InlineData(0, "  ", 9222, true, "selector must be a CSS selector")]
    [InlineData(0, "main", null, false, "useCdp=false reads through UI Automation only, so selector cannot be used")]
    public async Task GetBrowserContent_RefusesABadOffsetOrSelector_BeforeAnythingIsRead(
        int offset, string? selector, int? cdpPort, bool useCdp, string message)
    {
        var sut = CreateSut();

        var act = () => sut.GetBrowserContent(12345, offset: offset, selector: selector, cdpPort: cdpPort, useCdp: useCdp);

        (await act.Should().ThrowAsync<ToolErrorException>()).Which.Message.Should().Contain(message).And.Contain("Nothing was read");
        _mockBrowserReadingService.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task GetBrowserContent_TakesASelectorWithoutCdpPort_AndAUiaPagesOffsetWithUseCdpFalse()
    {
        // Roadmap M5.3: the selector is read through the window's own debug port; a 'uia' page continues with useCdp=false
        _mockBrowserReadingService
            .Setup(s => s.GetBrowserContentAsync(It.IsAny<nint>(), It.IsAny<BrowserContentOptions?>(), It.IsAny<CdpOptions?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(AnyContentResult);
        var sut = CreateSut();

        await sut.GetBrowserContent(12345, selector: "main");
        await sut.GetBrowserContent(12345, offset: 20_000, useCdp: false);

        _mockBrowserReadingService.Verify(s => s.GetBrowserContentAsync(
            new nint(12345), new BrowserContentOptions(20_000, true, 0, "main"), new CdpOptions(null, false, 10000, null, true),
            It.IsAny<CancellationToken>()), Times.Once);
        _mockBrowserReadingService.Verify(s => s.GetBrowserContentAsync(
            new nint(12345), new BrowserContentOptions(20_000, true, 20_000, null), new CdpOptions(null, false, 10000, null, false),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public void GetBrowserContent_DescribesHowEachSourcesPageContinues()
    {
        var parameters = typeof(BrowserReadingTools).GetMethod(nameof(BrowserReadingTools.GetBrowserContent))!.GetParameters();
        string Describe(string name) => parameters.Single(p => p.Name == name).GetCustomAttribute<DescriptionAttribute>()!.Description;

        Describe("offset").Should().Contain("'uia' page continues with useCdp=false").And.NotContain("without cdpPort");
        Describe("selector").Should().NotContain("cdpPort");
    }

    #endregion

    #region get_browser_url with a bare foreground selector (roadmap M6.1)

    [Fact]
    public async Task GetBrowserUrl_WithABareForegroundSelector_ReadsTheActiveBrowser()
    {
        // Arrange
        CdpOptions? passed = null;
        _mockBrowserReadingService.Setup(s => s.GetActiveBrowserUrlAsync(It.IsAny<CdpOptions?>(), It.IsAny<CancellationToken>()))
            .Callback((CdpOptions? cdp, CancellationToken _) => passed = cdp)
            .ReturnsAsync(new BrowserUrlResult(true, "https://example.com", "chrome", "Example", null, BrowserErrorCode.None));
        var windows = new Mock<IWindowService>(MockBehavior.Strict);
        var sut = new BrowserReadingTools(_mockBrowserReadingService.Object, _mockProcessService.Object, windows.Object);

        // Act
        var result = await sut.GetBrowserUrl(window: new WindowSelector { Foreground = true }, cdpPort: 9222, requireCdp: true, timeoutMs: 4000);

        // Assert - the active-browser service call, with the CDP options; no selector resolution
        result.Success.Should().BeTrue();
        result.Url.Should().Be("https://example.com");
        passed.Should().Be(new CdpOptions(9222, true, 4000, null, true));
        _mockBrowserReadingService.Verify(s => s.GetBrowserUrlAsync(It.IsAny<nint>(), It.IsAny<CdpOptions?>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task GetBrowserUrl_WithABareForegroundSelector_WhenTheWindowInFrontIsNoBrowser_IsNoBrowserInForeground()
    {
        // Arrange - the agent's terminal is in front
        _mockBrowserReadingService.Setup(s => s.GetActiveBrowserUrlAsync(It.IsAny<CdpOptions?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new BrowserUrlResult(false, null, null, null, "Foreground window 'WindowsTerminal' is not a browser",
                BrowserErrorCode.NoBrowserInForeground));

        // Act
        var result = await CreateSut().GetBrowserUrl(window: new WindowSelector { Foreground = true });

        // Assert - not NotABrowser, which names a window the agent chose
        result.Success.Should().BeFalse();
        result.ErrorCode.Should().Be("NoBrowserInForeground");
    }

    [Fact]
    public async Task GetBrowserUrl_WithAForegroundSelectorAndAFilter_ResolvesTheWindowAsBefore()
    {
        // Arrange - foreground plus processName checks the window in front, through the selector
        var browser = new nint(0x5150);
        var windows = new Mock<IWindowService>();
        windows.Setup(w => w.ResolveWindow(It.IsAny<WindowSelector>()))
            .Returns(WindowResolution.Found(TestDataFactory.CreateWindowInfo(handleValue: 0x5150, processName: "chrome")));
        _mockBrowserReadingService.Setup(s => s.GetBrowserUrlAsync(browser, It.IsAny<CdpOptions?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new BrowserUrlResult(true, "https://example.com", "chrome", "Example", null, BrowserErrorCode.None));
        var sut = new BrowserReadingTools(_mockBrowserReadingService.Object, _mockProcessService.Object, windows.Object);

        // Act
        var result = await sut.GetBrowserUrl(window: new WindowSelector { Foreground = true, ProcessName = "chrome" });

        // Assert
        result.Success.Should().BeTrue();
        _mockBrowserReadingService.Verify(s => s.GetActiveBrowserUrlAsync(It.IsAny<CdpOptions?>(), It.IsAny<CancellationToken>()), Times.Never);
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
            new nint(12345), new CdpOptions(9333, true, 4000, null, true), cts.Token), Times.Once);
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
            new nint(12345), new CdpOptions(null, false, 10000, null, true), It.IsAny<CancellationToken>()), Times.Once);
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
            new nint(12345), new BrowserContentOptions(10, false, 0, null), new CdpOptions(9222, true, 2500, null, true), cts.Token), Times.Once);
    }

    [Fact]
    public async Task GetBrowserUrl_PassesTargetIdThrough()
    {
        _mockBrowserReadingService
            .Setup(s => s.GetBrowserUrlAsync(It.IsAny<nint>(), It.IsAny<CdpOptions?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(AnyUrlResult);
        var sut = CreateSut();

        await sut.GetBrowserUrl(12345, cdpPort: 9222, targetId: "8C1F0A6E2B7D4F3A");

        _mockBrowserReadingService.Verify(s => s.GetBrowserUrlAsync(
            new nint(12345), new CdpOptions(9222, false, 10000, "8C1F0A6E2B7D4F3A", true), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task GetBrowserContent_PassesTargetIdThrough()
    {
        _mockBrowserReadingService
            .Setup(s => s.GetBrowserContentAsync(
                It.IsAny<nint>(), It.IsAny<BrowserContentOptions?>(), It.IsAny<CdpOptions?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(AnyContentResult);
        var sut = CreateSut();

        await sut.GetBrowserContent(12345, cdpPort: 9222, targetId: "8C1F0A6E2B7D4F3A");

        _mockBrowserReadingService.Verify(s => s.GetBrowserContentAsync(
            new nint(12345), It.IsAny<BrowserContentOptions?>(), new CdpOptions(9222, false, 10000, "8C1F0A6E2B7D4F3A", true),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Theory]
    [InlineData(9222, "", "CDP tab id")]
    [InlineData(null, "../browser/1", "CDP tab id")]
    [InlineData(9222, "../browser/1", "CDP tab id")]
    [InlineData(9222, "TAB1?x", "CDP tab id")]
    public async Task GetBrowserUrlAndContent_RefuseABadTargetId_BeforeReading(int? cdpPort, string targetId, string message)
    {
        var sut = CreateSut();

        var url = () => sut.GetBrowserUrl(12345, cdpPort: cdpPort, targetId: targetId);
        var content = () => sut.GetBrowserContent(12345, cdpPort: cdpPort, targetId: targetId);

        (await url.Should().ThrowAsync<ToolErrorException>()).Which.ErrorCode.Should().Be(ToolErrorReporting.InvalidArgumentsErrorCode);
        (await content.Should().ThrowAsync<ToolErrorException>()).Which.Message.Should().Contain(message).And.Contain("Nothing was read");
        _mockBrowserReadingService.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task GetBrowserUrlAndContent_TakeATargetIdWithoutCdpPort()
    {
        // Roadmap M5.3: the port of the window's own browser is found
        _mockBrowserReadingService
            .Setup(s => s.GetBrowserUrlAsync(It.IsAny<nint>(), It.IsAny<CdpOptions?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(AnyUrlResult);
        var sut = CreateSut();

        await sut.GetBrowserUrl(12345, targetId: "8C1F0A6E2B7D4F3A");

        _mockBrowserReadingService.Verify(s => s.GetBrowserUrlAsync(
            new nint(12345), new CdpOptions(null, false, 10000, "8C1F0A6E2B7D4F3A", true), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task GetBrowserUrlAndContent_PassUseCdpFalseThrough()
    {
        _mockBrowserReadingService
            .Setup(s => s.GetBrowserUrlAsync(It.IsAny<nint>(), It.IsAny<CdpOptions?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(AnyUrlResult);
        _mockBrowserReadingService
            .Setup(s => s.GetBrowserContentAsync(
                It.IsAny<nint>(), It.IsAny<BrowserContentOptions?>(), It.IsAny<CdpOptions?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(AnyContentResult);
        var sut = CreateSut();

        await sut.GetBrowserUrl(12345, useCdp: false);
        await sut.GetBrowserContent(12345, useCdp: false);

        _mockBrowserReadingService.Invocations.SelectMany(i => i.Arguments).OfType<CdpOptions>()
            .Should().HaveCount(2).And.OnlyContain(o => !o.UseCdp && o.Port == null);
    }

    [Theory]
    [InlineData(9222, null, false, "cdpPort")]
    [InlineData(null, "8C1F0A6E2B7D4F3A", false, "targetId")]
    [InlineData(null, null, true, "requireCdp")]
    public async Task GetBrowserUrlAndContent_RefuseUseCdpFalse_WithAnArgumentThatAsksForCdp(
        int? cdpPort, string? targetId, bool requireCdp, string named)
    {
        var sut = CreateSut();

        var url = () => sut.GetBrowserUrl(12345, cdpPort: cdpPort, targetId: targetId, requireCdp: requireCdp, useCdp: false);
        var content = () => sut.GetBrowserContent(12345, cdpPort: cdpPort, targetId: targetId, requireCdp: requireCdp, useCdp: false);

        (await url.Should().ThrowAsync<ToolErrorException>()).Which.ErrorCode.Should().Be(ToolErrorReporting.InvalidArgumentsErrorCode);
        (await content.Should().ThrowAsync<ToolErrorException>()).Which.Message.Should().Contain(named).And.Contain("Nothing was read");
        _mockBrowserReadingService.VerifyNoOtherCalls();
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
        requests.Should().Equal(new DebugBrowserLaunchRequest(DebugBrowser.Chrome, Url: null, Port: 0, UserDataDir: null, ExecutablePath: null));
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
            DebugBrowser.Edge, "https://example.com", 9224, @"C:\CustomProfile", @"C:\Edge Beta\Application\msedge.exe"));
    }

    [Theory]
    [InlineData(DebugBrowser.Chrome)]
    [InlineData(DebugBrowser.Edge)]
    [InlineData(DebugBrowser.Brave)]
    public async Task LaunchBrowserWithDebug_PassesTheBrowserToTheService(DebugBrowser requested)
    {
        // Arrange
        var requests = LaunchReturns(Launched);
        var sut = CreateSut();

        // Act
        var result = await sut.LaunchBrowserWithDebug(requested);

        // Assert - the enum itself, so a browser the tool takes is one the launch knows (no name to mistype)
        result.Success.Should().BeTrue();
        requests.Should().ContainSingle().Which.Browser.Should().Be(requested);
    }

    [Fact]
    public async Task LaunchBrowserWithDebug_PassesTheUrlWithoutTheWhiteSpaceAroundIt()
    {
        // Arrange
        var requests = LaunchReturns(Launched);

        // Act
        await CreateSut().LaunchBrowserWithDebug(url: "  https://example.com/a  ");

        // Assert - the url that was checked is the one the browser gets
        requests.Should().ContainSingle().Which.Url.Should().Be("https://example.com/a");
    }

    [Theory]
    [InlineData("http://127.0.0.1:9333/", "chrome", true)]
    [InlineData("http://localhost:9333/anything", "msedge", true)]
    [InlineData("http://127.0.0.1:9333/", null, true)]
    [InlineData("http://127.0.0.1:9333/", "dotnet", false)]
    [InlineData("https://example.com:9333/", "chrome", false)]
    public async Task LaunchBrowserWithDebug_ALoopbackUrlWhereABrowserListens_IsRefused_AsBrowserNavigateRefusesIt(
        string url, string? listener, bool refused)
    {
        // Arrange - another browser's DevTools port: its pages list and show its tabs, past the target policy
        var requests = LaunchReturns(Launched);
        _mockProcessService.Setup(p => p.GetProcessName(4242)).Returns(listener);
        var sut = new BrowserReadingTools(_mockBrowserReadingService.Object, _mockProcessService.Object,
            portOwners: new FakePortOwnerResolver(owner: 4242), browserDetection: new BrowserDetectionService());

        // Act
        var act = () => sut.LaunchBrowserWithDebug(url: url);

        // Assert
        if (refused)
        {
            (await act.Should().ThrowAsync<ToolErrorException>())
                .Which.Should().Match<ToolErrorException>(e => e.ErrorCode == ToolErrorReporting.InvalidArgumentsErrorCode &&
                                                              e.Message.Contains("DevTools") && e.Message.EndsWith("Nothing was launched."));
            requests.Should().BeEmpty();
        }
        else
        {
            await act.Should().NotThrowAsync();
            requests.Should().ContainSingle();
        }
    }

    [Fact]
    public async Task LaunchBrowserWithDebug_ALoopbackUrl_WhoseListenerCannotBeRead_IsRefused()
    {
        // Arrange - fails closed, as browser_navigate does
        var requests = LaunchReturns(Launched);
        var sut = new BrowserReadingTools(_mockBrowserReadingService.Object, _mockProcessService.Object,
            portOwners: new FakePortOwnerResolver(owner: null, tableReadable: false), browserDetection: new BrowserDetectionService());

        // Act
        var act = () => sut.LaunchBrowserWithDebug(url: "http://127.0.0.1:8080/");

        // Assert
        (await act.Should().ThrowAsync<ToolErrorException>()).Which.Message.Should().Contain("could not be read").And.EndWith("Nothing was launched.");
        requests.Should().BeEmpty();
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
    [InlineData("wam-profile")]
    [InlineData(@"profiles\debug")]
    [InlineData(@"\profiles\debug")]
    [InlineData(@"C:profile")]
    public async Task LaunchBrowserWithDebug_RefusesARelativeUserDataDir_AndLaunchesNothing(string userDataDir)
    {
        // Arrange - the server would resolve it against its own current directory, the browser against its install folder
        var sut = CreateSut();

        // Act
        var act = () => sut.LaunchBrowserWithDebug(userDataDir: userDataDir);

        // Assert
        (await act.Should().ThrowAsync<ToolErrorException>()).Which.Message.Should()
            .Contain("userDataDir must be a full path").And.Contain("Nothing was launched");
        _mockProcessService.VerifyNoOtherCalls();
    }

    [Theory]
    [InlineData(@"C:\Users\me\AppData\Local\Google\Chrome\USERDA~1\Default", "inside a 'User Data' folder")]
    [InlineData(@"C:\Users\me\AppData\Local\Google\Chrome\User Data::$INDEX_ALLOCATION\Default", "inside a 'User Data' folder")]
    [InlineData(@"C:\Users\me\AppData\Local\Google\Chrome\User Data:stream", "inside a 'User Data' folder")]
    [InlineData(@"C:\Users\me\AppData\Local\Google\Chrome\User Data. .\Default", "inside a 'User Data' folder")]
    [InlineData(@"\\localhost\C$\Users\me\AppData\Local\Google\Chrome\US1A2B~1", "short name")]
    [InlineData(@"\\10.255.255.1\s\PROFIL~1", "short name")]
    [InlineData(@"\\?\UNC\10.255.255.1\s\c~1", "short name")]
    public async Task LaunchBrowserWithDebug_RefusesAUserDataDirTheLaunchToolsRefuse_AndLaunchesNothing(string userDataDir, string message)
    {
        // Arrange - the launch tools' --user-data-dir check (ChromiumSwitchGuard), one implementation: an alternate data
        // stream and trailing dots and spaces are left out as Windows reads a name, and a short name on a network or device
        // path is refused, since expanding it would connect to another computer (\\localhost\C$ reaches the real profile)
        var sut = CreateSut();

        // Act
        var act = () => sut.LaunchBrowserWithDebug(userDataDir: userDataDir);

        // Assert
        var refused = (await act.Should().ThrowAsync<ToolErrorException>()).Which;
        refused.ErrorCode.Should().Be(ToolErrorReporting.InvalidArgumentsErrorCode);
        refused.Message.Should().Contain(message).And.Contain("Nothing was launched");
        _mockProcessService.VerifyNoOtherCalls();
    }

    [Fact]
    public void DebugProfileRefusal_ExpandsAShortNameOnADrive_AsTheLaunchToolsDo()
    {
        // Arrange - a hashed 8.3 name of the real profile's folder, which no pattern of the name catches
        const string userData = @"C:\Users\me\AppData\Local\Google\Chrome\User Data";
        var guard = new ChromiumSwitchGuard(path => path == @"C:\Users\me\AppData\Local\Google\Chrome\US1A2B~1" ? userData : null);

        // Act & Assert
        BrowserReadingTools.DebugProfileRefusal(@"C:\Users\me\AppData\Local\Google\Chrome\US1A2B~1\Default", guard)
            .Should().Contain("inside a 'User Data' folder").And.Contain("Nothing was launched");
        BrowserReadingTools.DebugProfileRefusal(@"C:\Temp\PROFIL~1", guard).Should().BeNull();
        BrowserReadingTools.DebugProfileRefusal(@"\\server\share\profiles\debug", guard).Should().BeNull("a network path without a short name is read by its text");
    }

    [Fact]
    public void DebugProfileRefusal_RefusesAShortNameOnANetworkPath_BeforeAnyPathIsMadeFull()
    {
        // Arrange - Path.GetFullPath expands 8.3 short names from the file system, which on a UNC path connects to that
        // computer; the guard's recorder makes paths full by their text only
        var made = new List<string>();
        var guard = new ChromiumSwitchGuard(_ => null, (path, basePath) =>
        {
            made.Add(path);
            return basePath == null ? path : Path.Join(basePath, path);
        });

        // Act
        var refusal = BrowserReadingTools.DebugProfileRefusal(@"\\10.255.255.1\s\PROFIL~1", guard);

        // Assert
        refusal.Should().Contain("short name").And.Contain("Nothing was launched");
        made.Should().BeEmpty();
    }

    [Theory]
    [InlineData(@"\\10.255.255.1\s\USERDA~1\Default", "inside a 'User Data' folder")]
    [InlineData(@"\\10.255.255.1\s\User Data\x~1", "inside a 'User Data' folder")]
    [InlineData(@"\\.\C:\..\UNC\10.255.255.1\s\x~1", "short name")]
    public void DebugProfileRefusal_RefusesANetworkPathWithAShortName_WithoutMakingItFull(string userDataDir, string message)
    {
        // Arrange - a 'User Data' folder on a network path, and a device path whose ".." leaves its drive for a server
        // (Windows makes \\.\C:\..\UNC\server full as \\.\UNC\server): Path.GetFullPath would expand the '~' over SMB. The
        // recorder makes a path with a '~' full by its text only
        var withShortName = new List<string>();
        var guard = new ChromiumSwitchGuard(_ => null, (path, basePath) =>
        {
            if (path.Contains('~') || (basePath?.Contains('~') ?? false))
            {
                withShortName.Add(path);
                return basePath == null ? path : Path.Join(basePath, path);
            }

            return basePath == null ? Path.GetFullPath(path) : Path.GetFullPath(path, basePath);
        });

        // Act
        var refusal = BrowserReadingTools.DebugProfileRefusal(userDataDir, guard);

        // Assert
        refusal.Should().Contain(message).And.Contain("Nothing was launched");
        withShortName.Should().BeEmpty();
    }

    [Theory]
    [InlineData("  C:/Temp/debug-profile  ", @"C:\Temp\debug-profile")]
    [InlineData(@"C:\Temp\a\..\debug-profile", @"C:\Temp\debug-profile")]
    [InlineData("   ", null)]
    public async Task LaunchBrowserWithDebug_PassesUserDataDirAsOneFullPath(string userDataDir, string? passed)
    {
        // Arrange - the browser, the port file and the result all use the path the 'User Data' check looked at
        var requests = LaunchReturns(Launched);
        var sut = CreateSut();

        // Act
        await sut.LaunchBrowserWithDebug(userDataDir: userDataDir);

        // Assert
        requests.Should().ContainSingle().Which.UserDataDir.Should().Be(passed);
    }

    [Theory]
    [InlineData(nameof(BrowserReadingTools.GetBrowserUrl))]
    [InlineData(nameof(BrowserReadingTools.GetBrowserContent))]
    public void TheReadingToolsCdpPort_IsTheOneLaunchBrowserWithDebugReturned(string tool)
    {
        // launch_browser_with_debug no longer listens on 9222 by default (roadmap M5.1)
        var cdpPort = typeof(BrowserReadingTools).GetMethod(tool)!.GetParameters().Single(p => p.Name == "cdpPort");
        var description = cdpPort.GetCustomAttribute<DescriptionAttribute>()!.Description;

        description.Should().Contain("launch_browser_with_debug returns it").And.NotContain("9222");
    }

    [Theory]
    [InlineData(DebugBrowser.Chrome, @"C:\Program Files\Google\Chrome Beta\Application\chrome.exe")]
    [InlineData(DebugBrowser.Edge, @"D:\Portable\Edge\MSEDGE.EXE")]
    [InlineData(DebugBrowser.Brave, @"\\server\share\Brave\brave.exe")]
    [InlineData(DebugBrowser.Chrome, null)]
    [InlineData(DebugBrowser.Chrome, "  ")]
    public void DebugExecutableRefusal_TakesAFullPathToTheBrowsersOwnExe(DebugBrowser browser, string? executablePath)
    {
        BrowserReadingTools.DebugExecutableRefusal(browser, executablePath).Should().BeNull();
    }

    [Theory]
    [InlineData(DebugBrowser.Chrome, "chrome.exe", "is not a full path")]
    [InlineData(DebugBrowser.Chrome, @"Google\Chrome\Application\chrome.exe", "is not a full path")]
    [InlineData(DebugBrowser.Chrome, @"C:chrome.exe", "is not a full path")]
    [InlineData(DebugBrowser.Edge, @"C:\Program Files\Google\Chrome\Application\chrome.exe", "must name msedge.exe for browser 'edge'")]
    [InlineData(DebugBrowser.Chrome, @"C:\Windows\System32\cmd.exe", "must name chrome.exe")]
    [InlineData(DebugBrowser.Brave, @"C:\Tools\brave.exe.lnk", "must name brave.exe")]
    public void DebugExecutableRefusal_RefusesARelativePathAndAnotherProgram(DebugBrowser browser, string executablePath, string message)
    {
        BrowserReadingTools.DebugExecutableRefusal(browser, executablePath).Should().Contain(message).And.Contain("Nothing was launched");
    }

    #endregion
}
