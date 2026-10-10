using System.Reflection;

namespace WindowsAppManagerMcp.E2ETests.Harness;

/// <summary>
/// Finds WamFixtureApp.exe: the build records its path relative to this assembly
/// (AssemblyMetadata "WamFixtureAppPath"), and <see cref="PathEnvironmentVariable"/> overrides it, for
/// example when the tests run in Windows Sandbox from a mapped folder.
/// </summary>
public static class FixtureAppLocator
{
    /// <summary>Full path of a WamFixtureApp.exe to use instead of the one built with the tests.</summary>
    public const string PathEnvironmentVariable = "WAM_FIXTURE_APP_PATH";

    /// <summary>The AssemblyMetadata key the project file writes the relative path under.</summary>
    public const string BuildPathMetadataKey = "WamFixtureAppPath";

    /// <summary>The fixture path the build recorded, relative to the test output folder.</summary>
    public static string? BuiltRelativePath =>
        typeof(FixtureAppLocator).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(a => a.Key == BuildPathMetadataKey)?.Value;

    /// <summary>The fixture exe for this test run.</summary>
    public static string Locate() =>
        Locate(Environment.GetEnvironmentVariable(PathEnvironmentVariable), BuiltRelativePath, AppContext.BaseDirectory);

    /// <summary>
    /// Resolves the fixture exe from an override path or the recorded build path. Only a file named
    /// WamFixtureApp.exe is accepted: the harness drives and kills what it starts, so it must never start
    /// anything else.
    /// </summary>
    /// <exception cref="InvalidOperationException">No path is known, or the file is not WamFixtureApp.exe.</exception>
    /// <exception cref="FileNotFoundException">The file does not exist.</exception>
    public static string Locate(string? overridePath, string? builtRelativePath, string baseDirectory)
    {
        string path;
        if (!string.IsNullOrWhiteSpace(overridePath))
        {
            path = Path.GetFullPath(overridePath);
        }
        else if (!string.IsNullOrWhiteSpace(builtRelativePath))
        {
            path = Path.GetFullPath(Path.Combine(baseDirectory, builtRelativePath));
        }
        else
        {
            throw new InvalidOperationException(
                $"This test assembly was built without the '{BuildPathMetadataKey}' metadata, and " +
                $"{PathEnvironmentVariable} is not set. Build tests/WindowsAppManagerMcp.E2ETests with its project file.");
        }

        var expectedName = FixtureContract.ProcessName + ".exe";
        if (!string.Equals(Path.GetFileName(path), expectedName, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"The fixture app must be {expectedName}, not '{path}'. The E2E harness starts, drives and kills " +
                "the app it is given, so it only accepts the suite's own fixture.");
        }

        if (!File.Exists(path))
        {
            throw new FileNotFoundException(
                $"The fixture app was not found at '{path}'. Build the solution (or tests/FixtureApp), " +
                $"or set {PathEnvironmentVariable} to its full path.",
                path);
        }

        return path;
    }
}
