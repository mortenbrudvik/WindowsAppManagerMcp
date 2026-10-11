using System.Text.RegularExpressions;

namespace WindowsAppManagerMcp.Services;

/// <summary>
/// The URI schemes launch_application hands to their registered app (roadmap M2.7): the built-in ones, plus the
/// schemes the user lists in WAM_ALLOWED_PROTOCOLS. The schemes in <see cref="BlockedSchemes"/> are refused whatever
/// the variable says, and file: URIs are always validated as the path they name.
/// </summary>
public sealed partial class ProtocolAllowlist
{
    /// <summary>The environment variable with the user's extra schemes, such as "vscode,ms-teams,spotify".</summary>
    public const string Variable = "WAM_ALLOWED_PROTOCOLS";

    /// <summary>
    /// Schemes that are always allowed. ms-settings opens a Settings page; Store apps start through
    /// shell:AppsFolder\&lt;AppUserModelID&gt;, which is checked on its own (the rest of shell: stays blocked).
    /// </summary>
    public static readonly IReadOnlyList<string> BuiltInSchemes = ["http", "https", "mailto", "tel", "ms-settings"];

    /// <summary>
    /// Schemes that run script or commands, or open arbitrary shell locations or diagnostics: refused even when the
    /// user lists them. shell: is refused except for shell:AppsFolder\&lt;AppUserModelID&gt;.
    /// </summary>
    public static readonly IReadOnlyList<string> BlockedSchemes =
    [
        "javascript", "vbscript", "data", "mshta", "cmd", "powershell", "pwsh", "wscript", "cscript",
        "shell", "ms-msdt", "search-ms", "search"
    ];

    /// <summary>Schemes that are handled apart and cannot be listed: file: URIs are validated as the path they name.</summary>
    private static readonly string[] SpecialSchemes = ["file"];

    private readonly HashSet<string> _extra;

    private ProtocolAllowlist(IReadOnlyList<string> extraSchemes, IReadOnlyList<string> ignoredEntries)
    {
        ExtraSchemes = extraSchemes;
        IgnoredEntries = ignoredEntries;
        _extra = new HashSet<string>(extraSchemes, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>The built-in schemes only.</summary>
    public static ProtocolAllowlist BuiltIn { get; } = new([], []);

    /// <summary>The schemes the user added, lower case, in the order given.</summary>
    public IReadOnlyList<string> ExtraSchemes { get; }

    /// <summary>The entries of the variable that were not added: blocked schemes, file, and text that is not a scheme name.</summary>
    public IReadOnlyList<string> IgnoredEntries { get; }

    /// <summary>Whether a scheme (without the colon) is allowed: built in, or added by the user and not blocked.</summary>
    public bool Allows(string scheme) =>
        !IsBlocked(scheme) &&
        (BuiltInSchemes.Contains(scheme, StringComparer.OrdinalIgnoreCase) || _extra.Contains(scheme));

    /// <summary>Whether a scheme (without the colon) is refused whatever the user allows.</summary>
    public static bool IsBlocked(string scheme) => BlockedSchemes.Contains(scheme, StringComparer.OrdinalIgnoreCase);

    /// <summary>Every allowed scheme, built-in first, for messages.</summary>
    public IEnumerable<string> AllowedSchemes => BuiltInSchemes.Concat(ExtraSchemes);

    /// <summary>Reads <see cref="Variable"/> from the environment (or <paramref name="read"/>).</summary>
    public static ProtocolAllowlist FromEnvironment(Func<string, string?>? read = null) =>
        Parse((read ?? Environment.GetEnvironmentVariable)(Variable));

    /// <summary>
    /// Parses a list of scheme names separated by commas, semicolons or whitespace; a trailing colon is allowed
    /// ("vscode:"). Blocked schemes, file, and entries that are not a scheme name (a letter, then at least one letter,
    /// digit, '+', '-' or '.') are ignored and listed in <see cref="IgnoredEntries"/>.
    /// </summary>
    public static ProtocolAllowlist Parse(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return BuiltIn;
        }

        var extra = new List<string>();
        var ignored = new List<string>();
        foreach (var entry in value.Split([',', ';', ' ', '\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            var scheme = entry.TrimEnd(':');
            if (!SchemeName().IsMatch(scheme) || IsBlocked(scheme) || SpecialSchemes.Contains(scheme, StringComparer.OrdinalIgnoreCase))
            {
                ignored.Add(entry);
                continue;
            }

            if (!BuiltInSchemes.Contains(scheme, StringComparer.OrdinalIgnoreCase) &&
                !extra.Contains(scheme, StringComparer.OrdinalIgnoreCase))
            {
                extra.Add(scheme.ToLowerInvariant());
            }
        }

        return new ProtocolAllowlist(extra, ignored);
    }

    [GeneratedRegex(@"^[a-zA-Z][a-zA-Z0-9+.-]+$")]
    private static partial Regex SchemeName();
}
