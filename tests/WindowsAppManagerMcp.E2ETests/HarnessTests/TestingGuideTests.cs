using System.Text.RegularExpressions;

namespace WindowsAppManagerMcp.E2ETests.HarnessTests;

/// <summary>
/// TESTING.md's description of the E2E guard ("End-to-end tests against the fixture app") is what a developer reads before
/// opting in to a tier or writing a scenario, so it must name every kind of call the guard tells apart. These tests read
/// the file only.
/// </summary>
public class TestingGuideTests
{
    /// <summary>How the guide names each <see cref="WrapperCallKind"/> in its list of the kinds.</summary>
    private static readonly IReadOnlyDictionary<WrapperCallKind, string> NamesInTheGuide = new Dictionary<WrapperCallKind, string>
    {
        [WrapperCallKind.Read] = "read",
        [WrapperCallKind.WindowAction] = "window action",
        [WrapperCallKind.Input] = "input",
        [WrapperCallKind.ProcessAction] = "process action",
        [WrapperCallKind.ProcessStart] = "process start",
        [WrapperCallKind.ThreadInputAttach] = "thread-input attach",
        [WrapperCallKind.GlobalHotkey] = "global hotkey",
        [WrapperCallKind.Clipboard] = "clipboard",
        [WrapperCallKind.UiaAction] = "UI Automation action"
    };

    [Fact]
    public void TheGuide_ListsEveryKindOfCallTheCatalogClassifies()
    {
        // Arrange - "`WrapperCatalog` classifies every wrapper member explicitly (read, window action, ... or global hotkey)"
        var guide = Regex.Replace(File.ReadAllText(PathOf("TESTING.md")), @"\s+", " ");
        var list = Regex.Match(guide, @"classifies every wrapper member explicitly \(([^)]*)\)");

        // Act
        var named = list.Groups[1].Value.Split([", ", " or "], StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);

        // Assert - a new kind needs a name here and a rule in the guide
        list.Success.Should().BeTrue("TESTING.md lists the kinds of calls the guard classifies");
        NamesInTheGuide.Keys.Should().BeEquivalentTo(Enum.GetValues<WrapperCallKind>());
        named.Should().BeEquivalentTo(NamesInTheGuide.Values);
    }

    /// <summary>A file of the repository, found by walking up from the test output folder to the solution.</summary>
    private static string PathOf(string fileName)
    {
        for (var folder = new DirectoryInfo(AppContext.BaseDirectory); folder != null; folder = folder.Parent)
        {
            if (File.Exists(Path.Combine(folder.FullName, "WindowsAppManagerMcp.sln")))
            {
                return Path.Combine(folder.FullName, fileName);
            }
        }

        throw new InvalidOperationException($"No WindowsAppManagerMcp.sln above {AppContext.BaseDirectory}.");
    }
}
