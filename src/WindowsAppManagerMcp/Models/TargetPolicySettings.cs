using System.Text.RegularExpressions;

namespace WindowsAppManagerMcp.Models;

/// <summary>
/// The server-enforced target policy (roadmap M1.12): the processes whose windows the agent may not act on or read, and
/// optionally the only processes it may. It covers input (with element_action, respond_to_dialog and file_dialog, which
/// the input guard checks), the window changes (focus, move, resize, state, snap, their batches and apply_layout), close,
/// kill, the captures (capture_window, capture_with_elements), the UI Automation reads, and the launch tools' placement and
/// reuseExisting. With the default list it also refuses Windows' "Open File - Security Warning" in any process, recognized
/// by its structure (roadmap M3.F5). While it denies anything, a window whose process cannot be named is refused as well, since it cannot be
/// checked (it fails closed). The user sets it in the server's environment; no tool argument can change it, because the
/// agent acts on untrusted screen and web content and sets its own confirm flags.
/// </summary>
/// <param name="DeniedProcesses">
/// Image names (without ".exe") that are always refused. Default: <see cref="DefaultDeniedProcesses"/>.
/// </param>
/// <param name="AllowedProcesses">
/// When not null, only these image names are allowed (the deny list still wins). Null: no allow list.
/// </param>
/// <param name="DenyListSource">
/// Where <paramref name="DeniedProcesses"/> came from: "default", or <see cref="DenyVariable"/>.
/// </param>
public sealed record TargetPolicySettings(
    IReadOnlyList<string> DeniedProcesses,
    IReadOnlyList<string>? AllowedProcesses,
    string DenyListSource)
{
    /// <summary>
    /// Environment variable with the denied image names, separated by commas or semicolons (a name may
    /// contain spaces; whitespace right after ".exe" also separates). It replaces the default list; the
    /// entry "default" puts the default list back in, and "none" alone turns the deny list off. Names may
    /// include a path or ".exe". The server logs the list in force when it starts.
    /// </summary>
    public const string DenyVariable = "WAM_DENY_PROCESSES";

    /// <summary>
    /// Environment variable with the only image names the agent may act on, separated by commas or
    /// semicolons. Unset: every process that is not denied.
    /// </summary>
    public const string AllowVariable = "WAM_ALLOW_PROCESSES";

    /// <summary>The value of <see cref="DenyListSource"/> when the default list applies.</summary>
    public const string DefaultSource = "default";

    private const string DefaultToken = "default";
    private const string NoneToken = "none";

    /// <summary>
    /// The default deny list: common password managers, whose windows show and type secrets; the Windows
    /// credential and elevation prompts (CredentialUIBroker.exe and CredDialogHost.exe, consent.exe on a
    /// desktop without the secure desktop); the SmartScreen prompt "Windows protected your PC"
    /// (CHXSmartScreen.exe, a UWP app, and smartscreen.exe, the reputation broker that starts it); and the
    /// Windows Security app (SecHealthUI.exe). The UWP ones are matched through the app that their
    /// ApplicationFrameHost frame hosts. A browser extension password manager runs inside the browser and
    /// cannot be told apart by process. Neither can a security prompt that runs inside a shared process, so
    /// while the deny list includes this list, the policy also refuses Windows' "Open File - Security Warning",
    /// whichever process shows it, by its structure (roadmap M3.F5; <see cref="Services.SecurityPrompts"/>).
    /// </summary>
    public static IReadOnlyList<string> DefaultDeniedProcesses { get; } =
    [
        "1Password", "Bitwarden", "KeePass", "KeePassXC", "LastPass", "Dashlane", "Keeper", "KeeperPasswordManager",
        "Enpass", "NordPass", "ProtonPass", "Proton Pass", "RoboForm", "pwsafe", "CredentialUIBroker", "CredDialogHost",
        "consent", "CHXSmartScreen", "smartscreen", "SecHealthUI"
    ];

    /// <summary>
    /// Whitespace right after ".exe" (and an optional closing quote) separates two names: no image name
    /// goes on after its extension, so "KeePass.exe Bitwarden.exe" is two names, not one that matches nothing.
    /// </summary>
    private static readonly Regex SpaceAfterExtension =
        new("(?<=\\.exe\"?)\\s+", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>The policy when nothing is configured: the default deny list and no allow list.</summary>
    public static TargetPolicySettings Default { get; } = new(DefaultDeniedProcesses, null, DefaultSource);

    /// <summary>Reads the policy from the server's environment.</summary>
    public static TargetPolicySettings FromEnvironment() => FromEnvironment(Environment.GetEnvironmentVariable);

    /// <summary>
    /// Reads the policy from <paramref name="readVariable"/>. An unset or empty variable keeps the default.
    /// </summary>
    public static TargetPolicySettings FromEnvironment(Func<string, string?> readVariable)
    {
        ArgumentNullException.ThrowIfNull(readVariable);

        var denyTokens = Tokens(readVariable(DenyVariable));
        IReadOnlyList<string> denied;
        string source;
        if (denyTokens.Count == 0)
        {
            denied = DefaultDeniedProcesses;
            source = DefaultSource;
        }
        else if (denyTokens.Count == 1 && string.Equals(denyTokens[0], NoneToken, StringComparison.OrdinalIgnoreCase))
        {
            denied = [];
            source = DenyVariable;
        }
        else
        {
            denied = Distinct(denyTokens.SelectMany(token =>
                string.Equals(token, DefaultToken, StringComparison.OrdinalIgnoreCase) ? DefaultDeniedProcesses : [token]));
            source = DenyVariable;
        }

        var allowTokens = Tokens(readVariable(AllowVariable));
        var allowed = allowTokens.Count == 0 ? null : Distinct(allowTokens);

        return new TargetPolicySettings(denied, allowed, source);
    }

    /// <summary>
    /// The image name a policy entry or a process name stands for: no quotes, no folder, no ".exe", no
    /// surrounding spaces. Compare the results case-insensitively.
    /// </summary>
    public static string NormalizeProcessName(string name)
    {
        ArgumentNullException.ThrowIfNull(name);

        var trimmed = name.Trim().Trim('"').Trim();
        var separator = trimmed.LastIndexOfAny(['\\', '/']);
        if (separator >= 0)
        {
            trimmed = trimmed[(separator + 1)..];
        }

        return trimmed.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? trimmed[..^4].Trim() : trimmed;
    }

    private static List<string> Tokens(string? value) =>
        string.IsNullOrWhiteSpace(value)
            ? []
            : value.Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .SelectMany(token => SpaceAfterExtension.Split(token))
                .Select(NormalizeProcessName)
                .Where(name => name.Length > 0)
                .ToList();

    private static List<string> Distinct(IEnumerable<string> names) =>
        names.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
}

/// <summary>
/// What the target policy decided for a window or process.
/// </summary>
/// <param name="Allowed">True when the agent may act on the target.</param>
/// <param name="ProcessName">The process name the decision is about; null when allowed.</param>
/// <param name="Reason">Why the target is refused, naming the environment variable; null when allowed.</param>
public sealed record TargetPolicyVerdict(bool Allowed, string? ProcessName, string? Reason)
{
    /// <summary>The target is allowed.</summary>
    public static TargetPolicyVerdict Allow { get; } = new(true, null, null);

    /// <summary>The target is refused.</summary>
    public static TargetPolicyVerdict Deny(string? processName, string reason) => new(false, processName, reason);

    /// <summary>
    /// The target is refused because its process name cannot be read, so it cannot be checked against a policy that
    /// denies anything (see <see cref="Services.Interfaces.TargetPolicyExtensions.EvaluateFailClosed(Services.Interfaces.ITargetPolicy, WindowInfo)"/>).
    /// </summary>
    public static TargetPolicyVerdict Unreadable { get; } =
        Deny(null, "its process name cannot be read, so the window cannot be checked against the policy");
}
