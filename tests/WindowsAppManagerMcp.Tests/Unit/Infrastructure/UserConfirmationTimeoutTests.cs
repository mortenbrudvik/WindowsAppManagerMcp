using System.Globalization;
using System.Text.Json;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using WindowsAppManagerMcp.Infrastructure;

namespace WindowsAppManagerMcp.Tests.Unit.Infrastructure;

/// <summary>
/// The reason a question to the user gives when nobody answered in time: the wait in words the user and the agent can read,
/// in seconds below a minute (a 30-second wait read "did not answer within 0.5 minutes", and one under three seconds
/// "within 0 minutes"), and the same in every culture.
/// </summary>
public class UserConfirmationTimeoutTests
{
    [Theory]
    [InlineData(200, "The user did not answer within 0.2 seconds.")]
    [InlineData(1_000, "The user did not answer within 1 second.")]
    [InlineData(2_500, "The user did not answer within 2.5 seconds.")]
    [InlineData(30_000, "The user did not answer within 30 seconds.")]
    [InlineData(59_000, "The user did not answer within 59 seconds.")]
    [InlineData(60_000, "The user did not answer within 1 minute.")]
    [InlineData(90_000, "The user did not answer within 1.5 minutes.")]
    [InlineData(120_000, "The user did not answer within 2 minutes.")]
    public void TheNoAnswerReason_SaysTheWait_InSecondsBelowAMinute(int milliseconds, string expected)
    {
        UserConfirmation.NoAnswerReason(TimeSpan.FromMilliseconds(milliseconds)).Should().Be(expected);
    }

    [Fact]
    public void TheNoAnswerReason_IsTheSameInACultureWithADecimalComma()
    {
        var culture = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("nb-NO");

            UserConfirmation.NoAnswerReason(TimeSpan.FromSeconds(90)).Should().Be("The user did not answer within 1.5 minutes.");
        }
        finally
        {
            CultureInfo.CurrentCulture = culture;
        }
    }

    [Fact]
    public async Task AQuestionNobodyAnswersInTime_GivesTheWaitInSeconds()
    {
        // Arrange - the user answers yes, but over a second after the tool's 200 ms deadline
        var options = new McpClientOptions
        {
            Handlers = new McpClientHandlers
            {
                ElicitationHandler = async (_, token) =>
                {
                    await Task.Delay(TimeSpan.FromMilliseconds(1_500), token);
                    return new ElicitResult
                    {
                        Action = "accept",
                        Content = new Dictionary<string, JsonElement> { [UserConfirmation.ConfirmField] = JsonSerializer.SerializeToElement(true) }
                    };
                }
            }
        };
        await using var server = await InProcessMcpServer.StartAsync(
            [McpServerTool.Create(AskAsync, new McpServerToolCreateOptions { Name = "ask_tool" })],
            clientOptions: InProcessMcpServer.WithRevision(options, InProcessMcpServer.InitializeRevision));

        // Act
        var result = await server.CallToolAsync("ask_tool");

        // Assert
        result.Content.OfType<TextContentBlock>().Single().Text.Should().Be("The user did not answer within 0.2 seconds.");
    }

    private static async Task<string> AskAsync(McpServer server, CancellationToken cancellationToken)
    {
        var answer = await UserConfirmation.AskAsync(server, "Allow it?", "Allow", TimeSpan.FromMilliseconds(200), cancellationToken);
        return answer.Confirmed ? "confirmed" : answer.Reason!;
    }
}
