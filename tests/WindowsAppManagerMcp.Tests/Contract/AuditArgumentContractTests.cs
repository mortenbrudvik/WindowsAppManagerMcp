using System.Globalization;
using WindowsAppManagerMcp.Infrastructure;

namespace WindowsAppManagerMcp.Tests.Contract;

/// <summary>
/// Contract: the audit log (roadmap M2.17) names the target of a call as the tool binds it, a handle written as a JSON
/// string included. Through the production tool and filter registrations, with mocked services
/// (<see cref="ContractServices"/>); the record's own rules are in AuditRecordTests.
/// </summary>
public class AuditArgumentContractTests
{
    [Fact]
    public async Task ACallWhoseHandleIsAString_IsAuditedWithTheWindowItNames()
    {
        // Arrange - refused while input is paused, so the target can only come from what the log read before the call
        using var services = new ContractServices(ContractMode.Typical);
        services.Session.Pause(InputControlSources.Hotkey);
        await using var server = await InProcessMcpServer.StartAsync(
            [], builder => builder.AddWindowsAppManagerTools(), configureServices: services.Register);

        // Act
        await server.CallToolAsync("close_window", new Dictionary<string, object?>
        {
            ["handle"] = TypicalDesktop.NotepadHandle.ToString(CultureInfo.InvariantCulture)
        });

        // Assert
        var line = services.AuditLines().Single();
        line.GetProperty("target").GetProperty("handle").GetInt64().Should().Be(TypicalDesktop.NotepadHandle);
        line.GetProperty("target").GetProperty("title").GetString().Should().Be(TypicalDesktop.Notepad.Title);
        line.GetProperty("args").GetProperty("handle").GetInt64().Should().Be(TypicalDesktop.NotepadHandle);
        line.GetProperty("args").TryGetProperty("handleLength", out _).Should().BeFalse();
    }
}
