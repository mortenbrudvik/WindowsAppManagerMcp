namespace WindowsAppManagerMcp.Tests.Unit.Services;

/// <summary>
/// save_layout's capture in schema version 2 (roadmap M6.8, owner decision D15): each placement records its monitor's
/// identity, the restore rectangle of a maximized or minimized window, and what launchMissing would start (the program, or a
/// packaged app's appId; never arguments); minimized windows only with includeMinimized. The services are mocks; only the
/// preset files are real (in a temp folder).
/// </summary>
public class LayoutServiceCaptureTests : IDisposable
{
    private readonly string _layoutsPath = Path.Combine(Path.GetTempPath(), $"LayoutServiceCaptureTests_{Guid.NewGuid():N}");
    private readonly Mock<IWindowService> _windows = TestDataFactory.CreateMockWindowService([]);
    private readonly Mock<IMonitorService> _monitors = TestDataFactory.CreateMockMonitorService(
    [
        TestDataFactory.CreateMonitorInfo() with { Id = "m-11111111", FriendlyName = "DELL U2720Q" },
        TestDataFactory.CreateMonitorInfo(1, @"\\.\DISPLAY2", false, 1920, 0, 2560, 1440, 1920, 0, 2560, 1400, 1.25)
    ]);
    private readonly Mock<IProcessService> _processes = TestDataFactory.CreateMockProcessService();
    private readonly Mock<INativeAppsWrapper> _apps = new();
    private readonly Mock<IInstalledAppsService> _installedApps = new();

    private const string CalculatorFamily = "Microsoft.WindowsCalculator_8wekyb3d8bbwe";
    private const string CalculatorAppId = CalculatorFamily + "!App";

    public void Dispose()
    {
        if (Directory.Exists(_layoutsPath))
            Directory.Delete(_layoutsPath, recursive: true);
    }

    private LayoutService CreateSut() =>
        new(_windows.Object, _monitors.Object, _processes.Object, TestDataFactory.CreateMockForegroundService().Object,
            LayoutStorageOptions.ForPath(_layoutsPath), apps: _apps.Object, installedApps: _installedApps.Object);

    private static WindowInfo Window(int handle, string process, int pid, int x, int y, int width, int height,
        WindowState state = WindowState.Normal, int monitor = 0) =>
        TestDataFactory.CreateWindowInfo(handle, $"{process} window", process, pid, x, y, width, height, state, monitorIndex: monitor);

    private void OpenWindows(params WindowInfo[] windows)
    {
        _windows.Setup(w => w.GetAllWindows(true)).Returns(windows);
        _windows.Setup(w => w.GetAllWindows(false)).Returns(windows.Where(w => w.State != WindowState.Minimized).ToList());
    }

    private async Task<WindowPlacement> CaptureOneAsync(bool includeMinimized = false)
    {
        var result = await CreateSut().CaptureCurrentLayoutAsync("captured", includeMinimized: includeMinimized);
        result.Success.Should().BeTrue(result.ErrorMessage);
        return CreateSut().GetPreset("captured")!.Placements.Single();
    }

    [Fact]
    public async Task ANormalWindow_RecordsItsMonitorsIdAndName_AndItsProgram()
    {
        OpenWindows(Window(0x101, "Code", 501, 0, 0, 960, 1040));
        _processes.Setup(p => p.GetProcessPath(501)).Returns(@"C:\Users\me\AppData\Local\Programs\Microsoft VS Code\Code.exe");

        var placement = await CaptureOneAsync();

        placement.Monitor.Should().Be("m-11111111");
        placement.MonitorName.Should().Be("DELL U2720Q");
        placement.MonitorIndex.Should().Be(0);
        placement.Launch.Should().Be(new LayoutLaunch(Executable: @"C:\Users\me\AppData\Local\Programs\Microsoft VS Code\Code.exe"));
        placement.LaunchCommand.Should().Be(@"C:\Users\me\AppData\Local\Programs\Microsoft VS Code\Code.exe", "older servers launch it too");
        placement.NormalPosition.Should().BeNull("a normal window's rectangle is its window rect, which also keeps a snapped one where it is");
        _windows.Verify(w => w.GetNormalBounds(It.IsAny<nint>()), Times.Never);
    }

    [Fact]
    public async Task AMonitorWithoutAnId_IsRecordedByItsDeviceName()
    {
        OpenWindows(Window(0x101, "Code", 501, 2000, 100, 1200, 800, monitor: 1));

        var placement = await CaptureOneAsync();

        placement.Monitor.Should().Be(@"\\.\DISPLAY2");
        placement.MonitorName.Should().BeNull();
        placement.MonitorIndex.Should().Be(1);
    }

    [Fact]
    public async Task AMaximizedWindow_RecordsItsRestoreRectangle()
    {
        OpenWindows(Window(0x101, "Code", 501, -8, -8, 1936, 1056, WindowState.Maximized));
        _windows.Setup(w => w.GetNormalBounds((nint)0x101)).Returns(new WindowRect(192, 104, 960, 520));

        var placement = await CaptureOneAsync();

        placement.State.Should().Be(WindowState.Maximized);
        placement.NormalPosition.Should().Be(new RelativePosition(0.1, 0.1, 0.5, 0.5));
        placement.Position.Should().Be(new RelativePosition(0, 0, 1, 1), "the version 1 field keeps the maximized rectangle");
    }

    [Fact]
    public async Task MinimizedWindows_AreLeftOut_UnlessIncludeMinimized()
    {
        OpenWindows(Window(0x101, "Code", 501, -32000, -32000, 160, 28, WindowState.Minimized));
        _windows.Setup(w => w.GetNormalBounds((nint)0x101)).Returns(new WindowRect(192, 104, 960, 520));

        var without = await CreateSut().CaptureCurrentLayoutAsync("captured");
        without.ErrorCode.Should().Be(nameof(LayoutErrorCode.NoWindowsCaptured));

        var placement = await CaptureOneAsync(includeMinimized: true);
        placement.State.Should().Be(WindowState.Minimized);
        placement.NormalPosition.Should().Be(new RelativePosition(0.1, 0.1, 0.5, 0.5));
        placement.Position.Should().Be(placement.NormalPosition, "an older server places it there, then minimizes it");
    }

    [Fact]
    public async Task AMinimizedWindowWhoseRestoreRectangleCannotBeRead_IsLeftOut()
    {
        // Its window rect is the 160x28 icon far off screen
        OpenWindows(Window(0x101, "Code", 501, -32000, -32000, 160, 28, WindowState.Minimized), Window(0x102, "notepad", 502, 0, 0, 800, 600));
        _windows.Setup(w => w.GetNormalBounds(It.IsAny<nint>())).Returns((WindowRect?)null);

        var placement = await CaptureOneAsync(includeMinimized: true);

        placement.Matcher.ProcessName.Should().Be("notepad");
    }

    [Fact]
    public async Task APackagedApp_IsRecordedByTheAppIdItsWindowCarries_NotByItsProgram()
    {
        OpenWindows(Window(0x101, "CalculatorApp", 501, 0, 0, 400, 600));
        _processes.Setup(p => p.GetProcessPath(501)).Returns(@"C:\Program Files\WindowsApps\Microsoft.WindowsCalculator_11.2_x64__8wekyb3d8bbwe\CalculatorApp.exe");
        _apps.Setup(a => a.GetPackageFamilyName(501)).Returns(CalculatorFamily);
        _apps.Setup(a => a.GetWindowAppUserModelId((nint)0x101)).Returns(CalculatorAppId);

        var placement = await CaptureOneAsync();

        placement.Launch.Should().Be(new LayoutLaunch(AppId: CalculatorAppId), "a package's program cannot be started from its folder");
        placement.LaunchCommand.Should().Be(@"shell:AppsFolder\" + CalculatorAppId);
    }

    [Fact]
    public async Task APackagedAppWhoseWindowCarriesNoAppId_IsFoundAmongTheInstalledApps_WhenItHasOneApp()
    {
        OpenWindows(Window(0x101, "CalculatorApp", 501, 0, 0, 400, 600));
        _apps.Setup(a => a.GetPackageFamilyName(501)).Returns(CalculatorFamily);
        _installedApps.Setup(i => i.List(It.IsAny<string?>(), It.IsAny<int>())).Returns(new InstalledAppsListing(
            [new InstalledApp("Calculator", CalculatorAppId, true, null)], 1, false, false));

        (await CaptureOneAsync()).Launch.Should().Be(new LayoutLaunch(AppId: CalculatorAppId));
    }

    [Fact]
    public async Task APackagedAppWithSeveralApps_RecordsNoLaunch_RatherThanGuess()
    {
        OpenWindows(Window(0x101, "PhotosApp", 501, 0, 0, 400, 600));
        _apps.Setup(a => a.GetPackageFamilyName(501)).Returns("Microsoft.Windows.Photos_8wekyb3d8bbwe");
        _installedApps.Setup(i => i.List(It.IsAny<string?>(), It.IsAny<int>())).Returns(new InstalledAppsListing(
        [
            new InstalledApp("Photos", "Microsoft.Windows.Photos_8wekyb3d8bbwe!App", true, null),
            new InstalledApp("Video Editor", "Microsoft.Windows.Photos_8wekyb3d8bbwe!SecondaryEntry", true, null)
        ], 2, false, false));

        var placement = await CaptureOneAsync();

        placement.Launch.Should().BeNull();
        placement.LaunchCommand.Should().BeNull();
    }

    [Fact]
    public async Task AWindowThePolicyDenies_RecordsNoLaunch()
    {
        OpenWindows(Window(0x101, "KeePass", 501, 0, 0, 800, 600));
        _processes.Setup(p => p.GetProcessPath(501)).Returns(@"C:\Program Files\KeePass\KeePass.exe");
        _windows.Setup(w => w.EvaluateTargetPolicy((nint)0x101)).Returns(TargetPolicyVerdict.Deny("KeePass", "KeePass is on the deny list"));

        var placement = await CaptureOneAsync();

        placement.Launch.Should().BeNull();
        placement.LaunchCommand.Should().BeNull();
    }

    [Fact]
    public async Task ACapturedPreset_IsVersion2_AndNotAgentAuthored()
    {
        OpenWindows(Window(0x101, "Code", 501, 0, 0, 960, 1040));

        await CreateSut().CaptureCurrentLayoutAsync("captured");

        var json = await File.ReadAllTextAsync(Path.Combine(_layoutsPath, "captured.json"));
        json.Should().Contain("\"version\": 2");
        CreateSut().GetPreset("captured")!.AuthoredBy.Should().BeNull();
    }
}
