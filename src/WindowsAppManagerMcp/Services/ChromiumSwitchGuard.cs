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
/// inside another switch's value, and the arguments of one argument that holds several (split with the Windows rules), are
/// read too, a few levels deep: a launcher (Squirrel's Update.exe --process-start-args, chrome_proxy.exe) hands them on to
/// the browser. Every program's arguments are checked, not only those of a program <see cref="ChromiumPrograms"/>
/// recognizes: such a launcher or helper is not one, and the switches mean nothing to any other program. Refused:
/// <list type="bullet">
/// <item>every *-cmd-prefix and *-launcher switch (utility, renderer, GPU, plugin) and --browser-subprocess-path, with which
/// Chromium starts its child processes through a command line or program of the caller's choosing;</item>
/// <item>--remote-debugging-port, -pipe, -address, -targets (any --remote-debugging-*) and --remote-allow-origins, which open
/// the program, with the profile it runs with, to remote control: launch_browser_with_debug's own start alone may pass
/// --remote-debugging-port, which it builds next to a separate profile it checked;</item>
/// <item>--load-extension and --disable-extensions-except, which load an unpacked extension: code that runs in the browser
/// with access to its pages;</item>
/// <item>Electron's (Node.js's) --inspect and every --inspect-* (VS Code's --inspect-extensions too), which open a debugger
/// port that runs any code sent to it, and --js-flags;</item>
/// <item>a --user-data-dir (also as "--user-data-dir &lt;folder&gt;", which Electron apps read) inside a folder named 'User
/// Data' or its 8.3 short name, where Chrome, Edge and Brave keep the user's real profiles, as launch_browser_with_debug
/// refuses it; a relative folder is taken in the launch's working directory, and an 8.3 short name is expanded from the
/// file system.</item>
/// </list>
/// A list of switch names, not a sandbox: a switch an app adds of its own (VS Code's --install-extension, for example) is
/// not on it.
/// </remarks>
public sealed class ChromiumSwitchGuard
{
    /// <summary>How deep a switch inside a switch's value, or an argument inside an argument, is read.</summary>
    private const int MaxDepth = 4;

    private const int MaxArgumentLengthInMessages = 80;

    private const string UserDataDirSwitch = "user-data-dir";

    private const string DebugPortSwitch = "remote-debugging-port";

    private static readonly string[] SwitchPrefixes = ["--", "-", "/"];

    private readonly Func<string, string?> _longPath;

    /// <summary>Expands 8.3 short names from the real file system.</summary>
    public ChromiumSwitchGuard()
        : this(InterpreterGuard.LongPathOf)
    {
    }

    /// <param name="longPath">The long form of a fully qualified path that holds a '~', or null when it cannot be read (the
    /// path does not exist).</param>
    public ChromiumSwitchGuard(Func<string, string?> longPath)
    {
        _longPath = longPath;
    }

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
    /// The refused switch in <paramref name="text"/> (its name and why), or null: the text as a switch, the value of that
    /// switch, and each argument of a text that holds several.
    /// </summary>
    private (string Name, string Why, string? Advice)? Refusal(
        string text, string? next, string? workingDirectory, bool debugBrowserLaunch, int depth)
    {
        var trimmed = text.Trim();
        if (SwitchOf(trimmed) is { } found)
        {
            // A name with other characters is no switch Chromium knows ("--foo --gpu-launcher" up to '='); the split below
            // reads the switch inside it
            if (IsSwitchName(found.Name) && Why(found.Name, debugBrowserLaunch) is { } why)
            {
                return (found.Name, why.Why, why.Advice);
            }

            // Chromium takes the value after '='; an Electron app such as VS Code also takes the next argument
            if (found.Name == UserDataDirSwitch && (found.Value ?? next) is { } folder && IsInUserDataFolder(folder, workingDirectory))
            {
                return (found.Name,
                    "it names a folder inside a 'User Data' folder, where Chrome, Edge and Brave keep the user's real profiles, " +
                    "with the user's signed-in sessions",
                    "Leave --user-data-dir out, or pass a separate folder.");
            }

            if (found.Value is { Length: > 0 } value && depth < MaxDepth &&
                Refusal(value, null, workingDirectory, debugBrowserLaunch, depth + 1) is { } inValue)
            {
                return inValue;
            }
        }

        if (depth < MaxDepth && trimmed.Any(char.IsWhiteSpace))
        {
            // A whole command line in one argument, as a launcher hands it on; "x" stands for the program's name, which the
            // split skips
            var parts = ChromiumCommandLine.Split("x " + trimmed).Skip(1).ToList();
            if (parts.Count > 1 || (parts.Count == 1 && parts[0] != trimmed))
            {
                for (var i = 0; i < parts.Count; i++)
                {
                    if (Refusal(parts[i], i + 1 < parts.Count ? parts[i + 1] : null, workingDirectory, debugBrowserLaunch, depth + 1) is { } inPart)
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
    private static (string Why, string? Advice)? Why(string name, bool debugBrowserLaunch)
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
            return ("it loads an unpacked extension: code that runs in the browser with access to its pages", null);
        }

        if (name == "inspect" || name.StartsWith("inspect-", StringComparison.Ordinal) || name == "js-flags")
        {
            return ("it opens an Electron app's Node.js inspector, a debugger port that runs any code sent to it, or passes flags " +
                    "to its JavaScript engine", null);
        }

        return null;
    }

    /// <summary>
    /// Whether <paramref name="value"/> names a folder inside a 'User Data' folder: as written, as a full path (a relative
    /// one taken in <paramref name="workingDirectory"/>), and with its 8.3 short names expanded.
    /// </summary>
    private bool IsInUserDataFolder(string value, string? workingDirectory)
    {
        var path = value.Trim().Trim('"').Trim();
        if (path.Length == 0)
        {
            return false;
        }

        var forms = new List<string> { path };
        try
        {
            var full = Path.IsPathFullyQualified(path) ? Path.GetFullPath(path)
                : !string.IsNullOrEmpty(workingDirectory) && Path.IsPathFullyQualified(workingDirectory) ? Path.GetFullPath(path, workingDirectory)
                : null;
            if (full != null)
            {
                forms.Add(full);
                if (full.Contains('~') && LongFormOf(full) is { } longForm)
                {
                    forms.Add(longForm);
                }
            }
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            // Not a path Windows takes; only its text is checked
        }

        return forms.Any(HasUserDataSegment);
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

    private static string Message(string executable, string argument, (string Name, string Why, string? Advice) refused)
    {
        var shown = argument.Length > MaxArgumentLengthInMessages ? argument[..MaxArgumentLengthInMessages] + "..." : argument;
        return $"'{executable}' would get the argument '{shown}', which Chromium (a browser such as Chrome or Edge, an Electron app " +
               $"such as VS Code, or a CEF app, also through a launcher that hands its arguments on) reads as the switch " +
               $"--{refused.Name}: {refused.Why}. The launch tools do not pass it to any program: the server does not run commands " +
               "for the agent or open a program to remote control. Nothing was launched." +
               (refused.Advice is { } advice ? $" {advice}" : string.Empty);
    }
}
