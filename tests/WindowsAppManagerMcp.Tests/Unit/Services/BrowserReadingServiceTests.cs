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
    /// Shows a page in the browser window <paramref name="handle"/>: Window <paramref name="title"/> > ToolBar > Edit of
    /// Chromium's address bar class with <paramref name="url"/> (none when null), and a Document "Example" with
    /// <paramref name="content"/> as its text and <paramref name="url"/> as its value (none when the content is null).
    /// </summary>
    private void ShowPage(nint handle, string? url, string? content = null, string? title = "Example - Google Chrome")
    {
        var window = new FakeUiaNode("Window", title);
        if (url != null)
        {
            window.With(new FakeUiaNode("ToolBar").With(
                new FakeUiaNode("Edit", "Adresse- og søkefelt") { ClassName = BrowserUiaReader.OmniboxClassName, ActionPatterns = ["Value"] }
                    .WithPattern<IUiaValuePattern>(new FakeValuePattern(url))));
        }

        if (content != null)
        {
            var document = new FakeUiaNode("Document", "Example").WithPattern<IUiaTextPattern>(new FakeTextPattern(content));
            window.With(url != null ? document.WithPattern<IUiaValuePattern>(new FakeValuePattern(url)) : document);
        }

        _tree.WithWindow(handle, window);
    }

    /// <summary>Shows <paramref name="browser"/> in the window <paramref name="handle"/>, its pages given by its render widgets.</summary>
    private void ShowBrowser(nint handle, FakeBrowserWindow browser)
    {
        _tree.WithWindow(handle, browser.Window);
        _warmup.Setup(w => w.FindPages(handle, It.IsAny<CancellationToken>()))
            .Returns(browser.Pages.Select((page, i) => new RenderWidgetPage(new nint(700 + i), handle, page)).ToList());
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
        result.UrlSource.Should().Be(BrowserUrlSource.AddressBar);
        result.DisplayUrl.Should().Be(expectedUrl);
        result.IsEditing.Should().BeFalse();
    }

    [Theory]
    [MemberData(nameof(Languages))]
    public async Task GetBrowserUrl_ReadsTheCommittedUrlFromTheShownPage_AndReportsTheAddressBar(BrowserNames names)
    {
        // Arrange
        var handle = new nint(12345);
        SetupValidBrowserWindow(handle, "msedge", "edge");
        var browser = new FakeBrowserWindow(names);
        ShowBrowser(handle, browser);

        // Act
        var result = await CreateSut().GetBrowserUrlAsync(handle);

        // Assert
        result.Should().BeEquivalentTo(new
        {
            Success = true,
            Url = FakeBrowserWindow.CommittedUrl,
            DisplayUrl = FakeBrowserWindow.ShownUrl,
            UrlSource = BrowserUrlSource.Document,
            IsEditing = (bool?)false,
            PageTitle = browser.Title,
            Source = BrowserReadSource.Uia
        });
        _warmup.Verify(w => w.FindPages(handle, _worker.LastToken), Times.Once);
    }

    public static TheoryData<BrowserNames> Languages() => new(BrowserNames.Localized);

    [Fact]
    public async Task GetBrowserUrl_ReportsTheNewTabPage_ApartFromAMissingAddressBar()
    {
        // Arrange
        var handle = new nint(12345);
        SetupValidBrowserWindow(handle, "chrome", "chrome");
        ShowBrowser(handle, new FakeBrowserWindow(BrowserNames.ChromeNorwegian, pageTitle: "Ny fane",
            committedUrl: "chrome://new-tab-page/", addressBarText: ""));

        // Act
        var result = await CreateSut().GetBrowserUrlAsync(handle);

        // Assert
        result.Success.Should().BeFalse();
        result.ErrorCode.Should().Be(BrowserErrorCode.NewTabPage);
        result.Url.Should().Be("chrome://new-tab-page/");
        result.Error.Should().Contain("new tab page");
    }

    [Fact]
    public async Task GetBrowserUrl_ReturnsUrlExtractionFailed_WhenTheAddressBarIsBeingEdited()
    {
        var handle = new nint(12345);
        SetupValidBrowserWindow(handle, "chrome", "chrome");
        var browser = new FakeBrowserWindow(BrowserNames.ChromeEnglish, committedUrl: null, addressBarText: "evil.example");
        browser.Omnibox.HasKeyboardFocus = true;
        ShowBrowser(handle, browser);

        var result = await CreateSut().GetBrowserUrlAsync(handle);

        result.Success.Should().BeFalse();
        result.ErrorCode.Should().Be(BrowserErrorCode.UrlExtractionFailed);
        result.Url.Should().BeNull();
        result.DisplayUrl.Should().Be("evil.example");
        result.IsEditing.Should().BeTrue();
    }

    [Fact]
    public async Task GetBrowserUrl_DoesNotReturnTheAddressBarsText_WhenTheBrowserIsNotInFront()
    {
        // Arrange - the agent's terminal is in front; the address bar holds text typed and left there, which a browser
        // behind another window reports as not focused
        var handle = new nint(12345);
        SetupValidBrowserWindow(handle, "chrome", "chrome");
        _mockNativeWrapper.Setup(n => n.GetForegroundWindow()).Returns(new nint(4242));
        ShowBrowser(handle, new FakeBrowserWindow(BrowserNames.ChromeEnglish, committedUrl: null,
            addressBarText: "intranet.corp/secret-project"));

        // Act
        var result = await CreateSut().GetBrowserUrlAsync(handle);

        // Assert
        result.Success.Should().BeFalse();
        result.ErrorCode.Should().Be(BrowserErrorCode.UrlExtractionFailed);
        result.Url.Should().BeNull();
        result.DisplayUrl.Should().Be("intranet.corp/secret-project");
        result.IsEditing.Should().BeNull();
    }

    [Fact]
    public async Task GetBrowserUrl_TakesThePagesAddress_WhenTheBrowserIsNotInFront()
    {
        var handle = new nint(12345);
        SetupValidBrowserWindow(handle, "chrome", "chrome");
        _mockNativeWrapper.Setup(n => n.GetForegroundWindow()).Returns(new nint(4242));
        ShowBrowser(handle, new FakeBrowserWindow(BrowserNames.ChromeEnglish));

        var result = await CreateSut().GetBrowserUrlAsync(handle);

        result.Success.Should().BeTrue();
        result.Url.Should().Be(FakeBrowserWindow.CommittedUrl);
        result.UrlSource.Should().Be(BrowserUrlSource.Document);
        result.IsEditing.Should().BeNull();
    }

    [Theory]
    [MemberData(nameof(Languages))]
    public async Task GetBrowserUrlAndContent_ReadTheTabsPage_WithTheBrowserBehindAnotherWindow(BrowserNames names)
    {
        // Arrange - Chrome and Edge as read on a real desktop: the warm-up gives the render widget's pane, the tab's
        // Document is beside it, and the agent's terminal is in front
        var handle = new nint(12345);
        SetupValidBrowserWindow(handle, names.IsEdge ? "msedge" : "chrome", names.IsEdge ? "edge" : "chrome");
        _mockNativeWrapper.Setup(n => n.GetForegroundWindow()).Returns(new nint(4242));
        ShowBrowser(handle, new FakeBrowserWindow(names));
        var sut = CreateSut();

        // Act
        var url = await sut.GetBrowserUrlAsync(handle);
        var content = await sut.GetBrowserContentAsync(handle);

        // Assert
        url.Should().BeEquivalentTo(new
        {
            Success = true, Url = FakeBrowserWindow.CommittedUrl, DisplayUrl = FakeBrowserWindow.ShownUrl,
            UrlSource = BrowserUrlSource.Document, IsEditing = (bool?)null, Source = BrowserReadSource.Uia
        });
        content.Success.Should().BeTrue();
        content.Url.Should().Be(FakeBrowserWindow.CommittedUrl);
        content.Content.Should().StartWith($"Welcome to {FakeBrowserWindow.PageTitle}");
    }

    [Fact]
    public async Task GetBrowserUrlAndContent_SayThePageMayNotBeShown_WhenItsTreeWasNeverBuilt()
    {
        // Arrange - an Edge that stayed behind other windows since its page loaded: no render widget, no Document
        var handle = new nint(12345);
        SetupValidBrowserWindow(handle, "msedge", "edge");
        _mockNativeWrapper.Setup(n => n.GetForegroundWindow()).Returns(new nint(4242));
        ShowBrowser(handle, new FakeBrowserWindow(BrowserNames.EdgeEnglish, withDocument: false, renderWidgetShown: false));
        var sut = CreateSut();

        // Act
        var url = await sut.GetBrowserUrlAsync(handle);
        var content = await sut.GetBrowserContentAsync(handle);

        // Assert
        url.ErrorCode.Should().Be(BrowserErrorCode.UrlExtractionFailed);
        url.Error.Should().Contain("behind other windows").And.Contain("cdpPort");
        content.ErrorCode.Should().Be(BrowserErrorCode.ContentAreaNotFound);
        content.Error.Should().Contain("behind other windows").And.Contain("cdpPort");
    }

    [Fact]
    public async Task GetBrowserUrlAndContent_ReadAChildWindowsHandle_AsItsTopLevelWindow()
    {
        // Arrange - the handle of the page's render widget, from an element's nativeWindowHandle
        var handle = new nint(12345);
        var renderWidget = new nint(777);
        SetupValidBrowserWindow(handle, "chrome", "chrome");
        _mockNativeWrapper.Setup(n => n.IsWindow(renderWidget)).Returns(true);
        _mockNativeWrapper.Setup(n => n.GetAncestor(renderWidget, WindowAncestor.Root)).Returns(handle);
        ShowBrowser(handle, new FakeBrowserWindow(BrowserNames.ChromeEnglish));
        var sut = CreateSut();

        // Act
        var url = await sut.GetBrowserUrlAsync(renderWidget);
        var content = await sut.GetBrowserContentAsync(renderWidget);

        // Assert
        url.Url.Should().Be(FakeBrowserWindow.CommittedUrl);
        url.PageTitle.Should().Be(new FakeBrowserWindow(BrowserNames.ChromeEnglish).Title);
        content.Success.Should().BeTrue();
        _worker.LastTargetProcessId.Should().Be(1000);
        _warmup.Verify(w => w.FindPages(handle, It.IsAny<CancellationToken>()), Times.Exactly(2));
        _warmup.Verify(w => w.FindPages(renderWidget, It.IsAny<CancellationToken>()), Times.Never);
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
    public async Task GetBrowserContent_ReturnsContentAreaNotFound_WhenTheWindowShowsNoPage()
    {
        // Arrange - the address bar and the browser's panes are there, but no Document of a tab
        var handle = new nint(12345);
        SetupValidBrowserWindow(handle, "chrome", "chrome");
        ShowPage(handle, "https://example.com", content: null);
        var sut = CreateSut();

        // Act
        var result = await sut.GetBrowserContentAsync(handle);

        // Assert
        result.Success.Should().BeFalse();
        result.ErrorCode.Should().Be(BrowserErrorCode.ContentAreaNotFound);
        result.Content.Should().BeNull();
        result.Url.Should().BeNull();
        result.Source.Should().Be("uia");
    }

    [Fact]
    public async Task GetBrowserContent_ReturnsContentExtractionFailed_WhenThePageHasNoText()
    {
        // Arrange
        var handle = new nint(12345);
        SetupValidBrowserWindow(handle, "chrome", "chrome");
        ShowPage(handle, "https://example.com", content: "");
        var sut = CreateSut();

        // Act
        var result = await sut.GetBrowserContentAsync(handle);

        // Assert
        result.Success.Should().BeFalse();
        result.ErrorCode.Should().Be(BrowserErrorCode.ContentExtractionFailed);
        result.Url.Should().Be("https://example.com");
    }

    [Fact]
    public async Task GetBrowserContent_ReadsTheShownPage_AfterTheWarmup_WithoutTheAddressBar()
    {
        // Arrange
        var handle = new nint(12345);
        SetupValidBrowserWindow(handle, "msedge", "edge");
        var browser = new FakeBrowserWindow(BrowserNames.EdgeNorwegian);
        ShowBrowser(handle, browser);
        var calls = new List<string>();
        _warmup.Setup(w => w.WarmUp(handle, It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
            .Callback(() => calls.Add("WarmUp")).Returns(Warmed);
        _warmup.Setup(w => w.FindPages(handle, It.IsAny<CancellationToken>()))
            .Callback(() => calls.Add("FindPages"))
            .Returns(browser.Pages.Select(page => new RenderWidgetPage(new nint(700), handle, page)).ToList());

        // Act
        var result = await CreateSut().GetBrowserContentAsync(handle);

        // Assert
        result.Success.Should().BeTrue();
        result.Url.Should().Be(FakeBrowserWindow.CommittedUrl);
        result.Content.Should().StartWith($"Welcome to {FakeBrowserWindow.PageTitle}");
        calls.Should().Equal("WarmUp", "FindPages");
        _tree.ChildListings.Should().NotContain([browser.RootView.Name!, browser.ToolBar.Name!]);
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
        _mockNativeWrapper.Setup(n => n.GetForegroundWindow()).Returns(handle);
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
                It.IsAny<int>(), It.IsAny<CdpWindow>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
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
            CdpPort, new CdpWindow(1000, "Test Page - chrome", null), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()), Times.Once);
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
            It.IsAny<int>(), It.IsAny<CdpWindow>(), TimeSpan.FromMilliseconds(expectedMs), It.IsAny<CancellationToken>()),
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
                It.IsAny<int>(), It.IsAny<CdpWindow>(), It.IsAny<TimeSpan>(), cts.Token))
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
    public async Task GetActiveBrowserUrl_Ambiguous_DoesNotTellTheCallerToPassTargetIdToItself()
    {
        // get_active_browser_url takes no targetId (an undeclared argument is refused), so the hint names the tools that do
        var handle = new nint(12345);
        _mockNativeWrapper.Setup(n => n.GetForegroundWindow()).Returns(handle);
        SetupValidBrowserWindow(handle, "chrome", "chrome");
        var cdp = new CdpService(
            new CdpTransport(
                FakeCdpHttpHandler.Targets(("A", "Test Page", "https://example.com/a"), ("B", "Test Page", "https://example.com/b")),
                FakeCdpWebSocketFactory.NeverConnects()),
            new FakePortOwnerResolver(1000));
        var sut = new BrowserReadingService(
            _mockNativeWrapper.Object, _mockBrowserDetection.Object, cdp, _tree, _worker, _warmup.Object, _policy);

        var result = await sut.GetActiveBrowserUrlAsync(new CdpOptions(Port: CdpPort, Required: true));

        result.ErrorCode.Should().Be(BrowserErrorCode.CdpTargetAmbiguous);
        result.Error.Should().Contain("targetId to get_browser_url or get_browser_content")
            .And.NotContain("pass one as targetId (");
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
        result.Error.Should().Contain("textOnly=true").And.Contain("start it with launch_browser_with_debug").And.Contain("cdpPort");
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
                It.IsAny<int>(), It.IsAny<CdpWindow>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
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
                It.IsAny<int>(), It.IsAny<CdpWindow>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
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

    [Theory]
    [InlineData(BrowserErrorCode.CdpTargetClosed)]
    [InlineData(BrowserErrorCode.CdpResponseTooLarge)]
    public async Task GetBrowserContent_FallsBackToUia_OnTheNewCdpCodes_WithoutTargetId(BrowserErrorCode code)
    {
        var handle = new nint(12345);
        SetupValidBrowserWindow(handle, "chrome", "chrome");
        SetupCdpMatch(CdpResult<CdpTarget>.Ok(CdpTab));
        SetupCdpSnapshot(CdpResult<CdpPageSnapshot>.Fail(code, "the reason"));
        ShowPage(handle, "https://example.com", "UIA text");
        var sut = CreateSut();

        var result = await sut.GetBrowserContentAsync(handle, cdp: new CdpOptions(Port: CdpPort));

        result.Success.Should().BeTrue();
        result.Source.Should().Be("uia");
        result.CdpError.Should().Be($"{code}: the reason");
    }

    [Fact]
    public async Task GetBrowserContent_ReturnsResponseTooLarge_InsteadOfHtmlRequiresCdp_ForHtml()
    {
        // The HTML exists and CDP works; it is only too large, so HtmlRequiresCdp's "CDP could not be used" would mislead
        var handle = new nint(12345);
        SetupValidBrowserWindow(handle, "chrome", "chrome");
        SetupCdpMatch(CdpResult<CdpTarget>.Ok(CdpTab));
        SetupCdpSnapshot(CdpResult<CdpPageSnapshot>.Fail(BrowserErrorCode.CdpResponseTooLarge, "over 64 MB; use maxLength"));
        var sut = CreateSut();

        var result = await sut.GetBrowserContentAsync(
            handle, new BrowserContentOptions(TextOnly: false), new CdpOptions(Port: CdpPort));

        result.ErrorCode.Should().Be(BrowserErrorCode.CdpResponseTooLarge);
        result.Error.Should().Be("over 64 MB; use maxLength");
        result.Source.Should().Be("cdp");
        result.TargetId.Should().Be("TAB1");
        _worker.Calls.Should().Be(0);
    }

    #endregion

    #region targetId (roadmap M5.2)

    private const string OtherTabId = "B7D4F3A9E5C1";

    private static readonly CdpTarget OtherTab = CdpTab with { Id = OtherTabId, Title = "Other", Url = "https://example.com/other" };

    private void SetupCdpTargetById(CdpResult<CdpTarget> result) =>
        _mockCdpService
            .Setup(c => c.FindTargetByIdAsync(
                It.IsAny<int>(), It.IsAny<int>(), It.IsAny<string>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(result);

    [Fact]
    public void CdpOptions_TargetIdImpliesRequired()
    {
        new CdpOptions(Port: CdpPort).EffectiveRequired.Should().BeFalse();
        new CdpOptions(Port: CdpPort, Required: true).EffectiveRequired.Should().BeTrue();
        new CdpOptions(Port: CdpPort, TargetId: OtherTabId).EffectiveRequired.Should().BeTrue();
    }

    [Fact]
    public async Task GetBrowserUrl_WithTargetId_LooksTheTabUpById_NotByTheWindowTitle()
    {
        var handle = new nint(12345);
        SetupValidBrowserWindow(handle, "chrome", "chrome");
        SetupCdpTargetById(CdpResult<CdpTarget>.Ok(OtherTab));
        var sut = CreateSut();

        var result = await sut.GetBrowserUrlAsync(handle, new CdpOptions(Port: CdpPort, TargetId: OtherTabId));

        result.Success.Should().BeTrue();
        result.Url.Should().Be("https://example.com/other");
        result.TargetId.Should().Be(OtherTabId);
        _mockCdpService.Verify(c => c.FindTargetByIdAsync(
            CdpPort, 1000, OtherTabId, It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()), Times.Once);
        _mockCdpService.Verify(c => c.FindTargetForWindowAsync(
            It.IsAny<int>(), It.IsAny<CdpWindow>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task GetBrowserUrl_WithTargetId_ReturnsTheCdpError_AndNeverReadsThroughUia()
    {
        var handle = new nint(12345);
        SetupValidBrowserWindow(handle, "chrome", "chrome");
        SetupCdpTargetById(CdpResult<CdpTarget>.Fail(BrowserErrorCode.CdpTargetNotFound, "No tab with targetId"));
        ShowPage(handle, "https://example.com/uia");
        var sut = CreateSut();

        var result = await sut.GetBrowserUrlAsync(handle, new CdpOptions(Port: CdpPort, Required: false, TargetId: OtherTabId));

        result.Success.Should().BeFalse();
        result.ErrorCode.Should().Be(BrowserErrorCode.CdpTargetNotFound);
        result.Source.Should().Be("cdp");
        _worker.Calls.Should().Be(0, "a tab asked for by id is never replaced by the window's address bar");
    }

    [Fact]
    public async Task GetBrowserContent_WithTargetId_ReadsThatTab_WithinOneBudget()
    {
        var handle = new nint(12345);
        SetupValidBrowserWindow(handle, "chrome", "chrome");
        var lookup = TimeSpan.Zero;
        _mockCdpService
            .Setup(c => c.FindTargetByIdAsync(
                It.IsAny<int>(), It.IsAny<int>(), It.IsAny<string>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
            .Returns(async () =>
            {
                var watch = System.Diagnostics.Stopwatch.StartNew();
                await Task.Delay(300);
                lookup = watch.Elapsed;
                return CdpResult<CdpTarget>.Ok(OtherTab);
            });
        SetupCdpSnapshot(CdpResult<CdpPageSnapshot>.Ok(new CdpPageSnapshot("Other", "https://example.com/other", "other text")));
        var sut = CreateSut();

        var result = await sut.GetBrowserContentAsync(
            handle, cdp: new CdpOptions(Port: CdpPort, TimeoutMs: CdpOptions.MaxTimeoutMs, TargetId: OtherTabId));

        result.Success.Should().BeTrue();
        result.Content.Should().Be("other text");
        result.TargetId.Should().Be(OtherTabId);
        lookup.Should().BeGreaterThan(TimeSpan.FromMilliseconds(250));
        _mockCdpService.Verify(c => c.GetPageSnapshotAsync(
            OtherTab, true, null,
            It.Is<TimeSpan>(t => t > TimeSpan.Zero && t <= TimeSpan.FromMilliseconds(CdpOptions.MaxTimeoutMs) - lookup),
            It.IsAny<CancellationToken>()), Times.Once);
        _mockCdpService.Verify(c => c.FindTargetForWindowAsync(
            It.IsAny<int>(), It.IsAny<CdpWindow>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task GetBrowserContent_WithTargetId_ReturnsTheCdpError_AndNeverReadsThroughUia()
    {
        var handle = new nint(12345);
        SetupValidBrowserWindow(handle, "chrome", "chrome");
        SetupCdpTargetById(CdpResult<CdpTarget>.Ok(OtherTab));
        SetupCdpSnapshot(CdpResult<CdpPageSnapshot>.Fail(BrowserErrorCode.CdpTargetClosed, "The tab closed"));
        ShowPage(handle, "https://example.com", "UIA text");
        var sut = CreateSut();

        var result = await sut.GetBrowserContentAsync(handle, cdp: new CdpOptions(Port: CdpPort, TargetId: OtherTabId));

        result.Success.Should().BeFalse();
        result.ErrorCode.Should().Be(BrowserErrorCode.CdpTargetClosed);
        result.TargetId.Should().Be(OtherTabId);
        _worker.Calls.Should().Be(0);
    }

    [Fact]
    public async Task GetBrowserContent_WithTargetIdButNoPort_ReturnsCdpUnavailable_WithoutReading()
    {
        var handle = new nint(12345);
        SetupValidBrowserWindow(handle, "chrome", "chrome");
        var sut = CreateSut();

        var result = await sut.GetBrowserContentAsync(handle, cdp: new CdpOptions(Port: null, TargetId: OtherTabId));

        result.ErrorCode.Should().Be(BrowserErrorCode.CdpUnavailable);
        _worker.Calls.Should().Be(0);
        _mockCdpService.VerifyNoOtherCalls();
    }

    #endregion

    #region The window's own debug port and its browser window (roadmap M5.3)

    private const int FoundPort = 51874;

    private readonly Mock<IDebugPortLocator> _locator = new(MockBehavior.Strict);

    private BrowserReadingService CreateSutWithLocator() => new(
        _mockNativeWrapper.Object,
        _mockBrowserDetection.Object,
        _mockCdpService.Object,
        _tree,
        _worker,
        _warmup.Object,
        _policy,
        _locator.Object);

    private void PortLookup(DebugPortLookupStatus status, int? port = null, string detail = "what the lookup found") =>
        _locator.Setup(l => l.Find(1000)).Returns(new DebugPortLookup(status, port, detail));

    [Fact]
    public async Task GetBrowserUrl_ReadsThroughThePortTheWindowsProcessListensOn_WithoutCdpPort()
    {
        var handle = new nint(12345);
        SetupValidBrowserWindow(handle, "msedge", "edge");
        PortLookup(DebugPortLookupStatus.Found, FoundPort);
        SetupCdpMatch(CdpResult<CdpTarget>.Ok(CdpTab with { Port = FoundPort }));
        var sut = CreateSutWithLocator();

        var result = await sut.GetBrowserUrlAsync(handle, new CdpOptions());

        result.Source.Should().Be("cdp");
        result.Url.Should().Be("https://example.com/cdp");
        result.CdpPort.Should().Be(FoundPort);
        _mockCdpService.Verify(c => c.FindTargetForWindowAsync(
            FoundPort, It.Is<CdpWindow>(w => w.ProcessId == 1000), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()), Times.Once);
        _worker.Calls.Should().Be(0);
    }

    [Fact]
    public async Task GetBrowserContent_ReadsTheHtmlThroughTheFoundPort()
    {
        var handle = new nint(12345);
        SetupValidBrowserWindow(handle, "chrome", "chrome");
        PortLookup(DebugPortLookupStatus.Found, FoundPort);
        SetupCdpMatch(CdpResult<CdpTarget>.Ok(CdpTab with { Port = FoundPort }));
        SetupCdpSnapshot(CdpResult<CdpPageSnapshot>.Ok(new CdpPageSnapshot("t", "https://example.com/", "<html></html>")));
        var sut = CreateSutWithLocator();

        var result = await sut.GetBrowserContentAsync(handle, new BrowserContentOptions(TextOnly: false), new CdpOptions());

        result.Success.Should().BeTrue();
        result.Content.Should().Be("<html></html>");
        result.Source.Should().Be("cdp");
        result.CdpPort.Should().Be(FoundPort);
    }

    [Fact]
    public async Task GetBrowserUrl_AnExplicitCdpPortWins_AndNoPortIsLookedFor()
    {
        var handle = new nint(12345);
        SetupValidBrowserWindow(handle, "chrome", "chrome");
        SetupCdpMatch(CdpResult<CdpTarget>.Ok(CdpTab));
        var sut = CreateSutWithLocator();

        var result = await sut.GetBrowserUrlAsync(handle, new CdpOptions(Port: CdpPort));

        result.CdpPort.Should().Be(CdpPort);
        _locator.VerifyNoOtherCalls();
    }

    [Theory]
    [InlineData(DebugPortLookupStatus.NoDebugPort)]
    [InlineData(DebugPortLookupStatus.CommandLineUnreadable)]
    public async Task GetBrowserUrlAndContent_ReadABrowserWithoutADebugPort_ThroughUiaAsBefore(DebugPortLookupStatus status)
    {
        // The user's own browser: no CDP call, no cdpError
        var handle = new nint(12345);
        SetupValidBrowserWindow(handle, "chrome", "chrome");
        PortLookup(status);
        ShowPage(handle, "https://example.com/uia", "UIA text");
        var sut = CreateSutWithLocator();

        var url = await sut.GetBrowserUrlAsync(handle, new CdpOptions());
        var content = await sut.GetBrowserContentAsync(handle, cdp: new CdpOptions());

        url.Source.Should().Be("uia");
        url.Url.Should().Be("https://example.com/uia");
        url.CdpError.Should().BeNull();
        url.CdpPort.Should().BeNull();
        content.Source.Should().Be("uia");
        content.Content.Should().Be("UIA text");
        content.CdpError.Should().BeNull();
        _mockCdpService.VerifyNoOtherCalls();
    }

    [Theory]
    [InlineData(DebugPortLookupStatus.NotListening, "CdpUnavailable")]
    [InlineData(DebugPortLookupStatus.PortNotKnown, "CdpUnavailable")]
    [InlineData(DebugPortLookupStatus.HeldByOtherProcess, "CdpPortOwnerMismatch")]
    public async Task GetBrowserUrl_SaysWhyTheDebugPortWasNotUsed_WhenItFallsBack(DebugPortLookupStatus status, string code)
    {
        var handle = new nint(12345);
        SetupValidBrowserWindow(handle, "chrome", "chrome");
        PortLookup(status, detail: "Port 9222 is not the browser's.");
        ShowPage(handle, "https://example.com/uia");
        var sut = CreateSutWithLocator();

        var result = await sut.GetBrowserUrlAsync(handle, new CdpOptions());

        result.Source.Should().Be("uia");
        result.CdpError.Should().Be($"{code}: Port 9222 is not the browser's.");
        _mockCdpService.VerifyNoOtherCalls();
    }

    [Theory]
    [InlineData(DebugPortLookupStatus.NoDebugPort, BrowserErrorCode.CdpUnavailable)]
    [InlineData(DebugPortLookupStatus.HeldByOtherProcess, BrowserErrorCode.CdpPortOwnerMismatch)]
    public async Task GetBrowserContent_RequiringCdp_SaysThatNoPortWasFound(DebugPortLookupStatus status, BrowserErrorCode expected)
    {
        var handle = new nint(12345);
        SetupValidBrowserWindow(handle, "chrome", "chrome");
        PortLookup(status, detail: "The browser was not started with a debug port.");
        var sut = CreateSutWithLocator();

        var result = await sut.GetBrowserContentAsync(handle, cdp: new CdpOptions(Required: true));

        result.ErrorCode.Should().Be(expected);
        result.Error.Should().Contain("The browser was not started with a debug port.")
            .And.Contain("launch_browser_with_debug").And.Contain("cdpPort");
        _worker.Calls.Should().Be(0);
        _mockCdpService.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task GetBrowserUrl_WithTargetIdAndNoCdpPort_LooksTheTabUpOnTheFoundPort()
    {
        var handle = new nint(12345);
        SetupValidBrowserWindow(handle, "chrome", "chrome");
        PortLookup(DebugPortLookupStatus.Found, FoundPort);
        _mockCdpService
            .Setup(c => c.FindTargetByIdAsync(FoundPort, 1000, "TAB2", It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(CdpResult<CdpTarget>.Ok(CdpTab with { Id = "TAB2", Port = FoundPort }));
        var sut = CreateSutWithLocator();

        var result = await sut.GetBrowserUrlAsync(handle, new CdpOptions(TargetId: "TAB2"));

        result.TargetId.Should().Be("TAB2");
        result.CdpPort.Should().Be(FoundPort);
    }

    [Fact]
    public async Task GetBrowserUrlAndContent_WithUseCdpFalse_ReadThroughUiaOnly_AndLookForNoPort()
    {
        var handle = new nint(12345);
        SetupValidBrowserWindow(handle, "chrome", "chrome");
        ShowPage(handle, "https://example.com/uia", "UIA text");
        var sut = CreateSutWithLocator();

        var url = await sut.GetBrowserUrlAsync(handle, new CdpOptions(UseCdp: false));
        var html = await sut.GetBrowserContentAsync(handle, new BrowserContentOptions(TextOnly: false), new CdpOptions(UseCdp: false));

        url.Source.Should().Be("uia");
        url.CdpError.Should().BeNull();
        html.ErrorCode.Should().Be(BrowserErrorCode.HtmlRequiresCdp);
        html.Error.Should().Contain("useCdp is false");
        _locator.VerifyNoOtherCalls();
        _mockCdpService.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task GetBrowserUrl_LooksForNoPort_InFirefoxOrWithoutCdpOptions()
    {
        // Firefox's debug port speaks WebDriver BiDi, not CDP; a caller without CdpOptions asked for UI Automation only
        var firefox = new nint(12345);
        var chrome = new nint(23456);
        SetupValidBrowserWindow(firefox, "firefox", "firefox");
        ShowPage(firefox, "https://example.com/ff");
        var sut = CreateSutWithLocator();

        var result = await sut.GetBrowserUrlAsync(firefox, new CdpOptions());
        _mockNativeWrapper.Setup(n => n.IsWindow(chrome)).Returns(true);
        _mockNativeWrapper.Setup(n => n.GetWindowThreadProcessId(chrome)).Returns(1000u);
        await sut.GetBrowserUrlAsync(chrome);

        result.Source.Should().Be("uia");
        _locator.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task GetBrowserUrl_PassesTheWindowsBoundsStateAndScale_ForTheBinding()
    {
        var handle = new nint(12345);
        SetupValidBrowserWindow(handle, "chrome", "chrome");
        _mockNativeWrapper.Setup(n => n.GetWindowRect(handle)).Returns((-12, -12, 2572, 1452));
        _mockNativeWrapper.Setup(n => n.IsZoomed(handle)).Returns(true);
        _mockNativeWrapper.Setup(n => n.GetDpiForWindow(handle)).Returns(144u);
        SetupCdpMatch(CdpResult<CdpTarget>.Ok(CdpTab));
        var sut = CreateSut();

        await sut.GetBrowserUrlAsync(handle, new CdpOptions(Port: CdpPort));

        _mockCdpService.Verify(c => c.FindTargetForWindowAsync(
            CdpPort,
            new CdpWindow(1000, "Test Page - chrome", new CdpWindowGeometry(-12, -12, 2572, 1452, CdpWindowState.Maximized, 1.5)),
            It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task GetBrowserUrl_DescribesAMinimizedWindow_AsMinimized()
    {
        var handle = new nint(12345);
        SetupValidBrowserWindow(handle, "chrome", "chrome");
        _mockNativeWrapper.Setup(n => n.GetWindowRect(handle)).Returns((-32000, -32000, -31840, -31972));
        _mockNativeWrapper.Setup(n => n.IsIconic(handle)).Returns(true);
        SetupCdpMatch(CdpResult<CdpTarget>.Ok(CdpTab));
        var sut = CreateSut();

        await sut.GetBrowserUrlAsync(handle, new CdpOptions(Port: CdpPort));

        _mockCdpService.Verify(c => c.FindTargetForWindowAsync(
            CdpPort, It.Is<CdpWindow>(w => w.Geometry!.State == CdpWindowState.Minimized && w.Geometry.Scale == 1.0),
            It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task GetActiveBrowserUrl_UsesTheFoundPortToo()
    {
        var handle = new nint(12345);
        SetupValidBrowserWindow(handle, "chrome", "chrome");
        PortLookup(DebugPortLookupStatus.Found, FoundPort);
        SetupCdpMatch(CdpResult<CdpTarget>.Ok(CdpTab with { Port = FoundPort }));
        var sut = CreateSutWithLocator();

        var result = await sut.GetActiveBrowserUrlAsync(new CdpOptions());

        result.Source.Should().Be("cdp");
        result.CdpPort.Should().Be(FoundPort);
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
        // Arrange - the window's name is unreadable; the address bar below it is still found (the first edit box in a toolbar)
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
    public async Task GetBrowserUrl_WarmsUpThePage_BeforeFindingTheShownPages()
    {
        // Arrange - the warm-up builds the page's Document, and gives the render widgets shown now, not the ones an
        // earlier read's warm-up left behind
        var handle = new nint(12345);
        SetupValidBrowserWindow(handle, "chrome", "chrome");
        var browser = new FakeBrowserWindow(BrowserNames.ChromeEnglish);
        ShowBrowser(handle, browser);
        var calls = new List<string>();
        _warmup.Setup(w => w.WarmUp(handle, It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
            .Callback(() => calls.Add("WarmUp")).Returns(Warmed);
        _warmup.Setup(w => w.FindPages(handle, It.IsAny<CancellationToken>()))
            .Callback(() => calls.Add("FindPages"))
            .Returns(browser.Pages.Select(page => new RenderWidgetPage(new nint(700), handle, page)).ToList());

        // Act
        var result = await CreateSut().GetBrowserUrlAsync(handle, new CdpOptions(TimeoutMs: 3000));

        // Assert
        result.Url.Should().Be(FakeBrowserWindow.CommittedUrl);
        calls.Should().Equal("WarmUp", "FindPages");
        _warmup.Verify(w => w.WarmUp(handle, AccessibilityWarmup.BudgetFor(TimeSpan.FromMilliseconds(3000)), _worker.LastToken), Times.Once);
    }

    [Fact]
    public async Task GetBrowserContent_ReturnsOperationTimedOut_WhenTheSearchForThePageTimesOut()
    {
        // Arrange - a browser whose page does not answer, which is not a window without a page
        var handle = new nint(12345);
        SetupValidBrowserWindow(handle, "firefox", "firefox");
        var window = new FakeUiaNode("Window", "Example - Mozilla Firefox").With(
            new FakeUiaNode("Document", "Example").WithPattern<IUiaTextPattern>(new FakeTextPattern("text")));
        window.FindAllException = new TimeoutException("The provider did not answer.");
        _tree.WithWindow(handle, window);

        // Act
        var result = await CreateSut().GetBrowserContentAsync(handle);

        // Assert
        result.Success.Should().BeFalse();
        result.ErrorCode.Should().Be(BrowserErrorCode.OperationTimedOut);
        result.Error.Should().Contain("did not answer");
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

        result.ErrorCode.Should().Be(BrowserErrorCode.ContentAreaNotFound);
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
        _mockNativeWrapper.Setup(n => n.GetForegroundWindow()).Returns(handle);
        _mockBrowserDetection.Setup(b => b.IsBrowser(It.IsAny<string>())).Returns(true);
    }

    #endregion
}
