using System.ComponentModel;
using System.Text.RegularExpressions;
using ModelContextProtocol.Server;
using WindowsAppManagerMcp.Infrastructure;
using WindowsAppManagerMcp.Models;
using WindowsAppManagerMcp.Services;
using WindowsAppManagerMcp.Services.Interfaces;

namespace WindowsAppManagerMcp.Tools;

[McpServerToolType]
public partial class WindowFinderTools
{
    private readonly IWindowService _windowService;
    private readonly INativeHitTestWrapper? _hitTest;
    private readonly IWindowWaitService? _windowWait;
    private readonly IProcessSecurityProbe? _securityProbe;
    private readonly INativeVirtualDesktopWrapper? _virtualDesktops;

    /// <param name="windowService">Window reads and the window selectors.</param>
    /// <param name="hitTest">The client-area read of get_window_info's detail 'full'; without it clientBounds is left out.</param>
    /// <param name="windowWait">wait_for_window's waits (roadmap M3.7); the server always registers it.</param>
    /// <param name="securityProbe">
    /// The integrity read of get_window_info's detail 'full' (roadmap M6.6); the server always registers it. Without it
    /// integrityLevel, elevated and aboveServer are left out.
    /// </param>
    /// <param name="virtualDesktops">
    /// The shell's virtual desktop manager, which marks a cloaked window on another virtual desktop with isOnOtherDesktop
    /// (roadmap M7.3); the server always registers it. Without it isOnOtherDesktop is left out.
    /// </param>
    public WindowFinderTools(
        IWindowService windowService,
        INativeHitTestWrapper? hitTest = null,
        IWindowWaitService? windowWait = null,
        IProcessSecurityProbe? securityProbe = null,
        INativeVirtualDesktopWrapper? virtualDesktops = null)
    {
        _virtualDesktops = virtualDesktops;
        _windowService = windowService;
        _hitTest = hitTest;
        _windowWait = windowWait;
        _securityProbe = securityProbe;
    }

    /// <summary>The window parameters every tool shares with find_windows (roadmap M2.4).</summary>
    private const string TitleNote =
        " Titles are compared ignoring case, invisible characters (the zero-width space in 'Microsoft Edge') and Edge's ' and N more pages'.";

    [McpServerTool(Name = "find_windows", Title = "Find Windows", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("Find windows matching the specified criteria. By default only user-facing app windows are returned: no cloaked windows (other virtual desktops, suspended apps), tool windows, untitled windows or shell windows (desktop, taskbar). A handle or className, or includeShell=true, skips that filter. Returns each window's handle, title, process, className, bounds (the window rect, with the invisible resize borders), visibleBounds (the visible frame, which set_window_bounds takes) and state, front to back unless sort says otherwise; a UWP app's window reports the app's own process, with hostProcessName 'ApplicationFrameHost'. isAgentHost: true marks the agent's own terminal, editor or chat window: input tools refuse it unless it is passed as windowHandle with allowHostTarget=true, and close tools refuse it unless force=true (with confirm=true when the client cannot ask the user); use either only when the user explicitly asked." +
                 " The same filters make up the window selector that every tool taking a window accepts as window (beside handle): it must " +
                 "name exactly one window, or the call does nothing and fails with WindowNotFound or AmbiguousWindow (with up to 5 " +
                 "candidates; pick one by handle, narrow the filters, or add index, which picks a match front to back); foreground: " +
                 "true is the window in front (alone, input tools take it as no windowHandle and close tools refuse it); handle plus " +
                 "processId or a title refuses a reused handle (HandleMismatch)." + TitleNote +
                 " An invalid titleRegex or limit fails with InvalidArguments.")]
    public IReadOnlyList<WindowInfoDto> FindWindows(
        [Description("Partial text to match in window title (case-insensitive)")]
        string? titleContains = null,
        [Description("The whole process name, case-insensitive, with or without .exe (e.g., 'notepad', 'chrome'); set processNameContains to match part of it. For a UWP app, both the app process (e.g., 'CalculatorApp') and its host 'ApplicationFrameHost' match")]
        string? processName = null,
        [Description("Process ID to match. For a UWP app, both the app process and its host process match")]
        int? processId = null,
        [Description("Window handle (as integer) to find. Skips the user-facing filter")]
        long? handle = null,
        [Description("Only return visible windows (default: true)")]
        bool visibleOnly = true,
        [Description("Exact window class name to match (case-insensitive), e.g. 'Notepad', '#32770' for a dialog or 'Shell_TrayWnd' for the taskbar. Skips the user-facing filter, so untitled and shell windows of that class are found too")]
        string? className = null,
        [Description("Also return cloaked windows: windows on other virtual desktops (isOnOtherDesktop: true) and suspended apps, which are not on screen (default: false)")]
        bool includeCloaked = false,
        [Description("Also return tool windows: floating palettes and helper windows that are kept out of the taskbar and Alt+Tab (default: false)")]
        bool includeToolWindows = false,
        [Description("Skip the user-facing filter: also return the desktop, taskbar and tray, and tool, cloaked and untitled windows (default: false)")]
        bool includeShell = false,
        [Description("The whole window title (case-insensitive)")]
        string? titleEquals = null,
        [Description("A .NET regular expression the title must match (case-insensitive, 100 ms timeout), e.g. '^Untitled - Notepad$'")]
        string? titleRegex = null,
        [Description("Match processName as a part of the process name instead of the whole name (default: false)")]
        bool processNameContains = false,
        [Description("Also return untitled windows, which the user-facing filter leaves out (default: false)")]
        bool includeUntitled = false,
        [Description("Order of the result: 'zorder' (default, front to back), 'title' or 'process'")]
        WindowSort sort = WindowSort.ZOrder,
        [Description("Return at most this many windows, the first in sort order (at least 1; default: all)")]
        int? limit = null)
    {
        if (limit is < 1)
        {
            throw new ToolErrorException(ToolErrorReporting.InvalidArgumentsErrorCode,
                $"limit must be at least 1, but it is {limit}. Leave it out for every window.");
        }

        if (titleRegex is { Length: > 0 } && WindowTitles.CompileTitleRegex(titleRegex, out var invalid) is null)
        {
            throw new ToolErrorException(ToolErrorReporting.InvalidArgumentsErrorCode, invalid!);
        }

        try
        {
            var results = _windowService.FindWindows(new WindowQuery
            {
                TitleContains = titleContains,
                TitleEquals = titleEquals,
                TitleRegex = titleRegex,
                ProcessName = processName,
                ProcessNameContains = processNameContains,
                ProcessId = processId,
                Handle = handle.HasValue ? (nint)handle.Value : null,
                VisibleOnly = visibleOnly,
                ClassName = className,
                IncludeCloaked = includeCloaked,
                IncludeToolWindows = includeToolWindows,
                IncludeUntitled = includeUntitled,
                IncludeShell = includeShell,
                Sort = sort,
                Limit = limit
            });

            return MarkOtherDesktops(results.Select(ToDto).ToList());
        }
        catch (RegexMatchTimeoutException)
        {
            throw new ToolErrorException(ToolErrorReporting.InvalidArgumentsErrorCode,
                $"titleRegex '{titleRegex}' took longer than {WindowTitles.RegexTimeout.TotalMilliseconds:0} ms on a window title, so it was " +
                "given up. Use a simpler pattern, or titleContains or titleEquals.");
        }
    }

    [McpServerTool(Name = "get_window_info", Title = "Get Window Info", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("Read one window, named by handle or by a window selector, without listing every window. detail 'compact' gives handle, " +
                 "title, process and state; 'standard' (default) what find_windows reports, visibleBounds (the frame you see) included; 'full' adds " +
                 "clientBounds (the client area on screen), isForeground, zOrder (position among all top-level windows, hidden ones " +
                 "included, front to back; not window.index, and isForeground tells what is in front), isTopmost, isEnabled (false: a " +
                 "modal dialog takes its input), isResponding (false: Windows reports it hung), dpi (96 is 100%), and its process's " +
                 "integrityLevel and elevated, with aboveServer: true when it runs above this server (TargetElevated). Untitled, cloaked " +
                 "and tool windows are read too when named by handle. errorCode InvalidHandle: the handle is no window; WindowNotFound, " +
                 "AmbiguousWindow (with candidates) or HandleMismatch: the selector named no single window.")]
    public WindowDetailsResult GetWindowInfo(
        [Description("Window handle (as integer) from find_windows. Pass handle or window, not both.")]
        long? handle = null,
        [Description(WindowTargeting.WindowDescription)]
        WindowSelector? window = null,
        [Description("How much to report: 'compact', 'standard' (default) or 'full'")]
        WindowDetail detail = WindowDetail.Standard)
    {
        var hwnd = WindowTargeting.Require(_windowService, handle, window);
        if (detail == WindowDetail.Full)
        {
            if (_windowService.GetWindowDetails(hwnd) is not { } details)
            {
                return WindowDetailsResult.InvalidHandle(hwnd);
            }

            var (integrity, aboveServer) = ReadIntegrity(details.Window);
            return MarkOtherDesktop(WindowDetailsResult.Full(details, _hitTest?.GetClientAreaOnScreen(hwnd), integrity, aboveServer));
        }

        return _windowService.GetWindowInfo(hwnd) is { } info
            ? detail == WindowDetail.Compact ? WindowDetailsResult.Compact(info) : MarkOtherDesktop(WindowDetailsResult.Standard(info))
            : WindowDetailsResult.InvalidHandle(hwnd);
    }

    /// <summary>
    /// Marks the cloaked windows the shell's virtual desktop manager puts on another virtual desktop (roadmap M7.3), asked
    /// in one call; a listing without cloaked windows does not ask it.
    /// </summary>
    private List<WindowInfoDto> MarkOtherDesktops(List<WindowInfoDto> windows)
    {
        var cloaked = windows.Where(w => w.IsCloaked).Select(w => (nint)w.Handle).Distinct().ToList();
        var elsewhere = VirtualDesktops.OnOtherDesktops(_virtualDesktops, cloaked);
        return elsewhere.Count == 0
            ? windows
            : windows.Select(w => elsewhere.Contains((nint)w.Handle) ? w with { IsOnOtherDesktop = true } : w).ToList();
    }

    private WindowDetailsResult MarkOtherDesktop(WindowDetailsResult result) =>
        result.IsCloaked == true && VirtualDesktops.OnOtherDesktops(_virtualDesktops, [(nint)result.Handle]).Count > 0
            ? result with { IsOnOtherDesktop = true }
            : result;

    /// <summary>
    /// The integrity of the window's process (for a UWP frame, the app's), and whether it or the frame host runs above the
    /// server by the input guard's own test (<see cref="ProcessIntegrity.IsMorePrivilegedThan"/>), as get_ui_elements'
    /// TargetElevated warning tells it. Token reads, cached per process, which no window's thread answers; nothing without a
    /// probe.
    /// </summary>
    private (ProcessIntegrity? Integrity, bool AboveServer) ReadIntegrity(WindowInfo window)
    {
        if (_securityProbe == null)
        {
            return (null, false);
        }

        var server = _securityProbe.ServerIntegrity;
        var integrity = _securityProbe.GetIntegrity(window.ProcessId);
        var aboveServer = integrity.IsMorePrivilegedThan(server) ||
                          window.HostProcessId is { } host && host != window.ProcessId &&
                          _securityProbe.GetIntegrity(host).IsMorePrivilegedThan(server);
        return (integrity, aboveServer);
    }

    [McpServerTool(Name = "get_all_windows", Title = "List All Windows", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("Deprecated, removed in 3.0.0: use find_windows instead. Lists the visible user-facing app windows, as find_windows with no filter does.")]
    public IReadOnlyList<WindowInfoDto> GetAllWindows(
        [Description("Include minimized windows (default: true)")]
        bool includeMinimized = true)
    {
        var results = _windowService.GetAllWindows(includeMinimized);
        return results.Select(ToDto).ToList();
    }

    [McpServerTool(Name = "get_foreground_window", Title = "Get Foreground Window", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("Get information about the currently focused (foreground) window. Untitled windows such as menus, popups and the taskbar are reported too, with an empty title and their className. When no window is in the foreground (for a moment while windows switch, or while a UAC prompt or the lock screen is shown), the call fails with errorCode NoForegroundWindow: call it again shortly.")]
    public WindowInfoDto GetForegroundWindow()
    {
        // Never null: the SDK sends null as empty content, which tells the agent nothing (roadmap M2.1)
        var window = _windowService.GetForegroundWindow()
            ?? throw new ToolErrorException(nameof(WindowErrorCode.NoForegroundWindow),
                "No window is in the foreground right now: windows may be switching, or a UAC prompt or the lock screen is shown. " +
                "Call get_foreground_window again shortly; do not send keyboard input until a window is in the foreground.");
        return ToDto(window);
    }

    [McpServerTool(Name = "get_owned_windows", Title = "Get Owned Windows", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("List the windows a window owns, directly or through its other owned windows, in z-order: its dialogs (Save As, " +
                 "message boxes, 'Save changes?' prompts), popups and palettes. Each has handle, title, className ('#32770' is a " +
                 "standard dialog, also isDialog), process, ownerHandle, bounds, state and isVisible; isBlocking: true marks the modal " +
                 "dialog that takes the window's input (the prompt close_window reports), and isBlocked says the window has one. Hidden helper " +
                 "windows (IME) only with includeHidden. Dialogs drawn inside the window (a WinUI ContentDialog) are not windows: " +
                 "use find_ui_elements and element_action. errorCode InvalidHandle: the handle is not a window.")]
    public OwnedWindowsResult GetOwnedWindows(
        [Description("Window handle (as integer) of the owner. Pass handle or window, not both.")]
        long? handle = null,
        [Description(WindowTargeting.WindowDescription)]
        WindowSelector? window = null,
        [Description("Also list hidden owned windows, such as IME and other helper windows (default: false)")]
        bool includeHidden = false)
    {
        var owner = WindowTargeting.Require(_windowService, handle, window);
        handle = owner.ToInt64();
        if (!_windowService.IsValidWindow(owner))
        {
            return new OwnedWindowsResult(false, handle.Value, Error: WindowService.InvalidHandleMessage, ErrorCode: nameof(WindowErrorCode.InvalidHandle));
        }

        var owned = _windowService.GetOwnedWindows(owner, includeHidden);
        return new OwnedWindowsResult(true, handle.Value, owned.Any(o => o.IsBlocking), owned.Select(o => new OwnedWindowDto(
            Handle: o.Window.Handle.ToInt64(),
            Title: AppText.Cut(o.Window.Title),
            ClassName: o.Window.ClassName,
            ProcessName: o.Window.ProcessName,
            ProcessId: o.Window.ProcessId,
            OwnerHandle: o.Window.OwnerHandle?.ToInt64(),
            Bounds: new BoundsDto(o.Window.Bounds.X, o.Window.Bounds.Y, o.Window.Bounds.Width, o.Window.Bounds.Height),
            State: o.Window.State.ToString().ToLowerInvariant(),
            IsVisible: o.Window.IsVisible,
            IsDialog: o.IsDialog,
            IsBlocking: o.IsBlocking ? true : null)).ToList());
    }

    /// <summary>
    /// A window as the tools return it (the launch tools return the window they found this way too), its title cut to
    /// <see cref="AppText.MaxLength"/> characters (roadmap M3.F14).
    /// </summary>
    internal static WindowInfoDto ToDto(WindowInfo info) => new(
        Handle: info.Handle.ToInt64(),
        Title: AppText.Cut(info.Title),
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
        IsAgentHost: info.IsAgentHost ? true : null,
        VisibleBounds: info.VisibleBounds is { } visible ? new BoundsDto(visible.X, visible.Y, visible.Width, visible.Height) : null,
        IsTopmost: info.IsTopmost ? true : null
    );
}

/// <summary>
/// A window as the window tools return it. OwnerHandle, HostProcessName and HostProcessId are
/// omitted when null: an unowned window, or a window that is not a UWP frame. IsAgentHost is true for
/// the agent's own host window (its terminal, editor or chat app, or a window it owns) and omitted
/// for every other window. Bounds is the window rect (GetWindowRect, with the invisible resize borders of about 7 px left,
/// right and bottom); VisibleBounds is the visible frame, the rect set_window_bounds, move_window and the geometry
/// results use (omitted when DWM cannot report it). Title is cut to <see cref="AppText.MaxLength"/> characters, the
/// ellipsis of a cut one included (roadmap M3.F14); the filters compare the whole title. IsTopmost is true for a topmost
/// window and omitted for every other (roadmap M6.7). IsOnOtherDesktop is true for a cloaked window the shell's virtual
/// desktop manager puts on another virtual desktop, and omitted for every other (roadmap M7.3); the server never switches
/// desktops or moves a window between them.
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
    bool? IsAgentHost = null,
    BoundsDto? VisibleBounds = null,
    bool? IsTopmost = null,
    bool? IsOnOtherDesktop = null
);

/// <summary>
/// get_window_info's result (roadmap M2.4): one window at the detail asked for. Compact has handle, title, processName,
/// processId and state; standard adds what find_windows reports (VisibleBounds included); full adds the fields after
/// VisibleBounds, the integrity of the window's process among them (roadmap M6.6; AboveServer only when true). Fields a
/// detail level does not report are left out. IsOnOtherDesktop (standard and full) is as find_windows reports it.
/// </summary>
public sealed record WindowDetailsResult(
    bool Success,
    long Handle,
    string? Title = null,
    string? ProcessName = null,
    int? ProcessId = null,
    string? State = null,
    BoundsDto? Bounds = null,
    bool? IsVisible = null,
    int? MonitorIndex = null,
    string? ClassName = null,
    bool? IsCloaked = null,
    long? OwnerHandle = null,
    string? HostProcessName = null,
    int? HostProcessId = null,
    bool? IsAgentHost = null,
    BoundsDto? VisibleBounds = null,
    BoundsDto? ClientBounds = null,
    bool? IsForeground = null,
    int? ZOrder = null,
    bool? IsTopmost = null,
    bool? IsEnabled = null,
    bool? IsResponding = null,
    int? Dpi = null,
    IntegrityLevel? IntegrityLevel = null,
    bool? Elevated = null,
    bool? AboveServer = null,
    bool? IsOnOtherDesktop = null,
    string? Error = null,
    string? ErrorCode = null)
{
    internal static WindowDetailsResult Compact(WindowInfo info) => new(
        true, info.Handle.ToInt64(), AppText.Cut(info.Title), info.ProcessName, info.ProcessId, info.State.ToString().ToLowerInvariant());

    internal static WindowDetailsResult Standard(WindowInfo info)
    {
        var dto = WindowFinderTools.ToDto(info);
        return new WindowDetailsResult(true, dto.Handle, dto.Title, dto.ProcessName, dto.ProcessId, dto.State, dto.Bounds, dto.IsVisible,
            dto.MonitorIndex, dto.ClassName, dto.IsCloaked, dto.OwnerHandle, dto.HostProcessName, dto.HostProcessId, dto.IsAgentHost,
            VisibleBounds: dto.VisibleBounds, IsTopmost: dto.IsTopmost);
    }

    internal static WindowDetailsResult Full(
        WindowDetails details, WindowRect? clientArea, ProcessIntegrity? integrity = null, bool aboveServer = false) => Standard(details.Window) with
    {
        VisibleBounds = details.Window.VisibleBounds is { } visible ? new BoundsDto(visible.X, visible.Y, visible.Width, visible.Height) : null,
        ClientBounds = clientArea is { } client ? new BoundsDto(client.X, client.Y, client.Width, client.Height) : null,
        IsForeground = details.IsForeground,
        ZOrder = details.ZOrder,
        IsTopmost = details.IsTopmost,
        IsEnabled = details.IsEnabled,
        IsResponding = details.IsResponding,
        Dpi = details.Dpi,
        IntegrityLevel = integrity?.Level,
        Elevated = integrity?.IsElevated,
        AboveServer = aboveServer ? true : null
    };

    internal static WindowDetailsResult InvalidHandle(nint handle) => new(
        false, handle.ToInt64(), Error: $"{WindowService.InvalidHandleMessage}: {handle} is no window (it closed). Find it again with find_windows.",
        ErrorCode: nameof(WindowErrorCode.InvalidHandle));
}
