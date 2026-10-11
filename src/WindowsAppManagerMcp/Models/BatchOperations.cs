namespace WindowsAppManagerMcp.Models;

// Input models for batch operations
public record WindowBoundsPlacement(
    long Handle,
    int X,
    int Y,
    int Width,
    int Height,
    bool BringToFront = true
);

/// <summary>
/// A snap_windows_batch item. Position is checked per item (an unknown one fails that item with InvalidSnapPosition);
/// the input schema lists the positions.
/// </summary>
public record WindowSnapPlacement(
    long Handle,
    [ToolValues(typeof(SnapPosition))] string Position,
    int? MonitorIndex = null,
    bool BringToFront = true
);

/// <summary>
/// A set_windows_state_batch item. State is checked per item (an unknown one fails that item with InvalidState); the
/// input schema lists the states.
/// </summary>
public record WindowStateChange(
    long Handle,
    [ToolValues(typeof(WindowStateRequest))] string State
);

public record ApplicationLaunchRequest(
    string Executable,
    string[]? Arguments = null,
    string? WorkingDirectory = null
);

public record WindowCloseRequest(long Handle);

// Output models for batch operations
public record BatchWindowResult(
    int TotalRequested,
    int Succeeded,
    int Failed,
    IReadOnlyList<WindowResultItem> Results
);

/// <summary>
/// One window of a batch. For the geometry batches (roadmap M2.3) the window as it is afterwards, failed or not (left out
/// for an invalid handle): Bounds is its visible frame, OuterBounds its window rect with the invisible resize borders (the
/// bounds find_windows reports), State normal, minimized or maximized, MonitorIndex its monitor. A minimized window has no
/// bounds. Status and Prompt are close_windows_batch's (roadmap M2.5): what became of the window after its close message,
/// and the prompt that keeps it open; the other batches leave them out.
/// </summary>
public record WindowResultItem(
    long Handle,
    bool Success,
    BoundsDto? Bounds = null,
    BoundsDto? OuterBounds = null,
    string? State = null,
    int? MonitorIndex = null,
    string? Error = null,
    string? ErrorCode = null,
    string? Status = null,
    ClosePromptDto? Prompt = null
);

public record BatchLaunchResult(
    int TotalRequested,
    int Succeeded,
    int Failed,
    IReadOnlyList<LaunchResultItem> Results
);

public record LaunchResultItem(
    string Executable,
    bool Success,
    int? ProcessId = null,
    long? WindowHandle = null,
    string? Error = null,
    string? Warning = null,
    string? ErrorCode = null
);

public record BoundsDto(int X, int Y, int Width, int Height);
