namespace WindowsAppManagerMcp.Services.Interfaces;

/// <summary>
/// The native calls behind packaged app launch and installed-app discovery (roadmap M6.5): activating a packaged app,
/// listing the Applications folder (shell:AppsFolder), and reading which package a process, a package family or a window
/// belongs to. <see cref="ActivateApplication"/> starts a process; every other member only reads. Tests replace it with a
/// fake, so no test activates an app.
/// </summary>
public interface INativeAppsWrapper
{
    /// <summary>
    /// Activates a packaged app through IApplicationActivationManager.ActivateApplication (AO_NOERRORUI), on a
    /// single-threaded apartment thread of its own, and blocks until Windows answers: a cold start with a splash screen
    /// can take seconds, so call it from a thread that may wait (ProcessService abandons it after its activation timeout).
    /// Never throws.
    /// </summary>
    /// <param name="appUserModelId">The packaged app's AppUserModelID (PackageFamilyName!AppId).</param>
    /// <param name="arguments">The command line the app gets, or null for none.</param>
    AppActivationResult ActivateApplication(string appUserModelId, string? arguments);

    /// <summary>
    /// Lists the Applications folder (FOLDERID_AppsFolder: the Start menu's apps, packaged and desktop) on a thread of its
    /// own, reading each item's display name and properties only (never an icon or thumbnail). The shell may load other
    /// programs' property handlers for it, so a list that takes longer than its deadline (3 seconds) is abandoned. Null
    /// when the folder cannot be read or the deadline passed. Never throws.
    /// </summary>
    IReadOnlyList<AppsFolderEntry>? EnumerateAppsFolder();

    /// <summary>
    /// The package family name of a running process (GetPackageFamilyName on a PROCESS_QUERY_LIMITED_INFORMATION handle),
    /// or null when it is not packaged, not running or cannot be opened. Only reads.
    /// </summary>
    string? GetPackageFamilyName(int processId);

    /// <summary>
    /// The AppxManifest.xml text of each package of the family installed for the user (GetPackagesByPackageFamily and
    /// GetPackagePathByFullName, then the file in the package's folder), main and resource packages alike; null when the
    /// family is not installed or cannot be read. Only reads.
    /// </summary>
    IReadOnlyList<string>? ReadPackageManifests(string packageFamilyName);

    /// <summary>
    /// The AppUserModelID a window carries in its property store (SHGetPropertyStoreForWindow, PKEY_AppUserModel_ID), or
    /// null when it has none or it cannot be read. Only reads; the property store is never written.
    /// </summary>
    string? GetWindowAppUserModelId(nint window);
}

/// <summary>
/// What <see cref="INativeAppsWrapper.ActivateApplication"/> got back: the HRESULT, and the activated app's process ID when
/// it succeeded (it may exit at once, when the app hands its request to an instance that was already running).
/// </summary>
public sealed record AppActivationResult(int HResult, int? ProcessId)
{
    public bool Succeeded => HResult >= 0;
}

/// <summary>
/// An item of the Applications folder: its display name (localized), its AppUserModelID (the item's parsing name),
/// the package family name of a packaged app, and the target path of a desktop app's shortcut (PKEY_Link_TargetParsingPath).
/// </summary>
public sealed record AppsFolderEntry(string Name, string AppId, string? PackageFamilyName = null, string? TargetPath = null);
