using System.Text;
using System.Text.RegularExpressions;

namespace WindowsAppManagerMcp.Infrastructure;

/// <summary>A command line after <see cref="CommandLineRedactor.Redact"/>, and whether anything in it was masked.</summary>
public sealed record RedactedCommandLine(string Text, bool Redacted);

/// <summary>
/// Masks the secrets a process's command line commonly carries, so that it can be shown to the agent (roadmap M6.6's
/// list_processes, and later M6.8's layout launch info and M7.5). Pure and heuristic: it knows the usual shapes, and a
/// secret in another shape (a positional password, a base64 blob, a switch with an unusual name) passes through. Callers
/// must keep showing a command line opt-in and narrow, and never claim it is free of secrets.
/// <para>Each masked value becomes <see cref="Mask"/>; everything else, quotes included, is kept as it was. It masks:</para>
/// <list type="bullet">
/// <item>the value of a switch with a secret name (see below), as <c>--name=value</c>, <c>--name:value</c>,
/// <c>/name:value</c> or <c>--name value</c>, with <c>-</c>, <c>--</c> or <c>/</c>; an attached value runs to the end of
/// the argument, a <c>;</c> included (<c>--password=a;b</c>); a following switch is not taken as the value;</item>
/// <item><c>name=value</c> pairs with a secret name in an argument, alone (<c>API_TOKEN=x</c>) or in a connection string or
/// property list (<c>Server=db;Password=x;</c>, <c>/p:Password=x;Configuration=Release</c>), where a value runs on over
/// a <c>;</c> to the next segment that looks like <c>Name=</c> (<c>Password=a;b;Encrypt=true</c> masks <c>a;b</c>);</item>
/// <item>header-like text, <c>Name: value</c>, with a secret name (<c>Authorization: Bearer x</c>,
/// <c>X-Api-Key: x</c>, <c>Cookie: x</c>), also as the value of another switch (wget's
/// <c>--header="Authorization: token x"</c>);</item>
/// <item>the credential after <c>Bearer</c>, and after <c>Basic</c> when it looks like base64;</item>
/// <item>the user info of a URL: the password of <c>scheme://user:password@</c>, or all of it when it has no colon (a
/// token in the user name's place); the user info runs to the last <c>@</c> before the host, as git and curl read it,
/// so an unencoded <c>@</c> in a password is masked too;</item>
/// <item>URL query and fragment parameters with a secret name (<c>?sig=</c> of an Azure SAS, <c>X-Amz-Signature</c>,
/// <c>access_token</c>, <c>key</c>, and the <c>#access_token=</c> of an OAuth implicit-flow redirect);</item>
/// <item>the password of a <c>user:password</c> pair given to <c>-u</c>, <c>--user</c>, <c>-U</c> or
/// <c>--proxy-user</c> (curl), apart or after <c>=</c>, and attached to curl's own <c>-u</c> or <c>-U</c>
/// (<c>-ualice:pw</c>); a drive path (<c>-U C:\x</c>) is no pair;</item>
/// <item><c>-pVALUE</c> of the MySQL and MariaDB tools (elsewhere <c>-p</c> is usually a port);</item>
/// <item><c>-P value</c> and <c>-Pvalue</c> of the SQL Server tools sqlcmd, bcp and osql (also <c>/P value</c>).</item>
/// </list>
/// <para>
/// A name is secret when one of its words (split at <c>-</c>, <c>_</c>, <c>.</c>, spaces and camelCase) is token,
/// password, passwd, pwd, pass, passphrase, secret, key, apikey, auth, authorization, credential, sig, signature or
/// cookie (or a plural), or when a word ends with token, password, passwd, secret, apikey, credential, signature or
/// passphrase (<c>accesstoken</c>, <c>clientsecret</c>). Words that only contain one (keyboard, monkey, author, passive,
/// tokenizer) do not count. A name that starts with "no", or ends with a word that names where or how a secret is kept
/// rather than the secret (file, path, dir, store, type, id, ...: <c>--key-file</c>, <c>--password-store</c>), is not
/// secret.
/// </para>
/// <para>
/// Arguments are split as Windows splits them (CommandLineToArgvW: spaces and tabs outside double quotes, <c>\"</c> a
/// literal quote); single quotes do not group. An argument that holds a space or a tab is also redacted as a command
/// line of its own, its first word naming its tool, so that an inner command line quoted into one argument is covered
/// (<c>cmd /c "app --password x"</c>, npm's <c>cmd /d /s /c "..."</c>, <c>powershell -Command "..."</c>), down to
/// <see cref="MaxNesting"/> levels.
/// </para>
/// <para>
/// A process sets its own command line (up to 32,767 characters), so the patterns run on the non-backtracking engine,
/// in time linear in the argument, and with a match timeout; an argument whose scan times out is masked whole rather
/// than shown unmasked.
/// </para>
/// </summary>
public static class CommandLineRedactor
{
    /// <summary>What a masked value is replaced with.</summary>
    public const string Mask = "***";

    /// <summary>
    /// How many inner command lines deep an argument is redacted again. Each level escapes its quotes once more, so real
    /// command lines stay well inside it; it bounds the work a crafted line can cause.
    /// </summary>
    private const int MaxNesting = 4;

    private static readonly HashSet<string> SecretWords = new(StringComparer.Ordinal)
    {
        "token", "tokens", "password", "passwords", "passwd", "pwd", "pass", "passphrase", "secret", "secrets",
        "key", "keys", "apikey", "apikeys", "auth", "authorization", "credential", "credentials", "sig", "signature",
        "cookie", "cookies"
    };

    /// <summary>Endings that make a run-together word secret (accesstoken, clientsecret, dbpassword).</summary>
    private static readonly string[] SecretWordEndings =
        ["token", "tokens", "password", "passwd", "secret", "apikey", "credential", "credentials", "signature", "passphrase"];

    /// <summary>Last words of a name that say where or how a secret is kept, or which one, rather than being it.</summary>
    private static readonly HashSet<string> NotTheSecretLastWords = new(StringComparer.Ordinal)
    {
        "file", "files", "path", "dir", "directory", "store", "type", "mode", "length", "policy", "prompt", "algorithm",
        "method", "name", "id", "url", "uri", "host", "server", "port", "user", "username", "env", "timeout", "version"
    };

    /// <summary>
    /// How long one pattern may scan one argument. The non-backtracking engine is linear, so this is a backstop; an
    /// argument whose scan runs out is masked whole.
    /// </summary>
    private static readonly TimeSpan MatchTimeout = TimeSpan.FromMilliseconds(100);

    // A switch's name and attached value: --name=value, -name:value, /name:value (the prefix already removed)
    private static readonly Regex ColonValue = Linear(@"^(?<name>[A-Za-z][A-Za-z0-9_.\-]*):\s*(?<value>.*)$", RegexOptions.Singleline);

    // A switch with no value: --name, -name, /name
    private static readonly Regex BareSwitch = Linear(@"^(?:--?|/)(?<name>[A-Za-z][A-Za-z0-9_.\-]*)$");

    // name=value at the start of a ;-separated segment, after an optional switch prefix and an optional /p: style
    // property switch
    private static readonly Regex Assignment = Linear(
        @"^\s*(?:(?<prefix>--?|/)(?<property>[A-Za-z]+:)?)?(?<name>[A-Za-z][A-Za-z0-9_. \-]*?)\s*=\s*(?<value>.*)$",
        RegexOptions.Singleline);

    private static readonly Regex BearerValue = Linear(@"\bBearer\s+(?<value>\S+)", RegexOptions.IgnoreCase);

    private static readonly Regex BasicValue = Linear(@"\bBasic\s+(?<value>[A-Za-z0-9+/]{6,}={0,2})(?:\s|$)", RegexOptions.IgnoreCase);

    private static readonly Regex Base64Like = Linear(@"^[A-Za-z0-9+/]{6,}={0,2}$");

    // The user info runs to the last @ before the host: an @ may be in it, a / ? # or space may not
    private static readonly Regex UrlUserInfo = Linear(@"\b[A-Za-z][A-Za-z0-9+.\-]*://(?<userinfo>[^/\s?#]+)@");

    // A URL query parameter, or a fragment parameter (#access_token= of an OAuth implicit-flow redirect)
    private static readonly Regex QueryParameter = Linear(@"[?&#](?<name>[^=&#\s]+)=(?<value>[^&#\s]*)");

    private static readonly Regex MySqlPassword = Linear(@"^-p(?<value>.+)$", RegexOptions.Singleline);

    private static readonly Regex SqlServerPassword = Linear(@"^-P(?<value>.+)$", RegexOptions.Singleline);

    // curl's -uUSER:PASS and -UUSER:PASS (proxy), the pair attached
    private static readonly Regex CurlUserPassword = Linear(@"^-[uU][^:\s]+:(?<value>.+)$", RegexOptions.Singleline);

    private static readonly Regex DrivePath = Linear(@"^[A-Za-z]:[\\/]");

    /// <summary>The tools whose one-letter switches carry a password.</summary>
    private enum Tool
    {
        Other,
        MySql,
        SqlServer,
        Curl
    }

    private static Regex Linear(string pattern, RegexOptions options = RegexOptions.None) =>
        new(pattern, options | RegexOptions.NonBacktracking | RegexOptions.CultureInvariant, MatchTimeout);

    /// <summary>Masks the secrets in <paramref name="commandLine"/>; see the class summary for what counts.</summary>
    public static RedactedCommandLine Redact(string commandLine)
    {
        ArgumentNullException.ThrowIfNull(commandLine);

        var spans = SpansIn(commandLine, 0);
        return spans.Count == 0
            ? new RedactedCommandLine(commandLine, false)
            : new RedactedCommandLine(Replace(commandLine, spans), true);
    }

    /// <summary>
    /// The secret values of a command line, as (start, end) in it. <paramref name="nesting"/> is how many command lines
    /// <paramref name="line"/> is quoted inside.
    /// </summary>
    private static List<(int Start, int End)> SpansIn(string line, int nesting)
    {
        var arguments = Split(line);
        var spans = new List<(int Start, int End)>();
        var tool = arguments.Count > 0 ? ToolOf(arguments[0].Text) : Tool.Other;

        for (var i = 0; i < arguments.Count; i++)
        {
            var argument = arguments[i];
            var next = i + 1 < arguments.Count ? arguments[i + 1] : null;
            var argumentTool = i > 0 ? tool : Tool.Other;
            foreach (var (start, length) in MaskedWholeOnTimeout(argument.Text, text => SecretsIn(text, argumentTool)))
            {
                if (length > 0)
                {
                    spans.Add(argument.ToRaw(start, length));
                }
            }

            // An inner command line quoted into one argument (cmd /c "app --password x"): its switches are seen only
            // when it is split again. Its spans are in the argument's text, and map back through the argument's
            // positions in this line.
            if (nesting < MaxNesting && argument.Text.AsSpan().IndexOfAny(' ', '\t') >= 0)
            {
                foreach (var (start, end) in SpansIn(argument.Text, nesting + 1))
                {
                    spans.Add(argument.ToRaw(start, end - start));
                }
            }

            if (next == null)
            {
                continue;
            }

            Func<string, IEnumerable<(int Start, int Length)>>? nextSecret;
            try
            {
                // -pVALUE of a MySQL tool and -PVALUE of a SQL Server tool are passwords with the value attached, not
                // switches named pVALUE
                nextSecret = AttachedPassword(argumentTool) is { } attached && attached.IsMatch(argument.Text)
                    ? null
                    : TakesTheNextArgument(argument.Text, argumentTool);
            }
            catch (RegexMatchTimeoutException)
            {
                // Whether this is a secret switch could not be told: the next argument may be its value
                nextSecret = text => [(0, text.Length)];
            }

            if (nextSecret != null)
            {
                foreach (var (start, length) in MaskedWholeOnTimeout(next.Text, nextSecret))
                {
                    if (length > 0)
                    {
                        spans.Add(next.ToRaw(start, length));
                    }
                }
            }
        }

        return spans;
    }

    /// <summary>
    /// The spans <paramref name="find"/> names in <paramref name="text"/>, or all of it when a pattern's scan times out:
    /// a secret that could not be looked for is not shown.
    /// </summary>
    private static List<(int Start, int Length)> MaskedWholeOnTimeout(
        string text, Func<string, IEnumerable<(int Start, int Length)>> find)
    {
        try
        {
            return find(text).ToList();
        }
        catch (RegexMatchTimeoutException)
        {
            return [(0, text.Length)];
        }
    }

    /// <summary>The secret values inside one argument, as (start, length) in its text.</summary>
    private static IEnumerable<(int Start, int Length)> SecretsIn(string text, Tool tool)
    {
        var unprefixed = StripSwitchPrefix(text);
        var prefixLength = text.Length - unprefixed.Length;

        // --name:value, /name:value, and header-like Name: value; a header can also be the value of another switch
        // (/header:Authorization: x)
        var colon = ColonValue.Match(unprefixed);
        if (colon.Success)
        {
            var value = colon.Groups["value"];
            if (IsSecretName(colon.Groups["name"].Value))
            {
                yield return (prefixLength + value.Index, value.Length);
            }
            else if (prefixLength > 0 && HeaderValueIn(value.Value) is var (start, length))
            {
                yield return (prefixLength + value.Index + start, length);
            }
        }

        if (text.Contains('='))
        {
            // A switch with its value attached (--name=value, not /p:name=value): the value runs to the end of the
            // argument, a ; included, since a switch is no connection string
            var assignment = Assignment.Match(text);
            var isSwitch = assignment.Success && assignment.Groups["prefix"].Success && !assignment.Groups["property"].Success;
            if (isSwitch && IsSecretName(assignment.Groups["name"].Value))
            {
                var value = assignment.Groups["value"];
                yield return (value.Index, value.Length);
            }
            else
            {
                if (isSwitch && HeaderValueIn(assignment.Groups["value"].Value) is var (start, length))
                {
                    // wget --header="Authorization: token x"
                    yield return (assignment.Groups["value"].Index + start, length);
                }

                // name=value, alone or in ;-separated segments (connection strings, /p: property lists, and a switch's
                // value that is one: --connection=Server=db;Password=x)
                foreach (var span in AssignmentSecretsIn(text))
                {
                    yield return span;
                }
            }
        }

        foreach (Match bearer in BearerValue.Matches(text))
        {
            yield return (bearer.Groups["value"].Index, bearer.Groups["value"].Length);
        }

        foreach (Match basic in BasicValue.Matches(text))
        {
            yield return (basic.Groups["value"].Index, basic.Groups["value"].Length);
        }

        foreach (Match url in UrlUserInfo.Matches(text))
        {
            var userInfo = url.Groups["userinfo"];
            var at = userInfo.Value.IndexOf(':');
            yield return at < 0
                ? (userInfo.Index, userInfo.Length)
                : (userInfo.Index + at + 1, userInfo.Length - at - 1);
        }

        foreach (Match parameter in QueryParameter.Matches(text))
        {
            if (IsSecretName(parameter.Groups["name"].Value))
            {
                yield return (parameter.Groups["value"].Index, parameter.Groups["value"].Length);
            }
        }

        if (AttachedPassword(tool)?.Match(text) is { Success: true } password)
        {
            yield return (password.Groups["value"].Index, password.Groups["value"].Length);
        }
    }

    /// <summary>
    /// The secret values of the name=value segments of a ;-separated argument. A value runs on over a ; to the next
    /// segment that looks like Name= (a ; inside a password), or to the end of the argument less a trailing ;.
    /// </summary>
    private static IEnumerable<(int Start, int Length)> AssignmentSecretsIn(string text)
    {
        var segments = text.Split(';');
        var starts = new int[segments.Length];
        var assignments = new Match[segments.Length];
        for (int i = 0, start = 0; i < segments.Length; start += segments[i].Length + 1, i++)
        {
            starts[i] = start;
            assignments[i] = Assignment.Match(segments[i]);
        }

        for (var i = 0; i < segments.Length; i++)
        {
            if (!assignments[i].Success)
            {
                continue;
            }

            var next = Array.FindIndex(assignments, i + 1, a => a.Success);
            var end = next >= 0 ? starts[next] - 1 : text.TrimEnd(';').Length;
            var name = assignments[i].Groups["name"].Value;
            var value = assignments[i].Groups["value"];
            var valueStart = starts[i] + value.Index;
            if (IsSecretName(name))
            {
                yield return (valueStart, Math.Max(0, end - valueStart));
            }
            else if (IsUserSwitch(name) && value.Value.IndexOf(':') is var at and >= 0)
            {
                yield return (valueStart + at + 1, Math.Max(0, end - valueStart - at - 1));
            }
        }
    }

    /// <summary>The value of header-like text, <c>Name: value</c>, with a secret name, as (start, length); else null.</summary>
    private static (int Start, int Length)? HeaderValueIn(string text)
    {
        var header = ColonValue.Match(text);
        return header.Success && IsSecretName(header.Groups["name"].Value)
            ? (header.Groups["value"].Index, header.Groups["value"].Length)
            : null;
    }

    /// <summary>The attached password switch of a tool's arguments (-pVALUE, -PVALUE, curl's -uUSER:PASS), or null.</summary>
    private static Regex? AttachedPassword(Tool tool) => tool switch
    {
        Tool.MySql => MySqlPassword,
        Tool.SqlServer => SqlServerPassword,
        Tool.Curl => CurlUserPassword,
        _ => null
    };

    /// <summary>
    /// When <paramref name="text"/> is a switch or a word whose value is the next argument (--password, /token,
    /// Authorization:, Bearer, -u, and -P of a SQL Server tool), what to mask in that argument; otherwise null.
    /// </summary>
    private static Func<string, IEnumerable<(int Start, int Length)>>? TakesTheNextArgument(string text, Tool tool)
    {
        if (tool == Tool.SqlServer && text is "-P" or "/P")
        {
            return next => LooksLikeASwitch(next) ? [] : [(0, next.Length)];
        }

        var bareSwitch = BareSwitch.Match(text);
        if (bareSwitch.Success)
        {
            var name = bareSwitch.Groups["name"].Value;
            if (IsSecretName(name))
            {
                return next => LooksLikeASwitch(next) ? [] : [(0, next.Length)];
            }

            if (IsUserSwitch(name))
            {
                return next => next.IndexOf(':') is var at and >= 0 && !LooksLikeASwitch(next) && !DrivePath.IsMatch(next)
                    ? [(at + 1, next.Length - at - 1)]
                    : [];
            }

            return null;
        }

        // A header name whose value is the next argument (Authorization: Bearer x, unquoted)
        var colon = ColonValue.Match(text);
        if (colon.Success && colon.Groups["value"].Length == 0 && IsSecretName(colon.Groups["name"].Value))
        {
            return next => [(0, next.Length)];
        }

        if (string.Equals(text, "Bearer", StringComparison.OrdinalIgnoreCase))
        {
            return next => [(0, next.Length)];
        }

        if (string.Equals(text, "Basic", StringComparison.OrdinalIgnoreCase))
        {
            return next => Base64Like.IsMatch(next) ? [(0, next.Length)] : [];
        }

        return null;
    }

    /// <summary>Whether a name (of a switch, an assignment, a header or a query parameter) names a secret.</summary>
    internal static bool IsSecretName(string name)
    {
        var words = Words(name);
        if (words.Count == 0 || words[0] == "no")
        {
            return false;
        }

        if (words.Count > 1 && NotTheSecretLastWords.Contains(words[^1]))
        {
            return false;
        }

        return words.Any(word =>
            SecretWords.Contains(word) ||
            SecretWordEndings.Any(ending => word.Length > ending.Length && word.EndsWith(ending, StringComparison.Ordinal)));
    }

    /// <summary>-u, --user, -U and --proxy-user, whose value may be user:password (curl).</summary>
    private static bool IsUserSwitch(string name) =>
        name is "u" or "U" ||
        string.Equals(name, "user", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(name, "proxy-user", StringComparison.OrdinalIgnoreCase);

    /// <summary>The lower-case words of a name, split at anything but letters and digits and at camelCase.</summary>
    private static List<string> Words(string name)
    {
        var words = new List<string>();
        var word = new StringBuilder();
        for (var i = 0; i < name.Length; i++)
        {
            var c = name[i];
            if (!char.IsLetterOrDigit(c))
            {
                Flush();
                continue;
            }

            // A new word starts at an upper-case letter after a lower-case one (apiKey), or at the last upper-case
            // letter of a run that a lower-case one follows (APIKey)
            if (char.IsUpper(c) && word.Length > 0 &&
                (char.IsLower(name[i - 1]) || (i + 1 < name.Length && char.IsLower(name[i + 1]) && char.IsUpper(name[i - 1]))))
            {
                Flush();
            }

            word.Append(char.ToLowerInvariant(c));
        }

        Flush();
        return words;

        void Flush()
        {
            if (word.Length > 0)
            {
                words.Add(word.ToString());
                word.Clear();
            }
        }
    }

    private static string StripSwitchPrefix(string text) =>
        text.StartsWith("--", StringComparison.Ordinal) ? text[2..]
        : text.Length > 0 && text[0] is '-' or '/' ? text[1..]
        : text;

    /// <summary>
    /// Whether an argument is another switch rather than a value: it starts with - (-v, --verbose), or with / and has no
    /// other / or \ (/quiet, but not /home/user/key.pem).
    /// </summary>
    private static bool LooksLikeASwitch(string text) =>
        text.Length > 1 &&
        (text[0] == '-' || (text[0] == '/' && text.IndexOfAny(['/', '\\'], 1) < 0));

    private static Tool ToolOf(string program)
    {
        var name = Path.GetFileNameWithoutExtension(program);
        if (name.StartsWith("mysql", StringComparison.OrdinalIgnoreCase) ||
            name.StartsWith("mariadb", StringComparison.OrdinalIgnoreCase))
        {
            return Tool.MySql;
        }

        if (name.Equals("sqlcmd", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("bcp", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("osql", StringComparison.OrdinalIgnoreCase))
        {
            return Tool.SqlServer;
        }

        return name.Equals("curl", StringComparison.OrdinalIgnoreCase) ? Tool.Curl : Tool.Other;
    }

    private static string Replace(string commandLine, List<(int Start, int End)> spans)
    {
        spans.Sort((a, b) => a.Start.CompareTo(b.Start));
        var result = new StringBuilder(commandLine.Length);
        var position = 0;
        var i = 0;
        while (i < spans.Count)
        {
            // Overlapping spans (a header's value and the Bearer credential inside it) are masked once
            var (start, end) = spans[i];
            for (i++; i < spans.Count && spans[i].Start < end; i++)
            {
                end = Math.Max(end, spans[i].End);
            }

            if (start < position)
            {
                start = position;
            }

            result.Append(commandLine, position, start - position).Append(Mask);
            position = end;
        }

        return result.Append(commandLine, position, commandLine.Length - position).ToString();
    }

    /// <summary>
    /// Splits a command line into arguments as CommandLineToArgvW does (spaces and tabs outside double quotes separate;
    /// a double quote toggles quoting; 2n backslashes before a quote are n backslashes and the quote toggles, 2n+1 are n
    /// backslashes and a literal quote), keeping where each character of an argument is in the line.
    /// </summary>
    private static List<Argument> Split(string line)
    {
        var arguments = new List<Argument>();
        var i = 0;
        while (i < line.Length)
        {
            while (i < line.Length && line[i] is ' ' or '\t')
            {
                i++;
            }

            if (i >= line.Length)
            {
                break;
            }

            var text = new StringBuilder();
            var positions = new List<int>();
            var quoted = false;
            while (i < line.Length && (quoted || line[i] is not (' ' or '\t')))
            {
                if (line[i] == '\\')
                {
                    var run = i;
                    while (run < line.Length && line[run] == '\\')
                    {
                        run++;
                    }

                    var count = run - i;
                    if (run < line.Length && line[run] == '"')
                    {
                        // The kept backslashes stand for their pairs
                        for (var k = 0; k < count / 2; k++)
                        {
                            text.Append('\\');
                            positions.Add(i + 2 * k + 1);
                        }

                        if (count % 2 == 1)
                        {
                            text.Append('"');
                            positions.Add(run);
                            i = run + 1;
                        }
                        else
                        {
                            i = run;
                        }
                    }
                    else
                    {
                        for (var k = 0; k < count; k++)
                        {
                            text.Append('\\');
                            positions.Add(i + k);
                        }

                        i = run;
                    }

                    continue;
                }

                if (line[i] == '"')
                {
                    quoted = !quoted;
                    i++;
                    continue;
                }

                text.Append(line[i]);
                positions.Add(i);
                i++;
            }

            arguments.Add(new Argument(text.ToString(), positions));
        }

        return arguments;
    }

    /// <summary>One argument: its text without the quoting, and where each of its characters is in the line.</summary>
    private sealed record Argument(string Text, List<int> Positions)
    {
        /// <summary>The span of the line, as (start, end), that holds <paramref name="length"/> characters of the text.</summary>
        public (int Start, int End) ToRaw(int start, int length) => (Positions[start], Positions[start + length - 1] + 1);
    }
}
