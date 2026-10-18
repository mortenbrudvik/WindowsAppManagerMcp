using ModelContextProtocol;

namespace WindowsAppManagerMcp.Tests.Unit.Tools;

using WindowsAppManagerMcp.Infrastructure;
using WindowsAppManagerMcp.Tools;

/// <summary>
/// Unit tests for LayoutPresetTools.
/// Tests the MCP tool layer that exposes layout preset functionality.
/// </summary>
public class LayoutPresetToolsTests
{
    private readonly Mock<ILayoutService> _mockLayoutService;

    public LayoutPresetToolsTests()
    {
        _mockLayoutService = new Mock<ILayoutService>();

        // No file in the folder is invalid (LayoutPresetToolsInvalidFileTests covers those that are)
        _mockLayoutService.Setup(l => l.GetInvalidFiles()).Returns([]);
    }

    private LayoutPresetTools CreateSut() => new LayoutPresetTools(_mockLayoutService.Object);

    #region ListLayouts Tests

    [Fact]
    public void ListLayouts_CallsService()
    {
        // Arrange
        _mockLayoutService.Setup(l => l.GetAllPresets())
            .Returns(new List<LayoutPreset>());
        var sut = CreateSut();

        // Act
        sut.ListLayouts();

        // Assert
        _mockLayoutService.Verify(l => l.GetAllPresets(), Times.Once);
    }

    [Fact]
    public void ListLayouts_ReturnsEmptyList_WhenNoPresets()
    {
        // Arrange
        _mockLayoutService.Setup(l => l.GetAllPresets())
            .Returns(new List<LayoutPreset>());
        var sut = CreateSut();

        // Act
        var result = sut.ListLayouts();

        // Assert
        result.Should().BeEmpty();
    }

    [Fact]
    public void ListLayouts_MapsPresetToSummaryDto()
    {
        // Arrange
        var preset = TestDataFactory.CreateLayoutPreset(
            name: "Development",
            description: "Dev layout");
        _mockLayoutService.Setup(l => l.GetAllPresets())
            .Returns(new List<LayoutPreset> { preset });
        var sut = CreateSut();

        // Act
        var result = sut.ListLayouts();

        // Assert
        result.Should().HaveCount(1);
        var dto = result[0];
        dto.Name.Should().Be("Development");
        dto.Description.Should().Be("Dev layout");
        dto.WindowCount.Should().Be(1); // Default preset has 1 placement
    }

    [Fact]
    public void ListLayouts_IncludesTimestamps()
    {
        // Arrange
        var preset = TestDataFactory.CreateLayoutPreset();
        _mockLayoutService.Setup(l => l.GetAllPresets())
            .Returns(new List<LayoutPreset> { preset });
        var sut = CreateSut();

        // Act
        var result = sut.ListLayouts();

        // Assert
        result[0].CreatedAt.Should().NotBeNullOrEmpty();
        result[0].UpdatedAt.Should().NotBeNullOrEmpty();
    }

    #endregion

    #region GetLayout Tests

    [Fact]
    public void GetLayout_WhenNotFound_ThrowsPresetNotFoundToolErrorNamingThePreset()
    {
        // Arrange
        _mockLayoutService.Setup(l => l.GetPreset(It.IsAny<string>()))
            .Returns((LayoutPreset?)null);
        var sut = CreateSut();

        // Act
        var act = () => sut.GetLayout("nonexistent");

        // Assert - ToolErrorReporting turns this into an isError result with errorCode PresetNotFound
        act.Should().Throw<ToolErrorException>()
            .WithMessage("*nonexistent*")
            .Which.ErrorCode.Should().Be(nameof(LayoutErrorCode.PresetNotFound));
    }

    [Fact]
    public void GetLayout_CallsServiceWithName()
    {
        // Arrange
        _mockLayoutService.Setup(l => l.GetPreset("my-layout"))
            .Returns(TestDataFactory.CreateLayoutPreset("my-layout"));
        var sut = CreateSut();

        // Act
        sut.GetLayout("my-layout");

        // Assert
        _mockLayoutService.Verify(l => l.GetPreset("my-layout"), Times.Once);
    }

    [Fact]
    public async Task GetLayout_MapsPresetToDetailDto()
    {
        // Arrange
        var placement = TestDataFactory.CreateWindowPlacement(
            processName: "code",
            titleContains: "Visual Studio",
            monitorIndex: 1,
            x: 0.0, y: 0.0,
            width: 0.5, height: 1.0,
            launchCommand: "code.exe",
            focus: true,
            order: 1);
        var preset = TestDataFactory.CreateLayoutPreset(
            name: "Development",
            description: "Dev setup",
            placements: new List<WindowPlacement> { placement });
        _mockLayoutService.Setup(l => l.GetPreset("Development"))
            .Returns(preset);
        var sut = CreateSut();

        // Act
        var result = sut.GetLayout("Development");

        // Assert
        result.Should().NotBeNull();
        result!.Name.Should().Be("Development");
        result.Description.Should().Be("Dev setup");
        result.Placements.Should().HaveCount(1);

        var placementDto = result.Placements[0];
        placementDto.ProcessName.Should().Be("code");
        placementDto.TitleContains.Should().Be("Visual Studio");
        placementDto.MonitorIndex.Should().Be(1);
        placementDto.Position!.X.Should().Be(0.0);
        placementDto.Position!.Y.Should().Be(0.0);
        placementDto.Position!.Width.Should().Be(0.5);
        placementDto.Position!.Height.Should().Be(1.0);
        placementDto.LaunchCommand.Should().Be("code.exe");
        placementDto.Focus.Should().BeTrue();
        placementDto.Order.Should().Be(1);
        placementDto.ExactProcessName.Should().BeNull("a hand-written placement matches part of the process name");
        placementDto.State.Should().BeNull();
    }

    [Fact]
    public void GetLayout_MapsTheIdentitySaveLayoutRecorded()
    {
        // Arrange
        var placement = new WindowPlacement(
            new WindowMatcher(ProcessName: "chrome", ClassName: "Chrome_WidgetWin_1")
            {
                ExactProcessName = true,
                TitlePattern = "Inbox - Mail - Google Chrome",
                ExePath = @"C:\Program Files\Google\Chrome\Application\chrome.exe",
                Ordinal = 1
            },
            0, new RelativePosition(0, 0, 1, 1)) { State = WindowState.Maximized };
        _mockLayoutService.Setup(l => l.GetPreset("captured"))
            .Returns(TestDataFactory.CreateLayoutPreset("captured", placements: [placement]));

        // Act
        var dto = CreateSut().GetLayout("captured").Placements.Single();

        // Assert
        dto.ExactProcessName.Should().BeTrue();
        dto.ClassName.Should().Be("Chrome_WidgetWin_1");
        dto.TitlePattern.Should().Be("Inbox - Mail - Google Chrome");
        dto.ExePath.Should().Be(@"C:\Program Files\Google\Chrome\Application\chrome.exe");
        dto.Ordinal.Should().Be(1);
        dto.State.Should().Be("maximized");
    }

    #endregion

    #region SaveLayout Tests

    [Fact]
    public async Task SaveLayout_CallsServiceWithParameters()
    {
        // Arrange
        var capturedPreset = TestDataFactory.CreateLayoutPreset("test-layout");
        _mockLayoutService.Setup(l => l.CaptureCurrentLayoutAsync(
            It.IsAny<string>(),
            It.IsAny<string?>(),
            It.IsAny<string[]?>(),
            It.IsAny<string[]?>(),
            It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new LayoutSaveResult(true, capturedPreset));
        var sut = CreateSut();

        // Act
        await sut.SaveLayout(
            name: "test-layout",
            description: "Test description",
            includeProcesses: "chrome,code",
            excludeProcesses: "explorer");

        // Assert
        _mockLayoutService.Verify(l => l.CaptureCurrentLayoutAsync(
            "test-layout",
            "Test description",
            It.Is<string[]>(arr => arr.Contains("chrome") && arr.Contains("code")),
            It.Is<string[]>(arr => arr.Contains("explorer")),
            It.IsAny<bool>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task SaveLayout_ParsesIncludeProcesses()
    {
        // Arrange
        var capturedPreset = TestDataFactory.CreateLayoutPreset();
        _mockLayoutService.Setup(l => l.CaptureCurrentLayoutAsync(
            It.IsAny<string>(),
            It.IsAny<string?>(),
            It.IsAny<string[]?>(),
            It.IsAny<string[]?>(),
            It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new LayoutSaveResult(true, capturedPreset));
        var sut = CreateSut();

        // Act
        await sut.SaveLayout("test", includeProcesses: " chrome , code , notepad ");

        // Assert
        _mockLayoutService.Verify(l => l.CaptureCurrentLayoutAsync(
            It.IsAny<string>(),
            It.IsAny<string?>(),
            It.Is<string[]>(arr => arr.Length == 3 && arr[0] == "chrome" && arr[1] == "code" && arr[2] == "notepad"),
            It.IsAny<string[]?>(),
            It.IsAny<bool>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task SaveLayout_ReturnsSuccessResult()
    {
        // Arrange
        var placements = new List<WindowPlacement>
        {
            TestDataFactory.CreateWindowPlacement(),
            TestDataFactory.CreateWindowPlacement()
        };
        var capturedPreset = TestDataFactory.CreateLayoutPreset(
            name: "captured-layout",
            placements: placements);
        _mockLayoutService.Setup(l => l.CaptureCurrentLayoutAsync(
            It.IsAny<string>(),
            It.IsAny<string?>(),
            It.IsAny<string[]?>(),
            It.IsAny<string[]?>(),
            It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new LayoutSaveResult(true, capturedPreset));
        var sut = CreateSut();

        // Act
        var result = await sut.SaveLayout("captured-layout");

        // Assert
        result.Success.Should().BeTrue();
        result.LayoutName.Should().Be("captured-layout");
        result.WindowCount.Should().Be(2);
    }

    [Fact]
    public async Task SaveLayout_WithNullFilters_PassesNull()
    {
        // Arrange
        var capturedPreset = TestDataFactory.CreateLayoutPreset();
        _mockLayoutService.Setup(l => l.CaptureCurrentLayoutAsync(
            It.IsAny<string>(),
            It.IsAny<string?>(),
            It.IsAny<string[]?>(),
            It.IsAny<string[]?>(),
            It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new LayoutSaveResult(true, capturedPreset));
        var sut = CreateSut();

        // Act
        await sut.SaveLayout("test");

        // Assert
        _mockLayoutService.Verify(l => l.CaptureCurrentLayoutAsync(
            "test",
            null,
            null,
            null,
            It.IsAny<bool>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    #endregion

    #region ApplyLayout Tests

    [Fact]
    public async Task ApplyLayout_CallsServiceWithParameters()
    {
        // Arrange
        var applyResult = new LayoutApplyResult(
            Success: true,
            PresetName: "test",
            WindowsArranged: 5,
            WindowsNotFound: 0,
            WindowsLaunched: 0,
            Errors: new List<string>());
        _mockLayoutService.Setup(l => l.ApplyPresetAsync(
            It.IsAny<string>(),
            It.IsAny<LayoutMatchBy>(),
            It.IsAny<bool>(),
            It.IsAny<LayoutApplyOptions?>(), It.IsAny<IProgress<OperationProgress>?>(),
            It.IsAny<CancellationToken>()))
            .ReturnsAsync(applyResult);
        var sut = CreateSut();

        // Act
        await sut.ApplyLayout(
            name: "test-layout",
            matchBy: LayoutMatchBy.ProcessOnly,
            launchMissing: true);

        // Assert
        _mockLayoutService.Verify(l => l.ApplyPresetAsync(
            "test-layout",
            LayoutMatchBy.ProcessOnly,
            true,
            It.IsAny<LayoutApplyOptions?>(), It.IsAny<IProgress<OperationProgress>?>(),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ApplyLayout_WithDefaults_UsesDefaultValues()
    {
        // Arrange
        var applyResult = new LayoutApplyResult(
            Success: true,
            PresetName: "test",
            WindowsArranged: 0,
            WindowsNotFound: 0,
            WindowsLaunched: 0,
            Errors: null);
        _mockLayoutService.Setup(l => l.ApplyPresetAsync(
            It.IsAny<string>(),
            It.IsAny<LayoutMatchBy>(),
            It.IsAny<bool>(),
            It.IsAny<LayoutApplyOptions?>(), It.IsAny<IProgress<OperationProgress>?>(),
            It.IsAny<CancellationToken>()))
            .ReturnsAsync(applyResult);
        var sut = CreateSut();

        // Act
        await sut.ApplyLayout("test");

        // Assert
        _mockLayoutService.Verify(l => l.ApplyPresetAsync(
            "test",
            LayoutMatchBy.ProcessAndTitle,
            false,
            It.IsAny<LayoutApplyOptions?>(), It.IsAny<IProgress<OperationProgress>?>(),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ApplyLayout_MapsTheStatusAndEachPlacement()
    {
        // Arrange
        var placements = new List<LayoutPlacementResult>
        {
            new(0, LayoutPlacementStatus.Applied, new LayoutWindowRef(0x101, "Inbox - Mail - Google Chrome", "chrome")),
            new(1, LayoutPlacementStatus.NotFound, ErrorCode: nameof(LayoutErrorCode.WindowNotFound), Error: "No open window matched placement 1")
        };
        _mockLayoutService.Setup(l => l.ApplyPresetAsync(
                It.IsAny<string>(), It.IsAny<LayoutMatchBy>(), It.IsAny<bool>(), It.IsAny<LayoutApplyOptions?>(), It.IsAny<IProgress<OperationProgress>?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new LayoutApplyResult(false, "two", WindowsArranged: 1, WindowsNotFound: 1,
                ErrorCode: nameof(LayoutErrorCode.WindowNotFound), Placements: placements));

        // Act
        var result = await CreateSut().ApplyLayout("two");

        // Assert
        result.Status.Should().Be(LayoutApplyStatus.Partial);
        result.Placements.Should().Equal(placements);
    }

    [Theory]
    [InlineData(true, 2, "applied")]
    [InlineData(false, 1, "partial")]
    [InlineData(false, 0, "failed")]
    public void LayoutApplyResult_Status_FollowsSuccessAndTheArrangedCount(bool success, int arranged, string status)
    {
        new LayoutApplyResult(success, "any", WindowsArranged: arranged).Status.Should().Be(status);
    }

    [Fact]
    public async Task ApplyLayout_MapsResultToDto()
    {
        // Arrange
        var applyResult = new LayoutApplyResult(
            Success: true,
            PresetName: "dev-setup",
            WindowsArranged: 4,
            WindowsNotFound: 1,
            WindowsLaunched: 2,
            Errors: new List<string> { "Warning: something" });
        _mockLayoutService.Setup(l => l.ApplyPresetAsync(
            It.IsAny<string>(),
            It.IsAny<LayoutMatchBy>(),
            It.IsAny<bool>(),
            It.IsAny<LayoutApplyOptions?>(), It.IsAny<IProgress<OperationProgress>?>(),
            It.IsAny<CancellationToken>()))
            .ReturnsAsync(applyResult);
        var sut = CreateSut();

        // Act
        var result = await sut.ApplyLayout("dev-setup");

        // Assert
        result.Success.Should().BeTrue();
        result.PresetName.Should().Be("dev-setup");
        result.WindowsArranged.Should().Be(4);
        result.WindowsNotFound.Should().Be(1);
        result.WindowsLaunched.Should().Be(2);
        result.Errors.Should().HaveCount(1);
        result.Errors![0].Should().Be("Warning: something");
        result.Focus.Should().BeNull("no placement asked for focus");
    }

    [Fact]
    public async Task ApplyLayout_MapsTheFocusResultOfEachFocusedPlacement()
    {
        // Arrange - two placements with focus: one came to the front, Windows kept the other one back
        var inFront = new FocusResult(true, 0x100, true, Method: FocusMethod.SetForeground, ForegroundHandle: 0x100,
            ForegroundTitle: "main.cs - Visual Studio Code", ForegroundProcess: "Code");
        var refused = new FocusResult(false, 0x200, false, ForegroundHandle: 0x300, ForegroundTitle: "pwsh",
            ForegroundProcess: "WindowsTerminal", Error: "SetForegroundWindow was refused (foreground lock)",
            ErrorCode: nameof(WindowErrorCode.FocusFailed));
        _mockLayoutService.Setup(l => l.ApplyPresetAsync(
                It.IsAny<string>(), It.IsAny<LayoutMatchBy>(), It.IsAny<bool>(),
                It.IsAny<LayoutApplyOptions?>(), It.IsAny<IProgress<OperationProgress>?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new LayoutApplyResult(
                Success: false,
                PresetName: "work-setup",
                WindowsArranged: 2,
                Errors: ["Arranged window 'Terminal' for placement 2, but could not bring it to the foreground (FocusFailed): ..."],
                ErrorCode: nameof(LayoutErrorCode.ArrangeFailed),
                Focus: [inFront, refused]));
        var sut = CreateSut();

        // Act
        var result = await sut.ApplyLayout("work-setup");

        // Assert - in placement order, with the same fields as focus_window
        result.Focus.Should().Equal(inFront, refused);
    }

    #endregion

    #region DeleteLayout Tests

    [Fact]
    public async Task DeleteLayout_CallsService()
    {
        // Arrange
        _mockLayoutService.Setup(l => l.DeletePresetAsync(
            It.IsAny<string>(),
            It.IsAny<CancellationToken>()))
            .ReturnsAsync(new LayoutDeleteResult(true, "test"));
        var sut = CreateSut();

        // Act
        await sut.DeleteLayout("to-delete");

        // Assert
        _mockLayoutService.Verify(l => l.DeletePresetAsync(
            "to-delete",
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task DeleteLayout_ReturnsSuccess_WhenDeleted()
    {
        // Arrange
        _mockLayoutService.Setup(l => l.DeletePresetAsync(
            It.IsAny<string>(),
            It.IsAny<CancellationToken>()))
            .ReturnsAsync(new LayoutDeleteResult(true, "test"));
        var sut = CreateSut();

        // Act
        var result = await sut.DeleteLayout("test");

        // Assert
        result.Success.Should().BeTrue();
        result.Error.Should().BeNull();
    }

    [Fact]
    public async Task DeleteLayout_ReturnsFailure_WhenNotDeleted()
    {
        // Arrange
        _mockLayoutService.Setup(l => l.DeletePresetAsync(
            It.IsAny<string>(),
            It.IsAny<CancellationToken>()))
            .ReturnsAsync(new LayoutDeleteResult(false, "test", "Failed to delete", nameof(LayoutErrorCode.DeleteFailed)));
        var sut = CreateSut();

        // Act
        var result = await sut.DeleteLayout("test");

        // Assert
        result.Success.Should().BeFalse();
        result.Error.Should().NotBeNullOrEmpty();
    }

    #endregion

    #region Honest results

    [Fact]
    public void ListLayouts_WhenStorageUnavailable_ThrowsStorageUnavailableToolErrorWithReason()
    {
        _mockLayoutService.Setup(l => l.GetStorageError()).Returns("Folder X could not be created");
        var sut = CreateSut();

        var act = () => sut.ListLayouts();

        act.Should().Throw<ToolErrorException>().WithMessage("*Folder X could not be created*")
            .Which.ErrorCode.Should().Be(nameof(LayoutErrorCode.StorageUnavailable));
        _mockLayoutService.Verify(l => l.GetAllPresets(), Times.Never);
    }

    [Fact]
    public async Task GetLayout_WhenStorageUnavailable_ThrowsStorageUnavailableToolErrorWithReason()
    {
        _mockLayoutService.Setup(l => l.GetStorageError()).Returns("Folder X could not be created");
        var sut = CreateSut();

        var act = () => sut.GetLayout("anything");

        act.Should().Throw<ToolErrorException>().WithMessage("*Folder X could not be created*")
            .Which.ErrorCode.Should().Be(nameof(LayoutErrorCode.StorageUnavailable));
    }

    [Fact]
    public async Task SaveLayout_WhenServiceReportsFailure_ReturnsFailure()
    {
        var capturedPreset = TestDataFactory.CreateLayoutPreset("failing");
        _mockLayoutService.Setup(l => l.CaptureCurrentLayoutAsync(
                It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string[]?>(), It.IsAny<string[]?>(),
                It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new LayoutSaveResult(
                Success: false,
                Preset: capturedPreset,
                ErrorMessage: "Disk full",
                ErrorCode: nameof(LayoutErrorCode.SaveFailed)));
        var sut = CreateSut();

        var result = await sut.SaveLayout("failing");

        result.Success.Should().BeFalse();
        result.Error.Should().Be("Disk full");
        result.ErrorCode.Should().Be(nameof(LayoutErrorCode.SaveFailed));
        result.LayoutName.Should().Be("failing");
    }

    [Fact]
    public async Task SaveLayout_WhenNothingCaptured_ReturnsRequestedNameAndZeroWindows()
    {
        _mockLayoutService.Setup(l => l.CaptureCurrentLayoutAsync(
                It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string[]?>(), It.IsAny<string[]?>(),
                It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new LayoutSaveResult(
                Success: false,
                Preset: null,
                ErrorMessage: "No windows",
                ErrorCode: nameof(LayoutErrorCode.InvalidName)));
        var sut = CreateSut();

        var result = await sut.SaveLayout("requested");

        result.Success.Should().BeFalse();
        result.LayoutName.Should().Be("requested");
        result.WindowCount.Should().Be(0);
    }

    [Fact]
    public async Task SaveLayout_ReportsReplaced()
    {
        _mockLayoutService.Setup(l => l.CaptureCurrentLayoutAsync(
                It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string[]?>(), It.IsAny<string[]?>(),
                It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new LayoutSaveResult(true, TestDataFactory.CreateLayoutPreset("again"), Replaced: true));
        var sut = CreateSut();

        var result = await sut.SaveLayout("again");

        result.Success.Should().BeTrue();
        result.Replaced.Should().BeTrue();
    }

    [Fact]
    public async Task ApplyLayout_MapsErrorCode()
    {
        _mockLayoutService.Setup(l => l.ApplyPresetAsync(
                It.IsAny<string>(), It.IsAny<LayoutMatchBy>(), It.IsAny<bool>(),
                It.IsAny<LayoutApplyOptions?>(), It.IsAny<IProgress<OperationProgress>?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new LayoutApplyResult(
                Success: false,
                PresetName: "work-setup",
                WindowsNotFound: 3,
                Errors: ["No open window matched placement 1"],
                ErrorCode: nameof(LayoutErrorCode.WindowNotFound)));
        var sut = CreateSut();

        var result = await sut.ApplyLayout("work-setup");

        result.Success.Should().BeFalse();
        result.ErrorCode.Should().Be(nameof(LayoutErrorCode.WindowNotFound));
    }

    [Fact]
    public async Task DeleteLayout_WhenPresetNotFound_ReturnsPresetNotFound()
    {
        _mockLayoutService.Setup(l => l.DeletePresetAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new LayoutDeleteResult(
                false, "typo", "Layout preset 'typo' was not found", nameof(LayoutErrorCode.PresetNotFound)));
        var sut = CreateSut();

        var result = await sut.DeleteLayout("typo");

        result.Success.Should().BeFalse();
        result.Error.Should().Contain("typo");
        result.ErrorCode.Should().Be(nameof(LayoutErrorCode.PresetNotFound));
    }

    #endregion

    [Fact]
    public async Task ApplyLayout_PassesTheCallsTokenAndForwardsProgressToTheClient()
    {
        // Arrange - the service reports one placement
        using var call = new CancellationTokenSource();
        CancellationToken serviceToken = default;
        _mockLayoutService.Setup(l => l.ApplyPresetAsync(
                It.IsAny<string>(), It.IsAny<LayoutMatchBy>(), It.IsAny<bool>(), It.IsAny<LayoutApplyOptions?>(), It.IsAny<IProgress<OperationProgress>?>(),
                It.IsAny<CancellationToken>()))
            .Callback((string _, LayoutMatchBy _, bool _, LayoutApplyOptions? _, IProgress<OperationProgress>? progress, CancellationToken token) =>
            {
                serviceToken = token;
                progress!.Report(new OperationProgress(1, 2, "1 of 2: placement 1 (process 'code') was arranged"));
            })
            .ReturnsAsync(new LayoutApplyResult(true, "work", WindowsArranged: 2));
        var client = new RecordingProgress<ProgressNotificationValue>();

        // Act
        await CreateSut().ApplyLayout("work", progress: client, cancellationToken: call.Token);

        // Assert
        serviceToken.Should().Be(call.Token);
        var report = client.Reports.Should().ContainSingle().Subject;
        (report.Progress, report.Total).Should().Be((1f, 2f));
        report.Message.Should().Be("1 of 2: placement 1 (process 'code') was arranged");
    }

    #region Schema version 2 (roadmap M6.8)

    /// <summary>get_layout's result for <paramref name="preset"/>.</summary>
    private LayoutDetailDto Detail(LayoutPreset preset)
    {
        _mockLayoutService.Setup(l => l.GetStorageError()).Returns((string?)null);
        _mockLayoutService.Setup(l => l.GetPreset(preset.Name)).Returns(preset);
        return CreateSut().GetLayout(preset.Name);
    }

    [Fact]
    public async Task SaveLayout_WithPlacements_SavesThemAsAnAgentLayout_AndCapturesNothing()
    {
        var specs = new[] { new LayoutPlacementSpec("Code", Snap: "left_half") };
        _mockLayoutService.Setup(l => l.SaveAuthoredLayoutAsync("desk", "mine", specs, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new LayoutSaveResult(true, TestDataFactory.CreateLayoutPreset("desk")));

        var result = await CreateSut().SaveLayout("desk", "mine", placements: specs);

        result.Success.Should().BeTrue();
        _mockLayoutService.Verify(l => l.CaptureCurrentLayoutAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string[]?>(),
            It.IsAny<string[]?>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Theory]
    [InlineData("chrome", null, false)]
    [InlineData(null, "explorer", false)]
    [InlineData(null, null, true)]
    public async Task SaveLayout_WithPlacementsAndACaptureFilter_IsInvalidArguments(string? include, string? exclude, bool includeMinimized)
    {
        var call = () => CreateSut().SaveLayout("desk", includeProcesses: include, excludeProcesses: exclude, includeMinimized: includeMinimized,
            placements: [new LayoutPlacementSpec("Code", Snap: "left_half")]);

        (await call.Should().ThrowAsync<ToolErrorException>()).Which.ErrorCode.Should().Be("InvalidArguments");
        _mockLayoutService.Verify(l => l.SaveAuthoredLayoutAsync(It.IsAny<string>(), It.IsAny<string?>(),
            It.IsAny<IReadOnlyList<LayoutPlacementSpec>>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task SaveLayout_PassesIncludeMinimized()
    {
        _mockLayoutService.Setup(l => l.CaptureCurrentLayoutAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string[]?>(),
                It.IsAny<string[]?>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new LayoutSaveResult(true, TestDataFactory.CreateLayoutPreset()));

        await CreateSut().SaveLayout("desk", includeMinimized: true);

        _mockLayoutService.Verify(l => l.CaptureCurrentLayoutAsync("desk", null, null, null, true, It.IsAny<CancellationToken>()), Times.Once);
    }

    private void ApplyReturns(Func<LayoutApplyOptions?, LayoutApplyResult> result) =>
        _mockLayoutService.Setup(l => l.ApplyPresetAsync(It.IsAny<string>(), It.IsAny<LayoutMatchBy>(), It.IsAny<bool>(),
                It.IsAny<LayoutApplyOptions?>(), It.IsAny<IProgress<OperationProgress>?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string _, LayoutMatchBy _, bool _, LayoutApplyOptions? options, IProgress<OperationProgress>? _, CancellationToken _) =>
                result(options));

    [Fact]
    public async Task ApplyLayout_DryRun_PassesTheOptions_AndRunsWhileInputIsPaused()
    {
        // A dry run only reads: it takes no input gate, and the pause does not stop it
        var session = new Mock<IInputSession>();
        session.Setup(s => s.IsPaused).Returns(true);
        LayoutApplyOptions? passed = null;
        ApplyReturns(options =>
        {
            passed = options;
            return new LayoutApplyResult(true, "desk", DryRun: true);
        });

        var result = await new LayoutPresetTools(_mockLayoutService.Object, session.Object).ApplyLayout("desk", dryRun: true, placements: [1, 2]);

        result.Status.Should().Be("dryRun");
        passed!.DryRun.Should().BeTrue();
        passed.Placements.Should().Equal(1, 2);
    }

    [Fact]
    public async Task ApplyLayout_WhileInputIsPaused_IsPausedByUser_AndAppliesNothing()
    {
        var session = new Mock<IInputSession>();
        session.Setup(s => s.IsPaused).Returns(true);

        var call = () => new LayoutPresetTools(_mockLayoutService.Object, session.Object).ApplyLayout("desk");

        (await call.Should().ThrowAsync<ToolErrorException>()).Which.ErrorCode.Should().Be(nameof(WindowErrorCode.PausedByUser));
        _mockLayoutService.Verify(l => l.ApplyPresetAsync(It.IsAny<string>(), It.IsAny<LayoutMatchBy>(), It.IsAny<bool>(),
            It.IsAny<LayoutApplyOptions?>(), It.IsAny<IProgress<OperationProgress>?>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ApplyLayout_OfAPresetNoAgentWrote_AsksNothing_AndPlansNoDryRun()
    {
        _mockLayoutService.Setup(l => l.GetPreset("desk")).Returns(TestDataFactory.CreateLayoutPreset("desk"));
        var calls = new List<LayoutApplyOptions?>();
        ApplyReturns(options =>
        {
            calls.Add(options);
            return new LayoutApplyResult(true, "desk");
        });

        await CreateSut().ApplyLayout("desk", launchMissing: true);

        calls.Should().ContainSingle().Which!.DryRun.Should().BeFalse();
        _mockLayoutService.Verify(l => l.AreLaunchesConfirmed(It.IsAny<LayoutPreset>()), Times.Never);
    }

    [Fact]
    public void GetLayout_ShowsTheVersion2Fields_AndAWarningForANewerPreset()
    {
        var preset = new LayoutPreset("v3", null,
        [
            new WindowPlacement(new WindowMatcher(ProcessName: "Code"), 1, new RelativePosition(0, 0, 1, 1))
            {
                State = WindowState.Maximized,
                Monitor = "m-22222222",
                MonitorName = "DELL U2720Q",
                NormalPosition = new RelativePosition(0.1, 0.1, 0.5, 0.5),
                Launch = new LayoutLaunch(Executable: "code")
            },
            new WindowPlacement(new WindowMatcher(ProcessName: "notepad"), 0, new RelativePosition(0, 0, 0.5, 1), Order: 1)
        ])
        { Version = 3, AuthoredBy = "agent" };

        var dto = Detail(preset);

        dto.AuthoredBy.Should().Be("agent");
        dto.Warning.Should().Contain("schema version 3");
        dto.Placements[0].Monitor.Should().Be(MonitorSelector.Of("m-22222222"));
        dto.Placements[0].MonitorName.Should().Be("DELL U2720Q");
        dto.Placements[0].NormalPosition.Should().Be(new PositionDto(0.1, 0.1, 0.5, 0.5));
        dto.Placements[0].Launch.Should().Be(new LayoutLaunch(Executable: "code"));
        dto.Placements[1].Monitor.Should().Be(MonitorSelector.OfIndex(0), "a version 1 placement's monitor is its index");
        Detail(preset with { Version = 2 }).Warning.Should().BeNull();
    }

    [Fact]
    public void GetLayoutsPlacements_GoBackIntoSaveLayout_Unchanged()
    {
        // Owner decision D15: get_layout's output round-trips into save_layout's placements (it replaces update_layout)
        IReadOnlyList<MonitorInfo> monitors =
        [
            TestDataFactory.CreateMonitorInfo() with { Id = "m-11111111" },
            TestDataFactory.CreateMonitorInfo(1, @"\\.\DISPLAY2", false, 1920, 0, 2560, 1440, 1920, 0, 2560, 1400, 1.25) with { Id = "m-22222222" }
        ];
        var original = new List<WindowPlacement>
        {
            new(new WindowMatcher(ProcessName: "Code", ClassName: "Chrome_WidgetWin_1")
            {
                ExactProcessName = true, TitlePattern = "notes - Visual Studio Code", ExePath = @"C:\Code\Code.exe", Ordinal = 1
            }, 1, new RelativePosition(0, 0, 1, 1), Focus: true)
            {
                State = WindowState.Maximized,
                Monitor = "m-22222222",
                MonitorName = "DELL U2720Q",
                NormalPosition = new RelativePosition(0.1, 0.1, 0.5, 0.5),
                Launch = new LayoutLaunch(Executable: @"C:\Code\Code.exe")
            },
            new(new WindowMatcher(TitleContains: "Inbox"), 0, new RelativePosition(0.5, 0, 0.5, 1), Order: 1)
            {
                Snap = "right_half",
                Monitor = "primary",
                Launch = new LayoutLaunch(AppId: "Microsoft.OutlookForWindows_8wekyb3d8bbwe!Microsoft.OutlookforWindows")
            },
            new(new WindowMatcher(ProcessName: "notepad"), 0, new RelativePosition(0.25, 0.25, 0.4, 0.4), Order: 2)
            {
                State = WindowState.Minimized,
                Monitor = "m-11111111",
                NormalPosition = new RelativePosition(0.25, 0.25, 0.4, 0.4)
            }
        };
        var dto = Detail(new LayoutPreset("desk", null, original));

        // Act - the client reads get_layout's JSON and passes its placements to save_layout as they are
        var json = System.Text.Json.JsonSerializer.Serialize(dto.Placements, ToolJson.SerializerOptions);
        var specs = System.Text.Json.JsonSerializer.Deserialize<LayoutPlacementSpec[]>(json, ToolJson.SerializerOptions)!;
        var rebuilt = specs.Select((spec, i) =>
        {
            LayoutPresetSchema.TryBuildPlacement(spec, i, monitors, out var placement, out var error).Should().BeTrue(error);
            return placement!;
        }).ToList();

        // Assert - the same preset, the version 1 launchCommand filled as save_layout writes it
        rebuilt.Should().BeEquivalentTo(original.Select(p => p with { LaunchCommand = LayoutPresetSchema.LaunchTarget(p) }));
    }

    #endregion

    [Theory]
    [InlineData("""{ "processName": "Code", "snap": "left_half", "launch": { "executable": "code.exe", "arguments": ["--new-window"] } }""", "launch.arguments")]
    [InlineData("""{ "processName": "Code", "snap": "left_half", "launchCommand": "code.exe --new-window" }""", "launchCommand")]
    [InlineData("""{ "processName": "Code", "snap": "left_half", "monitorIndex": 1 }""", "monitorIndex")]
    [InlineData("""{ "processName": "Code", "snap": "left_half", "order": 4 }""", "order")]
    [InlineData("""{ "processName": "Code", "snap": "left_half", "workspace": 2 }""", "workspace")]
    [InlineData("""{ "processName": "Code", "position": { "x": 0.75, "y": 0, "width": 0.5, "height": 1 } }""", "at most 1")]
    public void AnUnknownOrIgnoredField_AsAClientSendsIt_FailsThePlacement(string json, string expected)
    {
        // Review M6.8 #4: bound as the server binds save_layout's arguments, then checked
        var spec = System.Text.Json.JsonSerializer.Deserialize<LayoutPlacementSpec>(json, ToolJson.SerializerOptions)!;

        LayoutPresetSchema.TryBuildPlacement(spec, 0, [TestDataFactory.CreateMonitorInfo()], out _, out var error).Should().BeFalse();

        error.Should().StartWith("placements[0]").And.Contain(expected);
    }
}
