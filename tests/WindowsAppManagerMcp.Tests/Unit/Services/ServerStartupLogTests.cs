using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Server;
using WindowsAppManagerMcp.Infrastructure;

namespace WindowsAppManagerMcp.Tests.Unit.Services;

/// <summary>
/// What the server logs once when it starts about itself: its version and the MCP SDK's, the DPI awareness in force,
/// with a warning when it is not the per-monitor v2 the app manifest declares (coordinates may then be scaled), and the
/// tools it serves, with a warning for every toolset entry that was ignored. The awareness comes from a mocked wrapper,
/// and the logger records in memory.
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

    [Fact]
    public async Task StartAsync_LogsTheClipboardAndProtocolSettings_WithAWarningForWhatWasIgnored()
    {
        // Arrange
        _system.Setup(s => s.GetDpiAwareness()).Returns(DpiAwareness.PerMonitorV2);
        var sut = new ServerStartupLog(_system.Object, logger: _logger, clipboard: ClipboardOptions.FromEnvironment(_ => "ture"),
            protocols: ProtocolAllowlist.Parse("vscode, javascript"));

        // Act
        await sut.StartAsync(CancellationToken.None);

        // Assert
        _logger.Entries.Should().Contain(e => e.Level == LogLevel.Information && e.Message.Contains("user's clipboard") &&
                                              e.Message.Contains("off") && e.Message.Contains("vscode"));
        _logger.Entries.Should().Contain(e => e.Level == LogLevel.Warning && e.Message.Contains("'ture'"));
        _logger.Entries.Should().Contain(e => e.Level == LogLevel.Warning && e.Message.Contains("'javascript'"));
    }

    [Fact]
    public async Task StartAsync_PerMonitorV1_IsNoWarning()
    {
        // The manifest's fallback before Windows 10 1703: physical pixels on every monitor for a windowless server
        _system.Setup(s => s.GetDpiAwareness()).Returns(DpiAwareness.PerMonitor);

        await StartAsync();

        _logger.Entries.Should().ContainSingle().Which.Level.Should().Be(LogLevel.Information);
    }

    [Theory]
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

    [Fact]
    public async Task StartAsync_LogsTheToolsetsAndHowManyToolsAreServed()
    {
        // Arrange
        _system.Setup(s => s.GetDpiAwareness()).Returns(DpiAwareness.PerMonitorV2);
        var selection = ToolsetSelection.FromEnvironment(
            name => name switch
            {
                ToolsetSelection.ToolsetsVariable => "-browser,+widnows",
                ToolsetSelection.ReadOnlyVariable => "1",
                _ => null
            },
            Toolsets.Server);
        var options = new McpServerOptions { ToolCollection = [McpServerTool.Create(() => "x", new() { Name = "get_control_status" })] };

        // Act
        await new ServerStartupLog(_system.Object, selection, Options.Create(options), _logger).StartAsync(CancellationToken.None);

        // Assert
        _logger.Entries.Should().Contain(e => e.Level == LogLevel.Information && e.Message.StartsWith("Serving 1 tools") &&
                                              e.Message.Contains("from WAM_TOOLSETS") && e.Message.Contains("not served: workspaces, browser") &&
                                              e.Message.Contains("read-only mode on"));
        _logger.Entries.Should().Contain(e => e.Level == LogLevel.Warning && e.Message.Contains("'+widnows'"));
    }

    [Fact]
    public async Task StartAsync_NoToolServed_IsAWarning()
    {
        _system.Setup(s => s.GetDpiAwareness()).Returns(DpiAwareness.PerMonitorV2);

        await new ServerStartupLog(_system.Object, ToolsetSelection.All(), Options.Create(new McpServerOptions()), _logger)
            .StartAsync(CancellationToken.None);

        _logger.Entries.Should().Contain(e => e.Level == LogLevel.Warning && e.Message.StartsWith("No tool is served"));
    }
}
