namespace WindowsAppManagerMcp.Tests.Unit.Services;

/// <summary>
/// The close guard (roadmap M1.7): WindowService refuses the desktop shell's windows and the agent's host
/// windows with ProtectedWindow unless force is set, and reports why Windows refused a close message.
/// Every window is a fake on a mocked <see cref="INativeWindowWrapper"/>, so nothing is posted to a real
/// window.
/// </summary>
public class WindowServiceCloseTests
{
    // Win32 values (NativeEnums is internal to the server assembly)
    private const uint WmClose = 0x0010;
    private const uint GwOwner = 4;
    private const int ErrorAccessDenied = 5;
    private const int ErrorInvalidWindowHandle = 1400;
    private const int ErrorNotEnoughQuota = 1816;

    private readonly Mock<INativeWindowWrapper> _native = new();
    private readonly Mock<IHostGuard> _hostGuard = new();
    private int _nextHandle = 0x1000;

    public WindowServiceCloseTests()
    {
        _native.Setup(n => n.PostMessage(It.IsAny<nint>(), WmClose, nint.Zero, nint.Zero)).Returns(Win32CallResult.Ok);
    }

    private WindowService CreateSut() =>
        new(TestDataFactory.CreateMockMonitorService().Object, _native.Object, _hostGuard.Object);

    /// <summary>
    /// Adds a window. A top-level window is its own root and root owner; a child names its top-level
    /// <paramref name="root"/>, and an owned popup its <paramref name="rootOwner"/>.
    /// </summary>
    private nint AddWindow(string className, nint root = 0, nint rootOwner = 0, uint processId = 100)
    {
        var hWnd = new nint(_nextHandle++);
        var topLevel = root == 0 ? hWnd : root;
        _native.Setup(n => n.IsWindow(hWnd)).Returns(true);
        _native.Setup(n => n.GetClassName(hWnd)).Returns(className);
        _native.Setup(n => n.GetAncestor(hWnd, WindowAncestor.Root)).Returns(topLevel);
        _native.Setup(n => n.GetAncestor(hWnd, WindowAncestor.RootOwner)).Returns(rootOwner == 0 ? topLevel : rootOwner);
        _native.Setup(n => n.GetWindowThreadProcessId(hWnd)).Returns(processId);
        return hWnd;
    }

    private void VerifyPosted(nint hWnd) =>
        _native.Verify(n => n.PostMessage(hWnd, WmClose, nint.Zero, nint.Zero), Times.Once);

    private void VerifyNothingPosted() =>
        _native.Verify(n => n.PostMessage(It.IsAny<nint>(), It.IsAny<uint>(), It.IsAny<nint>(), It.IsAny<nint>()), Times.Never);

    #region Ordinary windows

    [Fact]
    public void CloseWindow_AppWindow_PostsWmClose()
    {
        // Arrange
        var notepad = AddWindow("Notepad");

        // Act
        var result = CreateSut().CloseWindow(notepad, force: false);

        // Assert
        result.Should().Be(new WindowCloseResult(notepad, Success: true));
        VerifyPosted(notepad);
        _hostGuard.Verify(g => g.IsHostWindow(notepad), Times.Once);
    }

    [Fact]
    public void CloseWindow_InvalidHandle_ReturnsInvalidHandle_WithoutPostingOrAskingTheGuard()
    {
        // Arrange - nothing is set up for this handle, so IsWindow is false
        var gone = new nint(0x9999);

        // Act
        var result = CreateSut().CloseWindow(gone, force: true);

        // Assert
        result.Success.Should().BeFalse();
        result.ErrorCode.Should().Be(WindowErrorCode.InvalidHandle);
        result.Error.Should().Be("Invalid window handle");
        VerifyNothingPosted();
        _hostGuard.Verify(g => g.IsHostWindow(It.IsAny<nint>()), Times.Never);
    }

    #endregion

    #region The desktop shell

    [Theory]
    [InlineData("Progman")]
    [InlineData("WorkerW")]
    [InlineData("Shell_TrayWnd")]
    [InlineData("Shell_SecondaryTrayWnd")]
    [InlineData("shell_traywnd")]
    public void CloseWindow_ShellWindowClass_IsRefusedAsProtected(string className)
    {
        // Arrange
        var shell = AddWindow(className);

        // Act
        var result = CreateSut().CloseWindow(shell, force: false);

        // Assert
        result.Success.Should().BeFalse();
        result.ErrorCode.Should().Be(WindowErrorCode.ProtectedWindow);
        result.Error.Should().Contain(className).And.Contain("desktop shell").And.Contain("force=true");
        VerifyNothingPosted();
    }

    [Fact]
    public void CloseWindow_ChildOfTheTaskbar_IsRefused()
    {
        // Arrange - DefWindowProc destroys a child window that gets WM_CLOSE, so part of the taskbar would go
        var taskbar = AddWindow("Shell_TrayWnd");
        var taskList = AddWindow("MSTaskSwWClass", root: taskbar);

        // Act
        var result = CreateSut().CloseWindow(taskList, force: false);

        // Assert
        result.ErrorCode.Should().Be(WindowErrorCode.ProtectedWindow);
        result.Error.Should().Contain("Shell_TrayWnd");
        VerifyNothingPosted();
    }

    [Fact]
    public void CloseWindow_PopupOwnedByTheTaskbar_IsRefused()
    {
        // Arrange - a helper popup of the shell, such as a taskbar tooltip
        var taskbar = AddWindow("Shell_TrayWnd");
        var tooltip = AddWindow("tooltips_class32", rootOwner: taskbar);

        // Act
        var result = CreateSut().CloseWindow(tooltip, force: false);

        // Assert
        result.ErrorCode.Should().Be(WindowErrorCode.ProtectedWindow);
        VerifyNothingPosted();
    }

    [Fact]
    public void CloseWindow_OverlappedWindowOwnedByTheTaskbar_IsRefused()
    {
        // Arrange - owned without WS_POPUP, so GA_ROOTOWNER returns the window itself and only GW_OWNER
        // (the shared owner chain) links it to the taskbar
        var taskbar = AddWindow("Shell_TrayWnd");
        var helper = AddWindow("ShellHelperWindow");
        _native.Setup(n => n.GetWindow(helper, GwOwner)).Returns(taskbar);

        // Act
        var result = CreateSut().CloseWindow(helper, force: false);

        // Assert
        result.ErrorCode.Should().Be(WindowErrorCode.ProtectedWindow);
        result.Error.Should().Contain("Shell_TrayWnd");
        VerifyNothingPosted();
    }

    [Fact]
    public void CloseWindow_TheShellWindow_IsRefusedWhateverItsClass()
    {
        // Arrange - a replacement shell's desktop window has its own class
        var desktop = AddWindow("CustomShellDesktop");
        _native.Setup(n => n.GetShellWindow()).Returns(desktop);

        // Act
        var result = CreateSut().CloseWindow(desktop, force: false);

        // Assert
        result.ErrorCode.Should().Be(WindowErrorCode.ProtectedWindow);
        result.Error.Should().Contain("CustomShellDesktop");
        VerifyNothingPosted();
    }

    [Fact]
    public void CloseWindow_FileExplorerWindow_IsNotProtected()
    {
        // Arrange - explorer.exe owns the desktop and the taskbar, but its folder windows are ordinary windows
        var folder = AddWindow("CabinetWClass", processId: 300);
        var desktop = AddWindow("Progman", processId: 300);
        _native.Setup(n => n.GetShellWindow()).Returns(desktop);

        // Act
        var result = CreateSut().CloseWindow(folder, force: false);

        // Assert
        result.Success.Should().BeTrue();
        VerifyPosted(folder);
    }

    #endregion

    #region Only the shell's own windows

    private const uint ShellPid = 300;
    private const uint OtherAppPid = 500;

    /// <summary>Makes a Progman window of <see cref="ShellPid"/> the shell's desktop window (GetShellWindow).</summary>
    private nint UseShell()
    {
        var desktop = AddWindow("Progman", processId: ShellPid);
        _native.Setup(n => n.GetShellWindow()).Returns(desktop);
        return desktop;
    }

    [Theory]
    [InlineData("Progman")]
    [InlineData("WorkerW")]
    [InlineData("Shell_TrayWnd")]
    [InlineData("Shell_SecondaryTrayWnd")]
    public void CloseWindow_ShellClassInTheShellsProcess_IsRefused(string className)
    {
        // Arrange
        UseShell();
        var window = AddWindow(className, processId: ShellPid);

        // Act
        var result = CreateSut().CloseWindow(window, force: false);

        // Assert
        result.ErrorCode.Should().Be(WindowErrorCode.ProtectedWindow);
        result.Error.Should().Contain(className);
        VerifyNothingPosted();
    }

    [Theory]
    [InlineData("WorkerW")]
    [InlineData("Progman")]
    [InlineData("Shell_TrayWnd")]
    [InlineData("Shell_SecondaryTrayWnd")]
    public void CloseWindow_ShellClassInAnotherProcess_IsNotProtected(string className)
    {
        // Arrange - WorkerW is also the class of SHCreateWorkerWindow, which any app that uses shlwapi can create
        UseShell();
        var helper = AddWindow(className, processId: OtherAppPid);

        // Act
        var result = CreateSut().CloseWindow(helper, force: false);

        // Assert
        result.Success.Should().BeTrue();
        VerifyPosted(helper);
    }

    [Fact]
    public void CloseWindow_WindowInsideAWorkerWOfAnotherProcess_IsNotProtected()
    {
        // Arrange
        UseShell();
        var worker = AddWindow("WorkerW", processId: OtherAppPid);
        var child = AddWindow("AppPane", root: worker, processId: OtherAppPid);

        // Act
        var result = CreateSut().CloseWindow(child, force: false);

        // Assert
        result.Success.Should().BeTrue();
        VerifyPosted(child);
    }

    [Fact]
    public void CloseWindow_ChildOfTheShellsWorkerW_IsRefused()
    {
        // Arrange - the desktop icons sit in a WorkerW layer of the shell
        UseShell();
        var worker = AddWindow("WorkerW", processId: ShellPid);
        var icons = AddWindow("SHELLDLL_DefView", root: worker, processId: ShellPid);

        // Act
        var result = CreateSut().CloseWindow(icons, force: false);

        // Assert
        result.ErrorCode.Should().Be(WindowErrorCode.ProtectedWindow);
        result.Error.Should().Contain("WorkerW");
        VerifyNothingPosted();
    }

    [Fact]
    public void CloseWindow_ShellClassWhileNoShellWindowExists_IsRefusedByItsClassAlone()
    {
        // Arrange - explorer.exe is restarting (GetShellWindow is zero), so its process is not known: stay on the safe side
        var taskbar = AddWindow("Shell_TrayWnd", processId: OtherAppPid);
        _native.Setup(n => n.GetShellWindow()).Returns(nint.Zero);

        // Act
        var result = CreateSut().CloseWindow(taskbar, force: false);

        // Assert
        result.ErrorCode.Should().Be(WindowErrorCode.ProtectedWindow);
        VerifyNothingPosted();
    }

    #endregion

    #region Dialogs that the shell owns

    /// <summary>Adds a visible, top-level dialog (class #32770) owned by <paramref name="owner"/>.</summary>
    private nint AddDialog(nint owner, uint processId = ShellPid, bool visible = true)
    {
        var dialog = AddWindow("#32770", rootOwner: owner, processId: processId);
        _native.Setup(n => n.IsWindowVisible(dialog)).Returns(visible);
        return dialog;
    }

    [Fact]
    public void CloseWindow_ShutDownWindowsDialogOfTheDesktop_IsClosed()
    {
        // Arrange - closing Program Manager opens Shut Down Windows; WM_CLOSE on a dialog works like its
        // Cancel button (DefDlgProc turns it into IDCANCEL), so it cancels the dialog and leaves its owner alone
        var desktop = UseShell();
        var shutDown = AddDialog(owner: desktop);

        // Act
        var result = CreateSut().CloseWindow(shutDown, force: false);

        // Assert
        result.Should().Be(new WindowCloseResult(shutDown, Success: true));
        VerifyPosted(shutDown);
        _hostGuard.Verify(g => g.IsHostWindow(shutDown), Times.Once, "the host rule still applies to dialogs");
    }

    [Fact]
    public void CloseWindow_DialogOwnedByTheTaskbar_IsClosed()
    {
        // Arrange
        UseShell();
        var taskbar = AddWindow("Shell_TrayWnd", processId: ShellPid);
        var dialog = AddDialog(owner: taskbar);

        // Act
        var result = CreateSut().CloseWindow(dialog, force: false);

        // Assert
        result.Success.Should().BeTrue();
        VerifyPosted(dialog);
    }

    [Fact]
    public void CloseWindow_DialogOwnedByTheShellWhileNoShellWindowExists_IsClosed()
    {
        // Arrange - the dialog itself is not a shell window, whatever is known about the shell's process
        var taskbar = AddWindow("Shell_TrayWnd", processId: ShellPid);
        var dialog = AddDialog(owner: taskbar);

        // Act
        var result = CreateSut().CloseWindow(dialog, force: false);

        // Assert
        result.Success.Should().BeTrue();
        VerifyPosted(dialog);
    }

    [Fact]
    public void CloseWindow_HiddenDialogOwnedByTheShell_IsRefused()
    {
        // Arrange - a hidden dialog is a helper window of the shell, not one the user can see and dismiss
        var desktop = UseShell();
        var helper = AddDialog(owner: desktop, visible: false);

        // Act
        var result = CreateSut().CloseWindow(helper, force: false);

        // Assert
        result.ErrorCode.Should().Be(WindowErrorCode.ProtectedWindow);
        VerifyNothingPosted();
    }

    [Fact]
    public void CloseWindow_DialogInsideAShellWindow_IsRefused()
    {
        // Arrange - a child dialog (for example a page) is part of the shell window that contains it
        UseShell();
        var taskbar = AddWindow("Shell_TrayWnd", processId: ShellPid);
        var page = AddWindow("#32770", root: taskbar, processId: ShellPid);
        _native.Setup(n => n.IsWindowVisible(page)).Returns(true);

        // Act
        var result = CreateSut().CloseWindow(page, force: false);

        // Assert
        result.ErrorCode.Should().Be(WindowErrorCode.ProtectedWindow);
        VerifyNothingPosted();
    }

    [Fact]
    public void CloseWindow_VisiblePopupOfTheShellThatIsNotADialog_IsStillRefused()
    {
        // Arrange - only dialogs turn WM_CLOSE into Cancel; DefWindowProc destroys any other popup
        UseShell();
        var taskbar = AddWindow("Shell_TrayWnd", processId: ShellPid);
        var flyout = AddWindow("Xaml_WindowedPopupClass", rootOwner: taskbar, processId: ShellPid);
        _native.Setup(n => n.IsWindowVisible(flyout)).Returns(true);

        // Act
        var result = CreateSut().CloseWindow(flyout, force: false);

        // Assert
        result.ErrorCode.Should().Be(WindowErrorCode.ProtectedWindow);
        result.Error.Should().Contain("Shell_TrayWnd");
        VerifyNothingPosted();
    }

    [Fact]
    public void CloseWindow_DialogOfTheAgentsHost_IsStillRefused()
    {
        // Arrange
        var terminal = AddWindow("CASCADIA_HOSTING_WINDOW_CLASS", processId: OtherAppPid);
        var dialog = AddDialog(owner: terminal, processId: OtherAppPid);
        _hostGuard.Setup(g => g.IsHostWindow(dialog)).Returns(true);

        // Act
        var result = CreateSut().CloseWindow(dialog, force: false);

        // Assert
        result.ErrorCode.Should().Be(WindowErrorCode.ProtectedWindow);
        result.Error.Should().Contain("agent's host");
        VerifyNothingPosted();
    }

    #endregion

    #region The agent's host

    [Fact]
    public void CloseWindow_AgentHostWindow_IsRefusedAsProtected()
    {
        // Arrange
        var terminal = AddWindow("CASCADIA_HOSTING_WINDOW_CLASS");
        _hostGuard.Setup(g => g.IsHostWindow(terminal)).Returns(true);

        // Act
        var result = CreateSut().CloseWindow(terminal, force: false);

        // Assert
        result.Success.Should().BeFalse();
        result.ErrorCode.Should().Be(WindowErrorCode.ProtectedWindow);
        result.Error.Should().Contain("agent's host").And.Contain("force=true");
        VerifyNothingPosted();
    }

    [Fact]
    public void CloseWindow_DialogOwnedByTheConsoleHostWindow_IsRefused_ByTheRealHostGuard()
    {
        // Arrange - the server's console is shown by a terminal window; a dialog of that terminal is the host too
        var processes = new Mock<INativeProcessWrapper>();
        var terminal = AddWindow("CASCADIA_HOSTING_WINDOW_CLASS", processId: 700);
        var pseudoConsole = AddWindow("PseudoConsoleWindow", rootOwner: terminal, processId: 701);
        var terminalDialog = AddWindow("#32770", rootOwner: terminal, processId: 700);
        var otherApp = AddWindow("Notepad", processId: 803); // not a multiple of 4, so never the test host's own PID
        processes.Setup(p => p.GetAncestorProcesses()).Returns([]);
        processes.Setup(p => p.GetConsoleWindow()).Returns(pseudoConsole);
        var hostGuard = new HostGuard(_native.Object, processes.Object);
        var sut = new WindowService(TestDataFactory.CreateMockMonitorService().Object, _native.Object, hostGuard);

        // Act
        var dialogResult = sut.CloseWindow(terminalDialog, force: false);
        var terminalResult = sut.CloseWindow(terminal, force: false);
        var otherResult = sut.CloseWindow(otherApp, force: false);

        // Assert
        dialogResult.ErrorCode.Should().Be(WindowErrorCode.ProtectedWindow);
        terminalResult.ErrorCode.Should().Be(WindowErrorCode.ProtectedWindow);
        otherResult.Success.Should().BeTrue();
        _native.Verify(n => n.PostMessage(It.IsAny<nint>(), It.IsAny<uint>(), It.IsAny<nint>(), It.IsAny<nint>()), Times.Once);
        VerifyPosted(otherApp);
    }

    [Fact]
    public void CloseWindow_WpfDialogOwnedByTheRecordedForegroundWindow_IsRefused_ByTheRealHostGuard()
    {
        // Arrange - a delegated terminal: not an ancestor and no console, only its window recorded at start.
        // Its WPF dialog is owned (GW_OWNER) but not WS_POPUP, so GA_ROOTOWNER is the dialog itself
        var processes = new Mock<INativeProcessWrapper>();
        var terminal = AddWindow("CASCADIA_HOSTING_WINDOW_CLASS", processId: 705);
        var wpfDialog = AddWindow("HwndWrapper[Terminal;;1]", processId: 705);
        var otherWindowOfTheTerminal = AddWindow("CASCADIA_HOSTING_WINDOW_CLASS", processId: 705);
        _native.Setup(n => n.GetWindow(wpfDialog, GwOwner)).Returns(terminal);
        _native.Setup(n => n.GetForegroundWindow()).Returns(terminal);
        processes.Setup(p => p.GetAncestorProcesses()).Returns([]);
        var hostGuard = new HostGuard(_native.Object, processes.Object);
        var sut = new WindowService(TestDataFactory.CreateMockMonitorService().Object, _native.Object, hostGuard);

        // Act
        var dialogResult = sut.CloseWindow(wpfDialog, force: false);
        var otherResult = sut.CloseWindow(otherWindowOfTheTerminal, force: false);

        // Assert
        dialogResult.ErrorCode.Should().Be(WindowErrorCode.ProtectedWindow);
        dialogResult.Error.Should().Contain("agent's host");
        otherResult.Success.Should().BeTrue("only the recorded window and the windows it owns are host windows");
        VerifyPosted(otherWindowOfTheTerminal);
        _native.Verify(n => n.PostMessage(wpfDialog, It.IsAny<uint>(), It.IsAny<nint>(), It.IsAny<nint>()), Times.Never);
    }

    #endregion

    #region force

    [Theory]
    [InlineData("Progman", false)]
    [InlineData("Shell_TrayWnd", false)]
    [InlineData("CASCADIA_HOSTING_WINDOW_CLASS", true)]
    public void CloseWindow_WithForce_PostsToAProtectedWindow(string className, bool isHost)
    {
        // Arrange
        var window = AddWindow(className);
        _hostGuard.Setup(g => g.IsHostWindow(window)).Returns(isHost);

        // Act
        var result = CreateSut().CloseWindow(window, force: true);

        // Assert
        result.Success.Should().BeTrue();
        VerifyPosted(window);
    }

    #endregion

    #region Win32 errors from PostMessage

    [Fact]
    public void CloseWindow_PostMessageAccessDenied_ReturnsAccessDenied()
    {
        // Arrange - UIPI: the window belongs to an elevated app
        var elevated = AddWindow("ElevatedApp");
        _native.Setup(n => n.PostMessage(elevated, WmClose, nint.Zero, nint.Zero))
            .Returns(Win32CallResult.Failure(ErrorAccessDenied));

        // Act
        var result = CreateSut().CloseWindow(elevated, force: false);

        // Assert
        result.Success.Should().BeFalse();
        result.ErrorCode.Should().Be(WindowErrorCode.AccessDenied);
        result.Error.Should().Contain("Win32 error 5").And.Contain("administrator");
    }

    [Fact]
    public void CloseWindow_WindowGoneBeforeThePost_ReturnsInvalidHandle()
    {
        // Arrange - IsWindow was still true, then the window closed by itself
        var closing = AddWindow("Notepad");
        _native.Setup(n => n.PostMessage(closing, WmClose, nint.Zero, nint.Zero))
            .Returns(Win32CallResult.Failure(ErrorInvalidWindowHandle));

        // Act
        var result = CreateSut().CloseWindow(closing, force: false);

        // Assert
        result.ErrorCode.Should().Be(WindowErrorCode.InvalidHandle);
        result.Error.Should().Contain("Win32 error 1400");
    }

    [Fact]
    public void CloseWindow_FullMessageQueue_IsNotResponding()
    {
        // Arrange - the window's message queue is full (a hung app)
        var hung = AddWindow("Notepad");
        _native.Setup(n => n.PostMessage(hung, WmClose, nint.Zero, nint.Zero))
            .Returns(Win32CallResult.Failure(ErrorNotEnoughQuota));

        // Act
        var result = CreateSut().CloseWindow(hung, force: false);

        // Assert
        result.ErrorCode.Should().Be(WindowErrorCode.NotResponding);
        result.Error.Should().Contain("Win32 error 1816").And.Contain("not responding").And.Contain("Nothing was closed");
    }

    [Fact]
    public void CloseWindow_OtherPostMessageFailure_ReturnsCloseFailedWithTheWin32Error()
    {
        // Arrange - ERROR_NOT_ENOUGH_MEMORY
        var window = AddWindow("Notepad");
        _native.Setup(n => n.PostMessage(window, WmClose, nint.Zero, nint.Zero))
            .Returns(Win32CallResult.Failure(8));

        // Act
        var result = CreateSut().CloseWindow(window, force: false);

        // Assert
        result.ErrorCode.Should().Be(WindowErrorCode.CloseFailed);
        result.Error.Should().Contain("Win32 error 8");
    }

    #endregion

    #region CloseWindowsBatch

    [Fact]
    public async Task CloseWindowsBatch_RefusesTheProtectedWindows_AndClosesTheRest()
    {
        // Arrange - "close everything except the editor", built from a listing that included the shell
        var notepad = AddWindow("Notepad");
        var desktop = AddWindow("Progman");
        var terminal = AddWindow("CASCADIA_HOSTING_WINDOW_CLASS");
        var paint = AddWindow("MSPaintApp");
        _hostGuard.Setup(g => g.IsHostWindow(terminal)).Returns(true);

        // Act
        var results = await CreateSut().CloseWindowsBatchAsync([notepad, desktop, terminal, paint], force: false, delayBetweenMs: 0);

        // Assert
        results.Select(r => r.Handle).Should().Equal(notepad, desktop, terminal, paint);
        results.Select(r => r.ErrorCode).Should().Equal(
            WindowErrorCode.None, WindowErrorCode.ProtectedWindow, WindowErrorCode.ProtectedWindow, WindowErrorCode.None);
        VerifyPosted(notepad);
        VerifyPosted(paint);
        _native.Verify(n => n.PostMessage(desktop, It.IsAny<uint>(), It.IsAny<nint>(), It.IsAny<nint>()), Times.Never);
        _native.Verify(n => n.PostMessage(terminal, It.IsAny<uint>(), It.IsAny<nint>(), It.IsAny<nint>()), Times.Never);
    }

    [Fact]
    public async Task CloseWindowsBatch_WithForce_ClosesProtectedWindowsToo()
    {
        // Arrange
        var notepad = AddWindow("Notepad");
        var taskbar = AddWindow("Shell_TrayWnd");

        // Act
        var results = await CreateSut().CloseWindowsBatchAsync([notepad, taskbar], force: true, delayBetweenMs: 0);

        // Assert
        results.Should().OnlyContain(r => r.Success);
        VerifyPosted(notepad);
        VerifyPosted(taskbar);
    }

    [Fact]
    public async Task CloseWindowsBatch_ReportsEachFailureWithItsCode()
    {
        // Arrange
        var gone = new nint(0x9999);
        var elevated = AddWindow("ElevatedApp");
        var throwing = AddWindow("Broken");
        _native.Setup(n => n.PostMessage(elevated, WmClose, nint.Zero, nint.Zero))
            .Returns(Win32CallResult.Failure(ErrorAccessDenied));
        _native.Setup(n => n.PostMessage(throwing, WmClose, nint.Zero, nint.Zero))
            .Throws(new InvalidOperationException("boom"));

        // Act
        var results = await CreateSut().CloseWindowsBatchAsync([gone, elevated, throwing], force: false, delayBetweenMs: 0);

        // Assert
        results.Select(r => r.ErrorCode).Should().Equal(
            WindowErrorCode.InvalidHandle, WindowErrorCode.AccessDenied, WindowErrorCode.CloseFailed);
        results[2].Error.Should().Be("boom");
    }

    #endregion
}
