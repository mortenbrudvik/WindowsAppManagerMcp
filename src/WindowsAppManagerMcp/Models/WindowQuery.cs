namespace WindowsAppManagerMcp.Models;

/// <summary>
/// What IWindowService.FindWindows looks for. Every filter that is set must match. The window selectors of the tools
/// (<see cref="WindowSelector"/>, roadmap M2.4) resolve through the same query, so find_windows and a selector agree on
/// which windows match.
/// </summary>
/// <remarks>
/// By default a listing contains only user-facing app windows (see IWindowService.IsUserFacingAppWindow):
/// no cloaked windows, tool windows, untitled windows, shell windows (desktop, taskbar, tray) or
/// windows of the server itself. A query that names its target explicitly, by <see cref="Handle"/>
/// or <see cref="ClassName"/>, or that sets <see cref="IncludeShell"/>, skips that filter and applies
/// only the filters set here, so the taskbar, tray and untitled popups stay reachable.
/// <para>
/// Titles are compared as <see cref="Services.WindowTitles"/> reads them: ignoring case, invisible format characters
/// (the zero-width space in "Microsoft​ Edge") and whitespace runs, with or without Edge's " and N more pages" segment.
/// </para>
/// </remarks>
public sealed record WindowQuery
{
    /// <summary>Part of the title, case-insensitive.</summary>
    public string? TitleContains { get; init; }

    /// <summary>The whole title, case-insensitive.</summary>
    public string? TitleEquals { get; init; }

    /// <summary>
    /// A .NET regular expression the title must match, case-insensitive, with a 100 ms match timeout
    /// (<see cref="Services.WindowTitles.CompileTitleRegex"/>).
    /// </summary>
    public string? TitleRegex { get; init; }

    /// <summary>
    /// The process name, case-insensitive, with or without ".exe": the whole name, or with <see cref="ProcessNameContains"/>
    /// a part of it. Matches the app process and, for UWP windows, the host process.
    /// </summary>
    public string? ProcessName { get; init; }

    /// <summary>Match <see cref="ProcessName"/> as a part of the process name instead of the whole name.</summary>
    public bool ProcessNameContains { get; init; }

    /// <summary>The process ID. Matches the app process and, for UWP windows, the host process.</summary>
    public int? ProcessId { get; init; }

    /// <summary>One window, by handle. Skips the user-facing filter.</summary>
    public nint? Handle { get; init; }

    /// <summary>The exact window class name, case-insensitive. Skips the user-facing filter.</summary>
    public string? ClassName { get; init; }

    /// <summary>Only windows with WS_VISIBLE.</summary>
    public bool VisibleOnly { get; init; } = true;

    /// <summary>Include minimized windows.</summary>
    public bool IncludeMinimized { get; init; } = true;

    /// <summary>Keep cloaked windows (other virtual desktops, suspended UWP frames) in a user-facing listing.</summary>
    public bool IncludeCloaked { get; init; }

    /// <summary>Keep tool windows (WS_EX_TOOLWINDOW without WS_EX_APPWINDOW) in a user-facing listing.</summary>
    public bool IncludeToolWindows { get; init; }

    /// <summary>Keep untitled windows in a user-facing listing.</summary>
    public bool IncludeUntitled { get; init; }

    /// <summary>Skip the user-facing filter: also list shell, tool, cloaked and untitled windows and the server's own.</summary>
    public bool IncludeShell { get; init; }

    /// <summary>The order of the result; z-order (front to back) by default.</summary>
    public WindowSort Sort { get; init; } = WindowSort.ZOrder;

    /// <summary>At most this many windows, the first in <see cref="Sort"/> order; null for all.</summary>
    public int? Limit { get; init; }

    /// <summary>
    /// True when the user-facing filter does not apply: the query names a handle or a class name, or
    /// sets <see cref="IncludeShell"/>.
    /// </summary>
    public bool SkipsUserFacingFilter => Handle.HasValue || !string.IsNullOrEmpty(ClassName) || IncludeShell;
}
