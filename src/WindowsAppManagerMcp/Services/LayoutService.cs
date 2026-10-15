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
    private readonly ILaunchPlacementService _launchPlacement;
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
    public LayoutService(
        IWindowService windowService,
        IMonitorService monitorService,
        IProcessService processService,
        IForegroundService foregroundService,
        LayoutStorageOptions options,
        ILayoutFolderAccess? folderAccess = null,
        ILaunchPlacementService? launchPlacement = null)
    {
        _windowService = windowService;
        _monitorService = monitorService;
        _processService = processService;
        _foregroundService = foregroundService;
        _launchPlacement = launchPlacement ?? new LaunchPlacementService(windowService, foregroundService);
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
        var exePaths = new Dictionary<int, string?>();
        var plans = LayoutPlanner.Plan(orderedPlacements, snapshot, monitors, matchBy, processId => ExePathOf(processId, exePaths));
        var assignedTo = plans
            .Where(plan => plan.Window != null)
            .ToDictionary(plan => plan.Window!.Handle, plan => plan.Placement.Order);

        var results = new List<LayoutPlacementResult>(plans.Count);
        List<FocusResult>? focusResults = null;

        // What the placement being applied has done so far, so that a placement that throws still reports its launch
        string? launchCommand = null;
        var launchedWindow = false;
        WindowInfo? placedWindow = null;

        for (var index = 0; index < plans.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var plan = plans[index];
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
                    LaunchCommand: launchCommand);
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
        async Task<(LayoutPlacementResult Result, string Outcome)> ApplyPlacementAsync(PlannedPlacement plan)
        {
            var placement = plan.Placement;
            var label = plan.Label;

            LayoutPlacementResult Result(string status, WindowInfo? window, string? errorCode = null, string? error = null,
                bool launched = false, bool? focused = null) =>
                new(placement.Order, status, window == null ? null : LayoutWindowRef.From(window),
                    launched ? true : null, focused, errorCode, error, launchCommand);

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
                launchCommand = placement.LaunchCommand;
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
                launchedWindow = true;
                window = launchResult.WindowHandle is { } handle ? _windowService.GetWindowInfo(handle) : null;
                placedWindow = window;
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

                // The launch tools' placement (roadmap M3.F27): the same checks before the window is touched (the agent's own
                // host, the user's policy, the pause), the watch for an app that moves its window back, and focus last
                var placed = (await _launchPlacement.PlaceAsync([(window.Handle, LaunchPlan(placement, plan.Target!))], cancellationToken)
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

            // What the placement service did with a launched window, as apply_layout reports an open window's placement: a
            // window that was not placed fails, one that was placed but not minimized, kept in place or focused is applied
            // with ArrangeFailed, and so is one the app kept moving back (apply_layout has no warnings)
            (LayoutPlacementResult Result, string Outcome) Launched(WindowInfo window, LaunchPlacementOutcome placed)
            {
                var code = placed.ErrorCode.ToString();
                bool? focused = placement.Focus ? placed.Activation?.IsForeground == true : null;
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
    /// The launch placement plan of a placement whose window was launched (roadmap M3.F27), as <see cref="Arrange"/> places an
    /// open window: a maximized placement is maximized on its monitor; any other is given the placement's rectangle as the
    /// window rect (<see cref="BoundsFrame.Outer"/>), the rect layouts save, and minimized afterwards for state minimized.
    /// </summary>
    private static LaunchPlacementPlan LaunchPlan(WindowPlacement placement, WindowRect target) =>
        placement.State == WindowState.Maximized
            ? new LaunchPlacementPlan(null, placement.MonitorIndex, null, LaunchWindowState.Maximized, placement.Focus)
            : new LaunchPlacementPlan(null, null, target, placement.State == WindowState.Minimized ? LaunchWindowState.Minimized : null,
                placement.Focus) { Frame = BoundsFrame.Outer };

    /// <summary>
    /// Puts the window where the placement says, through the window service's verified geometry: a maximized placement
    /// maximizes the window on its monitor; otherwise the window is given the placement's rectangle. SetWindowBounds
    /// restores a minimized or maximized window to normal first, without activating it (SW_SHOWNOACTIVATE), so a window
    /// that was maximized is not activated (SW_RESTORE would bring it to the front, roadmap M2.3).
    /// </summary>
    private WindowChangeResult Arrange(WindowInfo window, WindowPlacement placement, WindowRect target)
    {
        if (placement.State == WindowState.Maximized)
            return _windowService.MoveWindowToMonitor(window.Handle, placement.MonitorIndex, MonitorPositioning.Maximize);

        return _windowService.SetWindowBounds(window.Handle, target.X, target.Y, target.Width, target.Height);
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
