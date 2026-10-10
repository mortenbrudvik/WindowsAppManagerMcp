using System.Text.RegularExpressions;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using WindowsAppManagerMcp.Infrastructure;

namespace WindowsAppManagerMcp.Tests.Contract;

/// <summary>
/// Contract: the server's call-tool pipeline as <see cref="ServiceRegistration.AddWindowsAppManagerTools"/> builds it.
/// The tool error filter is the outermost filter, so its call log covers what the input gate filter does: a call
/// the gate filter refuses while input is paused, and the wait for the gate. Runs a real MCP client and server
/// in-process; the window tools the server registers are never run (they are refused while paused), and the only
/// tool that runs is a fake.
/// </summary>
public class ServerFilterPipelineTests
{
    private const string UnclassifiedTool = "a_tool_nobody_classified";
    private static readonly TimeSpan GateHeld = TimeSpan.FromMilliseconds(300);

    [Fact]
    public async Task TheSdk_RunsTheFirstFilterAddedAsTheOutermost()
    {
        // The order AddWindowsAppManagerTools relies on. SDK 0.5's XML documentation of McpServerOptions.Filters said
        // the opposite ("the last filter added will be the outermost"); 2.x documents this order. Pinned all the same.
        var order = new System.Collections.Concurrent.ConcurrentQueue<string>();
        await using var server = await InProcessMcpServer.StartAsync(
            [McpServerTool.Create(() => order.Enqueue("tool"), new McpServerToolCreateOptions { Name = UnclassifiedTool })],
            builder => builder
                .WithRequestFilters(filters => filters.AddCallToolFilter(Recording("first", order)))
                .WithRequestFilters(filters => filters.AddCallToolFilter(Recording("second", order))));

        await server.CallToolAsync(UnclassifiedTool);

        order.Should().Equal("first:before", "second:before", "tool", "second:after", "first:after");
    }

    [Fact]
    public async Task AWindowChangingCall_RefusedWhilePaused_IsLoggedWithPausedByUser()
    {
        // Arrange - the server's own tools and filters; the user has paused input
        var session = new InputSession(new FakeInputWrapper());
        session.Pause(InputControlSources.Hotkey);
        await using var server = await StartServerAsync(session);

        // Act
        var result = await server.CallToolAsync("focus_window", new Dictionary<string, object?> { ["handle"] = 4242 });

        // Assert - the gate filter refused it without running it, and the error filter logged the outcome
        result.IsError.Should().BeTrue();
        var lines = CallLines(server, "focus_window");
        lines.Should().HaveCount(2);
        lines[0].Should().Contain("started");
        lines[1].Should().EndWith(": isError PausedByUser");
    }

    [Fact]
    public async Task TheWaitForTheInputGate_CountsTowardTheLoggedDuration()
    {
        // Arrange - a served tool on no list runs inside the gate, which an input call holds
        await using var server = await StartServerAsync(session: null);
        var gate = InputGate.Enter();
        try
        {
            // Act - the gate is released only once the call has started (and its call log with it), and a while later
            var call = server.CallToolAsync(UnclassifiedTool);
            SpinWait.SpinUntil(() => CallLines(server, UnclassifiedTool).Count > 0, TimeSpan.FromSeconds(10)).Should().BeTrue();
            await Task.Delay(GateHeld);
            gate.Dispose();
            await call;
        }
        finally
        {
            gate.Dispose();
        }

        // Assert
        var finished = CallLines(server, UnclassifiedTool).Last();
        var duration = int.Parse(Regex.Match(finished, @"finished in (\d+) ms").Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
        duration.Should().BeGreaterThanOrEqualTo((int)GateHeld.TotalMilliseconds - 20, "the call waited for the gate before it ran");
        finished.Should().EndWith(": ok");
    }

    [Fact]
    public async Task AnUnknownTool_WhilePaused_IsStillAProtocolError()
    {
        // docs/TOOLS.md: an unknown tool name is reported as a JSON-RPC protocol error, not as a tool result. A call
        // that runs nothing cannot change which window is in front, so the pause does not apply to it.
        var session = new InputSession(new FakeInputWrapper());
        session.Pause(InputControlSources.Hotkey);
        await using var server = await StartServerAsync(session);

        var act = async () => await server.CallToolAsync("no_such_tool");

        (await act.Should().ThrowAsync<McpProtocolException>()).Which.Message.Should().Contain("no_such_tool");
    }

    [Fact]
    public async Task AnUnknownTool_DoesNotWaitForTheInputGate()
    {
        await using var server = await StartServerAsync(session: null);
        var gate = InputGate.Enter();
        try
        {
            var act = async () => await server.CallToolAsync("no_such_tool", timeout: TimeSpan.FromSeconds(5));

            await act.Should().ThrowAsync<McpProtocolException>("the protocol error comes back at once, not after the gate is free");
        }
        finally
        {
            gate.Dispose();
        }
    }

    /// <summary>
    /// The server's tool and filter registrations, plus a fake tool that is on none of the input gate filter's
    /// lists, and <paramref name="session"/> as the input session when given.
    /// </summary>
    private static Task<InProcessMcpServer> StartServerAsync(IInputSession? session) =>
        InProcessMcpServer.StartAsync(
            [McpServerTool.Create(() => "done", new McpServerToolCreateOptions { Name = UnclassifiedTool })],
            builder => builder.AddWindowsAppManagerTools(),
            configureServices: services =>
            {
                if (session != null)
                {
                    services.AddSingleton(session);
                }
            });

    private static List<string> CallLines(InProcessMcpServer server, string tool) =>
        server.Logs.Entries
            .Where(e => e.Category == ToolErrorReporting.CallLoggerCategory && e.Message.Contains($"'{tool}'"))
            .Select(e => e.Message)
            .ToList();

    private static McpRequestFilter<CallToolRequestParams, CallToolResult> Recording(
        string name, System.Collections.Concurrent.ConcurrentQueue<string> order) =>
        next => async (request, token) =>
        {
            order.Enqueue($"{name}:before");
            try
            {
                return await next(request, token);
            }
            finally
            {
                order.Enqueue($"{name}:after");
            }
        };
}
