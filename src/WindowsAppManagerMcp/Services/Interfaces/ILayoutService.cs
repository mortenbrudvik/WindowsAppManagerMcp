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

    Task<LayoutApplyResult> ApplyPresetAsync(
        string name,
        string matchBy = "process_and_title",
        bool launchMissing = false,
        CancellationToken cancellationToken = default);
}
