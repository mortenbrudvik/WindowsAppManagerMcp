namespace WindowsAppManagerMcp.Models;

public record LayoutPreset(
    string Name,
    string? Description,
    List<WindowPlacement> Placements
)
{
    /// <summary>
    /// Schema version for forward compatibility. Increment when making breaking changes.
    /// </summary>
    public int Version { get; init; } = 1;

    public DateTime CreatedAt { get; init; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; init; } = DateTime.UtcNow;
}
