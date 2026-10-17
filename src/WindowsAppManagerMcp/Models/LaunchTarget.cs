namespace WindowsAppManagerMcp.Models;

/// <summary>
/// How Windows will run a launch target, which decides how its arguments are parsed.
/// </summary>
public enum LaunchTargetKind
{
    /// <summary>
    /// A native program (.exe, .com). Its arguments are parsed with the standard Windows rules,
    /// so correctly quoted arguments cannot become commands.
    /// </summary>
    NativeExecutable,

    /// <summary>
    /// A batch file (.bat, .cmd). ShellExecute runs it through cmd.exe, which interprets
    /// &amp; | &lt; &gt; ^ % ! and quotes in its arguments. The launch refuses it as a script (roadmap M3.F8).
    /// </summary>
    BatchFile,

    /// <summary>
    /// A URI, document, shortcut, script, or a name that could not be resolved. The program that
    /// will receive the arguments is not known.
    /// </summary>
    Other,

    /// <summary>
    /// A packaged (MSIX or Store) app named by its AppUserModelID, bare or as shell:AppsFolder\&lt;AUMID&gt; (roadmap
    /// M6.5): activated through IApplicationActivationManager, which passes the arguments to the app as one command line
    /// (no cmd.exe). <see cref="ResolvedLaunchTarget.App"/> names it.
    /// </summary>
    PackagedApp
}

/// <summary>
/// The result of resolving an executable name or path to the file that will actually be started.
/// </summary>
/// <param name="FileName">The value to pass to ShellExecute: an absolute path when the target was resolved, otherwise the input unchanged.</param>
/// <param name="Kind">How the target will be run.</param>
/// <param name="FoundOnlyInWorkingDirectory">
/// For a bare name that was not found in a trusted location: the path of a matching file in the
/// current or working directory, which ShellExecute would otherwise run (binary planting).
/// </param>
/// <param name="NotFound">
/// True for a bare name that was found in no trusted location (and not in the working directory either): Windows
/// would search it on its own, the current directory included, so it must not be started.
/// </param>
public record ResolvedLaunchTarget(
    string FileName,
    LaunchTargetKind Kind,
    string? FoundOnlyInWorkingDirectory = null,
    bool NotFound = false
)
{
    /// <summary>
    /// For <see cref="LaunchTargetKind.PackagedApp"/>: the app's AppUserModelID, whose package family name also tells its
    /// windows (roadmap M6.5); <see cref="FileName"/> is then its shell:AppsFolder target. Null for any other kind.
    /// </summary>
    public PackagedAppId? App { get; init; }
}

/// <summary>
/// A file or folder a launch opens, as a window title can show it: its name (such as todo.txt), and its full path when
/// the server knows it (a relative argument is taken in the launch's working directory, as the app takes it).
/// </summary>
/// <param name="Name">The file or folder name, 3 characters or more.</param>
/// <param name="FullPath">The full path, without a trailing separator; null when it is not known.</param>
public sealed record DocumentHint(string Name, string? FullPath = null);
