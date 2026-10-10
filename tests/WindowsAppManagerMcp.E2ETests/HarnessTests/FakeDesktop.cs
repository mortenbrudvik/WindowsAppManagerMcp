namespace WindowsAppManagerMcp.E2ETests.HarnessTests;

/// <summary>
/// A desktop with one fixture window and one other window, for testing the guard rules without a real
/// desktop. Answers only from its own fields.
/// </summary>
internal sealed class FakeDesktop : IDesktopProbe
{
    public const int FixturePid = 4242;
    public const int OtherPid = 777;
    public static readonly nint FixtureWindow = 0x1000;
    public static readonly nint OtherWindow = 0x2000;

    public nint Foreground { get; set; }

    public (int X, int Y)? Cursor { get; set; } = (0, 0);

    public nint WindowAtCursor { get; set; }

    public Dictionary<(int X, int Y), nint> WindowsAtPoints { get; } = new();

    public nint GetForegroundWindow() => Foreground;

    public nint WindowFromPoint(int x, int y) =>
        WindowsAtPoints.TryGetValue((x, y), out var window) ? window
        : Cursor == (x, y) ? WindowAtCursor
        : 0;

    public (int X, int Y)? GetCursorPos() => Cursor;

    public int GetWindowProcessId(nint hwnd) =>
        hwnd == FixtureWindow ? FixturePid : hwnd == OtherWindow ? OtherPid : 0;

    public string GetWindowTitle(nint hwnd) =>
        hwnd == FixtureWindow ? "Fixture window" : hwnd == OtherWindow ? "Other window" : "";

    /// <summary>The thread the guarded calls come from.</summary>
    public uint CurrentThreadId { get; set; } = 1111;

    public uint GetCurrentThreadId() => CurrentThreadId;
}
