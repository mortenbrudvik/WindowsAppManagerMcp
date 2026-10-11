namespace WindowsAppManagerMcp.Tests.Unit.Services;

/// <summary>
/// Unit tests for LaunchWindowOwner (roadmap M2.6): which windows belong to a launch, whichever process owns them. The
/// process list, image paths and start times are a fake; nothing is started.
/// </summary>
public class LaunchWindowOwnerTests
{
    private const int LaunchedProcessId = 1000;
    private static readonly DateTime LaunchedAt = new(2026, 10, 11, 12, 0, 0, DateTimeKind.Utc);

    private readonly Dictionary<int, (int ParentProcessId, string ImageName)> _processList = [];
    private readonly Dictionary<int, DateTime> _startTimes = [];
    private readonly Dictionary<int, string> _imagePaths = [];
    private readonly Mock<INativeProcessWrapper> _processes = new(MockBehavior.Strict);

    public LaunchWindowOwnerTests()
    {
        _processes.Setup(p => p.GetProcessList()).Returns(() => _processList);
        _processes.Setup(p => p.GetProcessStartTime(It.IsAny<int>())).Returns<int>(id => _startTimes.TryGetValue(id, out var t) ? t : null);
        _processes.Setup(p => p.GetProcessImagePath(It.IsAny<int>())).Returns<int>(id => _imagePaths.GetValueOrDefault(id));
    }

    private void AddProcess(int processId, int parentProcessId, string imageName, DateTime? startedAt, string? imagePath = null)
    {
        _processList[processId] = (parentProcessId, imageName);
        if (startedAt is { } started)
            _startTimes[processId] = started;
        if (imagePath != null)
            _imagePaths[processId] = imagePath;
    }

    private LaunchWindowOwner OwnerOf(string fileName, LaunchTargetKind kind, int? launchedProcessId = LaunchedProcessId, LaunchWindowHint? hint = null) =>
        LaunchWindowOwner.For(_processes.Object, launchedProcessId, LaunchedAt, new ResolvedLaunchTarget(fileName, kind), hint);

    private static WindowCandidate Window(int processId, string title = "A window", int? hostProcessId = null) =>
        new(0x100, processId, hostProcessId, title);

    [Fact]
    public void AWindowOfTheStartedProcess_Belongs()
    {
        // Act & Assert
        OwnerOf(@"C:\Tools\app.exe", LaunchTargetKind.NativeExecutable).Matches(Window(LaunchedProcessId)).Should().BeTrue();
    }

    [Fact]
    public void AWindowOfAProcessTheLauncherStarted_Belongs_EvenAfterTheLauncherExited()
    {
        // Arrange - the launcher (1000) is gone; its child 2000 and grandchild 3000 started after the launch
        AddProcess(2000, LaunchedProcessId, "helper.exe", LaunchedAt.AddMilliseconds(50));
        AddProcess(3000, 2000, "real-app.exe", LaunchedAt.AddMilliseconds(80));

        // Act
        var owner = OwnerOf(@"C:\Tools\launcher.exe", LaunchTargetKind.NativeExecutable);

        // Assert
        owner.Matches(Window(2000)).Should().BeTrue();
        owner.Matches(Window(3000)).Should().BeTrue();
    }

    [Fact]
    public void AProcessOlderThanTheLaunch_IsNoChildOfIt_EvenWhenItsParentIdMatches()
    {
        // Arrange - 2000 names 1000 as its parent, but started an hour before the launch: its parent was another process
        // that had the PID before
        AddProcess(2000, LaunchedProcessId, "other.exe", LaunchedAt.AddHours(-1));

        // Act & Assert
        OwnerOf(@"C:\Tools\launcher.exe", LaunchTargetKind.NativeExecutable).Matches(Window(2000)).Should().BeFalse();
    }

    [Fact]
    public void AProcessWhoseStartTimeCannotBeRead_IsNoChildOfIt()
    {
        // Arrange
        AddProcess(2000, LaunchedProcessId, "other.exe", startedAt: null);

        // Act & Assert
        OwnerOf(@"C:\Tools\launcher.exe", LaunchTargetKind.NativeExecutable).Matches(Window(2000)).Should().BeFalse();
    }

    [Fact]
    public void AProcessWithTheSameImagePath_Belongs_ItIsTheRunningInstanceTheLauncherHandedOffTo()
    {
        // Arrange - Chrome was running before the launch; the started chrome.exe passed the URL to it and exited
        const string chrome = @"C:\Program Files\Google\Chrome\Application\chrome.exe";
        AddProcess(5000, 4, "chrome.exe", LaunchedAt.AddHours(-2), chrome);

        // Act & Assert
        OwnerOf(chrome, LaunchTargetKind.NativeExecutable).Matches(Window(5000)).Should().BeTrue();
    }

    [Theory]
    [InlineData(@"C:\Windows\System32\calc.exe", LaunchTargetKind.NativeExecutable, "CalculatorApp.exe")]
    [InlineData(@"C:\Users\me\AppData\Local\Microsoft\WindowsApps\wt.exe", LaunchTargetKind.NativeExecutable, "WindowsTerminal.exe")]
    [InlineData(@"C:\Users\me\AppData\Local\Programs\Microsoft VS Code\bin\code.cmd", LaunchTargetKind.BatchFile, "Code.exe")]
    [InlineData(@"C:\Windows\System32\notepad.exe", LaunchTargetKind.NativeExecutable, "Notepad.exe")]
    [InlineData("ms-settings:display", LaunchTargetKind.Other, "SystemSettings.exe")]
    [InlineData("https://example.com/", LaunchTargetKind.Other, "msedge.exe")]
    [InlineData("http://localhost:8080/", LaunchTargetKind.Other, "firefox.exe")]
    public void AProcessOfTheHandoffMapOrWithTheProgramsName_Belongs(string fileName, LaunchTargetKind kind, string imageName)
    {
        // Arrange - an older process with another path (Win11 Notepad runs from WindowsApps, for example)
        AddProcess(6000, 4, imageName, LaunchedAt.AddHours(-1), @"C:\Program Files\WindowsApps\pkg\" + imageName);

        // Act & Assert
        OwnerOf(fileName, kind).Matches(Window(6000)).Should().BeTrue();
    }

    [Fact]
    public void ADocumentsName_IsNoProcessName()
    {
        // Arrange - a process named like the document
        AddProcess(6000, 4, "report.exe", LaunchedAt.AddHours(-1));

        // Act & Assert - a document's app is not known without a hint
        OwnerOf(@"C:\Docs\report.pdf", LaunchTargetKind.Other, launchedProcessId: null).Matches(Window(6000)).Should().BeFalse();
    }

    [Fact]
    public void AnUnrelatedProcess_DoesNotBelong()
    {
        // Arrange
        AddProcess(7000, 4, "explorer.exe", LaunchedAt.AddHours(-1), @"C:\Windows\explorer.exe");

        // Act & Assert
        OwnerOf(@"C:\Tools\app.exe", LaunchTargetKind.NativeExecutable).Matches(Window(7000)).Should().BeFalse();
    }

    [Fact]
    public void AHint_MatchesWhenEveryFieldItSetsMatches()
    {
        // Arrange - Word opens the document; the started process is unknown (a handoff)
        AddProcess(8000, 4, "WINWORD.EXE", LaunchedAt.AddHours(-1));
        var owner = OwnerOf(@"C:\Docs\report.docx", LaunchTargetKind.Other, launchedProcessId: null,
            hint: new LaunchWindowHint(ProcessName: "winword", TitleContains: "report"));

        // Act & Assert
        owner.Matches(Window(8000, "report.docx - Word")).Should().BeTrue();
        owner.Matches(Window(8000, "Document1 - Word")).Should().BeFalse("the title does not match");
    }

    [Fact]
    public void AHintsProcessName_AlsoMatchesTheFrameHostOfAUwpWindow()
    {
        // Arrange
        AddProcess(9000, 4, "ApplicationFrameHost.exe", LaunchedAt.AddHours(-1));
        AddProcess(9001, 4, "SomeStoreApp.exe", LaunchedAt.AddHours(-1));
        var owner = OwnerOf(@"shell:AppsFolder\Vendor.App_abc!App", LaunchTargetKind.Other, launchedProcessId: null,
            hint: new LaunchWindowHint(ProcessName: "SomeStoreApp"));

        // Act & Assert
        owner.Matches(Window(9001, "Some Store App", hostProcessId: 9000)).Should().BeTrue();
    }

    [Fact]
    public void AHintWithATitleOnly_MatchesAnyProcess()
    {
        // Act
        var owner = OwnerOf("https://example.com/", LaunchTargetKind.Other, launchedProcessId: null,
            hint: new LaunchWindowHint(TitleContains: "Example Domain"));

        // Assert
        owner.Matches(Window(1234, "Example Domain - Browser")).Should().BeTrue();
        owner.Matches(Window(1234, "New Tab - Browser")).Should().BeFalse();
    }

    [Fact]
    public void AnEmptyHint_MatchesNothing()
    {
        // Act
        var owner = OwnerOf("https://example.com/", LaunchTargetKind.Other, launchedProcessId: null, hint: new LaunchWindowHint("  ", null));

        // Assert
        owner.Matches(Window(1234, "Example Domain")).Should().BeFalse();
    }

    [Fact]
    public void TheProcessChecks_AreMadeOncePerProcess()
    {
        // Arrange
        AddProcess(7000, 4, "explorer.exe", LaunchedAt.AddHours(-1), @"C:\Windows\explorer.exe");
        var owner = OwnerOf(@"C:\Tools\app.exe", LaunchTargetKind.NativeExecutable);

        // Act
        owner.Matches(Window(7000));
        owner.Matches(Window(7000));

        // Assert
        _processes.Verify(p => p.GetProcessImagePath(7000), Times.Once);
    }
}
