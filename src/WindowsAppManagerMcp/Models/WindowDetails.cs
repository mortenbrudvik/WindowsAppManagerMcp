namespace WindowsAppManagerMcp.Models;

/// <summary>
/// One window read in full for get_window_info's detail 'full' (roadmap M2.4): what a listing reports
/// (<see cref="Window"/>) plus the state that explains why an action may fail or where it will land. Every value is a
/// read that never waits for the window's thread.
/// </summary>
/// <param name="Window">The window as the listings report it.</param>
/// <param name="IsForeground">Whether it is the foreground window (GetForegroundWindow).</param>
/// <param name="ZOrder">
/// Its position among all top-level windows, hidden ones included, front to back (0 is the top one); null for a child window.
/// </param>
/// <param name="IsTopmost">Whether it stays above normal windows (WS_EX_TOPMOST).</param>
/// <param name="IsEnabled">False when it is disabled (WS_DISABLED), usually by a modal dialog that takes its input.</param>
/// <param name="IsResponding">False when Windows reports it hung (IsHungAppWindow: no message processed for 5 seconds).</param>
/// <param name="Dpi">Its DPI (GetDpiForWindow; 96 is 100%); null when it cannot be read.</param>
public sealed record WindowDetails(
    WindowInfo Window,
    bool IsForeground,
    int? ZOrder,
    bool IsTopmost,
    bool IsEnabled,
    bool IsResponding,
    int? Dpi);
