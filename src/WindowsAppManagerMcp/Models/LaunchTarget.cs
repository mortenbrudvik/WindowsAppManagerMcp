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
    /// &amp; | &lt; &gt; ^ % ! and quotes in its arguments.
    /// </summary>
    BatchFile,

    /// <summary>
    /// A URI, document, shortcut, script, or a name that could not be resolved. The program that
    /// will receive the arguments is not known.
    /// </summary>
    Other
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
public record ResolvedLaunchTarget(
    string FileName,
    LaunchTargetKind Kind,
    string? FoundOnlyInWorkingDirectory = null
);
