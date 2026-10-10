using Microsoft.Extensions.Logging;

namespace WindowsAppManagerMcp.Tests.Unit.Services;

/// <summary>
/// The safety settings in force are logged once when the server starts, so a mistyped
/// WAM_DENY_PROCESSES or WAM_DESTRUCTIVE_CHORDS shows up in the server log. Pure: settings are built
/// from lookup functions, and the logger records in memory.
/// </summary>
public class InputSafetyStartupTests
{
    private readonly RecordingLogger _logger = new();

    private async Task Start(TargetPolicySettings policy, InputGuardOptions options) =>
        await new InputSafetyStartup(new TargetPolicy(policy), options, _logger).StartAsync(CancellationToken.None);

    [Fact]
    public async Task StartAsync_LogsThePolicyAndTheChords()
    {
        // Act
        await Start(TargetPolicySettings.Default, InputGuardOptions.Default);

        // Assert
        var entry = _logger.Entries.Should().ContainSingle().Subject;
        entry.Level.Should().Be(LogLevel.Information);
        entry.Message.Should().Contain("KeePass").And.Contain("from default").And.Contain("%{F4}").And.Contain("WAM_ALLOW_PROCESSES not set");
    }

    [Fact]
    public async Task StartAsync_IgnoredChordEntries_AreAWarning()
    {
        // Arrange - a comma-separated list
        var options = InputGuardOptions.FromEnvironment(name => name == InputGuardOptions.DestructiveChordsVariable ? "%{F4},^w" : null);

        // Act
        await Start(TargetPolicySettings.Default, options);

        // Assert
        _logger.Entries.Should().Contain(e => e.Level == LogLevel.Warning && e.Message.Contains("%{F4},^w") &&
                                              e.Message.Contains("default chords stay in force"));
    }

    [Fact]
    public async Task StartAsync_AnAllowList_IsLogged()
    {
        var policy = TargetPolicySettings.FromEnvironment(name => name == TargetPolicySettings.AllowVariable ? "notepad" : null);

        await Start(policy, InputGuardOptions.Default);

        _logger.Entries.Single().Message.Should().Contain("allowed processes notepad");
    }

    private sealed class RecordingLogger : ILogger<InputSafetyStartup>
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) => Entries.Add((logLevel, formatter(state, exception)));
    }
}
