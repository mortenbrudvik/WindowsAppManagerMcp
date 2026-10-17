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
    public void TheTextFormatsExample_IsALineAsTheFormatWritesIt_RefFirst()
    {
        // Arrange - the format parameter's description is what an agent reads about the cheapest format
        var format = typeof(UIElementTools).GetMethod(nameof(UIElementTools.GetUIElements))!.GetParameters()
            .Single(p => p.Name == "format");
        var description = format.GetCustomAttributes(typeof(System.ComponentModel.DescriptionAttribute), false)
            .Cast<System.ComponentModel.DescriptionAttribute>().Single().Description;
        var example = description.Split('"')[1];

        // Act
        var line = UIElementTextFormat.Line(new UIElementInfo("Save", "Button", "SaveBtn", new BoundsDto(812, 44, 80, 24),
            IsEnabled: false, IsOffscreen: false, Ref: "w4120035e17"));

        // Assert
        description.Should().Contain("ref").And.Contain("fw=");
        example.Should().Be(line);
    }

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
        var result = await sut.GetUIElements(handle: 12345, format: UIElementFormat.Tree);

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
        var result = await sut.GetUIElements(handle: 12345, timeoutMs: 1, format: UIElementFormat.Tree);

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
        var result = await sut.GetUIElements(handle: 12345, format: UIElementFormat.Tree);

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
        var result = await sut.GetUIElements(handle: 12345, format: UIElementFormat.Tree);

        // Assert
        result.Success.Should().BeTrue();
        result.Elements.Should().NotBeNull();
        result.Elements.Should().NotBeEmpty();
        result.Error.Should().BeNull();
    }

    #endregion

    #region maxElements and format (roadmap M3.1)

    /// <summary>
    /// Window
    ///   ToolBar "Tools" > Button "Save" (aid SaveBtn, disabled), Button "It's \"open\"\n" (no bounds)
    ///   Edit (no name, aid Name)
    /// </summary>
    private void ReturnTree()
    {
        var tree = new List<UIElementInfo>
        {
            new("Tools", "ToolBar", null, new BoundsDto(0, 0, 200, 30), true, false,
            [
                new("Save", "Button", "SaveBtn", new BoundsDto(812, 44, 80, 24), IsEnabled: false, IsOffscreen: false),
                new("It's \"open\"\n", "Button", null, null, true, IsOffscreen: true)
            ]),
            new(null, "Edit", "Name", new BoundsDto(-1920, 10, 300, 20), true, false)
        };
        _mockService.Setup(s => s.GetUIElementsAsync(
                It.IsAny<nint>(), It.IsAny<int>(), It.IsAny<UIElementFilter?>(), It.IsAny<IProgress<OperationProgress>?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(TestDataFactory.CreateUIElementResult(elements: tree) with { TotalElementCount = 4 });
    }

    [Theory]
    [InlineData(null, 300)]
    [InlineData(25, 25)]
    [InlineData(0, 1)]
    [InlineData(99999, 5000)]
    public async Task GetUIElements_PassesTheClampedMaxElements(int? maxElements, int expected)
    {
        // Act
        await (maxElements is { } max ? CreateSut().GetUIElements(handle: 12345, maxElements: max) : CreateSut().GetUIElements(handle: 12345));

        // Assert
        _mockService.Verify(s => s.GetUIElementsAsync(
            It.IsAny<nint>(), It.IsAny<int>(), It.Is<UIElementFilter?>(f => f!.MaxElements == expected),
            It.IsAny<IProgress<OperationProgress>?>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task GetUIElements_TextFormat_IsTheDefault()
    {
        // Owner decision D3 (3.0.0): the compact text costs the fewest tokens
        ReturnTree();

        var defaulted = await CreateSut().GetUIElements(handle: 12345);
        var text = await CreateSut().GetUIElements(handle: 12345, format: UIElementFormat.Text);

        defaulted.Elements.Should().BeNull();
        defaulted.Text.Should().NotBeNullOrEmpty().And.Be(text.Text);
        typeof(UIElementTools).GetMethod(nameof(UIElementTools.GetUIElements))!.GetParameters().Single(p => p.Name == "format")
            .DefaultValue.Should().Be(UIElementFormat.Text);
    }

    [Fact]
    public async Task GetUIElements_TreeFormat_NestsTheElements()
    {
        ReturnTree();

        var result = await CreateSut().GetUIElements(handle: 12345, format: UIElementFormat.Tree);

        result.Elements!.Select(e => e.Name).Should().Equal("Tools", null);
        result.Elements![0].Children.Should().HaveCount(2);
        result.Elements.Should().OnlyContain(e => e.Depth == null && e.Parent == null);
        result.Text.Should().BeNull();
    }

    [Fact]
    public async Task GetUIElements_FlatFormat_ListsEveryElementOnceWithItsDepthAndParent()
    {
        ReturnTree();

        var result = await CreateSut().GetUIElements(handle: 12345, format: UIElementFormat.Flat);

        // Reading order: each element, then the elements inside it
        result.Elements!.Select(e => (e.Name, e.Depth, e.Parent)).Should().Equal(
            ("Tools", 1, (int?)null), ("Save", 2, 0), ("It's \"open\"\n", 2, 0), (null, 1, null));
        result.Elements.Should().OnlyContain(e => e.Children == null);
        result.TotalElementCount.Should().Be(4);
        result.Text.Should().BeNull();
    }

    [Fact]
    public async Task GetUIElements_TextFormat_GivesOneCompactLinePerElement_IndentedByDepth()
    {
        ReturnTree();

        var result = await CreateSut().GetUIElements(handle: 12345, format: UIElementFormat.Text);

        // Names are quoted with ' (escaped inside), control characters become spaces, and only flags that are set appear
        result.Elements.Should().BeNull();
        result.Text.Should().Be(
            "ToolBar 'Tools' @0,0 200x30\n" +
            "  Button 'Save' aid=SaveBtn @812,44 80x24 [disabled]\n" +
            "  Button 'It\\'s \"open\" ' [offscreen]\n" +
            "Edit aid=Name @-1920,10 300x20");
        result.Success.Should().BeTrue();
    }

    [Fact]
    public void TextFormat_CutsLongNames_AndQuotesAutomationIdsWithSpaces()
    {
        var element = new UIElementInfo(new string('x', 150), "Text", "my id", null, true, false);

        var line = UIElementTextFormat.Line(element);

        line.Should().Be($"Text '{new string('x', UIElementTextFormat.MaxNameLength)}…' aid='my id'");
    }

    [Theory]
    [InlineData("[disabled]", "aid='[disabled]'")]
    [InlineData("@1,2", "aid='@1,2'")]
    [InlineData("row]", "aid='row]'")]
    [InlineData("Save_Btn-1", "aid=Save_Btn-1")]
    public void TextFormat_QuotesAutomationIdsThatCouldReadAsBoundsOrFlags(string automationId, string expected)
    {
        var element = new UIElementInfo(null, "Button", automationId, null, true, false);

        UIElementTextFormat.Line(element).Should().Be($"Button {expected}");
    }

    [Theory]
    [InlineData("a\u2028b")]
    [InlineData("a\u2029b")]
    [InlineData("a\u0085b")]
    public void TextFormat_NeverLetsANameBreakTheLine(string name)
    {
        // A line or paragraph separator reads as a new line in many viewers, as a control character does
        var element = new UIElementInfo(name, "Button", name, null, true, false);

        UIElementTextFormat.Line(element).Should().Be("Button 'a b' aid='a b'");
    }

    [Fact]
    public void TextFormat_NamesTheFrameworkWhereItChanges_BeforeTheBounds()
    {
        var element = new UIElementInfo("Inbox", "Document", null, new BoundsDto(10, 20, 300, 200), true, false, FrameworkId: "Chrome");

        UIElementTextFormat.Line(element).Should().Be("Document 'Inbox' fw=Chrome @10,20 300x200");
    }

    [Theory]
    [InlineData(10_000, 1_500)]
    [InlineData(1_500, 500)]
    [InlineData(100, 333)]
    public async Task GetUIElements_GivesTheWarmupAThirdOfTheTimeout_AtMost1500Ms(int timeoutMs, int expectedBudgetMs)
    {
        // Arrange
        UIElementFilter? passed = null;
        _mockService.Setup(s => s.GetUIElementsAsync(
                It.IsAny<nint>(), It.IsAny<int>(), It.IsAny<UIElementFilter?>(), It.IsAny<IProgress<OperationProgress>?>(),
                It.IsAny<CancellationToken>()))
            .Callback((nint _, int _, UIElementFilter? filter, IProgress<OperationProgress>? _, CancellationToken _) => passed = filter)
            .ReturnsAsync(TestDataFactory.CreateUIElementResult());

        // Act - the timeout is clamped to 1000-30000 first
        await CreateSut().GetUIElements(handle: 12345, timeoutMs: timeoutMs);

        // Assert
        passed!.AccessibilityWarmupBudget!.Value.TotalMilliseconds.Should().BeApproximately(expectedBudgetMs, 1);
    }

    [Fact]
    public async Task GetUIElements_TextFormat_OfAFailedWalk_KeepsTheError()
    {
        _mockService.Setup(s => s.GetUIElementsAsync(
                It.IsAny<nint>(), It.IsAny<int>(), It.IsAny<UIElementFilter?>(), It.IsAny<IProgress<OperationProgress>?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(TestDataFactory.CreateUIElementResult(success: false, error: "Window not found"));

        var result = await CreateSut().GetUIElements(handle: 12345, format: UIElementFormat.Text);

        result.Success.Should().BeFalse();
        result.Error.Should().Be("Window not found");
        result.Text.Should().BeNull();
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
        _mockInspection.Setup(s => s.GetElementAtPointAsync(1, 2, 3, It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ElementAtPointResult(true, 1, 2, null, [], null, null, null, null, 1.0));
        var sut = CreateSut();

        // Act
        await sut.GetElementAtPoint(1, 2, timeoutMs: timeoutMs);

        // Assert
        _mockInspection.Verify(s => s.GetElementAtPointAsync(1, 2, 3, TimeSpan.FromMilliseconds(expectedMs), It.IsAny<CancellationToken>()), Times.Once);
    }

    #endregion

    #region Bounded AutomationIds and titles (roadmap M3.F31)

    private static readonly string LongId = "row-" + new string('7', 5000);
    private static readonly string LongTitle = "Report - " + new string('t', 5000);
    private static readonly string CutId = LongId[..(WindowsAppManagerMcp.Infrastructure.AppText.MaxLength - 1)] + "…";
    private static readonly string CutTitle = LongTitle[..(WindowsAppManagerMcp.Infrastructure.AppText.MaxLength - 1)] + "…";

    private static UIElementDetails Details(string? automationId) =>
        new("Row", "ListItem", automationId, null, null, null, true, false, true, true, false, []);

    [Theory]
    [InlineData(UIElementFormat.Tree)]
    [InlineData(UIElementFormat.Flat)]
    public async Task GetUIElements_CutsALongWindowTitle_AndTheAutomationIdsOfTheWholeTree(UIElementFormat format)
    {
        // Arrange - an application chooses both, down to an element deep in the tree
        var deep = new UIElementInfo("Cell", "DataItem", LongId, null, IsEnabled: true, IsOffscreen: false);
        var row = new UIElementInfo("Row", "ListItem", "row-1", null, IsEnabled: true, IsOffscreen: false, Children: [deep]);
        _mockService.Setup(s => s.GetUIElementsAsync(It.IsAny<nint>(), It.IsAny<int>(), It.IsAny<UIElementFilter?>(),
                It.IsAny<IProgress<OperationProgress>?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new UIElementResult(true, 100, LongTitle, [row], 2, 5, 1.0));

        // Act
        var result = await CreateSut().GetUIElements(handle: 100, format: format);

        // Assert
        result.WindowTitle.Should().Be(CutTitle);
        Flatten(result.Elements!).Select(e => e.AutomationId).Should().Contain(CutId).And.Contain("row-1").And.NotContain(LongId);
    }

    [Fact]
    public async Task GetUIElements_InTheTextFormat_WritesTheCutAutomationId()
    {
        // Arrange
        _mockService.Setup(s => s.GetUIElementsAsync(It.IsAny<nint>(), It.IsAny<int>(), It.IsAny<UIElementFilter?>(),
                It.IsAny<IProgress<OperationProgress>?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new UIElementResult(true, 100, "Report", [new UIElementInfo("Row", "ListItem", LongId, null, IsEnabled: true, IsOffscreen: false)], 1, 5, 1.0));

        // Act
        var result = await CreateSut().GetUIElements(handle: 100, format: UIElementFormat.Text);

        // Assert
        result.Text.Should().Contain(CutId).And.NotContain(LongId);
    }

    [Fact]
    public async Task GetUIElements_WithNothingToCut_ReturnsTheServicesResult()
    {
        // Arrange
        var expected = new UIElementResult(true, 100, "Report", [new UIElementInfo("Row", "ListItem", "row-1", null, IsEnabled: true, IsOffscreen: false)], 1, 5, 1.0);
        _mockService.Setup(s => s.GetUIElementsAsync(It.IsAny<nint>(), It.IsAny<int>(), It.IsAny<UIElementFilter?>(),
                It.IsAny<IProgress<OperationProgress>?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(expected);

        // Act
        var result = await CreateSut().GetUIElements(handle: 100, format: UIElementFormat.Tree);

        // Assert
        result.Should().BeSameAs(expected);
    }

    [Fact]
    public async Task GetFocusedElement_CutsALongAutomationIdAndWindowTitle()
    {
        // Arrange
        _mockInspection.Setup(s => s.GetFocusedElementAsync(It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new FocusedElementResult(true, Details(LongId), 100, LongTitle, 2, "app", 1.0));

        // Act
        var result = await CreateSut().GetFocusedElement();

        // Assert
        result.Element!.AutomationId.Should().Be(CutId);
        result.WindowTitle.Should().Be(CutTitle);
    }

    [Fact]
    public async Task GetElementAtPoint_CutsTheAutomationIdsOfTheElementAndItsAncestors_AndTheWindowTitle()
    {
        // Arrange
        var ancestor = new UIElementAncestor("List", "List", LongId, null, null);
        _mockInspection.Setup(s => s.GetElementAtPointAsync(1, 2, It.IsAny<int>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ElementAtPointResult(true, 1, 2, Details(LongId), [ancestor], 100, LongTitle, 2, "app", 1.0));

        // Act
        var result = await CreateSut().GetElementAtPoint(1, 2);

        // Assert
        result.Element!.AutomationId.Should().Be(CutId);
        result.Ancestors!.Single().AutomationId.Should().Be(CutId);
        result.WindowTitle.Should().Be(CutTitle);
    }

    private static IEnumerable<UIElementInfo> Flatten(IEnumerable<UIElementInfo> elements) =>
        elements.SelectMany(e => new[] { e }.Concat(Flatten(e.Children ?? [])));

    #endregion
}
