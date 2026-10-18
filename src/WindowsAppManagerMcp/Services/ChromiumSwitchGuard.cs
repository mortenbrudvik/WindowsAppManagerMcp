using WindowsAppManagerMcp.Models;

namespace WindowsAppManagerMcp.Services;

/// <summary>
/// Refuses the launch arguments that Chromium, and the Electron and CEF apps built on it, read as a switch that makes the
/// program run a command line of the caller's choosing, load code, or open itself to remote control (review F2): the
/// launch tools say "no commands or scripts are run", and launch_browser_with_debug refuses the same switches as its url
/// (review MCP-S-1). Without this a native .exe got any argument: launch_application("msedge",
/// ["--utility-cmd-prefix=powershell -c ..."]) made the browser run a command, and --remote-debugging-port with the real
/// 'User Data' profile put the user's signed-in sessions on an unauthenticated port.
/// </summary>
/// <remarks>
/// An argument is read the way Chromium reads a switch on Windows: white space trimmed, a "--", "-" or "/" prefix, the name
/// in any letter case up to the first "=", the value after it. Node.js, inside Electron, also takes "_" for "-". A switch
/// inside another switch's value, and the arguments of one argument that holds several or holds quotes (split and unquoted
/// with the Windows rules), are read too, up to <see cref="MaxDepth"/> levels deep: a launcher (Squirrel's Update.exe
/// --process-start-args, chrome_proxy.exe) hands them on to the browser as raw command line, whose quotes the program's
/// parser takes off, so "--inspect" in quotes is --inspect to it. What holds more than those levels can read is refused,
/// not let through unread. Every program's arguments are checked, not only those of a program
/// <see cref="ChromiumPrograms"/> recognizes: such a launcher or helper is not one, and the switches mean nothing to any
/// other program. Refused:
/// <list type="bullet">
/// <item>every *-cmd-prefix and *-launcher switch (utility, renderer, GPU, plugin) and --browser-subprocess-path, with which
/// Chromium starts its child processes through a command line or program of the caller's choosing;</item>
/// <item>--remote-debugging-port, -pipe, -address, -targets (any --remote-debugging-*) and --remote-allow-origins, which open
/// the program, with the profile it runs with, to remote control: launch_browser_with_debug's own start alone may pass
/// --remote-debugging-port, which it builds next to a separate profile it checked;</item>
/// <item>--load-extension and --disable-extensions-except, which load an unpacked extension: code that runs in the browser
/// with access to its pages;</item>
/// <item>VS Code's --extensionDevelopmentPath, --extensionTestsPath, --extensionEnvironment, --extensions-dir,
/// --builtin-extensions-dir, --install-extension and --install-builtin-extension, which load, run or install an extension
/// or a test script of the caller's choosing, with Node.js;</item>
/// <item>Electron's (Node.js's) --inspect and every --inspect-* (VS Code's --inspect-extensions too, and its older
/// --debugPluginHost and --debugBrkPluginHost), which open a debugger port that runs any code sent to it, and
/// --js-flags;</item>
/// <item>a --user-data-dir (also as "--user-data-dir &lt;folder&gt;", which Electron apps read) inside a folder named 'User
/// Data' or its 8.3 short name, where Chrome, Edge and Brave keep the user's real profiles, as launch_browser_with_debug
/// refuses it; a relative folder is taken in the launch's working directory, and an 8.3 short name on a drive letter is
/// expanded from the file system. One on a UNC or other device path is refused instead: expanding it would connect to
/// another computer.</item>
/// </list>
/// A list of switch names, not a sandbox: a switch that only weakens the program (--no-sandbox,
/// --disable-web-security), another app's switch of its own, and electron.exe given a script are not on it.
/// </remarks>
public sealed class ChromiumSwitchGuard
{
    /// <summary>
    /// How deep a switch inside a switch's value, or an argument inside an argument, is read; text that holds more at this
    /// depth is refused.
    /// </summary>
    private const int MaxDepth = 4;

    private const int MaxArgumentLengthInMessages = 80;

    private const string UserDataDirSwitch = "user-data-dir";

    private const string DebugPortSwitch = "remote-debugging-port";

    private static readonly string[] SwitchPrefixes = ["--", "-", "/"];

    private readonly Func<string, string?> _longPath;
    private readonly Func<string, string?, string> _fullPath;

    /// <summary>Expands 8.3 short names from the real file system.</summary>
    public ChromiumSwitchGuard()
        : this(InterpreterGuard.LongPathOf)
    {
    }

    /// <param name="longPath">The long form of a fully qualified path that holds a '~', or null when it cannot be read (the
    /// path does not exist).</param>
    public ChromiumSwitchGuard(Func<string, string?> longPath)
        : this(longPath, FullPathOf)
    {
    }

    /// <param name="longPath">The long form of a fully qualified path that holds a '~', or null when it cannot be read (the
    /// path does not exist).</param>
    /// <param name="fullPath">Makes a path full, in the base folder when one is given, as Path.GetFullPath does; tests pass a
    /// recorder. Path.GetFullPath expands 8.3 short names from the file system too, so it is never given one on a UNC or
    /// device path.</param>
    public ChromiumSwitchGuard(Func<string, string?> longPath, Func<string, string?, string> fullPath)
    {
        _longPath = longPath;
        _fullPath = fullPath;
    }

    private static string FullPathOf(string path, string? basePath) =>
        basePath == null ? Path.GetFullPath(path) : Path.GetFullPath(path, basePath);

    /// <summary>
    /// Why the launch must not get <paramref name="arguments"/>, as an UnsafeChromiumSwitch refusal, or null when it may.
    /// </summary>
    /// <param name="executable">The executable as the agent asked for it, for the message.</param>
    /// <param name="arguments">The arguments; null or empty means none.</param>
    /// <param name="workingDirectory">The folder the program starts in, which a relative --user-data-dir is taken in; null
    /// when it is not known.</param>
    /// <param name="debugBrowserLaunch">launch_browser_with_debug's own start (LaunchRequest.DebugBrowserLaunch): its
    /// --remote-debugging-port passes, every other switch of the list is still refused.</param>
    public InterpreterRefusal? Check(
        string executable, IReadOnlyList<string>? arguments, string? workingDirectory, bool debugBrowserLaunch = false)
    {
        if (arguments is not { Count: > 0 })
        {
            return null;
        }

        for (var i = 0; i < arguments.Count; i++)
        {
            if (arguments[i] is not { } argument)
            {
                continue;
            }

            var next = i + 1 < arguments.Count ? arguments[i + 1] : null;
            if (Refusal(argument, next, workingDirectory, debugBrowserLaunch, depth: 0) is { } found)
            {
                return new InterpreterRefusal(nameof(LaunchErrorCode.UnsafeChromiumSwitch), Message(executable, argument, found));
            }
        }

        return null;
    }

    /// <summary>
    /// The refused switch in <paramref name="text"/> (its name, why, and what to do instead), or null: the text as a
    /// switch, the value of that switch, and each argument of a text that holds several. The name is null when the text
    /// holds more than <see cref="MaxDepth"/> levels can read.
    /// </summary>
    private (string? Name, string Why, string Advice)? Refusal(
        string text, string? next, string? workingDirectory, bool debugBrowserLaunch, int depth)
    {
        var trimmed = text.Trim();
        var found = SwitchOf(trimmed);
        if (found is { } @switch)
        {
            // A name with other characters is no switch Chromium knows ("--foo --gpu-launcher" up to '=', --"inspect"); the
            // split below reads the switch inside it
            if (IsSwitchName(@switch.Name) && Why(@switch.Name, debugBrowserLaunch) is { } why)
            {
                return (@switch.Name, why.Why, why.Advice);
            }

            // Chromium takes the value after '='; an Electron app such as VS Code also takes the next argument
            if (@switch.Name == UserDataDirSwitch && (@switch.Value ?? next) is { } folder &&
                CheckProfileFolder(folder, workingDirectory) is { } problem)
            {
                var inFolder = Explain(problem);
                return (@switch.Name, inFolder.Why, inFolder.Advice);
            }
        }

        // A quote counts as much as white space: a launcher (Squirrel's Update.exe --process-start-args) hands the text on
        // as raw command line, whose quotes the program's parser takes off, so "--inspect" is --inspect to it
        var value = found?.Value is { Length: > 0 } v ? v : null;
        var holdsSeveral = trimmed.Any(c => char.IsWhiteSpace(c) || c == '"');
        if (value == null && !holdsSeveral)
        {
            return null;
        }

        if (depth >= MaxDepth)
        {
            // Fail closed: what is left unread could hold a switch of the list
            return (null,
                "which holds switches inside switch values or arguments nested deeper than the server reads them " +
                $"({MaxDepth} levels), so it cannot be checked for a Chromium or Electron switch that runs a command, loads " +
                "code or opens the program to remote control",
                "Pass the arguments without nesting them that deep.");
        }

        if (value != null && Refusal(value, null, workingDirectory, debugBrowserLaunch, depth + 1) is { } inValue)
        {
            return inValue;
        }

        if (holdsSeveral)
        {
            // A whole command line in one argument, or a quoted switch, as a launcher hands it on; "x" stands for the
            // program's name, which the split skips. The last part's next argument is the one after the text, as a
            // launcher that joins its arguments would pass it.
            var parts = ChromiumCommandLine.Split("x " + trimmed).Skip(1).ToList();
            if (parts.Count > 1 || (parts.Count == 1 && parts[0] != trimmed))
            {
                for (var i = 0; i < parts.Count; i++)
                {
                    if (Refusal(parts[i], i + 1 < parts.Count ? parts[i + 1] : next, workingDirectory, debugBrowserLaunch, depth + 1) is { } inPart)
                    {
                        return inPart;
                    }
                }
            }
        }

        return null;
    }

    /// <summary>
    /// The switch <paramref name="text"/> is to Chromium: its name in lower case, with Node.js's '_' read as '-', and its
    /// value (null without '='); null when it is no switch.
    /// </summary>
    private static (string Name, string? Value)? SwitchOf(string text)
    {
        var prefix = SwitchPrefixes.FirstOrDefault(p => text.Length > p.Length && text.StartsWith(p, StringComparison.Ordinal));
        if (prefix == null)
        {
            return null;
        }

        var body = text[prefix.Length..];
        var separator = body.IndexOf('=');
        var name = (separator < 0 ? body : body[..separator]).ToLowerInvariant().Replace('_', '-');
        return (name, separator < 0 ? null : body[(separator + 1)..]);
    }

    /// <summary>Whether <paramref name="name"/> can be a switch's name: lower-case ASCII letters, digits and '-'.</summary>
    private static bool IsSwitchName(string name) =>
        name.Length > 0 && name.All(c => char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c) || c == '-');

    /// <summary>Why a switch is refused whatever its value, with what to do instead; null when its name is not refused.</summary>
    private static (string Why, string Advice)? Why(string name, bool debugBrowserLaunch)
    {
        if (name.EndsWith("-cmd-prefix", StringComparison.Ordinal) || name.EndsWith("-launcher", StringComparison.Ordinal) ||
            name == "browser-subprocess-path")
        {
            return ("it makes the program start its own processes through a command line or program of the caller's choosing, " +
                    "so it would run any command", "Ask the user to run the command.");
        }

        if (name.StartsWith("remote-debugging", StringComparison.Ordinal) || name == "remote-allow-origins")
        {
            return name == DebugPortSwitch && debugBrowserLaunch
                ? null
                : ("it opens the program to remote control over an unauthenticated debugging port or pipe, with the profile and " +
                   "signed-in sessions it runs with",
                    "To read or control a web page, use launch_browser_with_debug, which starts a browser with a separate profile.");
        }

        if (name is "load-extension" or "disable-extensions-except")
        {
            return ("it loads an unpacked extension: code that runs in the browser with access to its pages",
                    "Ask the user to load the extension.");
        }

        // VS Code (an Electron app) reads these names in camel case; SwitchOf has lower-cased them
        if (name is "extensiondevelopmentpath" or "extensiontestspath" or "extensionenvironment" or "extensions-dir" or
            "builtin-extensions-dir" or "install-extension" or "install-builtin-extension")
        {
            return ("it makes VS Code load, run or install an extension or a test script of the caller's choosing, or set the " +
                    "environment its extensions run in: code that runs with Node.js and the user's rights",
                    "Ask the user to install or run the extension.");
        }

        if (name == "inspect" || name.StartsWith("inspect-", StringComparison.Ordinal) ||
            name is "debugpluginhost" or "debugbrkpluginhost" or "js-flags")
        {
            return ("it opens an Electron app's Node.js inspector, a debugger port that runs any code sent to it, or passes flags " +
                    "to its JavaScript engine", "Ask the user to start the program with it.");
        }

        return null;
    }

    /// <summary>
    /// Why the Chromium profile folder <paramref name="value"/> is refused, or null when it is not: a folder inside a 'User
    /// Data' folder as written, as a full path (a relative one taken in <paramref name="workingDirectory"/>), or with its 8.3
    /// short names expanded; or an 8.3 short name that cannot be expanded without asking another computer. The one check of
    /// a profile folder: <see cref="Check"/> applies it to a --user-data-dir, and launch_browser_with_debug to its
    /// userDataDir (BrowserReadingTools.DebugProfileRefusal), so both refuse the same folders.
    /// </summary>
    /// <param name="value">The folder as given; quotes and white space around it are left out.</param>
    /// <param name="workingDirectory">The folder a relative <paramref name="value"/> is taken in; null when it is not known
    /// (then only its text is checked).</param>
    public ProfileFolderProblem? CheckProfileFolder(string value, string? workingDirectory = null)
    {
        ArgumentNullException.ThrowIfNull(value);
        var path = value.Trim().Trim('"').Trim();
        if (path.Length == 0)
        {
            return null;
        }

        // The folder the path is taken in: itself when it is fully qualified, else the working directory (null: not known)
        var baseFolder = Path.IsPathFullyQualified(path) ? null
            : !string.IsNullOrEmpty(workingDirectory) && Path.IsPathFullyQualified(workingDirectory) ? workingDirectory
            : null;
        var where = baseFolder ?? (Path.IsPathFullyQualified(path) ? path : null);
        if (!HasUserDataSegment(path) && where != null && !IsOnADriveLetter(where) &&
            (path.Contains('~') || (baseFolder?.Contains('~') ?? false)))
        {
            // Path.GetFullPath expands 8.3 short names itself, from the file system (GetLongPathNameW), so on a UNC or other
            // device path it would connect to that computer as the expansion below would: refused before any path is made full
            return ProfileFolderProblem.UnexpandedShortName;
        }

        var forms = new List<string> { path };
        string? full = null;
        try
        {
            full = where == null ? null : _fullPath(path, baseFolder);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            // Not a path Windows takes; only its text is checked
        }

        if (full != null)
        {
            forms.Add(full);
        }

        if (!forms.Any(HasUserDataSegment) && full != null && full.Contains('~'))
        {
            // Expanding a short name on a UNC or other device path would make the server connect, during planning, to a
            // computer the agent named (with the user's credentials, and for as long as SMB waits), and \\localhost\C$
            // reaches the real profile's folder: so such a name is refused, not expanded and not let through unread
            if (!IsOnADriveLetter(full))
            {
                return ProfileFolderProblem.UnexpandedShortName;
            }

            if (LongFormOf(full) is { } longForm)
            {
                forms.Add(longForm);
            }
        }

        return forms.Any(HasUserDataSegment) ? ProfileFolderProblem.InsideUserData : null;
    }

    /// <summary>Why a --user-data-dir with <paramref name="problem"/> is refused, and what to do instead.</summary>
    private static (string Why, string Advice) Explain(ProfileFolderProblem problem) => problem == ProfileFolderProblem.UnexpandedShortName
        ? ("it names a folder by an 8.3 short name ('~') on a network or device path, which the server does not expand, so it " +
           "cannot tell whether the folder is a browser's real 'User Data' profile",
            "Pass the folder's long name, or a separate folder on a local drive.")
        : ("it names a folder inside a 'User Data' folder, where Chrome, Edge and Brave keep the user's real profiles, with the " +
           "user's signed-in sessions",
            "Leave --user-data-dir out, or pass a separate folder.");

    /// <summary>
    /// Whether the fully qualified <paramref name="fullPath"/> is on a drive letter (C:\, \\?\C:\, \\.\C:\), whose 8.3
    /// short names are read from this computer's file system; a UNC path (\\server\share, \\?\UNC\...) and any other
    /// device path (\\?\GLOBALROOT\..., \\?\Volume{...}\...) is not. A mapped network drive's letter counts as a drive:
    /// the user chose its server.
    /// </summary>
    private static bool IsOnADriveLetter(string fullPath)
    {
        var path = fullPath.StartsWith(@"\\?\", StringComparison.Ordinal) || fullPath.StartsWith(@"\\.\", StringComparison.Ordinal)
            ? fullPath[4..]
            : fullPath;
        return path.Length >= 3 && char.IsAsciiLetter(path[0]) && path[1] == ':' && path[2] is '\\' or '/';
    }

    /// <summary>
    /// <paramref name="fullPath"/> with its 8.3 short names expanded: the longest part of it that exists, expanded, and the
    /// rest as written; null when no part can be expanded.
    /// </summary>
    private string? LongFormOf(string fullPath)
    {
        var rest = new Stack<string>();
        var current = fullPath;
        for (var level = 0; level < 64 && !string.IsNullOrEmpty(current); level++)
        {
            string? expanded = null;
            try
            {
                expanded = _longPath(current);
            }
            catch (Exception)
            {
                // Unreadable: try the folder above
            }

            if (expanded != null)
            {
                while (rest.Count > 0)
                {
                    expanded = Path.Combine(expanded, rest.Pop());
                }

                return expanded;
            }

            rest.Push(Path.GetFileName(current));
            current = Path.GetDirectoryName(current);
        }

        return null;
    }

    /// <summary>
    /// Whether a folder of <paramref name="path"/> is 'User Data' or its 8.3 short name (USERDA~1), as Windows reads a
    /// name: a drive-relative start (C:User Data), an alternate data stream (User Data::$INDEX_ALLOCATION) and trailing dots
    /// and spaces left out.
    /// </summary>
    private static bool HasUserDataSegment(string path)
    {
        var segments = path.Split(['\\', '/'], StringSplitOptions.RemoveEmptyEntries);
        for (var i = 0; i < segments.Length; i++)
        {
            var segment = segments[i];
            if (i == 0 && segment.Length > 2 && char.IsAsciiLetter(segment[0]) && segment[1] == ':')
            {
                segment = segment[2..];
            }

            var stream = segment.IndexOf(':');
            if (stream >= 0)
            {
                segment = segment[..stream];
            }

            segment = segment.TrimEnd('.', ' ');
            if (string.Equals(segment, "User Data", StringComparison.OrdinalIgnoreCase) ||
                (segment.StartsWith("USERDA~", StringComparison.OrdinalIgnoreCase) && segment.Length > 7 && segment[7..].All(char.IsAsciiDigit)))
            {
                return true;
            }
        }

        return false;
    }

    private static string Message(string executable, string argument, (string? Name, string Why, string Advice) refused)
    {
        var shown = argument.Length > MaxArgumentLengthInMessages ? argument[..MaxArgumentLengthInMessages] + "..." : argument;
        var what = refused.Name is { } name
            ? "which Chromium (a browser such as Chrome or Edge, an Electron app such as VS Code, or a CEF app, also through a " +
              $"launcher that hands its arguments on) reads as the switch --{name}: {refused.Why}"
            : refused.Why;
        return $"'{executable}' would get the argument '{shown}', {what}. The launch tools do not pass it to any program: the " +
               "server does not run commands for the agent or open a program to remote control. Nothing was launched. " +
               refused.Advice;
    }
}

/// <summary>Why a Chromium profile folder is refused (<see cref="ChromiumSwitchGuard.CheckProfileFolder"/>).</summary>
public enum ProfileFolderProblem
{
    /// <summary>
    /// It is inside a folder named 'User Data' (also by its 8.3 short name), where Chrome, Edge and Brave keep the user's
    /// real profiles with their signed-in sessions.
    /// </summary>
    InsideUserData,

    /// <summary>
    /// It names a folder by an 8.3 short name on a UNC or other device path: expanding it would connect to another computer,
    /// so whether it is a 'User Data' folder cannot be told (fails closed).
    /// </summary>
    UnexpandedShortName
}
