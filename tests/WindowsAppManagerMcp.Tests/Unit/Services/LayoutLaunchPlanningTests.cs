namespace WindowsAppManagerMcp.Tests.Unit.Services;

/// <summary>
/// A layout's launches through the real launch planning (review M6.8 #3): apply_layout's launchMissing plans each target as
/// launch_application does (ProcessService.PlanLaunch: validation, the resolver, the script and interpreter refusals), its dry
/// run gives the same answer, and nothing is started. The process wrapper is a strict mock with no setups, so a start would
/// throw; the window service is a mock and the resolver reads only the file system.
/// </summary>
public class LayoutLaunchPlanningTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"LayoutLaunchPlanningTests_{Guid.NewGuid():N}");
    private readonly Mock<IWindowService> _windows = TestDataFactory.CreateMockWindowService([]);
    private readonly Mock<INativeProcessWrapper> _processes = new(MockBehavior.Strict);

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    private LayoutService CreateSut()
    {
        var processService = new ProcessService(
            _windows.Object,
            new InputValidationService(ProtocolAllowlist.BuiltIn),
            TestDataFactory.CreateMockBrowserDetectionService().Object,
            new ExecutableResolver(),
            _processes.Object,
            windowWait: new Mock<IWindowWaitService>(MockBehavior.Strict).Object);
        return new LayoutService(_windows.Object, TestDataFactory.CreateMockMonitorService().Object, processService,
            TestDataFactory.CreateMockForegroundService().Object, Path.Combine(_root, "layouts"),
            new Mock<ILaunchPlacementService>(MockBehavior.Strict).Object);
    }

    /// <summary>A script on disk, which the resolver finds as one.</summary>
    private string Script()
    {
        Directory.CreateDirectory(_root);
        var path = Path.Combine(_root, "run.bat");
        File.WriteAllText(path, "@echo off");
        return path;
    }

    public static TheoryData<string, LayoutLaunch> HostileLaunches() => new()
    {
        { "an interpreter with a command", new LayoutLaunch(Executable: "cmd.exe /c x") },
        { "a UNC path", new LayoutLaunch(Executable: @"\\server\share\app.exe") },
        { "a shell: folder", new LayoutLaunch(Executable: "shell:Foo") },
        { "an appId that is a shell: folder", new LayoutLaunch(AppId: @"shell:Foo") },
        { "an appId that climbs out", new LayoutLaunch(AppId: @"..\..\Windows\System32\cmd.exe") },
    };

    [Theory]
    [MemberData(nameof(HostileLaunches))]
    public async Task AHostileLaunch_IsRefusedByThePlan_TheSameInADryRunAndInTheApply_AndNothingStarts(string why, LayoutLaunch launch)
    {
        await ShouldBeRefusedAsync(why, launch);
    }

    [Fact]
    public async Task AScript_IsRefusedByThePlan_TheSameInADryRunAndInTheApply_AndNothingStarts()
    {
        await ShouldBeRefusedAsync("a batch file", new LayoutLaunch(Executable: Script()));
    }

    private async Task ShouldBeRefusedAsync(string why, LayoutLaunch launch)
    {
        // Arrange - a hand-written preset (no author), so nobody is asked: only the plan stands between it and a start
        var sut = CreateSut();
        var placement = new WindowPlacement(new WindowMatcher(ProcessName: "app"), 0, new RelativePosition(0, 0, 0.5, 1)) { Launch = launch };
        (await sut.SavePresetAsync(new LayoutPreset("hostile", null, [placement]))).Success.Should().BeTrue();

        // Act
        var preview = (await sut.ApplyPresetAsync("hostile", launchMissing: true, options: new LayoutApplyOptions(DryRun: true))).Placements!.Single();
        var applied = (await sut.ApplyPresetAsync("hostile", launchMissing: true)).Placements!.Single();

        // Assert
        applied.Status.Should().Be(LayoutPlacementStatus.Failed, why);
        applied.ErrorCode.Should().Be(nameof(LayoutErrorCode.LaunchFailed), why);
        applied.Launched.Should().BeNull(why);
        preview.Should().BeEquivalentTo(applied, why);
        _processes.Invocations.Should().BeEmpty("nothing is started for {0}", why);
    }
}
