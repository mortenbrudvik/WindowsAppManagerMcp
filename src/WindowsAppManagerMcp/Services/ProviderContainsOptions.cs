using WindowsAppManagerMcp.Services.Interfaces;

namespace WindowsAppManagerMcp.Services;

/// <summary>
/// Whether the provider narrows a search for a name that contains a text (roadmap M3.F12), read from the server's
/// environment when it starts. Off by default: Windows' substring search (PropertyConditionFlags_MatchSubstring with
/// IgnoreCase) has not been checked on a real desktop, and a provider that compared by locale could leave out a name the
/// server's ordinal test accepts (one with a combining mark after the text, say), which would turn a match into
/// ElementNotFound. Off, nameMatch contains is the server's test alone, as before; on, under the UIA3 engine, the provider
/// is also given <see cref="ElementCriteria.NameNarrowing"/> and the server still tests every element it returns. No tool
/// argument changes it.
/// </summary>
/// <param name="Enabled">True when <see cref="EnvironmentVariable"/> is 1.</param>
public sealed record ProviderContainsOptions(bool Enabled)
{
    /// <summary>The environment variable: 1 turns the narrowing on.</summary>
    public const string EnvironmentVariable = "WAM_UIA_PROVIDER_CONTAINS";

    /// <summary>Off: nameMatch contains is tested on the server alone.</summary>
    public static ProviderContainsOptions Default { get; } = new(false);

    /// <summary>Reads <see cref="EnvironmentVariable"/>: exactly 1 (around spaces) turns it on; anything else leaves it off.</summary>
    public static ProviderContainsOptions FromEnvironment(Func<string, string?>? readVariable = null) =>
        (readVariable ?? Environment.GetEnvironmentVariable)(EnvironmentVariable)?.Trim() == "1" ? new(true) : Default;

    /// <summary>The narrowing for <paramref name="criteria"/> when it is on, else null.</summary>
    public UiaCondition? NarrowingFor(ElementCriteria criteria) => Enabled ? criteria.NameNarrowing : null;
}
