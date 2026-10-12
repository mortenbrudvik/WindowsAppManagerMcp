using System.Text.Json;
using ModelContextProtocol.Protocol;

namespace WindowsAppManagerMcp.Tests.Unit.Tools;

using WindowsAppManagerMcp.Infrastructure;
using WindowsAppManagerMcp.Tools;

/// <summary>
/// Roadmap M3.11, part 2, in ScreenshotTools: capture_with_elements' Set-of-Marks defaults (actionable elements only
/// without controlTypes, at most 80), its overlay switches and legend; and capture_window's ref crop: the element is
/// found through its ref, its window must be the one captured, and its bounds read now (plus padding) are what the image
/// is cut to.
/// </summary>
public class CaptureMarksToolTests
{
    private static readonly nint RefWindow = 0x4242;
    private const string Ref = "w1000001e7";

    private readonly Mock<IScreenshotService> _screenshots = TestDataFactory.CreateMockScreenshotService();
    private readonly Mock<IUIElementService> _elements = TestDataFactory.CreateMockUIElementService();
    private readonly Mock<IWindowService> _windows = TestDataFactory.CreateMockWindowService();
    private readonly Mock<IElementRegistry> _registry = new();
    private TargetPolicySettings _policy = TargetPolicySettings.Default;

    public CaptureMarksToolTests()
    {
        _registry.Setup(r => r.Lookup(It.IsAny<string>())).Returns((string elementRef) =>
            new ElementLookup(elementRef, 0, null, 0, UIElementErrorCode.ElementStale, $"ref '{elementRef}' is not known."));
        _registry.Setup(r => r.Lookup(Ref)).Returns(new ElementLookup(Ref, RefWindow,
            new ElementFingerprint("Save", "Button", "btnSave", null, null), 3));
        Resolves(new UiaRect(150, 120, 40, 20));
        _elements.Setup(u => u.GetUIElementsAsync(It.IsAny<nint>(), It.IsAny<int>(), It.IsAny<UIElementFilter?>(),
                It.IsAny<IProgress<OperationProgress>?>(), It.IsAny<CancellationToken>()))
            .Callback((nint _, int _, UIElementFilter? filter, IProgress<OperationProgress>? _, CancellationToken _) => _walks.Add(filter))
            .ReturnsAsync(TestDataFactory.CreateUIElementResult());
        _screenshots.Setup(s => s.CaptureWindowArea(It.IsAny<nint>(), It.IsAny<bool>(), It.IsAny<BoundsDto>(), It.IsAny<CaptureOptions>()))
            .Returns(TestDataFactory.CreateScreenCapture(TestDataFactory.CreateScreenshotResult()));
    }

    private ScreenshotTools CreateSut() =>
        new(_screenshots.Object, _elements.Object, _windows.Object, new TargetPolicy(_policy), elementRegistry: _registry.Object);

    private void Resolves(UiaRect bounds, bool offscreen = false, string resolvedRef = Ref, bool moved = false)
    {
        var element = new UiaElement(new FakeUiaNode("Button", "Save"),
            new UiaNodeProperties("Save", "Button", "btnSave", bounds, IsEnabled: true, IsOffscreen: offscreen),
            ClassName: null, IsKeyboardFocusable: true, UiaPatterns.Invoke, RuntimeId: [42, 7], ControlTypeId: null);
        _registry.Setup(r => r.ResolveAsync(Ref, It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ElementResolution(new ResolvedElement(resolvedRef, RefWindow, element.Node, element,
                moved ? ElementResolutionPath.Selector : ElementResolutionPath.Live, moved)));
    }

    private readonly List<UIElementFilter?> _walks = [];

    /// <summary>The filter of the one walk the call made.</summary>
    private UIElementFilter? WalkFilter() => _walks.Should().ContainSingle().Subject;

    private static JsonElement Metadata(CallToolResult result) =>
        JsonDocument.Parse(result.Content.OfType<TextContentBlock>().Single().Text).RootElement;

    private void VerifyNotCaptured() =>
        _screenshots.Verify(s => s.CaptureWindowArea(It.IsAny<nint>(), It.IsAny<bool>(), It.IsAny<BoundsDto>(), It.IsAny<CaptureOptions>()), Times.Never);

    #region capture_with_elements

    [Fact]
    public async Task CaptureWithElements_ByDefault_MarksTheActionableElements_AtMost80()
    {
        await CreateSut().CaptureWithElements(handle: 12345);

        var filter = WalkFilter()!;
        filter.ActionableOnly.Should().BeTrue();
        filter.MaxElements.Should().Be(80);
        filter.InteractableOnly.Should().BeFalse();
        filter.VisibleOnly.Should().BeTrue();
    }

    [Fact]
    public async Task CaptureWithElements_WithControlTypes_MarksEveryElementOfThoseTypes()
    {
        await CreateSut().CaptureWithElements(handle: 12345, controlTypes: "Text,Image");

        var filter = WalkFilter()!;
        filter.ActionableOnly.Should().BeFalse("controlTypes chose the elements");
        filter.ControlTypes.Should().Equal("Text", "Image");
    }

    [Theory]
    [InlineData(null, false)]
    [InlineData("Button", true)]
    public async Task CaptureWithElements_AnExplicitActionableOnly_Wins(string? controlTypes, bool actionableOnly)
    {
        await CreateSut().CaptureWithElements(handle: 12345, controlTypes: controlTypes, actionableOnly: actionableOnly);

        WalkFilter()!.ActionableOnly.Should().Be(actionableOnly);
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(250, 250)]
    [InlineData(99_999, 5000)]
    public async Task CaptureWithElements_MaxElements_IsKeptInRange(int maxElements, int expected)
    {
        await CreateSut().CaptureWithElements(handle: 12345, maxElements: maxElements);

        WalkFilter()!.MaxElements.Should().Be(expected);
    }

    [Fact]
    public async Task CaptureWithElements_PassesTheOverlaySwitches()
    {
        await CreateSut().CaptureWithElements(handle: 12345, labels: false, drawOverlays: false, highlightInteractable: false);

        _screenshots.Verify(s => s.CaptureWindowWithElements(It.IsAny<nint>(), It.IsAny<string?>(),
            It.IsAny<IReadOnlyList<UIElementInfo>>(), It.IsAny<bool>(), new ElementOverlayOptions(false, false, false),
            It.IsAny<CaptureOptions>()), Times.Once);
    }

    [Fact]
    public async Task CaptureWithElements_ThatFound_NoElements_HasNoLegend()
    {
        _screenshots.Setup(s => s.CaptureWindowWithElements(It.IsAny<nint>(), It.IsAny<string?>(), It.IsAny<IReadOnlyList<UIElementInfo>>(),
                It.IsAny<bool>(), It.IsAny<ElementOverlayOptions>(), It.IsAny<CaptureOptions>()))
            .Returns(new ScreenCapture<AnnotatedScreenshotResult>(
                TestDataFactory.CreateAnnotatedScreenshotResult(elements: [], elementCount: 0), TestDataFactory.CreateEncodedImage()));

        var metadata = Metadata(await CreateSut().CaptureWithElements(handle: 12345));

        metadata.TryGetProperty("legend", out _).Should().BeFalse();
        metadata.GetProperty("elementCount").GetInt32().Should().Be(0);
    }

    #endregion

    #region The legend

    private static AnnotatedElement Mark(string? elementRef, string? name, string? automationId, BoundsDto screen, BoundsDto image,
        bool enabled = true, string type = "Button") =>
        new(name, type, automationId, image, screen, enabled, "green", elementRef);

    [Fact]
    public void LegendLine_HasTheRefTypeNameAutomationIdAndCenter()
    {
        var mark = Mark("w4120035e17", "Save", "btnSave", new BoundsDto(0, 0, 10, 10), new BoundsDto(0, 0, 10, 10));

        ElementMarkLegend.Line(mark, (392, 196)).Should().Be("[w4120035e17] Button 'Save' #btnSave center=(392,196)");
    }

    [Fact]
    public void LegendLine_FlagsADisabledElement_AndQuotesWhatCouldBeReadAsAnotherToken()
    {
        var mark = Mark(null, "Don't save", "save as", new BoundsDto(0, 0, 10, 10), new BoundsDto(0, 0, 10, 10), enabled: false);

        ElementMarkLegend.Line(mark, (5, 6)).Should().Be(@"[-] Button 'Don\'t save' #'save as' center=(5,6) [disabled]");
    }

    [Fact]
    public void LegendLine_WithoutNameOrAutomationId_HasTheTypeAndCenter()
    {
        var mark = Mark("w1e2", null, null, new BoundsDto(0, 0, 10, 10), new BoundsDto(0, 0, 10, 10), type: "ListItem");

        ElementMarkLegend.Line(mark, (1, 2)).Should().Be("[w1e2] ListItem center=(1,2)");
    }

    [Fact]
    public void LegendLine_CutsALongName()
    {
        var mark = Mark("w1e2", new string('x', 200), null, new BoundsDto(0, 0, 10, 10), new BoundsDto(0, 0, 10, 10));

        ElementMarkLegend.Line(mark, (1, 2)).Should().Be($"[w1e2] Button '{new string('x', ElementMarkLegend.MaxNameLength)}…' center=(1,2)");
    }

    [Fact]
    public void Center_IsTheElementsMiddle_InPixelsOfTheScaledImage_ThatConvertsBackThroughTheCapture()
    {
        // Arrange - a 1600 x 900 capture at (0, 100) scaled to 1568 x 882; the button's middle is screen (400, 300)
        var transform = new ImageToScreenTransform(0, 100, 1.020408, 1.020408);
        var region = new CapturedRegion(0, 100, 1600, 900);
        var mark = Mark("w1e2", "OK", null, new BoundsDto(380, 290, 40, 20), new BoundsDto(372, 186, 39, 20));

        // Act
        var center = ElementMarkLegend.Center(mark, transform, region, 1568, 882);

        // Assert - a click there with the captureId lands on the middle, not on a pixel the rounding moved
        center.Should().Be((392, 196));
        transform.ToScreen(center.X, center.Y).Should().Be((400, 300));
    }

    [Fact]
    public void Center_OfAnElementPartlyOutsideTheCapture_IsTheMiddleOfItsVisiblePart()
    {
        // The element starts 40 pixels left of the captured client area
        var transform = new ImageToScreenTransform(100, 100, 1, 1);
        var region = new CapturedRegion(100, 100, 400, 300);
        var mark = Mark("w1e2", "Wide", null, new BoundsDto(60, 150, 100, 20), new BoundsDto(0, 50, 60, 20));

        ElementMarkLegend.Center(mark, transform, region, 400, 300).Should().Be((30, 60));
    }

    #endregion

    #region capture_window with a ref

    [Fact]
    public async Task CaptureWindowRef_CapturesTheRefsWindow_CroppedToTheElementAndPadding()
    {
        // Act - no handle: the ref names its window
        var result = await CreateSut().CaptureWindow(@ref: "W1000001E7");

        // Assert - the bounds come from the ref's live element, read now
        result.IsError.Should().BeFalse();
        _screenshots.Verify(s => s.CaptureWindowArea(RefWindow, true, new BoundsDto(142, 112, 56, 36), It.IsAny<CaptureOptions>()), Times.Once);
        Metadata(result).GetProperty("ref").GetString().Should().Be(Ref);
    }

    [Fact]
    public async Task CaptureWindowRef_WithItsOwnWindow_AndAPadding_UsesThem()
    {
        var result = await CreateSut().CaptureWindow(handle: RefWindow, @ref: Ref, padding: 0, includeFrame: false);

        result.IsError.Should().BeFalse();
        _screenshots.Verify(s => s.CaptureWindowArea(RefWindow, false, new BoundsDto(150, 120, 40, 20), It.IsAny<CaptureOptions>()), Times.Once);
    }

    [Fact]
    public async Task CaptureWindowRef_ReportsTheRefTheElementResolvedTo()
    {
        // The element was re-created and already had a ref of its own: that one names it from now on
        Resolves(new UiaRect(150, 120, 40, 20), resolvedRef: "w1000001e9");

        var result = await CreateSut().CaptureWindow(@ref: Ref);

        Metadata(result).GetProperty("ref").GetString().Should().Be("w1000001e9");
    }

    [Fact]
    public async Task CaptureWindowRef_MovedToAReCreatedElement_CropsIt_AndSaysSo()
    {
        // The element the ref was given for is gone; the ref now names a re-created element that fits its description
        Resolves(new UiaRect(150, 120, 40, 20), moved: true);

        var result = await CreateSut().CaptureWindow(@ref: Ref);

        result.IsError.Should().BeFalse();
        var metadata = Metadata(result);
        metadata.GetProperty("refMoved").GetBoolean().Should().BeTrue();
        metadata.GetProperty("warnings").EnumerateArray().Select(w => w.GetString()).Should()
            .Contain(w => w!.StartsWith("RefMoved: ", StringComparison.Ordinal) && w.Contains("re-created"));
    }

    [Fact]
    public async Task CaptureWindowRef_OfALiveElement_HasNoMoveToReport()
    {
        var result = await CreateSut().CaptureWindow(@ref: Ref);

        Metadata(result).TryGetProperty("refMoved", out _).Should().BeFalse();
    }

    [Fact]
    public async Task CaptureWindowRef_OfAnotherWindow_IsInvalidArguments()
    {
        var call = () => CreateSut().CaptureWindow(handle: 12345, @ref: Ref);

        var error = (await call.Should().ThrowAsync<ToolErrorException>()).Which;
        error.ErrorCode.Should().Be("InvalidArguments");
        error.Message.Should().Contain("not of window 12345");
        _registry.Verify(r => r.ResolveAsync(It.IsAny<string>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()), Times.Never);
        VerifyNotCaptured();
    }

    [Theory]
    [InlineData(null, 8, "padding applies only with ref")]
    [InlineData(Ref, -1, "padding must be 0-500")]
    [InlineData(Ref, 501, "padding must be 0-500")]
    [InlineData("save-button", null, "is not an element ref")]
    public async Task CaptureWindowRef_WithBadArguments_IsInvalidArguments(string? elementRef, int? padding, string message)
    {
        var call = () => CreateSut().CaptureWindow(handle: RefWindow, @ref: elementRef, padding: padding);

        var error = (await call.Should().ThrowAsync<ToolErrorException>()).Which;
        error.ErrorCode.Should().Be("InvalidArguments");
        error.Message.Should().Contain(message);
        _screenshots.Verify(s => s.CaptureWindow(It.IsAny<nint>(), It.IsAny<bool>(), It.IsAny<CaptureOptions>()), Times.Never);
    }

    [Fact]
    public async Task CaptureWindowRef_ThatIsNotKnown_IsElementStale_AndCapturesNothing()
    {
        var result = await CreateSut().CaptureWindow(@ref: "w1000001e99");

        result.IsError.Should().BeTrue();
        Metadata(result).GetProperty("errorCode").GetString().Should().Be("ElementStale");
        VerifyNotCaptured();
    }

    [Theory]
    [InlineData(UIElementErrorCode.ElementStale)]
    [InlineData(UIElementErrorCode.AmbiguousElement)]
    [InlineData(UIElementErrorCode.NotResponding)]
    public async Task CaptureWindowRef_ThatCannotBeResolved_ReportsWhy(UIElementErrorCode code)
    {
        _registry.Setup(r => r.ResolveAsync(Ref, It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(ElementResolution.Failed(code, "The element is gone."));

        var result = await CreateSut().CaptureWindow(@ref: Ref);

        result.IsError.Should().BeTrue();
        Metadata(result).GetProperty("errorCode").GetString().Should().Be(code.ToString());
        VerifyNotCaptured();
    }

    [Theory]
    [InlineData(false, 0, 0)] // no bounds
    [InlineData(true, 40, 20)] // offscreen
    public async Task CaptureWindowRef_OfAnOffscreenElement_IsElementOffscreen(bool offscreen, double width, double height)
    {
        Resolves(new UiaRect(150, 120, width, height), offscreen);

        var result = await CreateSut().CaptureWindow(@ref: Ref);

        Metadata(result).GetProperty("errorCode").GetString().Should().Be("ElementOffscreen");
        Metadata(result).GetProperty("error").GetString().Should().Contain("scroll_into_view");
        VerifyNotCaptured();
    }

    [Fact]
    public async Task CaptureWindowRef_OfAWindowThePolicyDenies_IsRefusedBeforeTheElementIsRead()
    {
        _windows.Setup(w => w.GetWindowInfo(RefWindow)).Returns(TestDataFactory.CreateWindowInfo((int)RefWindow, "Vault", "KeePass"));
        _policy = new TargetPolicySettings(["keepass"], null, "test");

        var result = await CreateSut().CaptureWindow(@ref: Ref);

        Metadata(result).GetProperty("errorCode").GetString().Should().Be("TargetDeniedByPolicy");
        _registry.Verify(r => r.ResolveAsync(It.IsAny<string>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()), Times.Never);
        VerifyNotCaptured();
    }

    [Fact]
    public async Task CaptureWindowRef_OfAMinimizedWindow_IsRefusedBeforeTheElementIsRead()
    {
        _screenshots.Setup(s => s.CheckWindow(RefWindow))
            .Returns(new CaptureRefusal(ScreenshotErrorCode.WindowMinimized, ScreenshotService.MinimizedMessage));

        var result = await CreateSut().CaptureWindow(@ref: Ref);

        Metadata(result).GetProperty("errorCode").GetString().Should().Be("WindowMinimized");
        _registry.Verify(r => r.ResolveAsync(It.IsAny<string>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    #endregion
}
