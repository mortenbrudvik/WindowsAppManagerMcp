using System.Text.Json;

namespace WindowsAppManagerMcp.Tests.Unit.Services;

/// <summary>
/// Tests for how LayoutService prepares and reads its storage folder: lazy creation,
/// first-run migration and seeding, unusable locations, and picking up changes made on disk.
/// Every test works in its own temp folder.
/// </summary>
public class LayoutServiceStorageTests : IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private readonly string _root = Path.Combine(Path.GetTempPath(), $"LayoutServiceStorageTests_{Guid.NewGuid():N}");
    private readonly string _layoutsPath;
    private readonly string _legacyPath;
    private readonly Mock<IWindowService> _mockWindowService = TestDataFactory.CreateMockWindowService();
    private readonly Mock<IMonitorService> _mockMonitorService = TestDataFactory.CreateMockMonitorService();
    private readonly Mock<IProcessService> _mockProcessService = TestDataFactory.CreateMockProcessService();

    public LayoutServiceStorageTests()
    {
        _layoutsPath = Path.Combine(_root, "LocalAppData", "WindowsAppManagerMcp", "layouts");
        _legacyPath = Path.Combine(_root, "old-install", "layouts");
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            foreach (var file in Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories))
            {
                File.SetAttributes(file, FileAttributes.Normal);
            }
            Directory.Delete(_root, recursive: true);
        }
    }

    private LayoutService CreateSut(LayoutStorageOptions options, ILayoutFolderAccess? folderAccess = null) => new(
        _mockWindowService.Object,
        _mockMonitorService.Object,
        _mockProcessService.Object,
        TestDataFactory.CreateMockForegroundService().Object,
        options,
        folderAccess);

    private LayoutService CreateSutWithPath(string path) => new(
        _mockWindowService.Object,
        _mockMonitorService.Object,
        _mockProcessService.Object,
        TestDataFactory.CreateMockForegroundService().Object,
        path);

    private LayoutStorageOptions OptionsWith(
        IReadOnlyList<string>? migrationSources = null,
        IReadOnlyList<SampleLayout>? samples = null) =>
        new(_layoutsPath, migrationSources ?? [], samples ?? []);

    private static string ToJson(LayoutPreset preset) => JsonSerializer.Serialize(preset, JsonOptions);

    private static SampleLayout Sample(string name, string? description = null) =>
        new($"{name}.json", ToJson(TestDataFactory.CreateLayoutPreset(name, description)));

    private static void WritePreset(string folder, string fileName, LayoutPreset preset)
    {
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, fileName), ToJson(preset));
    }

    #region Lazy creation and unusable locations

    [Fact]
    public void Constructor_DoesNotTouchTheFileSystem()
    {
        CreateSut(OptionsWith(samples: [Sample("sample")]));

        Directory.Exists(_layoutsPath).Should().BeFalse();
    }

    [Fact]
    public void LayoutsPath_ReturnsConfiguredFolder()
    {
        var sut = CreateSut(OptionsWith());

        sut.LayoutsPath.Should().Be(_layoutsPath);
    }

    [Fact]
    public async Task GetStorageError_CreatesFolderAndReturnsNull()
    {
        var sut = CreateSut(OptionsWith());

        sut.GetStorageError().Should().BeNull();
        Directory.Exists(_layoutsPath).Should().BeTrue();
    }

    [Fact]
    public async Task UnusableFolder_ConstructsAndReportsStorageUnavailable()
    {
        // Arrange - a file sits where a parent folder would have to be created
        Directory.CreateDirectory(_root);
        var blocker = Path.Combine(_root, "blocker");
        File.WriteAllText(blocker, "not a folder");
        var options = new LayoutStorageOptions(Path.Combine(blocker, "layouts"), [], []);

        // Act
        var act = () => CreateSut(options);

        // Assert
        act.Should().NotThrow();
        var sut = CreateSut(options);

        sut.GetStorageError().Should().NotBeNullOrEmpty()
            .And.Contain(LayoutStorageOptions.LayoutsPathEnvironmentVariable);
        sut.GetAllPresets().Should().BeEmpty();
        sut.GetPreset("anything").Should().BeNull();

        var save = await sut.SavePresetAsync(TestDataFactory.CreateLayoutPreset("x"));
        save.Success.Should().BeFalse();
        save.ErrorCode.Should().Be(nameof(LayoutErrorCode.StorageUnavailable));

        var capture = await sut.CaptureCurrentLayoutAsync("x");
        capture.Success.Should().BeFalse();
        capture.ErrorCode.Should().Be(nameof(LayoutErrorCode.StorageUnavailable));

        var delete = await sut.DeletePresetAsync("x");
        delete.Success.Should().BeFalse();
        delete.ErrorCode.Should().Be(nameof(LayoutErrorCode.StorageUnavailable));

        var apply = await sut.ApplyPresetAsync("x");
        apply.Success.Should().BeFalse();
        apply.ErrorCode.Should().Be(nameof(LayoutErrorCode.StorageUnavailable));
    }

    [Fact]
    public async Task RelativeFolder_ReportsStorageUnavailable()
    {
        var sut = CreateSut(new LayoutStorageOptions(@"relative\layouts", [], []));

        sut.GetStorageError().Should().Contain("absolute");
        Directory.Exists(Path.Combine(Environment.CurrentDirectory, "relative")).Should().BeFalse();
    }

    /// <summary>
    /// Directory.CreateDirectory succeeds for a folder that already exists even if it is read-only
    /// for this user, so writing is checked separately. The denial is simulated through the
    /// injected folder access; no permissions are changed on a real folder.
    /// </summary>
    [Fact]
    public async Task ExistingFolderThatCannotBeWritten_ReportsStorageUnavailable()
    {
        // Arrange
        Directory.CreateDirectory(_layoutsPath);
        var access = new Mock<ILayoutFolderAccess>();
        access.Setup(a => a.CheckCanWrite(_layoutsPath))
            .Throws(new UnauthorizedAccessException($"Access to the path '{_layoutsPath}' is denied."));
        var sut = CreateSut(OptionsWith(samples: [Sample("sample")]), access.Object);

        // Act
        var error = sut.GetStorageError();

        // Assert
        error.Should().Contain(_layoutsPath).And.Contain("cannot be written")
            .And.Contain(LayoutStorageOptions.LayoutsPathEnvironmentVariable);
        sut.GetAllPresets().Should().BeEmpty();
        var save = await sut.SavePresetAsync(TestDataFactory.CreateLayoutPreset("x"));
        save.ErrorCode.Should().Be(nameof(LayoutErrorCode.StorageUnavailable));
        Directory.EnumerateFileSystemEntries(_layoutsPath).Should().BeEmpty("nothing is seeded into an unusable folder");
    }

    [Fact]
    public void ExistingFolderThatCannotBeListed_ReportsStorageUnavailable()
    {
        // Arrange
        Directory.CreateDirectory(_layoutsPath);
        var access = new Mock<ILayoutFolderAccess>();
        access.Setup(a => a.CheckCanList(_layoutsPath))
            .Throws(new UnauthorizedAccessException($"Access to the path '{_layoutsPath}' is denied."));
        var sut = CreateSut(OptionsWith(), access.Object);

        // Act
        var error = sut.GetStorageError();

        // Assert - before, the folder looked empty and list_layouts returned [] without an error
        error.Should().Contain(_layoutsPath).And.Contain("cannot be read")
            .And.Contain(LayoutStorageOptions.LayoutsPathEnvironmentVariable);
        sut.GetPreset("anything").Should().BeNull();
    }

    [Fact]
    public void FolderAccessDenied_IsCheckedAgainUntilItWorks()
    {
        // Arrange
        var access = new Mock<ILayoutFolderAccess>();
        access.SetupSequence(a => a.CheckCanWrite(_layoutsPath))
            .Throws(new UnauthorizedAccessException("denied"))
            .Pass();
        var sut = CreateSut(OptionsWith(samples: [Sample("sample")]), access.Object);

        // Act
        var first = sut.GetStorageError();
        var second = sut.GetStorageError();

        // Assert
        first.Should().NotBeNull();
        second.Should().BeNull();
        sut.GetPreset("sample").Should().NotBeNull("first-run seeding happens once the folder is usable");
    }

    [Fact]
    public void LayoutFolderAccess_OnWritableFolder_PassesAndLeavesNothingBehind()
    {
        // Arrange
        Directory.CreateDirectory(_layoutsPath);
        var access = new LayoutFolderAccess();

        // Act
        var act = () =>
        {
            access.CheckCanList(_layoutsPath);
            access.CheckCanWrite(_layoutsPath);
        };

        // Assert
        act.Should().NotThrow();
        Directory.EnumerateFileSystemEntries(_layoutsPath).Should().BeEmpty();
    }

    [Fact]
    public void LayoutFolderAccess_OnMissingFolder_Throws()
    {
        var access = new LayoutFolderAccess();
        var missing = Path.Combine(_root, "missing");

        access.Invoking(a => a.CheckCanList(missing)).Should().Throw<IOException>();
        access.Invoking(a => a.CheckCanWrite(missing)).Should().Throw<IOException>();
    }

    #endregion

    #region First-run seeding

    [Fact]
    public void FirstUse_SeedsSamplePresets()
    {
        var sut = CreateSut(OptionsWith(samples: [Sample("work-setup"), Sample("focus-mode")]));

        var presets = sut.GetAllPresets();

        presets.Select(p => p.Name).Should().BeEquivalentTo("work-setup", "focus-mode");
        File.Exists(Path.Combine(_layoutsPath, "work-setup.json")).Should().BeTrue();
    }

    [Fact]
    public async Task FirstUse_DoesNotOverwriteExistingPresetWithSampleName()
    {
        WritePreset(_layoutsPath, "work-setup.json",
            TestDataFactory.CreateLayoutPreset("work-setup", description: "Mine"));
        var sut = CreateSut(OptionsWith(samples: [Sample("work-setup", "Sample")]));

        sut.GetPreset("work-setup")!.Description.Should().Be("Mine");
    }

    [Fact]
    public async Task DeletedSample_IsNotSeededAgainOnNextStart()
    {
        var options = OptionsWith(samples: [Sample("work-setup")]);
        var first = CreateSut(options);
        first.GetPreset("work-setup").Should().NotBeNull();
        (await first.DeletePresetAsync("work-setup")).Success.Should().BeTrue();

        var second = CreateSut(options);

        second.GetPreset("work-setup").Should().BeNull();
    }

    [Fact]
    public void ForPathConstructor_DoesNotSeedOrMigrate()
    {
        WritePreset(_legacyPath, "old.json", TestDataFactory.CreateLayoutPreset("old"));
        var sut = CreateSutWithPath(_layoutsPath);

        sut.GetAllPresets().Should().BeEmpty();
    }

    #endregion

    #region First-run migration

    [Fact]
    public void FirstUse_CopiesPresetsFromLegacyFolder_WithoutDeletingThem()
    {
        WritePreset(_legacyPath, "morning.json", TestDataFactory.CreateLayoutPreset("morning"));
        var sut = CreateSut(OptionsWith(migrationSources: [_legacyPath]));

        sut.GetPreset("morning").Should().NotBeNull();
        File.Exists(Path.Combine(_layoutsPath, "morning.json")).Should().BeTrue();
        File.Exists(Path.Combine(_legacyPath, "morning.json")).Should().BeTrue();
    }

    [Fact]
    public void FirstUse_LegacyPresetDoesNotOverwriteExistingPreset()
    {
        WritePreset(_layoutsPath, "morning.json", TestDataFactory.CreateLayoutPreset("morning", "Current"));
        WritePreset(_legacyPath, "morning.json", TestDataFactory.CreateLayoutPreset("morning", "Legacy"));
        var sut = CreateSut(OptionsWith(migrationSources: [_legacyPath]));

        sut.GetPreset("morning")!.Description.Should().Be("Current");
    }

    [Fact]
    public void FirstUse_LegacyPresetWinsOverSampleWithSameName()
    {
        WritePreset(_legacyPath, "work-setup.json", TestDataFactory.CreateLayoutPreset("work-setup", "Saved by user"));
        var sut = CreateSut(OptionsWith(
            migrationSources: [_legacyPath],
            samples: [Sample("work-setup", "Sample")]));

        sut.GetPreset("work-setup")!.Description.Should().Be("Saved by user");
    }

    [Fact]
    public void FirstUse_WithSamePresetInSeveralLegacyFolders_CopiesTheNewest()
    {
        var olderFolder = Path.Combine(_root, "v1", "layouts");
        var newerFolder = Path.Combine(_root, "v2", "layouts");
        WritePreset(olderFolder, "morning.json", TestDataFactory.CreateLayoutPreset("morning", "Older"));
        WritePreset(newerFolder, "morning.json", TestDataFactory.CreateLayoutPreset("morning", "Newer"));
        File.SetLastWriteTimeUtc(Path.Combine(olderFolder, "morning.json"), DateTime.UtcNow.AddDays(-2));
        File.SetLastWriteTimeUtc(Path.Combine(newerFolder, "morning.json"), DateTime.UtcNow.AddDays(-1));

        var sut = CreateSut(OptionsWith(migrationSources: [newerFolder, olderFolder]));

        sut.GetPreset("morning")!.Description.Should().Be("Newer");
    }

    [Fact]
    public async Task FirstUse_SkipsInvalidLegacyFiles()
    {
        Directory.CreateDirectory(_legacyPath);
        File.WriteAllText(Path.Combine(_legacyPath, "broken.json"), "{ not json");
        var sut = CreateSut(OptionsWith(migrationSources: [_legacyPath]));

        sut.GetAllPresets().Should().BeEmpty();
        File.Exists(Path.Combine(_layoutsPath, "broken.json")).Should().BeFalse();
    }

    [Fact]
    public async Task DeletedMigratedPreset_IsNotMigratedAgainOnNextStart()
    {
        WritePreset(_legacyPath, "morning.json", TestDataFactory.CreateLayoutPreset("morning"));
        var options = OptionsWith(migrationSources: [_legacyPath]);
        var first = CreateSut(options);
        (await first.DeletePresetAsync("morning")).Success.Should().BeTrue();

        var second = CreateSut(options);

        second.GetPreset("morning").Should().BeNull();
    }

    [Fact]
    public void MissingLegacyFolder_IsIgnored()
    {
        var sut = CreateSut(OptionsWith(migrationSources: [Path.Combine(_root, "does-not-exist")]));

        sut.GetStorageError().Should().BeNull();
        sut.GetAllPresets().Should().BeEmpty();
    }

    #endregion

    #region Changes made on disk after startup

    [Fact]
    public async Task GetPreset_SeesPresetFileAddedAfterStartup()
    {
        var sut = CreateSutWithPath(_layoutsPath);
        sut.GetAllPresets().Should().BeEmpty();

        WritePreset(_layoutsPath, "dropped-in.json", TestDataFactory.CreateLayoutPreset("dropped-in"));

        sut.GetPreset("dropped-in").Should().NotBeNull();
    }

    [Fact]
    public async Task GetPreset_SeesPresetFileEditedAfterStartup()
    {
        var sut = CreateSutWithPath(_layoutsPath);
        await sut.SavePresetAsync(TestDataFactory.CreateLayoutPreset("edited", "Before"));
        sut.GetPreset("edited")!.Description.Should().Be("Before");

        var file = Path.Combine(_layoutsPath, "edited.json");
        File.WriteAllText(file, ToJson(TestDataFactory.CreateLayoutPreset("edited", "After edit")));
        File.SetLastWriteTimeUtc(file, DateTime.UtcNow.AddMinutes(1));

        sut.GetPreset("edited")!.Description.Should().Be("After edit");
    }

    [Fact]
    public async Task GetPreset_ForgetsPresetWhoseFileWasDeletedOnDisk()
    {
        var sut = CreateSutWithPath(_layoutsPath);
        await sut.SavePresetAsync(TestDataFactory.CreateLayoutPreset("gone"));

        File.Delete(Path.Combine(_layoutsPath, "gone.json"));

        sut.GetPreset("gone").Should().BeNull();
    }

    [Fact]
    public async Task ApplyPresetAsync_SeesPresetFileAddedAfterStartup()
    {
        var sut = CreateSutWithPath(_layoutsPath);
        sut.GetAllPresets().Should().BeEmpty();
        WritePreset(_layoutsPath, "late.json", TestDataFactory.CreateLayoutPreset("late",
            placements: [TestDataFactory.CreateWindowPlacement(processName: "notepad")]));

        var result = await sut.ApplyPresetAsync("late");

        result.Success.Should().BeTrue();
        result.WindowsArranged.Should().Be(1);
    }

    #endregion

    #region Concurrency

    [Fact]
    public async Task ParallelReadsAndWrites_DoNotThrow()
    {
        var sut = CreateSutWithPath(_layoutsPath);

        var tasks = Enumerable.Range(0, 16).Select(i => Task.Run(async () =>
        {
            for (var j = 0; j < 5; j++)
            {
                await sut.SavePresetAsync(TestDataFactory.CreateLayoutPreset($"p{i % 5}"));
                sut.GetAllPresets();
                sut.GetPreset($"p{j % 5}");
            }
        }));

        await FluentActions.Awaiting(() => Task.WhenAll(tasks)).Should().NotThrowAsync();
        sut.GetAllPresets().Should().HaveCount(5);
        Directory.GetFiles(_layoutsPath, "*.tmp").Should().BeEmpty();
    }

    #endregion
    #region Schema version 2 (roadmap M6.8)

    [Fact]
    public void GetPreset_OfAVersion1File_IsUpgradedInMemory_AndTheFileIsLeftAsItWas()
    {
        // Arrange - the sample shape: no version, a launchCommand
        const string json = """
            {
              "name": "old",
              "placements": [
                { "matcher": { "processName": "Code" }, "monitorIndex": 0,
                  "position": { "x": 0, "y": 0, "width": 0.5, "height": 1 }, "launchCommand": "code", "order": 0 }
              ]
            }
            """;
        Directory.CreateDirectory(_layoutsPath);
        var file = Path.Combine(_layoutsPath, "old.json");
        File.WriteAllText(file, json);

        // Act
        var preset = CreateSutWithPath(_layoutsPath).GetPreset("old");

        // Assert
        preset!.Version.Should().Be(2);
        preset.Placements.Single().Launch.Should().Be(new LayoutLaunch(Executable: "code"));
        File.ReadAllText(file).Should().Be(json, "a user's file is never rewritten on load");
    }

    [Fact]
    public async Task SavePresetAsync_WritesVersion2_WithTheVersion1Fields()
    {
        // Arrange
        var sut = CreateSutWithPath(_layoutsPath);
        var placement = TestDataFactory.CreateWindowPlacement(processName: "Code") with
        {
            Launch = new LayoutLaunch(AppId: "Microsoft.WindowsCalculator_8wekyb3d8bbwe!App"),
            Monitor = "m-11111111"
        };

        // Act
        await sut.SavePresetAsync(TestDataFactory.CreateLayoutPreset("v2", placements: [placement]));

        // Assert
        using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(_layoutsPath, "v2.json")));
        var root = document.RootElement;
        root.GetProperty("version").GetInt32().Should().Be(2);
        var written = root.GetProperty("placements")[0];
        written.GetProperty("launchCommand").GetString().Should().Be(@"shell:AppsFolder\Microsoft.WindowsCalculator_8wekyb3d8bbwe!App");
        written.GetProperty("monitorIndex").GetInt32().Should().Be(0);
        written.GetProperty("monitor").GetString().Should().Be("m-11111111");
        written.GetProperty("launch").GetProperty("appId").GetString().Should().Be("Microsoft.WindowsCalculator_8wekyb3d8bbwe!App");
    }

    #endregion
}
