using System.Text.Json;

namespace WindowsAppManagerMcp.Tests.Unit.Services;

/// <summary>
/// Tests for resolving where layout presets are stored, which legacy folders are
/// migrated, and which sample presets ship with the server.
/// </summary>
public class LayoutStorageOptionsTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"LayoutStorageOptionsTests_{Guid.NewGuid():N}");

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    #region ResolveLayoutsPath

    [Fact]
    public void ResolveLayoutsPath_WithoutOverride_UsesLocalAppDataFolder()
    {
        var path = LayoutStorageOptions.ResolveLayoutsPath(null, @"C:\Users\someone\AppData\Local");

        path.Should().Be(@"C:\Users\someone\AppData\Local\WindowsAppManagerMcp\layouts");
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void ResolveLayoutsPath_WithBlankOverride_UsesLocalAppDataFolder(string overridePath)
    {
        var path = LayoutStorageOptions.ResolveLayoutsPath(overridePath, @"C:\Local");

        path.Should().Be(@"C:\Local\WindowsAppManagerMcp\layouts");
    }

    [Fact]
    public void ResolveLayoutsPath_WithOverride_UsesOverride()
    {
        var path = LayoutStorageOptions.ResolveLayoutsPath(@"D:\Sync\layouts", @"C:\Local");

        path.Should().Be(@"D:\Sync\layouts");
    }

    [Fact]
    public void ResolveLayoutsPath_WithQuotedOverride_StripsQuotes()
    {
        var path = LayoutStorageOptions.ResolveLayoutsPath("\"D:\\My Layouts\"", @"C:\Local");

        path.Should().Be(@"D:\My Layouts");
    }

    [Fact]
    public void ResolveLayoutsPath_WithEnvironmentVariableInOverride_ExpandsIt()
    {
        var path = LayoutStorageOptions.ResolveLayoutsPath(@"%SystemRoot%\layouts", @"C:\Local");

        path.Should().Be(Path.Combine(Environment.GetEnvironmentVariable("SystemRoot")!, "layouts"));
    }

    [Fact]
    public void LayoutsPathEnvironmentVariable_IsWamLayoutsPath()
    {
        LayoutStorageOptions.LayoutsPathEnvironmentVariable.Should().Be("WAM_LAYOUTS_PATH");
    }

    #endregion

    #region FindLegacyLayoutFolders

    [Fact]
    public void FindLegacyLayoutFolders_IncludesLayoutsFolderNextToExecutable()
    {
        var baseDirectory = Path.Combine(_root, "app") + Path.DirectorySeparatorChar;

        var folders = LayoutStorageOptions.FindLegacyLayoutFolders(baseDirectory);

        folders.Should().ContainSingle()
            .Which.Should().Be(Path.Combine(_root, "app", "layouts"));
    }

    [Fact]
    public void FindLegacyLayoutFolders_InPluginCache_IncludesOtherInstalledVersions()
    {
        // Arrange - ...\plugins\cache\<marketplace>\windows-app-manager\<version>\bin
        var pluginDir = Path.Combine(_root, "plugins", "cache", "some-marketplace", "windows-app-manager");
        var currentBin = Path.Combine(pluginDir, "1.12.0", "bin");
        Directory.CreateDirectory(currentBin);
        Directory.CreateDirectory(Path.Combine(pluginDir, "1.10.0", "bin", "layouts"));
        Directory.CreateDirectory(Path.Combine(pluginDir, "1.11.0", "bin"));

        // Act
        var folders = LayoutStorageOptions.FindLegacyLayoutFolders(currentBin + Path.DirectorySeparatorChar);

        // Assert
        folders.Should().Contain(Path.Combine(currentBin, "layouts"));
        folders.Should().Contain(Path.Combine(pluginDir, "1.10.0", "bin", "layouts"));
        folders.Should().Contain(Path.Combine(pluginDir, "1.11.0", "bin", "layouts"));
    }

    [Fact]
    public void FindLegacyLayoutFolders_OutsidePluginCache_DoesNotScanSiblingFolders()
    {
        // Arrange - a dev checkout such as C:\code\WindowsAppManagerMcp\bin next to other projects
        var currentBin = Path.Combine(_root, "code", "WindowsAppManagerMcp", "bin");
        Directory.CreateDirectory(currentBin);
        Directory.CreateDirectory(Path.Combine(_root, "code", "OtherProject", "bin", "layouts"));

        // Act
        var folders = LayoutStorageOptions.FindLegacyLayoutFolders(currentBin);

        // Assert
        folders.Should().ContainSingle()
            .Which.Should().Be(Path.Combine(currentBin, "layouts"));
    }

    [Fact]
    public void FindLegacyLayoutFolders_ForDifferentPluginName_DoesNotScanSiblingFolders()
    {
        var pluginDir = Path.Combine(_root, "plugins", "cache", "some-marketplace", "another-plugin");
        var currentBin = Path.Combine(pluginDir, "2.0.0", "bin");
        Directory.CreateDirectory(currentBin);
        Directory.CreateDirectory(Path.Combine(pluginDir, "1.0.0", "bin", "layouts"));

        var folders = LayoutStorageOptions.FindLegacyLayoutFolders(currentBin);

        folders.Should().ContainSingle();
    }

    #endregion

    #region Create / ForPath

    [Fact]
    public void Create_CombinesResolvedPathLegacyFoldersAndSamples()
    {
        var samples = new List<SampleLayout> { new("a.json", "{}") };
        var baseDirectory = Path.Combine(_root, "app");

        var options = LayoutStorageOptions.Create(null, @"C:\Local", baseDirectory, samples);

        options.LayoutsPath.Should().Be(@"C:\Local\WindowsAppManagerMcp\layouts");
        options.MigrationSources.Should().Contain(Path.Combine(baseDirectory, "layouts"));
        options.Samples.Should().BeEquivalentTo(samples);
    }

    [Fact]
    public void ForPath_DoesNotMigrateOrSeed()
    {
        var options = LayoutStorageOptions.ForPath(_root);

        options.LayoutsPath.Should().Be(_root);
        options.MigrationSources.Should().BeEmpty();
        options.Samples.Should().BeEmpty();
    }

    #endregion

    #region Embedded samples

    [Fact]
    public void LoadEmbeddedSamples_ContainsTheBundledSamplePresets()
    {
        var samples = LayoutStorageOptions.LoadEmbeddedSamples();

        samples.Select(s => s.FileName).Should().BeEquivalentTo(
            "work-setup.json", "dual-monitor.json", "focus-mode.json");
    }

    [Fact]
    public void LoadEmbeddedSamples_AreValidPresetsNamedAfterTheirFiles()
    {
        var options = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

        foreach (var sample in LayoutStorageOptions.LoadEmbeddedSamples())
        {
            var preset = JsonSerializer.Deserialize<LayoutPreset>(sample.Json, options);

            preset.Should().NotBeNull();
            preset!.Name.Should().Be(Path.GetFileNameWithoutExtension(sample.FileName));
            preset.Placements.Should().NotBeEmpty();
        }
    }

    #endregion
}
