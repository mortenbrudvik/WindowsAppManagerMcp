using WindowsAppManagerMcp.Infrastructure;
using WindowsAppManagerMcp.Tools;

namespace WindowsAppManagerMcp.Tests.Unit.Tools;

/// <summary>
/// list_layouts and get_layout with layout files that are not valid presets (review finding F1): list_layouts lists each
/// after the presets, with its file, error and errorCode InvalidPresetFile, and get_layout's PresetNotFound names the file
/// that holds the preset asked for. The layout service is a mock.
/// </summary>
public class LayoutPresetToolsInvalidFileTests
{
    private static readonly DateTime Written = new(2026, 10, 1, 8, 30, 0, DateTimeKind.Utc);

    private readonly Mock<ILayoutService> _layouts = new();

    public LayoutPresetToolsInvalidFileTests()
    {
        _layouts.Setup(l => l.GetAllPresets()).Returns([TestDataFactory.CreateLayoutPreset("work-setup")]);
        _layouts.Setup(l => l.GetInvalidFiles()).Returns(
        [
            new InvalidLayoutFile("broken.json", "it is not valid layout JSON: 'x' is an invalid start of a value.", Written),
            new InvalidLayoutFile("morning.json", "placements[0].position: width -0.5 and height 1 must be above 0.", Written, "Morning")
        ]);
    }

    private LayoutPresetTools CreateSut() => new(_layouts.Object);

    [Fact]
    public void ListLayouts_ListsTheInvalidFilesAfterThePresets_WithWhy()
    {
        // Act
        var result = CreateSut().ListLayouts();

        // Assert - the presets keep their fields and come first
        result.Should().HaveCount(3);
        result[0].Name.Should().Be("work-setup");
        result[0].File.Should().BeNull();
        result[0].Error.Should().BeNull();
        result[0].ErrorCode.Should().BeNull();

        result[1].Should().BeEquivalentTo(new
        {
            Name = "broken",
            WindowCount = 0,
            File = "broken.json",
            ErrorCode = nameof(LayoutErrorCode.InvalidPresetFile),
            UpdatedAt = Written.ToString("O")
        });
        result[1].Error.Should().Contain("not valid layout JSON").And.Contain("Fix or delete");
        result[2].Name.Should().Be("Morning", "the name the file gives");
        result[2].File.Should().Be("morning.json");
    }

    [Fact]
    public void ListLayouts_WithoutInvalidFiles_IsThePresetsAlone()
    {
        _layouts.Setup(l => l.GetInvalidFiles()).Returns([]);

        CreateSut().ListLayouts().Should().ContainSingle().Which.Name.Should().Be("work-setup");
    }

    [Fact]
    public void GetLayout_OfAPresetWhoseFileIsInvalid_IsPresetNotFound_AndNamesTheFile()
    {
        // Arrange
        _layouts.Setup(l => l.GetPreset("morning")).Returns((LayoutPreset?)null);

        // Act
        var act = () => CreateSut().GetLayout("morning");

        // Assert
        var error = act.Should().Throw<ToolErrorException>().Which;
        error.ErrorCode.Should().Be(nameof(LayoutErrorCode.PresetNotFound));
        error.Message.Should().StartWith("Layout preset 'morning' was not found.")
            .And.Contain("'morning.json'").And.Contain("width -0.5").And.Contain("1 other file");
    }

    [Fact]
    public void GetLayout_OfANameNoFileStandsFor_SaysHowManyFilesAreInvalid()
    {
        _layouts.Setup(l => l.GetPreset("typo")).Returns((LayoutPreset?)null);

        var act = () => CreateSut().GetLayout("typo");

        act.Should().Throw<ToolErrorException>().Which.Message.Should()
            .StartWith("Layout preset 'typo' was not found. Call list_layouts to see the available presets.")
            .And.Contain("2 files in the layouts folder are not valid presets");
    }
}
