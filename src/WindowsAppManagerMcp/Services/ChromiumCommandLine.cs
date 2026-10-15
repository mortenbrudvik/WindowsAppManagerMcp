using System.Text;

namespace WindowsAppManagerMcp.Services;

/// <summary>
/// Reads a Chromium browser's switches from its process command line as Chromium does on Windows (roadmap M5.3): the
/// line is split with the CommandLineToArgvW rules, the program's path is skipped, a switch starts with "--", "-" or
/// "/", its name is lower-cased and its value follows the first "=", and the last of a repeated switch wins. Nothing
/// after "--" or "--single-argument" (how the Windows shell passes a URL) is a switch.
/// </summary>
public static class ChromiumCommandLine
{
    private static readonly string[] SwitchPrefixes = ["--", "-", "/"];

    /// <summary>The switches of <paramref name="commandLine"/>, by lower-cased name; a switch without a value maps to "".</summary>
    public static IReadOnlyDictionary<string, string> Parse(string? commandLine)
    {
        var switches = new Dictionary<string, string>(StringComparer.Ordinal);
        var arguments = Split(commandLine);
        foreach (var argument in arguments.Skip(1))
        {
            if (argument is "--" || string.Equals(argument, "--single-argument", StringComparison.OrdinalIgnoreCase))
                break;

            var prefix = SwitchPrefixes.FirstOrDefault(p => argument.Length > p.Length && argument.StartsWith(p, StringComparison.Ordinal));
            if (prefix == null)
                continue;

            var body = argument[prefix.Length..];
            var separator = body.IndexOf('=');
            var name = (separator < 0 ? body : body[..separator]).ToLowerInvariant();
            switches[name] = separator < 0 ? string.Empty : body[(separator + 1)..];
        }

        return switches;
    }

    /// <summary>
    /// Splits a command line into its arguments with the CommandLineToArgvW rules: the program's path runs to the next
    /// quote when it starts with one, else to the first space or tab; after it, 2n backslashes before a quote are n
    /// backslashes and the quote opens or closes a quoted part, 2n+1 are n and a literal quote, and other backslashes are
    /// literal.
    /// </summary>
    public static IReadOnlyList<string> Split(string? commandLine)
    {
        var arguments = new List<string>();
        if (string.IsNullOrEmpty(commandLine))
            return arguments;

        var i = 0;
        var program = new StringBuilder();
        if (commandLine[0] == '"')
        {
            i = 1;
            while (i < commandLine.Length && commandLine[i] != '"')
                program.Append(commandLine[i++]);

            i++;
        }
        else
        {
            while (i < commandLine.Length && !IsBlank(commandLine[i]))
                program.Append(commandLine[i++]);
        }

        arguments.Add(program.ToString());

        while (true)
        {
            while (i < commandLine.Length && IsBlank(commandLine[i]))
                i++;

            if (i >= commandLine.Length)
                break;

            var argument = new StringBuilder();
            var quoted = false;
            while (i < commandLine.Length && (quoted || !IsBlank(commandLine[i])))
            {
                var backslashes = 0;
                while (i < commandLine.Length && commandLine[i] == '\\')
                {
                    backslashes++;
                    i++;
                }

                if (i < commandLine.Length && commandLine[i] == '"')
                {
                    argument.Append('\\', backslashes / 2);
                    if (backslashes % 2 == 1)
                    {
                        argument.Append('"');
                    }
                    else if (quoted && i + 1 < commandLine.Length && commandLine[i + 1] == '"')
                    {
                        // "" inside a quoted part is a literal quote
                        argument.Append('"');
                        i++;
                    }
                    else
                    {
                        quoted = !quoted;
                    }

                    i++;
                }
                else
                {
                    argument.Append('\\', backslashes);
                    if (i < commandLine.Length && (quoted || !IsBlank(commandLine[i])))
                        argument.Append(commandLine[i++]);
                }
            }

            arguments.Add(argument.ToString());
        }

        return arguments;
    }

    private static bool IsBlank(char c) => c is ' ' or '\t';
}
