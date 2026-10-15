namespace WindowsAppManagerMcp.Tests.Unit.Native;

/// <summary>
/// NativeProcessWrapper.GetProcessCommandLine (roadmap M5.3), on this test process only: it only reads.
/// </summary>
public class ProcessCommandLineTests
{
    [Fact]
    public void GetProcessCommandLine_ReadsThisProcesssCommandLine()
    {
        var commandLine = new NativeProcessWrapper().GetProcessCommandLine(Environment.ProcessId);

        // The line as it was started, which .NET's Environment.CommandLine rebuilds differently for a host like this one
        commandLine.Should().NotBeNullOrEmpty();
        ChromiumCommandLine.Split(commandLine)[0].Should().BeEquivalentTo(Environment.ProcessPath);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void GetProcessCommandLine_OfNoProcess_IsNull(int processId)
    {
        new NativeProcessWrapper().GetProcessCommandLine(processId).Should().BeNull();
    }

    [Fact]
    public void GetProcessCommandLine_OfAProcessThatDoesNotRun_IsNull()
    {
        // Process ids are multiples of 4, so this one is never a process's
        new NativeProcessWrapper().GetProcessCommandLine(int.MaxValue - 2).Should().BeNull();
    }
}
