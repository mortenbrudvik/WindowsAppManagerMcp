using Microsoft.Extensions.Logging;

namespace WindowsAppManagerMcp.Tests.Unit.Services;

/// <summary>
/// What the server logs once when it starts about itself: its version and the MCP SDK's, and the DPI awareness in
/// force, with a warning when it is not the per-monitor v2 the app manifest declares (coordinates may then be scaled).
/// The awareness comes from a mocked wrapper, and the logger records in memory.
/// </summary>
public class ServerStartupLogTests
{
    private readonly Mock<INativeSystemInfoWrapper> _system = new();
    private readonly RecordingLogger<ServerStartupLog> _logger = new();

    private Task StartAsync() => new ServerStartupLog(_system.Object, logger: _logger).StartAsync(CancellationToken.None);

    [Fact]
    public async Task StartAsync_LogsTheVersionsAndTheDpiAwareness()
    {
        // Arrange
        _system.Setup(s => s.GetDpiAwareness()).Returns(DpiAwareness.PerMonitorV2);

        // Act
        await StartAsync();

        // Assert
        var entry = _logger.Entries.Should().ContainSingle().Subject;
        entry.Level.Should().Be(LogLevel.Information);
        entry.Message.Should().Contain(ServerVersion.Current).And.Contain($"MCP SDK {ServerVersion.SdkVersion}")
            .And.Contain("DPI awareness PerMonitorV2");
    }

    [Theory]
    [InlineData(DpiAwareness.PerMonitor)]
    [InlineData(DpiAwareness.System)]
    [InlineData(DpiAwareness.Unaware)]
    [InlineData(DpiAwareness.Unknown)]
    public async Task StartAsync_AnotherAwareness_IsAWarning(DpiAwareness awareness)
    {
        _system.Setup(s => s.GetDpiAwareness()).Returns(awareness);

        await StartAsync();

        _logger.Entries.Should().Contain(e => e.Level == LogLevel.Warning && e.Message.Contains(awareness.ToString()) &&
                                              e.Message.Contains("PerMonitorV2") && e.Message.Contains("scaled"));
    }
}
