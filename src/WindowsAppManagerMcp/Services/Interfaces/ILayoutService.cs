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

    /// <summary>
    /// The *.json files in the folder that are not valid presets, as they are currently on disk, each with why, sorted by file
    /// name (review finding F1): they are left out of <see cref="GetAllPresets"/>, so list_layouts lists them apart, and a
    /// preset that is not found names the one that may hold it (<see cref="LayoutService.NotFoundNote"/>). Empty when the
    /// storage is unavailable.
    /// </summary>
    IReadOnlyList<InvalidLayoutFile> GetInvalidFiles();

    Task<LayoutSaveResult> SavePresetAsync(LayoutPreset preset, CancellationToken cancellationToken = default);

    Task<LayoutDeleteResult> DeletePresetAsync(string name, CancellationToken cancellationToken = default);

    /// <summary>
    /// Captures the visible windows and saves them as a preset; minimized ones too with <paramref name="includeMinimized"/>
    /// (roadmap M6.8).
    /// </summary>
    Task<LayoutSaveResult> CaptureCurrentLayoutAsync(
        string name,
        string? description = null,
        string[]? includeProcesses = null,
        string[]? excludeProcesses = null,
        bool includeMinimized = false,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Saves the placements an agent wrote as a preset (roadmap M6.8), marked as agent-written
    /// (<see cref="LayoutPreset.AuthoredBy"/>). Each is checked first (<see cref="LayoutPresetSchema.TryBuildPlacement"/>):
    /// one that is not valid fails the save with InvalidPlacement, and nothing is written.
    /// </summary>
    Task<LayoutSaveResult> SaveAuthoredLayoutAsync(
        string name,
        string? description,
        IReadOnlyList<LayoutPlacementSpec> placements,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Whether the user confirmed that this version of an agent-written preset may start its programs (owner decision D15).
    /// True for a preset no agent wrote.
    /// </summary>
    bool AreLaunchesConfirmed(LayoutPreset preset);

    /// <summary>
    /// Records the user's confirmation for this version of the preset (<see cref="LayoutPresetSchema.LaunchFingerprint"/>):
    /// it holds until the preset is saved again or its launches change, across restarts when the layouts folder can be written.
    /// </summary>
    void ConfirmLaunches(LayoutPreset preset);

    /// <summary>
    /// Arranges the windows of a preset, placement by placement in order, launching missing applications when
    /// <paramref name="launchMissing"/> is set.
    /// </summary>
    /// <param name="progress">Receives "i of n" with each placement's outcome. Optional.</param>
    /// <param name="cancellationToken">
    /// Cancels the call between placements and during a launch's wait for its window; the placements already arranged
    /// stay arranged, and the call throws <see cref="OperationCanceledException"/>.
    /// </param>
    /// <param name="options">A dry run, a subset of the placements, and why launches were not confirmed (roadmap M6.8).</param>
    Task<LayoutApplyResult> ApplyPresetAsync(
        string name,
        LayoutMatchBy matchBy = LayoutMatchBy.ProcessAndTitle,
        bool launchMissing = false,
        LayoutApplyOptions? options = null,
        IProgress<OperationProgress>? progress = null,
        CancellationToken cancellationToken = default);
}
