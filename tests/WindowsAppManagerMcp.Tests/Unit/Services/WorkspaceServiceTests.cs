namespace WindowsAppManagerMcp.Tests.Unit.Services;

/// <summary>
/// Workspaces (roadmap M7.5): save_workspace records File Explorer folders and browser pages (every tab with a debug port,
/// else the shown page) cleaned of secrets, and restore_workspace opens only what is missing, refuses agent-written presets,
/// and then applies the preset with launchMissing. Every layer is a mock: nothing is captured, opened or launched.
/// </summary>
public class WorkspaceServiceTests : IDisposable
{
    private const nint Explorer = 0x100, Edge = 0x200, Notepad = 0x300;

    private readonly Mock<ILayoutService> _layouts = new();
    private readonly Mock<IWindowService> _windows = new();
    private readonly Mock<INativeWindowWrapper> _native = new();
    private readonly Mock<IBrowserReadingService> _browsers = new();
    private readonly Mock<INativeShellWindowsWrapper> _shell = new();
    private readonly Mock<IProcessService> _processes = new();
    private readonly Mock<ICdpTabService> _tabs = new();
    private readonly Mock<ITargetPolicy> _policy = new();
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "WamWorkspaceTests-" + Guid.NewGuid().ToString("N"));
    private LayoutPreset? _saved;

    public WorkspaceServiceTests()
    {
        Directory.CreateDirectory(_folder);
        _windows.Setup(w => w.GetAllWindows(It.IsAny<bool>())).Returns(
        [
            Window(Explorer, "Reports", "explorer"),
            Window(Edge, "Inbox - Mail - Microsoft Edge", "msedge"),
            Window(Notepad, "notes.txt - Notepad", "notepad")
        ]);
        _shell.Setup(s => s.GetExplorerFolders()).Returns([new ExplorerFolder(Explorer, _folder)]);
        _browsers.Setup(b => b.IsBrowserWindow(Edge)).Returns(true);
        _browsers.Setup(b => b.GetBrowserUrlAsync(Edge, It.IsAny<CdpOptions?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new BrowserUrlResult(true, "https://mail.example.com/inbox?folder=1&access_token=abc", "edge", "Inbox", null, BrowserErrorCode.None));
        _policy.Setup(p => p.Evaluate(It.IsAny<IReadOnlyCollection<string?>>())).Returns(TargetPolicyVerdict.Allow);
        _layouts.Setup(l => l.CaptureCurrentLayoutAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string[]?>(), It.IsAny<string[]?>(),
                It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new LayoutSaveResult(true, Preset()));
        _layouts.Setup(l => l.GetPreset("morning")).Returns(() => _saved ?? Preset());
        _layouts.Setup(l => l.SavePresetAsync(It.IsAny<LayoutPreset>(), It.IsAny<CancellationToken>()))
            .Callback<LayoutPreset, CancellationToken>((preset, _) => _saved = preset)
            .ReturnsAsync((LayoutPreset preset, CancellationToken _) => new LayoutSaveResult(true, preset, Replaced: true));
        _layouts.Setup(l => l.ApplyPresetAsync(It.IsAny<string>(), It.IsAny<LayoutMatchBy>(), It.IsAny<bool>(), It.IsAny<LayoutApplyOptions?>(),
                It.IsAny<IProgress<OperationProgress>?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new LayoutApplyResult(true, "morning", WindowsArranged: 3));
        _processes.Setup(p => p.LaunchApplicationAsync(It.IsAny<string>(), It.IsAny<string[]?>(), It.IsAny<string?>(), It.IsAny<bool>(),
                It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new LaunchResult(true, ProcessId: 4242, WindowHandle: 0x900));
    }

    public void Dispose() => Directory.Delete(_folder, recursive: true);

    private static WindowInfo Window(nint handle, string title, string process) =>
        new(handle, title, process, 1000 + (int)handle, new WindowRect(0, 0, 800, 600), WindowState.Normal, true, 0);

    private static WindowPlacement Placement(string process, string title, string? executable = null, WorkspaceContent? content = null) =>
        new(new WindowMatcher(ProcessName: process) { TitlePattern = title, ExactProcessName = true, Ordinal = 0 }, 0,
            new RelativePosition(0, 0, 0.5, 0.5))
        {
            Launch = executable == null ? null : new LayoutLaunch(executable),
            Content = content
        };

    private static LayoutPreset Preset(params WindowPlacement[] placements) => new("morning", null, placements.Length > 0
        ? [.. placements]
        :
        [
            Placement("explorer", "Reports", @"C:\Windows\explorer.exe"),
            Placement("msedge", "Inbox - Mail - Microsoft Edge", @"C:\Program Files\Microsoft\Edge\Application\msedge.exe"),
            Placement("notepad", "notes.txt - Notepad", @"C:\Windows\notepad.exe")
        ]) { Version = 2 };

    private WorkspaceService CreateSut() =>
        new(_layouts.Object, _windows.Object, _native.Object, _browsers.Object, _shell.Object, _processes.Object, _tabs.Object, _policy.Object);

    private void Saved(LayoutPreset preset) => _saved = preset;

    // ---- Save ----

    [Fact]
    public async Task Save_RecordsTheFolder_AndTheShownPage_CleanedOfSecrets()
    {
        // Act
        var result = await CreateSut().SaveAsync("morning", null, null, null, includeMinimized: false);

        // Assert
        result.Should().BeEquivalentTo(new WorkspaceSaveResult(true, "morning", 3, Folders: 1, BrowserWindows: 1, Urls: 1, PartialBrowsers: 1),
            o => o.Excluding(r => r.Warnings));
        result.Warnings.Should().ContainSingle().Which.Should().Contain("no debug port");
        _saved!.Placements[0].Content.Should().Be(new WorkspaceContent(Folder: _folder));
        _saved.Placements[1].Content!.Urls.Should().Equal("https://mail.example.com/inbox?folder=1");
        _saved.Placements[1].Content!.UrlsPartial.Should().BeTrue();
        _saved.Placements[2].Content.Should().BeNull("Notepad shows no folder or page");
    }

    [Fact]
    public async Task Save_WithADebugPort_RecordsEveryTabOfTheWindow()
    {
        // Arrange - the browser's port lists three tabs, two of them in this window
        _browsers.Setup(b => b.GetBrowserUrlAsync(Edge, It.IsAny<CdpOptions?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new BrowserUrlResult(true, "https://mail.example.com/inbox", "edge", "Inbox", null, BrowserErrorCode.None, CdpPort: 9222));
        CdpTab Tab(string url, bool inWindow) => new(new CdpTarget(url, "page", "t", url, null, 9222), 1, inWindow);
        _tabs.Setup(t => t.ListTabsAsync(9222, 1000 + (int)Edge, It.IsAny<CdpWindow?>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(CdpResult<CdpTabListing>.Ok(new CdpTabListing(
                [Tab("https://mail.example.com/inbox", true), Tab("https://docs.example.com/a?code=xyz", true), Tab("https://other.example.com/", false)],
                WindowBound: true, null, null)));

        // Act
        var result = await CreateSut().SaveAsync("morning", null, null, null, includeMinimized: false);

        // Assert
        result.PartialBrowsers.Should().Be(0);
        _saved!.Placements[1].Content!.Urls.Should().Equal("https://mail.example.com/inbox", "https://docs.example.com/a");
        _saved.Placements[1].Content!.UrlsPartial.Should().BeNull();
    }

    [Fact]
    public async Task Save_AWindowThePolicyDenies_KeepsNoContent()
    {
        _policy.Setup(p => p.Evaluate(It.Is<IReadOnlyCollection<string?>>(n => n.Contains("msedge"))))
            .Returns(new TargetPolicyVerdict(false, "msedge", "denied"));

        await CreateSut().SaveAsync("morning", null, null, null, includeMinimized: false);

        _saved!.Placements[1].Content.Should().BeNull();
        _browsers.Verify(b => b.GetBrowserUrlAsync(Edge, It.IsAny<CdpOptions?>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Save_WhenTheCaptureFails_SavesNothingMore()
    {
        _layouts.Setup(l => l.CaptureCurrentLayoutAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string[]?>(), It.IsAny<string[]?>(),
                It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new LayoutSaveResult(false, null, ErrorMessage: "No windows", ErrorCode: nameof(LayoutErrorCode.NoWindowsCaptured)));

        var result = await CreateSut().SaveAsync("morning", null, null, null, includeMinimized: false);

        result.Success.Should().BeFalse();
        result.ErrorCode.Should().Be(nameof(LayoutErrorCode.NoWindowsCaptured));
        _layouts.Verify(l => l.SavePresetAsync(It.IsAny<LayoutPreset>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    // ---- Restore ----

    [Fact]
    public async Task Restore_AnAgentWrittenPreset_IsRefused_AndNothingOpens()
    {
        Saved(Preset() with { AuthoredBy = "agent" });

        var result = await CreateSut().RestoreAsync("morning", dryRun: false);

        result.ErrorCode.Should().Be(nameof(LayoutErrorCode.LaunchNotConfirmed));
        _processes.VerifyNoOtherCalls();
        _layouts.Verify(l => l.ApplyPresetAsync(It.IsAny<string>(), It.IsAny<LayoutMatchBy>(), It.IsAny<bool>(), It.IsAny<LayoutApplyOptions?>(),
            It.IsAny<IProgress<OperationProgress>?>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Restore_AMissingWorkspace_IsPresetNotFound()
    {
        var result = await CreateSut().RestoreAsync("evening", dryRun: false);

        result.ErrorCode.Should().Be(nameof(LayoutErrorCode.PresetNotFound));
    }

    [Fact]
    public async Task Restore_OpensOnlyWhatNoWindowShows_ThenAppliesTheLayoutWithLaunchMissing()
    {
        // Arrange - the folder is open already; the mail window is gone; a second folder no longer exists
        var gone = Path.Combine(_folder, "gone");
        Saved(Preset(
            Placement("explorer", "Reports", content: new WorkspaceContent(Folder: _folder)),
            Placement("explorer", "Old", content: new WorkspaceContent(Folder: gone)),
            Placement("msedge", "Mail - Microsoft Edge", @"C:\Edge\msedge.exe",
                new WorkspaceContent(Urls: ["https://mail.example.com/inbox?session=s1&view=all", "file:///C:/secret.txt"]))));

        // Act
        var result = await CreateSut().RestoreAsync("morning", dryRun: false);

        // Assert - opened in a new window with the cleaned pages only, the rest as found
        result.Opened!.Select(o => o.Status).Should().Equal("alreadyOpen", "missing", "opened");
        _processes.Verify(p => p.LaunchApplicationAsync(@"C:\Edge\msedge.exe",
            It.Is<string[]?>(a => a!.SequenceEqual(new[] { "--new-window", "https://mail.example.com/inbox?view=all" })),
            null, true, WorkspaceService.OpenWaitMs, It.IsAny<CancellationToken>()), Times.Once);
        _processes.Verify(p => p.LaunchApplicationAsync("explorer.exe", It.IsAny<string[]?>(), It.IsAny<string?>(), It.IsAny<bool>(),
            It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Never);
        _layouts.Verify(l => l.ApplyPresetAsync("morning", LayoutMatchBy.ProcessAndTitle, true,
            It.Is<LayoutApplyOptions?>(o => o!.DryRun == false), It.IsAny<IProgress<OperationProgress>?>(), It.IsAny<CancellationToken>()), Times.Once);
        result.Status.Should().Be("partial", "a folder is missing");
        result.Success.Should().BeFalse();
    }

    [Fact]
    public async Task Restore_AFolderThatNoWindowShows_IsOpenedInFileExplorer()
    {
        _shell.Setup(s => s.GetExplorerFolders()).Returns([]);
        Saved(Preset(Placement("explorer", "Reports", content: new WorkspaceContent(Folder: _folder))));

        var result = await CreateSut().RestoreAsync("morning", dryRun: false);

        result.Opened!.Single().Should().BeEquivalentTo(new { Status = "opened", Kind = "folder", WindowHandle = (long?)0x900 });
        _processes.Verify(p => p.LaunchApplicationAsync("explorer.exe", It.Is<string[]?>(a => a!.Single() == _folder), null, true,
            WorkspaceService.OpenWaitMs, It.IsAny<CancellationToken>()), Times.Once);
        result.Status.Should().Be("restored");
    }

    [Fact]
    public async Task Restore_ADryRun_OpensAndStartsNothing()
    {
        _shell.Setup(s => s.GetExplorerFolders()).Returns([]);
        Saved(Preset(Placement("explorer", "Reports", content: new WorkspaceContent(Folder: _folder))));

        var result = await CreateSut().RestoreAsync("morning", dryRun: true);

        result.Status.Should().Be("dryRun");
        result.Opened!.Single().Status.Should().Be("wouldOpen");
        _processes.VerifyNoOtherCalls();
        _layouts.Verify(l => l.ApplyPresetAsync("morning", It.IsAny<LayoutMatchBy>(), true, It.Is<LayoutApplyOptions?>(o => o!.DryRun),
            It.IsAny<IProgress<OperationProgress>?>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Theory]
    [InlineData("Inbox - [InPrivate] - Microsoft Edge")]
    [InlineData("Inbox - Google Chrome (Incognito)")]
    [InlineData("Inbox — Mozilla Firefox Private Browsing")]
    public async Task Save_APrivateWindow_KeepsNoPages(string title)
    {
        // Arrange - its pages must not reach the file, nor come back in a window that is not private
        _windows.Setup(w => w.GetAllWindows(It.IsAny<bool>())).Returns([Window(Edge, title, "msedge")]);
        _layouts.Setup(l => l.CaptureCurrentLayoutAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string[]?>(), It.IsAny<string[]?>(),
                It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new LayoutSaveResult(true, Preset(Placement("msedge", title))));
        Saved(Preset(Placement("msedge", title)));

        // Act
        var result = await CreateSut().SaveAsync("morning", null, null, null, includeMinimized: false);

        // Assert
        result.Urls.Should().Be(0);
        result.Warnings.Should().ContainSingle().Which.Should().Contain("private browsing window");
        _browsers.Verify(b => b.GetBrowserUrlAsync(It.IsAny<nint>(), It.IsAny<CdpOptions?>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Save_WhenFileExplorerDoesNotAnswer_KeepsNoFolder_AndSaysSo()
    {
        _shell.Setup(s => s.GetExplorerFolders()).Returns((IReadOnlyList<ExplorerFolder>?)null);

        var result = await CreateSut().SaveAsync("morning", null, null, null, includeMinimized: false);

        result.Folders.Should().Be(0);
        result.Warnings.Should().Contain(w => w.Contains("File Explorer did not answer"));
    }

    [Fact]
    public async Task Restore_WhenFileExplorerDoesNotAnswer_OpensNoFolder_RatherThanASecondWindow()
    {
        _shell.Setup(s => s.GetExplorerFolders()).Returns((IReadOnlyList<ExplorerFolder>?)null);
        Saved(Preset(Placement("explorer", "Reports", content: new WorkspaceContent(Folder: _folder))));

        var result = await CreateSut().RestoreAsync("morning", dryRun: false);

        result.Opened!.Single().Should().BeEquivalentTo(new { Status = "failed", Kind = "folder" });
        _processes.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task Restore_ABrowserWindowShowingOneOfThePages_IsAlreadyOpen_SoASecondRestoreOpensNoSecondWindow()
    {
        // Arrange - an earlier restore opened the window: it shows the first page, under that page's title
        _windows.Setup(w => w.GetAllWindows(It.IsAny<bool>())).Returns([Window(Edge, "Docs - Microsoft Edge", "msedge")]);
        _browsers.Setup(b => b.GetBrowserUrlAsync(Edge, It.IsAny<CdpOptions?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new BrowserUrlResult(true, "https://docs.example.com/a", "edge", "Docs", null, BrowserErrorCode.None));
        Saved(Preset(Placement("msedge", "Inbox - Microsoft Edge", @"C:\Edge\msedge.exe",
            new WorkspaceContent(Urls: ["https://docs.example.com/a", "https://mail.example.com/"]))));

        // Act
        var result = await CreateSut().RestoreAsync("morning", dryRun: false);

        // Assert
        result.Opened!.Single().Status.Should().Be("alreadyOpen");
        _processes.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task Restore_AFolderThatIsNotAFullPath_IsNotOpened()
    {
        _shell.Setup(s => s.GetExplorerFolders()).Returns([]);
        Saved(Preset(Placement("explorer", "Here", content: new WorkspaceContent(Folder: "."))));

        var result = await CreateSut().RestoreAsync("morning", dryRun: false);

        result.Opened!.Single().Status.Should().Be("failed");
        _processes.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task Restore_APresetWhoseProgramIsNotTheBrowser_StartsNothing()
    {
        // Arrange - a file edited by hand: the window was msedge, the program is something else
        Saved(Preset(Placement("msedge", "Mail - Microsoft Edge", @"C:\Tools\other.exe",
            new WorkspaceContent(Urls: ["https://mail.example.com/"]))));

        // Act
        var result = await CreateSut().RestoreAsync("morning", dryRun: false);

        // Assert
        result.Opened!.Single().Status.Should().Be("unsupported");
        _processes.Verify(p => p.LaunchApplicationAsync(@"C:\Tools\other.exe", It.IsAny<string[]?>(), It.IsAny<string?>(), It.IsAny<bool>(),
            It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Theory]
    [InlineData("msedge", new[] { "--new-window", "https://a.example/" })]
    [InlineData("chrome", new[] { "--new-window", "https://a.example/" })]
    [InlineData("firefox", new[] { "-new-window", "https://a.example/" })]
    [InlineData("iexplore", null)]
    public void NewWindowArguments_OpenThePagesInANewWindow_ForTheBrowsersItKnows(string browser, string[]? expected) =>
        WorkspaceService.NewWindowArguments(browser, ["https://a.example/"]).Should().BeEquivalentTo(expected, o => o.WithStrictOrdering());

    // ---- Pages ----

    [Theory]
    [InlineData("https://example.com/a?b=1", "https://example.com/a?b=1")]
    [InlineData("https://user:pw@example.com/a", "https://example.com/a")]
    [InlineData("https://example.com/cb?code=abc&state=x&lang=no", "https://example.com/cb?lang=no")]
    [InlineData("https://example.com/a?api_key=1&id_token=2&page=3", "https://example.com/a?page=3")]
    [InlineData("https://example.com/#access_token=abc&scope=read", "https://example.com/#scope=read")]
    [InlineData("https://example.com/doc#section-2", "https://example.com/doc#section-2")]
    [InlineData("https://example.com/a?session_id=1&authToken=2&csrfToken=3&X-Amz-Credential=4&q=go", "https://example.com/a?q=go")]
    [InlineData("https://app.example.com/#/callback?token=abc&tab=2", "https://app.example.com/#/callback?tab=2")]
    [InlineData("https://app.example.com/#/cb?code=abc", "https://app.example.com/#/cb")]
    [InlineData("https://example.com/a?zipcode=1234", "https://example.com/a?zipcode=1234")]
    [InlineData("http://localhost:8080/", "http://localhost:8080/")]
    public void Urls_AreKeptWithoutCredentialsOrSecretLookingValues(string url, string expected) =>
        WorkspaceUrls.Clean(url).Should().Be(expected);

    [Theory]
    [InlineData("file:///C:/Users/user/secret.txt")]
    [InlineData("javascript:alert(1)")]
    [InlineData("edge://settings")]
    [InlineData("about:blank")]
    [InlineData("not a url")]
    [InlineData(null)]
    public void OnlyHttpAndHttpsPages_AreKept(string? url) => WorkspaceUrls.Clean(url).Should().BeNull();

    [Fact]
    public void AnOverlongAddress_IsNotKept() =>
        WorkspaceUrls.Clean("https://example.com/" + new string('a', WorkspaceUrls.MaxUrlLength)).Should().BeNull();
}
