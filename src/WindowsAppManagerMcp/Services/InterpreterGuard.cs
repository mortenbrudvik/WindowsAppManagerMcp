using System.Text.RegularExpressions;
using WindowsAppManagerMcp.Models;
using WindowsAppManagerMcp.Native;

namespace WindowsAppManagerMcp.Services;

/// <summary>
/// Refuses the launches that would run a command or a script (roadmap M3.F8, the repo owner's choice for D-PAL-07): a
/// command interpreter, script host or other program that runs what its command line names (<see cref="Interpreters"/>)
/// started with arguments, and a script file (<see cref="ScriptExtensions"/>), which Windows would hand to such a program,
/// as the target or as what explorer.exe is asked to open. The server does not run commands or scripts for the agent; the
/// user runs them. An interpreter started without arguments (an empty console or terminal) is allowed.
/// </summary>
/// <remarks>
/// Both the name the agent asked for and the file the resolver found are checked, so an App Paths entry that maps
/// another name to an interpreter counts. A name is read the way Windows reads it: quotes, a file: URI, forward slashes,
/// letter case, a missing .exe, trailing dots and spaces, an alternate data stream (cmd.exe::$DATA) and an 8.3 short name
/// (POWERS~1.EXE, expanded from the file system) all lead to the same program. A version after the name counts as the
/// name (python3.12, ubuntu2204). This is a list of names, not a sandbox: a renamed copy of an interpreter, a shortcut
/// (.lnk) or Internet shortcut (.url) whose target is one, a document type associated with an interpreter by the user, a
/// program missing from the list that runs code from its arguments, and a command typed into a console started without
/// arguments are not caught.
/// </remarks>
public sealed partial class InterpreterGuard
{
    /// <summary>
    /// The programs refused with arguments, by file name without .exe, in lower case: command shells and terminals, script
    /// hosts, language runtimes, and system programs that load or run code named on their command line.
    /// </summary>
    public static readonly IReadOnlySet<string> Interpreters = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        // Command shells, consoles and terminals (WSL distributions start a shell too)
        "cmd", "powershell", "powershell_ise", "pwsh", "bash", "sh", "wsl", "wslg", "conhost", "wt", "windowsterminal",
        "ubuntu", "debian", "kali",

        // Script hosts
        "wscript", "cscript", "mshta", "scriptrunner",

        // Language runtimes
        "python", "pythonw", "py", "pyw", "node", "deno", "java", "javaw", "ruby", "rubyw", "perl", "php", "dotnet",

        // System programs that load or run the code their arguments name
        "rundll32", "regsvr32", "msiexec", "cmstp", "msbuild", "installutil", "regasm", "regsvcs", "forfiles", "wmic",
        "schtasks", "pcalua"
    };

    /// <summary>
    /// The file types refused as a launch target, because Windows runs them with an interpreter or installer: batch files,
    /// PowerShell, Windows Script Host and HTML applications, Python, Java archives, Windows Installer packages, Control
    /// Panel items and the scripts of other runtimes.
    /// </summary>
    public static readonly IReadOnlySet<string> ScriptExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        ".bat", ".cmd", ".ps1", ".psm1", ".vbs", ".vbe", ".js", ".jse", ".wsf", ".wsh", ".hta", ".py", ".pyw", ".pyz",
        ".pyzw", ".jar", ".msi", ".msp", ".cpl", ".sh", ".rb", ".pl", ".php"
    };

    // Programs that open what their arguments name with the program registered for it, which runs a script
    private static readonly string[] ScriptOpeners = ["explorer"];

    private readonly Func<string, string?> _longPath;

    /// <summary>Expands 8.3 short names from the real file system.</summary>
    public InterpreterGuard()
        : this(LongPathOf)
    {
    }

    /// <param name="longPath">The long form of a fully qualified path that holds a '~', or null when it cannot be read.</param>
    public InterpreterGuard(Func<string, string?> longPath)
    {
        _longPath = longPath;
    }

    /// <summary>
    /// Why the launch must not be started, with its LaunchErrorCode name, or null when it may: the target is a script
    /// (ScriptFile), an interpreter given arguments (InterpreterWithArguments), or explorer.exe given a script (ScriptFile).
    /// </summary>
    /// <param name="executable">The executable as the agent asked for it.</param>
    /// <param name="resolvedFileName">What the resolver will hand to Windows: the program's full path, or the input unchanged.</param>
    /// <param name="arguments">The arguments; null or empty means none.</param>
    public InterpreterRefusal? Check(string executable, string resolvedFileName, IReadOnlyList<string>? arguments)
    {
        var names = new[] { FileNameOf(executable), FileNameOf(resolvedFileName) }.OfType<string>().ToList();
        var shown = string.Equals(executable.Trim(), resolvedFileName, StringComparison.OrdinalIgnoreCase)
            ? $"'{resolvedFileName}'"
            : $"'{executable}' ({resolvedFileName})";

        if (names.Select(name => Path.GetExtension(name)).FirstOrDefault(ScriptExtensions.Contains) is { } extension)
        {
            var batchHint = extension.Equals(".bat", StringComparison.OrdinalIgnoreCase) || extension.Equals(".cmd", StringComparison.OrdinalIgnoreCase)
                ? " A batch file that only starts a program, such as VS Code's code.cmd, can be replaced by the program's .exe " +
                  "(for VS Code code.exe, which App Paths resolves, or its full path)."
                : string.Empty;
            return new InterpreterRefusal(nameof(LaunchErrorCode.ScriptFile),
                $"{shown} is a script ({extension.ToLowerInvariant()}) that Windows would run with its interpreter, and " +
                $"launch_application does not run scripts: {NoCommands} Ask the user to run it.{batchHint} Nothing was started.");
        }

        if (arguments is not { Count: > 0 })
        {
            return null;
        }

        var programs = names.Select(ProgramName).ToList();
        if (programs.Select(InterpreterOf).OfType<string>().FirstOrDefault() is { } interpreter)
        {
            return new InterpreterRefusal(nameof(LaunchErrorCode.InterpreterWithArguments),
                $"{shown} is a command interpreter or script host ({interpreter}), and launch_application does not start one " +
                $"with arguments: {NoCommands} Ask the user to run the command; do not type it into a console or terminal " +
                "either. Started without arguments it opens as usual. Nothing was started.");
        }

        if (programs.Any(program => ScriptOpeners.Contains(program)))
        {
            foreach (var argument in arguments)
            {
                if (FileNameOf(argument) is { } name && ScriptExtensions.Contains(Path.GetExtension(name)))
                {
                    return new InterpreterRefusal(nameof(LaunchErrorCode.ScriptFile),
                        $"{shown} would open '{argument}', a script ({Path.GetExtension(name).ToLowerInvariant()}), with the " +
                        $"program registered for it, which runs it, and launch_application does not run scripts: {NoCommands} " +
                        "Ask the user to run it. Nothing was started.");
                }
            }
        }

        return null;
    }

    private const string NoCommands = "the server does not run commands or scripts for the agent.";

    /// <summary>
    /// The file name a path, a quoted path or a file: URI ends in, as Windows reads it (8.3 short names expanded, an
    /// alternate data stream and trailing dots and spaces left out); null for another URI or an empty value.
    /// </summary>
    private string? FileNameOf(string value)
    {
        var text = value.Trim().Trim('"').Trim();
        if (text.StartsWith("file:", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                if (!Uri.TryCreate(text, UriKind.Absolute, out var uri) || !uri.IsFile)
                    return null;
                text = uri.LocalPath;
            }
            catch (InvalidOperationException)
            {
                return null;
            }
        }
        else if (UriScheme().IsMatch(text))
        {
            return null;
        }

        text = text.Replace('/', '\\');
        if (text.Contains('~') && IsFullyQualified(text))
        {
            try
            {
                text = _longPath(text) ?? text;
            }
            catch (Exception)
            {
                // Unreadable: keep the short name, which a missing file also keeps from starting
            }
        }

        // A drive without a separator (C:cmd.exe) is not an alternate data stream
        if (text.Length >= 2 && char.IsAsciiLetter(text[0]) && text[1] == ':')
            text = text[2..];

        var name = text[(text.LastIndexOf('\\') + 1)..];
        var stream = name.IndexOf(':');
        if (stream >= 0)
            name = name[..stream];

        name = name.TrimEnd('.', ' ');
        return name.Length == 0 ? null : name;
    }

    /// <summary>A file name without .exe or .com, in lower case.</summary>
    private static string ProgramName(string fileName)
    {
        var extension = Path.GetExtension(fileName);
        var name = extension.Equals(".exe", StringComparison.OrdinalIgnoreCase) || extension.Equals(".com", StringComparison.OrdinalIgnoreCase)
            ? fileName[..^extension.Length]
            : fileName;
        return name.ToLowerInvariant();
    }

    /// <summary>
    /// The entry of <see cref="Interpreters"/> a program name stands for, by itself (rundll32) or without a version after
    /// it (python3.12, ubuntu2204); null for any other program.
    /// </summary>
    private static string? InterpreterOf(string programName)
    {
        if (Interpreters.Contains(programName))
            return programName;

        var unversioned = programName.TrimEnd('0', '1', '2', '3', '4', '5', '6', '7', '8', '9', '.');
        return unversioned.Length > 0 && Interpreters.Contains(unversioned) ? unversioned : null;
    }

    private static bool IsFullyQualified(string path)
    {
        try
        {
            return Path.IsPathFullyQualified(path);
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private static string? LongPathOf(string path)
    {
        var buffer = new char[260];
        var length = NativeMethods.Kernel32.GetLongPathNameW(path, buffer, (uint)buffer.Length);
        if (length > buffer.Length)
        {
            buffer = new char[length];
            length = NativeMethods.Kernel32.GetLongPathNameW(path, buffer, (uint)buffer.Length);
        }

        return length == 0 || length > buffer.Length ? null : new string(buffer, 0, (int)length);
    }

    // A URI scheme of two or more characters, so that a drive letter (C:) is not one
    [GeneratedRegex(@"^[a-zA-Z][a-zA-Z0-9+.-]+:")]
    private static partial Regex UriScheme();
}

/// <summary>Why a launch was refused by <see cref="InterpreterGuard"/> (roadmap M3.F8).</summary>
/// <param name="ErrorCode">The LaunchErrorCode name: InterpreterWithArguments or ScriptFile.</param>
/// <param name="Message">What was refused, why, and what to do instead.</param>
public sealed record InterpreterRefusal(string ErrorCode, string Message);
