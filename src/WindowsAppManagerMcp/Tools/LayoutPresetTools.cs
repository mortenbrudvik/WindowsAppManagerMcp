using System.ComponentModel;
using ModelContextProtocol;
using ModelContextProtocol.Server;
using WindowsAppManagerMcp.Infrastructure;
using WindowsAppManagerMcp.Models;
using WindowsAppManagerMcp.Models.Results;
using WindowsAppManagerMcp.Services;
using WindowsAppManagerMcp.Services.Interfaces;

namespace WindowsAppManagerMcp.Tools;

[McpServerToolType]
public class LayoutPresetTools
{
    private readonly ILayoutService _layoutService;
    private readonly IInputSession? _session;
    private readonly TimeSpan _answerTimeout;
    private readonly TimeSpan? _gateWaitTimeout;

    /// <param name="layoutService">Reads, writes and applies the presets.</param>
    /// <param name="session">The input session: apply_layout takes the input gate itself, after any question to the user (roadmap M6.8).</param>
    /// <param name="answerTimeout">How long apply_layout waits for the user's answer; default <see cref="UserConfirmation.DefaultAnswerTimeout"/>.</param>
    /// <param name="gateWaitTimeout">How long apply_layout waits for the input gate; default <see cref="InputGateToolFilter.GateWaitTimeout"/>.</param>
    public LayoutPresetTools(
        ILayoutService layoutService,
        IInputSession? session = null,
        TimeSpan? answerTimeout = null,
        TimeSpan? gateWaitTimeout = null)
    {
        _layoutService = layoutService;
        _session = session;
        _answerTimeout = answerTimeout ?? UserConfirmation.DefaultAnswerTimeout;
        _gateWaitTimeout = gateWaitTimeout;
    }

    [McpServerTool(Name = "list_layouts", Title = "List Layouts", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description(@"List all saved layout presets. Returns preset names, descriptions, and window counts. Presets are JSON files in %LOCALAPPDATA%\WindowsAppManagerMcp\layouts (or the folder in the WAM_LAYOUTS_PATH environment variable); the samples work-setup, dual-monitor and focus-mode are added there on first run.")]
    public IReadOnlyList<LayoutSummaryDto> ListLayouts()
    {
        ThrowIfStorageUnavailable();

        var presets = _layoutService.GetAllPresets();
        return presets.Select(p => new LayoutSummaryDto(
            Name: p.Name,
            Description: p.Description,
            WindowCount: p.Placements.Count,
            CreatedAt: p.CreatedAt.ToString("O"),
            UpdatedAt: p.UpdatedAt.ToString("O")
        )).ToList();
    }

    [McpServerTool(Name = "get_layout", Title = "Get Layout", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("Get the full definition of a layout preset including all window placements. Returns a tool error if the preset does not exist.")]
    public LayoutDetailDto GetLayout(
        [Description("Name of the layout preset")]
        string name)
    {
        ThrowIfStorageUnavailable();

        var preset = _layoutService.GetPreset(name)
            ?? throw new ToolErrorException(nameof(LayoutErrorCode.PresetNotFound), PresetNotFoundMessage(name));

        return ToDetailDto(preset);
    }

    [McpServerTool(Name = "save_layout", Title = "Save Layout", ReadOnly = false, Destructive = true, Idempotent = true, OpenWorld = false)]
    [Description("Save a layout preset. Without placements it captures the visible windows (minimized ones with includeMinimized): each " +
                 "placement records the window's exact process name, class, title, executable path, z-order ordinal, state, restore " +
                 "rectangle, monitor id and name, and what launchMissing would start (its program or a packaged app's appId, never " +
                 "arguments). With placements it saves the placements you write instead; get_layout's placements go back in unchanged. " +
                 "A bad placement fails with InvalidPlacement and nothing is saved. Saving under an existing name replaces that preset " +
                 "(replaced=true). NoWindowsCaptured, without touching an existing preset, when no window matches the filters.")]
    public async Task<SaveLayoutResultDto> SaveLayout(
        [Description("Name for the layout preset")]
        string name,
        [Description("Optional description of the layout")]
        string? description = null,
        [Description("Only save windows from these processes (comma-separated). Leave empty to save all visible windows.")]
        string? includeProcesses = null,
        [Description("Exclude windows from these processes (comma-separated)")]
        string? excludeProcesses = null,
        [Description("Also save minimized windows (default false)")]
        bool includeMinimized = false,
        [Description("The placements to save instead of capturing windows, in order; not with the capture filters")]
        LayoutPlacementSpec[]? placements = null,
        CancellationToken cancellationToken = default)
    {
        LayoutSaveResult result;
        if (placements != null)
        {
            // An agent-written layout (roadmap M6.8): nothing is captured, so the capture's filters mean nothing
            if (includeProcesses != null || excludeProcesses != null || includeMinimized)
            {
                throw new ToolErrorException(ToolErrorReporting.InvalidArgumentsErrorCode,
                    "placements saves the placements you give and captures no window, so it takes no includeProcesses, " +
                    "excludeProcesses or includeMinimized. Nothing was saved.");
            }

            result = await _layoutService.SaveAuthoredLayoutAsync(name, description, placements, cancellationToken);
        }
        else
        {
            result = await _layoutService.CaptureCurrentLayoutAsync(
                name, description, ProcessList(includeProcesses), ProcessList(excludeProcesses), includeMinimized, cancellationToken);
        }

        return new SaveLayoutResultDto(
            Success: result.Success,
            LayoutName: result.Preset?.Name ?? name,
            WindowCount: result.Preset?.Placements.Count ?? 0,
            Error: result.ErrorMessage,
            ErrorCode: result.ErrorCode,
            Replaced: result.Replaced
        );
    }

    private static string[]? ProcessList(string? names) =>
        string.IsNullOrEmpty(names) ? null : names.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    [McpServerTool(Name = "apply_layout", Title = "Apply Layout", ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = true)]
    [Description("Apply a previously saved layout preset. Takes one snapshot of the open windows and gives each placement its own window, never one window to two placements, preferring the window whose title, class and executable match what save_layout recorded. success is true only if every placement was arranged (and focused, for a placement with focus); status is applied, partial or failed, and placements reports each placement in order (status applied, notFound, notApplicable or failed, with window, errorCode and error). A placement matchBy cannot match (title_only on a placement without titleContains) is notApplicable with NoMatchCriterion, and one on a monitor that does not exist fails with InvalidMonitorIndex or MonitorNotFound. A monitor saved by id that is not connected falls back to the monitor with its name, then its index (monitorFallback name or index). A maximized window is given its restore rectangle, then maximized; a minimized one that stays minimized is never shown. focus lists the outcome of each focus placement that was arranged, with the same fields as focus_window (isForeground, foregroundHandle, blockingPopup, inMenuMode, errorCode). A window of an app the user's policy denies is not arranged (TargetDeniedByPolicy). dryRun changes nothing: status dryRun, each placement wouldApply or wouldLaunch with target {x, y, width, height}, or why not.")]
    public async Task<ApplyLayoutResultDto> ApplyLayout(
        [Description("Name of the layout preset to apply")]
        string name,
        [Description("How to match saved windows to current windows: 'process_and_title' (default), 'process_only' or 'title_only' (only for placements with titleContains; save_layout records none). Any other value fails with InvalidMatchBy.")]
        LayoutMatchBy matchBy = LayoutMatchBy.ProcessAndTitle,
        [Description("Start the program or app each placement names when its window is not found (default: false); a second call can start another instance when the launched window does not match yet. For a layout an agent wrote (save_layout placements), the user is asked once per version of it; without their yes nothing is started (LaunchNotConfirmed).")]
        bool launchMissing = false,
        [Description("Only plan: report what each placement would do, and change, focus and start nothing (default false)")]
        bool dryRun = false,
        [Description("Only these placements, 0-based as get_layout lists them (default: all)")]
        int[]? placements = null,
        McpServer? server = null,
        IProgress<ProgressNotificationValue>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var options = new LayoutApplyOptions(dryRun, placements);

        // A dry run only reads, so it neither waits for the input gate nor stops while the user has paused input (roadmap M6.8)
        if (dryRun)
            return ToDto(await _layoutService.ApplyPresetAsync(name, matchBy, launchMissing, options, ToolProgress.ForServices(progress), cancellationToken));

        // Nothing while the user has paused input, not even a question
        InputGateToolFilter.ThrowIfPaused(_session);

        // An agent wrote this layout: the user confirms its launches before the input gate is taken (owner decision D15), so a
        // user who takes minutes to answer holds up no input or window call. Only when this apply would start something
        if (launchMissing && _layoutService.GetPreset(name) is { } preset &&
            string.Equals(preset.AuthoredBy, LayoutPresetSchema.AgentAuthor, StringComparison.OrdinalIgnoreCase) &&
            !_layoutService.AreLaunchesConfirmed(preset))
        {
            var preview = await _layoutService.ApplyPresetAsync(name, matchBy, launchMissing, options with { DryRun = true }, null, cancellationToken);
            if (preview.Placements?.Any(p => p.ErrorCode == nameof(LayoutErrorCode.LaunchNotConfirmed)) == true)
            {
                if (!UserConfirmation.CanAsk(server))
                {
                    options = options with { LaunchRefusal = "the client cannot ask the user (no elicitation)" };
                }
                else
                {
                    var answer = await UserConfirmation.AskAsync(server!, LaunchQuestion(preset), "Let the layout start programs",
                        _answerTimeout, cancellationToken).ConfigureAwait(false);
                    if (answer.Confirmed)
                        _layoutService.ConfirmLaunches(preset);
                    else
                        options = options with { LaunchRefusal = answer.Reason?.TrimEnd('.') };
                }
            }
        }

        // The arrangement and its launches run inside the input gate, as every window-changing tool does; the client gets
        // "i of n" with each placement's outcome, and a cancelled call arranges no further placement
        using (await InputGateToolFilter.EnterAsync(_session, cancellationToken, _gateWaitTimeout).ConfigureAwait(false))
        {
            return ToDto(await _layoutService.ApplyPresetAsync(
                name, matchBy, launchMissing, options, ToolProgress.ForServices(progress), cancellationToken));
        }
    }

    private static ApplyLayoutResultDto ToDto(LayoutApplyResult result) => new(
        Success: result.Success,
        Status: result.Status,
        PresetName: result.PresetName,
        WindowsArranged: result.WindowsArranged,
        WindowsNotFound: result.WindowsNotFound,
        WindowsLaunched: result.WindowsLaunched,
        Errors: result.Errors,
        ErrorCode: result.ErrorCode,
        Focus: result.Focus,
        Placements: result.Placements
    );

    /// <summary>The most programs the launch question names; the rest are counted.</summary>
    private const int MaxQuestionPrograms = 8;

    /// <summary>
    /// The question apply_layout asks before an agent-written layout starts programs (owner decision D15): the layout's name and
    /// when it was saved, and what it may start (each program's path as <see cref="UserConfirmation.QuotePath"/> shows it, an
    /// app's ID quoted), at most <see cref="MaxQuestionPrograms"/> of them. Layout launches carry no arguments.
    /// </summary>
    internal static string LaunchQuestion(LayoutPreset preset)
    {
        var targets = preset.Placements
            .Select(LayoutPresetSchema.LaunchTarget)
            .OfType<string>()
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        var shown = targets.Take(MaxQuestionPrograms)
            .Select(t => t.StartsWith(PackagedAppId.AppsFolderPrefix, StringComparison.OrdinalIgnoreCase)
                ? $"the app {UserConfirmation.Quote(t[PackagedAppId.AppsFolderPrefix.Length..])}"
                : UserConfirmation.QuotePath(t));
        var more = targets.Count > MaxQuestionPrograms ? $" and {targets.Count - MaxQuestionPrograms} more" : string.Empty;
        return $"An AI agent wrote the window layout {UserConfirmation.Quote(preset.Name)} (saved " +
               $"{preset.UpdatedAt.ToUniversalTime():yyyy-MM-dd HH:mm} UTC), and applying it starts programs for its missing windows: " +
               $"{string.Join(", ", shown)}{more}, with no arguments. Allow this version of the layout to start them, now and " +
               "when it is applied again?";
    }

    [McpServerTool(Name = "delete_layout", Title = "Delete Layout", ReadOnly = false, Destructive = true, Idempotent = true, OpenWorld = false)]
    [Description("Delete a layout preset. Fails with PresetNotFound if no preset has that name.")]
    public async Task<DeleteLayoutResultDto> DeleteLayout(
        [Description("Name of the preset to delete")]
        string name,
        CancellationToken cancellationToken = default)
    {
        var result = await _layoutService.DeletePresetAsync(name, cancellationToken);
        return new DeleteLayoutResultDto(
            Success: result.Success,
            Error: result.ErrorMessage,
            ErrorCode: result.ErrorCode
        );
    }

    /// <summary>
    /// list_layouts and get_layout return plain data, so an unusable storage folder is reported as a
    /// tool error (isError=true) with errorCode StorageUnavailable and this message.
    /// </summary>
    private void ThrowIfStorageUnavailable()
    {
        var storageError = _layoutService.GetStorageError();
        if (storageError != null)
            throw new ToolErrorException(nameof(LayoutErrorCode.StorageUnavailable), storageError);
    }

    /// <summary>What get_layout and the wam://layouts/{name} resource (roadmap M6.9) say about a name no preset has.</summary>
    internal static string PresetNotFoundMessage(string name) =>
        $"Layout preset '{name}' was not found. Call list_layouts to see the available presets.";

    /// <summary>
    /// get_layout's result, which the wam://layouts/{name} resource returns too (roadmap M6.9), so a change to one changes
    /// both.
    /// </summary>
    internal static LayoutDetailDto ToDetailDto(LayoutPreset preset) => new(
        Name: preset.Name,
        Description: preset.Description,
        Placements: preset.Placements.Select(p => new PlacementDto(
            ProcessName: p.Matcher.ProcessName,
            TitleContains: p.Matcher.TitleContains,
            MonitorIndex: p.MonitorIndex,
            // A snap placement's position is for older servers; get_layout shows its snap, so its placements go back into save_layout
            Position: p.Snap == null ? PositionDto.From(p.Position) : null,
            LaunchCommand: p.LaunchCommand,
            Focus: p.Focus,
            Order: p.Order,
            ExactProcessName: p.Matcher.ExactProcessName ? true : null,
            ClassName: p.Matcher.ClassName,
            TitlePattern: p.Matcher.TitlePattern,
            ExePath: p.Matcher.ExePath,
            Ordinal: p.Matcher.Ordinal,
            State: p.State?.ToString().ToLowerInvariant(),
            // Schema version 2 (roadmap M6.8): monitor is what save_layout's placements take back (the saved identity, or
            // the index), so get_layout's placements round-trip
            Monitor: string.IsNullOrWhiteSpace(p.Monitor) ? MonitorSelector.OfIndex(p.MonitorIndex) : MonitorSelector.Of(p.Monitor),
            MonitorName: p.MonitorName,
            NormalPosition: p.NormalPosition is { } normal ? PositionDto.From(normal) : null,
            Snap: p.Snap,
            Launch: p.Launch
        )).ToList(),
        CreatedAt: preset.CreatedAt.ToString("O"),
        UpdatedAt: preset.UpdatedAt.ToString("O"),
        AuthoredBy: preset.AuthoredBy,
        Warning: LayoutPresetSchema.IsNewer(preset)
            ? $"A newer version of this server wrote this preset (schema version {preset.Version}); the fields this version does " +
              "not know are ignored, here and by apply_layout."
            : null
    );
}

public record LayoutSummaryDto(
    string Name,
    string? Description,
    int WindowCount,
    string CreatedAt,
    string UpdatedAt
);

/// <param name="AuthoredBy">"agent" when an agent wrote the placements (save_layout placements); null otherwise.</param>
/// <param name="Warning">Said when a newer server wrote the preset; null otherwise.</param>
public record LayoutDetailDto(
    string Name,
    string? Description,
    IReadOnlyList<PlacementDto> Placements,
    string CreatedAt,
    string UpdatedAt,
    string? AuthoredBy = null,
    string? Warning = null
);

/// <summary>A placement of get_layout. The fields after Order are what save_layout recorded about the window; null when not recorded.</summary>
/// <param name="ExactProcessName">True when processName must be the window's whole process name (save_layout); null when part of it is enough.</param>
/// <param name="State">normal, maximized or minimized.</param>
/// <param name="Monitor">The saved monitor identity (an id, device name or keyword), or the index when none was saved (roadmap M6.8).</param>
/// <param name="MonitorName">The monitor's EDID name when it was saved.</param>
/// <param name="NormalPosition">The restore rectangle of a maximized or minimized window, as fractions of the work area.</param>
/// <param name="Snap">The snap position of an agent-written placement, which then has no position.</param>
/// <param name="Launch">What launchMissing starts: executable or appId.</param>
public record PlacementDto(
    string? ProcessName,
    string? TitleContains,
    int MonitorIndex,
    PositionDto? Position,
    string? LaunchCommand,
    bool Focus,
    int Order,
    bool? ExactProcessName = null,
    string? ClassName = null,
    string? TitlePattern = null,
    string? ExePath = null,
    int? Ordinal = null,
    string? State = null,
    MonitorSelector? Monitor = null,
    string? MonitorName = null,
    PositionDto? NormalPosition = null,
    string? Snap = null,
    LayoutLaunch? Launch = null
);

public record PositionDto(double X, double Y, double Width, double Height)
{
    public static PositionDto From(RelativePosition position) => new(position.X, position.Y, position.Width, position.Height);
}

public record SaveLayoutResultDto(
    bool Success,
    string LayoutName,
    int WindowCount,
    string? Error = null,
    string? ErrorCode = null,
    bool Replaced = false
);

/// <summary>Result of apply_layout.</summary>
/// <param name="Focus">
/// One entry for each placement with focus set whose window was arranged, in placement order: what the verified
/// activation found, with the same fields as focus_window. Null when no such placement was activated.
/// </param>
/// <param name="Status">applied (every placement), partial (some) or failed (none).</param>
/// <param name="Placements">What happened to each placement, in placement order; null when the preset was not applied at all.</param>
public record ApplyLayoutResultDto(
    bool Success,
    string Status,
    string PresetName,
    int WindowsArranged,
    int WindowsNotFound,
    int WindowsLaunched,
    IReadOnlyList<string>? Errors,
    string? ErrorCode = null,
    IReadOnlyList<FocusResult>? Focus = null,
    IReadOnlyList<LayoutPlacementResult>? Placements = null
);

public record DeleteLayoutResultDto(
    bool Success,
    string? Error = null,
    string? ErrorCode = null
);
