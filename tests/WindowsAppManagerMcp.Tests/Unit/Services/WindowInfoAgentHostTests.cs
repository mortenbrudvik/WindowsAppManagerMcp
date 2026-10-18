namespace WindowsAppManagerMcp.Tests.Unit.Services;

/// <summary>
/// WindowInfo.IsAgentHost (roadmap M1.1): every window WindowService describes carries the host guard's
/// answer, so the listings, the close guard and the input guard agree on which windows are the agent's
/// host. Every window is a fake on a mocked <see cref="INativeWindowWrapper"/>.
/// </summary>
public class WindowInfoAgentHostTests
{
    private const uint GwOwner = 4;

    private static readonly nint Terminal = 0x100;
    private static readonly nint TerminalWpfDialog = 0x110; // owned by Terminal without WS_POPUP
    private static readonly nint Notepad = 0x200;
    private const uint TerminalPid = 1001; // not a multiple of 4, so never the test host's own PID
    private const uint NotepadPid = 2001;

    private readonly Mock<INativeWindowWrapper> _native = new();
    private readonly Mock<IHostGuard> _hostGuard = new();

    public WindowInfoAgentHostTests()
    {
        Window(Terminal, "CASCADIA_HOSTING_WINDOW_CLASS", "pwsh", TerminalPid);
        Window(TerminalWpfDialog, "HwndWrapper[WindowsTerminal;;1]", "Settings", TerminalPid);
        Window(Notepad, "Notepad", "Untitled - Notepad", NotepadPid);
        _native.Setup(n => n.GetWindow(TerminalWpfDialog, GwOwner)).Returns(Terminal);
        _native.Setup(n => n.EnumWindows(It.IsAny<Func<nint, bool>>()))
            .Callback((Func<nint, bool> callback) =>
            {
                foreach (var hWnd in new[] { Terminal, TerminalWpfDialog, Notepad })
                {
                    callback(hWnd);
                }
            });
        _native.Setup(n => n.GetCurrentProcessId()).Returns(4242u);
        _native.Setup(n => n.GetProcessName(TerminalPid)).Returns("WindowsTerminal");
        _native.Setup(n => n.GetProcessName(NotepadPid)).Returns("notepad");
        _hostGuard.Setup(g => g.IsHostWindow(Terminal)).Returns(true);
    }

    private void Window(nint handle, string className, string title, uint processId)
    {
        _native.Setup(n => n.IsWindow(handle)).Returns(true);
        _native.Setup(n => n.IsWindowVisible(handle)).Returns(true);
        _native.Setup(n => n.GetClassName(handle)).Returns(className);
        _native.Setup(n => n.GetWindowText(handle)).Returns(title);
        _native.Setup(n => n.GetWindowThreadProcessId(handle)).Returns(processId);
        _native.Setup(n => n.GetAncestor(handle, It.IsAny<WindowAncestor>())).Returns(handle);
        _native.Setup(n => n.GetWindowRect(handle)).Returns((0, 0, 800, 600));
    }

    private WindowService CreateSut(IHostGuard? hostGuard = null) =>
        new(TestDataFactory.CreateMockMonitorService().Object, _native.Object, hostGuard ?? _hostGuard.Object);

    [Fact]
    public void Listing_MarksOnlyTheHostWindow()
    {
        // Act
        var windows = CreateSut().GetAllWindows().ToDictionary(w => w.Handle);

        // Assert
        windows[Terminal].IsAgentHost.Should().BeTrue();
        windows[Notepad].IsAgentHost.Should().BeFalse();
    }

    [Fact]
    public void SingleWindowReads_MarkTheHostWindow()
    {
        // Arrange
        _native.Setup(n => n.GetForegroundWindow()).Returns(Terminal);
        var sut = CreateSut();

        // Act & Assert
        sut.GetForegroundWindow()!.IsAgentHost.Should().BeTrue();
        sut.GetWindowInfo(Terminal)!.IsAgentHost.Should().BeTrue();
        sut.GetWindowInfo(Notepad)!.IsAgentHost.Should().BeFalse();
        sut.FindWindows(new WindowQuery { Handle = Terminal }).Single().IsAgentHost.Should().BeTrue();
    }

    [Fact]
    public void RealHostGuard_ListingCloseAndOwnerChainAgree_ForAWpfDialogOfTheHost()
    {
        // Arrange - a delegated terminal: recorded only as the window in front at start. Its WPF dialog is
        // owned through GW_OWNER only (GA_ROOTOWNER is the dialog itself)
        var processes = new Mock<INativeProcessWrapper>();
        processes.Setup(p => p.GetAncestorProcesses()).Returns([]);
        _native.Setup(n => n.GetForegroundWindow()).Returns(Terminal);
        var hostGuard = new HostGuard(_native.Object, processes.Object);
        var sut = CreateSut(hostGuard);

        // Act
        var dialog = sut.GetWindowInfo(TerminalWpfDialog)!;
        var close = sut.CloseWindow(TerminalWpfDialog, force: false);

        // Assert
        dialog.IsAgentHost.Should().BeTrue();
        close.ErrorCode.Should().Be(WindowErrorCode.ProtectedWindow);
        sut.GetWindowInfo(Notepad)!.IsAgentHost.Should().BeFalse();
    }

    [Fact]
    public void AWindowThatIsTheHostsOnlyWhileTheAncestorsAreUnknown_IsMarkedSo_AndAKnownHostWindowIsNot()
    {
        // Arrange - the server cannot read its ancestors: Notepad started before it, so it counts as the host's too
        _hostGuard.Setup(g => g.IsHostWindow(Notepad)).Returns(true);
        _hostGuard.Setup(g => g.IsHostWindowOnlyWhileAncestorsUnknown(Notepad)).Returns(true);
        var sut = CreateSut();

        // Act
        var notepad = sut.GetWindowInfo(Notepad)!;
        var terminal = sut.GetWindowInfo(Terminal)!;

        // Assert - refusals of Notepad then say why it counts, rather than call it the agent's terminal
        notepad.IsAgentHost.Should().BeTrue();
        notepad.IsAgentHostOnlyWhileAncestorsUnknown.Should().BeTrue();
        HostGuard.AncestorsUnknownNoteFor(notepad).Should().Be(HostGuard.AncestorsUnknownNote);
        terminal.IsAgentHost.Should().BeTrue();
        terminal.IsAgentHostOnlyWhileAncestorsUnknown.Should().BeFalse();
        HostGuard.AncestorsUnknownNoteFor(terminal).Should().BeEmpty();
    }
}
