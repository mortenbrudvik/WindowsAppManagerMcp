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

public record WindowResultItem(
    long Handle,
    bool Success,
    BoundsDto? NewBounds = null,
    string? NewState = null,
    string? Error = null,
    string? ErrorCode = null
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
