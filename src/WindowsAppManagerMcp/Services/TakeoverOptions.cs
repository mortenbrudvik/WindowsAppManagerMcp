using WindowsAppManagerMcp.Models;

namespace WindowsAppManagerMcp.Services;

/// <summary>
/// Takeover detection's mode (roadmap M7.4). The user sets it with <see cref="EnvironmentVariable"/>; the agent cannot
/// change it. Off by default.
/// </summary>
public sealed class TakeoverOptions
{
    /// <summary>The environment variable that sets the mode: off (the default), warn or stop.</summary>
    public const string EnvironmentVariable = "WAM_TAKEOVER_DETECTION";

    /// <summary>The source of a mode that is not configured.</summary>
    public const string DefaultSource = "default";

    private TakeoverOptions(TakeoverMode mode, string source, string? warning)
    {
        Mode = mode;
        Source = source;
        Warning = warning;
    }

    /// <summary>The default: off.</summary>
    public static TakeoverOptions Default { get; } = new(TakeoverMode.Off, DefaultSource, null);

    /// <summary>The mode.</summary>
    public TakeoverMode Mode { get; }

    /// <summary>Where the mode comes from: <see cref="DefaultSource"/> or <see cref="EnvironmentVariable"/>.</summary>
    public string Source { get; }

    /// <summary>Why the configured value was not used as written (stop is used instead); null when it was.</summary>
    public string? Warning { get; }

    /// <summary>The options for <paramref name="mode"/>, as if the user had set it.</summary>
    public static TakeoverOptions For(TakeoverMode mode) => new(mode, EnvironmentVariable, null);

    /// <summary>
    /// Reads <see cref="EnvironmentVariable"/>. Not set or empty: off. "off", "none", "disabled", "false" or "0": off.
    /// "warn": warn. "stop", "on", "true" or "1": stop. Anything else: stop, with <see cref="Warning"/> saying why (the
    /// user asked for detection, so a value that is not a mode keeps the stricter one).
    /// </summary>
    public static TakeoverOptions FromEnvironment(Func<string, string?>? readVariable = null)
    {
        var value = (readVariable ?? Environment.GetEnvironmentVariable)(EnvironmentVariable)?.Trim();
        if (string.IsNullOrEmpty(value))
        {
            return Default;
        }

        switch (value.ToLowerInvariant())
        {
            case "off" or "none" or "disabled" or "false" or "0":
                return new TakeoverOptions(TakeoverMode.Off, EnvironmentVariable, null);
            case "warn":
                return new TakeoverOptions(TakeoverMode.Warn, EnvironmentVariable, null);
            case "stop" or "on" or "true" or "1":
                return new TakeoverOptions(TakeoverMode.Stop, EnvironmentVariable, null);
            default:
                return new TakeoverOptions(TakeoverMode.Stop, EnvironmentVariable,
                    $"{EnvironmentVariable} '{value}' is not a mode (off, warn or stop), so stop is used.");
        }
    }
}
