using System.Diagnostics.CodeAnalysis;
using System.Text.RegularExpressions;

namespace WindowsAppManagerMcp.Models;

/// <summary>
/// A packaged (MSIX or Store) app's AppUserModelID (roadmap M6.5): its package family name and the application ID inside
/// the package, joined by '!' ("Microsoft.WindowsCalculator_8wekyb3d8bbwe!App"). launch_application takes it bare or as
/// shell:AppsFolder\&lt;AUMID&gt; and activates the app through IApplicationActivationManager. AppUserModelIDs that desktop
/// apps register (MSEdge, Microsoft.Office.WINWORD.EXE.15) have no package family name and are not packaged apps.
/// </summary>
/// <param name="PackageFamilyName">The package's name and publisher ID, such as Microsoft.WindowsCalculator_8wekyb3d8bbwe.</param>
/// <param name="ApplicationId">The application's ID in the package manifest, such as App.</param>
public sealed partial record PackagedAppId(string PackageFamilyName, string ApplicationId)
{
    /// <summary>The prefix of a shell:AppsFolder launch target.</summary>
    public const string AppsFolderPrefix = @"shell:AppsFolder\";

    /// <summary>The AppUserModelID: the package family name, '!', and the application ID.</summary>
    public string AppUserModelId => $"{PackageFamilyName}!{ApplicationId}";

    /// <summary>The app as a shell:AppsFolder target, which ShellExecute starts too.</summary>
    public string AppsFolderTarget => AppsFolderPrefix + AppUserModelId;

    /// <summary>
    /// Reads a packaged app's AppUserModelID, bare or after shell:AppsFolder\ (any letter case), with surrounding spaces
    /// trimmed. False for anything else, including the AppUserModelIDs of desktop apps.
    /// </summary>
    public static bool TryParse(string? value, [NotNullWhen(true)] out PackagedAppId? app)
    {
        app = null;
        if (string.IsNullOrWhiteSpace(value))
            return false;

        var text = value.Trim();
        if (text.StartsWith(AppsFolderPrefix, StringComparison.OrdinalIgnoreCase))
            text = text[AppsFolderPrefix.Length..];

        var match = AppUserModelIdPattern().Match(text);
        if (!match.Success)
            return false;

        app = new PackagedAppId(match.Groups["family"].Value, match.Groups["app"].Value);
        return true;
    }

    // A package name (3 to 50 letters, digits, dots and dashes), '_', the 13-character publisher ID, '!', and an application
    // ID that starts with a letter
    [GeneratedRegex(@"^(?<family>[A-Za-z0-9][A-Za-z0-9.-]{2,49}_[A-Za-z0-9]{13})!(?<app>[A-Za-z][A-Za-z0-9._-]{0,63})$")]
    private static partial Regex AppUserModelIdPattern();
}
