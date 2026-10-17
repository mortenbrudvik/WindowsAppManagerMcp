namespace WindowsAppManagerMcp.Models;

/// <summary>
/// A layout preset as its JSON file holds it. Schema version 2 (roadmap M6.8) adds, per placement, the monitor's identity, the
/// restore rectangle, a snap position and what to launch, and keeps every version 1 field filled, so an older server still
/// applies a version 2 file (it ignores the fields it does not know). A version 1 file is upgraded when it is read
/// (<see cref="Services.LayoutPresetSchema.Upgrade"/>); the file itself is never rewritten.
/// </summary>
public record LayoutPreset(
    string Name,
    string? Description,
    List<WindowPlacement> Placements
)
{
    /// <summary>
    /// The schema version the file was written with: 1 for a file without one. The server writes
    /// <see cref="Services.LayoutPresetSchema.CurrentVersion"/>; a file of a later version is read as far as this server
    /// knows its fields, with a warning.
    /// </summary>
    public int Version { get; init; } = 1;

    public DateTime CreatedAt { get; init; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; init; } = DateTime.UtcNow;

    /// <summary>
    /// "agent" when an agent wrote the placements (save_layout with placements, roadmap M6.8), so apply_layout asks the user
    /// before it launches anything for them; null when save_layout captured the open windows or a person wrote the file.
    /// </summary>
    public string? AuthoredBy { get; init; }
}
