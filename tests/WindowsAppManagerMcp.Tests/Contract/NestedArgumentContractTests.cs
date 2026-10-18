using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using WindowsAppManagerMcp.Infrastructure;

namespace WindowsAppManagerMcp.Tests.Contract;

/// <summary>
/// Contract: the objects inside a tool's arguments (a batch item, a run_steps step, a launch's expected window and bounds)
/// refuse a member they do not take, as the window selector and the placements do, instead of ignoring it; a
/// set_windows_bounds_batch item without a coordinate fails alone instead of moving its window to 0; and the numbers the
/// server's checks read from the arguments are the numbers the SDK binds. Through the production tool and filter
/// registrations, with mocked services (<see cref="ContractServices"/>).
/// </summary>
public class NestedArgumentContractTests
{
    private const long Notepad = TypicalDesktop.NotepadHandle;

    private static async Task<(CallToolResult Result, ContractServices Services)> CallAsync(string tool, string arguments)
    {
        var services = new ContractServices(ContractMode.Typical);
        await using var server = await InProcessMcpServer.StartAsync(
            [], builder => builder.AddWindowsAppManagerTools(), configureServices: services.Register);
        using var document = JsonDocument.Parse(arguments);
        var parsed = document.RootElement.EnumerateObject().ToDictionary(p => p.Name, p => (object?)p.Value.Clone(), StringComparer.Ordinal);
        return (await server.CallToolAsync(tool, parsed), services);
    }

    private static JsonElement Payload(CallToolResult result) =>
        JsonDocument.Parse(result.Content.OfType<TextContentBlock>().Single().Text).RootElement.Clone();

    // ---- Unknown members are refused ----

    [Theory]
    [InlineData("set_windows_bounds_batch", """{ "placements": [ { "handle": 1706796, "x": 10, "y": 10, "width": 50, "height": 50, "unit": "percent" } ] }""", "unit")]
    [InlineData("set_windows_bounds_batch", """{ "placements": [ { "handle": 1706796, "x": 0, "y": 0, "width": 1280, "height": 1392, "monitor": "left" } ] }""", "monitor")]
    [InlineData("set_windows_bounds_batch", """{ "placements": [ { "handle": 1706796, "x": 0, "y": 0, "width": 1280, "height": 1392, "relativeTo": "monitor" } ] }""", "relativeTo")]
    [InlineData("set_windows_state_batch", """{ "changes": [ { "handle": 1706796, "state": "minimize", "windowHandle": 2821181 } ] }""", "windowHandle")]
    [InlineData("close_windows_batch", """{ "windows": [ { "hwnd": 2821181 } ] }""", "hwnd")]
    [InlineData("launch_applications_batch", """{ "applications": [ { "executable": "code", "args": ["--new-window"] } ] }""", "args")]
    [InlineData("launch_applications_batch", """{ "applications": [ { "executable": "notepad", "expectWindow": { "title": "Untitled" } } ] }""", "title")]
    [InlineData("launch_application", """{ "executable": "notepad", "expectWindow": { "processName": "notepad", "titleEquals": "notes.txt" } }""", "titleEquals")]
    [InlineData("launch_application", """{ "executable": "notepad", "placement": { "bounds": { "x": 0, "y": 0, "width": 50, "height": 50, "unit": "percent" } } }""", "unit")]
    public async Task AMemberAnArgumentsObjectDoesNotTake_IsRefused_AndNothingIsDone(string tool, string arguments, string member)
    {
        var (result, services) = await CallAsync(tool, arguments);

        using (services)
        {
            result.IsError.Should().BeTrue();
            var payload = Payload(result);
            payload.GetProperty("errorCode").GetString().Should().Be(ToolErrorReporting.InvalidArgumentsErrorCode);
            payload.GetProperty("error").GetString().Should().Contain($"'{member}'");
            services.Windows.Invocations.Should().NotContain(i => i.Method.Name.EndsWith("BatchAsync", StringComparison.Ordinal) ||
                                                                  i.Method.Name.StartsWith("Close", StringComparison.Ordinal));
            services.Processes.Invocations.Should().NotContain(i => i.Method.Name.StartsWith("Launch", StringComparison.Ordinal));
        }
    }

    [Fact]
    public async Task TheMembersOfTheArgumentsObjects_StillBindInAnyLetterCase()
    {
        // Disallow refuses unknown members only; a member in another letter case is the member (case-insensitive binding)
        var (result, services) = await CallAsync("set_windows_state_batch", """{ "changes": [ { "Handle": 1706796, "State": "minimize" } ] }""");

        using (services)
        {
            result.IsError.Should().NotBe(true, Payload(result).GetRawText());
            services.Windows.Verify(w => w.SetWindowStateBatchAsync(
                It.Is<IReadOnlyList<(nint, WindowStateRequest)>>(c => c.Count == 1 && c[0].Item1 == (nint)Notepad),
                It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Once);
        }
    }

    [Fact]
    public async Task EveryObjectInAToolsArguments_RefusesUnknownMembers_ExceptTheLayoutPlacementsThatCheckTheirOwn()
    {
        // The input schemas say it as additionalProperties false; save_layout's placements check their own unknown members
        // (InvalidPlacement), so get_layout's output, with members it does not take, goes back in unchanged
        var open = new List<string>();
        foreach (var tool in await ToolSurface.ListToolsAsync())
        {
            Walk(tool!["inputSchema"], $"{tool["name"]}", top: true);
        }

        open.Should().BeEquivalentTo(
            "save_layout.placements[]", "save_layout.placements[].position", "save_layout.placements[].normalPosition",
            "save_layout.placements[].launch");

        void Walk(JsonNode? schema, string path, bool top = false)
        {
            if (schema is not JsonObject node)
            {
                return;
            }

            if (!top && node["properties"] is JsonObject && node["additionalProperties"]?.GetValue<bool>() != false)
            {
                open.Add(path);
            }

            foreach (var (name, property) in node["properties"] as JsonObject ?? [])
            {
                Walk(property, $"{path}.{name}");
            }

            Walk(node["items"], $"{path}[]");
        }
    }

    [Fact]
    public async Task NoInputSchema_OffersNullForARequiredMember()
    {
        // A member that is nullable only so that a missing one is refused alone (a run_steps step's tool, a bounds' x) refuses
        // null too, so its schema says integer or string, not null; a client that checks against the schema never sends it
        var offered = new List<string>();
        foreach (var tool in await ToolSurface.ListToolsAsync())
        {
            Walk(tool!["inputSchema"], $"{tool["name"]}");
        }

        offered.Should().BeEmpty();

        void Walk(JsonNode? schema, string path)
        {
            if (schema is not JsonObject node)
            {
                return;
            }

            var required = (node["required"] as JsonArray ?? []).Select(n => n!.GetValue<string>()).ToHashSet(StringComparer.Ordinal);
            foreach (var (name, property) in node["properties"] as JsonObject ?? [])
            {
                if (required.Contains(name) && property is JsonObject member &&
                    (member["type"] is JsonArray types && types.Any(t => t?.GetValue<string>() == "null") ||
                     member["enum"] is JsonArray values && values.Any(v => v is null)))
                {
                    offered.Add($"{path}.{name}");
                }

                Walk(property, $"{path}.{name}");
            }

            Walk(node["items"], $"{path}[]");
        }
    }

    [Theory]
    [InlineData("run_steps", "steps", "tool", "string")]
    [InlineData("set_windows_bounds_batch", "placements", "x", "integer")]
    [InlineData("set_windows_bounds_batch", "placements", "height", "integer")]
    public async Task ARequiredMemberThatIsRefusedWhenMissing_StaysRequired_WithItsOneType(string toolName, string array, string member, string type)
    {
        var tool = (await ToolSurface.ListToolsAsync()).Single(t => (string)t!["name"]! == toolName)!;
        var item = tool["inputSchema"]!["properties"]![array]!["items"]!;

        item["required"]!.AsArray().Select(n => n!.GetValue<string>()).Should().Contain(member);
        item["properties"]![member]!["type"]!.GetValue<string>().Should().Be(type);
    }

    // ---- set_windows_bounds_batch ----

    [Theory]
    [InlineData("""{ "handle": 1706796, "y": 0, "width": 1280, "height": 1392 }""", "x")]
    [InlineData("""{ "handle": 1706796, "x": 0, "width": 1280, "height": 1392 }""", "y")]
    [InlineData("""{ "handle": 1706796, "x": 0, "y": 0, "height": 1392 }""", "width")]
    [InlineData("""{ "handle": 1706796, "x": 0, "y": 0, "width": 1280, "height": null }""", "height")]
    public async Task SetWindowsBoundsBatch_AnItemWithoutACoordinateOrSize_FailsAlone_InsteadOfMovingItsWindowToZero(string item, string missing)
    {
        var (result, services) = await CallAsync("set_windows_bounds_batch", $$"""
            { "placements": [ {{item}}, { "handle": 2821181, "x": 1280, "y": 0, "width": 1280, "height": 1392 } ] }
            """);

        using (services)
        {
            var payload = Payload(result);
            payload.GetProperty("succeeded").GetInt32().Should().Be(1, payload.GetRawText());
            var failed = payload.GetProperty("results")[0];
            failed.GetProperty("success").GetBoolean().Should().BeFalse();
            failed.GetProperty("errorCode").GetString().Should().Be(ToolErrorReporting.InvalidArgumentsErrorCode);
            failed.GetProperty("error").GetString().Should().Contain("Item 0").And.Contain(missing).And.Contain("Nothing was done");
            services.Windows.Verify(w => w.SetWindowBoundsBatchAsync(
                It.Is<IReadOnlyList<(nint Handle, int, int, int, int, bool)>>(p => p.Count == 1 && p[0].Handle == (nint)2821181),
                It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Once);
        }
    }

    // ---- A launch placement's bounds ----

    [Theory]
    [InlineData("""{ "y": 0, "width": 1280, "height": 1392 }""", "x")]
    [InlineData("""{ "x": 0, "width": 1280, "height": 1392 }""", "y")]
    [InlineData("""{ "x": 0, "y": 0, "height": 1392 }""", "width")]
    [InlineData("""{ "x": 0, "y": 0, "width": 1280, "height": null }""", "height")]
    [InlineData("""{ "width": 1280, "height": 1392 }""", "x or y")]
    public async Task LaunchApplication_PlacementBoundsWithoutACoordinateOrSize_IsRefused_AndNothingIsLaunched(string bounds, string missing)
    {
        var (result, services) = await CallAsync("launch_application", $$"""
            { "executable": "notepad", "placement": { "bounds": {{bounds}} } }
            """);

        using (services)
        {
            result.IsError.Should().BeTrue();
            var payload = Payload(result);
            payload.GetProperty("errorCode").GetString().Should().Be(ToolErrorReporting.InvalidArgumentsErrorCode, payload.GetRawText());
            payload.GetProperty("error").GetString().Should().Contain($"placement.bounds has no {missing}").And.EndWith("Nothing was launched.");
            services.Processes.Invocations.Should().NotContain(i => i.Method.Name.StartsWith("Launch", StringComparison.Ordinal));
            services.Placement.Invocations.Should().BeEmpty();
        }
    }

    [Fact]
    public async Task LaunchApplicationsBatch_AnItemWhosePlacementBoundsMissAMember_FailsAlone_AndTheOthersLaunch()
    {
        var (result, services) = await CallAsync("launch_applications_batch", """
            { "applications": [
                { "executable": "notepad", "placement": { "bounds": { "x": 0, "width": 1280, "height": 1392 } } },
                { "executable": "calc" } ] }
            """);

        using (services)
        {
            var payload = Payload(result);
            var items = payload.GetProperty("results");
            items[0].GetProperty("success").GetBoolean().Should().BeFalse(payload.GetRawText());
            items[0].GetProperty("errorCode").GetString().Should().Be(ToolErrorReporting.InvalidArgumentsErrorCode);
            items[0].GetProperty("error").GetString().Should().Contain("Item 0").And.Contain("placement.bounds has no y")
                .And.EndWith("Nothing was launched for it.");
            items[1].GetProperty("success").GetBoolean().Should().BeTrue(payload.GetRawText());
            services.Processes.Verify(p => p.LaunchApplicationsBatchAsync(
                It.Is<IReadOnlyList<LaunchRequest>>(apps => apps.Count == 1 && apps[0].Executable == "calc"),
                It.IsAny<bool>(), It.IsAny<int>(), It.IsAny<IProgress<OperationProgress>?>(), It.IsAny<CancellationToken>()), Times.Once);
        }
    }

    [Theory]
    [InlineData("launch_application", """{ "executable": "notepad", "placement": { "monitor": true } }""",
        "monitor takes an index (0-based) or a name: 'primary', 'left', 'right', 'current', or an id or name from get_monitors. (at placement.monitor)")]
    [InlineData("take_screenshot", """{ "monitor": true }""",
        "monitor takes an index (0-based) or a name: 'primary', 'left', 'right', 'current', or an id or name from get_monitors.")]
    [InlineData("press_keys", """{ "keys": 5, "windowHandle": 1706796 }""",
        "keys takes a string, such as 'ctrl+shift+t', or an array of strings, such as ['ctrl','shift','t'].")]
    [InlineData("close_windows_batch", """{ "windows": [ { "window": { "processId": "notepad" } } ] }""",
        "windows[0].window.processId does not have the type the input schema gives it.")]
    [InlineData("launch_applications_batch", """{ "applications": [ { "executable": "notepad" }, { "executable": "code", "args": [] } ] }""",
        "applications[1] has a member 'args' that it does not take.")]
    public async Task AnObjectsMemberThatCannotBeBound_IsNamedByItsPlace_AndNothingIsDone(string tool, string arguments, string expected)
    {
        var (result, services) = await CallAsync(tool, arguments);

        using (services)
        {
            var error = Payload(result).GetProperty("error").GetString();
            error.Should().Be($"Invalid arguments for tool '{tool}'. {expected} Check the argument names and types against the tool's " +
                              "input schema. Nothing was done.");
            services.Processes.Invocations.Should().NotContain(i => i.Method.Name.StartsWith("Launch", StringComparison.Ordinal));
            services.Windows.Invocations.Should().NotContain(i => i.Method.Name.StartsWith("Close", StringComparison.Ordinal));
        }
    }

    [Fact]
    public async Task LaunchApplication_PlacementBounds_AreRequiredIntegers_InTheInputSchema()
    {
        var tool = (await ToolSurface.ListToolsAsync()).Single(t => (string)t!["name"]! == "launch_application")!;
        var bounds = tool["inputSchema"]!["properties"]!["placement"]!["properties"]!["bounds"]!;

        bounds["required"]!.AsArray().Select(n => n!.GetValue<string>()).Should().BeEquivalentTo("x", "y", "width", "height");
        foreach (var member in new[] { "x", "y", "width", "height" })
        {
            bounds["properties"]![member]!["type"]!.GetValue<string>().Should().Be("integer", member);
        }

        bounds["additionalProperties"]!.GetValue<bool>().Should().BeFalse();
    }

    [Fact]
    public async Task SetWindowsBoundsBatch_SaysItTakesPixelsOnly()
    {
        var tool = (await ToolSurface.ListToolsAsync()).Single(t => (string)t!["name"]! == "set_windows_bounds_batch")!;

        tool["description"]!.GetValue<string>().Should().Contain("pixels").And.Contain("no unit")
            .And.NotContain("as set_window_bounds does");
    }

    // ---- The numbers the checks read are the numbers the SDK binds ----

    [Theory]
    [InlineData("1706796")]
    [InlineData("\"1706796\"")]
    [InlineData("\"+1706796\"")]
    [InlineData("\"-5\"")]
    [InlineData("\"0001706796\"")]
    [InlineData("\" 1706796\"")]
    [InlineData("\"1706796 \"")]
    [InlineData("\"1706796.0\"")]
    [InlineData("\"1e6\"")]
    [InlineData("\"0x1A0B2C\"")]
    [InlineData("\"\"")]
    [InlineData("\"9223372036854775808\"")]
    [InlineData("1706796.0")]
    [InlineData("1e6")]
    [InlineData("1706796.5")]
    [InlineData("true")]
    [InlineData("[1706796]")]
    [InlineData("{ \"handle\": 1706796 }")]
    public async Task BoundInteger_ReadsExactlyWhatTheSdkBindsToALongParameter(string json)
    {
        // Arrange
        await using var server = await InProcessMcpServer.StartAsync([], builder => builder.WithServerTools(new BindingProbe()));
        var value = JsonSerializer.Deserialize<JsonElement>(json);

        // Act
        var bound = await server.CallToolAsync("binding_probe", new Dictionary<string, object?> { ["windowHandle"] = value });
        var read = BoundInteger.Read(value);

        // Assert
        var text = bound.Content.OfType<TextContentBlock>().Single().Text;
        var sdk = bound.IsError == true ? null : (long?)long.Parse(text, CultureInfo.InvariantCulture);
        read.Should().Be(sdk, "BoundInteger.Read({0}) must agree with the SDK, which returned: {1}", json, text);
        BoundInteger.Read(JsonNode.Parse(json)).Should().Be(sdk, "the JsonNode overload reads the same");
    }

    [Fact]
    public void TheServersSerializerOptions_ReadNumbersFromStrings_AndMembersInAnyLetterCase()
    {
        // Why BoundInteger exists, and why nested lookups ignore case: the SDK's defaults, which the server keeps
        ToolJson.SerializerOptions.NumberHandling.Should().HaveFlag(System.Text.Json.Serialization.JsonNumberHandling.AllowReadingFromString);
        ToolJson.SerializerOptions.PropertyNameCaseInsensitive.Should().BeTrue();
    }

    private sealed class BindingProbe
    {
        [McpServerTool(Name = "binding_probe")]
        public static string Probe(long? windowHandle = null) =>
            windowHandle?.ToString(CultureInfo.InvariantCulture) ?? "unbound";
    }
}
