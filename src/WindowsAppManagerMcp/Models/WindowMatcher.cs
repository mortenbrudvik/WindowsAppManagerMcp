namespace WindowsAppManagerMcp.Models;

/// <summary>
/// Which open window a layout placement is for. <see cref="TitleContains"/> and <see cref="ProcessName"/> are the
/// criteria apply_layout's matchBy filters on; the identity fields save_layout records (<see cref="TitlePattern"/>,
/// <see cref="ClassName"/>, <see cref="ExePath"/>, <see cref="Ordinal"/>) only rank the windows that pass, so a
/// preset tells apart several windows of one application.
/// </summary>
/// <param name="TitleContains">Text the window title must contain (case-insensitive); null for no title criterion.</param>
/// <param name="ProcessName">
/// The process name: part of the window's process name (case-insensitive), or with <see cref="ExactProcessName"/> the
/// whole name. A Store (UWP) app's window matches its own process and ApplicationFrameHost. Null for no process criterion.
/// </param>
/// <param name="ClassName">The window class at capture time (for example "CabinetWClass"); ranks candidates, never filters.</param>
public record WindowMatcher(
    string? TitleContains = null,
    string? ProcessName = null,
    string? ClassName = null
)
{
    /// <summary>
    /// True when <see cref="ProcessName"/> must equal the window's process name (case-insensitive), as save_layout
    /// records it, so "Code" does not match "Code - Insiders". False (hand-written presets) matches part of the name.
    /// </summary>
    public bool ExactProcessName { get; init; }

    /// <summary>
    /// The window title at capture time. apply_layout prefers the window whose title equals it, then the one that
    /// shares most of its parts (split at " - ", " | " and dashes); it never filters.
    /// </summary>
    public string? TitlePattern { get; init; }

    /// <summary>The full path of the window's executable at capture time; ranks candidates and is never launched.</summary>
    public string? ExePath { get; init; }

    /// <summary>
    /// The window's position in the z-order among the captured windows of the same process (0 is the top one); a weak
    /// tie-breaker between otherwise equal windows.
    /// </summary>
    public int? Ordinal { get; init; }
}
