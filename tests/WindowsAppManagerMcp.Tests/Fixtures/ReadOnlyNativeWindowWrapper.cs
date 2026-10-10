namespace WindowsAppManagerMcp.Tests.Fixtures;

/// <summary>
/// An <see cref="INativeWindowWrapper"/> that can only read. It forwards the query methods to the
/// wrapped instance and throws on every method that moves, resizes, shows, focuses or closes a
/// window, so a test that uses the real <see cref="NativeWindowWrapper"/> cannot change a window on
/// the desktop, even if it is copied into a test of a mutating method.
/// </summary>
/// <remarks>
/// Every interface member is written out by hand, with no default or fallback: a new member of
/// <see cref="INativeWindowWrapper"/> does not compile here until someone decides whether it only
/// reads (forward it) or changes a window (throw).
/// </remarks>
public sealed class ReadOnlyNativeWindowWrapper(INativeWindowWrapper inner) : INativeWindowWrapper
{
    public ReadOnlyNativeWindowWrapper()
        : this(new NativeWindowWrapper())
    {
    }

    // Read-only queries: forwarded

    public void EnumWindows(Func<nint, bool> callback) => inner.EnumWindows(callback);

    public bool IsWindowVisible(nint hWnd) => inner.IsWindowVisible(hWnd);

    public bool IsWindow(nint hWnd) => inner.IsWindow(hWnd);

    public bool IsIconic(nint hWnd) => inner.IsIconic(hWnd);

    public bool IsZoomed(nint hWnd) => inner.IsZoomed(hWnd);

    public string GetWindowText(nint hWnd) => inner.GetWindowText(hWnd);

    public uint GetWindowThreadProcessId(nint hWnd) => inner.GetWindowThreadProcessId(hWnd);

    public (int Left, int Top, int Right, int Bottom)? GetWindowRect(nint hWnd) => inner.GetWindowRect(hWnd);

    public nint GetForegroundWindow() => inner.GetForegroundWindow();

    public nint GetAncestor(nint hWnd, WindowAncestor ancestor) => inner.GetAncestor(hWnd, ancestor);

    public nint GetShellWindow() => inner.GetShellWindow();

    public uint GetCurrentProcessId() => inner.GetCurrentProcessId();

    public string? GetProcessName(uint processId) => inner.GetProcessName(processId);

    // Window changes: refused before anything reaches the wrapped instance

    public bool SetWindowPos(nint hWnd, int x, int y, int width, int height, uint flags) =>
        throw Refused(nameof(SetWindowPos));

    public bool ShowWindow(nint hWnd, int cmdShow) => throw Refused(nameof(ShowWindow));

    public bool SetForegroundWindow(nint hWnd) => throw Refused(nameof(SetForegroundWindow));

    public bool BringWindowToTop(nint hWnd) => throw Refused(nameof(BringWindowToTop));

    public bool PostMessage(nint hWnd, uint msg, nint wParam, nint lParam) => throw Refused(nameof(PostMessage));

    private static InvalidOperationException Refused(string method) =>
        new($"{method} would change a real window. This test wrapper is read-only; " +
            "test window changes with a mocked INativeWindowWrapper.");
}
