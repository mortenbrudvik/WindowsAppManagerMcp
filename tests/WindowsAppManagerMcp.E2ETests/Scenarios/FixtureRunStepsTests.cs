using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol.Protocol;

namespace WindowsAppManagerMcp.E2ETests.Scenarios;

/// <summary>
/// run_steps (roadmap M6.2) against the fixture: the input tier, through the real MCP pipeline. Its steps are tool calls
/// inside the server, so only the in-process MCP host (<see cref="ServerHarness.StartMcpServerAsync"/>) can run them: with
/// the server's own filters (the audit log, error reporting, the undeclared-argument refusal, the input gate) over the
/// harness's guarded services, so the server acts only on the fixture. The fixture's event log is the independent record
/// of what arrived, and in what order. The foreground scenario moves the foreground to a second fixture the test started,
/// never to the terminal that runs the tests.
/// </summary>
public class FixtureRunStepsTests
{
    /// <summary>How long a run (and a direct call made beside it) may take before the test fails.</summary>
    private static readonly TimeSpan CallTimeout = TimeSpan.FromSeconds(60);

    /// <summary>How long a fixture event, or a state the test waits for, may take to show up.</summary>
    private static readonly TimeSpan SettleTimeout = TimeSpan.FromSeconds(10);

    // The modifiers that must never be left down after a run
    private const ushort VkLShift = 0xA0;
    private const ushort VkLControl = 0xA2;
    private const ushort VkLMenu = 0xA4;

    private readonly ITestOutputHelper _output;

    public FixtureRunStepsTests(ITestOutputHelper output)
    {
        _output = output;
    }

    private static Dictionary<string, object?> Run(nint window, string steps, int? maxDurationMs = null, int? delayBetweenStepsMs = null)
    {
        var arguments = new Dictionary<string, object?>
        {
            ["window"] = new Dictionary<string, object?> { ["handle"] = (long)window },
            ["steps"] = JsonSerializer.Deserialize<JsonElement>(steps)
        };
        if (maxDurationMs is { } max)
        {
            arguments["maxDurationMs"] = max;
        }

        if (delayBetweenStepsMs is { } delay)
        {
            arguments["delayBetweenStepsMs"] = delay;
        }

        return arguments;
    }

    private JsonObject Body(CallToolResult result)
    {
        var text = result.Content.OfType<TextContentBlock>().Single().Text;
        _output.WriteLine($"isError={result.IsError}: {text}");
        return JsonNode.Parse(text)!.AsObject();
    }

    private static IReadOnlyList<string?> Statuses(JsonObject body) =>
        body["steps"]!.AsArray().Select(s => s!["status"]!.GetValue<string>()).ToList();

    private static bool TextArrived(E2ESession e2e, string text) =>
        e2e.Fixture.Events.ReadAll().Any(e => e.Name == FixtureContract.Events.TextChanged &&
                                               e.GetString("id") == FixtureContract.Ids.EditBox && e.GetString("text") == text);

    private static long SequenceOf(E2ESession e2e, string text) =>
        e2e.Fixture.WaitForEvent(FixtureContract.Events.TextChanged,
            e => e.GetString("id") == FixtureContract.Ids.EditBox && e.GetString("text") == text).Sequence;

    private static void NoModifierIsDown(E2ESession e2e)
    {
        var input = e2e.Server.Get<INativeInputWrapper>();
        new[] { VkLShift, VkLControl, VkLMenu }.Where(input.IsKeyDown).Should().BeEmpty("no modifier may be left down after a run");
    }

    [DesktopFact]
    public async Task AFormFill_FocusClickTypeToggleAndVerify_RunsInOneCall_AndEveryStepArrivesInOrder()
    {
        // Arrange
        using var e2e = E2ESession.Start(DesktopTier.Input, _output);
        await using var server = await e2e.Server.StartMcpServerAsync();
        const string Typed = "typed by run_steps";
        var steps = $$"""
            [
              { "tool": "focus_window" },
              { "tool": "find_ui_elements", "id": "edit", "args": { "automationId": "{{FixtureContract.Ids.EditBox}}" } },
              { "tool": "mouse_click", "args": { "x": "$steps.edit.elements[0].clickPoint.x", "y": "$steps.edit.elements[0].clickPoint.y" } },
              { "tool": "press_keys", "args": { "keys": "ctrl+a" } },
              { "tool": "type_text", "args": { "text": "{{Typed}}" } },
              { "tool": "element_action", "args": { "automationId": "{{FixtureContract.Ids.CheckBox}}", "action": "toggle", "value": "on" } },
              { "tool": "wait_for_element", "args": { "automationId": "{{FixtureContract.Ids.EditBox}}", "condition": "value_equals", "expected": "{{Typed}}", "timeoutMs": 5000 } }
            ]
            """;

        // Act
        var result = await server.CallToolAsync("run_steps", Run(e2e.Fixture.MainWindowHandle, steps), CallTimeout);

        // Assert - the run's own report
        var body = Body(result);
        result.IsError.Should().NotBe(true);
        body["success"]!.GetValue<bool>().Should().BeTrue();
        Statuses(body).Should().AllBe("ok");
        body["steps"]![6]!["result"]!["satisfied"]!.GetValue<bool>().Should().BeTrue("the last step verified the typed text");

        // The fixture's record: the text, then the check box
        var typed = SequenceOf(e2e, Typed);
        var checkedOn = e2e.Fixture.WaitForEvent(FixtureContract.Events.CheckedChanged, e => e.GetBoolean("isChecked") == true).Sequence;
        typed.Should().BeLessThan(checkedOn, "the steps run in order");
        NoModifierIsDown(e2e);

        // Each audited step wrote its own line under "<run's id>#<step>", and the run one of its own
        var audit = Directory.GetFiles(e2e.Server.Get<IAuditLog>().Folder, "*.jsonl")
            .SelectMany(File.ReadAllLines)
            .Select(line => JsonDocument.Parse(line).RootElement)
            .ToList();
        audit.Select(l => l.GetProperty("tool").GetString()).Should().Contain(["mouse_click", "press_keys", "type_text", "element_action", "run_steps"]);
        audit.Where(l => l.GetProperty("tool").GetString() != "run_steps")
            .Select(l => l.GetProperty("requestId").GetString())
            .Should().OnlyContain(id => id != null && (id.EndsWith("#3") || id.EndsWith("#4") || id.EndsWith("#5") || id.EndsWith("#6")));
    }

    [DesktopFact]
    public async Task AFailedStep_StopsTheRun_TheStepsBeforeItStand_AndTheStepsAfterItDoNothing()
    {
        // Arrange
        using var e2e = E2ESession.Start(DesktopTier.Input, _output);
        await using var server = await e2e.Server.StartMcpServerAsync();
        var steps = $$"""
            [
              { "tool": "element_action", "args": { "automationId": "{{FixtureContract.Ids.EditBox}}", "action": "set_value", "value": "before the failure" } },
              { "tool": "element_action", "args": { "automationId": "noSuchButton", "action": "invoke", "timeoutMs": 500 } },
              { "tool": "element_action", "args": { "automationId": "{{FixtureContract.Ids.EditBox}}", "action": "set_value", "value": "after the failure" } }
            ]
            """;

        // Act
        var result = await server.CallToolAsync("run_steps", Run(e2e.Fixture.MainWindowHandle, steps), CallTimeout);

        // Assert
        var body = Body(result);
        result.IsError.Should().BeTrue();
        body["stoppedBy"]!.GetValue<string>().Should().Be("stepFailed");
        Statuses(body).Should().Equal("ok", "failed", "skipped");
        SequenceOf(e2e, "before the failure");
        await Task.Delay(500);
        TextArrived(e2e, "after the failure").Should().BeFalse("a step after the failed one never runs");
    }

    [DesktopFact]
    public async Task ADirectCallMadeDuringTheRun_WaitsForTheRunToEnd_AndNeverLandsBetweenItsSteps()
    {
        // Arrange - the run waits 1.5 s between its two values, so the direct call comes in between
        using var e2e = E2ESession.Start(DesktopTier.Input, _output);
        await using var server = await e2e.Server.StartMcpServerAsync();
        var main = e2e.Fixture.MainWindowHandle;
        var steps = $$"""
            [
              { "tool": "element_action", "args": { "automationId": "{{FixtureContract.Ids.EditBox}}", "action": "set_value", "value": "first" } },
              { "tool": "element_action", "args": { "automationId": "{{FixtureContract.Ids.EditBox}}", "action": "set_value", "value": "second" } }
            ]
            """;

        // Act
        var run = server.CallToolAsync("run_steps", Run(main, steps, delayBetweenStepsMs: 1_500), CallTimeout);
        SequenceOf(e2e, "first");
        var direct = server.CallToolAsync("element_action", new Dictionary<string, object?>
        {
            ["handle"] = (long)main,
            ["automationId"] = FixtureContract.Ids.EditBox,
            ["action"] = "set_value",
            ["value"] = "direct"
        }, CallTimeout);

        // Assert - the direct call got the gate only once the run let it go
        Body(await run)["success"]!.GetValue<bool>().Should().BeTrue();
        Body(await direct)["success"]!.GetValue<bool>().Should().BeTrue();
        var first = SequenceOf(e2e, "first");
        var second = SequenceOf(e2e, "second");
        var directValue = SequenceOf(e2e, "direct");
        first.Should().BeLessThan(second);
        second.Should().BeLessThan(directValue, "no other call's input lands between two steps of a run");
    }

    [DesktopFact]
    public async Task APauseBetweenSteps_StopsTheRunWithPausedByUser_AndNothingAfterItRuns()
    {
        // Arrange
        using var e2e = E2ESession.Start(DesktopTier.Input, _output);
        await using var server = await e2e.Server.StartMcpServerAsync();
        var session = e2e.Server.Get<IInputSession>();
        var steps = $$"""
            [
              { "tool": "element_action", "args": { "automationId": "{{FixtureContract.Ids.EditBox}}", "action": "set_value", "value": "before the pause" } },
              { "tool": "element_action", "args": { "automationId": "{{FixtureContract.Ids.EditBox}}", "action": "set_value", "value": "after the pause" } }
            ]
            """;

        try
        {
            // Act - the user presses the pause hotkey while the run waits between its steps
            var run = server.CallToolAsync("run_steps", Run(e2e.Fixture.MainWindowHandle, steps, delayBetweenStepsMs: 2_000), CallTimeout);
            SequenceOf(e2e, "before the pause");
            session.Pause(InputControlSources.Hotkey);
            var result = await run;

            // Assert
            var body = Body(result);
            body["stoppedBy"]!.GetValue<string>().Should().Be("paused");
            body["errorCode"]!.GetValue<string>().Should().Be("PausedByUser");
            Statuses(body).Should().Equal("ok", "skipped");
            await Task.Delay(500);
            TextArrived(e2e, "after the pause").Should().BeFalse("nothing runs after the pause");
            NoModifierIsDown(e2e);
        }
        finally
        {
            // The test's own pause; an agent never resumes
            session.Resume(InputControlSources.Hotkey);
        }
    }

    [DesktopFact]
    public async Task ARunOutOfTime_StopsWithOperationTimedOut_AndSkipsTheRest()
    {
        // Arrange - a wait for an element that never appears, longer than the run may take
        using var e2e = E2ESession.Start(DesktopTier.Input, _output);
        await using var server = await e2e.Server.StartMcpServerAsync();
        var steps = $$"""
            [
              { "tool": "wait_for_element", "args": { "automationId": "neverThere", "condition": "exists", "timeoutMs": 20000 } },
              { "tool": "element_action", "args": { "automationId": "{{FixtureContract.Ids.EditBox}}", "action": "set_value", "value": "after the time ran out" } }
            ]
            """;

        // Act
        var result = await server.CallToolAsync("run_steps", Run(e2e.Fixture.MainWindowHandle, steps, maxDurationMs: 2_000), CallTimeout);

        // Assert - the wait was cut to the run's time left
        var body = Body(result);
        body["stoppedBy"]!.GetValue<string>().Should().Be("timedOut");
        body["errorCode"]!.GetValue<string>().Should().Be("OperationTimedOut");
        Statuses(body).Should().Equal("failed", "skipped");
        body["elapsedMs"]!.GetValue<long>().Should().BeLessThan(5_000);
        TextArrived(e2e, "after the time ran out").Should().BeFalse();
    }

    [DesktopFact]
    public async Task WhenAnotherWindowTakesTheForeground_TheRunStopsWithTargetNotForeground_AndDoesNotTakeItBack()
    {
        // Arrange - a second fixture, a process of its own, plays the user's other window; the terminal is never touched
        using var e2e = E2ESession.Start(DesktopTier.Input, _output);
        using var other = E2ESession.Start(DesktopTier.Input, _output);
        await using var server = await e2e.Server.StartMcpServerAsync();
        var main = e2e.Fixture.MainWindowHandle;
        var steps = $$"""
            [
              { "tool": "focus_window" },
              { "tool": "element_action", "args": { "automationId": "{{FixtureContract.Ids.EditBox}}", "action": "set_value", "value": "after the user moved" } }
            ]
            """;

        // Act - once the run brought its window to the front, the other fixture takes it during the wait between the steps
        var run = server.CallToolAsync("run_steps", Run(main, steps, delayBetweenStepsMs: 2_000), CallTimeout);
        Wait.Until(() => E2ENative.GetForegroundWindow() == main, SettleTimeout).Should().BeTrue("the run's first step brings its window to the front");
        var moved = other.Server.Foreground.EnsureForeground(other.Fixture.MainWindowHandle);
        moved.IsForeground.Should().BeTrue(moved.Error);
        var result = await run;

        // Assert
        var body = Body(result);
        body["stoppedBy"]!.GetValue<string>().Should().Be("foregroundMoved");
        body["errorCode"]!.GetValue<string>().Should().Be("TargetNotForeground");
        Statuses(body).Should().Equal("ok", "skipped");
        E2ENative.GetForegroundWindow().Should().Be(other.Fixture.MainWindowHandle, "the run never takes the foreground back");
        TextArrived(e2e, "after the user moved").Should().BeFalse();
    }
}
