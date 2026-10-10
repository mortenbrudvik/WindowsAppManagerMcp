namespace WindowsAppManagerMcp.E2ETests.Harness;

/// <summary>
/// Read-only questions the input guard asks before each native call: which window is in front, which
/// window is under a point, and which process owns a window. A seam, so the guard rules can be
/// tested without a desktop.
/// </summary>
public interface IDesktopProbe
{
    nint GetForegroundWindow();

    nint WindowFromPoint(int x, int y);

    (int X, int Y)? GetCursorPos();

    /// <summary>The process that owns <paramref name="hwnd"/>, or 0 when there is none.</summary>
    int GetWindowProcessId(nint hwnd);

    string GetWindowTitle(nint hwnd);

    /// <summary>The OS ID of the calling thread: the thread about to make the guarded call.</summary>
    uint GetCurrentThreadId();
}

/// <summary>The real desktop, through read-only user32 calls.</summary>
public sealed class Win32DesktopProbe : IDesktopProbe
{
    public nint GetForegroundWindow() => E2ENative.GetForegroundWindow();

    public nint WindowFromPoint(int x, int y) => E2ENative.WindowFromPoint(new E2ENative.POINT { X = x, Y = y });

    public (int X, int Y)? GetCursorPos() =>
        E2ENative.GetCursorPos(out var point) ? (point.X, point.Y) : null;

    public int GetWindowProcessId(nint hwnd) => E2ENative.GetProcessId(hwnd);

    public string GetWindowTitle(nint hwnd) => hwnd == 0 ? "" : E2ENative.GetWindowText(hwnd);

    public uint GetCurrentThreadId() => E2ENative.GetCurrentThreadId();
}
