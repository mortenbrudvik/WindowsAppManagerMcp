using WindowsAppManagerMcp.Models;

namespace WindowsAppManagerMcp.Services.Interfaces;

/// <summary>
/// The installed apps as the Start menu lists them (roadmap M6.5): list_installed_apps, and the launch's friendly names
/// and argument checks for packaged apps. Reads the Applications folder through <see cref="INativeAppsWrapper"/> at most
/// once in 30 seconds.
/// </summary>
public interface IInstalledAppsService
{
    /// <summary>
    /// The apps whose name or AppUserModelID contains <paramref name="nameFilter"/> (case-insensitive; null or empty for
    /// all), packaged apps first, then by name, each AppUserModelID once, at most <paramref name="limit"/> of them.
    /// </summary>
    InstalledAppsListing List(string? nameFilter, int limit);

    /// <summary>
    /// The apps whose display name is exactly <paramref name="name"/> (case-insensitive, surrounding spaces ignored), each
    /// once (two shortcuts to the same program are one app); null when the folder cannot be read, or some of its items
    /// could not be (one of them may be another app of the name). Never a part of a name:
    /// any program of the user can add a Start menu shortcut, so a near name must not lead to one.
    /// </summary>
    IReadOnlyList<InstalledApp>? FindByName(string name);

    /// <summary>
    /// The app with this AppUserModelID (case-insensitive); null when there is none, or the folder or some of its items
    /// cannot be read.
    /// </summary>
    InstalledApp? FindByAppId(string appId);

    /// <summary>
    /// The file name of the program a packaged app runs (its manifest's Application Executable, such as
    /// WindowsTerminal.exe), or null when it cannot be told: the package is not installed, its manifest cannot be read, or
    /// the app names no executable (a hosted app).
    /// </summary>
    string? ProgramOf(PackagedAppId app);
}

/// <summary>
/// An installed app: its display name (localized, as the Start menu shows it), its AppUserModelID, whether it is a packaged
/// app (launched by <paramref name="AppId"/>), and for a desktop app the program or file its shortcut starts.
/// </summary>
public sealed record InstalledApp(string Name, string AppId, bool Packaged, string? Path);

/// <summary>
/// A list of installed apps: the apps returned, how many matched in all, whether the limit cut the list, whether the
/// Applications folder could not be read (the list is then empty), and how many of its items could not be read (they are
/// missing from the list).
/// </summary>
public sealed record InstalledAppsListing(IReadOnlyList<InstalledApp> Apps, int Total, bool Truncated, bool Unreadable, int SkippedApps = 0);
