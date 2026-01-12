using WindowsAppManagerMcp.Models;
using WindowsAppManagerMcp.Models.Results;

namespace WindowsAppManagerMcp.Services.Interfaces;

public interface ILayoutService
{
    IReadOnlyList<LayoutPreset> GetAllPresets();

    LayoutPreset? GetPreset(string name);

    Task<bool> SavePresetAsync(LayoutPreset preset, CancellationToken cancellationToken = default);

    Task<bool> DeletePresetAsync(string name, CancellationToken cancellationToken = default);

    Task<LayoutPreset> CaptureCurrentLayoutAsync(
        string name,
        string? description = null,
        string[]? includeProcesses = null,
        string[]? excludeProcesses = null,
        CancellationToken cancellationToken = default);

    Task<LayoutApplyResult> ApplyPresetAsync(
        string name,
        string matchBy = "process_and_title",
        bool launchMissing = false,
        CancellationToken cancellationToken = default);
}
