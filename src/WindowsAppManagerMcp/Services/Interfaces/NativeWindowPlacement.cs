namespace WindowsAppManagerMcp.Services.Interfaces;

/// <summary>
/// A window's WINDOWPLACEMENT as GetWindowPlacement reads it (roadmap M6.8): its show command, its flags (WPF_*), where its
/// icon and its maximized window go, and its restore rectangle, the rect it has when it is neither minimized nor maximized.
/// For a top-level window that is not a tool window (WS_EX_TOOLWINDOW) the positions are in workspace coordinates, which
/// start at the work area's corner (<see cref="WorkspaceCoordinates"/>); for a tool window they are screen coordinates.
/// </summary>
/// <param name="ShowCmd">The show state (SW_SHOWNORMAL 1, SW_SHOWMINIMIZED 2, SW_SHOWMAXIMIZED 3, ...).</param>
/// <param name="Flags">WPF_SETMINPOSITION (1), WPF_RESTORETOMAXIMIZED (2), WPF_ASYNCWINDOWPLACEMENT (4).</param>
public sealed record NativeWindowPlacement(
    int ShowCmd,
    int Flags,
    (int X, int Y) MinPosition,
    (int X, int Y) MaxPosition,
    (int Left, int Top, int Right, int Bottom) NormalPosition);
