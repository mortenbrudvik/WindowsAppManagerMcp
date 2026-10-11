using System.ComponentModel;
using ModelContextProtocol;
using ModelContextProtocol.Server;
using WindowsAppManagerMcp.Infrastructure;
using WindowsAppManagerMcp.Models;
using WindowsAppManagerMcp.Models.Results;
using WindowsAppManagerMcp.Services.Interfaces;

namespace WindowsAppManagerMcp.Tools;

[McpServerToolType]
public class LayoutPresetTools
{
    private readonly ILayoutService _layoutService;

    public LayoutPresetTools(ILayoutService layoutService)
    {
        _layoutService = layoutService;
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
            ?? throw new ToolErrorException(
                nameof(LayoutErrorCode.PresetNotFound),
                $"Layout preset '{name}' was not found. Call list_layouts to see the available presets.");

        return new LayoutDetailDto(
            Name: preset.Name,
            Description: preset.Description,
            Placements: preset.Placements.Select(p => new PlacementDto(
                ProcessName: p.Matcher.ProcessName,
                TitleContains: p.Matcher.TitleContains,
                MonitorIndex: p.MonitorIndex,
                Position: new PositionDto(p.Position.X, p.Position.Y, p.Position.Width, p.Position.Height),
                LaunchCommand: p.LaunchCommand,
                Focus: p.Focus,
                Order: p.Order
            )).ToList(),
            CreatedAt: preset.CreatedAt.ToString("O"),
            UpdatedAt: preset.UpdatedAt.ToString("O")
        );
    }

    [McpServerTool(Name = "save_layout", Title = "Save Layout", ReadOnly = false, Destructive = true, Idempotent = true, OpenWorld = false)]
    [Description("Save the current window arrangement as a named preset. Captures positions of visible windows relative to their monitors. Saving under an existing name replaces that preset (replaced=true). Fails with NoWindowsCaptured, without touching an existing preset, when no window matches the filters.")]
    public async Task<SaveLayoutResultDto> SaveLayout(
        [Description("Name for the layout preset")]
        string name,
        [Description("Optional description of the layout")]
        string? description = null,
        [Description("Only save windows from these processes (comma-separated). Leave empty to save all visible windows.")]
        string? includeProcesses = null,
        [Description("Exclude windows from these processes (comma-separated)")]
        string? excludeProcesses = null,
        CancellationToken cancellationToken = default)
    {
        var include = string.IsNullOrEmpty(includeProcesses)
            ? null
            : includeProcesses.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        var exclude = string.IsNullOrEmpty(excludeProcesses)
            ? null
            : excludeProcesses.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        var result = await _layoutService.CaptureCurrentLayoutAsync(
            name, description, include, exclude, cancellationToken);

        return new SaveLayoutResultDto(
            Success: result.Success,
            LayoutName: result.Preset?.Name ?? name,
            WindowCount: result.Preset?.Placements.Count ?? 0,
            Error: result.ErrorMessage,
            ErrorCode: result.ErrorCode,
            Replaced: result.Replaced
        );
    }

    [McpServerTool(Name = "apply_layout", Title = "Apply Layout", ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = true)]
    [Description("Apply a previously saved layout preset. Attempts to match saved windows to current windows and restore their positions. success is true only if every placement was arranged (and focused, for a placement with focus); otherwise errorCode and errors say which placements were not found, launched, arranged or focused. focus lists the outcome of each focus placement that was arranged, with the same fields as focus_window (isForeground, foregroundHandle, blockingPopup, inMenuMode, errorCode). A window of an app the user's policy denies is not arranged (TargetDeniedByPolicy).")]
    public async Task<ApplyLayoutResultDto> ApplyLayout(
        [Description("Name of the layout preset to apply")]
        string name,
        [Description("How to match saved windows to current windows: 'process_and_title' (default), 'process_only' or 'title_only'. Any other value fails with InvalidMatchBy.")]
        LayoutMatchBy matchBy = LayoutMatchBy.ProcessAndTitle,
        [Description("Run the preset's launchCommand for each placement whose window is not found (default: false). This starts the programs the preset file names; a second call can start another instance when the launched window does not match yet.")]
        bool launchMissing = false,
        IProgress<ProgressNotificationValue>? progress = null,
        CancellationToken cancellationToken = default)
    {
        // The client gets "i of n" with each placement's outcome; a cancelled call arranges no further placement
        var result = await _layoutService.ApplyPresetAsync(
            name, matchBy, launchMissing, ToolProgress.ForServices(progress), cancellationToken);

        return new ApplyLayoutResultDto(
            Success: result.Success,
            PresetName: result.PresetName,
            WindowsArranged: result.WindowsArranged,
            WindowsNotFound: result.WindowsNotFound,
            WindowsLaunched: result.WindowsLaunched,
            Errors: result.Errors,
            ErrorCode: result.ErrorCode,
            Focus: result.Focus
        );
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
}

public record LayoutSummaryDto(
    string Name,
    string? Description,
    int WindowCount,
    string CreatedAt,
    string UpdatedAt
);

public record LayoutDetailDto(
    string Name,
    string? Description,
    IReadOnlyList<PlacementDto> Placements,
    string CreatedAt,
    string UpdatedAt
);

public record PlacementDto(
    string? ProcessName,
    string? TitleContains,
    int MonitorIndex,
    PositionDto Position,
    string? LaunchCommand,
    bool Focus,
    int Order
);

public record PositionDto(double X, double Y, double Width, double Height);

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
public record ApplyLayoutResultDto(
    bool Success,
    string PresetName,
    int WindowsArranged,
    int WindowsNotFound,
    int WindowsLaunched,
    IReadOnlyList<string>? Errors,
    string? ErrorCode = null,
    IReadOnlyList<FocusResult>? Focus = null
);

public record DeleteLayoutResultDto(
    bool Success,
    string? Error = null,
    string? ErrorCode = null
);
