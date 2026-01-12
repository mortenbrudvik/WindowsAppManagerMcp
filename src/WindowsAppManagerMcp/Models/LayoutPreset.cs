namespace WindowsAppManagerMcp.Models;

public record LayoutPreset(
    string Name,
    string? Description,
    List<WindowPlacement> Placements
)
{
    public DateTime CreatedAt { get; init; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; init; } = DateTime.UtcNow;
}
