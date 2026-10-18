using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using WindowsAppManagerMcp.Models;
using WindowsAppManagerMcp.Models.Results;
using WindowsAppManagerMcp.Services.Interfaces;

namespace WindowsAppManagerMcp.Services;

/// <summary>
/// Stores layout presets as JSON files and applies them to the open windows.
/// </summary>
/// <remarks>
/// The folder is read on every lookup (only changed files are re-parsed), so presets added, edited
/// or removed on disk are seen immediately. All file and cache access is serialized by one gate,
/// which makes the singleton safe for parallel tool calls.
/// </remarks>
public class LayoutService : ILayoutService
{
    /// <summary>
    /// Maximum length of a preset name.
    /// </summary>
    public const int MaxNameLength = 128;

    /// <summary>
    /// Written once the folder has been migrated and seeded, so that presets the user deletes
    /// are not migrated or seeded again on the next start.
    /// </summary>
    private const string InitializedMarkerFileName = ".initialized";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,

        // A placement's state is written as "normal", "maximized" or "minimized"; any case is read back. A number is refused
        // (review finding F5): "state": 7 would bind a WindowState that does not exist, and no server ever wrote one
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, allowIntegerValues: false) }
    };

    private static readonly HashSet<string> ReservedDeviceNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9"
    };

    private readonly IWindowService _windowService;
    private readonly IMonitorService _monitorService;
    private readonly IProcessService _processService;
    private readonly IForegroundService _foregroundService;
    private readonly ILaunchPlacementService _launchPlacement;
    private readonly LayoutStorageOptions _options;
    private readonly ILayoutFolderAccess _folderAccess;
    private readonly INativeAppsWrapper? _apps;
    private readonly IInstalledAppsService? _installedApps;
    private readonly ILogger<LayoutService>? _logger;

    private readonly SemaphoreSlim _gate = new(1, 1);

    // Parsed preset files keyed by full path; Preset is null for files that are not valid presets, which keep why (Error)
    private readonly Dictionary<string, CachedPresetFile> _files = new(StringComparer.OrdinalIgnoreCase);
    private bool _storageReady;

    // Whether the last listing of the folder failed, so its warning is logged once per run of failures
    private bool _listingFailed;

    /// <summary>
    /// A file as it was last read: its version (write time and length), and the preset it holds, or why it holds none (with
    /// the name it gives, when its JSON can be read that far). A file is read again only when its version changes, so an
    /// invalid one is logged once per version.
    /// </summary>
    private sealed record CachedPresetFile(DateTime LastWriteTimeUtc, long Length, LayoutPreset? Preset, string? Error = null,
        string? DeclaredName = null);

    /// <summary>
    /// Creates a service that stores presets in <paramref name="layoutsPath"/> without migrating or seeding
    /// anything. When the path is null, the default location from <see cref="LayoutStorageOptions.FromEnvironment"/> is used.
    /// </summary>
    /// <param name="launchPlacement">Places the windows launchMissing launches; see the other constructor.</param>
    public LayoutService(
        IWindowService windowService,
        IMonitorService monitorService,
        IProcessService processService,
        IForegroundService foregroundService,
        string? layoutsPath = null,
        ILaunchPlacementService? launchPlacement = null)
        : this(
            windowService,
            monitorService,
            processService,
            foregroundService,
            layoutsPath == null ? LayoutStorageOptions.FromEnvironment() : LayoutStorageOptions.ForPath(layoutsPath),
            launchPlacement: launchPlacement)
    {
    }

    /// <summary>
    /// Creates a service for the given storage options. The constructor does not touch the file system:
    /// the folder is prepared on first use, so an unusable location is reported as
    /// <see cref="LayoutErrorCode.StorageUnavailable"/> instead of failing service construction.
    /// </summary>
    /// <param name="foregroundService">Brings the windows of placements with focus set to the front, verified.</param>
    /// <param name="folderAccess">Checks that the folder can be listed and written; defaults to the real file system.</param>
    /// <param name="launchPlacement">
    /// Places the windows launchMissing launches, as the launch tools place theirs (roadmap M3.F27). The server passes its
    /// own, which respects the user's pause; by default one over <paramref name="windowService"/> and
    /// <paramref name="foregroundService"/>.
    /// </param>
    /// <param name="apps">
    /// Tells a packaged app's window by its package and the AppUserModelID it carries, so save_layout records the app to
    /// launch rather than a program inside its package folder (roadmap M6.8). Without it every window records its program.
    /// </param>
    /// <param name="installedApps">Finds a packaged app's AppUserModelID among the installed apps when its window carries none.</param>
    /// <param name="logger">Logs a file that is not a valid preset (once per version of it), and a folder that cannot be listed.</param>
    public LayoutService(
        IWindowService windowService,
        IMonitorService monitorService,
        IProcessService processService,
        IForegroundService foregroundService,
        LayoutStorageOptions options,
        ILayoutFolderAccess? folderAccess = null,
        ILaunchPlacementService? launchPlacement = null,
        INativeAppsWrapper? apps = null,
        IInstalledAppsService? installedApps = null,
        ILogger<LayoutService>? logger = null)
    {
        _windowService = windowService;
        _monitorService = monitorService;
        _processService = processService;
        _foregroundService = foregroundService;
        _launchPlacement = launchPlacement ?? new LaunchPlacementService(windowService, foregroundService);
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _folderAccess = folderAccess ?? new LayoutFolderAccess();
        _apps = apps;
        _installedApps = installedApps;
        _logger = logger;
    }

    public string LayoutsPath => _options.LayoutsPath;

    public string? GetStorageError()
    {
        _gate.Wait();
        try
        {
            return EnsureStorage();
        }
        finally
        {
            _gate.Release();
        }
    }

    public IReadOnlyList<LayoutPreset> GetAllPresets()
    {
        _gate.Wait();
        try
        {
            if (EnsureStorage() != null)
                return [];

            RefreshCache();
            return BuildNameIndex().Values
                .Select(entry => entry.Preset)
                .OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        finally
        {
            _gate.Release();
        }
    }

    public LayoutPreset? GetPreset(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return null;

        _gate.Wait();
        try
        {
            return FindPreset(name);
        }
        finally
        {
            _gate.Release();
        }
    }

    public IReadOnlyList<InvalidLayoutFile> GetInvalidFiles()
    {
        _gate.Wait();
        try
        {
            if (EnsureStorage() != null)
                return [];

            RefreshCache();
            return InvalidFiles();
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// What a "not found" message adds about the files that are not valid presets (review finding F1): the ones that may hold
    /// the preset <paramref name="name"/> (the name they give, or their file name as save_layout names files, matches), with
    /// why, and how many others there are. Empty when there are none, so the message stays as it was; it starts with a space
    /// otherwise.
    /// </summary>
    /// <param name="invalidFiles">The folder's invalid files (<see cref="ILayoutService.GetInvalidFiles"/>); null for none known.</param>
    public static string NotFoundNote(string? name, IReadOnlyList<InvalidLayoutFile>? invalidFiles)
    {
        if (invalidFiles is not { Count: > 0 })
            return string.Empty;

        var wanted = name?.Trim() ?? string.Empty;
        var matching = invalidFiles.Where(f => wanted.Length > 0 && StandsFor(f, wanted)).Take(3).ToList();
        var note = string.Concat(matching.Select(f =>
            $" The file '{f.FileName}' in the layouts folder may hold it, but it is not a valid preset: {f.Error} Fix or delete the file."));

        var others = invalidFiles.Count - matching.Count;
        if (others > 0)
        {
            note += others == 1
                ? $" {(matching.Count > 0 ? "1 other file" : "1 file")} in the layouts folder is not a valid preset; list_layouts lists it."
                : $" {others} {(matching.Count > 0 ? "other files" : "files")} in the layouts folder are not valid presets; list_layouts lists them.";
        }

        return note;

        static bool StandsFor(InvalidLayoutFile file, string name)
        {
            var stem = Path.GetFileNameWithoutExtension(file.FileName);
            return string.Equals(file.DeclaredName?.Trim(), name, StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(stem, name, StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(stem, SanitizeFileName(name), StringComparison.OrdinalIgnoreCase);
        }
    }

    public async Task<LayoutSaveResult> SavePresetAsync(LayoutPreset preset, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(preset);

        var nameError = ValidateName(preset.Name);
        if (nameError != null)
            return new LayoutSaveResult(false, preset, ErrorMessage: nameError, ErrorCode: nameof(LayoutErrorCode.InvalidName));

        await _gate.WaitAsync(cancellationToken);
        try
        {
            var storageError = EnsureStorage();
            if (storageError != null)
                return new LayoutSaveResult(false, preset, ErrorMessage: storageError, ErrorCode: nameof(LayoutErrorCode.StorageUnavailable));

            RefreshCache();
            var replaced = BuildNameIndex().TryGetValue(preset.Name, out var existing);

            // Overwrite the file an existing preset came from (which may be hand-named); otherwise pick
            // a file name that no other file uses, so names that sanitize alike never clobber each other.
            var filePath = replaced ? existing.Path : GetAvailableFilePath(preset.Name);
            var tempPath = $"{filePath}.{Guid.NewGuid():N}.tmp";

            try
            {
                Directory.CreateDirectory(LayoutsPath);
                // Version 2, with every version 1 field filled for older servers (roadmap M6.8)
                await File.WriteAllTextAsync(tempPath, JsonSerializer.Serialize(LayoutPresetSchema.ForWriting(preset), JsonOptions),
                    cancellationToken);
                File.Move(tempPath, filePath, overwrite: true);
            }
            catch (OperationCanceledException)
            {
                TryDeleteFile(tempPath);
                throw;
            }
            catch (Exception ex) when (IsFileSystemError(ex))
            {
                TryDeleteFile(tempPath);
                return new LayoutSaveResult(
                    false,
                    preset,
                    replaced,
                    $"Failed to save layout preset '{preset.Name}' to '{filePath}': {ex.Message}",
                    nameof(LayoutErrorCode.SaveFailed));
            }

            // Re-read from disk on the next lookup.
            _files.Remove(filePath);
            return new LayoutSaveResult(true, preset, replaced);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<LayoutDeleteResult> DeletePresetAsync(string name, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return new LayoutDeleteResult(false, name ?? string.Empty, "Layout preset name must not be empty",
                nameof(LayoutErrorCode.InvalidName));
        }

        await _gate.WaitAsync(cancellationToken);
        try
        {
            var storageError = EnsureStorage();
            if (storageError != null)
                return new LayoutDeleteResult(false, name, storageError, nameof(LayoutErrorCode.StorageUnavailable));

            RefreshCache();

            // Delete every file that defines this preset, so it cannot reappear from a duplicate.
            var paths = _files
                .Where(entry => entry.Value.Preset is { } p && string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase))
                .Select(entry => entry.Key)
                .ToList();

            if (paths.Count == 0)
            {
                // A file that is no valid preset is named, never deleted: it is the user's to fix or delete
                var note = NotFoundNote(name, InvalidFiles());
                return new LayoutDeleteResult(false, name, $"Layout preset '{name}' was not found" + (note.Length > 0 ? "." + note : string.Empty),
                    nameof(LayoutErrorCode.PresetNotFound));
            }

            foreach (var path in paths)
            {
                try
                {
                    File.Delete(path);
                    _files.Remove(path);
                }
                catch (Exception ex) when (IsFileSystemError(ex))
                {
                    return new LayoutDeleteResult(false, name, $"Failed to delete layout preset file '{path}': {ex.Message}",
                        nameof(LayoutErrorCode.DeleteFailed));
                }
            }

            return new LayoutDeleteResult(true, name);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<LayoutSaveResult> CaptureCurrentLayoutAsync(
        string name,
        string? description = null,
        string[]? includeProcesses = null,
        string[]? excludeProcesses = null,
        bool includeMinimized = false,
        CancellationToken cancellationToken = default)
    {
        var nameError = ValidateName(name);
        if (nameError != null)
            return new LayoutSaveResult(false, null, ErrorMessage: nameError, ErrorCode: nameof(LayoutErrorCode.InvalidName));

        if (DescriptionError(description) is { } descriptionError)
            return new LayoutSaveResult(false, null, ErrorMessage: descriptionError, ErrorCode: Infrastructure.ToolErrorReporting.InvalidArgumentsErrorCode);

        var windows = _windowService.GetAllWindows(includeMinimized);
        var monitors = _monitorService.GetAllMonitors();
        var placements = new List<WindowPlacement>();
        var ordinals = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var exePaths = new Dictionary<int, string?>();
        var packages = new Dictionary<int, string?>();

        foreach (var window in windows)
        {
            // Filter by process name, the app's or (for UWP windows) the frame host's, as find_windows does
            if (includeProcesses?.Length > 0)
            {
                if (!includeProcesses.Any(p => ProcessNameContains(window, p)))
                    continue;
            }

            if (excludeProcesses?.Length > 0)
            {
                if (excludeProcesses.Any(p => ProcessNameContains(window, p)))
                    continue;
            }

            // Calculate relative position within monitor work area
            var monitor = monitors.ElementAtOrDefault(window.MonitorIndex) ?? monitors[0];
            var workArea = monitor.WorkArea;

            // A maximized or minimized window also records where it returns to (roadmap M6.8). A normal window's restore
            // rectangle is not used: for a snapped window it is where the window was before the snap
            RelativePosition? normalPosition = null;
            if (window.State is WindowState.Maximized or WindowState.Minimized)
            {
                normalPosition = _windowService.GetNormalBounds(window.Handle) is { } normal ? RelativeTo(normal, workArea) : null;

                // A minimized window's rect is its icon, far off screen: without its restore rectangle there is nothing to save
                if (window.State == WindowState.Minimized && normalPosition == null)
                    continue;
            }

            // A minimized window's position is where it returns to, so an older server places it there before minimizing it
            var position = window.State == WindowState.Minimized ? normalPosition! : RelativeTo(window.Bounds, workArea);

            // Which of the process's windows this is, in z-order; apply uses it to break ties
            var ordinal = ordinals.GetValueOrDefault(window.ProcessName);
            ordinals[window.ProcessName] = ordinal + 1;

            // The title of a window the user's policy denies (a password manager's database name) is not written to disk, nor
            // what would start it
            var denied = _windowService.EvaluateTargetPolicy(window.Handle) is { Allowed: false };
            var exePath = ExePathOf(window.ProcessId, exePaths);

            var placement = new WindowPlacement(
                Matcher: new WindowMatcher(
                    TitleContains: null,
                    ProcessName: window.ProcessName,
                    ClassName: string.IsNullOrEmpty(window.ClassName) ? null : window.ClassName
                )
                {
                    ExactProcessName = true,
                    TitlePattern = denied || string.IsNullOrEmpty(window.Title) ? null : Truncate(window.Title, MaxTitlePatternLength),
                    ExePath = exePath,
                    Ordinal = ordinal
                },
                MonitorIndex: window.MonitorIndex,
                Position: position,
                LaunchCommand: null,
                Focus: false,
                Order: placements.Count
            )
            {
                State = window.State,
                // The monitor by identity (roadmap M6.8): its id, which follows it on its port, or its device name without one
                Monitor = monitor.Id ?? monitor.DeviceName,
                MonitorName = monitor.FriendlyName,
                NormalPosition = normalPosition,
                Launch = denied ? null : LaunchOf(window, exePath, packages)
            };

            placements.Add(placement);
        }

        var preset = new LayoutPreset(
            Name: name,
            Description: description,
            Placements: placements
        )
        {
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };

        if (placements.Count == 0)
        {
            // Saving an empty layout would silently replace a good preset with one that does nothing.
            return new LayoutSaveResult(
                false,
                preset,
                ErrorMessage: "No visible windows matched the capture filters, so nothing was saved. Check includeProcesses/excludeProcesses.",
                ErrorCode: nameof(LayoutErrorCode.NoWindowsCaptured));
        }

        return await SavePresetAsync(preset, cancellationToken);
    }

    /// <summary>
    /// A rectangle as fractions of a work area, clamped as save_layout has always clamped it (x and y 0 to 1, sizes 0.1 to 1),
    /// and moved back inside the work area when it reaches past it (<see cref="LayoutPresetSchema.InsideWorkArea"/>), so a
    /// captured preset goes back into save_layout's placements.
    /// </summary>
    private static RelativePosition RelativeTo(WindowRect bounds, MonitorRect workArea) => LayoutPresetSchema.InsideWorkArea(new(
        Math.Clamp((double)(bounds.X - workArea.X) / workArea.Width, 0, 1),
        Math.Clamp((double)(bounds.Y - workArea.Y) / workArea.Height, 0, 1),
        Math.Clamp((double)bounds.Width / workArea.Width, 0.1, 1),
        Math.Clamp((double)bounds.Height / workArea.Height, 0.1, 1)));

    public async Task<LayoutSaveResult> SaveAuthoredLayoutAsync(
        string name,
        string? description,
        IReadOnlyList<LayoutPlacementSpec> placements,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(placements);
        var nameError = ValidateName(name);
        if (nameError != null)
            return new LayoutSaveResult(false, null, ErrorMessage: nameError, ErrorCode: nameof(LayoutErrorCode.InvalidName));

        if (DescriptionError(description) is { } descriptionError)
            return new LayoutSaveResult(false, null, ErrorMessage: descriptionError, ErrorCode: Infrastructure.ToolErrorReporting.InvalidArgumentsErrorCode);

        if (placements.Count is 0 or > LayoutPresetSchema.MaxPlacements)
        {
            return new LayoutSaveResult(false, null,
                ErrorMessage: $"placements takes 1 to {LayoutPresetSchema.MaxPlacements} placements, but it has {placements.Count}. Nothing was saved.",
                ErrorCode: nameof(LayoutErrorCode.InvalidPlacement));
        }

        // Every placement is checked before anything is written (roadmap M6.8)
        var monitors = _monitorService.GetAllMonitors();
        var built = new List<WindowPlacement>(placements.Count);
        for (var i = 0; i < placements.Count; i++)
        {
            if (placements[i] is not { } spec)
            {
                return new LayoutSaveResult(false, null, ErrorMessage: $"placements[{i}] is empty. Nothing was saved.",
                    ErrorCode: nameof(LayoutErrorCode.InvalidPlacement));
            }

            if (!LayoutPresetSchema.TryBuildPlacement(spec, i, monitors, out var placement, out var error))
            {
                return new LayoutSaveResult(false, null, ErrorMessage: $"{error} Nothing was saved.",
                    ErrorCode: nameof(LayoutErrorCode.InvalidPlacement));
            }

            built.Add(placement!);
        }

        var now = DateTime.UtcNow;
        var preset = new LayoutPreset(name, description, built)
        {
            CreatedAt = now,
            UpdatedAt = now,
            AuthoredBy = LayoutPresetSchema.AgentAuthor
        };

        // The user confirms these launches once, and sees every one of them (review M6.8 #1)
        if (LayoutPresetSchema.LaunchTargets(preset) is { Count: > LayoutPresetSchema.MaxLaunchTargets } targets)
        {
            return new LayoutSaveResult(false, null,
                ErrorMessage: $"placements start {targets.Count} different programs or apps, and a layout an agent writes may start at " +
                              $"most {LayoutPresetSchema.MaxLaunchTargets}: the user is asked about them all at once. Nothing was saved.",
                ErrorCode: nameof(LayoutErrorCode.InvalidPlacement));
        }

        return await SavePresetAsync(preset, cancellationToken);
    }

    /// <summary>Why a preset's description cannot be saved: longer than <see cref="LayoutPresetSchema.MaxDescriptionLength"/>.</summary>
    private static string? DescriptionError(string? description) =>
        description?.Length > LayoutPresetSchema.MaxDescriptionLength
            ? $"description is longer than {LayoutPresetSchema.MaxDescriptionLength} characters. Nothing was saved."
            : null;

    #region Launch confirmations (owner decision D15)

    /// <summary>
    /// The file in the layouts folder that keeps the confirmed launches (<see cref="LayoutPresetSchema.LaunchFingerprint"/>),
    /// one per line, so a confirmation holds across restarts. Not a .json file, so it is never read as a preset.
    /// </summary>
    public const string LaunchConfirmationsFileName = ".launch-confirmations";

    private readonly object _confirmationsLock = new();

    // The confirmations as last read from the file, and what the file was then: it is read again when it changes or goes away
    // (review M6.8 #6), so deleting it revokes every confirmation at once
    private HashSet<string> _confirmedLaunches = new(StringComparer.Ordinal);
    private (bool Exists, DateTime LastWriteUtc, long Length)? _confirmationsStamp;

    // Confirmations the file could not keep (it could not be written): they hold until the server stops
    private readonly HashSet<string> _unsavedConfirmations = new(StringComparer.Ordinal);

    public bool AreLaunchesConfirmed(LayoutPreset preset)
    {
        ArgumentNullException.ThrowIfNull(preset);
        if (!LayoutPresetSchema.IsAgentAuthored(preset))
            return true;

        // A preset that starts more than the question shows is never confirmed (review M6.8 #1)
        if (LayoutPresetSchema.ConfirmationRefusal(preset) != null)
            return false;

        var key = LayoutPresetSchema.LaunchFingerprint(preset);
        lock (_confirmationsLock)
        {
            return Confirmations().Contains(key) || _unsavedConfirmations.Contains(key);
        }
    }

    public void ConfirmLaunches(LayoutPreset preset)
    {
        ArgumentNullException.ThrowIfNull(preset);
        var key = LayoutPresetSchema.LaunchFingerprint(preset);
        lock (_confirmationsLock)
        {
            if (Confirmations().Contains(key))
                return;

            try
            {
                // Appended in one write, on a line of its own whatever the file ends with; another server may append too
                using (var stream = new FileStream(ConfirmationsPath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete))
                {
                    var line = System.Text.Encoding.UTF8.GetBytes("\n" + key + "\n");
                    stream.Write(line, 0, line.Length);
                }

                _confirmationsStamp = null;
            }
            catch (Exception ex) when (IsFileSystemError(ex))
            {
                _unsavedConfirmations.Add(key);
            }
        }
    }

    private string ConfirmationsPath => Path.Combine(LayoutsPath, LaunchConfirmationsFileName);

    /// <summary>
    /// The confirmed launches in the file, read again whenever it was written, replaced or deleted since the last read
    /// (callers hold <see cref="_confirmationsLock"/>). Only whole lines of 64 lowercase hex digits count: garbage, a cut-off
    /// line or a blank line confirms nothing.
    /// </summary>
    private HashSet<string> Confirmations()
    {
        (bool Exists, DateTime LastWriteUtc, long Length) stamp;
        try
        {
            var file = new FileInfo(ConfirmationsPath);
            stamp = file.Exists ? (true, file.LastWriteTimeUtc, file.Length) : (false, default, 0);
        }
        catch (Exception ex) when (IsFileSystemError(ex))
        {
            stamp = (false, default, 0);
        }

        if (_confirmationsStamp == stamp)
            return _confirmedLaunches;

        var keys = new HashSet<string>(StringComparer.Ordinal);
        try
        {
            if (stamp.Exists)
            {
                foreach (var line in File.ReadLines(ConfirmationsPath))
                {
                    var key = line.Trim();
                    if (key.Length == 64 && key.All(char.IsAsciiHexDigitLower))
                        keys.Add(key);
                }
            }
        }
        catch (Exception ex) when (IsFileSystemError(ex))
        {
            // An unreadable file confirms nothing
        }

        _confirmedLaunches = keys;
        _confirmationsStamp = stamp;
        return keys;
    }

    #endregion

    /// <summary>How long a layout's launch waits for its window.</summary>
    private const int LaunchWaitTimeoutMs = 5000;

    public async Task<LayoutApplyResult> ApplyPresetAsync(
        string name,
        LayoutMatchBy matchBy = LayoutMatchBy.ProcessAndTitle,
        bool launchMissing = false,
        LayoutApplyOptions? options = null,
        IProgress<OperationProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        options ??= new LayoutApplyOptions();
        var dryRun = options.DryRun;
        string? storageError;
        LayoutPreset? preset = null;
        var notFoundNote = string.Empty;

        await _gate.WaitAsync(cancellationToken);
        try
        {
            storageError = EnsureStorage();
            if (storageError == null && !string.IsNullOrWhiteSpace(name))
                preset = FindPreset(name);
            if (storageError == null && preset == null)
                notFoundNote = NotFoundNote(name, InvalidFiles());
        }
        finally
        {
            _gate.Release();
        }

        if (storageError != null)
        {
            return new LayoutApplyResult(
                Success: false,
                PresetName: name,
                Errors: [storageError],
                ErrorCode: nameof(LayoutErrorCode.StorageUnavailable),
                DryRun: dryRun);
        }

        if (preset == null)
        {
            return new LayoutApplyResult(
                Success: false,
                PresetName: name,
                Errors: [$"Layout preset '{name}' not found" + (notFoundNote.Length > 0 ? "." + notFoundNote : string.Empty)],
                ErrorCode: nameof(LayoutErrorCode.PresetNotFound),
                DryRun: dryRun);
        }

        if (SubsetError(options.Placements, preset.Placements.Count) is { } subsetError)
        {
            return new LayoutApplyResult(
                Success: false,
                PresetName: name,
                Errors: [subsetError],
                ErrorCode: Infrastructure.ToolErrorReporting.InvalidArgumentsErrorCode,
                DryRun: dryRun);
        }

        var monitors = _monitorService.GetAllMonitors();

        // The placements in the order they are applied, each with its place in the preset (the index get_layout lists it at)
        var ordered = preset.Placements.Select((placement, index) => (Placement: placement, Index: index))
            .OrderBy(t => t.Placement.Order)
            .ToList();

        // One snapshot of the user-facing windows, as find_windows lists them; every placement is matched against it, so a window is
        // given to at most one placement. A subset is planned with the whole preset, so each of its placements gets the
        // window it would get in a full apply
        var snapshot = _windowService.GetAllWindows(includeMinimized: true);
        var exePaths = new Dictionary<int, string?>();
        var plans = LayoutPlanner.Plan(ordered.Select(t => t.Placement).ToList(), snapshot, monitors, matchBy,
            processId => ExePathOf(processId, exePaths));
        var assignedTo = plans
            .Where(plan => plan.Window != null)
            .ToDictionary(plan => plan.Window!.Handle, plan => plan.Placement.Order);
        var chosen = Enumerable.Range(0, plans.Count)
            .Where(k => options.Placements == null || options.Placements.Contains(ordered[k].Index))
            .Select(k => plans[k])
            .ToList();

        // An agent wrote this preset: what it would launch needs the user's confirmation for this version of it (D15)
        var launchesConfirmed = AreLaunchesConfirmed(preset);

        var results = new List<LayoutPlacementResult>(chosen.Count);
        List<FocusResult>? focusResults = null;

        // What the placement being applied has done so far, so that a placement that throws still reports its launch
        string? launchCommand = null;
        var launchedWindow = false;
        WindowInfo? placedWindow = null;

        for (var index = 0; index < chosen.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var plan = chosen[index];
            LayoutPlacementResult result;
            string outcome;
            launchCommand = null;
            launchedWindow = false;
            placedWindow = plan.Window;
            try
            {
                (result, outcome) = await ApplyPlacementAsync(plan).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                // One placement's failure never aborts the others; a window it launched is still reported as launched
                result = new LayoutPlacementResult(
                    plan.Placement.Order,
                    LayoutPlacementStatus.Failed,
                    placedWindow is { } window ? LayoutWindowRef.From(window) : null,
                    Launched: launchedWindow ? true : null,
                    ErrorCode: nameof(LayoutErrorCode.ArrangeFailed),
                    Error: $"Could not arrange {plan.Label}: " + (ex is WindowNotRespondingException
                        ? $"{nameof(WindowErrorCode.NotResponding)}: {ex.Message}"
                        : $"{ex.GetType().Name}: {ex.Message}"),
                    LaunchCommand: launchCommand,
                    MonitorFallback: plan.MonitorFallback);
                outcome = "could not be arranged";
            }

            results.Add(result);

            // The client sees each placement's outcome as it happens: a layout that launches applications can take
            // several seconds per placement
            progress?.Report(new OperationProgress(index + 1, chosen.Count,
                $"{index + 1} of {chosen.Count}: {plan.Label} {outcome}"));
        }

        var errors = results.Where(r => r.Error != null).Select(r => r.Error!).ToList();
        if (dryRun)
        {
            return new LayoutApplyResult(
                Success: results.All(r => r.Status is LayoutPlacementStatus.WouldApply or LayoutPlacementStatus.WouldLaunch && r.ErrorCode == null),
                PresetName: name,
                WindowsNotFound: results.Count(r => r.Status == LayoutPlacementStatus.NotFound),
                Errors: errors.Count > 0 ? errors : null,
                ErrorCode: results.FirstOrDefault(r => r.ErrorCode != null)?.ErrorCode,
                Placements: results,
                DryRun: true);
        }

        return new LayoutApplyResult(
            Success: results.All(r => r.Status == LayoutPlacementStatus.Applied && r.ErrorCode == null),
            PresetName: name,
            WindowsArranged: results.Count(r => r.Status == LayoutPlacementStatus.Applied),
            WindowsNotFound: results.Count(r =>
                r.Status == LayoutPlacementStatus.NotFound || r.ErrorCode == nameof(LayoutErrorCode.LaunchFailed)),
            WindowsLaunched: results.Count(r => r.Launched == true),
            Errors: errors.Count > 0 ? errors : null,
            ErrorCode: results.FirstOrDefault(r => r.ErrorCode != null)?.ErrorCode,
            Focus: focusResults,
            Placements: results
        );

        // Arranges one placement and returns what happened to it, and the outcome in words for the progress report
        async Task<(LayoutPlacementResult Result, string Outcome)> ApplyPlacementAsync(PlannedPlacement plan)
        {
            var placement = plan.Placement;
            var label = plan.Label;

            LayoutPlacementResult Result(string status, WindowInfo? window, string? errorCode = null, string? error = null,
                bool launched = false, bool? focused = null, WindowRect? target = null) =>
                new(placement.Order, status, window == null ? null : LayoutWindowRef.From(window),
                    launched ? true : null, focused, errorCode, error, launchCommand,
                    target is { } t ? new BoundsDto(t.X, t.Y, t.Width, t.Height) : null, plan.MonitorFallback);

            if (plan.NotApplicable is { } notApplicable)
            {
                return (Result(LayoutPlacementStatus.NotApplicable, null, nameof(LayoutErrorCode.NoMatchCriterion), notApplicable),
                    "was skipped: it has no criterion matchBy can use");
            }

            if (plan.MonitorError is { } monitorError)
            {
                return (Result(LayoutPlacementStatus.Failed, null, plan.MonitorErrorCode ?? nameof(WindowErrorCode.InvalidMonitorIndex), monitorError),
                    "was not arranged: its monitor does not exist");
            }

            // A rectangle less than a pixel on its monitor is neither arranged nor launched (review finding F2)
            if (plan.PositionError is { } positionError)
            {
                return (Result(LayoutPlacementStatus.Failed, null, nameof(LayoutErrorCode.InvalidPlacement), positionError),
                    "was not arranged: its rectangle is less than a pixel");
            }

            var window = plan.Window;
            var launched = false;
            if (window == null)
            {
                var launchTarget = LayoutPresetSchema.LaunchTarget(placement);
                if (!launchMissing || launchTarget == null)
                {
                    var hint = launchTarget == null ? string.Empty : $"; set launchMissing=true to launch '{launchTarget}'";
                    return (Result(LayoutPlacementStatus.NotFound, null, nameof(LayoutErrorCode.WindowNotFound),
                        $"No open window matched {label}{hint}"), "was not found");
                }

                launchCommand = launchTarget;

                // Planned first, as the launch plans it, without starting anything: a dry run says what the apply would, and the
                // user is never asked about a launch that would be refused (review M6.8 #3)
                var request = new LaunchRequest(launchTarget, ExpectWindow: ExpectedWindow(placement));
                var refusal = LayoutPresetSchema.LaunchTargetError(launchTarget) is { } targetError
                    ? new LaunchRefusal(nameof(LayoutErrorCode.LaunchFailed), targetError)
                    : _processService.CheckLaunch(request);
                if (refusal != null)
                {
                    return (Result(LayoutPlacementStatus.Failed, null, nameof(LayoutErrorCode.LaunchFailed),
                        $"Failed to launch '{launchTarget}' for {label} ({refusal.ErrorCode}): {refusal.Error}"),
                        dryRun ? "would not be launched" : "could not be launched");
                }

                if (!launchesConfirmed)
                {
                    var why = options.LaunchRefusal ?? "apply_layout asks the user first when the client can ask (elicitation)";
                    var notConfirmed = $"Did not launch '{launchTarget}' for {label}: an AI agent wrote this layout, and the user has " +
                                       $"not confirmed that it may start programs ({why}). Ask the user to start it, or to confirm.";
                    return dryRun
                        ? (Result(LayoutPlacementStatus.WouldLaunch, null, nameof(LayoutErrorCode.LaunchNotConfirmed),
                            $"{notConfirmed} apply_layout asks them first when the client can ask."), "would need the user's confirmation to launch")
                        : (Result(LayoutPlacementStatus.Failed, null, nameof(LayoutErrorCode.LaunchNotConfirmed), notConfirmed),
                            "was not launched: the user has not confirmed it");
                }

                if (dryRun)
                    return (Result(LayoutPlacementStatus.WouldLaunch, null, target: DryRunTarget(plan)), "would be launched");

                // Launched as launch_application launches: the program is planned and checked (scripts and interpreters with
                // arguments are refused, a packaged app is activated), and its window is waited for, whichever process
                // owns it (roadmap M6.8, D-PAL-14). A cancelled call stops waiting for its window
                var launchResult = await _processService.LaunchApplicationAsync(
                    request,
                    waitForWindow: true,
                    waitTimeoutMs: LaunchWaitTimeoutMs,
                    cancellationToken: cancellationToken).ConfigureAwait(false);

                if (!launchResult.Success)
                {
                    return (Result(LayoutPlacementStatus.Failed, null, nameof(LayoutErrorCode.LaunchFailed),
                        $"Failed to launch '{launchTarget}' for {label}: {launchResult.ErrorMessage ?? "unknown error"}"),
                        "could not be launched");
                }

                launched = true;
                launchedWindow = true;
                window = launchResult.WindowHandle is { } handle ? _windowService.GetWindowInfo(handle) : null;
                placedWindow = window;
                if (window == null)
                {
                    return (Result(LayoutPlacementStatus.Failed, null, nameof(LayoutErrorCode.LaunchFailed),
                        $"Launched '{launchTarget}' for {label}, but its window could not be found", launched: true),
                        "was launched, but its window was not found");
                }

                // A single-instance application can hand the launch to a window it already has open
                if (!assignedTo.TryAdd(window.Handle, placement.Order))
                {
                    return (Result(LayoutPlacementStatus.Failed, window, nameof(LayoutErrorCode.LaunchFailed),
                        $"Launched '{launchTarget}' for {label}, but the window it brought up, '{window.Title}', " +
                        $"was already given to placement {assignedTo[window.Handle]}", launched: true),
                        "was launched, but its window belongs to another placement");
                }

                // The launch tools' placement (roadmap M3.F27): the same checks before the window is touched (the agent's own
                // host, the user's policy, the pause), the watch for an app that moves its window back, and focus last. A
                // monitor saved by identity is found again by it when the window is placed (roadmap M6.8)
                var placed = (await _launchPlacement.PlaceAsync([(window.Handle, LaunchPlan(plan))], cancellationToken)
                    .ConfigureAwait(false)).Single();
                if (placed.Activation is { } activation)
                    (focusResults ??= []).Add(FocusResult.From(activation));

                return Launched(window, placed);
            }

            // The user's policy: a denied window is neither placed nor focused (restoring or raising it would put it
            // on screen, where a screenshot shows it)
            if (_windowService.EvaluateTargetPolicy(window.Handle) is { Allowed: false } verdict)
            {
                return (Result(LayoutPlacementStatus.Failed, window, nameof(WindowErrorCode.TargetDeniedByPolicy),
                    $"Did not arrange window '{window.Title}' for {label}: {WindowService.WindowChangeRefusal(window.Handle, verdict)}",
                    launched),
                    "was not arranged: the user's policy denies its window");
            }

            if (dryRun)
            {
                // A window its app hid while it is minimized or maximized is refused by the arrangement, which would show it
                // (review M6.8 #3): the dry run says so too
                return HiddenRefusal(window, plan) is { } hidden
                    ? (Result(LayoutPlacementStatus.Failed, window, nameof(LayoutErrorCode.ArrangeFailed),
                        $"Failed to arrange window '{window.Title}' for {label}: {nameof(WindowErrorCode.MoveFailed)}: {hidden}"),
                        "would not be arranged: its window is hidden")
                    : (Result(LayoutPlacementStatus.WouldApply, window, target: DryRunTarget(plan)), "would be arranged");
            }

            // A window that is minimized and stays minimized is given its restore rectangle without being shown (roadmap M6.8)
            var keptMinimized = placement.State == WindowState.Minimized && window.State == WindowState.Minimized && plan.Snap == null;
            string? notPlaced;
            try
            {
                var change = Arrange(window, plan, keptMinimized);
                notPlaced = change.Success ? null : $"{change.ErrorCode}: {change.Error}";
            }
            catch (WindowNotRespondingException ex)
            {
                // The application is hung or busy: this placement fails, and the others still run
                notPlaced = $"{nameof(WindowErrorCode.NotResponding)}: {ex.Message}";
            }

            if (notPlaced != null)
            {
                return (Result(LayoutPlacementStatus.Failed, window, nameof(LayoutErrorCode.ArrangeFailed),
                    $"Failed to arrange window '{window.Title}' for {label}: {notPlaced}", launched),
                    "could not be arranged");
            }

            var outcome = "was arranged";
            string? errorCode = null;
            string? error = null;
            bool? focused = null;

            if (placement.State == WindowState.Minimized && !keptMinimized)
            {
                WindowChangeResult minimized;
                try
                {
                    minimized = _windowService.SetWindowState(window.Handle, WindowStateRequest.Minimize);
                }
                catch (WindowNotRespondingException ex)
                {
                    // The window was arranged: the placement stays applied, with the minimize's failure
                    minimized = WindowChangeResult.Failed(window.Handle, WindowErrorCode.NotResponding, ex.Message);
                }

                if (!minimized.Success)
                {
                    outcome = "was arranged, but could not be minimized";
                    errorCode = nameof(LayoutErrorCode.ArrangeFailed);
                    error = $"Arranged window '{window.Title}' for {label}, but could not minimize it ({minimized.ErrorCode}): {minimized.Error}";
                }
            }

            if (Focuses(placement))
            {
                var focus = _foregroundService.EnsureForeground(window.Handle);
                (focusResults ??= []).Add(FocusResult.From(focus));
                focused = focus.IsForeground;
                if (focus.IsForeground)
                {
                    outcome = "was arranged and brought to the front";
                }
                else
                {
                    outcome = "was arranged, but could not be brought to the front";
                    errorCode ??= nameof(LayoutErrorCode.ArrangeFailed);
                    error ??= $"Arranged window '{window.Title}' for {label}, but could not bring it to the foreground " +
                              $"({focus.ErrorCode}): {focus.Error}";
                }
            }

            // Small delay between window operations
            await Task.Delay(50, cancellationToken).ConfigureAwait(false);
            return (Result(LayoutPlacementStatus.Applied, window, errorCode, error, launched, focused), outcome);

            // What the placement service did with a launched window, as apply_layout reports an open window's placement: a
            // window that was not placed fails, one that was placed but not minimized, kept in place or focused is applied
            // with ArrangeFailed, and so is one the app kept moving back (apply_layout has no warnings)
            (LayoutPlacementResult Result, string Outcome) Launched(WindowInfo window, LaunchPlacementOutcome placed)
            {
                var code = placed.ErrorCode.ToString();
                bool? focused = Focuses(placement) ? placed.Activation?.IsForeground == true : null;
                return placed.FailedStep switch
                {
                    LaunchPlacementStep.None when placed.Warning is { } warning => (
                        Result(LayoutPlacementStatus.Applied, window, nameof(LayoutErrorCode.ArrangeFailed),
                            $"Arranged window '{window.Title}' for {label}, but its app kept moving it back: {warning}",
                            launched: true, focused: focused),
                        "was arranged, but its app kept moving it back"),
                    LaunchPlacementStep.None => (
                        Result(LayoutPlacementStatus.Applied, window, launched: true, focused: focused),
                        focused == true ? "was arranged and brought to the front" : "was arranged"),
                    LaunchPlacementStep.Check when placed.ErrorCode == WindowErrorCode.TargetDeniedByPolicy => (
                        Result(LayoutPlacementStatus.Failed, window, code, $"Did not arrange window '{window.Title}' for {label}: {placed.Error}",
                            launched: true),
                        "was not arranged: the user's policy denies its window"),
                    LaunchPlacementStep.Check => (
                        Result(LayoutPlacementStatus.Failed, window, nameof(LayoutErrorCode.ArrangeFailed),
                            $"Did not arrange window '{window.Title}' for {label}: {code}: {placed.Error}", launched: true),
                        "was not arranged"),
                    LaunchPlacementStep.Place => (
                        Result(LayoutPlacementStatus.Failed, window, nameof(LayoutErrorCode.ArrangeFailed),
                            $"Failed to arrange window '{window.Title}' for {label}: {code}: {placed.Error}", launched: true),
                        "could not be arranged"),
                    LaunchPlacementStep.Settle when placed.ErrorCode == WindowErrorCode.InvalidHandle => (
                        Result(LayoutPlacementStatus.Failed, window, nameof(LayoutErrorCode.ArrangeFailed),
                            $"Failed to arrange window '{window.Title}' for {label}: {code}: {placed.Error}", launched: true),
                        "was arranged, but its window closed"),
                    LaunchPlacementStep.Minimize => (
                        Result(LayoutPlacementStatus.Applied, window, nameof(LayoutErrorCode.ArrangeFailed),
                            $"Arranged window '{window.Title}' for {label}, but could not minimize it ({code}): {placed.Error}", launched: true),
                        "was arranged, but could not be minimized"),
                    LaunchPlacementStep.Focus => (
                        Result(LayoutPlacementStatus.Applied, window, nameof(LayoutErrorCode.ArrangeFailed),
                            $"Arranged window '{window.Title}' for {label}, but could not bring it to the foreground ({code}): {placed.Error}",
                            launched: true, focused: false),
                        "was arranged, but could not be brought to the front"),
                    // Settle: the app moved the window back, and it was not placed again (paused, or the placement failed)
                    _ => (
                        Result(LayoutPlacementStatus.Applied, window, nameof(LayoutErrorCode.ArrangeFailed),
                            $"Arranged window '{window.Title}' for {label}, but {code}: {placed.Error}", launched: true),
                        "was arranged, but its app moved it back")
                };
            }
        }
    }

    /// <summary>
    /// Whether the placement's window is brought to the front: when it asks to be, unless its state is minimized. A layout an
    /// agent writes cannot have both (save_layout refuses them); a file edited by hand can, and then minimized wins, for an open
    /// window as for one apply_layout launches (<see cref="LaunchPlacementPlan.ForLayout"/>), so the window the layout
    /// minimizes is not brought back to the front.
    /// </summary>
    private static bool Focuses(WindowPlacement placement) => placement.Focus && placement.State != WindowState.Minimized;

    /// <summary>
    /// Why a subset of placements cannot be applied: an index that is not a placement of the preset, or one given twice. Null
    /// for no subset, or a valid one.
    /// </summary>
    private static string? SubsetError(IReadOnlyList<int>? subset, int count)
    {
        if (subset == null)
            return null;

        if (subset.Count == 0)
            return "placements is empty: leave it out to apply every placement, or name at least one. Nothing was arranged.";

        var outside = subset.Where(i => i < 0 || i >= count).ToList();
        if (outside.Count > 0)
        {
            var bad = outside[0];
            return $"placements names {bad}, but the preset has {count} placement{(count == 1 ? string.Empty : "s")} (0 to {count - 1}, as " +
                   "get_layout lists them). Nothing was arranged.";
        }

        return subset.Distinct().Count() == subset.Count
            ? null
            : "placements names a placement twice. Nothing was arranged.";
    }

    /// <summary>
    /// Why the arrangement would refuse the window, as the window service does: it is hidden (its app hid it, in the notification
    /// area, say) and minimized or maximized, so restoring it or setting its restore rectangle would show it. A window maximized
    /// on its monitor that stays so is left alone and not refused. Null when it is not refused.
    /// </summary>
    private static string? HiddenRefusal(WindowInfo window, PlannedPlacement plan)
    {
        if (window.IsVisible || window.State is not (WindowState.Minimized or WindowState.Maximized))
            return null;

        var leftAlone = plan.Snap == null && plan.Placement.State == WindowState.Maximized && window.State == WindowState.Maximized &&
                        window.MonitorIndex == plan.Monitor?.Index;
        return leftAlone
            ? null
            : $"The window is hidden and {window.State.ToString().ToLowerInvariant()}: its application hid it (for example in the " +
              "notification area), and arranging it would show it, so it would not be arranged. Show it through its application, or ask the user.";
    }

    /// <summary>
    /// Where a dry run says the window would go: the restore rectangle of a maximized placement (the work area when it saved
    /// none) or of a minimized one, else its rectangle.
    /// </summary>
    private static WindowRect? DryRunTarget(PlannedPlacement plan) => plan.Placement.State switch
    {
        WindowState.Maximized => plan.NormalTarget ?? (plan.WorkArea is { } area ? new WindowRect(area.X, area.Y, area.Width, area.Height) : null),
        WindowState.Minimized => plan.NormalTarget ?? plan.Target,
        _ => plan.Target
    };

    /// <summary>
    /// The window a layout's launch expects: one of the placement's process (part of its name), which tells the launch's window
    /// apart when the program hands the launch to another process or app (roadmap M6.8). Null without a process name, and for
    /// one that names none once normalized ('.exe', review finding F4): the launch would take a new window of any process. The
    /// planner refuses such a placement before it launches; this keeps the hint from ever being looser than the matcher.
    /// </summary>
    private static LaunchWindowHint? ExpectedWindow(WindowPlacement placement) =>
        LayoutPlanner.NamesAProcess(placement.Matcher.ProcessName) ? new LaunchWindowHint(ProcessName: placement.Matcher.ProcessName) : null;

    /// <summary>
    /// The launch placement plan of a placement whose window was launched (roadmap M3.F27), as <see cref="Arrange"/> places an
    /// open window: snapped on its monitor; a maximized placement given its restore rectangle and then maximized (maximized on
    /// its monitor when it has none); any other given the placement's rectangle as the window rect
    /// (<see cref="BoundsFrame.Outer"/>), the rect layouts save, and minimized afterwards for state minimized. Every plan keeps
    /// the monitor's id (or device name), and a rectangle also as fractions of the work area, so the window is placed on the
    /// monitor as it is when the window appears (roadmap M6.8, review #5). The plan is made by
    /// <see cref="LaunchPlacementPlan.ForLayout"/>, which states and enforces the layout's rules; a placement with focus and
    /// state minimized stays minimized and is not focused, as <see cref="Focuses"/> has it for an open window.
    /// </summary>
    private static LaunchPlacementPlan LaunchPlan(PlannedPlacement plan)
    {
        var placement = plan.Placement;
        var monitor = plan.Monitor!;
        var minimized = placement.State == WindowState.Minimized ? LaunchWindowState.Minimized : (LaunchWindowState?)null;
        var key = monitor.Id ?? monitor.DeviceName;
        if (plan.Snap is { } snap)
            return LaunchPlacementPlan.ForLayout(key, monitor.Index, snap: snap, state: minimized, focus: placement.Focus);

        // A maximized placement with a restore rectangle is given it before it is maximized, as an open window is
        if (placement.State == WindowState.Maximized && plan.NormalTarget is null)
            return LaunchPlacementPlan.ForLayout(key, monitor.Index, state: LaunchWindowState.Maximized, focus: placement.Focus);

        // The rectangle as fractions too, so it is placed on the monitor's work area as it is when the window appears
        var maximized = placement.State == WindowState.Maximized;
        return LaunchPlacementPlan.ForLayout(key, monitor.Index,
            bounds: maximized ? plan.NormalTarget : plan.Target,
            relativeBounds: maximized ? placement.NormalPosition : placement.Position,
            state: maximized ? LaunchWindowState.Maximized : minimized,
            focus: placement.Focus);
    }

    /// <summary>
    /// Puts the window where the placement says, through the window service's verified geometry (roadmap M2.3, M6.8): snapped
    /// on its monitor; a maximized placement first given its restore rectangle, then maximized, so it restores to that
    /// rectangle (one already maximized on its monitor is left alone; one without a restore rectangle is maximized on its
    /// monitor); a window that is minimized and stays minimized given its restore rectangle without being shown
    /// (<paramref name="keptMinimized"/>); any other window given the placement's rectangle. SetWindowBounds restores a
    /// minimized or maximized window to normal first, without activating it (SW_SHOWNOACTIVATE); maximizing activates the
    /// window (Windows has no maximize that does not).
    /// </summary>
    private WindowChangeResult Arrange(WindowInfo window, PlannedPlacement plan, bool keptMinimized)
    {
        var handle = window.Handle;
        var monitor = plan.Monitor!;
        if (plan.Snap is { } snap)
            return _windowService.SnapWindow(handle, snap, monitor.Index);

        if (keptMinimized)
        {
            var restore = plan.NormalTarget ?? plan.Target!;
            return _windowService.SetMinimizedBounds(handle, restore.X, restore.Y, restore.Width, restore.Height);
        }

        if (plan.Placement.State == WindowState.Maximized)
        {
            if (window.State == WindowState.Maximized && window.MonitorIndex == monitor.Index)
                return WindowChangeResult.Done(handle);

            if (plan.NormalTarget is not { } normal)
                return _windowService.MoveWindowToMonitor(handle, monitor.Index, MonitorPositioning.Maximize);

            var placed = _windowService.SetWindowBounds(handle, normal.X, normal.Y, normal.Width, normal.Height);
            return placed.Success ? _windowService.SetWindowState(handle, WindowStateRequest.Maximize) : placed;
        }

        var target = plan.Target!;
        return _windowService.SetWindowBounds(handle, target.X, target.Y, target.Width, target.Height);
    }

    #region Capture helpers

    /// <summary>The longest title save_layout records.</summary>
    private const int MaxTitlePatternLength = 260;

    /// <summary>
    /// True when <paramref name="name"/> is part of the window's process name or, for a UWP window, of
    /// its frame host's (ApplicationFrameHost). Presets saved before UWP windows were attributed to the app
    /// store ApplicationFrameHost as their process name.
    /// </summary>
    private static bool ProcessNameContains(WindowInfo window, string name) =>
        WindowMatching.ProcessNameMatches(window.ProcessName, window.HostProcessName, name, contains: true);

    /// <summary>The process's executable path, read once per process; null when it cannot be read.</summary>
    private string? ExePathOf(int processId, Dictionary<int, string?> cache)
    {
        if (cache.TryGetValue(processId, out var cached))
            return cached;

        string? path;
        try
        {
            path = _processService.GetProcessPath(processId);
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or ArgumentException
                                       or UnauthorizedAccessException or NotSupportedException)
        {
            path = null;
        }

        cache[processId] = string.IsNullOrEmpty(path) ? null : path;
        return cache[processId];
    }

    /// <summary>
    /// What launchMissing would start for a captured window (roadmap M6.8, owner decision D15; never arguments): a packaged
    /// app's AppUserModelID, the one its window carries or its package's only app among the installed apps, since a program
    /// in a package's folder cannot be started directly; any other window's program. Null when it cannot be told: a packaged
    /// app with several apps and no AppUserModelID on its window, or a program whose path cannot be read.
    /// </summary>
    private LayoutLaunch? LaunchOf(WindowInfo window, string? exePath, Dictionary<int, string?> packages)
    {
        if (!packages.TryGetValue(window.ProcessId, out var family))
            packages[window.ProcessId] = family = _apps?.GetPackageFamilyName(window.ProcessId);

        if (family == null)
            return exePath == null ? null : new LayoutLaunch(Executable: exePath);

        if (PackagedAppId.TryParse(_apps!.GetWindowAppUserModelId(window.Handle), out var app) &&
            string.Equals(app.PackageFamilyName, family, StringComparison.OrdinalIgnoreCase))
        {
            return new LayoutLaunch(AppId: app.AppUserModelId);
        }

        var prefix = family + "!";
        var ofPackage = _installedApps?.List(prefix, MaxPackageApps).Apps
            .Where(a => a.Packaged && a.AppId.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            .ToList();
        return ofPackage is { Count: 1 } ? new LayoutLaunch(AppId: ofPackage[0].AppId) : null;
    }

    /// <summary>How many installed apps of one package are read to find the one a window belongs to.</summary>
    private const int MaxPackageApps = 10;

    private static string Truncate(string text, int maxLength) => text.Length <= maxLength ? text : text[..maxLength];

    #endregion

    #region Storage (callers must hold _gate)

    /// <summary>
    /// Creates the folder, checks that it can be listed and written, and the first time, migrates
    /// legacy presets and seeds the samples. Returns null when storage is usable, otherwise why it
    /// is not. Retries on every call until it succeeds.
    /// </summary>
    private string? EnsureStorage()
    {
        if (_storageReady)
            return null;

        if (!Path.IsPathFullyQualified(LayoutsPath))
        {
            return $"Layout storage folder '{LayoutsPath}' is not an absolute path. " +
                   $"Set the {LayoutStorageOptions.LayoutsPathEnvironmentVariable} environment variable to an absolute folder path.";
        }

        try
        {
            Directory.CreateDirectory(LayoutsPath);
        }
        catch (Exception ex) when (IsFileSystemError(ex))
        {
            return $"Layout storage folder '{LayoutsPath}' could not be created: {ex.Message} " +
                   $"Set the {LayoutStorageOptions.LayoutsPathEnvironmentVariable} environment variable to a writable folder.";
        }

        // CreateDirectory also succeeds for an existing folder this user cannot use, which would
        // otherwise look empty to list_layouts and only fail later in save_layout.
        try
        {
            _folderAccess.CheckCanList(LayoutsPath);
        }
        catch (Exception ex) when (IsFileSystemError(ex))
        {
            return $"Layout storage folder '{LayoutsPath}' exists but cannot be read: {ex.Message} " +
                   $"Set the {LayoutStorageOptions.LayoutsPathEnvironmentVariable} environment variable to a folder this user can read and write.";
        }

        try
        {
            _folderAccess.CheckCanWrite(LayoutsPath);
        }
        catch (Exception ex) when (IsFileSystemError(ex))
        {
            return $"Layout storage folder '{LayoutsPath}' exists but cannot be written: {ex.Message} " +
                   $"Set the {LayoutStorageOptions.LayoutsPathEnvironmentVariable} environment variable to a writable folder.";
        }

        InitializeFolderContents();
        _storageReady = true;
        return null;
    }

    /// <summary>
    /// One-time, best-effort migration and seeding. Never overwrites or deletes anything.
    /// </summary>
    private void InitializeFolderContents()
    {
        if (_options.MigrationSources.Count == 0 && _options.Samples.Count == 0)
            return;

        var markerPath = Path.Combine(LayoutsPath, InitializedMarkerFileName);
        if (File.Exists(markerPath))
            return;

        var knownNames = ReadPresetNames(LayoutsPath);
        MigrateLegacyPresets(knownNames);
        SeedSamplePresets(knownNames);

        try
        {
            File.WriteAllText(markerPath,
                "Windows App Manager migrated older layout presets and added the sample presets to this folder." + Environment.NewLine +
                "Delete this file to run that first-time setup again (existing presets are never overwritten)." + Environment.NewLine);
        }
        catch (Exception ex) when (IsFileSystemError(ex))
        {
            // Best effort: without the marker the setup simply runs again on the next start.
        }
    }

    private void MigrateLegacyPresets(HashSet<string> knownNames)
    {
        var candidates = new List<(FileInfo File, LayoutPreset Preset)>();

        foreach (var source in _options.MigrationSources)
        {
            if (IsSameFolder(source, LayoutsPath))
                continue;

            try
            {
                var folder = new DirectoryInfo(source);
                if (!folder.Exists)
                    continue;

                foreach (var file in folder.EnumerateFiles("*.json"))
                {
                    if (TryReadFile(file.FullName, out var json) && TryParsePreset(json) is { } preset)
                        candidates.Add((file, preset));
                }
            }
            catch (Exception ex) when (IsFileSystemError(ex))
            {
                // Best effort: skip folders that cannot be read.
            }
        }

        // When several old versions have the same preset, the most recently written one wins.
        foreach (var group in candidates.GroupBy(c => c.Preset.Name, StringComparer.OrdinalIgnoreCase))
        {
            if (knownNames.Contains(group.Key))
                continue;

            var newest = group.OrderByDescending(c => c.File.LastWriteTimeUtc).First();
            try
            {
                File.Copy(newest.File.FullName, GetAvailableFilePath(Path.GetFileNameWithoutExtension(newest.File.Name)), overwrite: false);
                knownNames.Add(group.Key);
            }
            catch (Exception ex) when (IsFileSystemError(ex))
            {
                // Best effort: the original file stays where it was.
            }
        }
    }

    private void SeedSamplePresets(HashSet<string> knownNames)
    {
        foreach (var sample in _options.Samples)
        {
            if (TryParsePreset(sample.Json) is not { } preset || knownNames.Contains(preset.Name))
                continue;

            var fileName = Path.GetFileName(sample.FileName);
            if (string.IsNullOrEmpty(fileName))
                continue;

            try
            {
                using var stream = new FileStream(Path.Combine(LayoutsPath, fileName), FileMode.CreateNew, FileAccess.Write);
                using var writer = new StreamWriter(stream);
                writer.Write(sample.Json);
                knownNames.Add(preset.Name);
            }
            catch (Exception ex) when (IsFileSystemError(ex))
            {
                // Best effort: an existing file with that name is never overwritten.
            }
        }
    }

    private static HashSet<string> ReadPresetNames(string folder)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (var file in Directory.EnumerateFiles(folder, "*.json"))
            {
                if (TryReadFile(file, out var json) && TryParsePreset(json) is { } preset)
                    names.Add(preset.Name);
            }
        }
        catch (Exception ex) when (IsFileSystemError(ex))
        {
            // Treat an unreadable folder as empty.
        }

        return names;
    }

    /// <summary>
    /// Brings the cache in line with the folder, re-parsing only new or changed files. A file that is not a valid preset is
    /// kept with why, and logged as a warning once per version of it (its write time and length).
    /// </summary>
    private void RefreshCache()
    {
        IReadOnlyList<FileInfo> files;
        try
        {
            files = _folderAccess.ListPresetFiles(LayoutsPath);
        }
        catch (Exception ex) when (IsFileSystemError(ex))
        {
            // Keep what we have rather than forgetting every preset on a transient error; it may be stale meanwhile, which is
            // logged when the listing starts to fail, not on every call while it does
            if (!_listingFailed)
            {
                _logger?.LogWarning(ex, "The layouts folder '{Folder}' could not be listed; the presets read from it before are used " +
                                        "until it can be, and may be stale", LayoutsPath);
            }

            _listingFailed = true;
            return;
        }

        if (_listingFailed)
            _logger?.LogInformation("The layouts folder '{Folder}' can be listed again", LayoutsPath);
        _listingFailed = false;

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in files)
        {
            seen.Add(file.FullName);

            if (_files.TryGetValue(file.FullName, out var cached) &&
                cached.LastWriteTimeUtc == file.LastWriteTimeUtc &&
                cached.Length == file.Length)
            {
                continue;
            }

            // If the file cannot be read right now (e.g. it is being written), keep the old entry and retry later.
            if (TryReadFile(file.FullName, out var json))
            {
                var (preset, error) = ParsePreset(json);
                _files[file.FullName] = new CachedPresetFile(file.LastWriteTimeUtc, file.Length, preset, error,
                    preset == null ? DeclaredNameOf(json) : null);
                if (error != null)
                {
                    _logger?.LogWarning("Layout file '{File}' is not a valid preset, so it is left out of the presets: {Error}",
                        file.FullName, error);
                }
            }
        }

        foreach (var removed in _files.Keys.Where(path => !seen.Contains(path)).ToList())
        {
            _files.Remove(removed);
        }
    }

    /// <summary>
    /// Maps preset names to the file that defines them. If two files define the same name, the most recently written wins.
    /// </summary>
    private Dictionary<string, (string Path, LayoutPreset Preset)> BuildNameIndex()
    {
        var index = new Dictionary<string, (string Path, LayoutPreset Preset)>(StringComparer.OrdinalIgnoreCase);
        foreach (var (path, cached) in _files
                     .OrderBy(entry => entry.Value.LastWriteTimeUtc)
                     .ThenBy(entry => entry.Key, StringComparer.OrdinalIgnoreCase))
        {
            if (cached.Preset is { } preset)
                index[preset.Name] = (path, preset);
        }

        return index;
    }

    private LayoutPreset? FindPreset(string name)
    {
        if (EnsureStorage() != null)
            return null;

        RefreshCache();
        return BuildNameIndex().TryGetValue(name, out var entry) ? entry.Preset : null;
    }

    /// <summary>The cached files that are not valid presets, with why, by file name (callers hold the gate).</summary>
    private List<InvalidLayoutFile> InvalidFiles() => _files
        .Where(entry => entry.Value.Preset == null)
        .Select(entry => new InvalidLayoutFile(Path.GetFileName(entry.Key), entry.Value.Error ?? "It is not a valid preset.",
            entry.Value.LastWriteTimeUtc, entry.Value.DeclaredName))
        .OrderBy(file => file.FileName, StringComparer.OrdinalIgnoreCase)
        .ToList();

    /// <summary>
    /// A file path for a new preset that no existing file or folder uses.
    /// </summary>
    private string GetAvailableFilePath(string name)
    {
        var baseName = SanitizeFileName(name);
        var candidate = Path.Combine(LayoutsPath, $"{baseName}.json");
        for (var suffix = 2; File.Exists(candidate) || Directory.Exists(candidate); suffix++)
        {
            candidate = Path.Combine(LayoutsPath, $"{baseName}-{suffix}.json");
        }

        return candidate;
    }

    private static string SanitizeFileName(string name)
    {
        var safeName = string.Join("_", name.Split(Path.GetInvalidFileNameChars()))
            .Trim()
            .TrimEnd('.'); // Windows drops trailing dots and spaces from file names

        if (safeName.Length == 0)
            safeName = "layout";

        // CON, NUL, COM1 and friends are devices, even with an extension.
        if (ReservedDeviceNames.Contains(safeName.Split('.')[0]))
            safeName = "_" + safeName;

        return safeName;
    }

    private static string? ValidateName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return "Layout preset name must not be empty";

        if (name.Length > MaxNameLength)
            return $"Layout preset name must be at most {MaxNameLength} characters";

        return null;
    }

    private static LayoutPreset? TryParsePreset(string json) => ParsePreset(json).Preset;

    /// <summary>
    /// The preset a file holds, upgraded to schema version 2 in memory (the file is never rewritten, roadmap M6.8), or why it
    /// holds none, as a sentence: it is not JSON (or has a value of the wrong kind, such as a state given as a number), lacks
    /// a name or placements, has a placement without a matcher or position, or a position that is no rectangle
    /// (<see cref="LayoutPresetSchema.LoadedError"/>).
    /// </summary>
    private static (LayoutPreset? Preset, string? Error) ParsePreset(string json)
    {
        LayoutPreset? preset;
        try
        {
            preset = JsonSerializer.Deserialize<LayoutPreset>(json, JsonOptions);
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException)
        {
            return (null, Sentence($"It is not valid layout JSON: {ex.Message}"));
        }

        // Records do not enforce non-nullable members during deserialization, so check the shape here.
        if (preset == null)
            return (null, "It holds no preset (its JSON is null).");
        if (string.IsNullOrWhiteSpace(preset.Name))
            return (null, "It has no name.");
        if (preset.Placements == null)
            return (null, "It has no placements.");

        for (var i = 0; i < preset.Placements.Count; i++)
        {
            var placement = preset.Placements[i];
            if (placement == null)
                return (null, $"placements[{i}] is empty.");
            if (placement.Matcher == null)
                return (null, $"placements[{i}] has no matcher.");
            if (placement.Position == null)
                return (null, $"placements[{i}] has no position.");
        }

        // A position that is no rectangle a window can have is never applied (review finding F2)
        if (LayoutPresetSchema.LoadedError(preset) is { } positionError)
            return (null, Sentence(positionError));

        return (LayoutPresetSchema.Upgrade(preset), null);
    }

    /// <summary>The name an invalid file gives (its "name" when its JSON can be read that far), trimmed; null otherwise.</summary>
    private static string? DeclaredNameOf(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            return document.RootElement.ValueKind == JsonValueKind.Object &&
                   document.RootElement.TryGetProperty("name", out var name) && name.ValueKind == JsonValueKind.String &&
                   !string.IsNullOrWhiteSpace(name.GetString())
                ? name.GetString()!.Trim()
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>The text with a full stop at its end, so a message can go on after it.</summary>
    private static string Sentence(string text) => text.TrimEnd().EndsWith('.') ? text.TrimEnd() : text.TrimEnd() + ".";

    private static bool TryReadFile(string path, out string content)
    {
        try
        {
            content = File.ReadAllText(path);
            return true;
        }
        catch (Exception ex) when (IsFileSystemError(ex))
        {
            content = string.Empty;
            return false;
        }
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (IsFileSystemError(ex))
        {
            // Best effort cleanup of a temp file.
        }
    }

    private static bool IsSameFolder(string a, string b)
    {
        try
        {
            return string.Equals(
                Path.TrimEndingDirectorySeparator(Path.GetFullPath(a)),
                Path.TrimEndingDirectorySeparator(Path.GetFullPath(b)),
                StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (IsFileSystemError(ex))
        {
            return false;
        }
    }

    private static bool IsFileSystemError(Exception ex) =>
        ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException
            or System.Security.SecurityException;

    #endregion
}
