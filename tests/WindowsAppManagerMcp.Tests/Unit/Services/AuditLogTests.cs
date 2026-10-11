using System.Text.Json;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Protocol;

namespace WindowsAppManagerMcp.Tests.Unit.Services;

/// <summary>
/// The audit log's writer (roadmap M2.17): one JSON line per call, appended to the local day's file in a temporary
/// folder, files older than the retention deleted, the target windows read before the call, and no failure ever thrown
/// to the tool call. Windows are a mock; the clock is a test clock in UTC, so the day does not depend on the machine.
/// </summary>
public sealed class AuditLogTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "WamAuditLogTests", Guid.NewGuid().ToString("N"), "audit");
    private readonly Mock<INativeWindowWrapper> _windows = new();
    private readonly UtcClock _clock = new(new DateTimeOffset(2026, 10, 10, 12, 0, 0, TimeSpan.Zero));
    private readonly RecordingLogger<AuditLog> _logger = new();

    private AuditLog CreateLog(TimeSpan? retention = null) =>
        new(new AuditLogOptions(_folder, retention ?? AuditLogOptions.DefaultRetention), _windows.Object, _clock, _logger);

    private string DayFile(string day) => Path.Combine(_folder, day + ".jsonl");

    private static Dictionary<string, JsonElement> Arguments(object arguments) =>
        JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(JsonSerializer.Serialize(arguments))!;

    private static CallToolResult Ok() => new() { Content = [new TextContentBlock { Text = """{"success":true}""" }] };

    private static void Record(AuditLog log, string tool, object? arguments = null, CallToolResult? result = null)
    {
        var call = log.Begin(tool, arguments == null ? null : Arguments(arguments), "1");
        log.End(call, result ?? Ok(), AuditOutcome.Ok);
    }

    private string[] Lines(string day) => File.ReadAllLines(DayFile(day));

    public void Dispose()
    {
        var root = Path.GetDirectoryName(_folder)!;
        if (Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Options_DefaultToTheAuditFolderUnderLocalAppData_OrTheOverride()
    {
        AuditLogOptions.FolderEnvironmentVariable.Should().Be("WAM_AUDIT_PATH");
        AuditLogOptions.DefaultRetention.Should().Be(TimeSpan.FromDays(7));
        AuditLogOptions.ResolveFolder(null, @"C:\Users\someone\AppData\Local")
            .Should().Be(@"C:\Users\someone\AppData\Local\WindowsAppManagerMcp\audit");
        AuditLogOptions.ResolveFolder("  ", @"C:\Local").Should().Be(@"C:\Local\WindowsAppManagerMcp\audit");
        AuditLogOptions.ResolveFolder("\"D:\\My Audit\"", @"C:\Local").Should().Be(@"D:\My Audit");
        AuditLogOptions.ResolveFolder(@"%SystemRoot%\audit", @"C:\Local")
            .Should().Be(Path.Combine(Environment.GetEnvironmentVariable("SystemRoot")!, "audit"));
    }

    [Fact]
    public void CreatingTheLog_TouchesNoFile()
    {
        var log = CreateLog();

        log.Folder.Should().Be(_folder);
        log.LastError.Should().BeNull();
        Directory.Exists(_folder).Should().BeFalse("the folder is created when the first line is written");
    }

    [Fact]
    public void End_AppendsOneJsonLinePerCall_ToTheDaysFile()
    {
        // Arrange
        var log = CreateLog();

        // Act
        var call = log.Begin("click", Arguments(new { x = 1, y = 2 }), "5");
        _clock.Advance(TimeSpan.FromMilliseconds(250));
        log.End(call, Ok(), AuditOutcome.Ok);
        Record(log, "close_window", new { handle = 9 });

        // Assert
        var lines = Lines("2026-10-10");
        lines.Should().HaveCount(2);
        using var first = JsonDocument.Parse(lines[0]);
        first.RootElement.GetProperty("tool").GetString().Should().Be("click");
        first.RootElement.GetProperty("ts").GetString().Should().Be("2026-10-10T12:00:00.000+00:00");
        first.RootElement.GetProperty("durationMs").GetInt64().Should().Be(250);
        first.RootElement.GetProperty("requestId").GetString().Should().Be("5");
        first.RootElement.GetProperty("serverPid").GetInt32().Should().Be(Environment.ProcessId);
        JsonDocument.Parse(lines[1]).RootElement.GetProperty("tool").GetString().Should().Be("close_window");
        File.ReadAllBytes(DayFile("2026-10-10"))[0].Should().Be((byte)'{', "the file has no byte order mark");
    }

    [Fact]
    public void ANewDay_StartsANewFile()
    {
        var log = CreateLog();

        Record(log, "click");
        _clock.Advance(TimeSpan.FromHours(12));
        Record(log, "click");

        Lines("2026-10-10").Should().HaveCount(1);
        Lines("2026-10-11").Should().HaveCount(1);
    }

    [Fact]
    public void TheFirstLineOfADay_DeletesTheDaysFilesOlderThanTheRetention_AndNothingElse()
    {
        // Arrange - seven days are kept: on 2026-10-10 the files of 10-03 and before go
        Directory.CreateDirectory(_folder);
        string[] old = ["2026-09-01", "2026-10-02", "2026-10-03"];
        string[] kept = ["2026-10-04", "2026-10-09"];
        foreach (var day in old.Concat(kept))
        {
            File.WriteAllText(DayFile(day), "{}\n");
        }

        string[] notOurs = ["notes.txt", "2026-01-01.json", "audit-2026-01-01.jsonl", "2026-13-45.jsonl", "2026-1-1.jsonl"];
        foreach (var name in notOurs)
        {
            File.WriteAllText(Path.Combine(_folder, name), "keep");
        }

        var log = CreateLog();

        // Act
        Record(log, "click");

        // Assert
        old.Should().AllSatisfy(day => File.Exists(DayFile(day)).Should().BeFalse($"{day} is older than seven days"));
        kept.Should().AllSatisfy(day => File.Exists(DayFile(day)).Should().BeTrue($"{day} is within seven days"));
        notOurs.Should().AllSatisfy(name => File.Exists(Path.Combine(_folder, name)).Should().BeTrue($"{name} is not a day's file"));

        // A day later, the next line deletes the next day's file
        _clock.Advance(TimeSpan.FromDays(1));
        Record(log, "click");
        File.Exists(DayFile("2026-10-04")).Should().BeFalse();
        File.Exists(DayFile("2026-10-09")).Should().BeTrue();
    }

    [Fact]
    public void Begin_ReadsTheTargetWindowsAndProcesses_BeforeTheCallChangesThem()
    {
        // Arrange - window 100 is Notepad's until the close
        _windows.Setup(w => w.IsWindow(100)).Returns(true);
        _windows.Setup(w => w.GetWindowText(100)).Returns("Untitled - Notepad");
        _windows.Setup(w => w.GetWindowThreadProcessId(100)).Returns(42u);
        _windows.Setup(w => w.GetProcessName(42u)).Returns("notepad");
        _windows.Setup(w => w.GetProcessName(77u)).Returns("calc");
        var log = CreateLog();

        // Act
        var close = log.Begin("close_window", Arguments(new { handle = 100 }), "1");
        var kill = log.Begin("kill_process", Arguments(new { processId = 77 }), "2");
        _windows.Setup(w => w.IsWindow(100)).Returns(false);
        _windows.Setup(w => w.GetProcessName(It.IsAny<uint>())).Returns((string?)null);
        log.End(close, Ok(), AuditOutcome.Ok);
        log.End(kill, Ok(), AuditOutcome.Ok);

        // Assert
        var lines = Lines("2026-10-10").Select(l => JsonDocument.Parse(l).RootElement).ToList();
        var target = lines[0].GetProperty("target");
        target.GetProperty("handle").GetInt64().Should().Be(100);
        target.GetProperty("title").GetString().Should().Be("Untitled - Notepad");
        target.GetProperty("process").GetString().Should().Be("notepad");
        target.GetProperty("processId").GetInt32().Should().Be(42);
        lines[1].GetProperty("target").GetProperty("process").GetString().Should().Be("calc");
    }

    [Fact]
    public void Begin_ReadsNothing_ForAHandleThatIsNoWindow()
    {
        var log = CreateLog();

        var call = log.Begin("close_window", Arguments(new { handle = 100 }), "1");

        call.Windows[100].Should().Be(new AuditTarget(Handle: 100));
        _windows.Verify(w => w.GetWindowText(It.IsAny<nint>()), Times.Never);
    }

    [Fact]
    public void Begin_AndEnd_NeverThrow()
    {
        // Arrange - every read fails
        _windows.Setup(w => w.IsWindow(It.IsAny<nint>())).Throws(new InvalidOperationException("boom"));
        _windows.Setup(w => w.GetProcessName(It.IsAny<uint>())).Throws(new InvalidOperationException("boom"));
        var log = CreateLog();

        // Act
        var act = () =>
        {
            var call = log.Begin("close_window", Arguments(new { handle = 100, processId = 5 }), null);
            log.End(call, new CallToolResult { Content = [new TextContentBlock { Text = "not json" }] }, AuditOutcome.Ok);
        };

        // Assert - the line is still written, with what is known
        act.Should().NotThrow();
        var line = JsonDocument.Parse(Lines("2026-10-10").Single()).RootElement;
        line.GetProperty("target").GetProperty("handle").GetInt64().Should().Be(100);
    }

    [Fact]
    public void AFolderThatCannotBeWritten_NeverBreaksTheCall_AndIsReportedOnce_UntilAWriteWorksAgain()
    {
        // Arrange - a file stands where the folder should be
        Directory.CreateDirectory(Path.GetDirectoryName(_folder)!);
        File.WriteAllText(_folder, "in the way");
        var log = CreateLog();

        // Act
        var act = () =>
        {
            Record(log, "click");
            Record(log, "click");
        };

        // Assert
        act.Should().NotThrow();
        log.LastError.Should().NotBeNull().And.Contain(_folder);
        _logger.Entries.Where(e => e.Level == LogLevel.Warning).Should().ContainSingle()
            .Which.Message.Should().Contain("audit log").And.Contain(_folder);

        // The folder can be written again: the next line is written and the error is gone
        File.Delete(_folder);
        Record(log, "click");
        log.LastError.Should().BeNull();
        Lines("2026-10-10").Should().HaveCount(1);
    }

    [Fact]
    public async Task ConcurrentCalls_WriteWholeLines()
    {
        var log = CreateLog();

        await Task.WhenAll(Enumerable.Range(0, 40).Select(i => Task.Run(() => Record(log, "type_text", new { text = new string('x', 500 + i) }))));

        var lines = Lines("2026-10-10");
        lines.Should().HaveCount(40);
        lines.Select(l => JsonDocument.Parse(l).RootElement.GetProperty("args").GetProperty("textLength").GetInt32())
            .Should().BeEquivalentTo(Enumerable.Range(500, 40));
    }

    [Fact]
    public async Task AFileAnotherServerIsWriting_IsWaitedFor_Briefly()
    {
        // Arrange - another server holds the day's file open for writing (the log opens it so that only one writer at a time can)
        Directory.CreateDirectory(_folder);
        var other = new FileStream(DayFile("2026-10-10"), FileMode.Append, FileAccess.Write, FileShare.Read | FileShare.Delete);
        var log = CreateLog();

        // Act
        var write = Task.Run(() => Record(log, "click"));
        await Task.Delay(50);
        await other.DisposeAsync();
        await write;

        // Assert
        log.LastError.Should().BeNull();
        Lines("2026-10-10").Should().ContainSingle();
    }

    /// <summary>A clock whose local time zone is UTC, so the day's file does not depend on the machine's zone.</summary>
    private sealed class UtcClock(DateTimeOffset start) : TimeProvider
    {
        private readonly object _lock = new();
        private DateTimeOffset _now = start;

        public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;

        public override DateTimeOffset GetUtcNow()
        {
            lock (_lock)
            {
                return _now;
            }
        }

        public override long GetTimestamp() => GetUtcNow().UtcTicks;

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;

        public void Advance(TimeSpan by)
        {
            lock (_lock)
            {
                _now += by;
            }
        }
    }
}
