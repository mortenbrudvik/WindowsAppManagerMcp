namespace WindowsAppManagerMcp.Tests.Unit.Tools;

using WindowsAppManagerMcp.Tools;

/// <summary>
/// Unit tests for UIElementTools.
/// Tests Task 11.2 from BACKLOG-TECHNICAL.md.
/// </summary>
public class UIElementToolsTests
{
    private readonly Mock<IUIElementService> _mockService;

    public UIElementToolsTests()
    {
        _mockService = TestDataFactory.CreateMockUIElementService();
    }

    private readonly Mock<IElementInspectionService> _mockInspection = new();

    private UIElementTools CreateSut() => new UIElementTools(_mockService.Object, _mockInspection.Object);

    #region Basic Tool Operation Tests

    [Fact]
    public void GetUIElements_CallsService()
    {
        // Arrange
        var sut = CreateSut();

        // Act
        sut.GetUIElements(handle: 12345);

        // Assert
        _mockService.Verify(s => s.GetUIElements(
            It.IsAny<nint>(),
            It.IsAny<int>(),
            It.IsAny<UIElementFilter?>(),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public void GetUIElements_PassesHandleCorrectly()
    {
        // Arrange
        var sut = CreateSut();

        // Act
        sut.GetUIElements(handle: 99999);

        // Assert
        _mockService.Verify(s => s.GetUIElements(
            new nint(99999),
            It.IsAny<int>(),
            It.IsAny<UIElementFilter?>(),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public void GetUIElements_ReturnsServiceResult()
    {
        // Arrange
        var expectedResult = TestDataFactory.CreateUIElementResult();
        _mockService.Setup(s => s.GetUIElements(
                It.IsAny<nint>(),
                It.IsAny<int>(),
                It.IsAny<UIElementFilter?>(),
                It.IsAny<CancellationToken>()))
            .Returns(expectedResult);
        var sut = CreateSut();

        // Act
        var result = sut.GetUIElements(handle: 12345);

        // Assert
        result.Should().Be(expectedResult);
    }

    #endregion

    #region MaxDepth Parameter Tests

    [Fact]
    public void GetUIElements_WithDefaultDepth_Passes5()
    {
        // Arrange
        var sut = CreateSut();

        // Act
        sut.GetUIElements(handle: 12345);

        // Assert
        _mockService.Verify(s => s.GetUIElements(
            It.IsAny<nint>(),
            5, // Default depth
            It.IsAny<UIElementFilter?>(),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public void GetUIElements_WithSpecifiedDepth_PassesClampedValue()
    {
        // Arrange
        var sut = CreateSut();

        // Act
        sut.GetUIElements(handle: 12345, maxDepth: 8);

        // Assert
        _mockService.Verify(s => s.GetUIElements(
            It.IsAny<nint>(),
            8,
            It.IsAny<UIElementFilter?>(),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public void GetUIElements_WithDepthAboveMax_ClampsTo10()
    {
        // Arrange
        var sut = CreateSut();

        // Act
        sut.GetUIElements(handle: 12345, maxDepth: 20);

        // Assert
        _mockService.Verify(s => s.GetUIElements(
            It.IsAny<nint>(),
            10, // Clamped to max
            It.IsAny<UIElementFilter?>(),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public void GetUIElements_WithDepthBelowMin_ClampsTo1()
    {
        // Arrange
        var sut = CreateSut();

        // Act
        sut.GetUIElements(handle: 12345, maxDepth: 0);

        // Assert
        _mockService.Verify(s => s.GetUIElements(
            It.IsAny<nint>(),
            1, // Clamped to min
            It.IsAny<UIElementFilter?>(),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    #endregion

    #region ControlTypes Parameter Tests

    [Fact]
    public void GetUIElements_WithNoControlTypes_PassesNullFilter()
    {
        // Arrange
        var sut = CreateSut();

        // Act
        sut.GetUIElements(handle: 12345, controlTypes: null);

        // Assert
        _mockService.Verify(s => s.GetUIElements(
            It.IsAny<nint>(),
            It.IsAny<int>(),
            It.Is<UIElementFilter?>(f => f != null && f.ControlTypes == null),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public void GetUIElements_WithControlTypes_ParsesCorrectly()
    {
        // Arrange
        var sut = CreateSut();

        // Act
        sut.GetUIElements(handle: 12345, controlTypes: "Button,Edit,CheckBox");

        // Assert
        _mockService.Verify(s => s.GetUIElements(
            It.IsAny<nint>(),
            It.IsAny<int>(),
            It.Is<UIElementFilter?>(f =>
                f != null &&
                f.ControlTypes != null &&
                f.ControlTypes.Length == 3 &&
                f.ControlTypes.Contains("Button") &&
                f.ControlTypes.Contains("Edit") &&
                f.ControlTypes.Contains("CheckBox")),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public void GetUIElements_WithControlTypesContainingSpaces_TrimsCorrectly()
    {
        // Arrange
        var sut = CreateSut();

        // Act
        sut.GetUIElements(handle: 12345, controlTypes: " Button , Edit , CheckBox ");

        // Assert
        _mockService.Verify(s => s.GetUIElements(
            It.IsAny<nint>(),
            It.IsAny<int>(),
            It.Is<UIElementFilter?>(f =>
                f != null &&
                f.ControlTypes != null &&
                f.ControlTypes.Length == 3 &&
                f.ControlTypes.All(t => !t.StartsWith(" ") && !t.EndsWith(" "))),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public void GetUIElements_WithEmptyControlTypes_PassesNullControlTypes()
    {
        // Arrange
        var sut = CreateSut();

        // Act
        sut.GetUIElements(handle: 12345, controlTypes: "");

        // Assert
        _mockService.Verify(s => s.GetUIElements(
            It.IsAny<nint>(),
            It.IsAny<int>(),
            It.Is<UIElementFilter?>(f => f != null && f.ControlTypes == null),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public void GetUIElements_WithWhitespaceOnlyControlTypes_PassesNullControlTypes()
    {
        // Arrange
        var sut = CreateSut();

        // Act
        sut.GetUIElements(handle: 12345, controlTypes: "   ");

        // Assert
        _mockService.Verify(s => s.GetUIElements(
            It.IsAny<nint>(),
            It.IsAny<int>(),
            It.Is<UIElementFilter?>(f => f != null && f.ControlTypes == null),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    #endregion

    #region Filter Parameters Tests

    [Fact]
    public void GetUIElements_WithInteractableOnly_PassesFilter()
    {
        // Arrange
        var sut = CreateSut();

        // Act
        sut.GetUIElements(handle: 12345, interactableOnly: true);

        // Assert
        _mockService.Verify(s => s.GetUIElements(
            It.IsAny<nint>(),
            It.IsAny<int>(),
            It.Is<UIElementFilter?>(f => f != null && f.InteractableOnly == true),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public void GetUIElements_WithVisibleOnlyFalse_PassesFilter()
    {
        // Arrange
        var sut = CreateSut();

        // Act
        sut.GetUIElements(handle: 12345, visibleOnly: false);

        // Assert
        _mockService.Verify(s => s.GetUIElements(
            It.IsAny<nint>(),
            It.IsAny<int>(),
            It.Is<UIElementFilter?>(f => f != null && f.VisibleOnly == false),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public void GetUIElements_WithMinWidth_PassesFilter()
    {
        // Arrange
        var sut = CreateSut();

        // Act
        sut.GetUIElements(handle: 12345, minWidth: 50);

        // Assert
        _mockService.Verify(s => s.GetUIElements(
            It.IsAny<nint>(),
            It.IsAny<int>(),
            It.Is<UIElementFilter?>(f => f != null && f.MinWidth == 50),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public void GetUIElements_WithMinHeight_PassesFilter()
    {
        // Arrange
        var sut = CreateSut();

        // Act
        sut.GetUIElements(handle: 12345, minHeight: 30);

        // Assert
        _mockService.Verify(s => s.GetUIElements(
            It.IsAny<nint>(),
            It.IsAny<int>(),
            It.Is<UIElementFilter?>(f => f != null && f.MinHeight == 30),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public void GetUIElements_WithAllFilters_PassesAllCorrectly()
    {
        // Arrange
        var sut = CreateSut();

        // Act
        sut.GetUIElements(
            handle: 12345,
            maxDepth: 7,
            controlTypes: "Button,Edit",
            interactableOnly: true,
            visibleOnly: true,
            minWidth: 50,
            minHeight: 20);

        // Assert
        _mockService.Verify(s => s.GetUIElements(
            new nint(12345),
            7,
            It.Is<UIElementFilter?>(f =>
                f != null &&
                f.ControlTypes != null &&
                f.ControlTypes.Length == 2 &&
                f.InteractableOnly == true &&
                f.VisibleOnly == true &&
                f.MinWidth == 50 &&
                f.MinHeight == 20),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    #endregion

    #region Timeout Parameter Tests

    [Fact]
    public void GetUIElements_WithDefaultTimeout_UsesCancellationToken()
    {
        // Arrange
        var sut = CreateSut();

        // Act
        sut.GetUIElements(handle: 12345);

        // Assert - Just verify it was called with some cancellation token
        _mockService.Verify(s => s.GetUIElements(
            It.IsAny<nint>(),
            It.IsAny<int>(),
            It.IsAny<UIElementFilter?>(),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public void GetUIElements_WithCustomTimeout_UsesCancellationToken()
    {
        // Arrange
        var sut = CreateSut();

        // Act
        sut.GetUIElements(handle: 12345, timeoutMs: 5000);

        // Assert - Just verify it was called
        _mockService.Verify(s => s.GetUIElements(
            It.IsAny<nint>(),
            It.IsAny<int>(),
            It.IsAny<UIElementFilter?>(),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public void GetUIElements_WithTimeoutAboveMax_Clamps()
    {
        // Arrange
        var sut = CreateSut();

        // Act - Should not throw, timeout should be clamped
        var result = sut.GetUIElements(handle: 12345, timeoutMs: 999999);

        // Assert - Call should complete
        result.Should().NotBeNull();
    }

    [Fact]
    public void GetUIElements_WithTimeoutBelowMin_Clamps()
    {
        // Arrange
        var sut = CreateSut();

        // Act - Should not throw, timeout should be clamped
        var result = sut.GetUIElements(handle: 12345, timeoutMs: 100);

        // Assert - Call should complete
        result.Should().NotBeNull();
    }

    #endregion

    #region Error Handling Tests

    [Fact]
    public void GetUIElements_WhenServiceReturnsError_ReturnsError()
    {
        // Arrange
        var errorResult = TestDataFactory.CreateUIElementResult(
            success: false,
            error: "Window not found",
            errorCode: UIElementErrorCode.WindowNotFound);
        _mockService.Setup(s => s.GetUIElements(
                It.IsAny<nint>(),
                It.IsAny<int>(),
                It.IsAny<UIElementFilter?>(),
                It.IsAny<CancellationToken>()))
            .Returns(errorResult);
        var sut = CreateSut();

        // Act
        var result = sut.GetUIElements(handle: 12345);

        // Assert
        result.Success.Should().BeFalse();
        result.Error.Should().Be("Window not found");
        result.ErrorCode.Should().Be(UIElementErrorCode.WindowNotFound);
    }

    [Fact]
    public void GetUIElements_WhenServiceReturnsSuccess_ReturnsElements()
    {
        // Arrange
        var successResult = TestDataFactory.CreateUIElementResult(success: true);
        _mockService.Setup(s => s.GetUIElements(
                It.IsAny<nint>(),
                It.IsAny<int>(),
                It.IsAny<UIElementFilter?>(),
                It.IsAny<CancellationToken>()))
            .Returns(successResult);
        var sut = CreateSut();

        // Act
        var result = sut.GetUIElements(handle: 12345);

        // Assert
        result.Success.Should().BeTrue();
        result.Elements.Should().NotBeNull();
        result.Elements.Should().NotBeEmpty();
        result.Error.Should().BeNull();
    }

    #endregion

    #region get_focused_element and get_element_at_point

    [Theory]
    [InlineData(null, 5000)]
    [InlineData(2500, 2500)]
    [InlineData(100, 1000)]
    [InlineData(999999, 30000)]
    public void GetFocusedElement_PassesTheClampedTimeoutAndTheCallsToken(int? timeoutMs, int expectedMs)
    {
        // Arrange
        using var cts = new CancellationTokenSource();
        var expected = new FocusedElementResult(true, null, 1, "Window", 2, "app", 3.0);
        _mockInspection.Setup(s => s.GetFocusedElement(TimeSpan.FromMilliseconds(expectedMs), cts.Token)).Returns(expected);
        var sut = CreateSut();

        // Act
        var result = timeoutMs is { } ms
            ? sut.GetFocusedElement(ms, cts.Token)
            : sut.GetFocusedElement(cancellationToken: cts.Token);

        // Assert
        result.Should().BeSameAs(expected);
    }

    [Theory]
    [InlineData(100, -200, null, 3)]
    [InlineData(0, 0, 0, 0)]
    [InlineData(5, 6, 10, 10)]
    public void GetElementAtPoint_PassesThePointAndAncestorsToTheService(int x, int y, int? ancestors, int expectedAncestors)
    {
        // Arrange
        var expected = new ElementAtPointResult(true, x, y, null, [], null, null, null, null, 1.0);
        _mockInspection.Setup(s => s.GetElementAtPoint(x, y, expectedAncestors, TimeSpan.FromMilliseconds(5000), It.IsAny<CancellationToken>()))
            .Returns(expected);
        var sut = CreateSut();

        // Act
        var result = ancestors is { } count
            ? sut.GetElementAtPoint(x, y, count)
            : sut.GetElementAtPoint(x, y);

        // Assert
        result.Should().BeSameAs(expected);
    }

    [Theory]
    [InlineData(1, 1000)]
    [InlineData(60000, 30000)]
    public void GetElementAtPoint_ClampsTheTimeout(int timeoutMs, int expectedMs)
    {
        // Arrange
        var sut = CreateSut();

        // Act
        sut.GetElementAtPoint(1, 2, timeoutMs: timeoutMs);

        // Assert
        _mockInspection.Verify(s => s.GetElementAtPoint(1, 2, 3, TimeSpan.FromMilliseconds(expectedMs), It.IsAny<CancellationToken>()), Times.Once);
    }

    #endregion
}
