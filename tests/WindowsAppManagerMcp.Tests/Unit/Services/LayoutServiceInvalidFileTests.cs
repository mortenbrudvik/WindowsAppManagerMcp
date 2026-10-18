using Microsoft.Extensions.Logging;

namespace WindowsAppManagerMcp.Tests.Unit.Services;

/// <summary>
/// Layout files that are not valid presets (review findings F1, F2, F5): a file that is not JSON, lacks a name or placements,
/// has a placement whose position is no rectangle (a size of 0 or less, or out of any range a window can have), or a state
/// given as a number, is never applied, and never left out silently either: GetInvalidFiles lists it with why, a warning is
/// logged once per version of the file, and PresetNotFound names it. Every test works in its own temp folder; the window
/// services are mocks.
/// </summary>
public class LayoutServiceInvalidFileTests : IDisposable
{
    private readonly string _layoutsPath = Path.Combine(Path.GetTempPath(), $"LayoutServiceInvalidFileTests_{Guid.NewGuid():N}");
    private readonly Mock<IWindowService> _windows = TestDataFactory.CreateMockWindowService([]);
    private readonly Mock<IMonitorService> _monitors = TestDataFactory.CreateMockMonitorService();
    private readonly Mock<IForegroundService> _foreground = TestDataFactory.CreateMockForegroundService();
    private readonly CapturingLoggerProvider _logs = new();

    public LayoutServiceInvalidFileTests()
    {
        Directory.CreateDirectory(_layoutsPath);
    }

    public void Dispose()
    {
        if (Directory.Exists(_layoutsPath))
            Directory.Delete(_layoutsPath, recursive: true);
    }

    private LayoutService CreateSut(ILayoutFolderAccess? folderAccess = null) => new(
        _windows.Object, _monitors.Object, TestDataFactory.CreateMockProcessService().Object, _foreground.Object,
        LayoutStorageOptions.ForPath(_layoutsPath), folderAccess,
        logger: LoggerFactory.Create(b => b.AddProvider(_logs)).CreateLogger<LayoutService>());

    private IEnumerable<CapturedLogEntry> Warnings => _logs.Entries.Where(e => e.Level == LogLevel.Warning);

    private void Write(string fileName, string json) => File.WriteAllText(Path.Combine(_layoutsPath, fileName), json);

    /// <summary>A version 1 preset with one placement of Code at <paramref name="position"/>; <paramref name="extra"/> is added to it.</summary>
    private static string Preset(string name, string position = """{ "x": 0, "y": 0, "width": 0.5, "height": 1 }""", string extra = "") => $$"""
        {
          "name": "{{name}}",
          "placements": [
            { "matcher": { "processName": "Code" }, "monitorIndex": 0, "position": {{position}}{{extra}} }
          ]
        }
        """;

    #region What is invalid

    public static TheoryData<string, string, string> InvalidFiles() => new()
    {
        { "not JSON", "{ \"name\": \"broken\", ", "not valid" },
        { "no name", """{ "placements": [] }""", "no name" },
        { "no placements", """{ "name": "broken" }""", "no placements" },
        { "an empty placement", """{ "name": "broken", "placements": [ null ] }""", "placements[0]" },
        { "a placement without a matcher", """{ "name": "broken", "placements": [ { "position": { "x": 0, "y": 0, "width": 1, "height": 1 } } ] }""", "matcher" },
        { "a placement without a position", """{ "name": "broken", "placements": [ { "matcher": { "processName": "Code" } } ] }""", "position" },
        { "a negative width", Preset("broken", """{ "x": 0, "y": 0, "width": -0.5, "height": 1 }"""), "placements[0].position" },
        { "a width of 0", Preset("broken", """{ "x": 0, "y": 0, "width": 0, "height": 1 }"""), "placements[0].position" },
        { "a height of 0", Preset("broken", """{ "x": 0, "y": 0, "width": 0.5, "height": 0 }"""), "placements[0].position" },
        { "an x a million work areas away", Preset("broken", """{ "x": 1000000, "y": 0, "width": 0.5, "height": 1 }"""), "placements[0].position" },
        { "a huge width", Preset("broken", """{ "x": 0, "y": 0, "width": 1e300, "height": 1 }"""), "placements[0].position" },
        {
            "a restore rectangle without a size",
            Preset("broken", extra: """, "state": "maximized", "normalPosition": { "x": 0, "y": 0, "width": 0, "height": 0 }"""),
            "placements[0].normalPosition"
        },
        { "a state as a number", Preset("broken", extra: """, "state": 7"""), "state" },
        { "a known state as a number", Preset("broken", extra: """, "state": 2"""), "state" },
    };

    [Theory]
    [MemberData(nameof(InvalidFiles))]
    public void AnInvalidFile_IsNoPreset_ButIsListedWithWhy(string why, string json, string expected)
    {
        // Arrange
        Write("broken.json", json);
        var sut = CreateSut();

        // Act
        var presets = sut.GetAllPresets();
        var invalid = sut.GetInvalidFiles();

        // Assert
        presets.Should().BeEmpty(why);
        var file = invalid.Should().ContainSingle(why).Which;
        file.FileName.Should().Be("broken.json");
        file.Error.Should().Contain(expected, why);
        sut.GetPreset("broken").Should().BeNull(why);
    }

    [Theory]
    [InlineData("hanging off the left edge", """{ "x": -0.5, "y": 0, "width": 0.5, "height": 1 }""")]
    [InlineData("on the monitor below", """{ "x": 0, "y": 1.2, "width": 1, "height": 1 }""")]
    [InlineData("wider than the work area", """{ "x": -0.004, "y": 0, "width": 1.008, "height": 1.01 }""")]
    public void APositionOffTheWorkArea_StaysValid_AsItAlwaysWas(string why, string position)
    {
        Write("off.json", Preset("off", position));
        var sut = CreateSut();

        sut.GetPreset("off").Should().NotBeNull(why);
        sut.GetInvalidFiles().Should().BeEmpty(why);
    }

    [Theory]
    [InlineData("maximized")]
    [InlineData("Maximized")]
    public void AStateByItsName_InAnyCase_IsRead(string state)
    {
        Write("named.json", Preset("named", extra: $$""", "state": "{{state}}" """));

        CreateSut().GetPreset("named")!.Placements.Single().State.Should().Be(WindowState.Maximized);
    }

    [Fact]
    public async Task APresetSaveLayoutWrites_HasItsStateByName()
    {
        // Arrange - writing is unchanged: names, which the reader takes
        var sut = CreateSut();
        var preset = new LayoutPreset("saved", null,
            [new WindowPlacement(new WindowMatcher(ProcessName: "Code"), 0, new RelativePosition(0, 0, 1, 1)) { State = WindowState.Minimized }]);

        // Act
        (await sut.SavePresetAsync(preset)).Success.Should().BeTrue();

        // Assert
        File.ReadAllText(Path.Combine(_layoutsPath, "saved.json")).Should().Contain("\"state\": \"minimized\"");
        sut.GetPreset("saved")!.Placements.Single().State.Should().Be(WindowState.Minimized);
    }

    #endregion

    #region Reporting

    [Fact]
    public void AnInvalidFile_IsLoggedOnce_UntilItChanges()
    {
        // Arrange
        Write("broken.json", "not json");
        var sut = CreateSut();

        // Act - read three times, then the file is rewritten (still broken)
        sut.GetAllPresets();
        sut.GetInvalidFiles();
        sut.GetPreset("anything");
        var once = Warnings.Count();
        Write("broken.json", "still not json, and longer");
        File.SetLastWriteTimeUtc(Path.Combine(_layoutsPath, "broken.json"), DateTime.UtcNow.AddMinutes(1));
        sut.GetAllPresets();

        // Assert
        once.Should().Be(1);
        Warnings.Should().HaveCount(2);
        Warnings.Should().OnlyContain(e => e.Message.Contains("broken.json"));
    }

    [Fact]
    public void AFixedFile_IsAPresetAgain_AndNoLongerInvalid()
    {
        Write("work.json", Preset("work", """{ "x": 0, "y": 0, "width": 0, "height": 1 }"""));
        var sut = CreateSut();
        sut.GetInvalidFiles().Should().ContainSingle();

        Write("work.json", Preset("work"));
        File.SetLastWriteTimeUtc(Path.Combine(_layoutsPath, "work.json"), DateTime.UtcNow.AddMinutes(1));

        sut.GetPreset("work").Should().NotBeNull();
        sut.GetInvalidFiles().Should().BeEmpty();
    }

    [Fact]
    public void AnInvalidFile_KnowsTheNameItGives()
    {
        Write("my-layout.json", Preset("Morning", """{ "x": 0, "y": 0, "width": -1, "height": 1 }"""));

        var file = CreateSut().GetInvalidFiles().Single();

        file.DeclaredName.Should().Be("Morning");
        file.Name.Should().Be("Morning");
    }

    [Fact]
    public async Task ApplyingAPresetWhoseFileIsInvalid_IsPresetNotFound_AndNamesTheFile()
    {
        // Arrange
        Write("morning.json", Preset("morning", """{ "x": 0, "y": 0, "width": -0.5, "height": 1 }"""));

        // Act
        var result = await CreateSut().ApplyPresetAsync("morning");

        // Assert
        result.Success.Should().BeFalse();
        result.ErrorCode.Should().Be(nameof(LayoutErrorCode.PresetNotFound));
        result.Errors!.Single().Should().StartWith("Layout preset 'morning' not found.")
            .And.Contain("morning.json").And.Contain("placements[0].position");
        _windows.Verify(w => w.SetWindowBounds(It.IsAny<nint>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<int>(),
            It.IsAny<bool>()), Times.Never);
    }

    [Fact]
    public async Task DeletingAPresetWhoseFileIsInvalid_IsPresetNotFound_AndNamesTheFile_ButDeletesNothing()
    {
        Write("morning.json", "{ not json");

        var result = await CreateSut().DeletePresetAsync("morning");

        result.ErrorCode.Should().Be(nameof(LayoutErrorCode.PresetNotFound));
        result.ErrorMessage.Should().Contain("morning.json");
        File.Exists(Path.Combine(_layoutsPath, "morning.json")).Should().BeTrue("a file that is no preset is the user's to fix or delete");
    }

    [Fact]
    public async Task ANameNoFileStandsFor_IsPresetNotFound_AsBefore()
    {
        var result = await CreateSut().ApplyPresetAsync("typo");

        result.Errors.Should().Equal("Layout preset 'typo' not found");
    }

    [Fact]
    public void AFolderThatCannotBeListed_KeepsThePresetsReadBefore_AndWarnsTheyMayBeStale()
    {
        // Arrange - the listing works once, then fails (simulated: no permissions are changed on a real folder)
        Write("work.json", Preset("work"));
        var listings = 0;
        var access = new Mock<ILayoutFolderAccess>();
        access.Setup(a => a.ListPresetFiles(_layoutsPath)).Returns(() => ++listings == 1
            ? new LayoutFolderAccess().ListPresetFiles(_layoutsPath)
            : throw new IOException("The network path was not found."));
        var sut = CreateSut(access.Object);

        // Act
        var before = sut.GetPreset("work");
        var after = sut.GetPreset("work");
        sut.GetPreset("work");

        // Assert
        before.Should().NotBeNull();
        after.Should().NotBeNull("a transient error does not forget every preset");
        Warnings.Should().ContainSingle("the warning is logged when the listing starts to fail, not on every call")
            .Which.Message.Should().Contain("stale");
    }

    #endregion
}
