using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Protocol;
using WindowsAppManagerMcp.Infrastructure;

namespace WindowsAppManagerMcp.Tests.Contract;

/// <summary>
/// Contract: the audit log of input and destructive actions (roadmap M2.17). Every call of an input, close, kill,
/// launch, layout-changing, scroll or clipboard-writing tool appends one line through the production filter
/// registration, with no typed text, keys or other string argument in it; read-only tools are not logged; and a log
/// that cannot be written never breaks a call. Mocked services (<see cref="ContractServices"/>), and an audit folder in
/// the temporary folder.
/// </summary>
public class AuditLogContractTests
{
    /// <summary>The captures are destructive only through overwrite:true; they observe, and are not audited.</summary>
    private static readonly string[] Captures = ["take_screenshot", "capture_region", "capture_window", "capture_with_elements"];

    public static TheoryData<string> ServedAuditedTools()
    {
        var data = new TheoryData<string>();
        foreach (var name in ServerToolCatalog.ServedToolNames.Where(AuditLogToolFilter.AuditedTools.Contains).Order(StringComparer.Ordinal))
        {
            data.Add(name);
        }

        return data;
    }

    private static async Task<JsonArray> ToolsAsync() => await ToolSurface.ListToolsAsync();

    private static bool Hint(JsonNode tool, string hint) => tool["annotations"]![hint]!.GetValue<bool>();

    private static async Task<(CallToolResult Result, IReadOnlyList<JsonElement> Lines)> CallAsync(
        string tool, IReadOnlyDictionary<string, object?> arguments, Action<ContractServices>? arrange = null,
        ContractMode mode = ContractMode.Typical)
    {
        using var services = new ContractServices(mode);
        arrange?.Invoke(services);
        CallToolResult result;
        await using (var server = await InProcessMcpServer.StartAsync(
                         [], builder => builder.AddWindowsAppManagerTools(), configureServices: services.Register))
        {
            result = await server.CallToolAsync(tool, arguments);
        }

        return (result, services.AuditLines());
    }

    [Fact]
    public void TheAuditedTools_AreTheInputCloseKillLaunchLayoutScrollAndClipboardTools()
    {
        AuditLogToolFilter.AuditedTools.Should().BeEquivalentTo(
            "click", "right_click", "double_click", "mouse_move", "type_text", "send_keys", "scroll", "release_all_input",
            "element_action", "respond_to_dialog", "file_dialog", "close_window", "close_windows_batch", "close_application", "kill_process",
            "launch_application", "launch_applications_batch", "launch_browser_with_debug",
            "apply_layout", "save_layout", "delete_layout", "set_clipboard", "get_clipboard");
    }

    [Fact]
    public void EveryInputTool_IsAudited()
    {
        AuditLogToolFilter.AuditedTools.Should().Contain(InputGateToolFilter.InputTools);
    }

    [Fact]
    public async Task EveryDestructiveTool_IsAudited_ExceptTheCaptures()
    {
        var destructive = (await ToolsAsync()).Where(t => Hint(t!, "destructiveHint")).Select(t => t!["name"]!.GetValue<string>());

        destructive.Except(Captures).Should().BeSubsetOf(AuditLogToolFilter.AuditedTools,
            "a tool that can lose work or act for the user must leave a trace; add it to AuditLogToolFilter.AuditedTools");
    }

    [Fact]
    public async Task NoReadOnlyTool_IsAudited_ButGetClipboard()
    {
        // get_clipboard is the one read that can return the user's own clipboard (review PRIVA-4)
        var readOnly = (await ToolsAsync()).Where(t => Hint(t!, "readOnlyHint")).Select(t => t!["name"]!.GetValue<string>());

        readOnly.Intersect(AuditLogToolFilter.AuditedTools).Should().Equal("get_clipboard");
    }

    [Fact]
    public async Task GetClipboard_RecordsTheFormatOriginAndLength_NeverTheContent()
    {
        // Act
        var (result, lines) = await CallAsync("get_clipboard", new Dictionary<string, object?> { ["format"] = "text" });

        // Assert
        result.IsError.Should().NotBe(true);
        var line = lines.Single();
        line.GetProperty("args").GetProperty("format").GetString().Should().Be("text");
        line.GetProperty("result").GetProperty("origin").GetString().Should().Be(nameof(ClipboardContentOrigin.AgentInput));
        line.GetProperty("result").GetProperty("length").GetInt32().Should().Be(TypicalDesktop.CopiedParagraph.Length);
        line.GetRawText().Should().NotContain(TypicalDesktop.CopiedParagraph[..20]);
    }

    [Fact]
    public void EveryAuditedTool_IsServed()
    {
        AuditLogToolFilter.AuditedTools.Should().BeSubsetOf(ServerToolCatalog.ServedToolNames, "an audited name that is no tool is a typo");
    }

    [Theory]
    [MemberData(nameof(ServedAuditedTools))]
    public async Task TheTypicalCall_OfAnAuditedTool_WritesOneLine_WithoutItsStringArguments(string tool)
    {
        // Arrange
        var arguments = EveryToolContractTests.ArgumentsFor(tool);

        // Act
        var (result, lines) = await CallAsync(tool, arguments);

        // Assert
        var line = lines.Should().ContainSingle("one call is one line").Subject;
        line.GetProperty("tool").GetString().Should().Be(tool);
        line.GetProperty("outcome").GetString().Should().Be(result.IsError == true ? AuditOutcome.Error : AuditOutcome.Ok);
        line.GetProperty("durationMs").GetInt64().Should().BeGreaterThanOrEqualTo(0);
        var text = line.GetRawText();
        foreach (var value in UnnamedStrings(arguments).Where(v => v.Length >= 4))
        {
            text.Should().NotContain(value, "the audit log never holds a string argument of '{0}' (only its length)", tool);
        }
    }

    [Theory]
    [MemberData(nameof(ServedTools))]
    public async Task AToolThatIsNotAudited_WritesNothing(string tool)
    {
        if (AuditLogToolFilter.AuditedTools.Contains(tool))
        {
            return;
        }

        var (_, lines) = await CallAsync(tool, EveryToolContractTests.ArgumentsFor(tool));

        lines.Should().BeEmpty("'{0}' is not audited", tool);
    }

    public static TheoryData<string> ServedTools() => EveryToolContractTests.ServedTools();

    [Fact]
    public async Task ElementAction_RecordsTheRefActionAndElementsWindow_AndTheValuesLengthOnly()
    {
        // Act
        var (_, lines) = await CallAsync("element_action", new Dictionary<string, object?>
        {
            ["ref"] = "w4120035e17", ["action"] = "set_value", ["value"] = "hunter2 is the password", ["mode"] = "type"
        });

        // Assert
        var line = lines.Single();
        line.GetRawText().Should().NotContain("hunter2");
        var args = line.GetProperty("args");
        args.GetProperty("valueLength").GetInt32().Should().Be(23);
        args.GetProperty("ref").GetString().Should().Be("w4120035e17");
        args.GetProperty("action").GetString().Should().Be("set_value");
        args.GetProperty("mode").GetString().Should().Be("type");
        line.GetProperty("target").GetProperty("handle").GetInt64().Should().Be(TypicalDesktop.NotepadHandle);
        line.GetProperty("target").GetProperty("process").GetString().Should().Be("notepad", "the tool notes the element's window before it acts");
        line.GetProperty("result").GetProperty("outcomeVerified").GetBoolean().Should().BeTrue();
        line.GetProperty("result").GetProperty("method").GetString().Should().BeEquivalentTo("value",
            "an audit reader sees whether the call used a pattern or moved the pointer or typed");
        line.GetProperty("result").GetProperty("status").GetString().Should().BeEquivalentTo("done");
        line.GetRawText().Should().NotContain("window layout", "the values in before and after are never logged");
    }

    [Fact]
    public async Task ElementAction_RecordsObserve_ButNotTheObservation()
    {
        // Act - roadmap M3.12: the observation after the action holds window titles and element names
        var (_, lines) = await CallAsync("element_action", new Dictionary<string, object?>
        {
            ["ref"] = "w4120035e17", ["action"] = "invoke", ["observe"] = "state"
        });

        // Assert
        var line = lines.Single();
        line.GetProperty("args").GetProperty("observe").GetString().Should().Be("state");
        line.GetRawText().Should().NotContain("Problems").And.NotContain("Google Chrome", "the observed elements and windows are never logged");
    }

    [Fact]
    public async Task RespondToDialog_RecordsTheButton_AndWhetherTheDialogClosed()
    {
        var (_, lines) = await CallAsync("respond_to_dialog", EveryToolContractTests.ArgumentsFor("respond_to_dialog"));

        var line = lines.Single();
        line.GetProperty("args").GetProperty("button").GetString().Should().Be("no");
        line.GetProperty("target").GetProperty("handle").GetInt64().Should().Be(TypicalDesktop.NotepadHandle);
        line.GetProperty("result").GetProperty("dialogClosed").GetBoolean().Should().BeTrue();
        line.GetRawText().Should().NotContain("save changes", "the dialog's text is never logged");
    }

    [Fact]
    public async Task FileDialog_RecordsThePathsLengthOnly_AndWhetherItCompleted()
    {
        var (_, lines) = await CallAsync("file_dialog", EveryToolContractTests.ArgumentsFor("file_dialog"));

        var line = lines.Single();
        line.GetRawText().Should().NotContain("meeting-notes", "a file name can say what the user works on");
        var args = line.GetProperty("args");
        args.GetProperty("pathLength").GetInt32().Should().Be(TypicalDesktop.SavePath.Length);
        args.GetProperty("action").GetString().Should().Be("save");
        args.GetProperty("overwrite").GetBoolean().Should().BeTrue();
        line.GetProperty("result").GetProperty("completed").GetBoolean().Should().BeTrue();
        line.GetProperty("result").GetProperty("promptHandled").GetBoolean().Should().BeTrue();
    }

    [Fact]
    public async Task TypeText_RecordsTheTargetAndReceivingWindow_AndTheTextsLengthOnly()
    {
        // Act
        var (_, lines) = await CallAsync("type_text", new Dictionary<string, object?>
        {
            ["text"] = "hunter2 is the password", ["windowHandle"] = TypicalDesktop.NotepadHandle
        });

        // Assert
        var line = lines.Single();
        line.GetRawText().Should().NotContain("hunter2");
        line.GetProperty("args").GetProperty("textLength").GetInt32().Should().Be(23);
        line.GetProperty("target").GetProperty("title").GetString().Should().Be(TypicalDesktop.Notepad.Title, "the target is read before the call");
        line.GetProperty("target").GetProperty("process").GetString().Should().Be("notepad");
        line.GetProperty("receivingWindow").GetProperty("handle").GetInt64().Should().Be(TypicalDesktop.NotepadHandle);
        line.GetProperty("receivingWindow").GetProperty("process").GetString().Should().Be("notepad");
        line.GetProperty("result").GetProperty("charactersTyped").GetInt32().Should().Be(23);
    }

    /// <summary>The audited tools whose typical call aims at nothing a line can name: no window, process, program or item.</summary>
    private static readonly string[] Untargeted = ["release_all_input", "save_layout", "delete_layout", "set_clipboard", "get_clipboard"]; // the clipboard tools act on no window; get_clipboard records the origin and length instead

    [Theory]
    [MemberData(nameof(ServedAuditedTools))]
    public async Task TheTypicalCall_OfAnAuditedTool_NamesWhatItActedOn(string tool)
    {
        if (Untargeted.Contains(tool))
        {
            return;
        }

        var (_, lines) = await CallAsync(tool, EveryToolContractTests.ArgumentsFor(tool));

        var line = lines.Single();
        var named = new[] { "target", "items", "receivingWindow" }.Where(p => line.TryGetProperty(p, out _)).ToList();
        named.Should().NotBeEmpty("the line of '{0}' must say what it acted on: {1}", tool, line.GetRawText());
    }

    [Fact]
    public async Task CloseApplication_RecordsEachWindowItClosed_WithItsTitleProcessAndStatus()
    {
        var (_, lines) = await CallAsync("close_application", new Dictionary<string, object?> { ["processName"] = "notepad" });

        var items = lines.Single().GetProperty("items").EnumerateArray().ToList();
        items.Should().NotBeEmpty();
        items.Should().OnlyContain(i => i.GetProperty("process").GetString() == "notepad" && i.GetProperty("processId").GetInt32() > 0);
        items.Select(i => i.GetProperty("title").GetString()).Should().Contain(TypicalDesktop.Notepad.Title);
        items.Should().OnlyContain(i => i.GetProperty("status").GetString() == "closed");
    }

    [Fact]
    public async Task ApplyLayout_RecordsEachPlacementsWindow()
    {
        var (_, lines) = await CallAsync("apply_layout", EveryToolContractTests.ArgumentsFor("apply_layout"));

        var items = lines.Single().GetProperty("items").EnumerateArray().ToList();
        items.Should().NotBeEmpty();
        items.Should().OnlyContain(i => i.GetProperty("handle").GetInt64() != 0 && i.GetProperty("status").GetString() == "applied");
        items[0].GetProperty("title").GetString().Should().Be(TypicalDesktop.Windows[0].Title);
    }

    [Fact]
    public async Task ACloseNamedByASelector_RecordsTheResolvedWindowsTitleAndProcess()
    {
        var (_, lines) = await CallAsync("close_window", new Dictionary<string, object?>
        {
            ["window"] = new Dictionary<string, object?> { ["processName"] = "notepad" }
        });

        var target = lines.Single().GetProperty("target");
        target.GetProperty("handle").GetInt64().Should().Be(TypicalDesktop.NotepadHandle);
        target.GetProperty("title").GetString().Should().Be(TypicalDesktop.Notepad.Title, "the tool notes the window before it closes it");
        target.GetProperty("process").GetString().Should().Be("notepad");
    }

    [Fact]
    public async Task ANumberSentForTheText_IsRecordedAsItsLength_FromTheToolsSchema()
    {
        // A PIN sent as a JSON number fails binding (InvalidArguments) and is still audited: never its digits
        var (_, lines) = await CallAsync("type_text", new Dictionary<string, object?>
        {
            ["text"] = 482913, ["windowHandle"] = TypicalDesktop.NotepadHandle, ["delayMs"] = 5
        });

        var line = lines.Single();
        line.GetRawText().Should().NotContain("482913");
        var args = line.GetProperty("args");
        args.GetProperty("textLength").GetInt32().Should().Be(6);
        args.TryGetProperty("text", out _).Should().BeFalse();
        args.GetProperty("windowHandle").GetInt64().Should().Be(TypicalDesktop.NotepadHandle, "the tool declares it an integer");
        args.GetProperty("delayMs").GetInt32().Should().Be(5);
    }

    [Fact]
    public async Task AnArgumentTheToolDoesNotDeclare_IsOnlyCounted()
    {
        var arguments = new Dictionary<string, object?>(EveryToolContractTests.ArgumentsFor("click")) { ["password=hunter2"] = 1 };

        var (_, lines) = await CallAsync("click", arguments);

        var line = lines.Single();
        line.GetRawText().Should().NotContain("hunter2").And.NotContain("password");
        line.GetProperty("args").GetProperty("unknownArgumentsCount").GetInt32().Should().Be(1);
    }

    [Fact]
    public async Task ALaunchedUrl_KeepsOnlyItsSchemeAndHost()
    {
        const string webhook = "https://hooks.example.com/services/T0/B0/SECRETxyz";

        var (_, lines) = await CallAsync("launch_application", new Dictionary<string, object?> { ["executable"] = webhook });

        var line = lines.Single();
        line.GetRawText().Should().NotContain("SECRET").And.NotContain("services");
        line.GetProperty("args").GetProperty("executable").GetString().Should().Be("https://hooks.example.com");
        line.GetProperty("args").GetProperty("executableLength").GetInt32().Should().Be(webhook.Length);
        line.GetProperty("target").GetProperty("executable").GetString().Should().Be("https://hooks.example.com");
    }

    /// <summary>The placement service places every window at the left half and reports it in front (roadmap M3.13).</summary>
    private static void PlacesAtTheLeftHalf(ContractServices services) =>
        services.Placement.Setup(p => p.PlaceAsync(It.IsAny<IReadOnlyList<(nint, LaunchPlacementPlan)>>(), It.IsAny<CancellationToken>()))
            .Returns<IReadOnlyList<(nint Handle, LaunchPlacementPlan Plan)>, CancellationToken>((placements, _) =>
                Task.FromResult<IReadOnlyList<LaunchPlacementOutcome>>(placements.Select(p => new LaunchPlacementOutcome(p.Handle,
                    TypicalDesktop.Notepad with { Handle = p.Handle, VisibleBounds = new WindowRect(0, 0, 960, 1040) },
                    Focused: true, Reapplied: true)).ToList()));

    [Fact]
    public async Task ALaunchsPlacement_IsAudited_WithWhatWasAskedAndWhatItDid()
    {
        var (_, lines) = await CallAsync("launch_application", new Dictionary<string, object?>
        {
            ["executable"] = "notepad",
            ["reuseExisting"] = true,
            ["placement"] = new Dictionary<string, object?> { ["snap"] = "Left-Half", ["monitorIndex"] = 0, ["focus"] = true }
        }, PlacesAtTheLeftHalf);

        var line = lines.Single();
        line.GetProperty("args").GetProperty("reuseExisting").GetBoolean().Should().BeTrue();
        line.GetProperty("result").GetProperty("launchStatus").GetString().Should().Be("windowObserved");
        var placement = line.GetProperty("target").GetProperty("placement");
        placement.GetProperty("snap").GetString().Should().Be("left_half", "the canonical name, never the text as sent");
        placement.GetProperty("monitorIndex").GetInt32().Should().Be(0);
        placement.GetProperty("focus").GetBoolean().Should().BeTrue();
        placement.GetProperty("finalBounds").GetProperty("width").GetInt32().Should().Be(960);
        placement.GetProperty("finalState").GetString().Should().Be("normal");
        placement.GetProperty("focused").GetBoolean().Should().BeTrue();
        placement.GetProperty("reapplied").GetBoolean().Should().BeTrue();
    }

    [Fact]
    public async Task ABatchItemsPlacement_IsAuditedWithTheItem_AndItsLaunchStatus()
    {
        var (_, lines) = await CallAsync("launch_applications_batch", new Dictionary<string, object?>
        {
            ["applications"] = new object?[]
            {
                new Dictionary<string, object?> { ["executable"] = "notepad" },
                new Dictionary<string, object?>
                {
                    ["executable"] = "code",
                    ["placement"] = new Dictionary<string, object?>
                    {
                        ["bounds"] = new Dictionary<string, object?> { ["x"] = 0, ["y"] = 0, ["width"] = 960, ["height"] = 1040 }
                    }
                }
            }
        }, PlacesAtTheLeftHalf);

        var items = lines.Single().GetProperty("items").EnumerateArray().ToList();
        items.Should().HaveCount(2);
        items[0].TryGetProperty("placement", out _).Should().BeFalse();
        items[0].GetProperty("status").GetString().Should().Be("windowObserved");
        items[1].GetProperty("placement").GetProperty("bounds").GetProperty("height").GetInt32().Should().Be(1040);
        items[1].GetProperty("placement").GetProperty("reapplied").GetBoolean().Should().BeTrue();
    }

    [Fact]
    public async Task ARefusedPlacement_KeepsNoTextItWasGiven()
    {
        var (result, lines) = await CallAsync("launch_application", new Dictionary<string, object?>
        {
            ["executable"] = "notepad",
            ["placement"] = new Dictionary<string, object?> { ["state"] = "hunter2", ["snap"] = "left_half" }
        });

        result.IsError.Should().BeTrue();
        var line = lines.Single();
        line.GetRawText().Should().NotContain("hunter2");
        line.GetProperty("errorCode").GetString().Should().Be(nameof(WindowErrorCode.InvalidState));
        line.GetProperty("target").GetProperty("placement").GetProperty("snap").GetString().Should().Be("left_half");
    }

    [Fact]
    public async Task ACallRefusedWhileInputIsPaused_IsAuditedAsAnError()
    {
        var (result, lines) = await CallAsync("close_window", new Dictionary<string, object?> { ["handle"] = TypicalDesktop.NotepadHandle },
            services => services.Session.Pause(InputControlSources.Hotkey));

        result.IsError.Should().BeTrue();
        var line = lines.Single();
        line.GetProperty("outcome").GetString().Should().Be(AuditOutcome.Error);
        line.GetProperty("errorCode").GetString().Should().Be("PausedByUser");
        line.GetProperty("target").GetProperty("title").GetString().Should().Be(TypicalDesktop.Notepad.Title);
    }

    [Fact]
    public async Task ACallWithInvalidArguments_IsAuditedWithInvalidArguments()
    {
        var (_, lines) = await CallAsync("click", new Dictionary<string, object?> { ["x"] = "left" });

        lines.Single().GetProperty("errorCode").GetString().Should().Be(ToolErrorReporting.InvalidArgumentsErrorCode);
    }

    [Fact]
    public async Task AFailedCall_RecordsItsErrorCode()
    {
        var (_, lines) = await CallAsync("kill_process", EveryToolContractTests.ArgumentsFor("kill_process"), mode: ContractMode.Failing);

        var line = lines.Single();
        line.GetProperty("outcome").GetString().Should().Be(AuditOutcome.Error);
        line.GetProperty("errorCode").GetString().Should().Be(nameof(ProcessErrorCode.ProcessNotFound));
        line.GetProperty("target").GetProperty("processId").GetInt32().Should().Be(10_244);
    }

    [Fact]
    public async Task AnAuditLogThatCannotBeWritten_NeverBreaksTheCall()
    {
        // Arrange - a file stands where the audit folder should be
        var (result, lines) = await CallAsync("click", EveryToolContractTests.ArgumentsFor("click"), services => services.BlockAuditFolder());

        // Assert
        result.IsError.Should().NotBe(true);
        lines.Should().BeEmpty();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task AnAuditLogThatThrows_NeverBreaksTheCall_AndIsEndedAtMostOnce(bool beginThrows)
    {
        // Arrange - an IAuditLog that breaks its contract (AuditLog itself never throws)
        using var services = new ContractServices(ContractMode.Typical);
        var audit = new Mock<IAuditLog>();
        var begin = audit.Setup(a => a.Begin(It.IsAny<string>(), It.IsAny<IEnumerable<KeyValuePair<string, JsonElement>>?>(),
            It.IsAny<string?>(), It.IsAny<JsonElement?>()));
        if (beginThrows)
        {
            begin.Throws(new IOException("begin"));
        }
        else
        {
            begin.Returns(new AuditCall("click", new Dictionary<string, JsonElement>(), null, DateTimeOffset.Now, 0,
                new Dictionary<long, AuditTarget>(), new Dictionary<int, string?>()));
            audit.Setup(a => a.EndAsync(It.IsAny<AuditCall>(), It.IsAny<CallToolResult?>(), It.IsAny<string>())).Throws(new IOException("end"));
        }

        await using var server = await InProcessMcpServer.StartAsync([], builder => builder.AddWindowsAppManagerTools(),
            configureServices: s =>
            {
                services.Register(s);
                s.AddSingleton(audit.Object);
            });

        // Act
        var result = await server.CallToolAsync("click", EveryToolContractTests.ArgumentsFor("click"));

        // Assert
        result.IsError.Should().NotBe(true);
        audit.Verify(a => a.EndAsync(It.IsAny<AuditCall>(), It.IsAny<CallToolResult?>(), It.IsAny<string>()),
            beginThrows ? Times.Never() : Times.Once());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AnAuditLogWhoseEndFaultsOrNeverFinishes_HoldsUpTheCall_AtMostTheCallDelay(bool neverFinishes)
    {
        // Arrange - an IAuditLog that breaks its contract: its wait faults, or never ends
        using var services = new ContractServices(ContractMode.Typical);
        var audit = new Mock<IAuditLog>();
        audit.Setup(a => a.Begin(It.IsAny<string>(), It.IsAny<IEnumerable<KeyValuePair<string, JsonElement>>?>(),
                It.IsAny<string?>(), It.IsAny<JsonElement?>()))
            .Returns(new AuditCall("click", new Dictionary<string, JsonElement>(), null, DateTimeOffset.Now, 0,
                new Dictionary<long, AuditTarget>(), new Dictionary<int, string?>()));
        audit.Setup(a => a.EndAsync(It.IsAny<AuditCall>(), It.IsAny<CallToolResult?>(), It.IsAny<string>()))
            .Returns(neverFinishes ? new TaskCompletionSource().Task : Task.FromException(new IOException("end")));

        await using var server = await InProcessMcpServer.StartAsync([], builder => builder.AddWindowsAppManagerTools(),
            configureServices: s =>
            {
                services.Register(s);
                s.AddSingleton(audit.Object);
            });

        // Act
        var watch = System.Diagnostics.Stopwatch.StartNew();
        var result = await server.CallToolAsync("click", EveryToolContractTests.ArgumentsFor("click"));

        // Assert
        result.IsError.Should().NotBe(true);
        watch.Elapsed.Should().BeLessThan(AuditLog.MaxCallDelay + TimeSpan.FromSeconds(10), "the filter bounds the wait");
    }

    [Fact]
    public async Task GetServerInfo_ReportsTheAuditFolder_AndAWarningWhenItCannotBeWritten()
    {
        // Arrange
        using var services = new ContractServices(ContractMode.Typical);
        services.BlockAuditFolder();
        await using var server = await InProcessMcpServer.StartAsync(
            [], builder => builder.AddWindowsAppManagerTools(), configureServices: services.Register);

        // Act
        var before = Json(await server.CallToolAsync("get_server_info"));
        await server.CallToolAsync("click", EveryToolContractTests.ArgumentsFor("click"));
        var after = Json(await server.CallToolAsync("get_server_info"));

        // Assert
        before.GetProperty("paths").GetProperty("auditLog").GetString().Should().Be(services.AuditFolder);
        before.TryGetProperty("warnings", out _).Should().BeFalse("nothing was written yet");
        after.GetProperty("warnings").EnumerateArray().Select(w => w.GetString()).Should()
            .Contain(w => w!.Contains("audit log") && w.Contains(services.AuditFolder));
    }

    private static JsonElement Json(CallToolResult result) =>
        JsonDocument.Parse(result.Content.OfType<TextContentBlock>().Single().Text).RootElement.Clone();

    /// <summary>The string values of the arguments, at any depth, except those the log keeps (process and preset names, fixed values).</summary>
    private static IEnumerable<string> UnnamedStrings(IReadOnlyDictionary<string, object?> arguments) =>
        arguments.Where(a => !AuditRecord.NamedStringArguments.Contains(a.Key)).SelectMany(a => a.Value switch
        {
            string s => [s],
            JsonElement e => UnnamedStrings(e),
            _ => []
        });

    private static IEnumerable<string> UnnamedStrings(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.String => [value.GetString()!],
        JsonValueKind.Array => value.EnumerateArray().SelectMany(UnnamedStrings),
        JsonValueKind.Object => value.EnumerateObject().Where(p => !AuditRecord.NamedStringArguments.Contains(p.Name))
            .SelectMany(p => UnnamedStrings(p.Value)),
        _ => []
    };
}
