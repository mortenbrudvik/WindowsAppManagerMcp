using System.Text.Json;
using System.Xml.Linq;

namespace WindowsAppManagerMcp.Tests.Fixtures;

/// <summary>
/// Read-only access to files at the repository root (version manifests).
/// Located by walking up from the test output directory.
/// </summary>
public static class RepositoryFiles
{
    private static readonly Lazy<string> LazyRoot = new(FindRoot);

    public static string Root => LazyRoot.Value;

    public static string PathOf(params string[] relativeParts) =>
        Path.Combine(new[] { Root }.Concat(relativeParts).ToArray());

    /// <summary>
    /// The single-sourced product version: &lt;Version&gt; in Directory.Build.props.
    /// </summary>
    public static string ReadPropsVersion()
    {
        var props = XDocument.Load(PathOf("Directory.Build.props"));
        var version = props.Descendants("Version").Select(e => e.Value.Trim()).SingleOrDefault();
        if (string.IsNullOrEmpty(version))
        {
            throw new InvalidOperationException("Directory.Build.props does not define a <Version>.");
        }
        return version;
    }

    public static JsonDocument ReadJson(params string[] relativeParts) =>
        JsonDocument.Parse(File.ReadAllText(PathOf(relativeParts)));

    private static string FindRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "WindowsAppManagerMcp.sln")))
            {
                return dir.FullName;
            }
            dir = dir.Parent;
        }
        throw new InvalidOperationException(
            $"Could not find the repository root (WindowsAppManagerMcp.sln) above {AppContext.BaseDirectory}.");
    }
}
