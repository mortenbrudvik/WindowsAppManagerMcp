using WindowsAppManagerMcp.Models;

namespace WindowsAppManagerMcp.Services.Interfaces;

/// <summary>
/// Resolves the executable passed to launch_application to the file Windows will actually run.
/// </summary>
public interface IExecutableResolver
{
    /// <summary>
    /// Resolves a validated executable (bare name, absolute path or URI).
    /// Bare names are searched in the Windows directory, the System32 directory, the absolute
    /// entries of PATH and then App Paths - never in the current or working directory.
    /// </summary>
    /// <param name="executable">The validated executable name, path or URI.</param>
    /// <param name="workingDirectory">The working directory the process will be started in.</param>
    ResolvedLaunchTarget Resolve(string executable, string? workingDirectory);
}
