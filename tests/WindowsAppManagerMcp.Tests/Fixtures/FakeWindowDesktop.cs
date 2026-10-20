namespace WindowsAppManagerMcp.Tests.Fixtures;

/// <summary>
/// Top-level windows on a mocked <see cref="INativeWindowWrapper"/>, in z-order (the first added is the front one), for
/// the window selector and get_window_info tests (roadmap M2.4). Nothing touches the real desktop.
/// </summary>
public sealed class FakeWindowDesktop
{
    // Win32 values (NativeEnums is internal to the server assembly)
    public const int GwlStyle = -16;
    public const int GwlExStyle = -20;
    public const uint GwOwner = 4;
    public const long WsExToolWindow = 0x00000080;
    public const long WsExTopmost = 0x00000008;
    public const long WsDisabled = 0x08000000;

    /// <summary>The server's own process.</summary>
    public const uint ServerProcessId = 4242;

    private readonly List<nint> _topLevel = [];
    private readonly Dictionary<string, uint> _processIds = new(StringComparer.Ordinal);
    private int _nextHandle = 0x1000;
    private nint _foreground;

    public FakeWindowDesktop()
    {
        Native.Setup(n => n.EnumWindows(It.IsAny<Func<nint, bool>>()))
            .Callback<Func<nint, bool>>(callback =>
            {
                foreach (var hWnd in _topLevel.ToList())
                {
                    if (!callback(hWnd))
                        break;
                }
            });
        Native.Setup(n => n.GetCurrentProcessId()).Returns(ServerProcessId);
        Native.Setup(n => n.GetForegroundWindow()).Returns(() => _foreground);
    }

    public Mock<INativeWindowWrapper> Native { get; } = new();

    /// <summary>Recognizes the agent's host windows; none unless a test says so.</summary>
    public Mock<IHostGuard> HostGuard { get; } = new();

    /// <summary>A window service over this desktop.</summary>
    public WindowService CreateService() =>
        new(TestDataFactory.CreateMockMonitorService().Object, Native.Object, HostGuard.Object);

    /// <summary>Adds a window behind the ones added before.</summary>
    public nint Add(
        string title,
        string className = "AppWindow",
        uint? processId = null,
        string processName = "app",
        bool visible = true,
        bool cloaked = false,
        long extendedStyle = 0,
        long style = 0,
        bool hung = false,
        uint dpi = 96,
        uint displayAffinity = 0)
    {
        var hWnd = new nint(_nextHandle++);
        _topLevel.Add(hWnd);

        // One process per name unless the test names the process ID
        var pid = processId ?? (_processIds.TryGetValue(processName, out var known) ? known : _processIds[processName] = 100u + (uint)_processIds.Count);

        Native.Setup(n => n.IsWindow(hWnd)).Returns(true);
        Native.Setup(n => n.IsWindowVisible(hWnd)).Returns(visible);
        Native.Setup(n => n.GetClassName(hWnd)).Returns(className);
        Native.Setup(n => n.GetWindowText(hWnd)).Returns(title);
        Native.Setup(n => n.IsCloaked(hWnd)).Returns(cloaked);
        Native.Setup(n => n.GetWindowLongPtr(hWnd, GwlExStyle)).Returns(new nint(extendedStyle));
        Native.Setup(n => n.GetWindowLongPtr(hWnd, GwlStyle)).Returns(new nint(style));
        Native.Setup(n => n.GetWindow(hWnd, GwOwner)).Returns(nint.Zero);
        Native.Setup(n => n.GetWindowThreadProcessId(hWnd)).Returns(pid);
        Native.Setup(n => n.GetProcessName(pid)).Returns(processName);
        Native.Setup(n => n.GetWindowRect(hWnd)).Returns((0, 0, 800, 600));
        Native.Setup(n => n.GetAncestor(hWnd, WindowAncestor.Root)).Returns(hWnd);
        Native.Setup(n => n.IsHungAppWindow(hWnd)).Returns(hung);
        Native.Setup(n => n.GetDpiForWindow(hWnd)).Returns(dpi);
        Native.Setup(n => n.GetWindowDisplayAffinity(hWnd)).Returns(displayAffinity);
        return hWnd;
    }

    /// <summary>Makes <paramref name="hWnd"/> the foreground window (zero for none).</summary>
    public void SetForeground(nint hWnd) => _foreground = hWnd;

    /// <summary>The window closes: its handle is no window any more.</summary>
    public void Close(nint hWnd)
    {
        _topLevel.Remove(hWnd);
        Native.Setup(n => n.IsWindow(hWnd)).Returns(false);
        Native.Setup(n => n.GetWindowRect(hWnd)).Returns(((int, int, int, int)?)null);
    }

    /// <summary>The window's title changes (roadmap M3.7).</summary>
    public void SetTitle(nint hWnd, string title) => Native.Setup(n => n.GetWindowText(hWnd)).Returns(title);

    /// <summary>The window is minimized, or restored (roadmap M3.7).</summary>
    public void SetMinimized(nint hWnd, bool minimized) => Native.Setup(n => n.IsIconic(hWnd)).Returns(minimized);

    /// <summary>The window is maximized, or no longer (issue #14).</summary>
    public void SetMaximized(nint hWnd, bool maximized) => Native.Setup(n => n.IsZoomed(hWnd)).Returns(maximized);

    /// <summary>The window is shown, or hidden without closing (roadmap M3.7).</summary>
    public void SetVisible(nint hWnd, bool visible) => Native.Setup(n => n.IsWindowVisible(hWnd)).Returns(visible);

    /// <summary>Windows reports the window hung, or responding again (roadmap M3.7).</summary>
    public void SetHung(nint hWnd, bool hung) => Native.Setup(n => n.IsHungAppWindow(hWnd)).Returns(hung);

    /// <summary>The handle now belongs to another window: another title and process (a reused HWND).</summary>
    public void Reuse(nint hWnd, string title, uint processId, string processName)
    {
        Native.Setup(n => n.GetWindowText(hWnd)).Returns(title);
        Native.Setup(n => n.GetWindowThreadProcessId(hWnd)).Returns(processId);
        Native.Setup(n => n.GetProcessName(processId)).Returns(processName);
    }

    /// <summary>The top-level windows, front to back.</summary>
    public IReadOnlyList<nint> ZOrder => _topLevel.ToList();

    /// <summary>Every SetWindowZOrder call, in order, since <see cref="AcceptZOrderChanges"/>.</summary>
    public List<(nint Window, nint InsertAfter, uint Flags)> ZOrderCalls { get; } = [];

    /// <summary>Whether the window has WS_EX_TOPMOST now.</summary>
    public bool IsTopmost(nint hWnd) => (ExtendedStyle(hWnd) & WsExTopmost) != 0;

    /// <summary>
    /// SetWindowZOrder (roadmap M6.7) succeeds and moves the window as Windows does: HWND_TOPMOST to the front, topmost;
    /// HWND_NOTOPMOST to the top of the normal windows, not topmost; HWND_TOP to the top of its band; HWND_BOTTOM to the
    /// back, not topmost; a window handle directly behind that window, in that window's band. With
    /// <paramref name="keepsOwnTopmost"/> the app puts its topmost state back at once (WPF Topmost and some games do).
    /// </summary>
    public void AcceptZOrderChanges(bool keepsOwnTopmost = false)
    {
        Native.Setup(n => n.SetWindowZOrder(It.IsAny<nint>(), It.IsAny<nint>(), It.IsAny<uint>()))
            .Callback<nint, nint, uint>((hWnd, insertAfter, flags) =>
            {
                ZOrderCalls.Add((hWnd, insertAfter, flags));
                var wasTopmost = IsTopmost(hWnd);
                Place(hWnd, insertAfter);
                if (keepsOwnTopmost)
                    SetTopmost(hWnd, wasTopmost);
            })
            .Returns(Win32CallResult.Ok);
    }

    private void Place(nint hWnd, nint insertAfter)
    {
        _topLevel.Remove(hWnd);
        int TopOfNormalBand() => _topLevel.FindLastIndex(IsTopmost) + 1;
        switch ((long)insertAfter)
        {
            case -1:
                SetTopmost(hWnd, true);
                _topLevel.Insert(0, hWnd);
                break;
            case -2:
                SetTopmost(hWnd, false);
                _topLevel.Insert(TopOfNormalBand(), hWnd);
                break;
            case 0:
                _topLevel.Insert(IsTopmost(hWnd) ? 0 : TopOfNormalBand(), hWnd);
                break;
            case 1:
                SetTopmost(hWnd, false);
                _topLevel.Add(hWnd);
                break;
            default:
                SetTopmost(hWnd, IsTopmost(insertAfter));
                _topLevel.Insert(_topLevel.IndexOf(insertAfter) + 1, hWnd);
                break;
        }
    }

    /// <summary>Sets or clears WS_EX_TOPMOST, as the window's app or Windows does.</summary>
    public void SetTopmost(nint hWnd, bool topmost)
    {
        var style = topmost ? ExtendedStyle(hWnd) | WsExTopmost : ExtendedStyle(hWnd) & ~WsExTopmost;
        Native.Setup(n => n.GetWindowLongPtr(hWnd, GwlExStyle)).Returns(new nint(style));
    }

    private long ExtendedStyle(nint hWnd) => (long)Native.Object.GetWindowLongPtr(hWnd, GwlExStyle);
}
