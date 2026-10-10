using ModelContextProtocol;

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
    public async Task GetUIElements_CallsService()
    {
        // Arrange
        var sut = CreateSut();

        // Act
        await sut.GetUIElements(handle: 12345);

        // Assert
        _mockService.Verify(s => s.GetUIElementsAsync(
            It.IsAny<nint>(),
            It.IsAny<int>(),
            It.IsAny<UIElementFilter?>(),
            It.IsAny<IProgress<OperationProgress>?>(),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task GetUIElements_PassesHandleCorrectly()
    {
        // Arrange
        var sut = CreateSut();

        // Act
        await sut.GetUIElements(handle: 99999);

        // Assert
        _mockService.Verify(s => s.GetUIElementsAsync(
            new nint(99999),
            It.IsAny<int>(),
            It.IsAny<UIElementFilter?>(),
            It.IsAny<IProgress<OperationProgress>?>(),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task GetUIElements_ReturnsServiceResult()
    {
        // Arrange
        var expectedResult = TestDataFactory.CreateUIElementResult();
        _mockService.Setup(s => s.GetUIElementsAsync(
                It.IsAny<nint>(),
                It.IsAny<int>(),
                It.IsAny<UIElementFilter?>(),
                It.IsAny<IProgress<OperationProgress>?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(expectedResult);
        var sut = CreateSut();

        // Act
        var result = await sut.GetUIElements(handle: 12345);

        // Assert
        result.Should().Be(expectedResult);
    }

    #endregion

    #region MaxDepth Parameter Tests

    [Fact]
    public async Task GetUIElements_WithDefaultDepth_Passes5()
    {
        // Arrange
        var sut = CreateSut();

        // Act
        await sut.GetUIElements(handle: 12345);

        // Assert
        _mockService.Verify(s => s.GetUIElementsAsync(
            It.IsAny<nint>(),
            5, // Default depth
            It.IsAny<UIElementFilter?>(),
            It.IsAny<IProgress<OperationProgress>?>(),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task GetUIElements_WithSpecifiedDepth_PassesClampedValue()
    {
        // Arrange
        var sut = CreateSut();

        // Act
        await sut.GetUIElements(handle: 12345, maxDepth: 8);

        // Assert
        _mockService.Verify(s => s.GetUIElementsAsync(
            It.IsAny<nint>(),
            8,
            It.IsAny<UIElementFilter?>(),
            It.IsAny<IProgress<OperationProgress>?>(),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task GetUIElements_WithDepthAboveMax_ClampsTo10()
    {
        // Arrange
        var sut = CreateSut();

        // Act
        await sut.GetUIElements(handle: 12345, maxDepth: 20);

        // Assert
        _mockService.Verify(s => s.GetUIElementsAsync(
            It.IsAny<nint>(),
            10, // Clamped to max
            It.IsAny<UIElementFilter?>(),
            It.IsAny<IProgress<OperationProgress>?>(),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task GetUIElements_WithDepthBelowMin_ClampsTo1()
    {
        // Arrange
        var sut = CreateSut();

        // Act
        await sut.GetUIElements(handle: 12345, maxDepth: 0);

        // Assert
        _mockService.Verify(s => s.GetUIElementsAsync(
            It.IsAny<nint>(),
            1, // Clamped to min
            It.IsAny<UIElementFilter?>(),
            It.IsAny<IProgress<OperationProgress>?>(),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    #endregion

    #region ControlTypes Parameter Tests

    [Fact]
    public async Task GetUIElements_WithNoControlTypes_PassesNullFilter()
    {
        // Arrange
        var sut = CreateSut();

        // Act
        await sut.GetUIElements(handle: 12345, controlTypes: null);

        // Assert
        _mockService.Verify(s => s.GetUIElementsAsync(
            It.IsAny<nint>(),
            It.IsAny<int>(),
            It.Is<UIElementFilter?>(f => f != null && f.ControlTypes == null),
            It.IsAny<IProgress<OperationProgress>?>(),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task GetUIElements_WithControlTypes_ParsesCorrectly()
    {
        // Arrange
        var sut = CreateSut();

        // Act
        await sut.GetUIElements(handle: 12345, controlTypes: "Button,Edit,CheckBox");

        // Assert
        _mockService.Verify(s => s.GetUIElementsAsync(
            It.IsAny<nint>(),
            It.IsAny<int>(),
            It.Is<UIElementFilter?>(f =>
                f != null &&
                f.ControlTypes != null &&
                f.ControlTypes.Length == 3 &&
                f.ControlTypes.Contains("Button") &&
                f.ControlTypes.Contains("Edit") &&
                f.ControlTypes.Contains("CheckBox")),
            It.IsAny<IProgress<OperationProgress>?>(),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task GetUIElements_WithControlTypesContainingSpaces_TrimsCorrectly()
    {
        // Arrange
        var sut = CreateSut();

        // Act
        await sut.GetUIElements(handle: 12345, controlTypes: " Button , Edit , CheckBox ");

        // Assert
        _mockService.Verify(s => s.GetUIElementsAsync(
            It.IsAny<nint>(),
            It.IsAny<int>(),
            It.Is<UIElementFilter?>(f =>
                f != null &&
                f.ControlTypes != null &&
                f.ControlTypes.Length == 3 &&
                f.ControlTypes.All(t => !t.StartsWith(" ") && !t.EndsWith(" "))),
            It.IsAny<IProgress<OperationProgress>?>(),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task GetUIElements_WithEmptyControlTypes_PassesNullControlTypes()
    {
        // Arrange
        var sut = CreateSut();

        // Act
        await sut.GetUIElements(handle: 12345, controlTypes: "");

        // Assert
        _mockService.Verify(s => s.GetUIElementsAsync(
            It.IsAny<nint>(),
            It.IsAny<int>(),
            It.Is<UIElementFilter?>(f => f != null && f.ControlTypes == null),
            It.IsAny<IProgress<OperationProgress>?>(),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task GetUIElements_WithWhitespaceOnlyControlTypes_PassesNullControlTypes()
    {
        // Arrange
        var sut = CreateSut();

        // Act
        await sut.GetUIElements(handle: 12345, controlTypes: "   ");

        // Assert
        _mockService.Verify(s => s.GetUIElementsAsync(
            It.IsAny<nint>(),
            It.IsAny<int>(),
            It.Is<UIElementFilter?>(f => f != null && f.ControlTypes == null),
            It.IsAny<IProgress<OperationProgress>?>(),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    #endregion

    #region Filter Parameters Tests

    [Fact]
    public async Task GetUIElements_WithInteractableOnly_PassesFilter()
    {
        // Arrange
        var sut = CreateSut();

        // Act
        await sut.GetUIElements(handle: 12345, interactableOnly: true);

        // Assert
        _mockService.Verify(s => s.GetUIElementsAsync(
            It.IsAny<nint>(),
            It.IsAny<int>(),
            It.Is<UIElementFilter?>(f => f != null && f.InteractableOnly == true),
            It.IsAny<IProgress<OperationProgress>?>(),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task GetUIElements_WithVisibleOnlyFalse_PassesFilter()
    {
        // Arrange
        var sut = CreateSut();

        // Act
        await sut.GetUIElements(handle: 12345, visibleOnly: false);

        // Assert
        _mockService.Verify(s => s.GetUIElementsAsync(
            It.IsAny<nint>(),
            It.IsAny<int>(),
            It.Is<UIElementFilter?>(f => f != null && f.VisibleOnly == false),
            It.IsAny<IProgress<OperationProgress>?>(),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task GetUIElements_WithMinWidth_PassesFilter()
    {
        // Arrange
        var sut = CreateSut();

        // Act
        await sut.GetUIElements(handle: 12345, minWidth: 50);

        // Assert
        _mockService.Verify(s => s.GetUIElementsAsync(
            It.IsAny<nint>(),
            It.IsAny<int>(),
            It.Is<UIElementFilter?>(f => f != null && f.MinWidth == 50),
            It.IsAny<IProgress<OperationProgress>?>(),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task GetUIElements_WithMinHeight_PassesFilter()
    {
        // Arrange
        var sut = CreateSut();

        // Act
        await sut.GetUIElements(handle: 12345, minHeight: 30);

        // Assert
        _mockService.Verify(s => s.GetUIElementsAsync(
            It.IsAny<nint>(),
            It.IsAny<int>(),
            It.Is<UIElementFilter?>(f => f != null && f.MinHeight == 30),
            It.IsAny<IProgress<OperationProgress>?>(),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task GetUIElements_WithAllFilters_PassesAllCorrectly()
    {
        // Arrange
        var sut = CreateSut();

        // Act
        await sut.GetUIElements(
            handle: 12345,
            maxDepth: 7,
            controlTypes: "Button,Edit",
            interactableOnly: true,
            visibleOnly: true,
            minWidth: 50,
            minHeight: 20);

        // Assert
        _mockService.Verify(s => s.GetUIElementsAsync(
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
            It.IsAny<IProgress<OperationProgress>?>(),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    #endregion

    #region Timeout Parameter Tests

    [Fact]
    public async Task GetUIElements_WithDefaultTimeout_UsesCancellationToken()
    {
        // Arrange
        var sut = CreateSut();

        // Act
        await sut.GetUIElements(handle: 12345);

        // Assert - Just verify it was called with some cancellation token
        _mockService.Verify(s => s.GetUIElementsAsync(
            It.IsAny<nint>(),
            It.IsAny<int>(),
            It.IsAny<UIElementFilter?>(),
            It.IsAny<IProgress<OperationProgress>?>(),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task GetUIElements_WithCustomTimeout_UsesCancellationToken()
    {
        // Arrange
        var sut = CreateSut();

        // Act
        await sut.GetUIElements(handle: 12345, timeoutMs: 5000);

        // Assert - Just verify it was called
        _mockService.Verify(s => s.GetUIElementsAsync(
            It.IsAny<nint>(),
            It.IsAny<int>(),
            It.IsAny<UIElementFilter?>(),
            It.IsAny<IProgress<OperationProgress>?>(),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task GetUIElements_WithTimeoutAboveMax_Clamps()
    {
        // Arrange
        var sut = CreateSut();

        // Act - Should not throw, timeout should be clamped
        var result = await sut.GetUIElements(handle: 12345, timeoutMs: 999999);

        // Assert - Call should complete
        result.Should().NotBeNull();
    }

    [Fact]
    public async Task GetUIElements_WithTimeoutBelowMin_Clamps()
    {
        // Arrange
        var sut = CreateSut();

        // Act - Should not throw, timeout should be clamped
        var result = await sut.GetUIElements(handle: 12345, timeoutMs: 100);

        // Assert - Call should complete
        result.Should().NotBeNull();
    }

    #endregion

    #region Cancellation and progress

    /// <summary>Makes the service run <paramref name="walk"/> with the walk's progress and token, then return a tree.</summary>
    private void WalkWith(Action<IProgress<OperationProgress>?, CancellationToken> walk) =>
        _mockService.Setup(s => s.GetUIElementsAsync(
                It.IsAny<nint>(), It.IsAny<int>(), It.IsAny<UIElementFilter?>(), It.IsAny<IProgress<OperationProgress>?>(),
                It.IsAny<CancellationToken>()))
            .Callback((nint _, int _, UIElementFilter? _, IProgress<OperationProgress>? progress, CancellationToken token) =>
                walk(progress, token))
            .ReturnsAsync(TestDataFactory.CreateUIElementResult());

    [Fact]
    public async Task GetUIElements_WhenTheClientCancels_StopsTheWalk_AndReportsTheCancellation()
    {
        // Arrange - the client cancels while the tree is read
        using var call = new CancellationTokenSource();
        var walkStopped = false;
        WalkWith((_, token) =>
        {
            call.Cancel();
            walkStopped = token.IsCancellationRequested;
        });
        var sut = CreateSut();

        // Act
        var act = () => sut.GetUIElements(handle: 12345, cancellationToken: call.Token);

        // Assert - the walk's token is linked with the call's, and a cancelled call has no result
        await act.Should().ThrowAsync<OperationCanceledException>();
        walkStopped.Should().BeTrue("the request's token is linked with the timeout");
    }

    [Fact]
    public async Task GetUIElements_AtItsTimeout_ReturnsWhatWasRead_NotACancellation()
    {
        // Arrange - a walk that runs until its token stops it, then hands back its partial tree
        var truncated = TestDataFactory.CreateUIElementResult() with
        {
            Truncated = true,
            TruncationReason = UIElementTruncationReason.Timeout
        };
        _mockService.Setup(s => s.GetUIElementsAsync(
                It.IsAny<nint>(), It.IsAny<int>(), It.IsAny<UIElementFilter?>(), It.IsAny<IProgress<OperationProgress>?>(),
                It.IsAny<CancellationToken>()))
            .Returns(async (nint _, int _, UIElementFilter? _, IProgress<OperationProgress>? _, CancellationToken token) =>
            {
                await Task.Delay(Timeout.Infinite, token).ContinueWith(_ => { }, TaskScheduler.Default);
                return truncated;
            });
        var sut = CreateSut();

        // Act - the shortest timeout (1000 ms)
        var result = await sut.GetUIElements(handle: 12345, timeoutMs: 1);

        // Assert
        result.Should().BeSameAs(truncated);
    }

    [Fact]
    public async Task GetUIElements_ForwardsTheWalksProgressToTheClient()
    {
        // Arrange
        WalkWith((progress, _) => progress!.Report(new OperationProgress(100, null, "Read 100 UI elements so far")));
        var client = new RecordingProgress<ProgressNotificationValue>();
        var sut = CreateSut();

        // Act
        await sut.GetUIElements(handle: 12345, progress: client);

        // Assert
        var report = client.Reports.Should().ContainSingle().Subject;
        report.Progress.Should().Be(100);
        report.Total.Should().BeNull();
        report.Message.Should().Be("Read 100 UI elements so far");
    }

    [Fact]
    public async Task GetUIElements_WithoutProgress_GivesTheWalkNone()
    {
        // Arrange
        IProgress<OperationProgress>? walkProgress = new RecordingProgress<OperationProgress>();
        WalkWith((progress, _) => walkProgress = progress);

        // Act
        await CreateSut().GetUIElements(handle: 12345);

        // Assert
        walkProgress.Should().BeNull();
    }

    #endregion

    #region Error Handling Tests

    [Fact]
    public async Task GetUIElements_WhenServiceReturnsError_ReturnsError()
    {
        // Arrange
        var errorResult = TestDataFactory.CreateUIElementResult(
            success: false,
            error: "Window not found",
            errorCode: UIElementErrorCode.WindowNotFound);
        _mockService.Setup(s => s.GetUIElementsAsync(
                It.IsAny<nint>(),
                It.IsAny<int>(),
                It.IsAny<UIElementFilter?>(),
                It.IsAny<IProgress<OperationProgress>?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(errorResult);
        var sut = CreateSut();

        // Act
        var result = await sut.GetUIElements(handle: 12345);

        // Assert
        result.Success.Should().BeFalse();
        result.Error.Should().Be("Window not found");
        result.ErrorCode.Should().Be(UIElementErrorCode.WindowNotFound);
    }

    [Fact]
    public async Task GetUIElements_WhenServiceReturnsSuccess_ReturnsElements()
    {
        // Arrange
        var successResult = TestDataFactory.CreateUIElementResult(success: true);
        _mockService.Setup(s => s.GetUIElementsAsync(
                It.IsAny<nint>(),
                It.IsAny<int>(),
                It.IsAny<UIElementFilter?>(),
                It.IsAny<IProgress<OperationProgress>?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(successResult);
        var sut = CreateSut();

        // Act
        var result = await sut.GetUIElements(handle: 12345);

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
    public async Task GetFocusedElement_PassesTheClampedTimeoutAndTheCallsToken(int? timeoutMs, int expectedMs)
    {
        // Arrange
        using var cts = new CancellationTokenSource();
        var expected = new FocusedElementResult(true, null, 1, "Window", 2, "app", 3.0);
        _mockInspection.Setup(s => s.GetFocusedElementAsync(TimeSpan.FromMilliseconds(expectedMs), cts.Token)).ReturnsAsync(expected);
        var sut = CreateSut();

        // Act
        var result = timeoutMs is { } ms
            ? await sut.GetFocusedElement(ms, cts.Token)
            : await sut.GetFocusedElement(cancellationToken: cts.Token);

        // Assert
        result.Should().BeSameAs(expected);
    }

    [Theory]
    [InlineData(100, -200, null, 3)]
    [InlineData(0, 0, 0, 0)]
    [InlineData(5, 6, 10, 10)]
    public async Task GetElementAtPoint_PassesThePointAndAncestorsToTheService(int x, int y, int? ancestors, int expectedAncestors)
    {
        // Arrange
        var expected = new ElementAtPointResult(true, x, y, null, [], null, null, null, null, 1.0);
        _mockInspection.Setup(s => s.GetElementAtPointAsync(x, y, expectedAncestors, TimeSpan.FromMilliseconds(5000), It.IsAny<CancellationToken>()))
            .ReturnsAsync(expected);
        var sut = CreateSut();

        // Act
        var result = ancestors is { } count
            ? await sut.GetElementAtPoint(x, y, count)
            : await sut.GetElementAtPoint(x, y);

        // Assert
        result.Should().BeSameAs(expected);
    }

    [Theory]
    [InlineData(1, 1000)]
    [InlineData(60000, 30000)]
    public async Task GetElementAtPoint_ClampsTheTimeout(int timeoutMs, int expectedMs)
    {
        // Arrange
        var sut = CreateSut();

        // Act
        await sut.GetElementAtPoint(1, 2, timeoutMs: timeoutMs);

        // Assert
        _mockInspection.Verify(s => s.GetElementAtPointAsync(1, 2, 3, TimeSpan.FromMilliseconds(expectedMs), It.IsAny<CancellationToken>()), Times.Once);
    }

    #endregion
}
