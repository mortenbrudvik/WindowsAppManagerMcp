namespace WindowsAppManagerMcp.Tests.Unit.Services;

/// <summary>
/// Finding the DevTools port of a browser window's process (roadmap M5.3): from its command line, else its profile's
/// DevToolsActivePort, and only when the port's listener is that process. Nothing is scanned or connected; the process
/// and the listener table are mocks.
/// </summary>
public class DebugPortLocatorTests
{
    private const int BrowserPid = 4242;

    private readonly Mock<INativeProcessWrapper> _processes = new(MockBehavior.Strict);
    private readonly Mock<ITcpPortOwnerResolver> _owners = new(MockBehavior.Strict);
    private readonly Dictionary<string, int?> _portFiles = new(StringComparer.OrdinalIgnoreCase);

    private DebugPortLocator CreateSut() =>
        new(_processes.Object, _owners.Object, dir => _portFiles.TryGetValue(dir, out var port) ? port : null);

    private void CommandLine(string? commandLine) =>
        _processes.Setup(p => p.GetProcessCommandLine(BrowserPid)).Returns(commandLine);

    private void Listener(int port, int? owner) =>
        _owners.Setup(o => o.GetListeningProcessId(port)).Returns(owner);

    [Fact]
    public void Find_TakesThePortFromTheCommandLine_WhenTheWindowsProcessListensOnIt()
    {
        CommandLine(@"""C:\Edge\msedge.exe"" --remote-debugging-port=9333 --user-data-dir=C:\Temp\P about:blank");
        Listener(9333, BrowserPid);

        var lookup = CreateSut().Find(BrowserPid);

        lookup.Status.Should().Be(DebugPortLookupStatus.Found);
        lookup.Port.Should().Be(9333);
    }

    [Fact]
    public void Find_ReadsDevToolsActivePort_ForPortZero()
    {
        CommandLine(@"""C:\Edge\msedge.exe"" --remote-debugging-port=0 ""--user-data-dir=C:\Temp\WamM5Check-1""");
        _portFiles[@"C:\Temp\WamM5Check-1"] = 51874;
        Listener(51874, BrowserPid);

        var lookup = CreateSut().Find(BrowserPid);

        lookup.Port.Should().Be(51874);
        lookup.Status.Should().Be(DebugPortLookupStatus.Found);
    }

    [Fact]
    public void Find_ReportsNoDebugPort_ForABrowserStartedWithoutOne_AndReadsNothingElse()
    {
        // The user's own browser: UI Automation exactly as before, and nothing to explain
        CommandLine(@"""C:\Program Files\Google\Chrome\Application\chrome.exe"" --flag-switches-begin --flag-switches-end");

        var lookup = CreateSut().Find(BrowserPid);

        lookup.Status.Should().Be(DebugPortLookupStatus.NoDebugPort);
        lookup.Port.Should().BeNull();
        lookup.WorthReporting.Should().BeFalse();
        _owners.VerifyNoOtherCalls();
    }

    [Fact]
    public void Find_ReportsAnUnreadableCommandLine_WithoutExplainingAFallback()
    {
        CommandLine(null);

        var lookup = CreateSut().Find(BrowserPid);

        lookup.Status.Should().Be(DebugPortLookupStatus.CommandLineUnreadable);
        lookup.Port.Should().BeNull();
        lookup.WorthReporting.Should().BeFalse();
        lookup.Detail.Should().Contain(BrowserPid.ToString());
        _owners.VerifyNoOtherCalls();
    }

    [Theory]
    [InlineData("chrome.exe --remote-debugging-port=abc")]
    [InlineData("chrome.exe --remote-debugging-port=70000")]
    [InlineData("chrome.exe --remote-debugging-port=-1")]
    [InlineData("chrome.exe --remote-debugging-port=")]
    public void Find_DoesNotUseASwitchThatIsNotAPort(string commandLine)
    {
        CommandLine(commandLine);

        var lookup = CreateSut().Find(BrowserPid);

        lookup.Status.Should().Be(DebugPortLookupStatus.PortNotKnown);
        lookup.Port.Should().BeNull();
        lookup.WorthReporting.Should().BeTrue();
        _owners.VerifyNoOtherCalls();
    }

    [Theory]
    [InlineData("chrome.exe --remote-debugging-port=0", "--user-data-dir")]
    [InlineData("chrome.exe --remote-debugging-port=0 --user-data-dir=Profile", "full path")]
    [InlineData(@"chrome.exe --remote-debugging-port=0 --user-data-dir=C:\Temp\NoFile", "DevToolsActivePort")]
    public void Find_ReportsPortZero_WithoutAPortFileToReadItFrom(string commandLine, string reason)
    {
        CommandLine(commandLine);

        var lookup = CreateSut().Find(BrowserPid);

        lookup.Status.Should().Be(DebugPortLookupStatus.PortNotKnown);
        lookup.Port.Should().BeNull();
        lookup.Detail.Should().Contain(reason);
        _owners.VerifyNoOtherCalls();
    }

    [Theory]
    [InlineData(@"\\server\share\Profile")]
    [InlineData(@"\\?\UNC\server\share\Profile")]
    [InlineData(@"\\?\C:\Temp\Profile")]
    [InlineData(@"\\.\pipe\Profile")]
    [InlineData(@"//server/share/Profile")]
    public void Find_DoesNotReadAPortFileOutsideALocalDrive_ForPortZero(string profile)
    {
        // The path comes from the process's own command line: a share, a pipe or a device could block the call or reach
        // another host, so only a drive-rooted local folder is read
        var reads = new List<string>();
        CommandLine($@"chrome.exe --remote-debugging-port=0 ""--user-data-dir={profile}""");
        var sut = new DebugPortLocator(_processes.Object, _owners.Object, dir => { reads.Add(dir); return 51874; });

        var lookup = sut.Find(BrowserPid);

        lookup.Status.Should().Be(DebugPortLookupStatus.PortNotKnown);
        lookup.Port.Should().BeNull();
        lookup.Detail.Should().Contain("local");
        reads.Should().BeEmpty();
        _owners.VerifyNoOtherCalls();
    }

    [Fact]
    public void DevToolsActivePortFile_ReadsThePort_OfAFileLargerThanItReads()
    {
        // The read is bounded: the port is on the first line, whatever follows it
        var dir = Path.Combine(Path.GetTempPath(), "WamPortFile-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllText(DevToolsActivePortFile.PathIn(dir), "51874\n/devtools/browser/abc\n" + new string('x', 1_000_000));

            DevToolsActivePortFile.TryReadPort(dir).Should().Be(51874);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Find_DoesNotUseAPortAnotherProcessListensOn()
    {
        // For example a port file a crashed run left, whose port another program has since taken
        CommandLine(@"chrome.exe --remote-debugging-port=9222");
        Listener(9222, 999);

        var lookup = CreateSut().Find(BrowserPid);

        lookup.Status.Should().Be(DebugPortLookupStatus.HeldByOtherProcess);
        lookup.Port.Should().BeNull();
        lookup.Detail.Should().Contain("999").And.Contain(BrowserPid.ToString());
        lookup.WorthReporting.Should().BeTrue();
    }

    [Fact]
    public void Find_FailsClosed_WhenNoListenerOfTheProcessIsFound()
    {
        // Nothing listens (Chrome 136+ ignores the switch for its default profile), or the table could not be read
        CommandLine(@"chrome.exe --remote-debugging-port=9222");
        Listener(9222, null);

        var lookup = CreateSut().Find(BrowserPid);

        lookup.Status.Should().Be(DebugPortLookupStatus.NotListening);
        lookup.Port.Should().BeNull();
        lookup.Detail.Should().Contain("9222");
        lookup.WorthReporting.Should().BeTrue();
    }

    [Fact]
    public void Find_IgnoresAPipeDebuggingSwitch()
    {
        // --remote-debugging-pipe opens no TCP port
        CommandLine(@"chrome.exe --remote-debugging-pipe");

        CreateSut().Find(BrowserPid).Status.Should().Be(DebugPortLookupStatus.NoDebugPort);
    }

    [Fact]
    public void Find_NeverLooksBeyondTheWindowsOwnProcess()
    {
        // A WebView2 or Electron host's DevTools port belongs to a child process: it is not the window's, so not used
        CommandLine(@"""C:\Apps\Host.exe""");

        CreateSut().Find(BrowserPid).Status.Should().Be(DebugPortLookupStatus.NoDebugPort);

        _processes.Verify(p => p.GetDescendantsOf(It.IsAny<int>()), Times.Never);
        _owners.VerifyNoOtherCalls();
    }
}
