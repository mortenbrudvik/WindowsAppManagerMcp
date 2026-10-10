using WindowsAppManagerMcp.Models;
using WindowsAppManagerMcp.Models.Results;

namespace WindowsAppManagerMcp.Services.Interfaces;

public interface ILayoutService
{
    /// <summary>
    /// Folder the preset JSON files are stored in.
    /// </summary>
    string LayoutsPath { get; }

    /// <summary>
    /// Prepares the storage folder if needed (creating it, migrating presets from older
    /// versions and seeding the sample presets on first use).
    /// </summary>
    /// <returns>Null when storage is usable; otherwise a message explaining why it is not.</returns>
    string? GetStorageError();

    /// <summary>
    /// Returns all presets currently on disk, sorted by name.
    /// </summary>
    IReadOnlyList<LayoutPreset> GetAllPresets();

    /// <summary>
    /// Returns the preset with the given name (case-insensitive) as it is currently on disk, or null.
    /// </summary>
    LayoutPreset? GetPreset(string name);

    Task<LayoutSaveResult> SavePresetAsync(LayoutPreset preset, CancellationToken cancellationToken = default);

    Task<LayoutDeleteResult> DeletePresetAsync(string name, CancellationToken cancellationToken = default);

    /// <summary>
    /// Captures the visible windows and saves them as a preset.
    /// </summary>
    Task<LayoutSaveResult> CaptureCurrentLayoutAsync(
        string name,
        string? description = null,
        string[]? includeProcesses = null,
        string[]? excludeProcesses = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Arranges the windows of a preset, placement by placement in order, launching missing applications when
    /// <paramref name="launchMissing"/> is set.
    /// </summary>
    /// <param name="progress">Receives "i of n" with each placement's outcome. Optional.</param>
    /// <param name="cancellationToken">
    /// Cancels the call between placements and during a launch's wait for its window; the placements already arranged
    /// stay arranged, and the call throws <see cref="OperationCanceledException"/>.
    /// </param>
    Task<LayoutApplyResult> ApplyPresetAsync(
        string name,
        string matchBy = "process_and_title",
        bool launchMissing = false,
        IProgress<OperationProgress>? progress = null,
        CancellationToken cancellationToken = default);
}
