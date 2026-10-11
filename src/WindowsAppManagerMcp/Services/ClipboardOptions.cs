namespace WindowsAppManagerMcp.Services;

/// <summary>
/// The user's clipboard setting (roadmap M2.16), read from the server's environment when it starts: whether
/// get_clipboard may return what the user or another app copied without asking. No tool argument changes it.
/// </summary>
/// <param name="AllowUserClipboard">
/// True: get_clipboard returns the user's clipboard too (content marked private never). False (the default): only what
/// the agent copied or set, and the user's clipboard only when the user says yes to the question.
/// </param>
/// <param name="Warning">Why the variable's value was not understood, for the log; null when it was.</param>
public sealed record ClipboardOptions(bool AllowUserClipboard, string? Warning = null)
{
    /// <summary>The environment variable.</summary>
    public const string EnvironmentVariable = "WAM_ALLOW_USER_CLIPBOARD";

    private static readonly string[] OnValues = ["1", "true", "yes", "on"];
    private static readonly string[] OffValues = ["0", "false", "no", "off"];

    /// <summary>Off: only the agent's own clipboard content is returned without asking.</summary>
    public static ClipboardOptions Default { get; } = new(false);

    /// <summary>
    /// Reads <see cref="EnvironmentVariable"/>: 1, true, yes or on turn it on; not set, empty, 0, false, no or off leave
    /// it off. Any other value leaves it off, to be safe, with a warning.
    /// </summary>
    public static ClipboardOptions FromEnvironment(Func<string, string?>? readVariable = null)
    {
        var value = (readVariable ?? Environment.GetEnvironmentVariable)(EnvironmentVariable)?.Trim();
        if (string.IsNullOrEmpty(value) || OffValues.Contains(value, StringComparer.OrdinalIgnoreCase))
        {
            return Default;
        }

        return OnValues.Contains(value, StringComparer.OrdinalIgnoreCase)
            ? new ClipboardOptions(true)
            : new ClipboardOptions(false,
                $"{EnvironmentVariable} '{value}' is not 1, true, yes, on, 0, false, no or off, so reading the user's clipboard " +
                "stays off: get_clipboard returns only what the agent copied, unless the user allows more when asked.");
    }
}
