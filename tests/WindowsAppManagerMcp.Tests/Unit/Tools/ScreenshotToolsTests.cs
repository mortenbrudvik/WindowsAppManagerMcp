using System.Text.Json;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;

namespace WindowsAppManagerMcp.Tests.Unit.Tools;

using WindowsAppManagerMcp.Tools;

/// <summary>
/// Unit tests for ScreenshotTools: what reaches the screenshot service, and the shape of the MCP
/// result (an image block followed by a metadata text block, isError on failure).
/// Tests T9.3 from BACKLOG-TECHNICAL.md.
/// </summary>
public class ScreenshotToolsTests
{
    private static readonly CaptureOptions Defaults = new();

    private readonly Mock<IScreenshotService> _mockScreenshotService;
    private readonly Mock<IUIElementService> _mockUIElementService;
    private readonly Mock<IWindowService> _mockWindowService;

    public ScreenshotToolsTests()
    {
        _mockScreenshotService = TestDataFactory.CreateMockScreenshotService();
        _mockUIElementService = TestDataFactory.CreateMockUIElementService();
        _mockWindowService = TestDataFactory.CreateMockWindowService();
    }

    private TargetPolicySettings _policy = TargetPolicySettings.Default;

    private ScreenshotTools CreateSut() => new ScreenshotTools(
        _mockScreenshotService.Object,
        _mockUIElementService.Object,
        _mockWindowService.Object,
        new TargetPolicy(_policy));

    #region T9.3.2-T9.3.4: TakeScreenshot Tests

    [Fact]
    public void TakeScreenshot_WithDefaults_UsesPrimaryMonitorAndDefaultOptions()
    {
        // Arrange
        var screenList = TestDataFactory.CreateScreenListResult(primaryIndex: 1);
        _mockScreenshotService.Setup(s => s.ListScreens()).Returns(screenList);
        var sut = CreateSut();

        // Act
        sut.TakeScreenshot();

        // Assert
        _mockScreenshotService.Verify(s => s.CaptureMonitor(1, Defaults), Times.Once);
    }

    [Fact]
    public void TakeScreenshot_WithMonitorIndex_UsesSpecifiedMonitor()
    {
        // Arrange
        var sut = CreateSut();

        // Act
        sut.TakeScreenshot(monitor: MonitorSelector.OfIndex(2));

        // Assert
        _mockScreenshotService.Verify(s => s.CaptureMonitor(2, Defaults), Times.Once);
        _mockScreenshotService.Verify(s => s.ListScreens(), Times.Never);
    }

    [Fact]
    public void TakeScreenshot_WithFormatAndQuality_PassesThem()
    {
        // Arrange
        var sut = CreateSut();

        var expected = Defaults with { Format = CaptureFormat.Png, Quality = 95 };

        // Act
        sut.TakeScreenshot(format: CaptureFormat.Png, quality: 95);

        // Assert
        _mockScreenshotService.Verify(s => s.CaptureMonitor(It.IsAny<int>(), expected), Times.Once);
    }

    [Fact]
    public void TakeScreenshot_ReturnsTheImageBlockFollowedByTheMetadata()
    {
        // Arrange
        var image = TestDataFactory.CreateEncodedImage();
        var metadata = TestDataFactory.CreateScreenshotResult(width: 1568, height: 882, regionX: -2560, regionY: 365);
        _mockScreenshotService.Setup(s => s.CaptureMonitor(It.IsAny<int>(), It.IsAny<CaptureOptions>()))
            .Returns(new ScreenCapture<ScreenshotResult>(metadata, image));
        var sut = CreateSut();

        // Act
        var result = sut.TakeScreenshot();

        // Assert
        result.IsError.Should().BeFalse();
        result.Content.Should().HaveCount(2);
        var imageBlock = result.Content[0].Should().BeOfType<ImageContentBlock>().Subject;
        imageBlock.MimeType.Should().Be("image/jpeg");
        imageBlock.DecodedData.ToArray().Should().Equal(image.Data);
        var json = Metadata(result);
        json.GetProperty("success").GetBoolean().Should().BeTrue();
        json.GetProperty("capturedRegion").GetProperty("x").GetInt32().Should().Be(-2560);
        json.GetProperty("imageToScreen").GetProperty("offsetY").GetInt32().Should().Be(365);
        json.TryGetProperty("imageData", out _).Should().BeFalse("the image travels in the image block only");
    }

    [Fact]
    public void TakeScreenshot_WhenServiceReturnsError_ReturnsIsErrorWithOnlyMetadata()
    {
        // Arrange
        var failure = TestDataFactory.CreateScreenshotResult(success: false, error: "Monitor index 99 is out of range.",
            errorCode: "InvalidMonitorIndex");
        _mockScreenshotService.Setup(s => s.CaptureMonitor(It.IsAny<int>(), It.IsAny<CaptureOptions>()))
            .Returns(new ScreenCapture<ScreenshotResult>(failure, null));
        var sut = CreateSut();

        // Act
        var result = sut.TakeScreenshot(monitor: MonitorSelector.OfIndex(99));

        // Assert
        result.IsError.Should().BeTrue();
        result.Content.Should().ContainSingle().Which.Should().BeOfType<TextContentBlock>();
        var json = Metadata(result);
        json.GetProperty("success").GetBoolean().Should().BeFalse();
        json.GetProperty("errorCode").GetString().Should().Be("InvalidMonitorIndex");
        json.GetProperty("error").GetString().Should().Contain("out of range");
    }

    [Fact]
    public void TakeScreenshot_WithFileOutput_ReturnsOnlyTheMetadata()
    {
        // Arrange - the service leaves the image out for output "file"
        var metadata = TestDataFactory.CreateScreenshotResult() with { FilePath = @"C:\captures\shot.jpg" };
        _mockScreenshotService.Setup(s => s.CaptureMonitor(It.IsAny<int>(), It.IsAny<CaptureOptions>()))
            .Returns(new ScreenCapture<ScreenshotResult>(metadata, null));
        var sut = CreateSut();

        // Act
        var result = sut.TakeScreenshot(output: CaptureOutputMode.File);

        // Assert
        result.IsError.Should().BeFalse();
        result.Content.Should().ContainSingle();
        Metadata(result).GetProperty("filePath").GetString().Should().Be(@"C:\captures\shot.jpg");
    }

    #endregion

    #region T9.3.5: CaptureRegion Tests

    [Fact]
    public void CaptureRegion_CallsServiceWithCorrectParameters()
    {
        // Arrange
        var sut = CreateSut();

        // Act
        sut.CaptureRegion(100, 200, 800, 600);

        // Assert
        _mockScreenshotService.Verify(s => s.CaptureRegion(100, 200, 800, 600, Defaults), Times.Once);
    }

    [Fact]
    public void CaptureRegion_WithFormatAndQuality_PassesThem()
    {
        // Arrange
        var sut = CreateSut();

        var expected = Defaults with { Format = CaptureFormat.Jpeg, Quality = 90 };

        // Act
        sut.CaptureRegion(0, 0, 400, 300, format: CaptureFormat.Jpeg, quality: 90);

        // Assert
        _mockScreenshotService.Verify(s => s.CaptureRegion(0, 0, 400, 300, expected), Times.Once);
    }

    [Fact]
    public void CaptureRegion_ReturnsImageAndMetadata()
    {
        // Arrange
        var sut = CreateSut();

        // Act
        var result = sut.CaptureRegion(100, 200, 800, 600);

        // Assert
        result.Content.Select(c => c.Type).Should().Equal("image", "text");
    }

    #endregion

    #region T9.3.6-T9.3.7: CaptureWindow Tests

    [Fact]
    public async Task CaptureWindow_CallsServiceWithCorrectHandle()
    {
        // Arrange
        var sut = CreateSut();

        // Act
        await sut.CaptureWindow(handle: 12345);

        // Assert
        _mockScreenshotService.Verify(s => s.CaptureWindow(new nint(12345), true, Defaults), Times.Once);
    }

    [Fact]
    public async Task CaptureWindow_WithIncludeFrameFalse_PassesCorrectFlag()
    {
        // Arrange
        var sut = CreateSut();

        // Act
        await sut.CaptureWindow(handle: 12345, includeFrame: false);

        // Assert
        _mockScreenshotService.Verify(s => s.CaptureWindow(new nint(12345), false, Defaults), Times.Once);
    }

    [Fact]
    public async Task CaptureWindow_WithAllParameters_PassesAllCorrectly()
    {
        // Arrange
        var sut = CreateSut();

        var expected = Defaults with { Format = CaptureFormat.Jpeg, Quality = 50 };

        // Act
        await sut.CaptureWindow(handle: 99999, includeFrame: false, format: CaptureFormat.Jpeg, quality: 50);

        // Assert
        _mockScreenshotService.Verify(s => s.CaptureWindow(new nint(99999), false, expected), Times.Once);
    }

    [Fact]
    public async Task CaptureWindow_WhenServiceReturnsError_ReturnsIsError()
    {
        // Arrange
        var failure = TestDataFactory.CreateScreenshotResult(success: false, error: "Invalid window handle",
            errorCode: "InvalidHandle");
        _mockScreenshotService.Setup(s => s.CaptureWindow(It.IsAny<nint>(), It.IsAny<bool>(), It.IsAny<CaptureOptions>()))
            .Returns(new ScreenCapture<ScreenshotResult>(failure, null));
        var sut = CreateSut();

        // Act
        var result = await sut.CaptureWindow(handle: 0);

        // Assert
        result.IsError.Should().BeTrue();
        Metadata(result).GetProperty("errorCode").GetString().Should().Be("InvalidHandle");
    }

    #endregion

    #region CaptureWithElements Tests

    [Fact]
    public async Task CaptureWithElements_WhenTheClientCancels_StopsTheWalk_AndCapturesNothing()
    {
        // Arrange - the client cancels while the tree is read
        using var call = new CancellationTokenSource();
        var walkStopped = false;
        _mockUIElementService.Setup(u => u.GetUIElementsAsync(
                It.IsAny<nint>(), It.IsAny<int>(), It.IsAny<UIElementFilter?>(), It.IsAny<IProgress<OperationProgress>?>(),
                It.IsAny<CancellationToken>()))
            .Callback((nint _, int _, UIElementFilter? _, IProgress<OperationProgress>? _, CancellationToken token) =>
            {
                call.Cancel();
                walkStopped = token.IsCancellationRequested;
            })
            .ReturnsAsync(TestDataFactory.CreateUIElementResult(elements: [TestDataFactory.CreateUIElementInfo()]));
        var sut = CreateSut();

        // Act
        var act = () => sut.CaptureWithElements(handle: 12345, cancellationToken: call.Token);

        // Assert
        await act.Should().ThrowAsync<OperationCanceledException>();
        walkStopped.Should().BeTrue("the request's token is linked with the walk's timeout");
        _mockScreenshotService.Verify(s => s.CaptureWindowWithElements(
            It.IsAny<nint>(), It.IsAny<string?>(), It.IsAny<IReadOnlyList<UIElementInfo>>(), It.IsAny<bool>(), It.IsAny<ElementOverlayOptions>(),
            It.IsAny<CaptureOptions>(), It.IsAny<CapturedWindow?>()), Times.Never);
    }

    [Fact]
    public async Task CaptureWithElements_ForwardsTheWalksProgressToTheClient()
    {
        // Arrange
        _mockUIElementService.Setup(u => u.GetUIElementsAsync(
                It.IsAny<nint>(), It.IsAny<int>(), It.IsAny<UIElementFilter?>(), It.IsAny<IProgress<OperationProgress>?>(),
                It.IsAny<CancellationToken>()))
            .Callback((nint _, int _, UIElementFilter? _, IProgress<OperationProgress>? progress, CancellationToken _) =>
                progress!.Report(new OperationProgress(200, null, "Read 200 UI elements so far")))
            .ReturnsAsync(TestDataFactory.CreateUIElementResult(elements: [TestDataFactory.CreateUIElementInfo()]));
        var client = new RecordingProgress<ProgressNotificationValue>();

        // Act
        await CreateSut().CaptureWithElements(handle: 12345, progress: client);

        // Assert
        client.Reports.Should().ContainSingle().Which.Progress.Should().Be(200);
    }

    [Fact]
    public async Task CaptureWithElements_WithValidHandle_CallsServices()
    {
        // Arrange
        var sut = CreateSut();

        // Act
        var result = await sut.CaptureWithElements(handle: 12345);

        // Assert
        result.IsError.Should().BeFalse();
        result.Content.Select(c => c.Type).Should().Equal("image", "text");
        _mockWindowService.Verify(w => w.IsValidWindow(new nint(12345)), Times.Once);
        _mockUIElementService.Verify(u => u.GetUIElementsAsync(
            new nint(12345),
            It.IsAny<int>(),
            It.IsAny<UIElementFilter?>(),
            It.IsAny<IProgress<OperationProgress>?>(),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task CaptureWithElements_WithInvalidWindow_ReturnsInvalidHandleWithoutWalkingTheTree()
    {
        // Arrange
        var sut = CreateSut();

        // Act
        var result = await sut.CaptureWithElements(handle: 0);

        // Assert
        result.IsError.Should().BeTrue();
        var json = Metadata(result);
        json.GetProperty("errorCode").GetString().Should().Be("InvalidHandle");
        json.GetProperty("error").GetString().Should().Contain("Window not found");
        VerifyNoWalk();
    }

    [Fact]
    public async Task CaptureWithElements_OfAMinimizedWindow_ReturnsWindowMinimizedWithoutWalkingTheTree()
    {
        // Arrange - every element of a minimized window is offscreen, and its pixels cannot be captured
        _mockScreenshotService.Setup(s => s.CheckWindow(new nint(4242)))
            .Returns(new CaptureRefusal(ScreenshotErrorCode.WindowMinimized, ScreenshotService.MinimizedMessage));
        var sut = CreateSut();

        // Act
        var result = await sut.CaptureWithElements(handle: 4242);

        // Assert
        result.IsError.Should().BeTrue();
        var json = Metadata(result);
        json.GetProperty("errorCode").GetString().Should().Be("WindowMinimized");
        json.GetProperty("error").GetString().Should().Contain("minimized").And.Contain("set_window_state");
        VerifyNoWalk();
    }

    [Fact]
    public async Task CaptureWithElements_ForAnUntitledWindow_StillCaptures()
    {
        // Arrange - popups and menus have no title
        _mockWindowService.Setup(w => w.GetWindowInfo(It.IsAny<nint>())).Returns(TestDataFactory.CreateWindowInfo(4242, title: ""));
        var sut = CreateSut();

        // Act
        var result = await sut.CaptureWithElements(handle: 4242);

        // Assert - the title then comes from the UI Automation walk
        result.IsError.Should().BeFalse();
        _mockScreenshotService.Verify(s => s.CaptureWindowWithElements(
            new nint(4242), It.IsAny<string?>(), It.IsAny<IReadOnlyList<UIElementInfo>>(), true, ElementOverlayOptions.Default, Defaults, It.IsAny<CapturedWindow?>()), Times.Once);
    }

    [Fact]
    public async Task CaptureWithElements_WithUIElementFailure_StillReturnsTheImage_WithTheWalksError()
    {
        // Arrange - the walk timed out before it found an element
        _mockUIElementService.Setup(u => u.GetUIElementsAsync(
                It.IsAny<nint>(),
                It.IsAny<int>(),
                It.IsAny<UIElementFilter?>(),
                It.IsAny<IProgress<OperationProgress>?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(TestDataFactory.CreateUIElementResult(
                success: false,
                error: "Timeout",
                errorCode: UIElementErrorCode.OperationTimedOut));
        var sut = CreateSut();

        // Act
        var result = await sut.CaptureWithElements(handle: 12345);

        // Assert - the pixels do not depend on the walk
        result.IsError.Should().BeFalse();
        result.Content.Select(c => c.Type).Should().Equal("image", "text");
        var json = Metadata(result);
        json.GetProperty("success").GetBoolean().Should().BeTrue();
        json.GetProperty("elementsErrorCode").GetString().Should().Be("OperationTimedOut");
        json.GetProperty("elementsError").GetString().Should().Contain("Timeout");
        json.GetProperty("truncated").GetBoolean().Should().BeTrue();
        json.GetProperty("truncationReason").GetString().Should().Be("Timeout");
        json.GetProperty("windowTitle").GetString().Should().Be("Test Window");
        _mockScreenshotService.Verify(s => s.CaptureWindowWithElements(
            new nint(12345), It.IsAny<string?>(), It.Is<IReadOnlyList<UIElementInfo>>(e => e.Count == 0),
            It.IsAny<bool>(), It.IsAny<ElementOverlayOptions>(), It.IsAny<CaptureOptions>(), It.IsAny<CapturedWindow?>()), Times.Once);
    }

    [Theory]
    [InlineData(0, "InvalidImageSize")]
    [InlineData(-4, "InvalidImageSize")]
    public async Task CaptureWithElements_WithInvalidOptions_DoesNotWalkTheTree(int? maxWidth, string expectedCode)
    {
        // Arrange - an unknown format is refused before the tool runs (ToolArgumentContractTests)
        var sut = CreateSut();

        // Act
        var result = await sut.CaptureWithElements(12345, maxWidth: maxWidth);

        // Assert
        result.IsError.Should().BeTrue();
        Metadata(result).GetProperty("errorCode").GetString().Should().Be(expectedCode);
        _mockWindowService.Verify(w => w.IsValidWindow(It.IsAny<nint>()), Times.Never);
        VerifyNoWalk();
    }

    [Fact]
    public async Task CaptureWithElements_WithDefaultParameters_UsesDefaults()
    {
        // Arrange
        var sut = CreateSut();

        // Act
        await sut.CaptureWithElements(handle: 12345);

        // Assert
        _mockUIElementService.Verify(u => u.GetUIElementsAsync(
            It.IsAny<nint>(),
            5, // Default maxDepth
            It.Is<UIElementFilter?>(f => f != null && f.VisibleOnly == true && f.InteractableOnly == false),
            It.IsAny<IProgress<OperationProgress>?>(),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task CaptureWithElements_WithControlTypesFilter_ParsesCorrectly()
    {
        // Arrange
        var sut = CreateSut();

        // Act
        await sut.CaptureWithElements(handle: 12345, controlTypes: "Button,Edit");

        // Assert
        _mockUIElementService.Verify(u => u.GetUIElementsAsync(
            It.IsAny<nint>(),
            It.IsAny<int>(),
            It.Is<UIElementFilter?>(f =>
                f != null &&
                f.ControlTypes != null &&
                f.ControlTypes.Contains("Button") &&
                f.ControlTypes.Contains("Edit")),
            It.IsAny<IProgress<OperationProgress>?>(),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task CaptureWithElements_WithMaxDepth_PassesValue()
    {
        // Arrange
        var sut = CreateSut();

        // Act
        await sut.CaptureWithElements(handle: 12345, maxDepth: 8);

        // Assert
        _mockUIElementService.Verify(u => u.GetUIElementsAsync(
            It.IsAny<nint>(),
            8,
            It.IsAny<UIElementFilter?>(),
            It.IsAny<IProgress<OperationProgress>?>(),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task CaptureWithElements_WithInteractableOnly_PassesFilter()
    {
        // Arrange
        var sut = CreateSut();

        // Act
        await sut.CaptureWithElements(handle: 12345, interactableOnly: true);

        // Assert
        _mockUIElementService.Verify(u => u.GetUIElementsAsync(
            It.IsAny<nint>(),
            It.IsAny<int>(),
            It.Is<UIElementFilter?>(f => f != null && f.InteractableOnly == true),
            It.IsAny<IProgress<OperationProgress>?>(),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task CaptureWithElements_CallsScreenshotServiceWithElementsTitleAndDefaults()
    {
        // Arrange
        var windowInfo = TestDataFactory.CreateWindowInfo(title: "My Test Window");
        _mockWindowService.Setup(w => w.GetWindowInfo(It.IsAny<nint>())).Returns(windowInfo);
        var sut = CreateSut();

        // Act
        await sut.CaptureWithElements(handle: 12345);

        // Assert
        _mockScreenshotService.Verify(s => s.CaptureWindowWithElements(
            new nint(12345),
            "My Test Window",
            It.IsAny<IReadOnlyList<UIElementInfo>>(),
            true, // default includeFrame
            ElementOverlayOptions.Default, // default: drawn, labelled, disabled dimmed
            Defaults, It.IsAny<CapturedWindow?>()), Times.Once);
    }

    [Fact]
    public async Task CaptureWithElements_WithFormatJpegAndHighlightOff_PassesThem()
    {
        // Arrange
        var sut = CreateSut();

        var expected = Defaults with { Format = CaptureFormat.Jpeg, Quality = 70 };

        // Act
        await sut.CaptureWithElements(handle: 12345, format: CaptureFormat.Jpeg, quality: 70, highlightInteractable: false);

        // Assert
        _mockScreenshotService.Verify(s => s.CaptureWindowWithElements(
            It.IsAny<nint>(),
            It.IsAny<string?>(),
            It.IsAny<IReadOnlyList<UIElementInfo>>(),
            It.IsAny<bool>(),
            new ElementOverlayOptions(true, true, false),
            expected, It.IsAny<CapturedWindow?>()), Times.Once);
    }

    [Fact]
    public async Task CaptureWithElements_MetadataHasTheLegend_AndTheElementsOnlyWithDetailFull()
    {
        // Arrange
        var sut = CreateSut();

        // Act
        var byDefault = Metadata(await sut.CaptureWithElements(handle: 12345));
        var full = Metadata(await sut.CaptureWithElements(handle: 12345, detail: CaptureElementDetail.Full));

        // Assert - one line per mark (roadmap M3.11); the list with both bounds is the costly part
        byDefault.TryGetProperty("elements", out _).Should().BeFalse();
        byDefault.GetProperty("elementCount").GetInt32().Should().Be(3);
        byDefault.GetProperty("legend").GetString()!.Split('\n').Should().HaveCount(3)
            .And.Contain("[-] Button 'Submit' #btnSubmit center=(140,115)");
        var elements = full.GetProperty("elements");
        elements.GetArrayLength().Should().Be(3);
        elements[0].GetProperty("screenBounds").GetProperty("x").GetInt32().Should().Be(200);
        full.GetProperty("legend").GetString().Should().Be(byDefault.GetProperty("legend").GetString());
    }

    [Fact]
    public async Task CaptureWithElements_NamesTheOwnedWindowsTheWalkLeftOut_AndOnlyWhenThereAreAny()
    {
        // Arrange - a dialog the window owns, which the walk leaves out (roadmap M3.F9)
        var withDialog = TestDataFactory.CreateUIElementResult() with { OwnedWindows = [67890] };
        var sut = CreateSut();
        var plain = Metadata(await sut.CaptureWithElements(handle: 12345));
        _mockUIElementService.Setup(u => u.GetUIElementsAsync(
                It.IsAny<nint>(), It.IsAny<int>(), It.IsAny<UIElementFilter?>(), It.IsAny<IProgress<OperationProgress>?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(withDialog);

        // Act
        var json = Metadata(await sut.CaptureWithElements(handle: 12345));

        // Assert
        plain.TryGetProperty("ownedWindows", out _).Should().BeFalse();
        json.GetProperty("ownedWindows").EnumerateArray().Select(h => h.GetInt64()).Should().Equal(67890);
        json.GetProperty("elementCount").GetInt32().Should().Be(3);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task CaptureWithElements_CarriesTheWalksWarnings_AndOnlyWhenThereAreAny(bool walkSucceeds)
    {
        // Arrange - an elevated window's tree comes back empty, or its walk fails (roadmap M3.F16)
        const string elevated = "TargetElevated: Window 12345 belongs to a process that runs at a higher integrity level.";
        var walk = walkSucceeds
            ? TestDataFactory.CreateUIElementResult() with { Warnings = [elevated] }
            : TestDataFactory.CreateUIElementResult() with
            {
                Success = false, Elements = null, Error = "access denied", ErrorCode = UIElementErrorCode.OperationException,
                Warnings = [elevated]
            };
        var sut = CreateSut();
        var plain = Metadata(await sut.CaptureWithElements(handle: 12345));
        _mockUIElementService.Setup(u => u.GetUIElementsAsync(
                It.IsAny<nint>(), It.IsAny<int>(), It.IsAny<UIElementFilter?>(), It.IsAny<IProgress<OperationProgress>?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(walk);

        // Act
        var json = Metadata(await sut.CaptureWithElements(handle: 12345));

        // Assert - with the captures' own warnings, in the same "Code: text" form
        plain.TryGetProperty("warnings", out _).Should().BeFalse();
        json.GetProperty("warnings").EnumerateArray().Select(w => w.GetString()).Should().Equal(elevated);
    }

    #endregion

    private void VerifyNoWalk()
    {
        _mockUIElementService.Verify(
            u => u.GetUIElementsAsync(
                It.IsAny<nint>(),
                It.IsAny<int>(),
                It.IsAny<UIElementFilter?>(),
                It.IsAny<IProgress<OperationProgress>?>(),
                It.IsAny<CancellationToken>()),
            Times.Never);
        _mockScreenshotService.Verify(s => s.CaptureWindowWithElements(
            It.IsAny<nint>(), It.IsAny<string?>(), It.IsAny<IReadOnlyList<UIElementInfo>>(),
            It.IsAny<bool>(), It.IsAny<ElementOverlayOptions>(), It.IsAny<CaptureOptions>(), It.IsAny<CapturedWindow?>()), Times.Never);
    }

    private static JsonElement Metadata(CallToolResult result) =>
        JsonDocument.Parse(result.Content.OfType<TextContentBlock>().Single().Text).RootElement;
}
