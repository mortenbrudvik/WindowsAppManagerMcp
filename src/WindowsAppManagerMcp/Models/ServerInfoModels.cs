namespace WindowsAppManagerMcp.Models;

/// <summary>Result of get_server_info: the server's build and the environment it works in, in one read.</summary>
/// <param name="Server">What is running.</param>
/// <param name="Connection">The MCP session the call came in on.</param>
/// <param name="Process">The server process: integrity, elevation and DPI awareness.</param>
/// <param name="Desktop">Whether input can reach the desktop now, and the foreground lock timeout.</param>
/// <param name="Monitors">The monitors with their DPI, in get_monitors order.</param>
/// <param name="Host">The agent's host as recorded at server start: windows and processes input never reaches.</param>
/// <param name="Input">The user's pause and the pause hotkey.</param>
/// <param name="Safety">The target policy and the destructive chords in force.</param>
/// <param name="Paths">Where the server keeps its files.</param>
/// <param name="Toolsets">The toolsets, exclusions and read-only mode in force.</param>
/// <param name="Warnings">What will make tools fail or coordinates wrong, one sentence each; null when nothing does.</param>
/// <param name="UiAutomation">The UI Automation engine the element tools use (roadmap M3.2).</param>
/// <param name="PinnedWindows">
/// The windows set_window_zorder made topmost that still are (roadmap M6.7, owner decision D14); null when there are none.
/// </param>
public sealed record ServerInfoResult(
    ServerBuildInfo Server,
    ServerConnectionInfo Connection,
    ServerProcessInfo Process,
    DesktopStateInfo Desktop,
    IReadOnlyList<ServerMonitorInfo> Monitors,
    AgentHostInfo Host,
    InputControlInfo Input,
    SafetySettingsInfo Safety,
    ServerPathsInfo Paths,
    ToolsetInfo Toolsets,
    IReadOnlyList<string>? Warnings,
    UiAutomationInfo? UiAutomation = null,
    IReadOnlyList<PinnedWindowInfo>? PinnedWindows = null);

/// <summary>A window this server made topmost (set_window_zorder) that still is: it stays in front until it is unpinned.</summary>
/// <param name="Handle">The window handle.</param>
/// <param name="Title">Its title now, cut as find_windows cuts it.</param>
/// <param name="ProcessName">Its process's name, or null when it cannot be read.</param>
public sealed record PinnedWindowInfo(long Handle, string? Title, string? ProcessName);

/// <summary>The UI Automation engine behind get_ui_elements, get_focused_element, get_element_at_point and verified close.</summary>
/// <param name="Engine">Uia3 (the COM client, the default) or Uia2 (the managed client, WAM_UIA_ENGINE=uia2).</param>
/// <param name="Source">"default", or WAM_UIA_ENGINE.</param>
/// <param name="ConnectionTimeoutMs">How long a UIA3 call waits to connect to an application; null for Uia2, which has none.</param>
/// <param name="TransactionTimeoutMs">How long a UIA3 call waits for an application to answer; null for Uia2, which has none.</param>
public sealed record UiAutomationInfo(
    UiaEngine Engine,
    string Source,
    int? ConnectionTimeoutMs,
    int? TransactionTimeoutMs);

/// <summary>What is running.</summary>
/// <param name="Name">The MCP server name, windows-app-manager.</param>
/// <param name="Version">The product version with the commit it was built from, as serverInfo.version.</param>
/// <param name="GitSha">The commit, or null when the build has none.</param>
/// <param name="SdkVersion">The version of the MCP C# SDK (ModelContextProtocol).</param>
/// <param name="Runtime">The .NET runtime.</param>
/// <param name="Os">The Windows version.</param>
public sealed record ServerBuildInfo(string Name, string Version, string? GitSha, string SdkVersion, string Runtime, string Os);

/// <summary>The MCP session.</summary>
/// <param name="ProtocolVersion">The negotiated MCP revision, or null when the SDK does not report one.</param>
/// <param name="ClientName">The client's name from its clientInfo, or null.</param>
/// <param name="ClientVersion">The client's version from its clientInfo, or null.</param>
public sealed record ServerConnectionInfo(string? ProtocolVersion, string? ClientName, string? ClientVersion);

/// <summary>The server process.</summary>
/// <param name="ProcessId">The server's process ID.</param>
/// <param name="SessionId">The Windows session it runs in, or null when it cannot be read.</param>
/// <param name="IntegrityLevel">Its integrity level. Windows drops input to windows of a higher level (TargetElevated).</param>
/// <param name="Elevated">Whether its token is elevated (run as administrator), or null when it cannot be read.</param>
/// <param name="DpiAwareness">
/// The DPI awareness in force; PerMonitorV2 (from the app manifest), or its fallback PerMonitor on Windows before 10 1703,
/// means every coordinate is a physical pixel.
/// </param>
public sealed record ServerProcessInfo(int ProcessId, int? SessionId, IntegrityLevel IntegrityLevel, bool? Elevated, DpiAwareness DpiAwareness);

/// <summary>Whether input can reach the desktop now.</summary>
/// <param name="InputDesktopAvailable">
/// False while the secure desktop (UAC prompt, lock screen, Ctrl+Alt+Del) is shown or the session is disconnected:
/// input and captures fail (DesktopUnavailable).
/// </param>
/// <param name="SessionLocked">True while the session is locked (SessionLocked).</param>
/// <param name="ForegroundLockTimeoutMs">
/// How long Windows keeps another process from taking the foreground after the user's last input
/// (SPI_GETFOREGROUNDLOCKTIMEOUT), or null when it cannot be read. focus_window works around it.
/// </param>
public sealed record DesktopStateInfo(bool InputDesktopAvailable, bool SessionLocked, int? ForegroundLockTimeoutMs);

/// <summary>A monitor.</summary>
/// <param name="Index">Its index, as get_monitors and monitorIndex parameters use it.</param>
/// <param name="Name">The device name.</param>
/// <param name="IsPrimary">True for the primary monitor, whose top-left corner is (0, 0).</param>
/// <param name="Bounds">Its bounds in physical virtual-screen pixels.</param>
/// <param name="Dpi">Its effective DPI (96 at 100%).</param>
/// <param name="ScaleFactor">Dpi / 96, for information only: never multiply coordinates by it.</param>
public sealed record ServerMonitorInfo(int Index, string Name, bool IsPrimary, BoundsDto Bounds, int Dpi, double ScaleFactor);

/// <summary>The agent's host as the server recorded it at start (the input guard's TargetIsAgentHost).</summary>
/// <param name="Windows">The host windows: the window showing the server's console, and the window in front at start.</param>
/// <param name="Processes">The host processes: the server, its ancestors and its console servers.</param>
public sealed record AgentHostInfo(IReadOnlyList<AgentHostWindow> Windows, IReadOnlyList<AgentHostProcess> Processes);

/// <summary>A host window.</summary>
/// <param name="Handle">The window handle.</param>
/// <param name="Role">"console" (the window that shows the server's console) or "startupForeground".</param>
/// <param name="ProcessId">Its process when it was recorded.</param>
/// <param name="ProcessName">That process's name now, or null when it has exited.</param>
public sealed record AgentHostWindow(long Handle, string Role, int ProcessId, string? ProcessName);

/// <summary>A host process.</summary>
/// <param name="ProcessId">The process ID.</param>
/// <param name="Role">"server", "ancestor" (parent first) or "consoleServer".</param>
/// <param name="ProcessName">Its name now, or null when it has exited.</param>
public sealed record AgentHostProcess(int ProcessId, string Role, string? ProcessName);

/// <summary>The user's control over the server's input (get_control_status has the details).</summary>
/// <param name="Paused">True while the user has paused input: input tools and window changes return PausedByUser.</param>
/// <param name="PausedBy">How it was paused; null while running.</param>
/// <param name="PausedAt">When it was paused; null while running.</param>
/// <param name="Hotkey">The pause hotkey: chord, state, source and error.</param>
/// <param name="SharedPause">True when the pause is shared with the other Windows App Manager servers on the desktop.</param>
public sealed record InputControlInfo(bool Paused, string? PausedBy, DateTimeOffset? PausedAt, PauseHotkeyStatus Hotkey, bool SharedPause);

/// <summary>The safety settings the user chose.</summary>
/// <param name="DeniedProcesses">Apps the agent may not act on (TargetDeniedByPolicy).</param>
/// <param name="DenyListSource">"default", or WAM_DENY_PROCESSES.</param>
/// <param name="AllowedProcesses">When set, the only apps the agent may act on; null when every app that is not denied is allowed.</param>
/// <param name="DestructiveChords">The send_keys chords that need windowHandle (DestructiveChordNeedsTarget).</param>
/// <param name="DestructiveChordSource">"default", or WAM_DESTRUCTIVE_CHORDS.</param>
/// <param name="UserClipboardAllowed">
/// WAM_ALLOW_USER_CLIPBOARD: true when get_clipboard returns what the user or another app copied without asking (content
/// marked private never); false when only the agent's own content is, and the user's only after a yes.
/// </param>
/// <param name="AllowedProtocols">The URI schemes launch_application opens: the built-in ones, then those WAM_ALLOWED_PROTOCOLS adds.</param>
public sealed record SafetySettingsInfo(
    IReadOnlyList<string> DeniedProcesses,
    string DenyListSource,
    IReadOnlyList<string>? AllowedProcesses,
    IReadOnlyList<string> DestructiveChords,
    string DestructiveChordSource,
    bool UserClipboardAllowed = false,
    IReadOnlyList<string>? AllowedProtocols = null);

/// <summary>Where the server keeps its files.</summary>
/// <param name="Layouts">The layout presets folder (WAM_LAYOUTS_PATH).</param>
/// <param name="AuditLog">
/// The folder of the audit log of input and destructive actions (roadmap M2.17, WAM_AUDIT_PATH): one
/// <c>&lt;yyyy-MM-dd&gt;.jsonl</c> file per day, kept for 7 days. Null when the server has no audit log.
/// </param>
public sealed record ServerPathsInfo(string Layouts, string? AuditLog = null);

/// <summary>The tools the server serves (WAM_TOOLSETS, WAM_EXCLUDE_TOOLS, WAM_READ_ONLY).</summary>
/// <param name="Enabled">The toolsets served.</param>
/// <param name="Disabled">The toolsets not served; WAM_TOOLSETS turns them on.</param>
/// <param name="Source">"default", WAM_TOOLSETS, WAM_EXCLUDE_TOOLS (an entry it could not read left only the server toolset on), or "all".</param>
/// <param name="ReadOnly">True when only tools annotated read-only are served.</param>
/// <param name="ExcludedTools">Tools WAM_EXCLUDE_TOOLS leaves out.</param>
/// <param name="ServedTools">How many tools tools/list lists.</param>
/// <param name="Moved">
/// Where the tools that moved out of a toolset in force went (roadmap M6.1), one sentence per toolset that is off: for
/// example click, which the deprecated toolset holds and mouse_click replaces. Null when none is out of reach.
/// </param>
/// <param name="Resources">
/// The URIs (or URI templates) of the resources served (roadmap M6.9), so an agent whose client shows resources only to the
/// user still learns they exist; null when none is served.
/// </param>
/// <param name="Prompts">The names of the prompts served (roadmap M6.9); null when none is served.</param>
public sealed record ToolsetInfo(
    IReadOnlyList<string> Enabled,
    IReadOnlyList<string> Disabled,
    string Source,
    bool ReadOnly,
    IReadOnlyList<string> ExcludedTools,
    int ServedTools,
    IReadOnlyList<string>? Moved = null,
    IReadOnlyList<string>? Resources = null,
    IReadOnlyList<string>? Prompts = null);
