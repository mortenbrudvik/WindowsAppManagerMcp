using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Protocol;
using WindowsAppManagerMcp.Infrastructure;

namespace WindowsAppManagerMcp.Tests.Contract;

/// <summary>
/// Contract: an audit log that breaks its contract (roadmap M2.17: it throws, faults or cannot be created) never breaks the
/// call, and leaves a warning with the exception in the server's log (<see cref="AuditLogToolFilter.LoggerCategory"/>), so
/// that a call that went unaudited leaves a trace. Through the production tool and filter registrations, with mocked
/// services (<see cref="ContractServices"/>).
/// </summary>
public class AuditLogFilterWarningContractTests
{
    [Theory]
    [InlineData("begin")]
    [InlineData("end")]
    [InlineData("endFaults")]
    public async Task AnAuditLogThatBreaksItsContract_LeavesAWarningWithTheException_AndTheCallGoesOn(string failure)
    {
        // Arrange - an IAuditLog that throws or faults (AuditLog itself never does)
        using var services = new ContractServices(ContractMode.Typical);
        var audit = new Mock<IAuditLog>();
        var begin = audit.Setup(a => a.Begin(It.IsAny<string>(), It.IsAny<IEnumerable<KeyValuePair<string, JsonElement>>?>(),
            It.IsAny<string?>(), It.IsAny<JsonElement?>()));
        if (failure == "begin")
        {
            begin.Throws(new IOException("begin broke"));
        }
        else
        {
            begin.Returns(new AuditCall("mouse_click", new Dictionary<string, JsonElement>(), null, DateTimeOffset.Now, 0,
                new Dictionary<long, AuditTarget>(), new Dictionary<int, string?>()));
            var end = audit.Setup(a => a.EndAsync(It.IsAny<AuditCall>(), It.IsAny<CallToolResult?>(), It.IsAny<string>()));
            if (failure == "end")
            {
                end.Throws(new IOException("end broke"));
            }
            else
            {
                end.Returns(Task.FromException(new IOException("end broke")));
            }
        }

        await using var server = await InProcessMcpServer.StartAsync([], builder => builder.AddWindowsAppManagerTools(),
            configureServices: s =>
            {
                services.Register(s);
                s.AddSingleton(audit.Object);
            });

        // Act
        var result = await server.CallToolAsync("mouse_click", EveryToolContractTests.ArgumentsFor("mouse_click"));

        // Assert
        result.IsError.Should().NotBe(true);
        var warning = server.Logs.Entries.Where(e => e.Level == LogLevel.Warning && e.Exception is IOException).Should().ContainSingle().Which;
        warning.Category.Should().Be(AuditLogToolFilter.LoggerCategory);
        warning.Message.Should().Contain("mouse_click").And.Contain("audit");
        warning.Exception!.Message.Should().Be(failure == "begin" ? "begin broke" : "end broke");
    }

    [Fact]
    public async Task AnAuditLogThatCannotBeCreated_LeavesAWarning_AndTheCallGoesOn()
    {
        using var services = new ContractServices(ContractMode.Typical);
        await using var server = await InProcessMcpServer.StartAsync([], builder => builder.AddWindowsAppManagerTools(),
            configureServices: s =>
            {
                services.Register(s);
                s.AddSingleton<IAuditLog>(_ => throw new InvalidOperationException("no audit folder"));
            });

        var result = await server.CallToolAsync("mouse_click", EveryToolContractTests.ArgumentsFor("mouse_click"));

        result.IsError.Should().NotBe(true);
        server.Logs.Entries.Should().Contain(e => e.Level == LogLevel.Warning && e.Category == AuditLogToolFilter.LoggerCategory &&
                                                  e.Exception is InvalidOperationException);
    }
}
