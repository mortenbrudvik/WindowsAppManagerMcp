using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace WindowsAppManagerMcp.E2ETests.Scenarios;

/// <summary>
/// The scenario evaluation (roadmap M7.6): real desktop tasks done the way an agent does them, through the in-process MCP
/// server (<see cref="ServerHarness.StartMcpServerAsync"/>), each measured for success, tool calls, wall time and response
/// size, compared with the previous run and written as a JSON file (<see cref="ScenarioReport"/>). Input tier: the scenarios
/// act on the fixture, and one on a Calculator the scenario starts itself and ends by its process ID; nothing else on the
/// desktop is touched. Run them while nobody uses the keyboard or mouse.
/// </summary>
public class ScenarioEvaluationTests : IClassFixture<ScenarioReport>
{
    private const string CalculatorAppId = "Microsoft.WindowsCalculator_8wekyb3d8bbwe!App";
    private const string CalculatorProcessName = "CalculatorApp";
    private const string Typed = "filled by the scenario";

    private readonly ScenarioReport _report;
    private readonly ITestOutputHelper _output;

    public ScenarioEvaluationTests(ScenarioReport report, ITestOutputHelper output)
    {
        _report = report;
        _output = output;
    }

    private static Dictionary<string, object?> Window(nint handle) => new() { ["handle"] = (long)handle };

    private static Dictionary<string, object?> Selector(nint handle) => new() { ["handle"] = (long)handle };

    private static bool IsTrue(JsonObject body, string property) =>
        body[property] is JsonValue value && value.TryGetValue<bool>(out var flag) && flag;

    [DesktopFact]
    public async Task FormFill_WithDirectCalls()
    {
        // Arrange
        using var e2e = E2ESession.Start(DesktopTier.Input, _output);
        await using var server = await e2e.Server.StartMcpServerAsync();
        var main = e2e.Fixture.MainWindowHandle;

        // Act - one call per step: focus, the text, the check box, and a wait that verifies the text
        var measure = await _report.RunAsync("form_fill_direct", server, _output, async scenario =>
        {
            await scenario.CallAsync("focus_window", Window(main));
            await scenario.CallAsync("element_action", new(Window(main))
            {
                ["automationId"] = FixtureContract.Ids.EditBox, ["action"] = "set_value", ["value"] = Typed
            });
            await scenario.CallAsync("element_action", new(Window(main))
            {
                ["automationId"] = FixtureContract.Ids.CheckBox, ["action"] = "toggle", ["value"] = "on"
            });
            var waited = await scenario.CallAsync("wait_for_element", new()
            {
                ["window"] = Selector(main), ["automationId"] = FixtureContract.Ids.EditBox, ["condition"] = "value_equals",
                ["expected"] = Typed, ["timeoutMs"] = 5000
            });
            return IsTrue(waited, "satisfied");
        });

        // Assert
        measure.Success.Should().BeTrue(measure.Error);
        e2e.Fixture.WaitForEvent(FixtureContract.Events.CheckedChanged, e => e.GetBoolean("isChecked") == true);
    }

    [DesktopFact]
    public async Task FormFill_WithRunSteps()
    {
        // Arrange
        using var e2e = E2ESession.Start(DesktopTier.Input, _output);
        await using var server = await e2e.Server.StartMcpServerAsync();
        var main = e2e.Fixture.MainWindowHandle;
        var steps = $$"""
            [
              { "tool": "focus_window" },
              { "tool": "element_action", "args": { "automationId": "{{FixtureContract.Ids.EditBox}}", "action": "set_value", "value": "{{Typed}}" } },
              { "tool": "element_action", "args": { "automationId": "{{FixtureContract.Ids.CheckBox}}", "action": "toggle", "value": "on" } },
              { "tool": "wait_for_element", "args": { "automationId": "{{FixtureContract.Ids.EditBox}}", "condition": "value_equals", "expected": "{{Typed}}", "timeoutMs": 5000 } }
            ]
            """;

        // Act - the same form in one call (roadmap M6.2)
        var measure = await _report.RunAsync("form_fill_run_steps", server, _output, async scenario =>
        {
            var run = await scenario.CallAsync("run_steps", new()
            {
                ["window"] = Selector(main), ["steps"] = JsonSerializer.Deserialize<JsonElement>(steps)
            });
            return IsTrue(run, "success");
        });

        // Assert
        measure.Success.Should().BeTrue(measure.Error);
        measure.Calls.Should().Be(1);
    }

    [DesktopFact]
    public async Task ADialog_IsNoticedThroughTheEventJournal_AnsweredAndGone()
    {
        // Arrange
        using var e2e = E2ESession.Start(DesktopTier.Input, _output);
        await using var server = await e2e.Server.StartMcpServerAsync();
        var main = e2e.Fixture.MainWindowHandle;

        // Act - the journal before the action, the action, what came up, the answer, and the wait for it to go (roadmap M7.2)
        var measure = await _report.RunAsync("dialog_notice_and_answer", server, _output, async scenario =>
        {
            var before = await scenario.CallAsync("get_window_events", new());
            await scenario.CallAsync("element_action", new(Window(main))
            {
                ["automationId"] = FixtureContract.Ids.OpenModalButton, ["action"] = "invoke"
            });
            var since = before["nextSeq"]!.GetValue<long>();
            JsonObject events = [];
            for (var attempt = 0; attempt < 10; attempt++)
            {
                events = await scenario.CallAsync("get_window_events", new() { ["sinceSeq"] = since, ["types"] = new[] { "shown" } });
                if (events["events"]!.AsArray().Any(e => e!["title"]!.GetValue<string>().StartsWith(FixtureContract.ModalTitlePrefix, StringComparison.Ordinal)))
                {
                    break;
                }

                await Task.Delay(100);
            }

            var noticed = events["events"]!.AsArray()
                .Any(e => e!["title"]!.GetValue<string>().StartsWith(FixtureContract.ModalTitlePrefix, StringComparison.Ordinal));
            var answered = await scenario.CallAsync("respond_to_dialog", new(Window(main)) { ["button"] = "Cancel" });
            var gone = await scenario.CallAsync("wait_for_window", new()
            {
                ["window"] = new Dictionary<string, object?> { ["titleContains"] = FixtureContract.ModalTitlePrefix, ["processName"] = FixtureContract.ProcessName },
                ["condition"] = "gone",
                ["timeoutMs"] = 5000
            });
            return noticed && IsTrue(answered, "success") && IsTrue(gone, "satisfied");
        });

        // Assert
        measure.Success.Should().BeTrue(measure.Error);
        e2e.Fixture.WaitForEvent(FixtureContract.Events.ModalClosed, e => e.GetString("result") == "cancel");
    }

    [DesktopFact]
    public async Task ALayout_IsSaved_TheWindowMoved_AndTheLayoutPutsItBack()
    {
        // Arrange - the harness's own layouts folder, never the user's
        using var e2e = E2ESession.Start(DesktopTier.Input, _output);
        await using var server = await e2e.Server.StartMcpServerAsync();
        var main = e2e.Fixture.MainWindowHandle;

        // Act
        var measure = await _report.RunAsync("layout_round_trip", server, _output, async scenario =>
        {
            var saved = await scenario.CallAsync("save_layout", new()
            {
                ["name"] = "scenario-round-trip", ["includeProcesses"] = FixtureContract.ProcessName
            });
            var before = await scenario.CallAsync("get_window_info", Window(main));
            await scenario.CallAsync("set_window_bounds", new(Window(main))
            {
                ["x"] = before["visibleBounds"]!["x"]!.GetValue<int>() + 120, ["y"] = before["visibleBounds"]!["y"]!.GetValue<int>() + 80,
                ["width"] = 700, ["height"] = 500
            });
            var applied = await scenario.CallAsync("apply_layout", new() { ["name"] = "scenario-round-trip" });
            var after = await scenario.CallAsync("get_window_info", Window(main));
            var back = JsonNode.DeepEquals(before["visibleBounds"], after["visibleBounds"]);
            _output.WriteLine($"Visible frame before: {before["visibleBounds"]}; after the layout: {after["visibleBounds"]}");
            return IsTrue(saved, "success") && IsTrue(applied, "success") && back;
        });

        // Assert
        measure.Success.Should().BeTrue(measure.Error);
    }

    [DesktopFact]
    public async Task Calculator_AddsThroughUiAutomation()
    {
        // Arrange - never touch a Calculator the user has open
        RequireNoCalculatorRunning();
        using var e2e = E2ESession.Start(DesktopTier.Input, _output, allowedActivations: [CalculatorAppId],
            uiaActionProcesses: [CalculatorProcessName]);
        await using var server = await e2e.Server.StartMcpServerAsync();
        var launchedAt = DateTime.UtcNow;
        int? processId = null;

        try
        {
            // Act - launch, 12 + 30 =, and read the display (roadmap M6.5, M3.6, M3.9)
            var measure = await _report.RunAsync("calculator_add", server, _output, async scenario =>
            {
                var launched = await scenario.CallAsync("launch_application", new()
                {
                    ["executable"] = CalculatorAppId, ["waitForWindow"] = true, ["waitTimeoutMs"] = 15000
                });
                processId = launched["processId"]?.GetValue<int>();
                var window = launched["window"]!["handle"]!.GetValue<long>();
                foreach (var button in new[] { "num1Button", "num2Button", "plusButton", "num3Button", "num0Button", "equalButton" })
                {
                    var pressed = await scenario.CallAsync("element_action", new()
                    {
                        ["handle"] = window, ["automationId"] = button, ["action"] = "invoke"
                    });
                    if (!IsTrue(pressed, "success"))
                    {
                        return false;
                    }
                }

                var display = await scenario.CallAsync("read_element_text", new()
                {
                    ["window"] = new Dictionary<string, object?> { ["handle"] = window }, ["automationId"] = "CalculatorResults"
                });
                return display["text"]?.GetValue<string>()?.Contains("42", StringComparison.Ordinal) == true;
            });

            // Assert
            measure.Success.Should().BeTrue(measure.Error);
        }
        finally
        {
            if (processId is { } started)
            {
                EndCalculatorThisTestStarted(started, launchedAt);
            }
        }
    }

    /// <summary>Fails the scenario, starting nothing, when a Calculator runs already: its windows are the user's.</summary>
    private static void RequireNoCalculatorRunning()
    {
        var running = Process.GetProcessesByName(CalculatorProcessName);
        foreach (var process in running)
        {
            process.Dispose();
        }

        if (running.Length > 0)
        {
            throw new InvalidOperationException(
                "Calculator is running: close it first. This scenario starts Calculator and ends only the one it started.");
        }
    }

    /// <summary>Ends the Calculator this scenario started, by its process ID, only when it is a CalculatorApp started since.</summary>
    private void EndCalculatorThisTestStarted(int processId, DateTime launchedAtUtc)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            if (!string.Equals(process.ProcessName, CalculatorProcessName, StringComparison.OrdinalIgnoreCase) ||
                process.StartTime.ToUniversalTime() < launchedAtUtc.AddSeconds(-1))
            {
                _output.WriteLine($"Process {processId} is not the Calculator this scenario started; it was left running.");
                return;
            }

            process.Kill();
            process.WaitForExit(5000);
        }
        catch (ArgumentException)
        {
            // It exited already
        }
    }
}
