using System.Text.Json.Serialization;

namespace WindowsAppManagerMcp.Models;

/// <summary>
/// Which windows an element search covers (roadmap M3.5). Menus, dropdowns, flyouts and many dialogs are top-level windows
/// of their own, outside the UI Automation subtree of the window that opened them.
/// </summary>
/// <remarks>Optional on the tools: left out (or null), the search covers windowAndPopups when the window or one of its popups
/// is in the foreground (<see cref="Services.Interfaces.IPopupService.IsInForeground"/>), else the window alone.</remarks>
[ToolEnum(WindowsAppManagerMcp.Infrastructure.ToolErrorReporting.InvalidArgumentsErrorCode, "scope value")]
public enum ElementSearchScope
{
    /// <summary>Only the window (below withinRef's element when one is given).</summary>
    [JsonStringEnumMemberName("window")]
    Window,

    /// <summary>
    /// The window and its open popups (<see cref="Services.Interfaces.IPopupService.GetPopups"/>): the windows it owns, the
    /// popup, dialog and tool windows of its process, and the popups of a child process such as msedgewebview2.exe.
    /// </summary>
    [JsonStringEnumMemberName("windowAndPopups")]
    WindowAndPopups,

    /// <summary>
    /// Every visible top-level window on the desktop, in z-order, at most <see cref="Services.PopupService.DesktopWindowLimit"/>,
    /// without the windows the target policy denies and the agent's own host windows (unless named as the window).
    /// </summary>
    [JsonStringEnumMemberName("desktop")]
    Desktop
}

/// <summary>What a popup window is, from its window class and style (and, in get_active_popups, its UI Automation root).</summary>
public enum PopupKind
{
    /// <summary>A menu: a Win32 menu (#32768), or a window whose UI Automation root is a Menu.</summary>
    Menu = 1,

    /// <summary>The list of an open combo box: ComboLBox, or a window whose UI Automation root is a List.</summary>
    Dropdown = 2,

    /// <summary>A dialog box (#32770), or an owned window with a title bar (a WPF or WinForms dialog).</summary>
    Dialog = 3,

    /// <summary>A WinUI or XAML flyout in a window of its own (Xaml_WindowedPopupClass, PopupWindowSiteBridge).</summary>
    Flyout = 4,

    /// <summary>A tooltip (tooltips_class32, or a UI Automation ToolTip).</summary>
    Tooltip = 5,

    /// <summary>Another window without a title bar: a browser's or an app's own menu or dropdown, a notification.</summary>
    Popup = 6,

    /// <summary>A window with a title bar that is no dialog: a tool palette.</summary>
    Window = 7
}

/// <summary>Why a window counts as a popup of the target window.</summary>
public enum PopupRelation
{
    /// <summary>The target window owns it, directly or through its other owned windows.</summary>
    Owned = 1,

    /// <summary>
    /// It belongs to the target's process and is owned by another of its windows, has no title bar (or is a tool window), or
    /// is a dialog box.
    /// </summary>
    SameProcess = 2,

    /// <summary>
    /// It is a popup of a process the target's process started (msedgewebview2.exe draws the menus and dropdowns of a
    /// WebView2 pane), and the target is not the desktop shell.
    /// </summary>
    ChildProcess = 3
}

/// <summary>
/// A popup of a window (<see cref="Services.Interfaces.IPopupService.GetPopups"/>), read from Win32 only: no UI Automation call.
/// </summary>
/// <param name="Handle">The popup's own top-level window: the window its elements' refs are registered under.</param>
/// <param name="Title">Its window text, or null when it has none (most menus and dropdowns).</param>
/// <param name="OwnerHandle">The window that owns it (GW_OWNER), or zero.</param>
/// <param name="Bounds">Its visible frame in physical screen pixels.</param>
/// <param name="IsBlocking">True when the target is disabled and this is its enabled popup (GW_ENABLEDPOPUP): the modal
/// dialog that takes the target's input.</param>
public sealed record PopupWindow(
    nint Handle,
    PopupKind Kind,
    string? Title,
    string ClassName,
    int ProcessId,
    nint OwnerHandle,
    PopupRelation Relation,
    BoundsDto? Bounds,
    bool IsBlocking);

/// <summary>The windows a desktop-scope search covers (<see cref="Services.Interfaces.IPopupService.GetDesktopWindows"/>).</summary>
/// <param name="Windows">The windows to search, in z-order (front first); the named window first when there is one.</param>
/// <param name="Excluded">Visible windows left out because the target policy denies their process, or because they are the
/// agent's own host windows (its terminal or editor and their popups).</param>
/// <param name="OverLimit">Visible windows left out because the limit was reached (the ones furthest back).</param>
public sealed record DesktopWindowSet(IReadOnlyList<nint> Windows, int Excluded, int OverLimit);

/// <summary>An open popup of the target app, as get_active_popups returns it (roadmap M3.5).</summary>
/// <param name="Ref">The ref of the popup window's own element, for find_ui_elements withinRef and the element tools; null
/// when UI Automation could not read the popup (it did not respond, or closed meanwhile).</param>
/// <param name="ControlType">The UI Automation control type of the popup window's element (Menu, List, Window, Pane...),
/// when it was read.</param>
/// <param name="OwnerHandle">The window that owns it, when one does.</param>
/// <param name="IsBlocking">True for the modal dialog that takes the target window's input; left out otherwise.</param>
public sealed record ActivePopup(
    [property: JsonPropertyOrder(-1)] string? Ref,
    long Handle,
    PopupKind Kind,
    string? Title,
    string ClassName,
    int ProcessId,
    string? ProcessName,
    long? OwnerHandle,
    BoundsDto? Bounds,
    string? ControlType = null,
    bool? IsBlocking = null);

/// <summary>Result of get_active_popups (roadmap M3.5).</summary>
/// <param name="WindowHandle">The app window whose popups were listed: the window given, or the window in front (the window
/// that owns it, when the window in front is a dialog).</param>
/// <param name="IsForeground">True when that window, or one of its popups, is in the foreground.</param>
/// <param name="Popups">The open popups, front first (z-order).</param>
/// <param name="Truncated">True when more popups were open than one call returns (<see cref="Services.PopupService.MaxPopups"/>).</param>
public sealed record ActivePopupsResult(
    bool Success,
    long WindowHandle,
    string? WindowTitle,
    bool IsForeground,
    IReadOnlyList<ActivePopup>? Popups,
    double ElapsedMilliseconds,
    bool Truncated = false,
    string? Error = null,
    UIElementErrorCode ErrorCode = UIElementErrorCode.None);
