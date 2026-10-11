using WindowsAppManagerMcp.Models;

namespace WindowsAppManagerMcp.Services;

/// <summary>
/// The UI Automation engine behind every UI Automation read (roadmap M3.2), read from the server's environment when it
/// starts: <see cref="UiaEngine.Uia3"/> (the COM client, with per-call timeouts) by default, or
/// <see cref="UiaEngine.Uia2"/> (the managed System.Windows.Automation client) as a fallback the user can choose if the
/// COM client misbehaves with an application. The tools return the same results with either; get_server_info reports
/// the engine in force.
/// </summary>
/// <param name="Engine">The engine.</param>
/// <param name="Source">"default", or <see cref="EnvironmentVariable"/> when the user chose it.</param>
/// <param name="Warning">Why the variable's value was not understood; null when it was.</param>
public sealed record UiaEngineOptions(UiaEngine Engine, string Source, string? Warning = null)
{
    /// <summary>The environment variable: uia3 or uia2.</summary>
    public const string EnvironmentVariable = "WAM_UIA_ENGINE";

    /// <summary>The UIA3 engine, by default.</summary>
    public static UiaEngineOptions Default { get; } = new(UiaEngine.Uia3, "default");

    /// <summary>
    /// Reads <see cref="EnvironmentVariable"/>: uia3 or uia2 (any letter case) choose the engine; not set or empty is the
    /// default. Any other value keeps the default, with a warning.
    /// </summary>
    public static UiaEngineOptions FromEnvironment(Func<string, string?>? readVariable = null)
    {
        var value = (readVariable ?? Environment.GetEnvironmentVariable)(EnvironmentVariable)?.Trim();
        if (string.IsNullOrEmpty(value))
        {
            return Default;
        }

        if (value.Equals("uia3", StringComparison.OrdinalIgnoreCase))
        {
            return new UiaEngineOptions(UiaEngine.Uia3, EnvironmentVariable);
        }

        if (value.Equals("uia2", StringComparison.OrdinalIgnoreCase))
        {
            return new UiaEngineOptions(UiaEngine.Uia2, EnvironmentVariable);
        }

        return Default with
        {
            Warning = $"{EnvironmentVariable} '{value}' is not uia3 or uia2, so the default UI Automation engine (uia3) is used."
        };
    }
}
