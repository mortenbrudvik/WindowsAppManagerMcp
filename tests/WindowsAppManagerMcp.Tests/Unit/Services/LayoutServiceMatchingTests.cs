namespace WindowsAppManagerMcp.Tests.Unit.Services;

/// <summary>
/// Roadmap M2.8: save_layout records which window each placement is for, and apply_layout gives each placement its
/// own window (one to one, from one snapshot), skips placements it cannot match honestly, and reports each placement.
/// The window, monitor, process and foreground services are mocks; only the preset files are real (in a temp folder).
/// </summary>
public class LayoutServiceMatchingTests : IDisposable
{
    private readonly string _layoutsPath = Path.Combine(Path.GetTempPath(), $"LayoutServiceMatchingTests_{Guid.NewGuid():N}");
    private readonly Mock<IWindowService> _windows = TestDataFactory.CreateMockWindowService([]);
    private readonly Mock<IMonitorService> _monitors = TestDataFactory.CreateMockMonitorService();
    private readonly Mock<IProcessService> _processes = TestDataFactory.CreateMockProcessService();
    private readonly Mock<IForegroundService> _foreground = TestDataFactory.CreateMockForegroundService();

    // The one monitor's work area is 1920x1040 at 0,0
    private static readonly WindowInfo ChromeLeft = Window(0x101, "Inbox - Mail - Google Chrome", "chrome", 501, 0, 0, 960, 1040, "Chrome_WidgetWin_1");
    private static readonly WindowInfo ChromeRight = Window(0x102, "Pull requests - GitHub - Google Chrome", "chrome", 501, 960, 0, 960, 1040, "Chrome_WidgetWin_1");

    public void Dispose()
    {
        if (Directory.Exists(_layoutsPath))
            Directory.Delete(_layoutsPath, recursive: true);
    }

    private LayoutService CreateSut() =>
        new(_windows.Object, _monitors.Object, _processes.Object, _foreground.Object, _layoutsPath);

    private static WindowInfo Window(
        int handle, string title, string process, int pid, int x, int y, int width, int height,
        string className = "AppWindow", WindowState state = WindowState.Normal) =>
        TestDataFactory.CreateWindowInfo(handle, title, process, pid, x, y, width, height, state) with { ClassName = className };

    private void OpenWindows(params WindowInfo[] windows)
    {
        _windows.Setup(w => w.GetAllWindows(It.IsAny<bool>())).Returns(windows);
        foreach (var window in windows)
            _windows.Setup(w => w.GetWindowInfo(window.Handle)).Returns(window);
    }

    private void VerifyPlaced(WindowInfo window, int x, int y, int width, int height) =>
        _windows.Verify(w => w.SetWindowBounds(window.Handle, x, y, width, height, It.IsAny<bool>()), Times.Once);

    private void VerifyNothingPlaced() =>
        _windows.Verify(w => w.SetWindowBounds(It.IsAny<nint>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<bool>()),
            Times.Never);

    private static WindowPlacement Placement(WindowMatcher matcher, int order, double x = 0, double y = 0, double width = 0.5, double height = 1.0,
        int monitorIndex = 0, string? launchCommand = null, bool focus = false) =>
        new(matcher, monitorIndex, new RelativePosition(x, y, width, height), launchCommand, focus, order);

    private async Task SaveAsync(LayoutService sut, string name, params WindowPlacement[] placements) =>
        (await sut.SavePresetAsync(new LayoutPreset(name, null, [.. placements]))).Success.Should().BeTrue();

    #region Capture records identity

    [Fact]
    public async Task Capture_RecordsTheExactProcessNameClassTitleExePathOrdinalAndState()
    {
        // Arrange
        var explorer = Window(0x201, "Downloads - File Explorer", "explorer", 700, 0, 0, 960, 1040, "CabinetWClass", WindowState.Maximized);
        OpenWindows(ChromeLeft, explorer, ChromeRight);
        _processes.Setup(p => p.GetProcessPath(501)).Returns(@"C:\Program Files\Google\Chrome\Application\chrome.exe");
        _processes.Setup(p => p.GetProcessPath(700)).Returns(@"C:\Windows\explorer.exe");
        var sut = CreateSut();

        // Act
        var result = await sut.CaptureCurrentLayoutAsync("captured");

        // Assert
        result.Success.Should().BeTrue();
        var placements = sut.GetPreset("captured")!.Placements;
        placements.Select(p => p.Matcher.ProcessName).Should().Equal("chrome", "explorer", "chrome");
        placements.Should().OnlyContain(p => p.Matcher.ExactProcessName && p.Matcher.TitleContains == null);
        placements.Select(p => p.Matcher.ClassName).Should().Equal("Chrome_WidgetWin_1", "CabinetWClass", "Chrome_WidgetWin_1");
        placements.Select(p => p.Matcher.TitlePattern).Should().Equal(ChromeLeft.Title, explorer.Title, ChromeRight.Title);
        placements.Select(p => p.Matcher.ExePath).Should().Equal(
            @"C:\Program Files\Google\Chrome\Application\chrome.exe", @"C:\Windows\explorer.exe", @"C:\Program Files\Google\Chrome\Application\chrome.exe");
        placements.Select(p => p.Matcher.Ordinal).Should().Equal(0, 0, 1); // counted per process
        placements.Select(p => p.State).Should().Equal(WindowState.Normal, WindowState.Maximized, WindowState.Normal);
        placements.Should().OnlyContain(p => p.LaunchCommand == null, "save_layout never records a command to run");
    }

    [Fact]
    public async Task Capture_StoresTheStateAsText_AndPresetsWithoutTheNewFieldsStillLoad()
    {
        // Arrange - a hand-written preset from an earlier version, next to a captured one
        OpenWindows(Window(0x201, "Downloads - File Explorer", "explorer", 700, 0, 0, 960, 1040, "CabinetWClass", WindowState.Maximized));
        Directory.CreateDirectory(_layoutsPath);
        await File.WriteAllTextAsync(Path.Combine(_layoutsPath, "old.json"), """
            { "name": "old", "placements": [ { "matcher": { "processName": "Code" }, "monitorIndex": 0,
              "position": { "x": 0, "y": 0, "width": 1, "height": 1 }, "order": 1 } ] }
            """);
        var sut = CreateSut();

        // Act
        await sut.CaptureCurrentLayoutAsync("new");

        // Assert
        var json = await File.ReadAllTextAsync(Path.Combine(_layoutsPath, "new.json"));
        json.Should().Contain("\"state\": \"maximized\"").And.Contain("\"exactProcessName\": true");
        var old = sut.GetPreset("old")!.Placements.Single();
        old.State.Should().BeNull();
        old.Matcher.ExactProcessName.Should().BeFalse();
        old.Matcher.TitlePattern.Should().BeNull();
    }

    [Fact]
    public async Task Capture_DoesNotRecordTheTitleOfAWindowThePolicyDenies()
    {
        // Arrange
        var keePass = Window(0x301, "Passwords.kdbx - KeePass", "KeePass", 900, 0, 0, 800, 600);
        OpenWindows(keePass);
        _windows.Setup(w => w.EvaluateTargetPolicy(keePass.Handle)).Returns(TargetPolicyVerdict.Deny("KeePass", "KeePass is on the deny list"));
        var sut = CreateSut();

        // Act
        await sut.CaptureCurrentLayoutAsync("with-denied");

        // Assert
        var matcher = sut.GetPreset("with-denied")!.Placements.Single().Matcher;
        matcher.ProcessName.Should().Be("KeePass");
        matcher.TitlePattern.Should().BeNull("the title of a denied app's window is not written to disk");
    }

    #endregion

    #region One-to-one assignment from one snapshot

    [Fact]
    public async Task CaptureThenApply_TwoWindowsOfOneProcess_EachReturnsToItsOwnPlace()
    {
        // Arrange - capture the two Chrome windows, then the user swaps them and changes the z-order
        OpenWindows(ChromeLeft, ChromeRight);
        var sut = CreateSut();
        (await sut.CaptureCurrentLayoutAsync("browsers")).Success.Should().BeTrue();
        var leftNowRight = ChromeLeft with { Bounds = new WindowRect(960, 0, 960, 1040) };
        var rightNowLeft = ChromeRight with { Bounds = new WindowRect(0, 0, 960, 1040) };
        OpenWindows(rightNowLeft, leftNowRight);

        // Act
        var result = await sut.ApplyPresetAsync("browsers");

        // Assert - by title, not by z-order or position
        result.Success.Should().BeTrue();
        result.Status.Should().Be(LayoutApplyStatus.Applied);
        result.WindowsArranged.Should().Be(2);
        VerifyPlaced(ChromeLeft, 0, 0, 960, 1040);
        VerifyPlaced(ChromeRight, 960, 0, 960, 1040);
    }

    [Fact]
    public async Task Apply_ATitleThatChanged_StillPrefersTheWindowSharingMostOfItsParts()
    {
        // Arrange - captured "Inbox - Mail - Google Chrome" and "Pull requests - GitHub - Google Chrome"; both tabs changed
        var sut = CreateSut();
        await SaveAsync(sut, "renamed",
            Placement(new WindowMatcher(ProcessName: "chrome") { ExactProcessName = true, TitlePattern = ChromeLeft.Title }, 0),
            Placement(new WindowMatcher(ProcessName: "chrome") { ExactProcessName = true, TitlePattern = ChromeRight.Title }, 1, x: 0.5));
        var github = ChromeRight with { Title = "Issues - GitHub - Google Chrome", Bounds = new WindowRect(0, 0, 960, 1040) };
        var mail = ChromeLeft with { Title = "Sent - Mail - Google Chrome", Bounds = new WindowRect(960, 0, 960, 1040) };
        OpenWindows(github, mail);

        // Act
        var result = await sut.ApplyPresetAsync("renamed");

        // Assert
        result.Success.Should().BeTrue();
        VerifyPlaced(mail, 0, 0, 960, 1040);
        VerifyPlaced(github, 960, 0, 960, 1040);
    }

    [Fact]
    public async Task Apply_MorePlacementsThanWindows_MovesTheWindowOnce_AndReportsTheRestNotFound()
    {
        // Arrange - three placements of one process, one window open (before, it was moved three times)
        var sut = CreateSut();
        var chrome = new WindowMatcher(ProcessName: "chrome") { ExactProcessName = true };
        await SaveAsync(sut, "three", Placement(chrome, 0), Placement(chrome, 1, x: 0.5), Placement(chrome, 2, y: 0.5));
        OpenWindows(ChromeLeft);

        // Act
        var result = await sut.ApplyPresetAsync("three");

        // Assert
        result.Success.Should().BeFalse();
        result.Status.Should().Be(LayoutApplyStatus.Partial);
        result.WindowsArranged.Should().Be(1);
        result.WindowsNotFound.Should().Be(2);
        result.ErrorCode.Should().Be(nameof(LayoutErrorCode.WindowNotFound));
        _windows.Verify(w => w.SetWindowBounds(ChromeLeft.Handle, It.IsAny<int>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<bool>()),
            Times.Once);
        result.Placements!.Select(p => p.Status).Should().Equal(
            LayoutPlacementStatus.Applied, LayoutPlacementStatus.NotFound, LayoutPlacementStatus.NotFound);
    }

    [Fact]
    public async Task Apply_HandWrittenPlacementsOfOneProcess_GetDifferentWindows_TheNearestFirst()
    {
        // Arrange - a hand-written preset with no identity: each placement takes the unassigned window closest to its place
        var sut = CreateSut();
        await SaveAsync(sut, "halves",
            Placement(new WindowMatcher(ProcessName: "chrome"), 0, x: 0.5),
            Placement(new WindowMatcher(ProcessName: "chrome"), 1, x: 0));
        OpenWindows(ChromeLeft, ChromeRight);

        // Act
        var result = await sut.ApplyPresetAsync("halves");

        // Assert - nothing has to move
        result.Success.Should().BeTrue();
        VerifyPlaced(ChromeRight, 960, 0, 960, 1040);
        VerifyPlaced(ChromeLeft, 0, 0, 960, 1040);
    }

    [Fact]
    public async Task Apply_TakesOneWindowSnapshot_ForAllPlacements()
    {
        // Arrange
        var sut = CreateSut();
        await SaveAsync(sut, "snapshot",
            Placement(new WindowMatcher(ProcessName: "chrome"), 0),
            Placement(new WindowMatcher(ProcessName: "chrome"), 1, x: 0.5),
            Placement(new WindowMatcher(ProcessName: "missing"), 2));
        OpenWindows(ChromeLeft, ChromeRight);

        // Act
        await sut.ApplyPresetAsync("snapshot");

        // Assert
        _windows.Verify(w => w.GetAllWindows(true), Times.Once);
        _windows.Verify(w => w.GetAllWindows(false), Times.Never);
    }

    [Fact]
    public async Task Apply_CapturedProcessName_MustMatchExactly()
    {
        // Arrange - a captured "Code" placement; only VS Code Insiders is open
        var sut = CreateSut();
        await SaveAsync(sut, "exact", Placement(new WindowMatcher(ProcessName: "code") { ExactProcessName = true }, 0));
        OpenWindows(Window(0x401, "README.md - Visual Studio Code - Insiders", "Code - Insiders", 800, 0, 0, 800, 600));

        // Act
        var result = await sut.ApplyPresetAsync("exact");

        // Assert
        result.Success.Should().BeFalse();
        result.WindowsNotFound.Should().Be(1);
        VerifyNothingPlaced();
    }

    [Fact]
    public async Task Apply_HandWrittenProcessName_StillMatchesPartOfTheName()
    {
        // Arrange
        var sut = CreateSut();
        await SaveAsync(sut, "contains", Placement(new WindowMatcher(ProcessName: "code"), 0));
        var insiders = Window(0x401, "README.md - Visual Studio Code - Insiders", "Code - Insiders", 800, 0, 0, 800, 600);
        OpenWindows(insiders);

        // Act
        var result = await sut.ApplyPresetAsync("contains");

        // Assert
        result.Success.Should().BeTrue();
        VerifyPlaced(insiders, 0, 0, 960, 1040);
    }

    [Fact]
    public async Task Apply_TheClassNameRanksWindowsOfOneProcess()
    {
        // Arrange - Explorer's folder window and one of its dialogs, both untouched since capture
        var sut = CreateSut();
        await SaveAsync(sut, "folder",
            Placement(new WindowMatcher(ProcessName: "explorer", ClassName: "CabinetWClass") { ExactProcessName = true }, 0));
        var dialog = Window(0x501, "Confirm", "explorer", 700, 0, 0, 400, 200, "#32770");
        var folder = Window(0x502, "Documents", "explorer", 700, 1500, 800, 400, 200, "CabinetWClass");
        OpenWindows(dialog, folder);

        // Act
        await sut.ApplyPresetAsync("folder");

        // Assert
        VerifyPlaced(folder, 0, 0, 960, 1040);
    }

    [Fact]
    public async Task Apply_TheExePathRanksWindowsOfOneProcessName()
    {
        // Arrange - two python.exe windows from different installations
        var sut = CreateSut();
        await SaveAsync(sut, "python",
            Placement(new WindowMatcher(ProcessName: "python") { ExactProcessName = true, ExePath = @"C:\Python312\python.exe" }, 0));
        var other = Window(0x601, "IDLE Shell", "python", 1601, 0, 0, 960, 1040);
        var wanted = Window(0x602, "IDLE Shell", "python", 1602, 1500, 800, 400, 200);
        OpenWindows(other, wanted);
        _processes.Setup(p => p.GetProcessPath(1601)).Returns(@"C:\Python311\python.exe");
        _processes.Setup(p => p.GetProcessPath(1602)).Returns(@"C:\Python312\python.exe");

        // Act
        await sut.ApplyPresetAsync("python");

        // Assert
        VerifyPlaced(wanted, 0, 0, 960, 1040);
    }

    #endregion

    #region Placements that cannot be matched honestly

    [Fact]
    public async Task Apply_TitleOnly_OnAPlacementWithoutTitleText_IsNotApplicable_AndMovesNothing()
    {
        // Arrange - before, an empty title criterion matched every window, the agent's own terminal included
        var sut = CreateSut();
        await SaveAsync(sut, "captured",
            Placement(new WindowMatcher(ProcessName: "chrome") { ExactProcessName = true, TitlePattern = ChromeLeft.Title }, 0));
        OpenWindows(ChromeLeft, ChromeRight);

        // Act
        var result = await sut.ApplyPresetAsync("captured", LayoutMatchBy.TitleOnly);

        // Assert
        result.Success.Should().BeFalse();
        result.Status.Should().Be(LayoutApplyStatus.Failed);
        result.ErrorCode.Should().Be(nameof(LayoutErrorCode.NoMatchCriterion));
        result.Errors.Should().ContainSingle().Which.Should().Contain("title_only").And.Contain("titleContains");
        var placement = result.Placements.Should().ContainSingle().Subject;
        placement.Status.Should().Be(LayoutPlacementStatus.NotApplicable);
        placement.ErrorCode.Should().Be(nameof(LayoutErrorCode.NoMatchCriterion));
        placement.Window.Should().BeNull();
        VerifyNothingPlaced();
    }

    [Theory]
    [InlineData(LayoutMatchBy.ProcessAndTitle)]
    [InlineData(LayoutMatchBy.ProcessOnly)]
    public async Task Apply_AnEmptyMatcher_IsNotApplicable(LayoutMatchBy matchBy)
    {
        // Arrange
        var sut = CreateSut();
        await SaveAsync(sut, "empty", Placement(new WindowMatcher(), 0));
        OpenWindows(ChromeLeft);

        // Act
        var result = await sut.ApplyPresetAsync("empty", matchBy);

        // Assert
        result.Placements.Should().ContainSingle().Which.Status.Should().Be(LayoutPlacementStatus.NotApplicable);
        result.ErrorCode.Should().Be(nameof(LayoutErrorCode.NoMatchCriterion));
        VerifyNothingPlaced();
    }

    [Fact]
    public async Task Apply_APlacementOnAMonitorThatDoesNotExist_FailsAlone_WithoutFallingBackToThePrimary()
    {
        // Arrange - placement 0 was saved on a second monitor that is not connected now
        var sut = CreateSut();
        await SaveAsync(sut, "docked",
            Placement(new WindowMatcher(TitleContains: "Mail", ProcessName: "chrome"), 0, monitorIndex: 1),
            Placement(new WindowMatcher(TitleContains: "GitHub", ProcessName: "chrome"), 1, x: 0.5));
        OpenWindows(ChromeLeft, ChromeRight);

        // Act
        var result = await sut.ApplyPresetAsync("docked");

        // Assert
        result.Success.Should().BeFalse();
        result.Status.Should().Be(LayoutApplyStatus.Partial);
        result.ErrorCode.Should().Be(nameof(WindowErrorCode.InvalidMonitorIndex));
        result.Errors.Should().ContainSingle().Which.Should().Contain("Monitor index 1 does not exist");
        result.Placements!.Select(p => (p.Status, p.ErrorCode)).Should().Equal(
            (LayoutPlacementStatus.Failed, nameof(WindowErrorCode.InvalidMonitorIndex)),
            (LayoutPlacementStatus.Applied, null));
        _windows.Verify(w => w.SetWindowBounds(ChromeLeft.Handle, It.IsAny<int>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<bool>()),
            Times.Never);
        VerifyPlaced(ChromeRight, 960, 0, 960, 1040);
    }

    [Fact]
    public async Task Apply_MatchesTitlesAndProcessNames_AsTheWindowSelectorsDo()
    {
        // Arrange - Edge writes a U+200B zero-width space into "Microsoft Edge" and adds " and N more pages"; a hand-written
        // preset names "Microsoft Edge" and "msedge.exe" (roadmap M2.4: the selectors' title normalizer and process-name rule)
        var edge = Window(0x301, "Inbox and 2 more pages - Personal - Microsoft​ Edge", "msedge", 900, 0, 0, 960, 1040, "Chrome_WidgetWin_1");
        var sut = CreateSut();
        await SaveAsync(sut, "edge",
            Placement(new WindowMatcher(TitleContains: "Inbox - Personal - Microsoft Edge", ProcessName: "msedge.exe"), 0, x: 0.5));
        OpenWindows(edge);

        // Act
        var result = await sut.ApplyPresetAsync("edge");

        // Assert
        result.Placements!.Single().Status.Should().Be(LayoutPlacementStatus.Applied);
        VerifyPlaced(edge, 960, 0, 960, 1040);
    }

    #endregion

    #region Per-placement results and failures

    [Fact]
    public async Task Apply_ReportsEachPlacement_WithItsStatusWindowAndErrorCode()
    {
        // Arrange
        var sut = CreateSut();
        await SaveAsync(sut, "report",
            Placement(new WindowMatcher(ProcessName: "chrome"), 0, focus: true),
            Placement(new WindowMatcher(ProcessName: "slack"), 1),
            Placement(new WindowMatcher(ProcessName: "app"), 2, launchCommand: "app.exe"));
        var launched = Window(8888, string.Empty, "app", 9999, 10, 10, 300, 200);
        OpenWindows(ChromeLeft);
        _windows.Setup(w => w.GetWindowInfo(launched.Handle)).Returns(launched);

        // Act
        var result = await sut.ApplyPresetAsync("report", launchMissing: true);

        // Assert - the freshly launched untitled window is placed (before, an untitled window ended the call with an NRE)
        result.Placements.Should().Equal(
            new LayoutPlacementResult(0, LayoutPlacementStatus.Applied, LayoutWindowRef.From(ChromeLeft), Focused: true),
            new LayoutPlacementResult(1, LayoutPlacementStatus.NotFound, ErrorCode: nameof(LayoutErrorCode.WindowNotFound),
                Error: "No open window matched placement 1 (process 'slack')"),
            new LayoutPlacementResult(2, LayoutPlacementStatus.Applied, new LayoutWindowRef(8888, string.Empty, "app"), Launched: true,
                LaunchCommand: "app.exe"));
        result.WindowsLaunched.Should().Be(1);
        result.Status.Should().Be(LayoutApplyStatus.Partial);
    }

    [Fact]
    public async Task Apply_AnUnexpectedErrorInOnePlacement_FailsOnlyThatPlacement()
    {
        // Arrange
        var sut = CreateSut();
        await SaveAsync(sut, "throws",
            Placement(new WindowMatcher(TitleContains: "Mail", ProcessName: "chrome"), 0),
            Placement(new WindowMatcher(TitleContains: "GitHub", ProcessName: "chrome"), 1, x: 0.5));
        OpenWindows(ChromeLeft, ChromeRight);
        _windows.Setup(w => w.SetWindowBounds(ChromeLeft.Handle, It.IsAny<int>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<bool>()))
            .Throws(new InvalidOperationException("The window's thread went away"));

        // Act
        var result = await sut.ApplyPresetAsync("throws");

        // Assert
        result.Success.Should().BeFalse();
        result.ErrorCode.Should().Be(nameof(LayoutErrorCode.ArrangeFailed));
        result.Placements!.Select(p => p.Status).Should().Equal(LayoutPlacementStatus.Failed, LayoutPlacementStatus.Applied);
        result.Placements![0].Error.Should().Contain("The window's thread went away");
        VerifyPlaced(ChromeRight, 960, 0, 960, 1040);
    }

    [Fact]
    public async Task Apply_ALaunchThatBringsUpAWindowAnotherPlacementWasGiven_FailsThatPlacement()
    {
        // Arrange - a single-instance app hands the launch to its open window, which placement 0 already has
        var sut = CreateSut();
        await SaveAsync(sut, "single-instance",
            Placement(new WindowMatcher(TitleContains: "Mail", ProcessName: "chrome"), 0),
            Placement(new WindowMatcher(TitleContains: "Calendar", ProcessName: "chrome"), 1, x: 0.5, launchCommand: "chrome"));
        OpenWindows(ChromeLeft);
        _processes.Setup(p => p.LaunchApplicationAsync(It.IsAny<string>(), It.IsAny<string[]?>(), It.IsAny<string?>(), It.IsAny<bool>(),
                It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new LaunchResult(true, ProcessId: 501, WindowHandle: ChromeLeft.Handle));

        // Act
        var result = await sut.ApplyPresetAsync("single-instance", launchMissing: true);

        // Assert
        var second = result.Placements![1];
        second.Status.Should().Be(LayoutPlacementStatus.Failed);
        second.ErrorCode.Should().Be(nameof(LayoutErrorCode.LaunchFailed));
        second.Error.Should().Contain("placement 0");
        _windows.Verify(w => w.SetWindowBounds(ChromeLeft.Handle, It.IsAny<int>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<bool>()),
            Times.Once);
    }

    [Fact]
    public async Task Apply_AFocusThatFails_LeavesThePlacementApplied_WithTheErrorCode()
    {
        // Arrange
        _foreground.Setup(f => f.EnsureForeground(It.IsAny<nint>(), It.IsAny<int>()))
            .Returns((nint handle, int _) => new FocusOutcome { Target = handle, ErrorCode = WindowErrorCode.FocusFailed, Error = "kept back" });
        var sut = CreateSut();
        await SaveAsync(sut, "focus", Placement(new WindowMatcher(ProcessName: "chrome"), 0, focus: true));
        OpenWindows(ChromeLeft);

        // Act
        var result = await sut.ApplyPresetAsync("focus");

        // Assert
        var placement = result.Placements.Should().ContainSingle().Subject;
        placement.Status.Should().Be(LayoutPlacementStatus.Applied);
        placement.Focused.Should().BeFalse();
        placement.ErrorCode.Should().Be(nameof(LayoutErrorCode.ArrangeFailed));
        result.Status.Should().Be(LayoutApplyStatus.Partial);
        result.Focus.Should().ContainSingle();
    }

    [Fact]
    public async Task Apply_WhenThePresetIsNotFound_HasStatusFailedAndNoPlacements()
    {
        var result = await CreateSut().ApplyPresetAsync("nothing");

        result.Status.Should().Be(LayoutApplyStatus.Failed);
        result.Placements.Should().BeNull();
    }

    #endregion

    #region Window state

    [Fact]
    public async Task Apply_AMaximizedPlacement_MaximizesTheWindowOnItsMonitor()
    {
        // Arrange
        var sut = CreateSut();
        await SaveAsync(sut, "max", Placement(new WindowMatcher(ProcessName: "chrome"), 0, width: 1, height: 1) with { State = WindowState.Maximized });
        OpenWindows(ChromeLeft);

        // Act
        var result = await sut.ApplyPresetAsync("max");

        // Assert
        result.Success.Should().BeTrue();
        _windows.Verify(w => w.MoveWindowToMonitor(ChromeLeft.Handle, 0, MonitorPositioning.Maximize, It.IsAny<bool>()), Times.Once);
        VerifyNothingPlaced();
    }

    [Fact]
    public async Task Apply_AMaximizedPlacementThatCannotBeMaximized_Fails()
    {
        // Arrange
        _windows.Setup(w => w.MoveWindowToMonitor(It.IsAny<nint>(), It.IsAny<int>(), It.IsAny<MonitorPositioning>(), It.IsAny<bool>()))
            .Returns((nint handle, int _, MonitorPositioning _, bool _) => WindowChangeResult.Failed(handle, WindowErrorCode.StateChangeFailed, "stayed normal"));
        var sut = CreateSut();
        await SaveAsync(sut, "max", Placement(new WindowMatcher(ProcessName: "chrome"), 0) with { State = WindowState.Maximized });
        OpenWindows(ChromeLeft);

        // Act
        var result = await sut.ApplyPresetAsync("max");

        // Assert
        result.Success.Should().BeFalse();
        result.Placements!.Single().Should().Match<LayoutPlacementResult>(p =>
            p.Status == LayoutPlacementStatus.Failed && p.ErrorCode == nameof(LayoutErrorCode.ArrangeFailed) && p.Error!.Contains("StateChangeFailed"));
    }

    [Fact]
    public async Task Apply_ANormalPlacement_PlacesAMaximizedWindow_WithoutAnActivatingRestore()
    {
        // Arrange - SetWindowBounds restores a maximized window itself, without activating it (SW_SHOWNOACTIVATE); the
        // set_window_state Restore (SW_RESTORE) would bring it to the front
        var sut = CreateSut();
        await SaveAsync(sut, "normal", Placement(new WindowMatcher(ProcessName: "chrome"), 0) with { State = WindowState.Normal });
        var maximized = ChromeLeft with { State = WindowState.Maximized };
        OpenWindows(maximized);

        // Act
        var result = await sut.ApplyPresetAsync("normal");

        // Assert
        result.Success.Should().BeTrue();
        VerifyPlaced(maximized, 0, 0, 960, 1040);
        _windows.Verify(w => w.SetWindowState(It.IsAny<nint>(), It.IsAny<WindowStateRequest>()), Times.Never);
    }

    [Fact]
    public async Task Apply_AMinimizeThatDoesNotAnswer_LeavesThePlacementApplied_WithNotResponding()
    {
        // Arrange - the window was placed, then its app stopped answering
        var sut = CreateSut();
        await SaveAsync(sut, "min", Placement(new WindowMatcher(ProcessName: "chrome"), 0) with { State = WindowState.Minimized });
        OpenWindows(ChromeLeft);
        _windows.Setup(w => w.SetWindowState(ChromeLeft.Handle, WindowStateRequest.Minimize))
            .Throws(new WindowNotRespondingException(ChromeLeft.Handle, "The window did not answer within 5 s."));

        // Act
        var result = await sut.ApplyPresetAsync("min");

        // Assert
        var placement = result.Placements.Should().ContainSingle().Subject;
        placement.Status.Should().Be(LayoutPlacementStatus.Applied);
        placement.ErrorCode.Should().Be(nameof(LayoutErrorCode.ArrangeFailed));
        placement.Error.Should().Contain("could not minimize it (NotResponding)").And.Contain("did not answer");
        result.WindowsArranged.Should().Be(1);
        result.Status.Should().Be(LayoutApplyStatus.Partial);
    }

    [Fact]
    public async Task Apply_ALaunchedPlacementThatThrows_StillReportsItsLaunch_AndWindow()
    {
        // Arrange - the launched window cannot be arranged: an unexpected error after the launch
        var launched = Window(0x501, "Untitled - Notepad", "notepad", 900, 0, 0, 800, 600);
        var sut = CreateSut();
        await SaveAsync(sut, "launch", Placement(new WindowMatcher(ProcessName: "notepad"), 0, launchCommand: "notepad"));
        OpenWindows();
        _windows.Setup(w => w.GetWindowInfo(launched.Handle)).Returns(launched);
        _processes.Setup(p => p.LaunchApplicationAsync(It.IsAny<string>(), It.IsAny<string[]?>(), It.IsAny<string?>(), It.IsAny<bool>(),
                It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new LaunchResult(true, ProcessId: 900, WindowHandle: launched.Handle));
        _windows.Setup(w => w.SetWindowBounds(launched.Handle, It.IsAny<int>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<bool>()))
            .Throws(new InvalidOperationException("The window's thread went away"));

        // Act
        var result = await sut.ApplyPresetAsync("launch", launchMissing: true);

        // Assert
        var placement = result.Placements.Should().ContainSingle().Subject;
        placement.Status.Should().Be(LayoutPlacementStatus.Failed);
        placement.Launched.Should().BeTrue("the agent must not launch it again");
        placement.LaunchCommand.Should().Be("notepad");
        placement.Window!.Handle.Should().Be(0x501);
        result.WindowsLaunched.Should().Be(1);
    }

    [Fact]
    public async Task Apply_AnArrangeThatDoesNotAnswer_IsReportedAsNotResponding()
    {
        // Arrange
        var sut = CreateSut();
        await SaveAsync(sut, "hung", Placement(new WindowMatcher(ProcessName: "chrome"), 0) with { State = WindowState.Maximized });
        OpenWindows(ChromeLeft);
        _windows.Setup(w => w.MoveWindowToMonitor(ChromeLeft.Handle, It.IsAny<int>(), It.IsAny<MonitorPositioning>(), It.IsAny<bool>()))
            .Throws(new WindowNotRespondingException(ChromeLeft.Handle, "hung"));

        // Act
        var result = await sut.ApplyPresetAsync("hung");

        // Assert
        result.Placements!.Single().Error.Should().Contain("NotResponding: hung").And.NotContain(nameof(WindowNotRespondingException));
    }

    [Fact]
    public async Task Apply_APlacementThatLaunchesNothing_HasNoLaunchCommand()
    {
        var sut = CreateSut();
        await SaveAsync(sut, "open", Placement(new WindowMatcher(ProcessName: "chrome"), 0, launchCommand: "chrome"));
        OpenWindows(ChromeLeft);

        var result = await sut.ApplyPresetAsync("open", launchMissing: true);

        result.Placements!.Single().LaunchCommand.Should().BeNull("the window was open, so nothing was launched");
    }

    [Fact]
    public async Task Apply_AMinimizedPlacement_PlacesTheWindowThenMinimizesIt()
    {
        // Arrange
        var sut = CreateSut();
        await SaveAsync(sut, "min", Placement(new WindowMatcher(ProcessName: "chrome"), 0) with { State = WindowState.Minimized });
        OpenWindows(ChromeLeft);

        // Act
        var result = await sut.ApplyPresetAsync("min");

        // Assert
        result.Success.Should().BeTrue();
        VerifyPlaced(ChromeLeft, 0, 0, 960, 1040);
        _windows.Verify(w => w.SetWindowState(ChromeLeft.Handle, WindowStateRequest.Minimize), Times.Once);
    }

    #endregion
}
