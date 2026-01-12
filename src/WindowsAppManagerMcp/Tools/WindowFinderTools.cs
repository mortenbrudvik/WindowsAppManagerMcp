using System.ComponentModel;
using ModelContextProtocol.Server;
using WindowsAppManagerMcp.Models;
using WindowsAppManagerMcp.Services.Interfaces;

namespace WindowsAppManagerMcp.Tools;

[McpServerToolType]
public class WindowFinderTools
{
    private readonly IWindowService _windowService;

    public WindowFinderTools(IWindowService windowService)
    {
        _windowService = windowService;
    }

    [McpServerTool(Name = "find_windows")]
    [Description("Find windows matching the specified criteria. Returns a list of matching windows with their properties including handle, title, process name, bounds, and state.")]
    public IReadOnlyList<WindowInfoDto> FindWindows(
        [Description("Partial text to match in window title (case-insensitive)")]
        string? titleContains = null,
        [Description("Process name to match (e.g., 'notepad', 'chrome')")]
        string? processName = null,
        [Description("Process ID to match")]
        int? processId = null,
        [Description("Window handle (as integer) to find")]
        long? handle = null,
        [Description("Only return visible windows (default: true)")]
        bool visibleOnly = true)
    {
        var results = _windowService.FindWindows(
            titleContains: titleContains,
            processName: processName,
            processId: processId,
            handle: handle.HasValue ? (nint)handle.Value : null,
            visibleOnly: visibleOnly);

        return results.Select(ToDto).ToList();
    }

    [McpServerTool(Name = "get_all_windows")]
    [Description("List all visible top-level windows on the system. Returns window information including handle, title, process, bounds, and state.")]
    public IReadOnlyList<WindowInfoDto> GetAllWindows(
        [Description("Include minimized windows (default: true)")]
        bool includeMinimized = true)
    {
        var results = _windowService.GetAllWindows(includeMinimized);
        return results.Select(ToDto).ToList();
    }

    [McpServerTool(Name = "get_foreground_window")]
    [Description("Get information about the currently focused (foreground) window.")]
    public WindowInfoDto? GetForegroundWindow()
    {
        var window = _windowService.GetForegroundWindow();
        return window != null ? ToDto(window) : null;
    }

    private static WindowInfoDto ToDto(WindowInfo info) => new(
        Handle: info.Handle.ToInt64(),
        Title: info.Title,
        ProcessName: info.ProcessName,
        ProcessId: info.ProcessId,
        Bounds: new BoundsDto(info.Bounds.X, info.Bounds.Y, info.Bounds.Width, info.Bounds.Height),
        State: info.State.ToString().ToLowerInvariant(),
        IsVisible: info.IsVisible,
        MonitorIndex: info.MonitorIndex
    );
}

public record WindowInfoDto(
    long Handle,
    string Title,
    string ProcessName,
    int ProcessId,
    BoundsDto Bounds,
    string State,
    bool IsVisible,
    int MonitorIndex
);

public record BoundsDto(int X, int Y, int Width, int Height);
