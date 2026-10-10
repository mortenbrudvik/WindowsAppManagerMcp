namespace WindowsAppManagerMcp.Tests.Unit.Services;

/// <summary>
/// WindowService listings show only user-facing app windows, explicit queries (handle, class name,
/// includeShell) and single-window reads see every window, and UWP frame windows are reported under
/// the app's own process. Every window here is a fake on a mocked <see cref="INativeWindowWrapper"/>.
/// </summary>
public class WindowServiceListingTests
{
    // Win32 values (NativeEnums is internal to the server assembly)
    private const int GwlExStyle = -20;
    private const uint GwOwner = 4;
    private const long WsExToolWindow = 0x00000080;
    private const long WsExAppWindow = 0x00040000;

    private const uint ServerProcessId = 4242;
    private const uint ExplorerProcessId = 300;
    private const uint FrameHostProcessId = 500;
    private const uint CalculatorProcessId = 600;

    /// <summary>What GetAncestor(GA_ROOT) returns for the fake child windows: some other, top-level window.</summary>
    private static readonly nint ChildWindowRoot = 0x0FFF;

    private readonly FakeDesktop _desktop = new();

    private WindowService CreateSut() =>
        new(TestDataFactory.CreateMockMonitorService().Object, _desktop.Native.Object, Mock.Of<IHostGuard>());

    #region The user-facing listing predicate

    public static TheoryData<string, string, long, bool, bool> UserFacingRuleCases() => new()
    {
        // className, title, extended style, cloaked, user-facing
        { "Notepad", "Untitled - Notepad", 0, false, true },
        { "Tauri Window", "Duetty", WsExToolWindow | WsExAppWindow, false, true },
        { "ApplicationFrameWindow", "Settings", 0, true, false },
        { "Windows.UI.Core.CoreWindow", "Windows Input Experience", 0, true, false },
        { "PaletteWindow", "Layers", WsExToolWindow, false, false },
        { "#32768", "", WsExToolWindow, false, false },
        { "SplashWindow", "", 0, false, false },
        { "Progman", "Program Manager", 0, false, false },
        { "WorkerW", "Desktop layer", 0, false, false },
        { "Shell_TrayWnd", "", WsExToolWindow, false, false },
        { "shell_secondarytraywnd", "Taskbar", 0, false, false },
    };

    [Theory]
    [MemberData(nameof(UserFacingRuleCases))]
    public void Listing_KeepsOnlyUserFacingAppWindows(string className, string title, long extendedStyle, bool cloaked, bool userFacing)
    {
        // Arrange
        var window = _desktop.Add(title, className, extendedStyle: extendedStyle, cloaked: cloaked);
        var sut = CreateSut();

        // Act
        var listed = sut.GetAllWindows().Select(w => w.Handle).ToList();
        var found = sut.FindWindows(new WindowQuery()).Select(w => w.Handle).ToList();

        // Assert
        sut.IsUserFacingAppWindow(window).Should().Be(userFacing);
        listed.Contains(window).Should().Be(userFacing);
        found.Contains(window).Should().Be(userFacing);
    }

    [Fact]
    public void Listing_SkipsHiddenWindows()
    {
        var hidden = _desktop.Add("Hidden helper", visible: false);
        var sut = CreateSut();

        sut.GetAllWindows().Should().BeEmpty();
        sut.IsUserFacingAppWindow(hidden).Should().BeFalse();
    }

    [Fact]
    public void Listing_KeepsOwnedDialogs_AndReportsTheirOwner()
    {
        // Arrange - a Save As dialog owned by the main window; agents look for these by title
        var main = _desktop.Add("notes.txt - Notepad", "Notepad");
        var dialog = _desktop.Add("Save As", "#32770", owner: main);
        var sut = CreateSut();

        // Act
        var windows = sut.FindWindows(titleContains: "Save As");

        // Assert
        var info = windows.Should().ContainSingle().Which;
        info.Handle.Should().Be(dialog);
        info.OwnerHandle.Should().Be(main);
        info.ClassName.Should().Be("#32770");
        sut.GetWindowInfo(main)!.OwnerHandle.Should().BeNull();
    }

    [Fact]
    public void Listing_LeavesOutTheServersOwnWindows()
    {
        var own = _desktop.Add("Server window", processId: ServerProcessId, processName: "WindowsAppManagerMcp");
        var sut = CreateSut();

        sut.GetAllWindows().Should().BeEmpty();
        sut.IsUserFacingAppWindow(own).Should().BeFalse("the predicate decides what the listing decides");
        sut.GetWindowInfo(own).Should().NotBeNull("single-window reads have no listing filter");
    }

    [Fact]
    public void IsUserFacingAppWindow_ChildControl_IsFalse()
    {
        // Arrange - a visible, titled, uncloaked button inside an app window; listings never see it,
        // because they enumerate only top-level windows
        var main = _desktop.Add("Untitled - Notepad", "Notepad");
        var button = _desktop.Add("OK", "Button", topLevel: false);
        _desktop.SetChildren(main, button);
        var sut = CreateSut();

        // Act & Assert
        sut.IsUserFacingAppWindow(main).Should().BeTrue();
        sut.IsUserFacingAppWindow(button).Should().BeFalse();
    }

    [Fact]
    public void IncludeCloakedAndIncludeToolWindows_KeepThoseWindows_ButNotShellWindows()
    {
        // Arrange
        var cloaked = _desktop.Add("Settings", "ApplicationFrameWindow", cloaked: true);
        var tool = _desktop.Add("Layers", "PaletteWindow", extendedStyle: WsExToolWindow);
        _desktop.Add("Program Manager", "Progman", extendedStyle: WsExToolWindow, processId: ExplorerProcessId, processName: "explorer");
        var sut = CreateSut();

        // Act
        var withCloaked = sut.FindWindows(new WindowQuery { IncludeCloaked = true });
        var withTools = sut.FindWindows(new WindowQuery { IncludeToolWindows = true });
        var withBoth = sut.FindWindows(new WindowQuery { IncludeCloaked = true, IncludeToolWindows = true });

        // Assert
        withCloaked.Should().ContainSingle().Which.Should().Match<WindowInfo>(w => w.Handle == cloaked && w.IsCloaked);
        withTools.Should().ContainSingle().Which.Handle.Should().Be(tool);
        withBoth.Select(w => w.Handle).Should().BeEquivalentTo([cloaked, tool]);
    }

    [Fact]
    public void Listing_ReadsCheapFactsBeforeProcessNames_AndLooksUpEachProcessNameOnce()
    {
        // Arrange - two windows of one app, and windows the cheap rules reject
        _desktop.Add("Document 1 - Word", processId: 700, processName: "WINWORD");
        _desktop.Add("Document 2 - Word", processId: 700, processName: "WINWORD");
        _desktop.Add("Layers", extendedStyle: WsExToolWindow, processId: 701, processName: "palette");
        _desktop.Add("Settings", "ApplicationFrameWindow", cloaked: true, processId: 702, processName: "ApplicationFrameHost");
        _desktop.Add("Program Manager", "Progman", processId: 703, processName: "explorer");
        _desktop.Add("", "SplashWindow", processId: 704, processName: "splash");
        var sut = CreateSut();

        // Act
        var windows = sut.GetAllWindows();

        // Assert
        windows.Should().HaveCount(2).And.OnlyContain(w => w.ProcessName == "WINWORD");
        _desktop.Native.Verify(n => n.GetProcessName(700u), Times.Once);
        _desktop.Native.Verify(n => n.GetProcessName(It.Is<uint>(pid => pid != 700u)), Times.Never);
    }

    #endregion

    #region Explicit queries skip the predicate

    [Fact]
    public void ClassName_FindsTheTaskbar_ThatTheListingLeavesOut()
    {
        // Arrange - the taskbar is an untitled tool window of a shell class
        var taskbar = _desktop.Add("", "Shell_TrayWnd", extendedStyle: WsExToolWindow, processId: ExplorerProcessId, processName: "explorer");
        _desktop.Add("Untitled - Notepad", "Notepad");
        var sut = CreateSut();

        // Act
        var windows = sut.FindWindows(new WindowQuery { ClassName = "shell_traywnd" });

        // Assert
        var info = windows.Should().ContainSingle().Which;
        info.Handle.Should().Be(taskbar);
        info.ClassName.Should().Be("Shell_TrayWnd");
        info.Title.Should().BeEmpty();
        info.ProcessName.Should().Be("explorer");
        sut.GetAllWindows().Should().NotContain(w => w.Handle == taskbar);
    }

    [Fact]
    public void ClassName_StillAppliesTheOtherFilters()
    {
        _desktop.Add("Find", "#32770", processId: 800, processName: "notepad");
        var replace = _desktop.Add("Replace", "#32770", processId: 800, processName: "notepad");
        _desktop.Add("Replace", "#32770", processId: 801, processName: "wordpad");
        _desktop.Add("Replace", "#32770", visible: false, processId: 800, processName: "notepad");
        var sut = CreateSut();

        var windows = sut.FindWindows(new WindowQuery { ClassName = "#32770", TitleContains = "replace", ProcessName = "notepad" });

        windows.Should().ContainSingle().Which.Handle.Should().Be(replace);
    }

    [Fact]
    public void Handle_FindsAnUntitledMenuOrAnOwnWindow()
    {
        // Arrange
        var menu = _desktop.Add("", "#32768", extendedStyle: WsExToolWindow);
        var own = _desktop.Add("Server window", processId: ServerProcessId);
        var sut = CreateSut();

        // Act + Assert
        sut.FindWindows(handle: menu).Should().ContainSingle().Which.ClassName.Should().Be("#32768");
        sut.FindWindows(handle: own).Should().ContainSingle().Which.Handle.Should().Be(own);
        sut.FindWindows(handle: new nint(987654)).Should().BeEmpty("the handle is not a window");
    }

    [Fact]
    public void Handle_IsReadDirectly_WithoutListingEveryWindow()
    {
        var window = _desktop.Add("Untitled - Notepad");
        var sut = CreateSut();

        sut.FindWindows(handle: window).Should().ContainSingle();

        _desktop.Native.Verify(n => n.EnumWindows(It.IsAny<Func<nint, bool>>()), Times.Never);
    }

    [Fact]
    public void IncludeShell_ListsShellCloakedToolAndUntitledWindows_ButKeepsTheExplicitFilters()
    {
        // Arrange
        var progman = _desktop.Add("Program Manager", "Progman", extendedStyle: WsExToolWindow, processId: ExplorerProcessId, processName: "explorer");
        var taskbar = _desktop.Add("", "Shell_TrayWnd", extendedStyle: WsExToolWindow, processId: ExplorerProcessId, processName: "explorer");
        var cloaked = _desktop.Add("Hidden folder", "CabinetWClass", cloaked: true, processId: ExplorerProcessId, processName: "explorer");
        _desktop.Add("", "WorkerW", visible: false, processId: ExplorerProcessId, processName: "explorer");
        _desktop.Add("Untitled - Notepad", "Notepad");
        var sut = CreateSut();

        // Act
        var windows = sut.FindWindows(new WindowQuery { IncludeShell = true, ProcessName = "explorer" });

        // Assert
        windows.Select(w => w.Handle).Should().BeEquivalentTo([progman, taskbar, cloaked]);
        windows.Single(w => w.Handle == cloaked).IsCloaked.Should().BeTrue();
    }

    #endregion

    #region Single-window reads have no listing filter

    [Fact]
    public void GetForegroundWindow_DescribesAnUntitledForegroundWindow()
    {
        // Arrange - the taskbar has focus
        var taskbar = _desktop.Add("", "Shell_TrayWnd", extendedStyle: WsExToolWindow, processId: ExplorerProcessId, processName: "explorer");
        _desktop.Native.Setup(n => n.GetForegroundWindow()).Returns(taskbar);
        var sut = CreateSut();

        // Act
        var info = sut.GetForegroundWindow();

        // Assert
        info.Should().NotBeNull("an untitled foreground window is still a foreground window");
        info!.Handle.Should().Be(taskbar);
        info.Title.Should().BeEmpty();
        info.ClassName.Should().Be("Shell_TrayWnd");
        info.ProcessName.Should().Be("explorer");
    }

    [Fact]
    public void GetForegroundWindow_ReturnsNull_OnlyWhenNoWindowIsInTheForeground()
    {
        _desktop.Native.Setup(n => n.GetForegroundWindow()).Returns(nint.Zero);

        CreateSut().GetForegroundWindow().Should().BeNull();
    }

    [Fact]
    public void GetWindowInfo_DescribesUntitledToolAndCloakedWindows()
    {
        // Arrange
        var popup = _desktop.Add("", "Chrome_WidgetWin_1", extendedStyle: WsExToolWindow, processId: 900, processName: "chrome");
        var cloaked = _desktop.Add("Settings", "ApplicationFrameWindow", cloaked: true, processId: FrameHostProcessId, processName: "ApplicationFrameHost");
        var sut = CreateSut();

        // Act
        var popupInfo = sut.GetWindowInfo(popup);
        var cloakedInfo = sut.GetWindowInfo(cloaked);

        // Assert
        popupInfo.Should().NotBeNull();
        popupInfo!.Should().Match<WindowInfo>(w => w.Title == "" && w.ClassName == "Chrome_WidgetWin_1" && w.ProcessName == "chrome" && !w.IsCloaked);
        popupInfo!.Bounds.Should().Be(new WindowRect(0, 0, 800, 600));
        cloakedInfo.Should().NotBeNull();
        cloakedInfo!.IsCloaked.Should().BeTrue();
    }

    #endregion

    #region UWP windows are attributed to the app

    [Fact]
    public void UwpFrame_IsReportedUnderTheAppProcess_WithApplicationFrameHostAsHost()
    {
        // Arrange - the frame's own children (title bar) belong to the host; the CoreWindow to the app
        var frame = AddCalculatorFrame();
        var sut = CreateSut();

        // Act
        var listed = sut.GetAllWindows().Should().ContainSingle().Which;
        var read = sut.GetWindowInfo(frame)!;

        // Assert
        foreach (var info in new[] { listed, read })
        {
            info.Handle.Should().Be(frame);
            info.ProcessName.Should().Be("CalculatorApp");
            info.ProcessId.Should().Be((int)CalculatorProcessId);
            info.HostProcessName.Should().Be("ApplicationFrameHost");
            info.HostProcessId.Should().Be((int)FrameHostProcessId);
            info.ClassName.Should().Be("ApplicationFrameWindow");
        }
    }

    [Theory]
    [InlineData("calculatorapp", null)]
    [InlineData("ApplicationFrameHost", null)]
    [InlineData(null, (int)CalculatorProcessId)]
    [InlineData(null, (int)FrameHostProcessId)]
    public void UwpFrame_MatchesTheAppAndTheHostProcess(string? processName, int? processId)
    {
        var frame = AddCalculatorFrame();
        _desktop.Add("Untitled - Notepad", processId: 1000, processName: "notepad");
        var sut = CreateSut();

        var windows = sut.FindWindows(processName: processName, processId: processId);

        windows.Should().ContainSingle().Which.Handle.Should().Be(frame);
    }

    [Fact]
    public void MinimizedUwpFrame_IsMatchedToTheTopLevelCoreWindowWithItsTitle()
    {
        // Arrange - while the app is minimized its CoreWindow is a cloaked top-level window
        var frame = _desktop.Add("Calculator", "ApplicationFrameWindow", processId: FrameHostProcessId, processName: "ApplicationFrameHost", minimized: true);
        _desktop.Add("Calculator", "Windows.UI.Core.CoreWindow", cloaked: true, processId: CalculatorProcessId, processName: "CalculatorApp");
        _desktop.Add("Windows Input Experience", "Windows.UI.Core.CoreWindow", cloaked: true, processId: 610, processName: "TextInputHost");
        var sut = CreateSut();

        // Act
        var listed = sut.GetAllWindows();
        var read = sut.GetWindowInfo(frame);

        // Assert
        listed.Should().ContainSingle().Which.Should().Match<WindowInfo>(w =>
            w.Handle == frame && w.ProcessName == "CalculatorApp" && w.HostProcessName == "ApplicationFrameHost" &&
            w.State == WindowState.Minimized);
        read!.ProcessName.Should().Be("CalculatorApp");
    }

    [Fact]
    public void UwpFrame_WithAnAmbiguousTitle_StaysAttributedToTheHost()
    {
        // Arrange - two apps have a top-level CoreWindow with the frame's title
        var frame = _desktop.Add("Settings", "ApplicationFrameWindow", processId: FrameHostProcessId, processName: "ApplicationFrameHost");
        _desktop.Add("Settings", "Windows.UI.Core.CoreWindow", cloaked: true, processId: 610, processName: "SystemSettings");
        _desktop.Add("Settings", "Windows.UI.Core.CoreWindow", cloaked: true, processId: 611, processName: "OtherApp");
        var sut = CreateSut();

        // Act
        var info = sut.GetWindowInfo(frame)!;

        // Assert
        info.ProcessName.Should().Be("ApplicationFrameHost");
        info.ProcessId.Should().Be((int)FrameHostProcessId);
        info.HostProcessName.Should().BeNull();
        info.HostProcessId.Should().BeNull();
    }

    [Fact]
    public void OrdinaryWindows_AreNotSearchedForAHostedApp()
    {
        var window = _desktop.Add("Untitled - Notepad", "Notepad");
        var sut = CreateSut();

        var info = sut.GetAllWindows().Should().ContainSingle().Which;

        info.HostProcessName.Should().BeNull();
        info.HostProcessId.Should().BeNull();
        _desktop.Native.Verify(n => n.EnumChildWindows(It.IsAny<nint>(), It.IsAny<Func<nint, bool>>()), Times.Never);
        _desktop.Native.Verify(n => n.GetClassName(It.Is<nint>(h => h != window)), Times.Never);
    }

    private nint AddCalculatorFrame()
    {
        var frame = _desktop.Add("Calculator", "ApplicationFrameWindow", processId: FrameHostProcessId, processName: "ApplicationFrameHost");
        var titleBar = _desktop.Add("", "ApplicationFrameTitleBarWindow", processId: FrameHostProcessId, processName: "ApplicationFrameHost", topLevel: false);
        var coreWindow = _desktop.Add("Calculator", "Windows.UI.Core.CoreWindow", processId: CalculatorProcessId, processName: "CalculatorApp", topLevel: false);
        var inputSink = _desktop.Add("", "ApplicationFrameInputSinkWindow", processId: FrameHostProcessId, processName: "ApplicationFrameHost", topLevel: false);
        _desktop.SetChildren(frame, titleBar, coreWindow, inputSink);
        return frame;
    }

    #endregion

    /// <summary>
    /// Fake windows on a mocked <see cref="INativeWindowWrapper"/>. Top-level windows are returned by
    /// EnumWindows in the order they were added; child windows only through SetChildren.
    /// </summary>
    private sealed class FakeDesktop
    {
        private readonly List<nint> _topLevel = [];
        private int _nextHandle = 0x1000;

        public FakeDesktop()
        {
            Native.Setup(n => n.EnumWindows(It.IsAny<Func<nint, bool>>()))
                .Callback<Func<nint, bool>>(callback =>
                {
                    foreach (var hWnd in _topLevel)
                    {
                        if (!callback(hWnd))
                            break;
                    }
                });
            Native.Setup(n => n.GetCurrentProcessId()).Returns(ServerProcessId);
        }

        public Mock<INativeWindowWrapper> Native { get; } = new();

        public nint Add(
            string title,
            string className = "AppWindow",
            uint processId = 100,
            string processName = "app",
            bool visible = true,
            bool cloaked = false,
            long extendedStyle = 0,
            nint owner = 0,
            bool minimized = false,
            bool topLevel = true)
        {
            var hWnd = new nint(_nextHandle++);
            if (topLevel)
                _topLevel.Add(hWnd);

            Native.Setup(n => n.IsWindow(hWnd)).Returns(true);
            Native.Setup(n => n.IsWindowVisible(hWnd)).Returns(visible);
            Native.Setup(n => n.GetClassName(hWnd)).Returns(className);
            Native.Setup(n => n.GetWindowText(hWnd)).Returns(title);
            Native.Setup(n => n.IsCloaked(hWnd)).Returns(cloaked);
            Native.Setup(n => n.GetWindowLongPtr(hWnd, GwlExStyle)).Returns(new nint(extendedStyle));
            Native.Setup(n => n.GetWindow(hWnd, GwOwner)).Returns(owner);
            Native.Setup(n => n.GetWindowThreadProcessId(hWnd)).Returns(processId);
            Native.Setup(n => n.GetProcessName(processId)).Returns(processName);
            Native.Setup(n => n.GetWindowRect(hWnd)).Returns((0, 0, 800, 600));
            Native.Setup(n => n.IsIconic(hWnd)).Returns(minimized);
            Native.Setup(n => n.GetAncestor(hWnd, WindowAncestor.Root)).Returns(topLevel ? hWnd : ChildWindowRoot);
            return hWnd;
        }

        public void SetChildren(nint parent, params nint[] children) =>
            Native.Setup(n => n.EnumChildWindows(parent, It.IsAny<Func<nint, bool>>()))
                .Callback<nint, Func<nint, bool>>((_, callback) =>
                {
                    foreach (var child in children)
                    {
                        if (!callback(child))
                            break;
                    }
                });
    }
}
