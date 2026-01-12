using System.ComponentModel;
using ModelContextProtocol.Server;
using WindowsAppManagerMcp.Models;
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

    [McpServerTool(Name = "list_layouts")]
    [Description("List all saved layout presets. Returns preset names, descriptions, and window counts.")]
    public IReadOnlyList<LayoutSummaryDto> ListLayouts()
    {
        var presets = _layoutService.GetAllPresets();
        return presets.Select(p => new LayoutSummaryDto(
            Name: p.Name,
            Description: p.Description,
            WindowCount: p.Placements.Count,
            CreatedAt: p.CreatedAt.ToString("O"),
            UpdatedAt: p.UpdatedAt.ToString("O")
        )).ToList();
    }

    [McpServerTool(Name = "get_layout")]
    [Description("Get the full definition of a layout preset including all window placements.")]
    public LayoutDetailDto? GetLayout(
        [Description("Name of the layout preset")]
        string name)
    {
        var preset = _layoutService.GetPreset(name);
        if (preset == null)
            return null;

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

    [McpServerTool(Name = "save_layout")]
    [Description("Save the current window arrangement as a named preset. Captures positions of visible windows relative to their monitors.")]
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

        var preset = await _layoutService.CaptureCurrentLayoutAsync(
            name, description, include, exclude, cancellationToken);

        return new SaveLayoutResultDto(
            Success: true,
            LayoutName: preset.Name,
            WindowCount: preset.Placements.Count
        );
    }

    [McpServerTool(Name = "apply_layout")]
    [Description("Apply a previously saved layout preset. Attempts to match saved windows to current windows and restore their positions.")]
    public async Task<ApplyLayoutResultDto> ApplyLayout(
        [Description("Name of the layout preset to apply")]
        string name,
        [Description("How to match saved windows to current windows: 'process_and_title', 'process_only', or 'title_only' (default: process_and_title)")]
        string matchBy = "process_and_title",
        [Description("Attempt to launch applications for missing windows (default: false)")]
        bool launchMissing = false,
        CancellationToken cancellationToken = default)
    {
        var result = await _layoutService.ApplyPresetAsync(name, matchBy, launchMissing, cancellationToken);

        return new ApplyLayoutResultDto(
            Success: result.Success,
            PresetName: result.PresetName,
            WindowsArranged: result.WindowsArranged,
            WindowsNotFound: result.WindowsNotFound,
            WindowsLaunched: result.WindowsLaunched,
            Errors: result.Errors
        );
    }

    [McpServerTool(Name = "delete_layout")]
    [Description("Delete a layout preset.")]
    public async Task<DeleteLayoutResultDto> DeleteLayout(
        [Description("Name of the preset to delete")]
        string name,
        CancellationToken cancellationToken = default)
    {
        var success = await _layoutService.DeletePresetAsync(name, cancellationToken);
        return new DeleteLayoutResultDto(
            Success: success,
            Error: success ? null : "Failed to delete layout preset"
        );
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
    string? Error = null
);

public record ApplyLayoutResultDto(
    bool Success,
    string PresetName,
    int WindowsArranged,
    int WindowsNotFound,
    int WindowsLaunched,
    IReadOnlyList<string>? Errors
);

public record DeleteLayoutResultDto(
    bool Success,
    string? Error = null
);
