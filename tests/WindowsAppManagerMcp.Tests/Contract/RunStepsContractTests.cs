using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using WindowsAppManagerMcp.Infrastructure;

namespace WindowsAppManagerMcp.Tests.Contract;

/// <summary>
/// Contract: run_steps (roadmap M6.2) through the production tool and filter registrations, with mocked services
/// (<see cref="ContractServices"/>). Its steps are the server's own tools, called through the server's own call filters
/// (the S0b spike's seam on ModelContextProtocol 2.2.0): each audited step writes its own audit line under
/// "&lt;run's id&gt;#&lt;step&gt;", an argument a step's tool does not declare refuses the whole run, the toolsets and
/// read-only mode apply to steps, a step's own error is reported as a direct call's would be, and the run holds the input
/// gate so that another window call waits for it. The runner's own rules are in StepRunnerTests.
/// </summary>
public class RunStepsContractTests
{
    private const long Notepad = TypicalDesktop.NotepadHandle;
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    public static TheoryData<string?> Revisions => InProcessMcpServer.Revisions();

    private static Task<InProcessMcpServer> StartAsync(
        ContractServices services,
        ToolsetSelection? toolsets = null,
        string? revision = null,
        TaskCompletionSource<RequestId>? runId = null) =>
        InProcessMcpServer.StartAsync(
            [],
            builder => builder
                .WithRequestFilters(filters => filters.AddCallToolFilter(next => (request, token) =>
                {
                    if (request.Params?.Name == "run_steps")
                    {
                        runId?.TrySetResult(request.JsonRpcRequest.Id);
                    }

                    return next(request, token);
                }))
                .AddWindowsAppManagerTools(toolsets),
            clientOptions: InProcessMcpServer.WithRevision(null, revision),
            configureServices: services.Register);

    private static Dictionary<string, object?> Run(string steps, long window = Notepad) => new()
    {
        ["window"] = new Dictionary<string, object?> { ["handle"] = window },
        ["steps"] = JsonSerializer.Deserialize<JsonElement>(steps)
    };

    /// <summary>A form fill: the window to the front, a field, a check box, a typed line and a save.</summary>
    private const string FormFill = """
        [
          { "tool": "focus_window", "id": "front" },
          { "tool": "element_action", "args": { "action": "set_value", "ref": "w4120035e17", "value": "Ada Lovelace" } },
          { "tool": "element_action", "args": { "action": "toggle", "ref": "w4120035e21" } },
          { "tool": "type_text", "args": { "text": "Meeting notes" } },
          { "tool": "press_keys", "args": { "keys": "ctrl+s" } }
        ]
        """;

    private static string Text(CallToolResult result) => result.Content.OfType<TextContentBlock>().Single().Text;

    private static JsonObject Body(CallToolResult result) => JsonNode.Parse(Text(result))!.AsObject();

    /// <summary>True once the run's own audit line is written; false while the log still writes it.</summary>
    private static bool AuditedRun(ContractServices services)
    {
        try
        {
            return services.AuditLines().Any(l => Prop(l, "tool") == "run_steps");
        }
        catch (IOException)
        {
            return false;
        }
    }

    private static string Prop(JsonElement line, string name) =>
        line.TryGetProperty(name, out var value) ? value.ValueKind == JsonValueKind.String ? value.GetString()! : value.GetRawText() : "(none)";

    [Fact]
    public async Task AFormFill_RunsEveryStepThroughItsOwnTool_InOneCall()
    {
        // Arrange
        using var services = new ContractServices(ContractMode.Typical);
        await using var server = await StartAsync(services);

        // Act
        var result = await server.CallToolAsync("run_steps", Run(FormFill));

        // Assert
        result.IsError.Should().NotBe(true, Text(result));
        var body = Body(result);
        body["success"]!.GetValue<bool>().Should().BeTrue();
        body["ran"]!.GetValue<int>().Should().Be(5);
        body["steps"]!.AsArray().Select(s => s!["status"]!.GetValue<string>()).Should().AllBe("ok");
        body["steps"]![4]!["result"]!["keysSent"]!.GetValue<int>().Should().Be(1, "the last step returns its own result");
        body["steps"]![1]!.AsObject().ContainsKey("result").Should().BeFalse();

        services.Foreground.Verify(f => f.EnsureForeground((nint)Notepad, It.IsAny<int>()), Times.Once);
        services.ElementActions.Verify(a => a.ActAsync(It.IsAny<ElementActionRequest>(), It.IsAny<CancellationToken>()),
            Times.Exactly(2));
        services.Input.Verify(i => i.TypeTextAsync("Meeting notes", It.IsAny<int>(), It.Is<InputTarget>(t => t.WindowHandle == (nint)Notepad),
            It.IsAny<bool>(), It.IsAny<TextControlChars?>(), TextInputMethod.Unicode, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task EachAuditedStep_WritesItsOwnAuditLine_UnderTheRunsRequestId_AndTheRunWritesOne()
    {
        // Arrange
        using var services = new ContractServices(ContractMode.Typical);
        var runId = new TaskCompletionSource<RequestId>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var server = await StartAsync(services, runId: runId);

        // Act
        await server.CallToolAsync("run_steps", Run(FormFill));

        // Assert - focus_window is not audited (it injects nothing); the run's own line comes last, when it ended
        var id = (await runId.Task.WaitAsync(Timeout)).ToString();
        var lines = services.AuditLines();
        lines.Select(l => (Prop(l, "tool"), Prop(l, "requestId"))).Should().Equal(
            ("element_action", $"{id}#2"), ("element_action", $"{id}#3"), ("type_text", $"{id}#4"), ("press_keys", $"{id}#5"),
            ("run_steps", id));
        lines.Should().AllSatisfy(l => Prop(l, "outcome").Should().Be("ok"));
        // The default-deny summaries: the typed text and the value as their lengths only, and the run's steps as a count
        string.Join("\n", lines.Select(l => l.GetRawText())).Should().NotContain("Meeting notes").And.NotContain("Ada Lovelace");
        lines[^1].GetRawText().Should().Contain("\"stepsCount\":5");
    }

    [Fact]
    public async Task AStepArgumentItsToolDoesNotDeclare_RefusesTheWholeRun_BeforeAnythingRuns()
    {
        // Arrange
        using var services = new ContractServices(ContractMode.Typical);
        await using var server = await StartAsync(services);

        // Act
        var result = await server.CallToolAsync("run_steps", Run("""
            [ { "tool": "focus_window" }, { "tool": "type_text", "args": { "txt": "Meeting notes" } } ]
            """));

        // Assert
        result.IsError.Should().BeTrue();
        Body(result)["errorCode"]!.GetValue<string>().Should().Be("InvalidArguments");
        Text(result).Should().Contain("Step 2 (type_text)").And.Contain("did you mean 'text'?").And.Contain("Nothing was done");
        services.Foreground.Verify(f => f.EnsureForeground(It.IsAny<nint>(), It.IsAny<int>()), Times.Never);
        services.AuditLines().Select(l => (Prop(l, "tool"), Prop(l, "errorCode"))).Should().Equal(("run_steps", "InvalidArguments"));
    }

    [Theory]
    [InlineData("""{ "tool": "type_text", "args": { "text": "a", "method": "paste" } }""")]
    [InlineData("""{ "tool": "capture_window", "args": { "outputPath": "C:\\Users\\user\\a.png" } }""")]
    [InlineData("""{ "tool": "click", "args": { "x": 1, "y": 2 } }""")] // removed in 3.0.0
    [InlineData("""{ "tool": "set_clipboard", "args": { "text": "a" } }""")]
    [InlineData("""{ "tool": "close_window" }""")]
    [InlineData("""{ "tool": "run_steps" }""")]
    [InlineData("""{ "tool": "press_keys", "args": { "keys": "alt+f4" } }""")]
    [InlineData("""{ "tool": "send_keys", "args": { "keys": "^w" } }""")]
    [InlineData("""{ "tool": "element_action", "args": { "action": "invoke", "name": "Send", "scope": "desktop" } }""")]
    public async Task AStepThatMayNotRun_RefusesTheWholeRun_WithInvalidArguments(string step)
    {
        using var services = new ContractServices(ContractMode.Typical);
        await using var server = await StartAsync(services);

        var result = await server.CallToolAsync("run_steps", Run($"[ {{ \"tool\": \"focus_window\" }}, {step} ]"));

        result.IsError.Should().BeTrue();
        Body(result)["errorCode"]!.GetValue<string>().Should().Be("InvalidArguments", Text(result));
        services.Foreground.Verify(f => f.EnsureForeground(It.IsAny<nint>(), It.IsAny<int>()), Times.Never);
    }

    [Theory]
    [InlineData("""{ "keys": "enter", "windowHandle": null }""")]
    [InlineData("""{ "keys": "enter", "window": null }""")]
    [InlineData("""{ "keys": "enter", "window": {} }""")]
    public async Task AKeyStepWithANullOrEmptyWindow_SendsToTheRunsWindow_NotTheOneInFront(string args)
    {
        // Arrange
        using var services = new ContractServices(ContractMode.Typical);
        await using var server = await StartAsync(services);

        // Act
        var result = await server.CallToolAsync("run_steps", Run($$"""[ { "tool": "press_keys", "args": {{args}} } ]"""));

        // Assert - the tool got the run's window as its target, not none (which means the window in front)
        result.IsError.Should().NotBe(true, Text(result));
        services.Input.Verify(i => i.PressKeysAsync(It.IsAny<KeyList>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<int>(),
            It.Is<InputTarget>(t => t.WindowHandle == (nint)Notepad), It.IsAny<bool>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task AStepToolTheToolsetsTurnedOff_IsRefusedUpfront()
    {
        // Arrange - WAM_EXCLUDE_TOOLS takes type_text out of the tool collection, so a step cannot call it either
        using var services = new ContractServices(ContractMode.Typical);
        var toolsets = ToolsetSelection.FromEnvironment(name => name == "WAM_EXCLUDE_TOOLS" ? "type_text" : null, Toolsets.Server);
        await using var server = await StartAsync(services, toolsets);

        // Act
        var result = await server.CallToolAsync("run_steps", Run("""[ { "tool": "type_text", "args": { "text": "a" } } ]"""));

        // Assert
        result.IsError.Should().BeTrue();
        Text(result).Should().Contain("does not serve type_text");
        services.Input.Verify(i => i.TypeTextAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<InputTarget>(), It.IsAny<bool>(),
            It.IsAny<TextControlChars?>(), It.IsAny<TextInputMethod>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Theory]
    [InlineData("WAM_READ_ONLY", "1")]
    [InlineData("WAM_TOOLSETS", "-steps")]
    [InlineData("WAM_EXCLUDE_TOOLS", "steps")]
    public async Task RunSteps_IsNotServed_InReadOnlyMode_OrWithoutItsToolset(string variable, string value)
    {
        using var services = new ContractServices(ContractMode.Typical);
        var toolsets = ToolsetSelection.FromEnvironment(name => name == variable ? value : null, Toolsets.Server);
        await using var server = await StartAsync(services, toolsets);

        var tools = await server.Client.ListToolsAsync();

        tools.Select(t => t.Name).Should().NotContain("run_steps");
    }

    [Fact]
    public async Task AStepsOwnError_IsReportedAsADirectCallsWouldBe_AndStopsTheRun()
    {
        // Arrange - an exception the tool did not mean, which the error filter turns into UnhandledException
        using var services = new ContractServices(ContractMode.Typical);
        services.Input.Setup(i => i.TypeTextAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<InputTarget>(), It.IsAny<bool>(),
                It.IsAny<TextControlChars?>(), It.IsAny<TextInputMethod>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("boom"));
        await using var server = await StartAsync(services);

        // Act
        var result = await server.CallToolAsync("run_steps", Run(FormFill));

        // Assert
        result.IsError.Should().BeTrue();
        var body = Body(result);
        body["stoppedBy"]!.GetValue<string>().Should().Be("stepFailed");
        body["errorCode"]!.GetValue<string>().Should().Be("UnhandledException");
        body["steps"]!.AsArray().Select(s => s!["status"]!.GetValue<string>()).Should().Equal("ok", "ok", "ok", "failed", "skipped");
        body["steps"]![3]!["result"]!["errorCode"]!.GetValue<string>().Should().Be("UnhandledException");
        services.Input.Verify(i => i.PressKeysAsync(It.IsAny<KeyList>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<int>(),
            It.IsAny<InputTarget>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Theory]
    [MemberData(nameof(Revisions))]
    public async Task TheRun_ReportsItsProgressOncePerStep_AndNotItsStepsOwn(string? revision)
    {
        // Arrange - wait_for_window reports progress of its own, which a step does not forward
        using var services = new ContractServices(ContractMode.Typical);
        services.WindowWait.Setup(w => w.WaitForConditionAsync(It.IsAny<WindowConditionRequest>(), It.IsAny<IProgress<OperationProgress>?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync((WindowConditionRequest _, IProgress<OperationProgress>? progress, CancellationToken _) =>
            {
                progress?.Report(new OperationProgress(1, null, "waiting"));
                return new WindowConditionResult { Satisfied = true, ElapsedMs = 10, MatchCount = 1, Window = TypicalDesktop.Notepad };
            });
        await using var server = await StartAsync(services, revision: revision);

        // Act
        var result = await server.Client.CallToolAsync("run_steps", Run("""
            [
              { "tool": "focus_window" },
              { "tool": "wait_for_window", "args": { "window": { "processName": "notepad" }, "condition": "foreground" } },
              { "tool": "press_keys", "args": { "keys": "ctrl+s" } }
            ]
            """), progress: new RecordingProgress<ProgressNotificationValue>());

        // Assert
        result.IsError.Should().NotBe(true, Text(result));
        server.WaitForProgressSent(3).Select(p => (p.Progress, p.Total, p.Message)).Should().Equal(
            (1, 3, "step 1 of 3: focus_window ok"), (2, 3, "step 2 of 3: wait_for_window ok"), (3, 3, "step 3 of 3: press_keys ok"));
        await Task.Delay(200);
        server.ProgressSent().Should().HaveCount(3);
    }

    [Fact]
    public async Task AWindowCallMadeDuringTheRun_WaitsForTheRunToEnd()
    {
        // Arrange - the run's typing blocks until the test lets it go
        using var services = new ContractServices(ContractMode.Typical);
        var typing = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var letGo = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        services.Input.Setup(i => i.TypeTextAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<InputTarget>(), It.IsAny<bool>(),
                It.IsAny<TextControlChars?>(), It.IsAny<TextInputMethod>(), It.IsAny<CancellationToken>()))
            .Returns(async (string text, int _, InputTarget _, bool _, TextControlChars? _, TextInputMethod _, CancellationToken _) =>
            {
                typing.TrySetResult();
                await letGo.Task;
                return new TypeTextResult(true, text.Length, text.Length);
            });
        var layoutApplied = false;
        services.Layouts.Setup(l => l.ApplyPresetAsync(It.IsAny<string>(), It.IsAny<LayoutMatchBy>(), It.IsAny<bool>(),
                It.IsAny<LayoutApplyOptions?>(), It.IsAny<IProgress<OperationProgress>?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string name, LayoutMatchBy _, bool _, LayoutApplyOptions? _, IProgress<OperationProgress>? _, CancellationToken _) =>
            {
                layoutApplied = true;
                return new LayoutApplyResult(true, name, WindowsArranged: 1);
            });
        await using var server = await StartAsync(services);

        // Act - apply_layout runs inside the gate (InputGateToolFilter), which the run holds
        var run = server.CallToolAsync("run_steps", Run(FormFill));
        await typing.Task.WaitAsync(Timeout);
        var layout = server.CallToolAsync("apply_layout", new Dictionary<string, object?> { ["name"] = "layout-1" });
        await Task.Delay(300);
        var appliedDuringTheRun = layoutApplied;
        letGo.SetResult();

        // Assert
        (await run).IsError.Should().NotBe(true);
        (await layout).IsError.Should().NotBe(true);
        appliedDuringTheRun.Should().BeFalse("no other window call runs between two steps of a run");
        layoutApplied.Should().BeTrue();
    }

    [Fact]
    public async Task ACancelledRun_StopsInsideItsStep_AndFreesTheInputGate()
    {
        // Arrange
        using var services = new ContractServices(ContractMode.Typical);
        var typing = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        services.Input.Setup(i => i.TypeTextAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<InputTarget>(), It.IsAny<bool>(),
                It.IsAny<TextControlChars?>(), It.IsAny<TextInputMethod>(), It.IsAny<CancellationToken>()))
            .Returns(async (string _, int _, InputTarget _, bool _, TextControlChars? _, TextInputMethod _, CancellationToken token) =>
            {
                typing.TrySetResult();
                await Task.Delay(System.Threading.Timeout.Infinite, token);
                return new TypeTextResult(true, 1, 1);
            });
        var runId = new TaskCompletionSource<RequestId>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var server = await StartAsync(services, runId: runId);

        // Act - the client names the run's request in notifications/cancelled, as LongRunningToolContractTests does
        using var clientGivesUp = new CancellationTokenSource();
        var call = server.CallToolAsync("run_steps", Run(FormFill), cancellationToken: clientGivesUp.Token);
        var id = await runId.Task.WaitAsync(Timeout);
        await typing.Task.WaitAsync(Timeout);
        await server.Client.SendNotificationAsync(NotificationMethods.CancelledNotification,
            new CancelledNotificationParams { RequestId = id, Reason = "the user cancelled" });

        // Assert - the next window call gets the gate
        SpinWait.SpinUntil(() => AuditedRun(services), Timeout).Should().BeTrue();
        clientGivesUp.Cancel();
        await FluentActions.Awaiting(() => call).Should().ThrowAsync<Exception>();
        var focus = await server.CallToolAsync("focus_window", new Dictionary<string, object?> { ["handle"] = Notepad });
        focus.IsError.Should().NotBe(true, Text(focus));
        services.AuditLines().Where(l => Prop(l, "tool") is "type_text" or "run_steps").Select(l => Prop(l, "outcome"))
            .Should().Equal("cancelled", "cancelled");
        services.Input.Verify(i => i.PressKeysAsync(It.IsAny<KeyList>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<int>(),
            It.IsAny<InputTarget>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task TheRunsWindow_MustBeNamed_AndNotTheAgentsOwnHost()
    {
        using var services = new ContractServices(ContractMode.Typical);
        services.Windows.Setup(w => w.GetWindowInfo((nint)0x30_0D1E)).Returns(TypicalDesktop.Notepad with { Handle = 0x30_0D1E, IsAgentHost = true });
        await using var server = await StartAsync(services);

        var foreground = await server.CallToolAsync("run_steps", new Dictionary<string, object?>
        {
            ["window"] = new Dictionary<string, object?> { ["foreground"] = true },
            ["steps"] = JsonSerializer.Deserialize<JsonElement>("""[ { "tool": "focus_window" } ]""")
        });
        var host = await server.CallToolAsync("run_steps", Run("""[ { "tool": "focus_window" } ]""", window: 0x30_0D1E));

        Body(foreground)["errorCode"]!.GetValue<string>().Should().Be("InvalidArguments");
        Body(host)["errorCode"]!.GetValue<string>().Should().Be("TargetIsAgentHost");
        services.Foreground.Verify(f => f.EnsureForeground(It.IsAny<nint>(), It.IsAny<int>()), Times.Never);
    }

    [Fact]
    public async Task TheRunsWindow_CountedAsTheHostOnlyWhileTheAncestorsAreUnknown_IsRefused_SayingWhy()
    {
        // The server cannot read its ancestors, so a window of a program that started before it counts as the host's
        using var services = new ContractServices(ContractMode.Typical);
        services.Windows.Setup(w => w.GetWindowInfo((nint)0x30_0D1E)).Returns(TypicalDesktop.Notepad with
        {
            Handle = 0x30_0D1E, IsAgentHost = true, IsAgentHostOnlyWhileAncestorsUnknown = true
        });
        await using var server = await StartAsync(services);

        var host = await server.CallToolAsync("run_steps", Run("""[ { "tool": "focus_window" } ]""", window: 0x30_0D1E));

        Body(host)["errorCode"]!.GetValue<string>().Should().Be("TargetIsAgentHost");
        Body(host)["error"]!.GetValue<string>().Should().Contain(HostGuard.AncestorsUnknownNote).And.Contain("Nothing was done");
        services.Foreground.Verify(f => f.EnsureForeground(It.IsAny<nint>(), It.IsAny<int>()), Times.Never);
    }

    [Fact]
    public async Task TheStepDispatcher_RunsStepsThroughTheServersOwnCallFilters_InTheirOrder()
    {
        // The SDK seam M6.2 stands on (spike S0b): the registered call-tool filters are readable from the server's options
        using var services = new ContractServices(ContractMode.Typical);
        await using var server = await StartAsync(services);

        var options = server.Services.GetRequiredService<Microsoft.Extensions.Options.IOptions<McpServerOptions>>().Value;
        var filters = options.Filters.Request.CallToolFilters.ToList();

        // The test's own filter first (it records the run's id), then the five production filters in production order
        filters.Skip(1).Should().Equal(
            (McpRequestFilter<CallToolRequestParams, CallToolResult>)AuditLogToolFilter.Filter,
            (McpRequestFilter<CallToolRequestParams, CallToolResult>)ToolErrorReporting.Filter,
            (McpRequestFilter<CallToolRequestParams, CallToolResult>)RemovedTools.Filter,
            (McpRequestFilter<CallToolRequestParams, CallToolResult>)UndeclaredArgumentToolFilter.Filter,
            (McpRequestFilter<CallToolRequestParams, CallToolResult>)InputGateToolFilter.Filter);
    }

    // ---- The step allow list ----

    [Fact]
    public void TheStepTools_AreServedTools_NoneRemoved_AndNoneThatAsksClosesLaunchesOrWritesTheClipboard()
    {
        var steps = Enum.GetValues<StepTool>().Select(StepRunner.ToolName).ToList();

        steps.Should().BeSubsetOf(ServerToolCatalog.ServedToolNames);
        steps.Should().NotIntersectWith(RemovedTools.Replacements.Keys, "a removed tool is never a step (owner decision D1)");
        steps.Should().NotIntersectWith(InputGateToolFilter.WindowChangingTools, "a step launches nothing and writes no clipboard or layout");
        // Nothing that asks the user either: a question inside a held gate would stall every input call while the user reads it
        steps.Should().NotContain(["run_steps", "close_window", "close_windows_batch", "close_application", "kill_process", "set_clipboard",
            "get_clipboard", "resume_input", "release_all_input", "respond_to_dialog", "file_dialog", "save_layout", "delete_layout",
            "launch_application", "launch_browser_with_debug", "close_debug_browser"]);
        steps.Where(t => Toolsets.Server.ToolsetOf(t)!.Name is "browser" or "browser-actions" or "browser-script")
            .Should().BeEmpty("no browser tool is a step");
    }

    [Fact]
    public void EveryStepTool_PassesTheRunsLease_AsAnInputReadOnlyOrSelfGatedTool()
    {
        // A step tool the gate filter held itself would be a tool the lease must pass through the filter: today none is
        foreach (var tool in Enum.GetValues<StepTool>().Select(StepRunner.ToolName))
        {
            (InputGateToolFilter.InputTools.Contains(tool) || InputGateToolFilter.ReadOnlyTools.Contains(tool) ||
             InputGateToolFilter.SelfGatedTools.Contains(tool)).Should().BeTrue("{0} is on a gate list the lease covers", tool);
        }
    }
}
