namespace WindowsAppManagerMcp.Services;

/// <summary>
/// A bundled sample layout preset that is copied into the layouts folder on first use.
/// </summary>
/// <param name="FileName">File name to create in the layouts folder (no directories).</param>
/// <param name="Json">Preset JSON content.</param>
public sealed record SampleLayout(string FileName, string Json);

/// <summary>
/// Where layout presets are stored and how the folder is prepared the first time it is used.
/// </summary>
/// <remarks>
/// Presets used to live in <c>&lt;exe folder&gt;\layouts</c>. For the Claude Code plugin that is inside a
/// per-version cache folder, so every plugin update started with an empty folder. Presets now live in
/// <c>%LOCALAPPDATA%\WindowsAppManagerMcp\layouts</c> (or <see cref="LayoutsPathEnvironmentVariable"/>).
/// </remarks>
/// <param name="LayoutsPath">Folder that holds the preset JSON files.</param>
/// <param name="MigrationSources">
/// Folders used by older versions. On first use, their presets are copied (never moved, never
/// overwriting) into <paramref name="LayoutsPath"/>.
/// </param>
/// <param name="Samples">Sample presets seeded on first use, without overwriting existing presets.</param>
public sealed record LayoutStorageOptions(
    string LayoutsPath,
    IReadOnlyList<string> MigrationSources,
    IReadOnlyList<SampleLayout> Samples)
{
    /// <summary>
    /// Environment variable that overrides the layouts folder. It should be an absolute path;
    /// environment variables inside it (e.g. <c>%USERPROFILE%</c>) are expanded.
    /// </summary>
    public const string LayoutsPathEnvironmentVariable = "WAM_LAYOUTS_PATH";

    /// <summary>
    /// Folder name under %LOCALAPPDATA% used by this server.
    /// </summary>
    public const string AppDataFolderName = "WindowsAppManagerMcp";

    private const string LayoutsFolderName = "layouts";
    private const string PluginFolderName = "windows-app-manager";
    private const string PluginCacheFolderName = "cache";
    private const string SampleResourcePrefix = "WindowsAppManagerMcp.SampleLayouts.";

    /// <summary>
    /// Options for a plain folder: no migration and no sample seeding.
    /// </summary>
    public static LayoutStorageOptions ForPath(string layoutsPath) => new(layoutsPath, [], []);

    /// <summary>
    /// Options for the running server: the folder from <see cref="LayoutsPathEnvironmentVariable"/> or
    /// %LOCALAPPDATA%, legacy folders next to this and other installed plugin versions, and the bundled samples.
    /// </summary>
    public static LayoutStorageOptions FromEnvironment() => Create(
        Environment.GetEnvironmentVariable(LayoutsPathEnvironmentVariable),
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        AppContext.BaseDirectory,
        LoadEmbeddedSamples());

    public static LayoutStorageOptions Create(
        string? overridePath,
        string localAppDataPath,
        string baseDirectory,
        IReadOnlyList<SampleLayout> samples) => new(
            ResolveLayoutsPath(overridePath, localAppDataPath),
            FindLegacyLayoutFolders(baseDirectory),
            samples);

    /// <summary>
    /// Returns the override path (quotes stripped, environment variables expanded) when it is set,
    /// otherwise <c>&lt;localAppDataPath&gt;\WindowsAppManagerMcp\layouts</c>.
    /// </summary>
    public static string ResolveLayoutsPath(string? overridePath, string localAppDataPath)
    {
        if (!string.IsNullOrWhiteSpace(overridePath))
        {
            return Environment.ExpandEnvironmentVariables(overridePath.Trim().Trim('"'));
        }

        return Path.Combine(localAppDataPath ?? string.Empty, AppDataFolderName, LayoutsFolderName);
    }

    /// <summary>
    /// Folders older versions stored presets in: <c>&lt;baseDirectory&gt;\layouts</c> and, when running from the
    /// Claude Code plugin cache (<c>...\cache\&lt;marketplace&gt;\windows-app-manager\&lt;version&gt;\...</c>), the
    /// matching folders of the other installed versions. Sibling folders are only scanned inside that
    /// plugin cache structure, never next to an arbitrary install location.
    /// </summary>
    public static IReadOnlyList<string> FindLegacyLayoutFolders(string baseDirectory)
    {
        var folders = new List<string>();
        if (string.IsNullOrWhiteSpace(baseDirectory))
            return folders;

        string baseDir;
        try
        {
            baseDir = Path.TrimEndingDirectorySeparator(Path.GetFullPath(baseDirectory));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return folders;
        }

        folders.Add(Path.Combine(baseDir, LayoutsFolderName));

        var versionDir = FindPluginVersionFolder(baseDir);
        if (versionDir?.Parent == null)
            return folders;

        // The exe may sit in <version>\bin or <version>\.claude-plugin\bin depending on how the
        // plugin was installed, so look in both for every other installed version.
        var relativeExeFolders = new[]
            {
                Path.GetRelativePath(versionDir.FullName, baseDir),
                "bin",
                Path.Combine(".claude-plugin", "bin")
            }
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        try
        {
            foreach (var sibling in versionDir.Parent.EnumerateDirectories().OrderBy(d => d.Name, StringComparer.OrdinalIgnoreCase))
            {
                if (string.Equals(sibling.FullName, versionDir.FullName, StringComparison.OrdinalIgnoreCase))
                    continue;

                foreach (var relative in relativeExeFolders)
                {
                    folders.Add(Path.Combine(sibling.FullName, relative, LayoutsFolderName));
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            // Best effort: migration from other versions is optional.
        }

        return folders;
    }

    /// <summary>
    /// Sample presets compiled into the assembly from config/layouts/*.json.
    /// </summary>
    public static IReadOnlyList<SampleLayout> LoadEmbeddedSamples()
    {
        var assembly = typeof(LayoutStorageOptions).Assembly;
        var samples = new List<SampleLayout>();

        foreach (var resourceName in assembly.GetManifestResourceNames()
                     .Where(n => n.StartsWith(SampleResourcePrefix, StringComparison.Ordinal))
                     .OrderBy(n => n, StringComparer.Ordinal))
        {
            using var stream = assembly.GetManifestResourceStream(resourceName);
            if (stream == null)
                continue;

            using var reader = new StreamReader(stream);
            samples.Add(new SampleLayout(resourceName[SampleResourcePrefix.Length..], reader.ReadToEnd()));
        }

        return samples;
    }

    /// <summary>
    /// Walks up from the exe folder to the <c>&lt;version&gt;</c> folder of a plugin cache install
    /// (<c>...\cache\&lt;marketplace&gt;\windows-app-manager\&lt;version&gt;</c>), or returns null.
    /// </summary>
    private static DirectoryInfo? FindPluginVersionFolder(string baseDir)
    {
        // The exe is at most two levels below the version folder (bin or .claude-plugin\bin).
        var candidate = new DirectoryInfo(baseDir);
        for (var depth = 0; depth < 3 && candidate != null; depth++, candidate = candidate.Parent)
        {
            var pluginDir = candidate.Parent;
            var cacheDir = pluginDir?.Parent?.Parent;
            if (pluginDir != null && cacheDir != null &&
                pluginDir.Name.Equals(PluginFolderName, StringComparison.OrdinalIgnoreCase) &&
                cacheDir.Name.Equals(PluginCacheFolderName, StringComparison.OrdinalIgnoreCase))
            {
                return candidate;
            }
        }

        return null;
    }
}
