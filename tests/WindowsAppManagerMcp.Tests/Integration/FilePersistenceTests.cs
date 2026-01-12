using System.Text.Json;

namespace WindowsAppManagerMcp.Tests.Integration;

/// <summary>
/// Integration tests for file persistence behavior.
/// Tests T7.2 from BACKLOG-TECHNICAL.md.
///
/// These tests verify JSON file I/O, path handling, and concurrent operations.
/// </summary>
public class FilePersistenceTests : IDisposable
{
    private readonly string _testLayoutPath;
    private readonly Mock<IWindowService> _mockWindowService;
    private readonly Mock<IMonitorService> _mockMonitorService;
    private readonly Mock<IProcessService> _mockProcessService;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public FilePersistenceTests()
    {
        _testLayoutPath = Path.Combine(
            Path.GetTempPath(),
            $"FilePersistenceTests_{Guid.NewGuid()}");

        _mockWindowService = TestDataFactory.CreateMockWindowService();
        _mockMonitorService = TestDataFactory.CreateMockMonitorService();
        _mockProcessService = TestDataFactory.CreateMockProcessService();
    }

    private LayoutService CreateSut(string? customPath = null) => new(
        _mockWindowService.Object,
        _mockMonitorService.Object,
        _mockProcessService.Object,
        customPath ?? _testLayoutPath);

    public void Dispose()
    {
        if (Directory.Exists(_testLayoutPath))
        {
            Directory.Delete(_testLayoutPath, recursive: true);
        }
    }

    #region T7.2.1: Save Preset Creates JSON File in Correct Directory

    [Fact]
    public async Task SavePreset_CreatesJsonFile_InCorrectDirectory()
    {
        // Arrange
        var sut = CreateSut();
        var preset = TestDataFactory.CreateLayoutPreset("file-location-test");

        // Act
        await sut.SavePresetAsync(preset);

        // Assert
        var expectedPath = Path.Combine(_testLayoutPath, "file-location-test.json");
        File.Exists(expectedPath).Should().BeTrue();
    }

    #endregion

    #region T7.2.2: File Naming Uses Preset Name

    [Fact]
    public async Task SavePreset_FileNaming_UsesPresetName()
    {
        // Arrange
        var sut = CreateSut();
        var preset1 = TestDataFactory.CreateLayoutPreset("workspace-development");
        var preset2 = TestDataFactory.CreateLayoutPreset("workspace-gaming");

        // Act
        await sut.SavePresetAsync(preset1);
        await sut.SavePresetAsync(preset2);

        // Assert
        File.Exists(Path.Combine(_testLayoutPath, "workspace-development.json")).Should().BeTrue();
        File.Exists(Path.Combine(_testLayoutPath, "workspace-gaming.json")).Should().BeTrue();
    }

    #endregion

    #region T7.2.3: Invalid Characters in Name Sanitizes Filename

    [Fact]
    public async Task SavePreset_InvalidCharsInName_SanitizesFilename()
    {
        // Arrange
        var sut = CreateSut();
        var preset = TestDataFactory.CreateLayoutPreset("test:with*invalid?chars<>|");

        // Act
        await sut.SavePresetAsync(preset);

        // Assert
        var files = Directory.GetFiles(_testLayoutPath, "*.json");
        files.Should().HaveCount(1);

        // File should exist with sanitized name (invalid chars replaced with underscores)
        var fileName = Path.GetFileName(files[0]);
        fileName.Should().NotContain(":");
        fileName.Should().NotContain("*");
        fileName.Should().NotContain("?");
        fileName.Should().NotContain("<");
        fileName.Should().NotContain(">");
        fileName.Should().NotContain("|");
        fileName.Should().Contain("_");
    }

    #endregion

    #region T7.2.4: Directory Not Exists Creates Directory

    [Fact]
    public async Task SavePreset_DirectoryNotExists_CreatesDirectory()
    {
        // Arrange
        var newPath = Path.Combine(
            Path.GetTempPath(),
            $"NewDir_{Guid.NewGuid()}",
            "layouts");

        try
        {
            Directory.Exists(newPath).Should().BeFalse();
            var sut = CreateSut(newPath);
            var preset = TestDataFactory.CreateLayoutPreset("auto-create-dir-test");

            // Act
            await sut.SavePresetAsync(preset);

            // Assert
            Directory.Exists(newPath).Should().BeTrue();
            File.Exists(Path.Combine(newPath, "auto-create-dir-test.json")).Should().BeTrue();
        }
        finally
        {
            // Cleanup
            var parentDir = Path.GetDirectoryName(newPath);
            if (parentDir != null && Directory.Exists(parentDir))
            {
                Directory.Delete(parentDir, recursive: true);
            }
        }
    }

    #endregion

    #region T7.2.5: Load Presets Valid JSON Files All Loaded

    [Fact]
    public async Task LoadPresets_ValidJsonFiles_AllLoaded()
    {
        // Arrange - Create preset files directly
        Directory.CreateDirectory(_testLayoutPath);

        var preset1 = TestDataFactory.CreateLayoutPreset("valid-1");
        var preset2 = TestDataFactory.CreateLayoutPreset("valid-2");
        var preset3 = TestDataFactory.CreateLayoutPreset("valid-3");

        await WritePresetToFile(preset1);
        await WritePresetToFile(preset2);
        await WritePresetToFile(preset3);

        // Act - Create service (loads presets in constructor)
        var sut = CreateSut();
        var presets = sut.GetAllPresets();

        // Assert
        presets.Should().HaveCount(3);
        presets.Should().Contain(p => p.Name == "valid-1");
        presets.Should().Contain(p => p.Name == "valid-2");
        presets.Should().Contain(p => p.Name == "valid-3");
    }

    #endregion

    #region T7.2.6: Load Presets Invalid JSON File Skips Without Error

    [Fact]
    public void LoadPresets_InvalidJsonFile_SkipsWithoutError()
    {
        // Arrange - Create an invalid JSON file
        Directory.CreateDirectory(_testLayoutPath);
        var invalidFilePath = Path.Combine(_testLayoutPath, "invalid.json");
        File.WriteAllText(invalidFilePath, "{ this is not valid json }");

        // Act - Should not throw
        var sut = CreateSut();
        var presets = sut.GetAllPresets();

        // Assert
        presets.Should().BeEmpty();
    }

    #endregion

    #region T7.2.7: Load Presets Mixed Valid Invalid Loads Valid Only

    [Fact]
    public async Task LoadPresets_MixedValidInvalid_LoadsValidOnly()
    {
        // Arrange
        Directory.CreateDirectory(_testLayoutPath);

        // Valid presets
        await WritePresetToFile(TestDataFactory.CreateLayoutPreset("valid-preset"));

        // Invalid files
        File.WriteAllText(
            Path.Combine(_testLayoutPath, "invalid.json"),
            "{ not valid json }");
        File.WriteAllText(
            Path.Combine(_testLayoutPath, "empty.json"),
            "");
        File.WriteAllText(
            Path.Combine(_testLayoutPath, "wrong-schema.json"),
            "{\"notAPreset\": true}");

        // Act
        var sut = CreateSut();
        var presets = sut.GetAllPresets();

        // Assert - Only the valid preset should be loaded
        presets.Should().HaveCount(1);
        presets[0].Name.Should().Be("valid-preset");
    }

    #endregion

    #region T7.2.8: Save Preset JSON Format CamelCase and Indented

    [Fact]
    public async Task SavePreset_JsonFormat_CamelCaseAndIndented()
    {
        // Arrange
        var sut = CreateSut();
        var preset = new LayoutPreset(
            Name: "format-test",
            Description: "Test description",
            Placements: new List<WindowPlacement>
            {
                new(
                    Matcher: new WindowMatcher("Title", "Process", null),
                    MonitorIndex: 0,
                    Position: new RelativePosition(0.1, 0.2, 0.3, 0.4),
                    LaunchCommand: null,
                    Focus: false,
                    Order: 0)
            });

        // Act
        await sut.SavePresetAsync(preset);

        // Assert - Read raw JSON
        var filePath = Path.Combine(_testLayoutPath, "format-test.json");
        var json = await File.ReadAllTextAsync(filePath);

        // Should use camelCase property names
        json.Should().Contain("\"name\":");
        json.Should().Contain("\"description\":");
        json.Should().Contain("\"placements\":");
        json.Should().Contain("\"monitorIndex\":");
        json.Should().Contain("\"titleContains\":");
        json.Should().Contain("\"processName\":");
        json.Should().Contain("\"launchCommand\":");

        // Should NOT use PascalCase
        json.Should().NotContain("\"Name\":");
        json.Should().NotContain("\"MonitorIndex\":");

        // Should be indented (contains newlines and whitespace)
        json.Should().Contain("\n");
        json.Should().Contain("  "); // Indentation
    }

    #endregion

    #region T7.2.9: Concurrent Save Different Presets No Conflicts

    [Fact]
    public async Task ConcurrentSave_DifferentPresets_NoConflicts()
    {
        // Arrange
        var sut = CreateSut();
        var presets = Enumerable.Range(1, 10)
            .Select(i => TestDataFactory.CreateLayoutPreset($"concurrent-{i}"))
            .ToList();

        // Act - Save all presets concurrently
        var tasks = presets.Select(p => sut.SavePresetAsync(p));
        var results = await Task.WhenAll(tasks);

        // Assert - All should succeed
        results.Should().AllSatisfy(r => r.Should().BeTrue());

        // Verify all files exist
        var files = Directory.GetFiles(_testLayoutPath, "*.json");
        files.Should().HaveCount(10);

        // Verify all presets are loadable
        var loaded = sut.GetAllPresets();
        loaded.Should().HaveCount(10);
    }

    #endregion

    #region T7.2.10: Concurrent Save Same Preset Last Write Wins

    [Fact]
    public async Task ConcurrentSave_SamePreset_LastWriteWins()
    {
        // Arrange
        var sut = CreateSut();
        var variants = Enumerable.Range(1, 5)
            .Select(i => new LayoutPreset(
                Name: "same-name",
                Description: $"Variant {i}",
                Placements: new List<WindowPlacement>()))
            .ToList();

        // Act - Save all variants concurrently (same name)
        var tasks = variants.Select(p => sut.SavePresetAsync(p));
        await Task.WhenAll(tasks);

        // Assert - Only one file should exist
        var files = Directory.GetFiles(_testLayoutPath, "*.json");
        files.Should().HaveCount(1);

        // One of the variants should have won
        var loaded = sut.GetPreset("same-name");
        loaded.Should().NotBeNull();
        loaded!.Description.Should().StartWith("Variant ");
    }

    #endregion

    #region Helper Methods

    private async Task WritePresetToFile(LayoutPreset preset)
    {
        var filePath = Path.Combine(_testLayoutPath, $"{preset.Name}.json");
        var json = JsonSerializer.Serialize(preset, new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        });
        await File.WriteAllTextAsync(filePath, json);
    }

    #endregion
}
