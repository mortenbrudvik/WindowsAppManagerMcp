using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace WindowsAppManagerMcp.Tests.Unit;

/// <summary>
/// The release scripts prove that the committed bin/WindowsAppManagerMcp.exe was built from committed
/// files by checking the paths in $BuildInputPaths (scripts/ReleaseCommon.ps1): Publish-Plugin.ps1
/// refuses to build while one of them is dirty, and Test-PluginBinary.ps1 reports the exe as stale
/// when one of them changed. Anything else the build reads would slip past both checks.
/// These tests only read files in the repository.
/// </summary>
public class ReleaseBuildInputsTests
{
    private static readonly string[] RootBuildFilePatterns =
        ["Directory.Build.*", "Directory.Packages.props", "global.json", "nuget.config"];

    [Fact]
    public void BuildInputPaths_IncludeSrcAndTheEmbeddedSampleLayouts()
    {
        ReadBuildInputPaths().Should().Contain(["src", "config"]);
    }

    [Fact]
    public void EveryItemTheProjectIncludesFromOutsideSrc_IsABuildInput()
    {
        // Arrange - e.g. <EmbeddedResource Include="..\..\config\layouts\*.json" />
        var projectPath = RepositoryFiles.PathOf("src", "WindowsAppManagerMcp", "WindowsAppManagerMcp.csproj");
        var projectDirectory = Path.GetDirectoryName(projectPath)!;
        var buildInputs = ReadBuildInputPaths();

        var outsideSrc = XDocument.Load(projectPath).Descendants()
            .Select(element => element.Attribute("Include")?.Value)
            .Where(include => include != null && (include.Contains('\\') || include.Contains('/')))
            .Select(include => ToRepositoryPath(Path.Combine(projectDirectory, include!)))
            .Where(path => !path.StartsWith("src/", StringComparison.OrdinalIgnoreCase))
            .ToList();

        // Assert
        outsideSrc.Should().NotBeEmpty("the project embeds the sample layouts from config/layouts");
        outsideSrc.Where(path => !IsCovered(path, buildInputs)).Should().BeEmpty(
            "every file the build reads must be listed in $BuildInputPaths in scripts/ReleaseCommon.ps1");
    }

    [Fact]
    public void EveryRootBuildFile_IsABuildInput()
    {
        // Arrange - MSBuild, NuGet and the SDK pick these up from the repository root.
        // Directory.Build.props is checked by the scripts separately ($BuildPropsPath), because
        // Set-Version.ps1 changes its <Version> on purpose.
        var buildInputs = ReadBuildInputPaths();
        var rootFiles = RootBuildFilePatterns
            .SelectMany(pattern => Directory.EnumerateFiles(RepositoryFiles.Root, pattern))
            .Select(Path.GetFileName)
            .Where(name => !string.Equals(name, "Directory.Build.props", StringComparison.OrdinalIgnoreCase))
            .ToList();

        // Assert
        rootFiles.Where(name => !IsCovered(name!, buildInputs)).Should().BeEmpty(
            "root build files must be listed in $BuildInputPaths in scripts/ReleaseCommon.ps1");
    }

    private static List<string> ReadBuildInputPaths()
    {
        var script = File.ReadAllText(RepositoryFiles.PathOf("scripts", "ReleaseCommon.ps1"));
        var list = Regex.Match(script, @"\$script:BuildInputPaths\s*=\s*@\((?<body>.*?)\r?\n\)", RegexOptions.Singleline);
        list.Success.Should().BeTrue("scripts/ReleaseCommon.ps1 defines $script:BuildInputPaths");

        return Regex.Matches(list.Groups["body"].Value, "'(?<path>[^']*)'")
            .Select(match => match.Groups["path"].Value)
            .ToList();
    }

    private static bool IsCovered(string repositoryPath, IEnumerable<string> buildInputs) =>
        buildInputs.Any(input =>
        {
            // Git pathspec magic such as ':(icase)nuget.config'
            var ignoreCase = input.StartsWith(":(icase)", StringComparison.Ordinal);
            var path = ignoreCase ? input[":(icase)".Length..] : input;
            var comparison = ignoreCase ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            return repositoryPath.Equals(path, comparison) || repositoryPath.StartsWith(path + "/", comparison);
        });

    private static string ToRepositoryPath(string path) =>
        Path.GetRelativePath(RepositoryFiles.Root, Path.GetFullPath(path)).Replace('\\', '/');
}
