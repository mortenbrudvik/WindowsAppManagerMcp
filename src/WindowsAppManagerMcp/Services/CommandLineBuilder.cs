using System.Text;

namespace WindowsAppManagerMcp.Services;

/// <summary>
/// Builds a Windows command line from individual arguments so that the standard parser
/// (CommandLineToArgvW / the MSVCRT argv rules) splits it back into exactly the same arguments.
/// </summary>
/// <remarks>
/// Rules applied to each argument (the same algorithm .NET uses internally for ArgumentList):
/// <list type="bullet">
/// <item>Arguments without whitespace or quotes are emitted unchanged.</item>
/// <item>Other arguments, including the empty string, are wrapped in double quotes.</item>
/// <item>Inside quotes, embedded quotes are escaped as \", and backslashes that precede a quote
/// (or the closing quote) are doubled. Other backslashes are literal.</item>
/// </list>
/// These rules do not make an argument safe for cmd.exe, which ignores \" escaping. Callers must not
/// pass untrusted arguments to batch files without first rejecting cmd.exe metacharacters.
/// </remarks>
public static class CommandLineBuilder
{
    private const char Quote = '"';
    private const char Backslash = '\\';

    // cmd.exe splits batch file parameters on these characters (in addition to whitespace),
    // and parentheses are significant inside blocks.
    private static readonly char[] CmdDelimiters = [',', ';', '=', '(', ')'];

    /// <summary>
    /// Joins arguments into a single command-line string.
    /// </summary>
    /// <param name="arguments">The arguments, in order. Must not contain null entries.</param>
    /// <param name="quoteCmdDelimiters">
    /// When true (target is a batch file), arguments that contain cmd.exe parameter delimiters
    /// (, ; = ( ) are quoted as well so that each argument stays a single batch parameter.
    /// </param>
    public static string JoinArguments(IEnumerable<string> arguments, bool quoteCmdDelimiters = false)
    {
        var builder = new StringBuilder();
        foreach (var argument in arguments)
        {
            AppendArgument(builder, argument, quoteCmdDelimiters);
        }

        return builder.ToString();
    }

    /// <summary>
    /// Appends one argument, preceded by a space if the builder is not empty.
    /// </summary>
    public static void AppendArgument(StringBuilder builder, string argument, bool quoteCmdDelimiters = false)
    {
        ArgumentNullException.ThrowIfNull(argument);

        if (builder.Length != 0)
        {
            builder.Append(' ');
        }

        if (!NeedsQuotes(argument, quoteCmdDelimiters))
        {
            builder.Append(argument);
            return;
        }

        builder.Append(Quote);

        var index = 0;
        while (index < argument.Length)
        {
            var c = argument[index++];

            if (c == Backslash)
            {
                var backslashCount = 1;
                while (index < argument.Length && argument[index] == Backslash)
                {
                    index++;
                    backslashCount++;
                }

                if (index == argument.Length)
                {
                    // The closing quote follows, so every backslash must be doubled.
                    builder.Append(Backslash, backslashCount * 2);
                }
                else if (argument[index] == Quote)
                {
                    // Backslashes followed by a literal quote: double them and escape the quote.
                    builder.Append(Backslash, backslashCount * 2 + 1);
                    builder.Append(Quote);
                    index++;
                }
                else
                {
                    // Backslashes not followed by a quote are literal.
                    builder.Append(Backslash, backslashCount);
                }

                continue;
            }

            if (c == Quote)
            {
                builder.Append(Backslash);
                builder.Append(Quote);
                continue;
            }

            builder.Append(c);
        }

        builder.Append(Quote);
    }

    private static bool NeedsQuotes(string argument, bool quoteCmdDelimiters)
    {
        if (argument.Length == 0)
        {
            return true;
        }

        foreach (var c in argument)
        {
            if (char.IsWhiteSpace(c) || c == Quote)
            {
                return true;
            }

            if (quoteCmdDelimiters && Array.IndexOf(CmdDelimiters, c) >= 0)
            {
                return true;
            }
        }

        return false;
    }
}
