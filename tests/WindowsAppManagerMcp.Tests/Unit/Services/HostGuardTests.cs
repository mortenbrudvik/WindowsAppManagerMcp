namespace WindowsAppManagerMcp.Tests.Unit.Services;

/// <summary>
/// Unit tests for <see cref="HostGuard"/>: what it records at server start and how it recognizes
/// the agent's host processes and windows. The window and process wrappers are mocks; nothing is
/// read from or done to the real desktop.
/// </summary>
/// <remarks>
/// The default scenario mirrors a real chain: server &lt;- claude &lt;- pwsh &lt;- WindowsTerminal &lt;- explorer,
/// with the server's console shown in the terminal window.
/// </remarks>
public class HostGuardTests
{
    // Not multiples of 4, so none of them can be the test host's own PID (ServerPid), which Windows
    // always makes a multiple of 4
    private const int AgentPid = 4001;     // claude.exe
    private const int ShellPid = 3001;     // pwsh.exe
    private const int TerminalPid = 2001;  // WindowsTerminal.exe
    private const int ExplorerPid = 1001;  // explorer.exe, the desktop shell
    private const int ConPtyPid = 2501;    // OpenConsole.exe, owns the hidden console pseudo-window
    private const int OtherPid = 9001;     // an unrelated application

    private const uint GwOwner = 4;

    private static readonly int ServerPid = Environment.ProcessId;
    private static readonly DateTime T0 = new(2026, 10, 1, 8, 0, 0, DateTimeKind.Utc);

    private static readonly nint TerminalWindow = 0x100;
    private static readonly nint TerminalDialog = 0x110;       // owned by TerminalWindow
    private static readonly nint SecondTerminalWindow = 0x120; // another window of the terminal process
    private static readonly nint ConsoleWindow = 0x200;        // pseudo-window owned by TerminalWindow
    private static readonly nint ShellDesktopWindow = 0x300;   // Progman
    private static readonly nint ExplorerFolder = 0x310;
    private static readonly nint OtherWindow = 0x400;
    private static readonly nint OtherDialog = 0x410;          // owned by OtherWindow
    private static readonly nint TerminalWpfDialog = 0x130;    // owned by TerminalWindow without WS_POPUP (GW_OWNER only)

    private readonly Mock<INativeWindowWrapper> _windows = new();
    private readonly Mock<INativeProcessWrapper> _processes = new();
    private readonly Dictionary<int, DateTime?> _startTimes = new()
    {
        [AgentPid] = T0.AddMinutes(3),
        [ShellPid] = T0.AddMinutes(2),
        [TerminalPid] = T0.AddMinutes(1),
        [ExplorerPid] = T0,
        [ConPtyPid] = T0.AddMinutes(2),
        [OtherPid] = T0.AddMinutes(4),
    };

    public HostGuardTests()
    {
        _processes.Setup(p => p.GetAncestorProcesses()).Returns(() =>
            new[] { AgentPid, ShellPid, TerminalPid, ExplorerPid }.Select(pid => (pid, _startTimes[pid])).ToList());
        _processes.Setup(p => p.GetProcessStartTime(It.IsAny<int>()))
            .Returns<int>(pid => _startTimes.TryGetValue(pid, out var start) ? start : null);
        _processes.Setup(p => p.GetConsoleWindow()).Returns(ConsoleWindow);

        Window(TerminalWindow, TerminalPid);
        Window(TerminalDialog, TerminalPid, rootOwner: TerminalWindow);
        Window(SecondTerminalWindow, TerminalPid);
        Window(ConsoleWindow, ConPtyPid, rootOwner: TerminalWindow);
        Window(ShellDesktopWindow, ExplorerPid);
        Window(ExplorerFolder, ExplorerPid);
        Window(OtherWindow, OtherPid);
        Window(OtherDialog, OtherPid, rootOwner: OtherWindow);
        Window(TerminalWpfDialog, TerminalPid);
        _windows.Setup(w => w.GetWindow(TerminalWpfDialog, GwOwner)).Returns(TerminalWindow);

        _windows.Setup(w => w.GetShellWindow()).Returns(ShellDesktopWindow);
        _windows.Setup(w => w.GetForegroundWindow()).Returns(TerminalWindow);
    }

    private void Window(nint handle, int processId, nint? rootOwner = null)
    {
        _windows.Setup(w => w.GetWindowThreadProcessId(handle)).Returns((uint)processId);
        _windows.Setup(w => w.GetAncestor(handle, WindowAncestor.RootOwner)).Returns(rootOwner ?? handle);
    }

    private HostGuard CreateSut() => new(_windows.Object, _processes.Object);

    /// <summary>
    /// Windows Terminal as the default terminal: the console was started elsewhere and handed over,
    /// so the terminal is not an ancestor, and the server has no console window (CREATE_NO_WINDOW).
    /// </summary>
    private void UseDelegatedTerminalWithoutConsole()
    {
        _processes.Setup(p => p.GetAncestorProcesses()).Returns(() =>
            new[] { AgentPid, ShellPid, ExplorerPid }.Select(pid => (pid, _startTimes[pid])).ToList());
        _processes.Setup(p => p.GetConsoleWindow()).Returns(nint.Zero);
    }

    #region What is recorded at start

    [Fact]
    public void Host_RecordsAncestorsConsoleHostWindowAndStartupForeground()
    {
        // Act
        var host = CreateSut().Host;

        // Assert
        host.ServerProcessId.Should().Be(ServerPid);
        host.Ancestors.Should().Equal(
            new HostProcess(AgentPid, T0.AddMinutes(3)),
            new HostProcess(ShellPid, T0.AddMinutes(2)),
            new HostProcess(TerminalPid, T0.AddMinutes(1)),
            new HostProcess(ExplorerPid, T0));
        host.ConsoleWindow.Should().Be(ConsoleWindow);
        host.ConsoleHostWindow.Should().Be(new HostWindow(TerminalWindow, new HostProcess(TerminalPid, T0.AddMinutes(1))));
        host.StartupForegroundWindow.Should().Be(new HostWindow(TerminalWindow, new HostProcess(TerminalPid, T0.AddMinutes(1))));
        host.ShellProcessId.Should().Be(ExplorerPid);
    }

    [Fact]
    public void Host_StartupForegroundIsAnOwnedDialog_RecordsItsRootOwner()
    {
        // Arrange
        _windows.Setup(w => w.GetForegroundWindow()).Returns(TerminalDialog);

        // Act & Assert
        CreateSut().Host.StartupForegroundWindow!.Handle.Should().Be(TerminalWindow);
    }

    [Fact]
    public void Host_StartupForegroundIsAnOwnedOverlappedWindow_RecordsItsOwner()
    {
        // Arrange - a WPF or WinForms dialog: GA_ROOTOWNER is the dialog itself, GW_OWNER the terminal
        _windows.Setup(w => w.GetForegroundWindow()).Returns(TerminalWpfDialog);

        // Act & Assert
        CreateSut().Host.StartupForegroundWindow!.Handle.Should().Be(TerminalWindow);
    }

    [Fact]
    public void Host_NoConsoleWindow_HasNoConsoleHostWindow()
    {
        // Arrange
        _processes.Setup(p => p.GetConsoleWindow()).Returns(nint.Zero);

        // Act
        var host = CreateSut().Host;

        // Assert
        host.ConsoleWindow.Should().Be(nint.Zero);
        host.ConsoleHostWindow.Should().BeNull();
        host.ConsoleServerProcesses.Should().BeEmpty();
    }

    [Fact]
    public void Host_UnderAPseudoConsole_RecordsTheConsoleServerThatOwnsTheConsoleWindow()
    {
        // Act - Windows Terminal (ConPTY): the pseudo-window belongs to OpenConsole.exe, and the terminal owns it
        var host = CreateSut().Host;

        // Assert - the console host window is the terminal's, the console server is a sibling of the shell
        host.ConsoleHostWindow!.Process.ProcessId.Should().Be(TerminalPid);
        host.ConsoleServerProcesses.Should().Equal(new HostProcess(ConPtyPid, T0.AddMinutes(2)));
    }

    [Fact]
    public void Host_WhenTheConsoleWindowReportsItsClient_StillRecordsTheConsoleServersTheTerminalStarted()
    {
        // Arrange - Windows can report a console window as owned by its first client (the shell) instead of
        // the console server; the terminal that shows the console started the console server itself
        Window(ConsoleWindow, ShellPid, rootOwner: TerminalWindow);
        _processes.Setup(p => p.GetDescendantsOf(TerminalPid))
            .Returns([(ConPtyPid, "OpenConsole.exe"), (ShellPid, "pwsh.exe"), (OtherPid, "notepad.exe")]);

        // Act
        var sut = CreateSut();

        // Assert
        sut.Host.ConsoleServerProcesses.Select(p => p.ProcessId).Should().BeEquivalentTo([ShellPid, ConPtyPid]);
        sut.IsHostProcess(ConPtyPid).Should().BeTrue();
        sut.IsHostProcess(OtherPid).Should().BeFalse();
    }

    [Fact]
    public void Host_TheConsoleHostIsTheShell_DoesNotProtectEveryConsoleServerItStarted()
    {
        // Arrange - not a real terminal setup, but explorer.exe starts the console servers of every console the
        // user opens, and those are not the agent's
        Window(ConsoleWindow, ConPtyPid, rootOwner: ExplorerFolder);
        _processes.Setup(p => p.GetDescendantsOf(ExplorerPid)).Returns([(OtherPid, "conhost.exe")]);

        // Act & Assert
        CreateSut().IsHostProcess(OtherPid).Should().BeFalse();
    }

    [Theory]
    [InlineData(0x300)] // the desktop (Progman)
    [InlineData(0x310)] // a File Explorer window
    public void Host_StartupForegroundBelongsToTheShell_IsNotRecorded(long foreground)
    {
        // Arrange - the user clicked the desktop or a folder; that is not where the agent runs
        _windows.Setup(w => w.GetForegroundWindow()).Returns(new nint(foreground));

        // Act & Assert
        CreateSut().Host.StartupForegroundWindow.Should().BeNull();
    }

    [Fact]
    public void Host_NothingInTheForeground_HasNoStartupForegroundWindow()
    {
        // Arrange - for example while the session is locked
        _windows.Setup(w => w.GetForegroundWindow()).Returns(nint.Zero);

        // Act & Assert
        CreateSut().Host.StartupForegroundWindow.Should().BeNull();
    }

    [Fact]
    public void Host_NoShell_HasNoShellProcess()
    {
        // Arrange
        _windows.Setup(w => w.GetShellWindow()).Returns(nint.Zero);

        // Act & Assert
        CreateSut().Host.ShellProcessId.Should().BeNull();
    }

    [Fact]
    public void Host_IsRecordedOnce_LaterForegroundChangesDoNotMatter()
    {
        // Arrange
        var sut = CreateSut();
        _windows.Setup(w => w.GetForegroundWindow()).Returns(OtherWindow);

        // Act
        var host = sut.Host;

        // Assert
        host.StartupForegroundWindow!.Handle.Should().Be(TerminalWindow);
        sut.IsHostWindow(OtherWindow).Should().BeFalse();
        _windows.Verify(w => w.GetForegroundWindow(), Times.Once);
        _processes.Verify(p => p.GetAncestorProcesses(), Times.Once);
    }

    [Fact]
    public void Host_AReadFails_RecordsTheRest()
    {
        // Arrange
        _processes.Setup(p => p.GetAncestorProcesses()).Throws(new InvalidOperationException("snapshot failed"));

        // Act
        var sut = CreateSut();

        // Assert - the server still starts, and the console host is still protected
        sut.Host.Ancestors.Should().BeEmpty();
        sut.IsHostWindow(TerminalWindow).Should().BeTrue();
    }

    #endregion

    #region IsHostProcess

    [Fact]
    public void IsHostProcess_TheServerItself_IsTrue()
    {
        CreateSut().IsHostProcess(ServerPid).Should().BeTrue();
    }

    [Theory]
    [InlineData(AgentPid)]
    [InlineData(ShellPid)]
    [InlineData(TerminalPid)]
    [InlineData(ExplorerPid)]
    public void IsHostProcess_Ancestor_IsTrue(int processId)
    {
        CreateSut().IsHostProcess(processId).Should().BeTrue();
    }

    [Fact]
    public void IsHostProcess_AncestorIdReusedByANewerProcess_IsFalse()
    {
        // Arrange - the agent exited and an unrelated process got its PID
        var sut = CreateSut();
        _startTimes[AgentPid] = T0.AddHours(2);

        // Act & Assert
        sut.IsHostProcess(AgentPid).Should().BeFalse();
    }

    [Fact]
    public void IsHostProcess_AncestorStartTimeNoLongerReadable_StaysTrue()
    {
        // Arrange - fail safe: an unverifiable host process still counts as the host
        var sut = CreateSut();
        _startTimes[AgentPid] = null;

        // Act & Assert
        sut.IsHostProcess(AgentPid).Should().BeTrue();
    }

    [Fact]
    public void IsHostProcess_AncestorRecordedWithoutStartTime_StaysTrue()
    {
        // Arrange
        _startTimes[ShellPid] = null;
        var sut = CreateSut();
        _startTimes[ShellPid] = T0.AddHours(2);

        // Act & Assert
        sut.IsHostProcess(ShellPid).Should().BeTrue();
    }

    [Fact]
    public void IsHostProcess_OwnerOfTheConsoleHostWindow_IsTrueEvenWhenNotAnAncestor()
    {
        // Arrange - the terminal took over the console, so it is not in the parent chain
        _processes.Setup(p => p.GetAncestorProcesses()).Returns(() =>
            new[] { AgentPid, ShellPid, ExplorerPid }.Select(pid => (pid, _startTimes[pid])).ToList());

        // Act & Assert
        CreateSut().IsHostProcess(TerminalPid).Should().BeTrue();
    }

    [Fact]
    public void IsHostProcess_TheConsoleServerOfThePseudoConsole_IsTrueAlthoughNotAnAncestor()
    {
        // Arrange - OpenConsole.exe (or conhost.exe) serves the console the agent's shell runs in;
        // ending it ends that session, and it is neither an ancestor nor the terminal window's process

        // Act & Assert
        CreateSut().IsHostProcess(ConPtyPid).Should().BeTrue();
    }

    [Fact]
    public void IsHostProcess_ConsoleServerIdReusedByANewerProcess_IsFalse()
    {
        // Arrange
        var sut = CreateSut();
        _startTimes[ConPtyPid] = T0.AddHours(2);

        // Act & Assert
        sut.IsHostProcess(ConPtyPid).Should().BeFalse();
    }

    [Fact]
    public void IsHostProcess_DelegatedTerminalThatOwnedTheStartupForegroundWindow_IsTrue()
    {
        // Arrange - Windows Terminal as the default terminal and a server without a console window:
        // the terminal is neither an ancestor nor the console host, but killing it ends the session
        UseDelegatedTerminalWithoutConsole();

        // Act & Assert
        CreateSut().IsHostProcess(TerminalPid).Should().BeTrue();
    }

    [Fact]
    public void IsHostProcess_StartupForegroundProcessIdReusedByANewerProcess_IsFalse()
    {
        // Arrange - the terminal exited and an unrelated process got its PID
        UseDelegatedTerminalWithoutConsole();
        var sut = CreateSut();
        _startTimes[TerminalPid] = T0.AddHours(2);

        // Act & Assert
        sut.IsHostProcess(TerminalPid).Should().BeFalse();
    }

    [Fact]
    public void IsHostProcess_NothingRecordedInTheForeground_DoesNotAddAProcess()
    {
        // Arrange
        UseDelegatedTerminalWithoutConsole();
        _windows.Setup(w => w.GetForegroundWindow()).Returns(nint.Zero);

        // Act & Assert
        CreateSut().IsHostProcess(TerminalPid).Should().BeFalse();
    }

    [Theory]
    [InlineData(OtherPid)]
    [InlineData(0)]
    [InlineData(-1)]
    public void IsHostProcess_AnyOtherProcess_IsFalse(int processId)
    {
        CreateSut().IsHostProcess(processId).Should().BeFalse();
    }

    [Fact]
    public void TestProcessIds_CannotBeTheTestHostsOwn()
    {
        // ServerPid always counts as the host, so a fake PID equal to it would flip the tests above
        new[] { AgentPid, ShellPid, TerminalPid, ExplorerPid, ConPtyPid, OtherPid }.Should().NotContain(ServerPid);
    }

    #endregion

    #region IsHostWindow

    [Fact]
    public void IsHostWindow_TheConsoleHostWindow_IsTrue()
    {
        CreateSut().IsHostWindow(TerminalWindow).Should().BeTrue();
    }

    [Fact]
    public void IsHostWindow_DialogOwnedByAHostWindow_IsTrue()
    {
        CreateSut().IsHostWindow(TerminalDialog).Should().BeTrue();
    }

    [Fact]
    public void IsHostWindow_AnotherWindowOfAnAncestor_IsTrue()
    {
        // The terminal is an ancestor, so all of its windows are shells that reach the agent's host
        CreateSut().IsHostWindow(SecondTerminalWindow).Should().BeTrue();
    }

    [Theory]
    [InlineData(0x300)] // the desktop (Progman)
    [InlineData(0x310)] // a File Explorer window
    public void IsHostWindow_ShellWindows_AreNotHostWindowsAlthoughTheShellIsAnAncestor(long handle)
    {
        // Arrange
        var sut = CreateSut();

        // Act & Assert
        sut.IsHostProcess(ExplorerPid).Should().BeTrue("the shell is an ancestor");
        sut.IsHostWindow(new nint(handle)).Should().BeFalse("explorer.exe also owns the desktop, taskbar and folders");
    }

    [Fact]
    public void IsHostWindow_UnrelatedWindowsAndTheirDialogs_AreFalse()
    {
        // Arrange
        var sut = CreateSut();

        // Act & Assert
        sut.IsHostWindow(OtherWindow).Should().BeFalse();
        sut.IsHostWindow(OtherDialog).Should().BeFalse();
        sut.IsHostWindow(nint.Zero).Should().BeFalse();
    }

    [Fact]
    public void IsHostWindow_DelegatedTerminal_ProtectsTheStartupForegroundWindowAndItsDialogs()
    {
        // Arrange
        UseDelegatedTerminalWithoutConsole();
        var sut = CreateSut();

        // Act & Assert - although the process is a host process (killing it closes the recorded window)
        sut.IsHostWindow(TerminalWindow).Should().BeTrue();
        sut.IsHostWindow(TerminalDialog).Should().BeTrue();
        sut.IsHostWindow(SecondTerminalWindow).Should().BeFalse("only the recorded window is known to host the agent");
        sut.IsHostProcess(TerminalPid).Should().BeTrue();
    }

    [Fact]
    public void IsHostWindow_DelegatedTerminal_ProtectsItsOwnedOverlappedWindowsToo()
    {
        // Arrange - only the recorded window identifies the host; its WPF dialog is linked by GW_OWNER only
        UseDelegatedTerminalWithoutConsole();
        var sut = CreateSut();

        // Act & Assert
        sut.IsHostWindow(TerminalWpfDialog).Should().BeTrue("the dialog is owned by the recorded terminal window");
        sut.IsHostWindow(SecondTerminalWindow).Should().BeFalse();
    }

    [Fact]
    public void IsHostWindow_UnrelatedAppInFrontAtStartup_ProtectsOnlyThatWindow()
    {
        // Arrange - the server started while another application was in front
        _windows.Setup(w => w.GetForegroundWindow()).Returns(OtherWindow);
        var otherAppSecondWindow = new nint(0x420); // another top-level window of that application, unowned
        Window(otherAppSecondWindow, OtherPid);
        var sut = CreateSut();

        // Act & Assert - fail-safe for the process; input still reaches its other windows
        sut.IsHostWindow(OtherWindow).Should().BeTrue();
        sut.IsHostWindow(OtherDialog).Should().BeTrue("the dialog is owned by the recorded window");
        sut.IsHostProcess(OtherPid).Should().BeTrue();
        sut.IsHostWindow(otherAppSecondWindow).Should().BeFalse(
            "only the recorded window and the popups it owns are host windows, not every window of its process");
    }

    [Fact]
    public void IsHostWindow_RecordedHandleNowBelongsToAnotherProcess_IsFalse()
    {
        // Arrange - the terminal window closed and its handle value was reused
        UseDelegatedTerminalWithoutConsole();
        var sut = CreateSut();
        Window(TerminalWindow, OtherPid);

        // Act & Assert
        sut.IsHostWindow(TerminalWindow).Should().BeFalse();
    }

    [Fact]
    public void IsHostWindow_RootOwnerUnavailable_UsesTheWindowItself()
    {
        // Arrange - GetAncestor returns zero for a window that is being destroyed
        var sut = CreateSut();
        _windows.Setup(w => w.GetAncestor(TerminalDialog, WindowAncestor.RootOwner)).Returns(nint.Zero);

        // Act & Assert - still a window of a host process
        sut.IsHostWindow(TerminalDialog).Should().BeTrue();
    }

    [Fact]
    public void IsHostWindow_NeverChangesAWindow()
    {
        // Arrange
        var windows = new Mock<INativeWindowWrapper>(MockBehavior.Strict);
        windows.Setup(w => w.GetShellWindow()).Returns(ShellDesktopWindow);
        windows.Setup(w => w.GetForegroundWindow()).Returns(TerminalWindow);
        windows.Setup(w => w.GetAncestor(It.IsAny<nint>(), It.IsAny<WindowAncestor>())).Returns<nint, WindowAncestor>((h, _) => h);
        windows.Setup(w => w.GetWindow(It.IsAny<nint>(), GwOwner)).Returns(nint.Zero);
        windows.Setup(w => w.GetWindowThreadProcessId(It.IsAny<nint>()))
            .Returns<nint>(h => (uint)(h == ShellDesktopWindow ? ExplorerPid : TerminalPid));
        var sut = new HostGuard(windows.Object, _processes.Object);

        // Act & Assert - a strict mock throws on anything but the queries set up above
        sut.IsHostWindow(SecondTerminalWindow).Should().BeTrue();
        sut.IsHostWindow(ShellDesktopWindow).Should().BeFalse();
    }

    #endregion
}
