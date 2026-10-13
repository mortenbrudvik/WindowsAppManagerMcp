using Moq;

namespace WindowsAppManagerMcp.E2ETests.HarnessTests;

/// <summary>
/// The session's checks of the agent's host, without a desktop: a session fails when its server counts the fixture as the
/// host, and a scenario that needs the host in front fails when another window is. Nothing here starts the fixture.
/// </summary>
public class E2ESessionTests
{
    private const int FixturePid = FakeDesktop.FixturePid;
    private static readonly nint FixtureWindow = FakeDesktop.FixtureWindow;
    private static readonly nint Terminal = 0x3000;
    private const int TerminalPid = 5150;

    private static readonly HostSnapshot Recorded = new(
        ServerProcessId: 100,
        Ancestors: [new HostProcess(90, null)],
        ConsoleWindow: 0,
        ConsoleHostWindow: null,
        StartupForegroundWindow: new HostWindow(Terminal, new HostProcess(TerminalPid, null)),
        ShellProcessId: null);

    private readonly Mock<IHostGuard> _host = new();

    public E2ESessionTests()
    {
        _host.SetupGet(h => h.Host).Returns(Recorded);
        _host.Setup(h => h.IsHostWindow(Terminal)).Returns(true);
        _host.Setup(h => h.IsHostProcess(TerminalPid)).Returns(true);
    }

    private static WindowInfo MainWindow(bool isAgentHost) =>
        new(FixtureWindow, "WAM Fixture Main abc", FixtureContract.ProcessName, FixturePid, new WindowRect(0, 0, 800, 600),
            WindowState.Normal, IsVisible: true, MonitorIndex: 0)
        {
            IsAgentHost = isAgentHost
        };

    [Fact]
    public void FixtureIsHostError_WhenTheHostWasRecordedBeforeTheFixture_IsNull()
    {
        E2ESession.FixtureIsHostError(MainWindow(isAgentHost: false), FixturePid, _host.Object).Should().BeNull();
    }

    [Fact]
    public void FixtureIsHostError_WhenFindWindowsCallsTheFixtureTheHost_FailsTheSession_AndSaysToKeepTheTerminalInFront()
    {
        // The gate's failure: the fixture took the foreground before the server recorded its host
        var error = E2ESession.FixtureIsHostError(MainWindow(isAgentHost: true), FixturePid, _host.Object);

        error.Should().Contain("as the agent's own host").And.Contain(E2ESession.KeepTheTerminalInFront)
            .And.Contain($"0x{Terminal:X} (process {TerminalPid})", "the message names what was recorded");
    }

    [Fact]
    public void FixtureIsHostError_WhenTheGuardCountsTheFixturesWindowOrProcess_FailsTheSession()
    {
        _host.Setup(h => h.IsHostWindow(FixtureWindow)).Returns(true);
        E2ESession.FixtureIsHostError(MainWindow(isAgentHost: false), FixturePid, _host.Object).Should().NotBeNull();

        _host.Setup(h => h.IsHostWindow(FixtureWindow)).Returns(false);
        _host.Setup(h => h.IsHostProcess(FixturePid)).Returns(true);
        E2ESession.FixtureIsHostError(MainWindow(isAgentHost: false), FixturePid, _host.Object).Should().NotBeNull();
    }

    [Fact]
    public void AgentHostNotInFrontError_WithTheTerminalInFront_IsNull()
    {
        E2ESession.AgentHostNotInFrontError(Terminal, TerminalPid, FixturePid, _host.Object).Should().BeNull();
    }

    [Fact]
    public void AgentHostNotInFrontError_WithAFixtureWindowInFront_Fails_EvenWhenTheGuardCountsItAsTheHost()
    {
        // A fixture recorded as the host would make the scenario pass for the wrong reason
        _host.Setup(h => h.IsHostWindow(FixtureWindow)).Returns(true);

        var error = E2ESession.AgentHostNotInFrontError(FixtureWindow, FixturePid, FixturePid, _host.Object);

        error.Should().Contain("a fixture window is").And.Contain(E2ESession.KeepTheTerminalInFront);
    }

    [Fact]
    public void AgentHostNotInFrontError_WithAnotherWindowOrNoneInFront_Fails()
    {
        E2ESession.AgentHostNotInFrontError(FakeDesktop.OtherWindow, FakeDesktop.OtherPid, FixturePid, _host.Object)
            .Should().Contain("another window is");
        E2ESession.AgentHostNotInFrontError(0, 0, FixturePid, _host.Object).Should().Contain("no window is in front");
    }

    [Fact]
    public void DescribeHost_NamesTheWindowThatWasInFront_TheConsoleWindow_AndTheAncestors()
    {
        E2ESession.DescribeHost(Recorded).Should().Be($"window in front 0x{Terminal:X} (process {TerminalPid}), console window none, ancestors 90");
    }
}
