using System.Diagnostics;

namespace WindowsAppManagerMcp.Tests.Unit.Native;

/// <summary>
/// Unit tests for the bounded wait behind NativeProcessWrapper.FindWithWhereExe.
/// ReadFirstLineWithin only sees delegates and a task, so no process is started or killed here.
/// </summary>
public class WhereExeTimeoutTests
{
    private const int TimeoutMs = 5000;
    private static readonly TimeSpan HangBound = TimeSpan.FromSeconds(3);

    private int _killCount;

    private void Kill() => _killCount++;

    [Fact]
    public void ReadFirstLineWithin_ProcessExitsWithOutput_ReturnsFirstLineWithoutKilling()
    {
        // Arrange - where.exe prints every match, one per line
        var output = Task.FromResult("C:\\Apps\\Chrome\\chrome.exe\r\nC:\\Other\\chrome.exe\r\n");

        // Act
        var result = NativeProcessWrapper.ReadFirstLineWithin(output, _ => true, Kill, TimeoutMs);

        // Assert
        result.Should().Be("C:\\Apps\\Chrome\\chrome.exe");
        _killCount.Should().Be(0);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   \r\n")]
    public void ReadFirstLineWithin_BlankOutput_ReturnsNull(string text)
    {
        // Act
        var result = NativeProcessWrapper.ReadFirstLineWithin(Task.FromResult(text), _ => true, Kill, TimeoutMs);

        // Assert
        result.Should().BeNull();
    }

    [Fact]
    public void ReadFirstLineWithin_ProcessDoesNotExit_KillsItAndReturnsNull()
    {
        // Arrange - where.exe hangs without printing anything, so the read never completes
        var neverCompletes = new TaskCompletionSource<string>().Task;
        var waitedMs = new List<int>();

        // Act
        var result = NativeProcessWrapper.ReadFirstLineWithin(
            neverCompletes,
            ms => { waitedMs.Add(ms); return false; },
            Kill,
            TimeoutMs);

        // Assert
        result.Should().BeNull();
        _killCount.Should().Be(1);
        waitedMs.Should().Equal(TimeoutMs);
    }

    [Fact]
    public void ReadFirstLineWithin_ProcessPrintsButDoesNotExit_KillsItAndReturnsNull()
    {
        // Arrange - a line is already printed, but where.exe is still running when the time is up
        var output = Task.FromResult("C:\\Apps\\Chrome\\chrome.exe\r\n");

        // Act
        var result = NativeProcessWrapper.ReadFirstLineWithin(output, _ => false, Kill, TimeoutMs);

        // Assert
        result.Should().BeNull();
        _killCount.Should().Be(1);
    }

    [Fact]
    public void ReadFirstLineWithin_OutputNeverCompletes_ReturnsNullWithinTheTimeLimit()
    {
        // Arrange - the process exited, but its output pipe stays open
        var neverCompletes = new TaskCompletionSource<string>().Task;
        var stopwatch = Stopwatch.StartNew();

        // Act
        var result = NativeProcessWrapper.ReadFirstLineWithin(neverCompletes, _ => true, Kill, timeoutMs: 50);

        // Assert
        stopwatch.Elapsed.Should().BeLessThan(HangBound);
        result.Should().BeNull();
        _killCount.Should().Be(1);
    }

    [Fact]
    public void ReadFirstLineWithin_ReadFails_ReturnsNull()
    {
        // Arrange
        var failed = Task.FromException<string>(new IOException("pipe broken"));

        // Act
        var result = NativeProcessWrapper.ReadFirstLineWithin(failed, _ => true, Kill, TimeoutMs);

        // Assert
        result.Should().BeNull();
    }
}
