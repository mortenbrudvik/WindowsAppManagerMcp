using Microsoft.Win32;

namespace WindowsAppManagerMcp.E2ETests.Harness;

/// <summary>
/// The Evergreen Edge WebView2 runtime, which the fixture's WebView2 window needs (roadmap M3.F4): a scenario that opens it
/// is gated with <c>Requires = typeof(WebView2Runtime)</c>, so on a machine without the runtime it is skipped with this
/// reason instead of failing. Read as Microsoft documents it: the runtime's version (pv) under its EdgeUpdate client key,
/// per machine (32- and 64-bit views) or per user. Only reads the registry.
/// </summary>
public sealed class WebView2Runtime : IDesktopTestRequirement
{
    /// <summary>The EdgeUpdate client id of the Evergreen WebView2 runtime.</summary>
    public const string ClientId = "{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}";

    private static readonly (RegistryHive Hive, string Key)[] Keys =
    [
        (RegistryHive.LocalMachine, $@"SOFTWARE\WOW6432Node\Microsoft\EdgeUpdate\Clients\{ClientId}"),
        (RegistryHive.LocalMachine, $@"SOFTWARE\Microsoft\EdgeUpdate\Clients\{ClientId}"),
        (RegistryHive.CurrentUser, $@"Software\Microsoft\EdgeUpdate\Clients\{ClientId}")
    ];

    /// <inheritdoc />
    public string? MissingReason => MissingReasonFor(Keys.Select(k => ReadVersion(k.Hive, k.Key)));

    /// <summary>
    /// Why the scenario cannot run, given the versions the runtime's keys hold (null for a key or value that is not there),
    /// or null when one of them names an installed runtime: not empty and not 0.0.0.0, which the installer leaves behind.
    /// </summary>
    public static string? MissingReasonFor(IEnumerable<string?> versions) =>
        versions.Any(v => !string.IsNullOrWhiteSpace(v) && v.Trim() != "0.0.0.0")
            ? null
            : "The Edge WebView2 runtime is not installed on this machine (no version under EdgeUpdate\\Clients\\" + ClientId +
              "), and the fixture's WebView2 window needs it. Install the Evergreen runtime " +
              "(https://developer.microsoft.com/microsoft-edge/webview2/) to run this scenario.";

    private static string? ReadVersion(RegistryHive hive, string key)
    {
        try
        {
            using var root = RegistryKey.OpenBaseKey(hive, RegistryView.Registry64);
            using var client = root.OpenSubKey(key);
            return client?.GetValue("pv") as string;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            return null;
        }
    }
}
