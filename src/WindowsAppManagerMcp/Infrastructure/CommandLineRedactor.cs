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
/// <c>/name:value</c> or <c>--name value</c>, with <c>-</c>, <c>--</c> or <c>/</c>; a following switch is not taken as the
/// value;</item>
/// <item><c>name=value</c> pairs with a secret name in an argument, alone (<c>API_TOKEN=x</c>) or in a connection string or
/// property list (<c>Server=db;Password=x;</c>, <c>/p:Password=x;Configuration=Release</c>);</item>
/// <item>header-like text, <c>Name: value</c>, with a secret name (<c>Authorization: Bearer x</c>,
/// <c>X-Api-Key: x</c>, <c>Cookie: x</c>);</item>
/// <item>the credential after <c>Bearer</c>, and after <c>Basic</c> when it looks like base64;</item>
/// <item>the user info of a URL: the password of <c>scheme://user:password@</c>, or all of it when it has no colon (a
/// token in the user name's place);</item>
/// <item>URL query parameters with a secret name (<c>?sig=</c> of an Azure SAS, <c>X-Amz-Signature</c>,
/// <c>access_token</c>, <c>key</c>);</item>
/// <item>the password of a <c>user:password</c> pair given to <c>-u</c> or <c>--user</c> (curl);</item>
/// <item><c>-pVALUE</c> of the MySQL and MariaDB tools (elsewhere <c>-p</c> is usually a port).</item>
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
/// literal quote); single quotes do not group.
/// </para>
/// </summary>
public static class CommandLineRedactor
{
    /// <summary>What a masked value is replaced with.</summary>
    public const string Mask = "***";

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

    // A switch's name and attached value: --name=value, -name:value, /name:value (the prefix already removed)
    private static readonly Regex ColonValue = new(@"^(?<name>[A-Za-z][A-Za-z0-9_.\-]*):\s*(?<value>.*)$", RegexOptions.Singleline);

    // A switch with no value: --name, -name, /name
    private static readonly Regex BareSwitch = new(@"^(?:--?|/)(?<name>[A-Za-z][A-Za-z0-9_.\-]*)$");

    // name=value at the start of a ;-separated segment, after an optional switch prefix and an optional /p: style
    // property switch
    private static readonly Regex Assignment = new(
        @"^\s*(?:(?:--?|/)(?:[A-Za-z]+:)?)?(?<name>[A-Za-z][A-Za-z0-9_. \-]*?)\s*=\s*(?<value>.*)$", RegexOptions.Singleline);

    private static readonly Regex BearerValue = new(@"\bBearer\s+(?<value>\S+)", RegexOptions.IgnoreCase);

    private static readonly Regex BasicValue = new(@"\bBasic\s+(?<value>[A-Za-z0-9+/]{6,}={0,2})(?=\s|$)", RegexOptions.IgnoreCase);

    private static readonly Regex Base64Like = new(@"^[A-Za-z0-9+/]{6,}={0,2}$");

    private static readonly Regex UrlUserInfo = new(@"\b[A-Za-z][A-Za-z0-9+.\-]*://(?<userinfo>[^/\s@?#]+)@");

    private static readonly Regex QueryParameter = new(@"[?&](?<name>[^=&#\s]+)=(?<value>[^&#\s]*)");

    private static readonly Regex MySqlPassword = new(@"^-p(?<value>.+)$", RegexOptions.Singleline);

    /// <summary>Masks the secrets in <paramref name="commandLine"/>; see the class summary for what counts.</summary>
    public static RedactedCommandLine Redact(string commandLine)
    {
        ArgumentNullException.ThrowIfNull(commandLine);

        var arguments = Split(commandLine);
        var spans = new List<(int Start, int End)>();
        var isMySqlTool = arguments.Count > 0 && IsMySqlTool(arguments[0].Text);

        for (var i = 0; i < arguments.Count; i++)
        {
            var argument = arguments[i];
            var next = i + 1 < arguments.Count ? arguments[i + 1] : null;
            var mySqlArgument = isMySqlTool && i > 0;
            foreach (var (start, length) in SecretsIn(argument.Text, mySqlArgument))
            {
                if (length > 0)
                {
                    spans.Add(argument.ToRaw(start, length));
                }
            }

            // -pVALUE of a MySQL tool is a password with its value attached, not a switch named pVALUE
            var attachedMySqlPassword = mySqlArgument && MySqlPassword.IsMatch(argument.Text);
            if (next != null && !attachedMySqlPassword && TakesTheNextArgument(argument.Text) is { } nextSecret)
            {
                foreach (var (start, length) in nextSecret(next.Text))
                {
                    if (length > 0)
                    {
                        spans.Add(next.ToRaw(start, length));
                    }
                }
            }
        }

        return spans.Count == 0
            ? new RedactedCommandLine(commandLine, false)
            : new RedactedCommandLine(Replace(commandLine, spans), true);
    }

    /// <summary>The secret values inside one argument, as (start, length) in its text.</summary>
    private static IEnumerable<(int Start, int Length)> SecretsIn(string text, bool mySqlArgument)
    {
        var unprefixed = StripSwitchPrefix(text);
        var prefixLength = text.Length - unprefixed.Length;

        // --name:value, /name:value, and header-like Name: value
        var colon = ColonValue.Match(unprefixed);
        if (colon.Success && IsSecretName(colon.Groups["name"].Value))
        {
            var value = colon.Groups["value"];
            yield return (prefixLength + value.Index, value.Length);
        }

        // name=value, alone or in ;-separated segments (connection strings, /p: property lists)
        if (text.Contains('='))
        {
            var segmentStart = 0;
            foreach (var segment in text.Split(';'))
            {
                var assignment = Assignment.Match(segment);
                if (assignment.Success)
                {
                    var name = assignment.Groups["name"].Value;
                    var value = assignment.Groups["value"];
                    if (IsSecretName(name))
                    {
                        yield return (segmentStart + value.Index, value.Length);
                    }
                    else if (IsUserSwitch(name) && value.Value.IndexOf(':') is var at and >= 0)
                    {
                        yield return (segmentStart + value.Index + at + 1, value.Length - at - 1);
                    }
                }

                segmentStart += segment.Length + 1;
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

        if (mySqlArgument && MySqlPassword.Match(text) is { Success: true } password)
        {
            yield return (password.Groups["value"].Index, password.Groups["value"].Length);
        }
    }

    /// <summary>
    /// When <paramref name="text"/> is a switch or a word whose value is the next argument (--password, /token,
    /// Authorization:, Bearer, -u), what to mask in that argument; otherwise null.
    /// </summary>
    private static Func<string, IEnumerable<(int Start, int Length)>>? TakesTheNextArgument(string text)
    {
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
                return next => next.IndexOf(':') is var at and >= 0 && !LooksLikeASwitch(next)
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

    /// <summary>-u and --user, whose value may be user:password (curl).</summary>
    private static bool IsUserSwitch(string name) =>
        name == "u" || string.Equals(name, "user", StringComparison.OrdinalIgnoreCase);

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

    private static bool IsMySqlTool(string program)
    {
        var name = Path.GetFileNameWithoutExtension(program);
        return name.StartsWith("mysql", StringComparison.OrdinalIgnoreCase) ||
               name.StartsWith("mariadb", StringComparison.OrdinalIgnoreCase);
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
