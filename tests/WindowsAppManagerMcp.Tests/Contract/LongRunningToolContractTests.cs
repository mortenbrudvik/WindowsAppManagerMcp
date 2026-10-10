using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using WindowsAppManagerMcp.Infrastructure;

namespace WindowsAppManagerMcp.Tests.Contract;

/// <summary>
/// Contract: the long-running window tools through the production tool and filter registrations, with mocked services
/// (<see cref="ContractServices"/>). The progress a service reports reaches the client as notifications/progress in the
/// order it was reported, on both protocol paths, and a client that cancels a call stops it and frees the input gate,
/// which these tools hold while they run, for the next window call. The services' own progress and cancellation are
/// tested in ProcessServiceTests and LayoutServiceTests.
/// </summary>
public class LongRunningToolContractTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    public static TheoryData<string?> Revisions => new() { null, InProcessMcpServer.InitializeRevision };

    private static Task<InProcessMcpServer> StartAsync(
        ContractServices services,
        string? revision,
        TaskCompletionSource<RequestId>? requestId = null,
        TaskCompletionSource? callEnded = null) =>
        InProcessMcpServer.StartAsync(
            [],
            builder => builder
                .WithRequestFilters(filters => filters.AddCallToolFilter(next => async (request, token) =>
                {
                    // The JSON-RPC id of the long call, which a client names when it cancels it
                    if (request.Params?.Name is "apply_layout" or "launch_applications_batch")
                    {
                        requestId?.TrySetResult(request.JsonRpcRequest.Id);
                    }

                    try
                    {
                        return await next(request, token);
                    }
                    finally
                    {
                        if (request.Params?.Name is "apply_layout" or "launch_applications_batch")
                        {
                            callEnded?.TrySetResult();
                        }
                    }
                }))
                .AddWindowsAppManagerTools(),
            clientOptions: InProcessMcpServer.WithRevision(null, revision),
            configureServices: services.Register);

    [Theory]
    [MemberData(nameof(Revisions))]
    public async Task LaunchApplicationsBatch_SendsProgressAfterEachApplication(string? revision)
    {
        // Arrange - the service reports each application it started
        using var services = new ContractServices(ContractMode.Typical);
        services.Processes.Setup(p => p.LaunchApplicationsBatchAsync(
                It.IsAny<IReadOnlyList<(string, string[]?, string?)>>(), It.IsAny<bool>(), It.IsAny<int>(),
                It.IsAny<IProgress<OperationProgress>?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyList<(string, string[]?, string?)> apps, bool _, int _, IProgress<OperationProgress>? progress, CancellationToken _) =>
            {
                for (var i = 0; i < apps.Count; i++)
                {
                    progress?.Report(new OperationProgress(i + 1, apps.Count, $"{i + 1} of {apps.Count}: {apps[i].Item1} started"));
                }

                return apps.Select((_, i) => new LaunchResult(true, ProcessId: 100 + i)).ToList();
            });
        await using var server = await StartAsync(services, revision);
        var progress = new RecordingProgress<ProgressNotificationValue>();

        // Act - the SDK client sends a progress token with the call
        var result = await server.Client.CallToolAsync(
            "launch_applications_batch",
            new Dictionary<string, object?>
            {
                ["applications"] = JsonSerializer.SerializeToElement(new[] { new { executable = "notepad" }, new { executable = "calc" } })
            },
            progress);

        // Assert - in the order reported, before the result
        result.IsError.Should().NotBe(true);
        server.WaitForProgressSent(2).Should().Equal((1d, 2d, "1 of 2: notepad started"), (2d, 2d, "2 of 2: calc started"));
    }

    [Theory]
    [MemberData(nameof(Revisions))]
    public async Task ApplyLayout_SendsProgressForEachPlacement(string? revision)
    {
        // Arrange
        using var services = new ContractServices(ContractMode.Typical);
        services.Layouts.Setup(l => l.ApplyPresetAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<IProgress<OperationProgress>?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync((string name, string _, bool _, IProgress<OperationProgress>? progress, CancellationToken _) =>
            {
                progress?.Report(new OperationProgress(1, 2, "1 of 2: placement 1 (process 'code') was arranged"));
                progress?.Report(new OperationProgress(2, 2, "2 of 2: placement 2 (process 'msedge') was arranged"));
                return new LayoutApplyResult(true, name, WindowsArranged: 2);
            });
        await using var server = await StartAsync(services, revision);
        var progress = new RecordingProgress<ProgressNotificationValue>();

        // Act
        var result = await server.Client.CallToolAsync(
            "apply_layout", new Dictionary<string, object?> { ["name"] = "work" }, progress);

        // Assert
        result.IsError.Should().NotBe(true);
        server.WaitForProgressSent(2).Select(p => (p.Progress, p.Total)).Should().Equal((1d, 2d), (2d, 2d));
    }

    [Theory]
    [MemberData(nameof(Revisions))]
    public async Task ApplyLayout_CancelledByTheClient_StopsAndFreesTheInputGate(string? revision)
    {
        // Arrange - a layout whose launch waits for a window until the call is cancelled
        using var services = new ContractServices(ContractMode.Typical);
        var running = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var tokenCancelled = false;
        services.Layouts.Setup(l => l.ApplyPresetAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<IProgress<OperationProgress>?>(),
                It.IsAny<CancellationToken>()))
            .Returns(async (string name, string _, bool _, IProgress<OperationProgress>? _, CancellationToken token) =>
            {
                running.TrySetResult();
                try
                {
                    await Task.Delay(System.Threading.Timeout.Infinite, token);
                }
                finally
                {
                    tokenCancelled = token.IsCancellationRequested;
                }

                return new LayoutApplyResult(true, name);
            });
        var requestId = new TaskCompletionSource<RequestId>(TaskCreationOptions.RunContinuationsAsynchronously);
        var callEnded = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var server = await StartAsync(services, revision, requestId, callEnded);
        using var clientGivesUp = new CancellationTokenSource();
        var call = server.CallToolAsync("apply_layout",
            new Dictionary<string, object?> { ["name"] = "work", ["launchMissing"] = true }, cancellationToken: clientGivesUp.Token);
        var id = await requestId.Task.WaitAsync(Timeout);

        // The call holds the input gate once it runs (it may first wait for another test's window call to free it)
        await running.Task.WaitAsync(TimeSpan.FromSeconds(40));

        // Act - the client cancels the call
        await server.Client.SendNotificationAsync(NotificationMethods.CancelledNotification,
            new CancelledNotificationParams { RequestId = id, Reason = "the user cancelled" });

        // Assert - the layout stopped, and the next window call gets the gate at once instead of InputBusy after 30 s
        await callEnded.Task.WaitAsync(Timeout);
        tokenCancelled.Should().BeTrue();
        var focus = await server.CallToolAsync("focus_window", new Dictionary<string, object?> { ["handle"] = 1706796L }, Timeout);
        focus.IsError.Should().NotBe(true);
        JsonDocument.Parse(focus.Content.OfType<TextContentBlock>().Single().Text).RootElement
            .GetProperty("success").GetBoolean().Should().BeTrue();

        clientGivesUp.Cancel();
        await FluentActions.Awaiting(() => call).Should().ThrowAsync<Exception>("the cancelled call gets no result");
        server.Logs.Entries.Should().Contain(e => e.Category == ToolErrorReporting.CallLoggerCategory &&
                                                  e.Message.Contains("'apply_layout'") && e.Message.EndsWith(": cancelled"));
    }
}
