using System.Text.Json;
using System.Text.Json.Serialization;
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

        // A placement's state is written as "normal", "maximized" or "minimized"; any case is read back
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
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
    private readonly LayoutStorageOptions _options;
    private readonly ILayoutFolderAccess _folderAccess;

    private readonly SemaphoreSlim _gate = new(1, 1);

    // Parsed preset files keyed by full path; Preset is null for files that are not valid presets.
    private readonly Dictionary<string, CachedPresetFile> _files = new(StringComparer.OrdinalIgnoreCase);
    private bool _storageReady;

    private sealed record CachedPresetFile(DateTime LastWriteTimeUtc, long Length, LayoutPreset? Preset);

    /// <summary>
    /// Creates a service that stores presets in <paramref name="layoutsPath"/> without migrating or seeding
    /// anything. When the path is null, the default location from <see cref="LayoutStorageOptions.FromEnvironment"/> is used.
    /// </summary>
    public LayoutService(
        IWindowService windowService,
        IMonitorService monitorService,
        IProcessService processService,
        IForegroundService foregroundService,
        string? layoutsPath = null)
        : this(
            windowService,
            monitorService,
            processService,
            foregroundService,
            layoutsPath == null ? LayoutStorageOptions.FromEnvironment() : LayoutStorageOptions.ForPath(layoutsPath))
    {
    }

    /// <summary>
    /// Creates a service for the given storage options. The constructor does not touch the file system:
    /// the folder is prepared on first use, so an unusable location is reported as
    /// <see cref="LayoutErrorCode.StorageUnavailable"/> instead of failing service construction.
    /// </summary>
    /// <param name="foregroundService">Brings the windows of placements with focus set to the front, verified.</param>
    /// <param name="folderAccess">Checks that the folder can be listed and written; defaults to the real file system.</param>
    public LayoutService(
        IWindowService windowService,
        IMonitorService monitorService,
        IProcessService processService,
        IForegroundService foregroundService,
        LayoutStorageOptions options,
        ILayoutFolderAccess? folderAccess = null)
    {
        _windowService = windowService;
        _monitorService = monitorService;
        _processService = processService;
        _foregroundService = foregroundService;
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _folderAccess = folderAccess ?? new LayoutFolderAccess();
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
                await File.WriteAllTextAsync(tempPath, JsonSerializer.Serialize(preset, JsonOptions), cancellationToken);
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
                return new LayoutDeleteResult(false, name, $"Layout preset '{name}' was not found",
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
        CancellationToken cancellationToken = default)
    {
        var nameError = ValidateName(name);
        if (nameError != null)
            return new LayoutSaveResult(false, null, ErrorMessage: nameError, ErrorCode: nameof(LayoutErrorCode.InvalidName));

        var windows = _windowService.GetAllWindows(includeMinimized: false);
        var monitors = _monitorService.GetAllMonitors();
        var placements = new List<WindowPlacement>();
        var ordinals = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var exePaths = new Dictionary<int, string?>();

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

            var relX = (double)(window.Bounds.X - workArea.X) / workArea.Width;
            var relY = (double)(window.Bounds.Y - workArea.Y) / workArea.Height;
            var relW = (double)window.Bounds.Width / workArea.Width;
            var relH = (double)window.Bounds.Height / workArea.Height;

            // Clamp to valid range
            relX = Math.Clamp(relX, 0, 1);
            relY = Math.Clamp(relY, 0, 1);
            relW = Math.Clamp(relW, 0.1, 1);
            relH = Math.Clamp(relH, 0.1, 1);

            // Which of the process's windows this is, in z-order; apply uses it to break ties
            var ordinal = ordinals.GetValueOrDefault(window.ProcessName);
            ordinals[window.ProcessName] = ordinal + 1;

            // The title of a window the user's policy denies (a password manager's database name) is not written to disk
            var denied = _windowService.EvaluateTargetPolicy(window.Handle) is { Allowed: false };

            var placement = new WindowPlacement(
                Matcher: new WindowMatcher(
                    TitleContains: null,
                    ProcessName: window.ProcessName,
                    ClassName: string.IsNullOrEmpty(window.ClassName) ? null : window.ClassName
                )
                {
                    ExactProcessName = true,
                    TitlePattern = denied || string.IsNullOrEmpty(window.Title) ? null : Truncate(window.Title, MaxTitlePatternLength),
                    ExePath = ExePathOf(window.ProcessId, exePaths),
                    Ordinal = ordinal
                },
                MonitorIndex: window.MonitorIndex,
                Position: new RelativePosition(relX, relY, relW, relH),
                LaunchCommand: null,
                Focus: false,
                Order: placements.Count
            )
            {
                State = window.State
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

    public async Task<LayoutApplyResult> ApplyPresetAsync(
        string name,
        LayoutMatchBy matchBy = LayoutMatchBy.ProcessAndTitle,
        bool launchMissing = false,
        IProgress<OperationProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        string? storageError;
        LayoutPreset? preset = null;

        await _gate.WaitAsync(cancellationToken);
        try
        {
            storageError = EnsureStorage();
            if (storageError == null && !string.IsNullOrWhiteSpace(name))
                preset = FindPreset(name);
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
                ErrorCode: nameof(LayoutErrorCode.StorageUnavailable));
        }

        if (preset == null)
        {
            return new LayoutApplyResult(
                Success: false,
                PresetName: name,
                Errors: [$"Layout preset '{name}' not found"],
                ErrorCode: nameof(LayoutErrorCode.PresetNotFound));
        }

        var monitors = _monitorService.GetAllMonitors();
        var orderedPlacements = preset.Placements.OrderBy(p => p.Order).ToList();

        // One snapshot of the windows get_all_windows lists; every placement is matched against it, so a window is
        // given to at most one placement
        var snapshot = _windowService.GetAllWindows(includeMinimized: true);
        var plans = PlanPlacements(orderedPlacements, snapshot, monitors, matchBy);
        var assignedTo = plans
            .Where(plan => plan.Window != null)
            .ToDictionary(plan => plan.Window!.Handle, plan => plan.Placement.Order);

        var results = new List<LayoutPlacementResult>(plans.Count);
        List<FocusResult>? focusResults = null;

        for (var index = 0; index < plans.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var plan = plans[index];
            LayoutPlacementResult result;
            string outcome;
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
                // One placement's failure never aborts the others
                result = new LayoutPlacementResult(
                    plan.Placement.Order,
                    LayoutPlacementStatus.Failed,
                    plan.Window is { } window ? LayoutWindowRef.From(window) : null,
                    ErrorCode: nameof(LayoutErrorCode.ArrangeFailed),
                    Error: $"Could not arrange {plan.Label}: {ex.GetType().Name}: {ex.Message}");
                outcome = "could not be arranged";
            }

            results.Add(result);

            // The client sees each placement's outcome as it happens: a layout that launches applications can take
            // several seconds per placement
            progress?.Report(new OperationProgress(index + 1, plans.Count,
                $"{index + 1} of {plans.Count}: {plan.Label} {outcome}"));
        }

        var errors = results.Where(r => r.Error != null).Select(r => r.Error!).ToList();
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
        async Task<(LayoutPlacementResult Result, string Outcome)> ApplyPlacementAsync(PlacementPlan plan)
        {
            var placement = plan.Placement;
            var label = plan.Label;

            LayoutPlacementResult Result(string status, WindowInfo? window, string? errorCode = null, string? error = null,
                bool launched = false, bool? focused = null) =>
                new(placement.Order, status, window == null ? null : LayoutWindowRef.From(window),
                    launched ? true : null, focused, errorCode, error);

            if (plan.NotApplicable is { } notApplicable)
            {
                return (Result(LayoutPlacementStatus.NotApplicable, null, nameof(LayoutErrorCode.NoMatchCriterion), notApplicable),
                    "was skipped: it has no criterion matchBy can use");
            }

            if (plan.MonitorError is { } monitorError)
            {
                return (Result(LayoutPlacementStatus.Failed, null, nameof(WindowErrorCode.InvalidMonitorIndex), monitorError),
                    "was not arranged: its monitor does not exist");
            }

            var window = plan.Window;
            var launched = false;
            if (window == null)
            {
                if (!launchMissing || string.IsNullOrEmpty(placement.LaunchCommand))
                {
                    var hint = string.IsNullOrEmpty(placement.LaunchCommand)
                        ? string.Empty
                        : $"; set launchMissing=true to launch '{placement.LaunchCommand}'";
                    return (Result(LayoutPlacementStatus.NotFound, null, nameof(LayoutErrorCode.WindowNotFound),
                        $"No open window matched {label}{hint}"), "was not found");
                }

                // Try to launch the application; a cancelled call stops waiting for its window
                var launchResult = await _processService.LaunchApplicationAsync(
                    placement.LaunchCommand,
                    waitForWindow: true,
                    waitTimeoutMs: 5000,
                    cancellationToken: cancellationToken).ConfigureAwait(false);

                if (!launchResult.Success)
                {
                    return (Result(LayoutPlacementStatus.Failed, null, nameof(LayoutErrorCode.LaunchFailed),
                        $"Failed to launch '{placement.LaunchCommand}' for {label}: {launchResult.ErrorMessage ?? "unknown error"}"),
                        "could not be launched");
                }

                launched = true;
                window = launchResult.WindowHandle is { } handle ? _windowService.GetWindowInfo(handle) : null;
                if (window == null)
                {
                    return (Result(LayoutPlacementStatus.Failed, null, nameof(LayoutErrorCode.LaunchFailed),
                        $"Launched '{placement.LaunchCommand}' for {label}, but its window could not be found", launched: true),
                        "was launched, but its window was not found");
                }

                // A single-instance application can hand the launch to a window it already has open
                if (!assignedTo.TryAdd(window.Handle, placement.Order))
                {
                    return (Result(LayoutPlacementStatus.Failed, window, nameof(LayoutErrorCode.LaunchFailed),
                        $"Launched '{placement.LaunchCommand}' for {label}, but the window it brought up, '{window.Title}', " +
                        $"was already given to placement {assignedTo[window.Handle]}", launched: true),
                        "was launched, but its window belongs to another placement");
                }
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

            string? notPlaced;
            try
            {
                var change = Arrange(window, placement, plan.Target!);
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

            if (placement.State == WindowState.Minimized)
            {
                var minimized = _windowService.SetWindowState(window.Handle, WindowStateRequest.Minimize);
                if (!minimized.Success)
                {
                    outcome = "was arranged, but could not be minimized";
                    errorCode = nameof(LayoutErrorCode.ArrangeFailed);
                    error = $"Arranged window '{window.Title}' for {label}, but could not minimize it ({minimized.ErrorCode}): {minimized.Error}";
                }
            }

            if (placement.Focus)
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
        }
    }

    /// <summary>
    /// Puts the window where the placement says, through the window service's verified geometry: a maximized placement
    /// maximizes the window on its monitor; otherwise a maximized window is restored first (a maximized window moved with
    /// SetWindowPos stays maximized in name, at the wrong size), then given the placement's rectangle.
    /// </summary>
    private WindowChangeResult Arrange(WindowInfo window, WindowPlacement placement, WindowRect target)
    {
        if (placement.State == WindowState.Maximized)
            return _windowService.MoveWindowToMonitor(window.Handle, placement.MonitorIndex, MonitorPositioning.Maximize);

        if (window.State == WindowState.Maximized)
        {
            var restored = _windowService.SetWindowState(window.Handle, WindowStateRequest.Restore);
            if (!restored.Success)
                return restored;
        }

        return _windowService.SetWindowBounds(window.Handle, target.X, target.Y, target.Width, target.Height);
    }

    #region Matching

    // How much each identity signal counts when ranking the windows that pass a placement's matchBy filter. The title is
    // the strongest identity, the class tells a main window from a dialog, the exe path two installations apart; the
    // distance from the placement's rectangle and the z-order ordinal only break ties.
    private const double TitleWeight = 40;
    private const double ClassWeight = 20;
    private const double ExePathWeight = 10;
    private const double DistanceWeight = 8;
    private const double OrdinalWeight = 4;

    /// <summary>The longest title save_layout records.</summary>
    private const int MaxTitlePatternLength = 260;

    private static readonly string[] TitleSeparators = [" - ", " – ", " — ", " | ", " · "];

    /// <summary>A placement on its way to being applied: the window it was given and where it goes.</summary>
    private sealed class PlacementPlan(WindowPlacement placement, string label)
    {
        public WindowPlacement Placement { get; } = placement;
        public string Label { get; } = label;

        /// <summary>Why the placement cannot be matched under the chosen matchBy; null when it can.</summary>
        public string? NotApplicable { get; set; }

        /// <summary>Why the placement's monitor cannot be used; null when it can.</summary>
        public string? MonitorError { get; set; }

        /// <summary>The placement's rectangle on its monitor; null when it is not applicable or its monitor does not exist.</summary>
        public WindowRect? Target { get; set; }

        /// <summary>The work area of the placement's monitor; null when the monitor does not exist.</summary>
        public MonitorRect? WorkArea { get; set; }

        /// <summary>The window the placement was given from the snapshot; null when none matched.</summary>
        public WindowInfo? Window { get; set; }
    }

    /// <summary>
    /// Gives each placement at most one window of <paramref name="snapshot"/>, and each window to at most one placement.
    /// Every (placement, window) pair that passes the matchBy filter is scored on the recorded identity (title pattern,
    /// class name, exe path, ordinal) and the distance from the placement's rectangle; the pairs are taken best first,
    /// so a placement that recognizes its window by title is not robbed of it by an earlier placement that would take
    /// any window of the process. Ties go to the earlier placement, then the window higher in the z-order.
    /// </summary>
    private List<PlacementPlan> PlanPlacements(
        IReadOnlyList<WindowPlacement> placements,
        IReadOnlyList<WindowInfo> snapshot,
        IReadOnlyList<MonitorInfo> monitors,
        LayoutMatchBy matchBy)
    {
        var plans = placements.Select(p => new PlacementPlan(p, DescribePlacement(p))).ToList();
        foreach (var plan in plans)
        {
            var placement = plan.Placement;
            if (MissingCriterion(placement.Matcher, matchBy) is { } missing)
            {
                plan.NotApplicable = $"Skipped {plan.Label}: {missing}";
                continue;
            }

            // A saved monitor that is gone is reported, not swapped for the primary one: the window would land on the
            // wrong screen with success
            if (placement.MonitorIndex < 0 || placement.MonitorIndex >= monitors.Count)
            {
                plan.MonitorError = $"Did not arrange {plan.Label}: {WindowService.MonitorIndexMessage(placement.MonitorIndex, monitors.Count)}";
                continue;
            }

            var workArea = monitors[placement.MonitorIndex].WorkArea;
            plan.WorkArea = workArea;
            plan.Target = new WindowRect(
                (int)(workArea.X + placement.Position.X * workArea.Width),
                (int)(workArea.Y + placement.Position.Y * workArea.Height),
                (int)(placement.Position.Width * workArea.Width),
                (int)(placement.Position.Height * workArea.Height));
        }

        // The position of each window among the snapshot's windows of its process, as save_layout counted it
        var ordinals = new int[snapshot.Count];
        var seen = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < snapshot.Count; i++)
        {
            ordinals[i] = seen.GetValueOrDefault(snapshot[i].ProcessName);
            seen[snapshot[i].ProcessName] = ordinals[i] + 1;
        }

        var exePaths = new Dictionary<int, string?>();
        var pairs = new List<(double Score, int Plan, int Window)>();
        for (var p = 0; p < plans.Count; p++)
        {
            if (plans[p].Target is not { } target)
                continue;

            var matcher = plans[p].Placement.Matcher;
            for (var w = 0; w < snapshot.Count; w++)
            {
                if (Matches(snapshot[w], matcher, matchBy))
                    pairs.Add((Score(matcher, snapshot[w], ordinals[w], target, plans[p].WorkArea!, exePaths), p, w));
            }
        }

        var taken = new bool[snapshot.Count];
        foreach (var (_, p, w) in pairs.OrderByDescending(pair => pair.Score).ThenBy(pair => pair.Plan).ThenBy(pair => pair.Window))
        {
            if (plans[p].Window != null || taken[w])
                continue;

            plans[p].Window = snapshot[w];
            taken[w] = true;
        }

        return plans;
    }

    /// <summary>
    /// Why <paramref name="matcher"/> has nothing <paramref name="matchBy"/> can match on, or null when it has. An empty
    /// criterion used to match every window, so title_only on a captured layout moved arbitrary windows.
    /// </summary>
    private static string? MissingCriterion(WindowMatcher matcher, LayoutMatchBy matchBy)
    {
        var hasProcess = !string.IsNullOrEmpty(matcher.ProcessName);
        var hasTitle = !string.IsNullOrEmpty(matcher.TitleContains);
        return matchBy switch
        {
            LayoutMatchBy.TitleOnly when !hasTitle =>
                "matchBy title_only needs titleContains, and this placement has none (save_layout records the process, " +
                "not title text); use process_and_title or process_only",
            LayoutMatchBy.ProcessOnly when !hasProcess =>
                "matchBy process_only needs a processName, and this placement has none; use process_and_title or title_only",
            LayoutMatchBy.ProcessAndTitle when !hasProcess && !hasTitle =>
                "the placement has neither a processName nor titleContains, so it would match any window",
            _ => null
        };
    }

    private static bool Matches(WindowInfo window, WindowMatcher matcher, LayoutMatchBy matchBy) => matchBy switch
    {
        LayoutMatchBy.ProcessOnly => MatchesProcessName(window, matcher),
        LayoutMatchBy.TitleOnly => MatchesTitle(window, matcher),
        LayoutMatchBy.ProcessAndTitle =>
            (string.IsNullOrEmpty(matcher.ProcessName) || MatchesProcessName(window, matcher)) &&
            (string.IsNullOrEmpty(matcher.TitleContains) || MatchesTitle(window, matcher)),
        _ => throw new ArgumentOutOfRangeException(nameof(matchBy), matchBy, "Not a matchBy value.")
    };

    /// <summary>
    /// A captured process name (<see cref="WindowMatcher.ExactProcessName"/>) must be the window's whole process name,
    /// so "Code" does not match "Code - Insiders"; a hand-written one may be part of it. Either may name a UWP window's
    /// frame host.
    /// </summary>
    private static bool MatchesProcessName(WindowInfo window, WindowMatcher matcher) =>
        matcher.ExactProcessName
            ? string.Equals(window.ProcessName, matcher.ProcessName, StringComparison.OrdinalIgnoreCase) ||
              string.Equals(window.HostProcessName, matcher.ProcessName, StringComparison.OrdinalIgnoreCase)
            : ProcessNameContains(window, matcher.ProcessName!);

    /// <summary>
    /// True when <paramref name="name"/> is part of the window's process name or, for a UWP window, of
    /// its frame host's (ApplicationFrameHost), the same rule as find_windows. Presets saved before
    /// UWP windows were attributed to the app store ApplicationFrameHost as their process name.
    /// </summary>
    private static bool ProcessNameContains(WindowInfo window, string name) =>
        window.ProcessName.Contains(name, StringComparison.OrdinalIgnoreCase) ||
        window.HostProcessName?.Contains(name, StringComparison.OrdinalIgnoreCase) == true;

    private static bool MatchesTitle(WindowInfo window, WindowMatcher matcher) =>
        window.Title.Contains(matcher.TitleContains!, StringComparison.OrdinalIgnoreCase);

    private double Score(
        WindowMatcher matcher, WindowInfo window, int ordinal, WindowRect target, MonitorRect workArea, Dictionary<int, string?> exePaths)
    {
        var score = DistanceWeight * Closeness(window.Bounds, target, workArea);

        if (!string.IsNullOrEmpty(matcher.TitlePattern))
            score += TitleWeight * TitleSimilarity(matcher.TitlePattern, window.Title);

        if (!string.IsNullOrEmpty(matcher.ClassName) && !string.IsNullOrEmpty(window.ClassName))
            score += string.Equals(matcher.ClassName, window.ClassName, StringComparison.OrdinalIgnoreCase) ? ClassWeight : -ClassWeight;

        if (!string.IsNullOrEmpty(matcher.ExePath) && ExePathOf(window.ProcessId, exePaths) is { } exePath)
            score += string.Equals(matcher.ExePath, exePath, StringComparison.OrdinalIgnoreCase) ? ExePathWeight : -ExePathWeight;

        if (matcher.Ordinal == ordinal)
            score += OrdinalWeight;

        return score;
    }

    /// <summary>
    /// 1 when the titles are equal (ignoring case); otherwise up to 0.8 for the share of the pattern's parts (split at
    /// " - ", " | " and dashes, so "Inbox - Mail - Google Chrome" has three) that are also parts of the title.
    /// </summary>
    private static double TitleSimilarity(string pattern, string title)
    {
        if (string.Equals(pattern, title, StringComparison.OrdinalIgnoreCase))
            return 1;

        var patternParts = SplitTitle(pattern);
        if (patternParts.Length == 0)
            return 0;

        var titleParts = new HashSet<string>(SplitTitle(title), StringComparer.OrdinalIgnoreCase);
        return 0.8 * patternParts.Count(titleParts.Contains) / patternParts.Length;
    }

    private static string[] SplitTitle(string title) =>
        title.Split(TitleSeparators, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    /// <summary>1 when the window is exactly at the target rectangle, falling to 0 as its edges move a work area away.</summary>
    private static double Closeness(WindowRect bounds, WindowRect target, MonitorRect workArea)
    {
        double scale = 2L * (workArea.Width + workArea.Height);
        if (scale <= 0)
            return 0;

        double offset = Math.Abs((long)bounds.X - target.X) + Math.Abs((long)bounds.Y - target.Y) +
                        Math.Abs((long)bounds.Width - target.Width) + Math.Abs((long)bounds.Height - target.Height);
        return 1 - Math.Min(1, offset / scale);
    }

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

    private static string Truncate(string text, int maxLength) => text.Length <= maxLength ? text : text[..maxLength];

    private static string DescribePlacement(WindowPlacement placement)
    {
        var criteria = new List<string>();
        if (!string.IsNullOrEmpty(placement.Matcher.ProcessName))
            criteria.Add($"process '{placement.Matcher.ProcessName}'");
        if (!string.IsNullOrEmpty(placement.Matcher.TitleContains))
            criteria.Add($"title containing '{placement.Matcher.TitleContains}'");

        return $"placement {placement.Order} ({(criteria.Count > 0 ? string.Join(", ", criteria) : "no matcher")})";
    }

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
    /// Brings the cache in line with the folder, re-parsing only new or changed files.
    /// </summary>
    private void RefreshCache()
    {
        List<FileInfo> files;
        try
        {
            var folder = new DirectoryInfo(LayoutsPath);
            files = folder.Exists ? folder.EnumerateFiles("*.json").ToList() : [];
        }
        catch (Exception ex) when (IsFileSystemError(ex))
        {
            // Keep what we have rather than forgetting every preset on a transient error.
            return;
        }

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
                _files[file.FullName] = new CachedPresetFile(file.LastWriteTimeUtc, file.Length, TryParsePreset(json));
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

    private static LayoutPreset? TryParsePreset(string json)
    {
        try
        {
            var preset = JsonSerializer.Deserialize<LayoutPreset>(json, JsonOptions);

            // Records do not enforce non-nullable members during deserialization, so check the shape here.
            if (preset == null || string.IsNullOrWhiteSpace(preset.Name) || preset.Placements == null)
                return null;

            if (preset.Placements.Any(p => p == null || p.Matcher == null || p.Position == null))
                return null;

            return preset;
        }
        catch (JsonException)
        {
            return null;
        }
    }

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
