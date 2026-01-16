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

public record WindowSnapPlacement(
    long Handle,
    string Position,
    int? MonitorIndex = null,
    bool BringToFront = true
);

public record WindowStateChange(
    long Handle,
    string State
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
    string? Error = null
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
    string? Warning = null
);

public record BoundsDto(int X, int Y, int Width, int Height);
