using System.Xml;
using System.Xml.Linq;
using WindowsAppManagerMcp.Models;
using WindowsAppManagerMcp.Services.Interfaces;

namespace WindowsAppManagerMcp.Services;

/// <summary>
/// See <see cref="IInstalledAppsService"/>. Pure logic over <see cref="INativeAppsWrapper"/>: the folder is read at most once
/// in <see cref="CacheDuration"/> (a read that failed is not kept), and a manifest only parsed, never trusted beyond the
/// program's file name.
/// </summary>
public sealed class InstalledAppsService : IInstalledAppsService
{
    /// <summary>How long a read of the Applications folder is used before it is read again.</summary>
    public static readonly TimeSpan CacheDuration = TimeSpan.FromSeconds(30);

    private readonly INativeAppsWrapper _native;
    private readonly TimeProvider _time;
    private readonly object _sync = new();
    private Snapshot? _snapshot;
    private long _readAt;

    /// <summary>The read in progress, which callers that come meanwhile wait for instead of starting their own (review M6.5 #6).</summary>
    private Lazy<Snapshot?>? _reading;

    /// <summary>One read of the folder: its apps, packaged first and then by name, and how many items could not be read.</summary>
    private sealed record Snapshot(IReadOnlyList<InstalledApp> Apps, int SkippedItems);

    /// <param name="native">Reads the Applications folder and the package manifests.</param>
    /// <param name="time">The clock of the cache; default the system clock.</param>
    public InstalledAppsService(INativeAppsWrapper native, TimeProvider? time = null)
    {
        _native = native;
        _time = time ?? TimeProvider.System;
    }

    public InstalledAppsListing List(string? nameFilter, int limit)
    {
        if (Read() is not { Apps: var apps } snapshot)
            return new InstalledAppsListing([], 0, false, Unreadable: true);

        var matching = string.IsNullOrEmpty(nameFilter)
            ? apps
            : apps.Where(a => a.Name.Contains(nameFilter, StringComparison.OrdinalIgnoreCase) ||
                              a.AppId.Contains(nameFilter, StringComparison.OrdinalIgnoreCase)).ToList();
        var returned = matching.Take(Math.Max(0, limit)).ToList();
        return new InstalledAppsListing(returned, matching.Count, returned.Count < matching.Count, Unreadable: false, snapshot.SkippedItems);
    }

    public IReadOnlyList<InstalledApp>? FindByName(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        if (Complete() is not { } apps)
            return null;

        var wanted = name.Trim();
        return apps
            .Where(a => string.Equals(a.Name.Trim(), wanted, StringComparison.OrdinalIgnoreCase))
            .DistinctBy(a => a.Path ?? a.AppId, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public InstalledApp? FindByAppId(string appId)
    {
        ArgumentNullException.ThrowIfNull(appId);
        return Complete()?.FirstOrDefault(a => string.Equals(a.AppId, appId, StringComparison.OrdinalIgnoreCase));
    }

    public string? ProgramOf(PackagedAppId app)
    {
        ArgumentNullException.ThrowIfNull(app);
        var manifests = _native.ReadPackageManifests(app.PackageFamilyName);
        return manifests?.Select(manifest => ExecutableIn(manifest, app.ApplicationId)).FirstOrDefault(program => program != null);
    }

    /// <summary>
    /// The file name of the Executable of the Application with <paramref name="applicationId"/> in a package manifest, or null
    /// when the manifest is no XML, has a DTD, or has no such application or executable.
    /// </summary>
    private static string? ExecutableIn(string manifest, string applicationId)
    {
        XDocument document;
        try
        {
            using var reader = XmlReader.Create(new StringReader(manifest),
                new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null });
            document = XDocument.Load(reader);
        }
        catch (XmlException)
        {
            return null;
        }

        var executable = document.Descendants()
            .Where(e => e.Name.LocalName == "Application" &&
                        string.Equals((string?)e.Attribute("Id"), applicationId, StringComparison.OrdinalIgnoreCase))
            .Select(e => (string?)e.Attribute("Executable"))
            .FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));
        if (executable == null)
            return null;

        var name = executable.Replace('/', '\\');
        return name[(name.LastIndexOf('\\') + 1)..] is { Length: > 0 } fileName ? fileName : null;
    }

    /// <summary>
    /// The folder's apps when every item could be read; null otherwise. A name or appId is resolved only on a complete
    /// list (review M6.5 #7): an item that was skipped may be another app of the same name.
    /// </summary>
    private IReadOnlyList<InstalledApp>? Complete() => Read() is { SkippedItems: 0 } snapshot ? snapshot.Apps : null;

    /// <summary>
    /// The folder, read at most once in <see cref="CacheDuration"/>; null when it cannot be read (which is not kept). A read
    /// takes up to the wrapper's deadline, so it runs outside the lock, and callers that come meanwhile share it.
    /// </summary>
    private Snapshot? Read()
    {
        Lazy<Snapshot?> reading;
        lock (_sync)
        {
            if (_snapshot != null && _time.GetElapsedTime(_readAt) < CacheDuration)
                return _snapshot;

            reading = _reading ??= new Lazy<Snapshot?>(ReadFolder, LazyThreadSafetyMode.ExecutionAndPublication);
        }

        var snapshot = reading.Value;
        lock (_sync)
        {
            if (ReferenceEquals(_reading, reading))
            {
                _reading = null;
                if (snapshot != null)
                {
                    _snapshot = snapshot;
                    _readAt = _time.GetTimestamp();
                }
            }
        }

        return snapshot;
    }

    private Snapshot? ReadFolder()
    {
        var listing = _native.EnumerateAppsFolder();
        if (listing == null)
            return null;

        var apps = listing.Entries
            .Where(e => !string.IsNullOrWhiteSpace(e.Name) && !string.IsNullOrWhiteSpace(e.AppId))
            .DistinctBy(e => e.AppId, StringComparer.OrdinalIgnoreCase)
            .Select(ToApp)
            .OrderBy(a => a.Packaged ? 0 : 1)
            .ThenBy(a => a.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(a => a.AppId, StringComparer.OrdinalIgnoreCase)
            .ToList();
        return new Snapshot(apps, listing.SkippedItems);
    }

    private static InstalledApp ToApp(AppsFolderEntry entry)
    {
        var packaged = !string.IsNullOrEmpty(entry.PackageFamilyName) && PackagedAppId.TryParse(entry.AppId, out _);
        var path = packaged ? null : entry.TargetPath ?? (IsFullPath(entry.AppId) ? entry.AppId : null);
        return new InstalledApp(entry.Name.Trim(), entry.AppId, packaged, path);
    }

    private static bool IsFullPath(string value)
    {
        try
        {
            return Path.IsPathFullyQualified(value);
        }
        catch (ArgumentException)
        {
            return false;
        }
    }
}
