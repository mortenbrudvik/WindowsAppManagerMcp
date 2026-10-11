namespace WindowsAppManagerMcp.Tests.Unit.Services;

/// <summary>
/// The Win32 reads of the verified close (roadmap M2.5): a window's owned windows (get_owned_windows), the prompt that
/// blocks a close (GW_ENABLEDPOPUP of a disabled window, or an owned #32770 dialog of a disabled owner) with its text and
/// buttons, the Alt-Tab set that close_application closes, and the close checks without the close. Every window is a fake
/// on a mocked <see cref="INativeWindowWrapper"/>, so nothing is posted to a real window.
/// </summary>
public class WindowServiceOwnedWindowsTests
{
    // Win32 values (NativeEnums is internal to the server assembly)
    private const int GwlStyle = -16;
    private const int GwlExStyle = -20;
    private const uint GwOwner = 4;
    private const uint GwEnabledPopup = 6;
    private const long WsDisabled = 0x08000000;
    private const long WsExToolWindow = 0x00000080;
    private const long WsExAppWindow = 0x00040000;
    private const uint ServerProcessId = 4242;

    private readonly FakeDesktop _desktop = new();
    private readonly Mock<IHostGuard> _hostGuard = new();

    private WindowService CreateSut(ITargetPolicy? policy = null) =>
        new(TestDataFactory.CreateMockMonitorService().Object, _desktop.Native.Object, _hostGuard.Object, policy);

    #region get_owned_windows

    [Fact]
    public void GetOwnedWindows_ListsTheVisibleWindowsItOwns_DirectlyOrThroughOtherOwnedWindows()
    {
        // Arrange - Save As (owned by the main window) and its Overwrite? message box (owned by Save As)
        var main = _desktop.Add("notes.txt - Notepad", "Notepad", processName: "notepad");
        var saveAs = _desktop.Add("Save As", "#32770", owner: main, processName: "notepad");
        var overwrite = _desktop.Add("Confirm Save As", "#32770", owner: saveAs, processName: "notepad");
        _desktop.Add("Untitled - Notepad", "Notepad", processName: "notepad");
        _desktop.Add("Default IME", "IME", owner: main, visible: false);

        // Act
        var owned = CreateSut().GetOwnedWindows(main);

        // Assert
        owned.Select(o => o.Window.Handle).Should().Equal(saveAs, overwrite);
        owned.Should().OnlyContain(o => o.IsDialog && !o.IsBlocking, "the main window is not disabled");
        owned[1].Window.OwnerHandle.Should().Be(saveAs);
    }

    [Fact]
    public void GetOwnedWindows_MarksTheEnabledPopupOfADisabledOwner_AsBlocking()
    {
        // Arrange - a modal dialog disabled the main window
        var main = _desktop.Add("Document - Editor", disabled: true);
        var options = _desktop.Add("Options", "HwndWrapper[Editor;;1]", owner: main);
        var palette = _desktop.Add("Tools", "Palette", owner: main, extendedStyle: WsExToolWindow);
        _desktop.SetEnabledPopup(main, options);

        // Act
        var owned = CreateSut().GetOwnedWindows(main);

        // Assert
        owned.Single(o => o.Window.Handle == options).Should().Match<OwnedWindowInfo>(o => o.IsBlocking && !o.IsDialog);
        owned.Single(o => o.Window.Handle == palette).IsBlocking.Should().BeFalse();
    }

    [Fact]
    public void GetOwnedWindows_MarksTheOwnedDialogTheCloseToolsReport_AsBlocking()
    {
        // Arrange - the prompt is owned by a disabled owned window of the main window, as FindBlockingDialog's ownedDialog
        var main = _desktop.Add("Untitled - Editor", disabled: true);
        var helper = _desktop.Add("Helper", "Helper", owner: main, disabled: true);
        var prompt = _desktop.Add("Save changes?", "#32770", owner: helper);

        // Act
        var owned = CreateSut().GetOwnedWindows(main);

        // Assert
        owned.Single(o => o.Window.Handle == prompt).IsBlocking.Should().BeTrue();
        owned.Single(o => o.Window.Handle == helper).IsBlocking.Should().BeFalse();
        CreateSut().FindBlockingDialog(main)!.Handle.Should().Be(prompt);
    }

    [Fact]
    public void GetOwnedWindows_IncludeHidden_AlsoListsHiddenHelperWindows()
    {
        var main = _desktop.Add("Untitled - Notepad", "Notepad");
        var ime = _desktop.Add("Default IME", "IME", owner: main, visible: false);

        CreateSut().GetOwnedWindows(main).Should().BeEmpty();
        CreateSut().GetOwnedWindows(main, includeHidden: true).Should().ContainSingle().Which.Window.Handle.Should().Be(ime);
    }

    [Fact]
    public void GetOwnedWindows_OfANonWindow_IsEmpty()
    {
        CreateSut().GetOwnedWindows(new nint(0x9999)).Should().BeEmpty();
    }

    #endregion

    #region The prompt that blocks a close

    [Fact]
    public void FindBlockingDialog_DisabledWindowWithAnEnabledPopup_IsAModalPopup_WithItsTextAndButtons()
    {
        // Arrange - a MessageBox: a #32770 with a Static text, an icon Static without text and three buttons
        var main = _desktop.Add("Untitled - Editor", disabled: true);
        var prompt = _desktop.Add("Editor", "#32770", owner: main);
        _desktop.SetEnabledPopup(main, prompt);
        _desktop.SetChildren(prompt,
            _desktop.Add("", "Static", topLevel: false),
            _desktop.Add("Do you want to save changes to Untitled?", "Static", topLevel: false),
            _desktop.Add("&Yes", "Button", topLevel: false),
            _desktop.Add("&No", "Button", topLevel: false),
            _desktop.Add("Cancel", "Button", topLevel: false));

        // Act
        var found = CreateSut().FindBlockingDialog(main);

        // Assert
        found.Should().NotBeNull();
        found!.Handle.Should().Be(prompt);
        found.Title.Should().Be("Editor");
        found.ClassName.Should().Be("#32770");
        found.Kind.Should().Be(ClosePromptKind.ModalPopup);
        found.Text.Should().Be("Do you want to save changes to Untitled?");
        found.Buttons.Should().Equal("Yes", "No", "Cancel");
    }

    [Fact]
    public void FindBlockingDialog_KeepsADoubledAmpersand_AsOneAmpersand()
    {
        var main = _desktop.Add("Untitled - Editor", disabled: true);
        var prompt = _desktop.Add("Editor", "#32770", owner: main);
        _desktop.SetEnabledPopup(main, prompt);
        _desktop.SetChildren(prompt, _desktop.Add("Save && &close", "Button", topLevel: false));

        CreateSut().FindBlockingDialog(main)!.Buttons.Should().Equal("Save & close");
    }

    [Fact]
    public void FindBlockingDialog_DisabledOwnerOfAnOwnedDialog_FindsTheDialog_WhenGwEnabledPopupDoesNot()
    {
        // Arrange - the prompt is owned by an owned window of the closed window, and GW_ENABLEDPOPUP returns the window itself
        var main = _desktop.Add("Untitled - Editor", disabled: true);
        var helper = _desktop.Add("Helper", "Helper", owner: main, disabled: true);
        var prompt = _desktop.Add("Save changes?", "#32770", owner: helper);

        // Act
        var found = CreateSut().FindBlockingDialog(main);

        // Assert
        found!.Handle.Should().Be(prompt);
        found.Kind.Should().Be(ClosePromptKind.OwnedDialog);
        found.Text.Should().BeNull("the dialog has no Static text");
        found.Buttons.Should().BeEmpty();
    }

    [Fact]
    public void FindBlockingDialog_ANonModalDialogOfAnEnabledWindow_IsNoPrompt()
    {
        // Arrange - Find stays open next to the window, which still takes input
        var main = _desktop.Add("Untitled - Notepad", "Notepad");
        var find = _desktop.Add("Find", "#32770", owner: main);
        _desktop.SetEnabledPopup(main, find);

        // Act + Assert
        CreateSut().FindBlockingDialog(main).Should().BeNull();
    }

    [Fact]
    public void FindBlockingDialog_HiddenOrCloakedPopups_AreNoPrompt()
    {
        var main = _desktop.Add("Untitled - Editor", disabled: true);
        var hidden = _desktop.Add("Hidden", "#32770", owner: main, visible: false);
        _desktop.Add("Elsewhere", "#32770", owner: main, cloaked: true);
        _desktop.SetEnabledPopup(main, hidden);

        CreateSut().FindBlockingDialog(main).Should().BeNull();
    }

    [Fact]
    public void FindBlockingDialog_WithoutAPrompt_OrForANonWindow_IsNull()
    {
        var main = _desktop.Add("Untitled - Notepad", "Notepad");
        _desktop.SetEnabledPopup(main, main);

        CreateSut().FindBlockingDialog(main).Should().BeNull();
        CreateSut().FindBlockingDialog(new nint(0x9999)).Should().BeNull();
    }

    #endregion

    #region The Alt-Tab set

    [Fact]
    public void GetApplicationWindows_IsTheAltTabSet()
    {
        // Arrange
        var main = _desktop.Add("notes.txt - Notepad", "Notepad", processId: 700, processName: "notepad");
        var minimized = _desktop.Add("todo.txt - Notepad", "Notepad", processId: 700, processName: "notepad", minimized: true);
        var untitled = _desktop.Add("", "Chrome_WidgetWin_1", processId: 701, processName: "chrome");
        var ownedApp = _desktop.Add("Detached tab", "AppWindow", owner: main, extendedStyle: WsExAppWindow, processId: 700, processName: "notepad");
        _desktop.Add("Save As", "#32770", owner: main, processId: 700, processName: "notepad");
        _desktop.Add("Default IME", "IME", processId: 700, processName: "notepad");
        _desktop.Add("MSCTFIME UI", "MSCTFIME UI", processId: 700, processName: "notepad");
        _desktop.Add("GDI+ Window (notepad.exe)", "GDI+ Hook Window Class", processId: 700, processName: "notepad");
        _desktop.Add("Tray", "TrayAppHiddenWindow", visible: false, processId: 702, processName: "trayapp");
        _desktop.Add("Layers", "Palette", extendedStyle: WsExToolWindow, processId: 700, processName: "notepad");
        _desktop.Add("Elsewhere", "Notepad", cloaked: true, processId: 700, processName: "notepad");
        _desktop.Add("Program Manager", "Progman", processId: 300, processName: "explorer");
        _desktop.Add("Server window", processId: ServerProcessId, processName: "WindowsAppManagerMcp");

        // Act
        var windows = CreateSut().GetApplicationWindows();

        // Assert
        windows.Select(w => w.Handle).Should().Equal(main, minimized, untitled, ownedApp);
        windows[1].State.Should().Be(WindowState.Minimized);
    }

    [Fact]
    public void GetApplicationWindowsOnOtherDesktops_AreTheAltTabWindowsTheShellCloaks()
    {
        // Arrange - DWM_CLOAKED_SHELL (2): on another virtual desktop; DWM_CLOAKED_APP (1): the app cloaked it (a suspended frame)
        _desktop.Add("notes.txt - Notepad", "Notepad", processId: 700, processName: "notepad");
        var elsewhere = _desktop.Add("draft.txt - Notepad", "Notepad", cloaked: true, processId: 700, processName: "notepad");
        _desktop.Native.Setup(n => n.GetCloakReason(elsewhere)).Returns(2);
        var suspended = _desktop.Add("Calculator", "Notepad", cloaked: true, processId: 701, processName: "calc");
        _desktop.Native.Setup(n => n.GetCloakReason(suspended)).Returns(1);
        var tool = _desktop.Add("Layers", "Palette", extendedStyle: WsExToolWindow, cloaked: true, processId: 700, processName: "notepad");
        _desktop.Native.Setup(n => n.GetCloakReason(tool)).Returns(2);

        // Act
        var windows = CreateSut().GetApplicationWindowsOnOtherDesktops();

        // Assert
        var window = windows.Should().ContainSingle().Which;
        window.Handle.Should().Be(elsewhere);
        window.IsCloaked.Should().BeTrue();
        CreateSut().GetApplicationWindows().Select(w => w.Handle).Should().NotContain(elsewhere);
    }

    [Fact]
    public void GetApplicationWindows_LeavesOutNoActivateWindows_UnlessTheyAreAppWindows()
    {
        // Arrange - Alt+Tab skips WS_EX_NOACTIVATE windows (overlays, on-screen keyboards) without WS_EX_APPWINDOW
        const long wsExNoActivate = 0x08000000;
        var main = _desktop.Add("notes.txt - Notepad", "Notepad", processId: 700, processName: "notepad");
        _desktop.Add("Overlay", "OverlayWindow", extendedStyle: wsExNoActivate, processId: 700, processName: "notepad");
        var keyboard = _desktop.Add("Keyboard", "KeyboardWindow", extendedStyle: wsExNoActivate | WsExAppWindow, processId: 700, processName: "notepad");

        // Act + Assert
        CreateSut().GetApplicationWindows().Select(w => w.Handle).Should().Equal(main, keyboard);
    }

    [Fact]
    public void GetApplicationWindows_OfTheShellsProcess_AreOnlyFileExplorerWindows()
    {
        // Arrange - explorer.exe draws the desktop, the taskbar's flyouts and File Explorer; a separate explorer.exe has only folders
        var progman = _desktop.Add("Program Manager", "Progman", processId: 300, processName: "explorer");
        _desktop.Native.Setup(n => n.GetShellWindow()).Returns(progman);
        var folder = _desktop.Add("Downloads", "CabinetWClass", processId: 300, processName: "explorer");
        _desktop.Add("Search", "XamlExplorerHostIslandWindow", processId: 300, processName: "explorer");
        _desktop.Add("", "Windows.UI.Input.InputSite.WindowClass", processId: 300, processName: "explorer");
        var separate = _desktop.Add("Documents", "CabinetWClass", processId: 301, processName: "explorer");

        // Act + Assert
        CreateSut().GetApplicationWindows().Select(w => w.Handle).Should().Equal(folder, separate);
    }

    [Fact]
    public void GetApplicationWindows_ReportsAUwpFrameUnderTheAppProcess()
    {
        // Arrange - Calculator's frame belongs to ApplicationFrameHost; its CoreWindow child to CalculatorApp
        var frame = _desktop.Add("Calculator", "ApplicationFrameWindow", processId: 500, processName: "ApplicationFrameHost");
        var core = _desktop.Add("Calculator", "Windows.UI.Core.CoreWindow", processId: 600, processName: "CalculatorApp", topLevel: false);
        _desktop.SetChildren(frame, core);

        // Act
        var window = CreateSut().GetApplicationWindows().Should().ContainSingle().Which;

        // Assert
        window.Handle.Should().Be(frame);
        window.ProcessId.Should().Be(600);
        window.ProcessName.Should().Be("CalculatorApp");
        window.HostProcessName.Should().Be("ApplicationFrameHost");
    }

    #endregion

    #region The close checks without the close

    [Fact]
    public void CheckClose_ReportsWhatCloseWindowWouldRefuse_WithoutPostingAnything()
    {
        // Arrange
        var notepad = _desktop.Add("Untitled - Notepad", "Notepad");
        var terminal = _desktop.Add("pwsh", "CASCADIA_HOSTING_WINDOW_CLASS");
        var denied = _desktop.Add("Bank", "BankApp", processId: 800, processName: "bank");
        _hostGuard.Setup(g => g.IsHostWindow(terminal)).Returns(true);
        var policy = new TargetPolicy(TargetPolicySettings.Default with { DeniedProcesses = ["bank"] });
        var sut = CreateSut(policy);

        // Act + Assert
        sut.CheckClose(notepad, force: false).Should().BeNull();
        sut.CheckClose(terminal, force: false)!.ErrorCode.Should().Be(WindowErrorCode.ProtectedWindow);
        sut.CheckClose(terminal, force: true).Should().BeNull();
        sut.CheckClose(denied, force: true)!.ErrorCode.Should().Be(WindowErrorCode.TargetDeniedByPolicy);
        sut.CheckClose(new nint(0x9999), force: false)!.ErrorCode.Should().Be(WindowErrorCode.InvalidHandle);
        _desktop.Native.Verify(n => n.PostMessage(It.IsAny<nint>(), It.IsAny<uint>(), It.IsAny<nint>(), It.IsAny<nint>()), Times.Never);
    }

    #endregion

    /// <summary>
    /// Fake windows on a mocked <see cref="INativeWindowWrapper"/>. Top-level windows are returned by EnumWindows in the
    /// order they were added (z-order); child windows only through SetChildren. GW_ENABLEDPOPUP returns the window itself
    /// unless SetEnabledPopup says otherwise, as Windows does when a window has no enabled popup.
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
            bool disabled = false,
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
            Native.Setup(n => n.GetWindowLongPtr(hWnd, GwlStyle)).Returns(new nint(disabled ? WsDisabled : 0));
            Native.Setup(n => n.GetWindow(hWnd, GwOwner)).Returns(owner);
            Native.Setup(n => n.GetWindow(hWnd, GwEnabledPopup)).Returns(hWnd);
            Native.Setup(n => n.GetWindowThreadProcessId(hWnd)).Returns(processId);
            Native.Setup(n => n.GetProcessName(processId)).Returns(processName);
            Native.Setup(n => n.GetWindowRect(hWnd)).Returns((0, 0, 800, 600));
            Native.Setup(n => n.IsIconic(hWnd)).Returns(minimized);
            Native.Setup(n => n.GetAncestor(hWnd, WindowAncestor.Root)).Returns(topLevel ? hWnd : 0x0FFF);
            Native.Setup(n => n.GetAncestor(hWnd, WindowAncestor.RootOwner)).Returns(topLevel ? hWnd : 0x0FFF);
            return hWnd;
        }

        public void SetEnabledPopup(nint owner, nint popup) =>
            Native.Setup(n => n.GetWindow(owner, GwEnabledPopup)).Returns(popup);

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
