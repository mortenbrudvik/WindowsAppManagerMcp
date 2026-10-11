using System.Text.Json.Serialization;

namespace WindowsAppManagerMcp.Models;

// The fixed-value tool parameters (roadmap M2.2). The input schema lists each enum's canonical names and listed aliases,
// and an unknown value fails with the enum's errorCode and the allowed values instead of falling back to a default. The
// names are the strings the tools took before they were typed, and so are the aliases the documentation named. For an
// optional parameter, NullMeans keeps a JSON null meaning the parameter's default, as it did for the strings.

/// <summary>The state set_window_state and set_windows_state_batch put a window in.</summary>
[ToolEnum(nameof(WindowErrorCode.InvalidState), "window state")]
public enum WindowStateRequest
{
    /// <summary>Minimize the window.</summary>
    [JsonStringEnumMemberName("minimize")]
    [ToolValueAlias("minimized")]
    Minimize,

    /// <summary>Maximize the window on its monitor.</summary>
    [JsonStringEnumMemberName("maximize")]
    [ToolValueAlias("maximized")]
    Maximize,

    /// <summary>
    /// Restore the window: a maximized window becomes normal, and a minimized window returns to the state it had before
    /// it was minimized (normal, or maximized), as the Windows Restore command does.
    /// </summary>
    [JsonStringEnumMemberName("restore")]
    [ToolValueAlias("normal")]
    Restore
}

/// <summary>Where move_window_to_monitor puts the window on the target monitor's work area.</summary>
[ToolEnum(nameof(WindowErrorCode.InvalidPositioning), "positioning", NullMeans = "center")]
public enum MonitorPositioning
{
    /// <summary>Centered, at its current size.</summary>
    [JsonStringEnumMemberName("center")]
    Center,

    /// <summary>In the top-left corner, at its current size.</summary>
    [JsonStringEnumMemberName("topleft")]
    TopLeft,

    /// <summary>In the top-right corner, at its current size.</summary>
    [JsonStringEnumMemberName("topright")]
    TopRight,

    /// <summary>In the bottom-left corner, at its current size.</summary>
    [JsonStringEnumMemberName("bottomleft")]
    BottomLeft,

    /// <summary>In the bottom-right corner, at its current size.</summary>
    [JsonStringEnumMemberName("bottomright")]
    BottomRight,

    /// <summary>Maximized (SW_MAXIMIZE).</summary>
    [JsonStringEnumMemberName("maximize")]
    Maximize,

    /// <summary>At the work area's top-left corner, at its current size, restored first if it is minimized.</summary>
    [JsonStringEnumMemberName("restore")]
    Restore
}

/// <summary>How apply_layout matches a preset's placements to open windows.</summary>
[ToolEnum(nameof(LayoutErrorCode.InvalidMatchBy), "matchBy value", NullMeans = "process_and_title")]
public enum LayoutMatchBy
{
    /// <summary>The process name and the title text must both match.</summary>
    [JsonStringEnumMemberName("process_and_title")]
    ProcessAndTitle,

    /// <summary>Only the process name must match.</summary>
    [JsonStringEnumMemberName("process_only")]
    ProcessOnly,

    /// <summary>Only the title text must match.</summary>
    [JsonStringEnumMemberName("title_only")]
    TitleOnly
}

/// <summary>The image format of a capture.</summary>
[ToolEnum(nameof(ScreenshotErrorCode.InvalidFormat), "image format", NullMeans = "auto")]
public enum CaptureFormat
{
    /// <summary>JPEG, or PNG when the image's long edge is at most 512 pixels; with an outputPath, its extension decides.</summary>
    [JsonStringEnumMemberName("auto")]
    Auto,

    /// <summary>PNG (lossless).</summary>
    [JsonStringEnumMemberName("png")]
    Png,

    /// <summary>JPEG.</summary>
    [JsonStringEnumMemberName("jpeg")]
    [ToolValueAlias("jpg")]
    Jpeg
}

/// <summary>The browser launch_browser_with_debug starts.</summary>
[ToolEnum(nameof(BrowserErrorCode.UnsupportedBrowser), "browser", NullMeans = "chrome")]
public enum DebugBrowser
{
    /// <summary>Google Chrome.</summary>
    [JsonStringEnumMemberName("chrome")]
    Chrome,

    /// <summary>Microsoft Edge.</summary>
    [JsonStringEnumMemberName("edge")]
    Edge,

    /// <summary>Brave.</summary>
    [JsonStringEnumMemberName("brave")]
    Brave
}

/// <summary>The order find_windows lists the windows in (roadmap M2.4).</summary>
[ToolEnum(WindowsAppManagerMcp.Infrastructure.ToolErrorReporting.InvalidArgumentsErrorCode, "sort order", NullMeans = "zorder")]
public enum WindowSort
{
    /// <summary>Front to back: the window on top first, as Windows stacks them.</summary>
    [JsonStringEnumMemberName("zorder")]
    ZOrder,

    /// <summary>By title, ignoring case; windows with the same title in z-order.</summary>
    [JsonStringEnumMemberName("title")]
    Title,

    /// <summary>By process name, ignoring case; the windows of one process in z-order.</summary>
    [JsonStringEnumMemberName("process")]
    Process
}

/// <summary>How much get_window_info reports about a window (roadmap M2.4).</summary>
[ToolEnum(WindowsAppManagerMcp.Infrastructure.ToolErrorReporting.InvalidArgumentsErrorCode, "detail level", NullMeans = "standard")]
public enum WindowDetail
{
    /// <summary>Handle, title, process and state.</summary>
    [JsonStringEnumMemberName("compact")]
    Compact,

    /// <summary>What find_windows reports for a window.</summary>
    [JsonStringEnumMemberName("standard")]
    Standard,

    /// <summary>Standard, plus the visible and client bounds, foreground, z-order, topmost, enabled, responding and DPI.</summary>
    [JsonStringEnumMemberName("full")]
    Full
}

/// <summary>The shape of get_ui_elements' result (roadmap M3.1).</summary>
[ToolEnum(WindowsAppManagerMcp.Infrastructure.ToolErrorReporting.InvalidArgumentsErrorCode, "format", NullMeans = "tree")]
public enum UIElementFormat
{
    /// <summary>Elements nested under the element they are inside (children).</summary>
    [JsonStringEnumMemberName("tree")]
    Tree,

    /// <summary>Every element once, in reading order, with its depth and the index of its parent in the list.</summary>
    [JsonStringEnumMemberName("flat")]
    Flat,

    /// <summary>One compact line per element in text, indented by depth; elements is left out.</summary>
    [JsonStringEnumMemberName("text")]
    Text
}
