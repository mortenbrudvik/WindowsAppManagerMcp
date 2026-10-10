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
    [Description("Find windows matching the specified criteria. By default only user-facing app windows are returned: no cloaked windows (other virtual desktops, suspended apps), tool windows, untitled windows or shell windows (desktop, taskbar). A handle or className, or includeShell=true, skips that filter. Returns each window's handle, title, process, className, bounds and state; a UWP app's window reports the app's own process, with hostProcessName 'ApplicationFrameHost'. isAgentHost: true marks the agent's own terminal, editor or chat window, which input and close tools refuse unless it is named explicitly.")]
    public IReadOnlyList<WindowInfoDto> FindWindows(
        [Description("Partial text to match in window title (case-insensitive)")]
        string? titleContains = null,
        [Description("Process name to match (e.g., 'notepad', 'chrome'). For a UWP app, both the app process (e.g., 'CalculatorApp') and its host 'ApplicationFrameHost' match")]
        string? processName = null,
        [Description("Process ID to match. For a UWP app, both the app process and its host process match")]
        int? processId = null,
        [Description("Window handle (as integer) to find. Skips the user-facing filter")]
        long? handle = null,
        [Description("Only return visible windows (default: true)")]
        bool visibleOnly = true,
        [Description("Exact window class name to match (case-insensitive), e.g. 'Notepad', '#32770' for a dialog or 'Shell_TrayWnd' for the taskbar. Skips the user-facing filter, so untitled and shell windows of that class are found too")]
        string? className = null,
        [Description("Also return cloaked windows: windows on other virtual desktops and suspended apps, which are not on screen (default: false)")]
        bool includeCloaked = false,
        [Description("Also return tool windows: floating palettes and helper windows that are kept out of the taskbar and Alt+Tab (default: false)")]
        bool includeToolWindows = false,
        [Description("Skip the user-facing filter: also return the desktop, taskbar and tray, and tool, cloaked and untitled windows (default: false)")]
        bool includeShell = false)
    {
        var results = _windowService.FindWindows(new WindowQuery
        {
            TitleContains = titleContains,
            ProcessName = processName,
            ProcessId = processId,
            Handle = handle.HasValue ? (nint)handle.Value : null,
            VisibleOnly = visibleOnly,
            ClassName = className,
            IncludeCloaked = includeCloaked,
            IncludeToolWindows = includeToolWindows,
            IncludeShell = includeShell
        });

        return results.Select(ToDto).ToList();
    }

    [McpServerTool(Name = "get_all_windows")]
    [Description("List the visible user-facing app windows on the system: no cloaked, tool, untitled or shell windows (use find_windows with className, includeCloaked, includeToolWindows or includeShell for those). Returns window information including handle, title, process, className, bounds, and state.")]
    public IReadOnlyList<WindowInfoDto> GetAllWindows(
        [Description("Include minimized windows (default: true)")]
        bool includeMinimized = true)
    {
        var results = _windowService.GetAllWindows(includeMinimized);
        return results.Select(ToDto).ToList();
    }

    [McpServerTool(Name = "get_foreground_window")]
    [Description("Get information about the currently focused (foreground) window. Untitled windows such as menus, popups and the taskbar are reported too, with an empty title and their className. Null means no window is in the foreground.")]
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
        MonitorIndex: info.MonitorIndex,
        ClassName: info.ClassName,
        IsCloaked: info.IsCloaked,
        OwnerHandle: info.OwnerHandle?.ToInt64(),
        HostProcessName: info.HostProcessName,
        HostProcessId: info.HostProcessId,
        IsAgentHost: info.IsAgentHost ? true : null
    );
}

/// <summary>
/// A window as the window tools return it. OwnerHandle, HostProcessName and HostProcessId are
/// omitted when null: an unowned window, or a window that is not a UWP frame. IsAgentHost is true for
/// the agent's own host window (its terminal, editor or chat app, or a window it owns) and omitted
/// for every other window.
/// </summary>
public record WindowInfoDto(
    long Handle,
    string Title,
    string ProcessName,
    int ProcessId,
    BoundsDto Bounds,
    string State,
    bool IsVisible,
    int MonitorIndex,
    string ClassName,
    bool IsCloaked,
    long? OwnerHandle = null,
    string? HostProcessName = null,
    int? HostProcessId = null,
    bool? IsAgentHost = null
);
