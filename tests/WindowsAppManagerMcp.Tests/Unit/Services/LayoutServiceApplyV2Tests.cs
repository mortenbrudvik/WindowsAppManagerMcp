namespace WindowsAppManagerMcp.Tests.Unit.Services;

/// <summary>
/// apply_layout in schema version 2 (roadmap M6.8, owner decision D15): monitors found by identity with an honest fallback,
/// maximize as restore rectangle then maximize, a minimized window given its restore rectangle without being shown, snap
/// positions, launches through LaunchRequest, the dry run (which changes nothing), a subset of placements, and the user's
/// confirmation of an agent-written layout's launches. The services are mocks; only the preset files are real.
/// </summary>
public class LayoutServiceApplyV2Tests : IDisposable
{
    private readonly string _layoutsPath = Path.Combine(Path.GetTempPath(), $"LayoutServiceApplyV2Tests_{Guid.NewGuid():N}");
    private readonly Mock<IWindowService> _windows = TestDataFactory.CreateMockWindowService([]);
    private readonly Mock<IMonitorService> _monitors;
    private readonly Mock<IProcessService> _processes = TestDataFactory.CreateMockProcessService();
    private readonly Mock<IForegroundService> _foreground = TestDataFactory.CreateMockForegroundService();
    private readonly Mock<ILaunchPlacementService> _placer = new();
    private readonly List<(nint Handle, LaunchPlacementPlan Plan)> _placed = [];
    private readonly List<string> _calls = [];

    // The docked monitors: the primary 'm-11111111' at index 0, 'm-22222222' (a DELL) right of it at index 1
    private static readonly MonitorInfo Primary =
        TestDataFactory.CreateMonitorInfo() with { Id = "m-11111111", FriendlyName = "Built-in" };

    private static readonly MonitorInfo Dell =
        TestDataFactory.CreateMonitorInfo(1, @"\\.\DISPLAY2", false, 1920, 0, 2560, 1440, 1920, 0, 2560, 1400, 1.25)
            with { Id = "m-22222222", FriendlyName = "DELL U2720Q" };

    private List<MonitorInfo> _monitorList = [Primary, Dell];

    private static readonly WindowInfo Code = Window(0x101, "Code", 501, 100, 100, 800, 600);
    private static readonly WindowInfo Notepad = Window(0x102, "notepad", 502, 200, 200, 700, 500);

    public LayoutServiceApplyV2Tests()
    {
        _monitors = TestDataFactory.CreateMockMonitorService(_monitorList);
        _monitors.Setup(m => m.GetAllMonitors()).Returns(() => _monitorList);
        _windows.Setup(w => w.SetWindowBounds(It.IsAny<nint>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<bool>()))
            .Returns((nint h, int x, int y, int width, int height, bool _) => Record(h, $"bounds {x},{y},{width},{height}"));
        _windows.Setup(w => w.SetWindowState(It.IsAny<nint>(), It.IsAny<WindowStateRequest>()))
            .Returns((nint h, WindowStateRequest state) => Record(h, $"state {state}"));
        _windows.Setup(w => w.MoveWindowToMonitor(It.IsAny<nint>(), It.IsAny<int>(), It.IsAny<MonitorPositioning>(), It.IsAny<bool>()))
            .Returns((nint h, int monitor, MonitorPositioning positioning, bool _) => Record(h, $"monitor {monitor} {positioning}"));
        _windows.Setup(w => w.SnapWindow(It.IsAny<nint>(), It.IsAny<SnapPosition>(), It.IsAny<int?>(), It.IsAny<bool>()))
            .Returns((nint h, SnapPosition snap, int? monitor, bool _) => Record(h, $"snap {snap} {monitor}"));
        _windows.Setup(w => w.SetMinimizedBounds(It.IsAny<nint>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<int>()))
            .Returns((nint h, int x, int y, int width, int height) => Record(h, $"minimized bounds {x},{y},{width},{height}"));
        _placer.Setup(p => p.PlaceAsync(It.IsAny<IReadOnlyList<(nint, LaunchPlacementPlan)>>(), It.IsAny<CancellationToken>()))
            .Returns<IReadOnlyList<(nint Handle, LaunchPlacementPlan Plan)>, CancellationToken>((placements, _) =>
            {
                _placed.AddRange(placements);
                return Task.FromResult<IReadOnlyList<LaunchPlacementOutcome>>(placements
                    .Select(p => new LaunchPlacementOutcome(p.Handle, Notepad, false, false, WindowErrorCode.None, null)).ToList());
            });
        _processes.Setup(p => p.LaunchApplicationAsync(It.IsAny<LaunchRequest>(), It.IsAny<bool>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new LaunchResult(true, ProcessId: 502, WindowHandle: Notepad.Handle));
        _windows.Setup(w => w.GetWindowInfo(Notepad.Handle)).Returns(Notepad);
    }

    public void Dispose()
    {
        if (Directory.Exists(_layoutsPath))
            Directory.Delete(_layoutsPath, recursive: true);
    }

    private LayoutService CreateSut() =>
        new(_windows.Object, _monitors.Object, _processes.Object, _foreground.Object, _layoutsPath, _placer.Object);

    private WindowChangeResult Record(nint handle, string call)
    {
        _calls.Add(call);
        return WindowChangeResult.Done(handle);
    }

    private static WindowInfo Window(int handle, string process, int pid, int x, int y, int width, int height,
        WindowState state = WindowState.Normal, int monitor = 0) =>
        TestDataFactory.CreateWindowInfo(handle, $"{process} window", process, pid, x, y, width, height, state, monitorIndex: monitor);

    private void OpenWindows(params WindowInfo[] windows) => _windows.Setup(w => w.GetAllWindows(It.IsAny<bool>())).Returns(windows);

    private static WindowPlacement Placement(string process, int order = 0, int monitorIndex = 0, string? monitor = null,
        string? monitorName = null) =>
        new(new WindowMatcher(ProcessName: process), monitorIndex, new RelativePosition(0, 0, 0.5, 1), Order: order)
        {
            Monitor = monitor,
            MonitorName = monitorName
        };

    private async Task<LayoutService> SavedAsync(params WindowPlacement[] placements) => await SavedAsync(null, placements);

    private async Task<LayoutService> SavedAsync(string? authoredBy, params WindowPlacement[] placements)
    {
        var sut = CreateSut();
        (await sut.SavePresetAsync(new LayoutPreset("layout", null, [.. placements]) { AuthoredBy = authoredBy })).Success.Should().BeTrue();
        return sut;
    }

    private void VerifyNothingChanged()
    {
        _calls.Should().BeEmpty();
        _foreground.Verify(f => f.EnsureForeground(It.IsAny<nint>(), It.IsAny<int>()), Times.Never);
        _processes.Verify(p => p.LaunchApplicationAsync(It.IsAny<LaunchRequest>(), It.IsAny<bool>(), It.IsAny<int>(), It.IsAny<CancellationToken>()),
            Times.Never);
        _processes.Verify(p => p.LaunchApplicationAsync(It.IsAny<string>(), It.IsAny<string[]?>(), It.IsAny<string?>(), It.IsAny<bool>(),
            It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Never);
        _placer.Verify(p => p.PlaceAsync(It.IsAny<IReadOnlyList<(nint, LaunchPlacementPlan)>>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    #region Monitors by identity

    [Fact]
    public async Task AMonitorSavedById_IsFoundWhereverItIsNow_WithoutAFallback()
    {
        // Saved at index 0; after docking the DELL is index 0 and the built-in panel index 1
        _monitorList = [Dell with { Index = 0 }, Primary with { Index = 1 }];
        OpenWindows(Code);
        var sut = await SavedAsync(Placement("Code", monitorIndex: 1, monitor: "m-22222222"));

        var result = await sut.ApplyPresetAsync("layout");

        result.Success.Should().BeTrue();
        result.Placements!.Single().MonitorFallback.Should().BeNull();
        _calls.Should().Equal("bounds 1920,0,1280,1400");
    }

    [Fact]
    public async Task AMonitorThatIsNotConnected_FallsBackToTheOneWithItsName_AndSaysSo()
    {
        // The DELL moved to another port: a new id, the same EDID name
        _monitorList = [Primary, Dell with { Id = "m-33333333" }];
        OpenWindows(Code);
        var sut = await SavedAsync(Placement("Code", monitorIndex: 0, monitor: "m-22222222", monitorName: "DELL U2720Q"));

        var result = await sut.ApplyPresetAsync("layout");

        result.Placements!.Single().MonitorFallback.Should().Be(LayoutMonitorFallback.Name);
        _calls.Should().Equal("bounds 1920,0,1280,1400");
    }

    [Fact]
    public async Task AMonitorWithNeitherIdNorName_FallsBackToItsIndex_AndSaysSo()
    {
        _monitorList = [Primary];
        OpenWindows(Code);
        var sut = await SavedAsync(Placement("Code", monitorIndex: 0, monitor: "m-22222222", monitorName: "DELL U2720Q"));

        var result = await sut.ApplyPresetAsync("layout");

        result.Placements!.Single().MonitorFallback.Should().Be(LayoutMonitorFallback.Index);
        result.Placements!.Single().Status.Should().Be(LayoutPlacementStatus.Applied);
    }

    [Fact]
    public async Task AMonitorThatIsGone_WithAnIndexThatIsGoneToo_IsMonitorNotFound()
    {
        _monitorList = [Primary];
        OpenWindows(Code);
        var sut = await SavedAsync(Placement("Code", monitorIndex: 1, monitor: "m-22222222", monitorName: "DELL U2720Q"));

        var result = await sut.ApplyPresetAsync("layout");

        var placement = result.Placements!.Single();
        placement.Status.Should().Be(LayoutPlacementStatus.Failed);
        placement.ErrorCode.Should().Be(nameof(WindowErrorCode.MonitorNotFound));
        placement.Error.Should().Contain("m-22222222").And.Contain("DELL U2720Q");
        _calls.Should().BeEmpty("the window never lands on another screen with success");
    }

    [Fact]
    public async Task AVersion1Placement_UsesItsIndex_WithoutAFallback()
    {
        OpenWindows(Code);
        var sut = await SavedAsync(Placement("Code", monitorIndex: 1));

        var result = await sut.ApplyPresetAsync("layout");

        result.Placements!.Single().MonitorFallback.Should().BeNull();
        _calls.Should().Equal("bounds 1920,0,1280,1400");
    }

    [Fact]
    public async Task AKeyword_IsResolvedWhenTheLayoutIsApplied()
    {
        OpenWindows(Code);
        var sut = await SavedAsync(Placement("Code", monitorIndex: 0, monitor: "right"));

        await sut.ApplyPresetAsync("layout");

        _calls.Should().Equal("bounds 1920,0,1280,1400");
    }

    #endregion

    #region States and snap positions

    [Fact]
    public async Task AMaximizedPlacement_GetsItsRestoreRectangleFirst_ThenIsMaximized()
    {
        OpenWindows(Code);
        var sut = await SavedAsync(Placement("Code") with
        {
            State = WindowState.Maximized,
            NormalPosition = new RelativePosition(0.1, 0.1, 0.5, 0.5)
        });

        var result = await sut.ApplyPresetAsync("layout");

        result.Success.Should().BeTrue();
        _calls.Should().Equal("bounds 192,104,960,520", "state Maximize");
    }

    [Fact]
    public async Task AWindowAlreadyMaximizedOnItsMonitor_IsLeftAlone()
    {
        OpenWindows(Code with { State = WindowState.Maximized });
        var sut = await SavedAsync(Placement("Code") with
        {
            State = WindowState.Maximized,
            NormalPosition = new RelativePosition(0.1, 0.1, 0.5, 0.5)
        });

        (await sut.ApplyPresetAsync("layout")).Success.Should().BeTrue();

        _calls.Should().BeEmpty("maximizing activates; a window that is already there is not touched");
    }

    [Fact]
    public async Task AMaximizedPlacementWithoutARestoreRectangle_IsMaximizedOnItsMonitor_AsBefore()
    {
        OpenWindows(Code);
        var sut = await SavedAsync(Placement("Code", monitorIndex: 1) with { State = WindowState.Maximized });

        await sut.ApplyPresetAsync("layout");

        _calls.Should().Equal("monitor 1 Maximize");
    }

    [Fact]
    public async Task AMinimizedWindowThatStaysMinimized_GetsItsRestoreRectangle_WithoutBeingShown()
    {
        OpenWindows(Code with { State = WindowState.Minimized });
        var sut = await SavedAsync(Placement("Code") with
        {
            State = WindowState.Minimized,
            NormalPosition = new RelativePosition(0.1, 0.1, 0.5, 0.5)
        });

        var result = await sut.ApplyPresetAsync("layout");

        result.Success.Should().BeTrue();
        _calls.Should().Equal("minimized bounds 192,104,960,520");
    }

    [Fact]
    public async Task ANormalWindowOfAMinimizedPlacement_IsPlacedThenMinimized_AsBefore()
    {
        OpenWindows(Code);
        var sut = await SavedAsync(Placement("Code") with { State = WindowState.Minimized });

        await sut.ApplyPresetAsync("layout");

        _calls.Should().Equal("bounds 0,0,960,1040", "state Minimize");
    }

    [Fact]
    public async Task ASnapPosition_IsSnappedOnItsMonitor()
    {
        OpenWindows(Code);
        var sut = await SavedAsync(Placement("Code", monitor: "m-22222222") with { Snap = "right_third" });

        (await sut.ApplyPresetAsync("layout")).Success.Should().BeTrue();

        _calls.Should().Equal("snap RightThird 1");
    }

    #endregion

    #region Dry run

    [Fact]
    public async Task ADryRun_ChangesNothing_AndSaysWhatWouldHappen()
    {
        OpenWindows(Code);
        var sut = await SavedAsync(
            Placement("Code", monitor: "m-22222222") with { Focus = true },
            Placement("notepad", order: 1) with { Launch = new LayoutLaunch(Executable: "notepad.exe") },
            Placement("calc", order: 2));

        var result = await sut.ApplyPresetAsync("layout", launchMissing: true, options: new LayoutApplyOptions(DryRun: true));

        VerifyNothingChanged();
        result.DryRun.Should().BeTrue();
        result.Status.Should().Be(LayoutApplyStatus.DryRun);
        result.Success.Should().BeFalse("calc has no window and nothing to launch");
        result.Placements!.Select(p => p.Status).Should().Equal(
            LayoutPlacementStatus.WouldApply, LayoutPlacementStatus.WouldLaunch, LayoutPlacementStatus.NotFound);
        result.Placements![0].Target.Should().Be(new BoundsDto(1920, 0, 1280, 1400));
        result.Placements![0].Window!.Handle.Should().Be(Code.Handle.ToInt64());
        result.Placements![1].LaunchCommand.Should().Be("notepad.exe");
        result.Placements![1].Target.Should().Be(new BoundsDto(0, 0, 960, 1040));
        result.WindowsArranged.Should().Be(0);
        result.WindowsLaunched.Should().Be(0);
        result.Focus.Should().BeNull();
    }

    [Fact]
    public async Task ADryRunsTargets_AreWhereTheApplyPutsTheWindows()
    {
        OpenWindows(Code, Notepad);
        var sut = await SavedAsync(
            Placement("Code", monitor: "m-22222222"),
            Placement("notepad", order: 1) with { NormalPosition = new RelativePosition(0.1, 0.1, 0.5, 0.5), State = WindowState.Maximized });

        var preview = await sut.ApplyPresetAsync("layout", options: new LayoutApplyOptions(DryRun: true));
        await sut.ApplyPresetAsync("layout");

        preview.Placements!.Select(p => p.Target).Should().Equal(new BoundsDto(1920, 0, 1280, 1400), new BoundsDto(192, 104, 960, 520));
        _calls.Should().Equal("bounds 1920,0,1280,1400", "bounds 192,104,960,520", "state Maximize");
    }

    [Fact]
    public async Task ADryRun_ReportsAWindowThePolicyDenies()
    {
        OpenWindows(Code);
        _windows.Setup(w => w.EvaluateTargetPolicy(Code.Handle)).Returns(TargetPolicyVerdict.Deny("Code", "Code is on the deny list"));
        var sut = await SavedAsync(Placement("Code"));

        var result = await sut.ApplyPresetAsync("layout", options: new LayoutApplyOptions(DryRun: true));

        result.Placements!.Single().ErrorCode.Should().Be(nameof(WindowErrorCode.TargetDeniedByPolicy));
        VerifyNothingChanged();
    }

    #endregion

    #region A subset of the placements

    [Fact]
    public async Task ASubset_AppliesOnlyThosePlacements()
    {
        OpenWindows(Code, Notepad);
        var sut = await SavedAsync(Placement("Code"), Placement("notepad", order: 1, monitorIndex: 1));

        var result = await sut.ApplyPresetAsync("layout", options: new LayoutApplyOptions(Placements: [1]));

        result.Placements!.Select(p => p.Order).Should().Equal(1);
        result.Success.Should().BeTrue();
        _calls.Should().Equal("bounds 1920,0,1280,1400");
    }

    [Fact]
    public async Task ASubset_GivesEachOfItsPlacementsTheWindowAFullApplyWould()
    {
        // Placement 0 knows its window by title; placement 1 takes any Code window. Alone, placement 1 must not take placement 0's
        var first = Code with { Title = "Project A - Visual Studio Code" };
        var second = Window(0x103, "Code", 501, 900, 100, 800, 600) with { Title = "Project B - Visual Studio Code" };
        OpenWindows(first, second);
        var sut = await SavedAsync(
            Placement("Code") with { Matcher = new WindowMatcher(ProcessName: "Code") { TitlePattern = "Project A - Visual Studio Code" } },
            Placement("Code", order: 1, monitorIndex: 1));

        var result = await sut.ApplyPresetAsync("layout", options: new LayoutApplyOptions(Placements: [1]));

        result.Placements!.Single().Window!.Handle.Should().Be(second.Handle.ToInt64());
    }

    [Theory]
    [InlineData(new[] { 2 }, "2")]
    [InlineData(new[] { -1 }, "-1")]
    [InlineData(new[] { 0, 0 }, "twice")]
    [InlineData(new int[0], "empty")]
    public async Task ASubsetThatNamesNoPlacement_IsInvalidArguments_AndNothingIsArranged(int[] subset, string expected)
    {
        OpenWindows(Code, Notepad);
        var sut = await SavedAsync(Placement("Code"), Placement("notepad", order: 1));

        var result = await sut.ApplyPresetAsync("layout", options: new LayoutApplyOptions(Placements: subset));

        result.Success.Should().BeFalse();
        result.ErrorCode.Should().Be("InvalidArguments");
        result.Errors!.Single().Should().Contain(expected);
        _calls.Should().BeEmpty();
    }

    #endregion

    #region Launches

    [Fact]
    public async Task ALaunch_GoesThroughALaunchRequest_WithTheWindowItExpects()
    {
        OpenWindows();
        var sut = await SavedAsync(Placement("CalculatorApp", monitor: "m-22222222") with
        {
            Snap = "left_half",
            Launch = new LayoutLaunch(AppId: "Microsoft.WindowsCalculator_8wekyb3d8bbwe!App")
        });

        var result = await sut.ApplyPresetAsync("layout", launchMissing: true);

        result.WindowsLaunched.Should().Be(1);
        result.Placements!.Single().LaunchCommand.Should().Be(@"shell:AppsFolder\Microsoft.WindowsCalculator_8wekyb3d8bbwe!App");
        _processes.Verify(p => p.LaunchApplicationAsync(
            It.Is<LaunchRequest>(r => r.Executable == @"shell:AppsFolder\Microsoft.WindowsCalculator_8wekyb3d8bbwe!App" &&
                                      r.Arguments == null && r.ExpectWindow!.ProcessName == "CalculatorApp"),
            true, It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Once);
        _placed.Single().Plan.Should().Be(new LaunchPlacementPlan(SnapPosition.LeftHalf, 1, null, null, false) { MonitorKey = "m-22222222" });
    }

    [Fact]
    public async Task AnAgentWrittenLayoutsLaunch_WaitsForTheUsersConfirmation()
    {
        OpenWindows();
        var sut = await SavedAsync(LayoutPresetSchema.AgentAuthor, Placement("notepad") with { Launch = new LayoutLaunch(Executable: "notepad.exe") });

        var refused = await sut.ApplyPresetAsync("layout", launchMissing: true,
            options: new LayoutApplyOptions(LaunchRefusal: "the user declined"));

        var placement = refused.Placements!.Single();
        placement.Status.Should().Be(LayoutPlacementStatus.Failed);
        placement.ErrorCode.Should().Be(nameof(LayoutErrorCode.LaunchNotConfirmed));
        placement.Error.Should().Contain("the user declined").And.Contain("notepad.exe");
        VerifyNothingChanged();

        sut.ConfirmLaunches(sut.GetPreset("layout")!);
        var confirmed = await sut.ApplyPresetAsync("layout", launchMissing: true);
        confirmed.WindowsLaunched.Should().Be(1);
    }

    [Fact]
    public async Task ADryRunOfAnUnconfirmedAgentLayout_SaysTheLaunchNeedsConfirmation()
    {
        OpenWindows();
        var sut = await SavedAsync(LayoutPresetSchema.AgentAuthor, Placement("notepad") with { Launch = new LayoutLaunch(Executable: "notepad.exe") });

        var result = await sut.ApplyPresetAsync("layout", launchMissing: true, options: new LayoutApplyOptions(DryRun: true));

        var placement = result.Placements!.Single();
        placement.Status.Should().Be(LayoutPlacementStatus.WouldLaunch);
        placement.ErrorCode.Should().Be(nameof(LayoutErrorCode.LaunchNotConfirmed));
    }

    [Fact]
    public async Task AConfirmation_HoldsAcrossRestarts_UntilThePresetIsSavedAgain()
    {
        var placement = Placement("notepad") with { Launch = new LayoutLaunch(Executable: "notepad.exe") };
        var sut = await SavedAsync(LayoutPresetSchema.AgentAuthor, placement);
        sut.AreLaunchesConfirmed(sut.GetPreset("layout")!).Should().BeFalse();

        sut.ConfirmLaunches(sut.GetPreset("layout")!);

        var restarted = CreateSut();
        restarted.AreLaunchesConfirmed(restarted.GetPreset("layout")!).Should().BeTrue();
        File.Exists(Path.Combine(_layoutsPath, LayoutService.LaunchConfirmationsFileName)).Should().BeTrue();

        // The agent saves it again: a new version, asked again
        await Task.Delay(20);
        await restarted.SavePresetAsync(new LayoutPreset("layout", null, [placement])
        {
            AuthoredBy = LayoutPresetSchema.AgentAuthor, UpdatedAt = DateTime.UtcNow
        });
        restarted.AreLaunchesConfirmed(restarted.GetPreset("layout")!).Should().BeFalse();
    }

    [Fact]
    public void APresetNoAgentWrote_NeedsNoConfirmation()
    {
        CreateSut().AreLaunchesConfirmed(new LayoutPreset("p", null, [])).Should().BeTrue();
    }

    #endregion

    #region Agent-written presets

    [Fact]
    public async Task SaveAuthoredLayout_WritesAnAgentPreset()
    {
        var sut = CreateSut();

        var result = await sut.SaveAuthoredLayoutAsync("authored", "two halves",
        [
            new LayoutPlacementSpec("Code", Snap: "left_half"),
            new LayoutPlacementSpec(TitleContains: "Inbox", Snap: "right_half", Monitor: MonitorSelector.Of("right"))
        ]);

        result.Success.Should().BeTrue(result.ErrorMessage);
        var preset = sut.GetPreset("authored")!;
        preset.AuthoredBy.Should().Be(LayoutPresetSchema.AgentAuthor);
        preset.Placements.Select(p => p.Order).Should().Equal(0, 1);
        preset.Placements[1].MonitorIndex.Should().Be(1);
    }

    [Fact]
    public async Task SaveAuthoredLayout_WithAnInvalidPlacement_SavesNothing()
    {
        var sut = CreateSut();
        await sut.SaveAuthoredLayoutAsync("authored", null, [new LayoutPlacementSpec("Code", Snap: "left_half")]);

        var result = await sut.SaveAuthoredLayoutAsync("authored", null,
            [new LayoutPlacementSpec("Code", Snap: "right_half"), new LayoutPlacementSpec("Code", Snap: "nowhere")]);

        result.Success.Should().BeFalse();
        result.ErrorCode.Should().Be(nameof(LayoutErrorCode.InvalidPlacement));
        result.ErrorMessage.Should().StartWith("placements[1].snap");
        sut.GetPreset("authored")!.Placements.Single().Snap.Should().Be("left_half", "the preset that was there is untouched");
    }

    [Fact]
    public async Task SaveAuthoredLayout_WithoutPlacements_IsInvalidPlacement()
    {
        var result = await CreateSut().SaveAuthoredLayoutAsync("authored", null, []);

        result.ErrorCode.Should().Be(nameof(LayoutErrorCode.InvalidPlacement));
    }

    #endregion

    #region Review M6.8

    private string StorePath => Path.Combine(_layoutsPath, LayoutService.LaunchConfirmationsFileName);

    private async Task<(LayoutService Sut, LayoutPreset Preset)> AgentLayoutAsync(string authoredBy = LayoutPresetSchema.AgentAuthor)
    {
        OpenWindows();
        var sut = await SavedAsync(authoredBy, Placement("notepad") with { Launch = new LayoutLaunch(Executable: "notepad.exe") });
        return (sut, sut.GetPreset("layout")!);
    }

    [Fact]
    public async Task SaveAuthoredLayout_ThatStartsMoreThanTheQuestionShows_IsInvalidPlacement()
    {
        var specs = Enumerable.Range(0, 9)
            .Select(i => new LayoutPlacementSpec($"app{i}", Snap: "left_half", Launch: new LayoutLaunch($@"C:\Tools\app{i}.exe")))
            .ToList();

        var result = await CreateSut().SaveAuthoredLayoutAsync("many", null, specs);

        result.ErrorCode.Should().Be(nameof(LayoutErrorCode.InvalidPlacement));
        result.ErrorMessage.Should().Contain("9").And.Contain("at most 8");
        CreateSut().GetPreset("many").Should().BeNull();
    }

    [Fact]
    public async Task ADescriptionThatIsTooLong_IsRefused_ByBothSaves()
    {
        var description = new string('d', LayoutPresetSchema.MaxDescriptionLength + 1);
        OpenWindows(Code);

        (await CreateSut().SaveAuthoredLayoutAsync("long", description, [new LayoutPlacementSpec("Code", Snap: "left_half")]))
            .ErrorCode.Should().Be("InvalidArguments");
        (await CreateSut().CaptureCurrentLayoutAsync("long", description)).ErrorCode.Should().Be("InvalidArguments");
    }

    [Theory]
    [InlineData("Agent")]
    [InlineData(" agent ")]
    [InlineData("someone else")]
    public async Task APresetWithAnyAuthor_NeedsTheUsersConfirmation(string authoredBy)
    {
        var (sut, _) = await AgentLayoutAsync(authoredBy);

        var result = await sut.ApplyPresetAsync("layout", launchMissing: true);

        result.Placements!.Single().ErrorCode.Should().Be(nameof(LayoutErrorCode.LaunchNotConfirmed));
        VerifyNothingChanged();
    }

    [Fact]
    public async Task DeletingTheStore_RevokesTheConfirmations_WithoutARestart()
    {
        var (sut, preset) = await AgentLayoutAsync();
        sut.ConfirmLaunches(preset);
        sut.AreLaunchesConfirmed(preset).Should().BeTrue();

        File.Delete(StorePath);

        sut.AreLaunchesConfirmed(preset).Should().BeFalse();
    }

    [Fact]
    public async Task AStoreEditedOnDisk_IsReadAgain()
    {
        var (sut, preset) = await AgentLayoutAsync();
        sut.AreLaunchesConfirmed(preset).Should().BeFalse();

        await File.WriteAllTextAsync(StorePath, LayoutPresetSchema.LaunchFingerprint(preset));

        sut.AreLaunchesConfirmed(preset).Should().BeTrue("a whole key on the last line counts, without a line break after it");
    }

    [Fact]
    public async Task ATamperedStore_ConfirmsOnlyWholeKeys_AndANewKeyStaysOnItsOwnLine()
    {
        var (sut, preset) = await AgentLayoutAsync();
        var key = LayoutPresetSchema.LaunchFingerprint(preset);
        await File.WriteAllTextAsync(StorePath, $"garbage\r\n{key.ToUpperInvariant()}\n{key[..63]}\n{key}x\n  \n{key[..40]}");
        sut.AreLaunchesConfirmed(preset).Should().BeFalse("no line holds the key, whole and lowercase");

        sut.ConfirmLaunches(preset);

        var restarted = CreateSut();
        restarted.AreLaunchesConfirmed(restarted.GetPreset("layout")!).Should().BeTrue("the key was appended on a line of its own");
        (await File.ReadAllLinesAsync(StorePath)).Should().Contain(key);
    }

    [Fact]
    public async Task ALaunchThePlanRefuses_IsTheSameFailure_InADryRunAndInTheApply_AndNothingStarts()
    {
        // Review M6.8 #3: the dry run plans the launch as the launch would; the user is not asked about a refused one
        OpenWindows();
        _processes.Setup(p => p.CheckLaunch(It.IsAny<LaunchRequest>()))
            .Returns(new LaunchRefusal(nameof(LaunchErrorCode.ScriptFile), "run.bat is a script."));
        var sut = await SavedAsync(LayoutPresetSchema.AgentAuthor, Placement("notepad") with { Launch = new LayoutLaunch(Executable: @"C:\Tools\run.bat") });

        var preview = (await sut.ApplyPresetAsync("layout", launchMissing: true, options: new LayoutApplyOptions(DryRun: true))).Placements!.Single();
        var applied = (await sut.ApplyPresetAsync("layout", launchMissing: true)).Placements!.Single();

        preview.Should().BeEquivalentTo(applied);
        applied.Status.Should().Be(LayoutPlacementStatus.Failed);
        applied.ErrorCode.Should().Be(nameof(LayoutErrorCode.LaunchFailed));
        applied.Error.Should().Contain("ScriptFile").And.Contain("run.bat is a script.");
        VerifyNothingChanged();
    }

    [Fact]
    public async Task ADryRun_SaysAHiddenMinimizedWindowWouldNotBeArranged()
    {
        // Review M6.8 #3: SetMinimizedBounds refuses it, since it would show it
        OpenWindows(Code with { State = WindowState.Minimized, IsVisible = false });
        var sut = await SavedAsync(Placement("Code") with { State = WindowState.Minimized });

        var placement = (await sut.ApplyPresetAsync("layout", options: new LayoutApplyOptions(DryRun: true))).Placements!.Single();

        placement.Status.Should().Be(LayoutPlacementStatus.Failed);
        placement.ErrorCode.Should().Be(nameof(LayoutErrorCode.ArrangeFailed));
        placement.Error.Should().Contain("hidden");
    }

    [Fact]
    public async Task AVersion1File_IsByteForByteTheSame_AfterAnApply()
    {
        OpenWindows(Code);
        const string json = "{\n  \"name\": \"old\",\n  \"placements\": [ { \"matcher\": { \"processName\": \"Code\" }, \"monitorIndex\": 0,\n" +
                            "    \"position\": { \"x\": 0, \"y\": 0, \"width\": 0.5, \"height\": 1 }, \"launchCommand\": \"code.exe\" } ]\n}\n";
        Directory.CreateDirectory(_layoutsPath);
        var file = Path.Combine(_layoutsPath, "old.json");
        await File.WriteAllBytesAsync(file, System.Text.Encoding.UTF8.GetBytes(json));
        var before = await File.ReadAllBytesAsync(file);

        (await CreateSut().ApplyPresetAsync("old", launchMissing: true)).Success.Should().BeTrue();

        (await File.ReadAllBytesAsync(file)).Should().Equal(before);
    }

    #endregion
}
