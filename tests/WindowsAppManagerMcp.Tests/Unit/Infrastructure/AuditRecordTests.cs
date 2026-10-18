using System.Text.Json;
using ModelContextProtocol.Protocol;
using WindowsAppManagerMcp.Infrastructure;

namespace WindowsAppManagerMcp.Tests.Unit.Infrastructure;

/// <summary>
/// What one audit log line holds (roadmap M2.17): the call's target, the window its input went to, its outcome and
/// errorCode, and a summary of its arguments that never holds typed text, keys, clipboard text, URLs or command-line
/// arguments, only their lengths or counts. Pure: no file and no window is touched.
/// </summary>
public class AuditRecordTests
{
    private static readonly DateTimeOffset Started = new(2026, 10, 10, 14, 3, 22, TimeSpan.FromHours(2));

    /// <summary>A call whose tool declares each argument with the JSON type it has, unless <paramref name="schema"/> says otherwise.</summary>
    private static AuditCall Call(
        string tool,
        object arguments,
        IReadOnlyDictionary<long, AuditTarget>? windows = null,
        IReadOnlyDictionary<int, string?>? processes = null,
        object? schema = null)
    {
        var values = Arguments(arguments);
        return new(tool, values, "7", Started, 0, windows ?? new Dictionary<long, AuditTarget>(),
            processes ?? new Dictionary<int, string?>(), AuditRecord.DeclaredTypes(Schema(schema ?? SchemaOf(values))));
    }

    private static JsonElement Schema(object schema) => JsonSerializer.SerializeToElement(schema);

    /// <summary>An input schema that declares each argument as the JSON type of its value, nullable as the SDK makes them.</summary>
    private static object SchemaOf(IReadOnlyDictionary<string, JsonElement> arguments) => new
    {
        type = "object",
        properties = arguments.ToDictionary(a => a.Key, a => (object)new
        {
            type = new[]
            {
                a.Value.ValueKind switch
                {
                    JsonValueKind.Number => "integer",
                    JsonValueKind.True or JsonValueKind.False => "boolean",
                    JsonValueKind.Array => "array",
                    JsonValueKind.Object => "object",
                    _ => "string"
                },
                "null"
            }
        })
    };

    private static Dictionary<string, JsonElement> Arguments(object arguments) =>
        JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(JsonSerializer.Serialize(arguments))!;

    private static CallToolResult Result(object body, bool isError = false) => new()
    {
        IsError = isError,
        Content = [new TextContentBlock { Text = JsonSerializer.Serialize(body, JsonSerializerOptions.Web) }]
    };

    private static AuditEntry Build(AuditCall call, CallToolResult? result, string outcome = AuditOutcome.Ok) =>
        AuditRecord.Build(call, result, outcome, durationMs: 12, serverPid: 4242);

    [Fact]
    public void TypedText_IsRecordedAsItsLength_Only()
    {
        // Arrange
        var call = Call("type_text", new { text = "hunter2 is my password", windowHandle = 100, delayMs = 0, allowHostTarget = false });
        var result = Result(new
        {
            success = true, text = "hunter2 is my password", charactersTyped = 22,
            receivingWindow = new { handle = 100, title = "Sign in", processName = "app", processId = 42 }
        });

        // Act
        var entry = Build(call, result);
        var line = AuditRecord.ToJsonLine(entry);

        // Assert
        line.Should().NotContain("hunter2");
        entry.Args!["textLength"]!.GetValue<int>().Should().Be(22);
        entry.Args.ContainsKey("text").Should().BeFalse();
        entry.Args["windowHandle"]!.GetValue<long>().Should().Be(100);
        entry.Args["delayMs"]!.GetValue<int>().Should().Be(0);
        entry.Args["allowHostTarget"]!.GetValue<bool>().Should().BeFalse();
        entry.Result!["charactersTyped"]!.GetValue<int>().Should().Be(22);
        entry.Result.ContainsKey("text").Should().BeFalse();
        entry.Result.ContainsKey("success").Should().BeFalse("the outcome says it");
        entry.ReceivingWindow.Should().Be(new AuditTarget(100, "Sign in", "app", 42));
    }

    [Theory]
    [InlineData("send_keys", "keys", "p4ssw0rd{ENTER}")]
    [InlineData("set_clipboard", "text", "my secret token")]
    [InlineData("launch_browser_with_debug", "url", "https://example.com/?token=secret")]
    [InlineData("launch_application", "workingDirectory", @"C:\secret project")]
    [InlineData("element_action", "name", "secret merger plan.docx")] // an element's name, not a preset's
    public void EveryOtherString_IsRecordedAsItsLength(string tool, string argument, string value)
    {
        var entry = Build(Call(tool, new Dictionary<string, object> { [argument] = value }), null);

        AuditRecord.ToJsonLine(entry).Should().NotContain("secret").And.NotContain("p4ss");
        entry.Args![argument + "Length"]!.GetValue<int>().Should().Be(value.Length);
    }

    [Theory]
    [InlineData("processName", "notepad")]
    [InlineData("expectedProcessName", "notepad")]
    [InlineData("name", "Coding")]
    [InlineData("matchBy", "processAndTitle")]
    [InlineData("browser", "edge")]
    public void StringsThatNameAProcessAPresetOrAFixedValue_AreKept(string argument, string value)
    {
        var entry = Build(Call("close_application", new Dictionary<string, object> { [argument] = value }), null);

        entry.Args![argument]!.GetValue<string>().Should().Be(value);
    }

    [Fact]
    public void ArraysAreCounted_AndObjectsAreLeftOut()
    {
        var call = Call("launch_application", new
        {
            executable = "pwsh.exe",
            arguments = new[] { "-Command", "Set-Secret -Value hunter2" },
            options = new { password = "hunter2" }
        });

        var entry = Build(call, null);

        AuditRecord.ToJsonLine(entry).Should().NotContain("hunter2");
        entry.Args!["argumentsCount"]!.GetValue<int>().Should().Be(2);
        entry.Args.ContainsKey("options").Should().BeFalse();
        entry.Args.ContainsKey("optionsCount").Should().BeFalse();
    }

    [Theory]
    [InlineData("notepad", "notepad")]
    [InlineData("Visual Studio Code", "Visual Studio Code")]
    [InlineData(@"tools\app.exe", @"tools\app.exe")]
    [InlineData(@"C:\Program Files\App\app.exe", @"C:\Program Files\App\app.exe")]
    [InlineData(@"C:\a#b\app.exe", @"C:\a#b\app.exe")]
    [InlineData(@"\\server\share\app.exe", @"\\server\share\app.exe")]
    [InlineData("file:///C:/x/app.exe?token=secret#frag", @"C:\x\app.exe")]
    // An installed app's AppUserModelID names the app, bare or through shell:AppsFolder (roadmap M6.5)
    [InlineData("Microsoft.WindowsCalculator_8wekyb3d8bbwe!App", "Microsoft.WindowsCalculator_8wekyb3d8bbwe!App")]
    [InlineData(@"shell:AppsFolder\Microsoft.WindowsCalculator_8wekyb3d8bbwe!App", @"shell:AppsFolder\Microsoft.WindowsCalculator_8wekyb3d8bbwe!App")]
    [InlineData(@"shell:AppsFolder\MSEdge", @"shell:AppsFolder\MSEdge")]
    public void AProgramPath_IsKept(string executable, string recorded)
    {
        AuditRecord.RedactExecutable(executable).Should().Be(recorded);

        var entry = Build(Call("launch_application", new { executable }), null);
        entry.Args!["executable"]!.GetValue<string>().Should().Be(recorded);
        entry.Target!.Executable.Should().Be(recorded);
        entry.Args.ContainsKey("executableLength").Should().Be(recorded != executable, "the length is recorded when something was cut");
    }

    [Theory]
    [InlineData("https://hooks.slack.com/services/T0/B0/SECRETxyz", "https://hooks.slack.com")] // a webhook URL is its own secret
    [InlineData("https://user:pw@example.com:8443/reset/SECRET?token=abc#frag", "https://example.com:8443")]
    [InlineData("https://www.dropbox.com/s/SECRETkey/report.pdf", "https://www.dropbox.com")]
    [InlineData("data:text/plain,hunter2SECRET", "data:")]
    [InlineData("javascript:alert('SECRET')", "javascript:")]
    [InlineData("ms-settings:SECRET", "ms-settings:")]
    [InlineData("mailto:SECRET@example.com?subject=SECRET", "mailto:example.com")] // the address is user info
    [InlineData("www.example.com/reset/SECRET?t=1", "www.example.com")] // no scheme, but ShellExecute opens it
    [InlineData("example.com/SECRET", "example.com")]
    [InlineData("SECRET@example.com", "example.com")]
    [InlineData("SECRET@example.com/reset?t=SECRET", "example.com")]
    public void AUrl_KeepsOnlyItsSchemeAndHost(string executable, string recorded)
    {
        AuditRecord.RedactExecutable(executable).Should().Be(recorded);

        var entry = Build(Call("launch_application", new { executable }), null);
        AuditRecord.ToJsonLine(entry).Should().NotContain("SECRET");
        entry.Args!["executableLength"]!.GetValue<int>().Should().Be(executable.Length);
    }

    [Theory]
    [InlineData("https://accounts.example.com/reset?token=SECRET", "https://accounts.example.com")]
    [InlineData("http://user:SECRET@127.0.0.1:8080/SECRET#SECRET", "http://127.0.0.1:8080")]
    [InlineData("about:blank", "about:blank")]
    [InlineData("file:///C:/Users/me/Documents/SECRET/2025-return.pdf", "file:")] // refused, but recorded: never its path
    [InlineData(@"C:\Users\me\SECRET.txt", "file:")]
    [InlineData(@"\\server\share\SECRET.txt", "file:")]
    [InlineData("javascript:alert('SECRET')", "javascript:")]
    [InlineData("data:text/html,SECRET", "data:")]
    [InlineData("Chrome://settings/SECRET", "chrome:")]
    [InlineData("example.com/SECRET", "invalid")]
    [InlineData("SECRET", "invalid")]
    public void BrowserNavigatesUrl_KeepsOnlyAWebPagesSchemeAndHost_AndOtherwiseItsSchemeAlone(string url, string recorded)
    {
        AuditRecord.RedactNavigatedUrl(url).Should().Be(recorded);

        var entry = Build(Call("browser_navigate", new { url }), null);
        AuditRecord.ToJsonLine(entry).Should().NotContain("SECRET");
        entry.Args!["url"]!.GetValue<string>().Should().Be(recorded);
        entry.Args["urlLength"]!.GetValue<int>().Should().Be(url.Length);
    }

    [Theory]
    [InlineData(271828)]
    [InlineData(true)]
    public void BrowserEvaluatesValue_IsNeverRecorded_NotEvenAsANumber_OnlyItsLength(object value)
    {
        // The script's value is whatever the page returned: a one-time code, a balance, a flag (roadmap M5.8)
        var call = Call("browser_evaluate", new { expression = "document.querySelector('#otp').value", timeoutMs = 5000, handle = 100 });
        var result = Result(new { success = true, targetId = "T1", value, valueType = "number", valueLength = 6, cdpPort = 9222 });

        var entry = Build(call, result);

        var line = AuditRecord.ToJsonLine(entry);
        line.Should().NotContain("271828").And.NotContain("#otp");
        entry.Result!.ContainsKey("value").Should().BeFalse();
        entry.Result["valueLength"]!.GetValue<int>().Should().Be(6);
        entry.Args!["expressionLength"]!.GetValue<int>().Should().Be("document.querySelector('#otp').value".Length);
        entry.Args["timeoutMs"]!.GetValue<int>().Should().Be(5000);
    }

    [Fact]
    public void BrowserClicksName_IsAnElementsName_RecordedAsItsLength_WhileItsButtonIsKept()
    {
        // An accessible name on a web page is content, as element_action's name criterion is (roadmap M5.8)
        var entry = Build(Call("browser_click", new { role = "button", name = "Pay 1,250 USD to J. Smith", button = "primary", handle = 100 }), null);

        AuditRecord.ToJsonLine(entry).Should().NotContain("Smith");
        entry.Args!["nameLength"]!.GetValue<int>().Should().Be("Pay 1,250 USD to J. Smith".Length);
        entry.Args["roleLength"]!.GetValue<int>().Should().Be(6);
        entry.Args["button"]!.GetValue<string>().Should().Be("primary");
    }

    [Fact]
    public void BrowserTypesTextAndSelector_AreRecordedAsTheirLengths()
    {
        var entry = Build(Call("browser_type", new { text = "hunter2", selector = "#password", pressEnter = true, handle = 100 }), null);

        AuditRecord.ToJsonLine(entry).Should().NotContain("hunter2").And.NotContain("#password");
        entry.Args!["textLength"]!.GetValue<int>().Should().Be(7);
        entry.Args["selectorLength"]!.GetValue<int>().Should().Be(9);
        entry.Args["pressEnter"]!.GetValue<bool>().Should().BeTrue();
    }

    [Fact]
    public void ANumberOrBooleanForAStringParameter_IsRecordedAsItsLength()
    {
        // A PIN sent as a JSON number: binding fails with InvalidArguments, and the call is still audited
        var schema = new { type = "object", properties = new { text = new { type = "string" }, delayMs = new { type = "integer" } } };

        var number = Build(Call("type_text", new { text = 482913, delayMs = 5 }, schema: schema), null);
        var boolean = Build(Call("type_text", new { text = true }, schema: schema), null);

        AuditRecord.ToJsonLine(number).Should().NotContain("482913");
        number.Args!["textLength"]!.GetValue<int>().Should().Be(6);
        number.Args.ContainsKey("text").Should().BeFalse();
        number.Args["delayMs"]!.GetValue<int>().Should().Be(5, "the tool declares it a number");
        boolean.Args!["textLength"]!.GetValue<int>().Should().Be(4);
    }

    [Fact]
    public void AnArgumentTheToolDoesNotDeclare_IsOnlyCounted()
    {
        var schema = new { type = "object", properties = new { x = new { type = "integer" } } };
        var arguments = new Dictionary<string, object> { ["x"] = 1, ["password=hunter2"] = 1, ["extraNote"] = "abc" };

        var entry = Build(Call("click", arguments, schema: schema), null);

        AuditRecord.ToJsonLine(entry).Should().NotContain("hunter2").And.NotContain("extraNote");
        entry.Args!["x"]!.GetValue<int>().Should().Be(1);
        entry.Args["unknownArgumentsCount"]!.GetValue<int>().Should().Be(2);
    }

    [Fact]
    public void WithoutTheToolsSchema_NoNumberIsKept_AndOnlyIdentifierNamesAre()
    {
        var arguments = Arguments(new Dictionary<string, object> { ["x"] = 10, ["force"] = true, ["processName"] = "notepad", ["password=hunter2"] = 1 });

        var summary = AuditRecord.SummarizeArguments(arguments, declaredTypes: null)!;

        summary.ToJsonString().Should().NotContain("hunter2");
        summary["xLength"]!.GetValue<int>().Should().Be(2);
        summary["forceLength"]!.GetValue<int>().Should().Be(4);
        summary["processName"]!.GetValue<string>().Should().Be("notepad");
        summary["unknownArgumentsCount"]!.GetValue<int>().Should().Be(1);
    }

    [Fact]
    public void TheDeclaredTypes_ComeFromTheInputSchema()
    {
        var declared = AuditRecord.DeclaredTypes(Schema(new
        {
            type = "object",
            properties = new { x = new { type = "integer" }, windowHandle = new { type = new[] { "integer", "null" } }, note = new { description = "untyped" } }
        }))!;

        declared["x"].Should().BeEquivalentTo("integer");
        declared["windowHandle"].Should().BeEquivalentTo("integer", "null");
        declared["note"].Should().BeEmpty();
        AuditRecord.DeclaredTypes(null).Should().BeNull();
        AuditRecord.DeclaredTypes(Schema(new { type = "object" }))!.Should().BeEmpty();
    }

    [Fact]
    public void TheTarget_IsTheWindowAsItWasBeforeTheCall()
    {
        // close_window: the window is gone afterwards, so its title and process come from Begin's read
        var windows = new Dictionary<long, AuditTarget> { [100] = new(100, "Untitled - Notepad", "notepad", 42) };
        var call = Call("close_window", new { handle = 100, force = false }, windows);

        var entry = Build(call, Result(new { success = true }));

        entry.Target.Should().Be(new AuditTarget(100, "Untitled - Notepad", "notepad", 42));
        entry.Outcome.Should().Be(AuditOutcome.Ok);
        entry.ErrorCode.Should().BeNull();
    }

    [Fact]
    public void AWindowThatCouldNotBeRead_IsRecordedByItsHandle()
    {
        var entry = Build(Call("click", new { x = 10, y = 20, windowHandle = 0x1234 }), null);

        entry.Target.Should().Be(new AuditTarget(Handle: 0x1234));
    }

    [Fact]
    public void AWindowNamedByASelector_IsRecordedByTheHandleTheResultReports()
    {
        // move_window with a window selector (roadmap M2.4): the arguments hold no handle, the result names the window moved
        var entry = Build(Call("move_window", new { x = 10, y = 20, window = new { processName = "notepad" } }),
            Result(new { success = true, handle = 0x1234, state = "normal" }));

        entry.Target.Should().Be(new AuditTarget(Handle: 0x1234));
        entry.Args!.ContainsKey("window").Should().BeFalse("a selector's titles can be content, so objects are left out");
    }

    [Fact]
    public void AKill_NamesTheProcess_FromBeforeTheCall_OrElseFromTheResult()
    {
        var before = Build(Call("kill_process", new { processId = 42, forceKill = true }, processes: new Dictionary<int, string?> { [42] = "notepad" }),
            Result(new { success = true, processId = 42, forced = true, exited = true }));
        var after = Build(Call("kill_process", new { processId = 42 }),
            Result(new { success = true, processId = 42, processName = "notepad", exited = true }));

        before.Target.Should().Be(new AuditTarget(ProcessId: 42, Process: "notepad"));
        after.Target.Should().Be(new AuditTarget(ProcessId: 42, Process: "notepad"));
        before.Result!["forced"]!.GetValue<bool>().Should().BeTrue();
    }

    [Fact]
    public void ALaunch_NamesTheProcessAndWindowItStarted()
    {
        var entry = Build(Call("launch_application", new { executable = "notepad" }),
            Result(new { success = true, processId = 42, windowHandle = 0x500 }));

        entry.Target.Should().Be(new AuditTarget(Handle: 0x500, ProcessId: 42, Executable: "notepad"));
    }

    [Fact]
    public void AnInputCallWithoutATarget_HasNoTarget_ButItsReceivingWindow()
    {
        var entry = Build(Call("send_keys", new { keys = "^s" }),
            Result(new { success = false, keys = "^s", errorCode = "TargetIsAgentHost", receivingWindow = new { handle = 7, title = "pwsh", processName = "WindowsTerminal", processId = 9 } }, isError: true),
            AuditOutcome.Error);

        entry.Target.Should().BeNull();
        entry.ReceivingWindow.Should().Be(new AuditTarget(7, "pwsh", "WindowsTerminal", 9));
        entry.Outcome.Should().Be(AuditOutcome.Error);
        entry.ErrorCode.Should().Be("TargetIsAgentHost");
    }

    [Fact]
    public void AnErrorCodeThatIsNoIdentifier_IsLeftOut()
    {
        var entry = Build(Call("click", new { x = 1, y = 2 }), Result(new { success = false, errorCode = "Not a code: hunter2" }, isError: true));

        entry.ErrorCode.Should().BeNull();
    }

    [Fact]
    public void ABatch_RecordsEachItem_WithTheWindowsAsTheyWereBeforeTheCall()
    {
        var windows = new Dictionary<long, AuditTarget>
        {
            [1] = new(1, "Report.docx - Word", "WINWORD", 11),
            [2] = new(2, "Budget.xlsx - Excel", "EXCEL", 22)
        };
        var call = Call("close_windows_batch", new { windows = new[] { new { handle = 1 }, new { handle = 2 } }, confirm = true }, windows);
        var result = Result(new
        {
            totalRequested = 2, succeeded = 1, failed = 1,
            results = new object[] { new { handle = 1, success = true }, new { handle = 2, success = false, error = "x", errorCode = "HandleMismatch" } }
        });

        var entry = Build(call, result);

        entry.Items.Should().Equal(
            new AuditItem(1, "Report.docx - Word", "WINWORD", 11, null, true, null),
            new AuditItem(2, "Budget.xlsx - Excel", "EXCEL", 22, null, false, "HandleMismatch"));
        entry.Args!["windowsCount"]!.GetValue<int>().Should().Be(2);
        entry.Result!["failed"]!.GetValue<int>().Should().Be(1);
        entry.Target.Should().BeNull("a batch names its targets in its items");
    }

    [Fact]
    public void ACloseBatchRefusedAsAWhole_RecordsTheWindowsItAskedFor()
    {
        // PausedByUser: a tool error without results
        var windows = new Dictionary<long, AuditTarget> { [1] = new(1, "Report.docx - Word", "WINWORD", 11) };
        var call = Call("close_windows_batch", new { windows = new[] { new { handle = 1 }, new { handle = 2 } } }, windows);

        var entry = Build(call, Result(new { success = false, error = "Paused", errorCode = "PausedByUser", tool = "close_windows_batch" }, isError: true),
            AuditOutcome.Error);

        entry.Items.Should().Equal(
            new AuditItem(1, "Report.docx - Word", "WINWORD", 11, null, null, null),
            new AuditItem(2, null, null, null, null, null, null));
        entry.Target.Should().BeNull();
        entry.ErrorCode.Should().Be("PausedByUser");
    }

    [Fact]
    public void ALaunchBatch_RecordsEachExecutable_Redacted()
    {
        var call = Call("launch_applications_batch", new { applications = new[] { new { executable = "https://example.com/?t=secret" } } });
        var result = Result(new
        {
            totalRequested = 1, succeeded = 1, failed = 0,
            results = new object[] { new { executable = "https://example.com/?t=secret", success = true, processId = 5, windowHandle = 6 } }
        });

        var entry = Build(call, result);

        entry.Items.Should().Equal(new AuditItem(6, null, null, 5, "https://example.com", true, null));
        AuditRecord.ToJsonLine(entry).Should().NotContain("secret");
    }

    [Fact]
    public void ALongTitle_IsCut()
    {
        var title = new string('x', AuditRecord.MaxTitleLength + 50);
        var entry = Build(Call("click", new { x = 1, y = 2 }),
            Result(new { success = true, receivingWindow = new { handle = 1, title, processName = "p", processId = 2 } }));

        entry.ReceivingWindow!.Title.Should().HaveLength(AuditRecord.MaxTitleLength);
    }

    [Fact]
    public void ACallWithoutAResult_RecordsItsOutcome()
    {
        var entry = Build(Call("type_text", new { text = "abc" }), null, AuditOutcome.Cancelled);

        entry.Outcome.Should().Be(AuditOutcome.Cancelled);
        entry.Result.Should().BeNull();
        entry.ReceivingWindow.Should().BeNull();
    }

    [Fact]
    public void TheLine_IsOneLineOfCamelCaseJson_WithTheCallsIdentity()
    {
        var entry = Build(Call("click", new { x = 1, y = 2 }), Result(new { success = true }));

        var line = AuditRecord.ToJsonLine(entry);

        line.Should().NotContain("\n").And.NotContain("\r");
        using var json = JsonDocument.Parse(line);
        var root = json.RootElement;
        root.GetProperty("ts").GetString().Should().Be("2026-10-10T14:03:22.000+02:00");
        root.GetProperty("tool").GetString().Should().Be("click");
        root.GetProperty("outcome").GetString().Should().Be("ok");
        root.GetProperty("durationMs").GetInt64().Should().Be(12);
        root.GetProperty("serverPid").GetInt32().Should().Be(4242);
        root.GetProperty("requestId").GetString().Should().Be("7");
        root.TryGetProperty("errorCode", out _).Should().BeFalse("nulls are left out");
        root.EnumerateObject().First().Name.Should().Be("ts");
    }

    [Fact]
    public void ATitleWithLineBreaks_StaysOnOneLine()
    {
        var entry = Build(Call("click", new { x = 1, y = 2 }),
            Result(new { success = true, receivingWindow = new { handle = 1, title = "a\r\nb\u2028c", processName = "p", processId = 2 } }));

        AuditRecord.ToJsonLine(entry).Should().NotContain("\n").And.NotContain("\r").And.NotContain("\u2028");
    }

    [Fact]
    public void ACloseNamedByASelector_RecordsTheTitleAndProcess_OfTheWindowTheToolResolved()
    {
        // close_window with a window selector: the arguments name no handle, so Begin reads nothing; the tool notes the
        // window it resolved before it closes it (AuditScope), and the result names its handle
        var call = Call("close_window", new { window = new { processName = "notepad", titleContains = "notes" } });
        using (AuditScope.Enter(call))
        {
            AuditScope.NoteWindow(TestDataFactory.CreateWindowInfo(0x1234, "notes.txt - Notepad", "notepad", 42));
        }

        var entry = Build(call, Result(new { success = true, status = "closed", handle = 0x1234 }));

        entry.Target.Should().Be(new AuditTarget(0x1234, "notes.txt - Notepad", "notepad", 42));
    }

    [Fact]
    public void ACloseBatchNamedBySelectors_RecordsEachItem_WithTheResolvedWindow_AndItsStatus()
    {
        var call = Call("close_windows_batch", new { windows = new[] { new { window = new { processName = "notepad" } } } });
        using (AuditScope.Enter(call))
        {
            AuditScope.NoteWindow(TestDataFactory.CreateWindowInfo(7, "a.txt - Notepad", "notepad", 42));
        }

        var entry = Build(call, Result(new
        {
            totalRequested = 1, succeeded = 1, failed = 0,
            results = new object[] { new { handle = 7, success = true, status = "closed" } }
        }));

        entry.Items.Should().Equal(new AuditItem(7, "a.txt - Notepad", "notepad", 42, null, true, null, Status: "closed"));
    }

    [Fact]
    public void CloseApplication_RecordsEachWindowItClosed_WithItsProcess_AndStatus()
    {
        // close_application {processName}: the windows come from the result, their processes from what the tool listed
        var call = Call("close_application", new { processName = "notepad" });
        using (AuditScope.Enter(call))
        {
            AuditScope.NoteWindows([
                TestDataFactory.CreateWindowInfo(1, "a.txt - Notepad", "notepad", 42),
                TestDataFactory.CreateWindowInfo(2, "b.txt - Notepad", "notepad", 43)
            ]);
        }

        var entry = Build(call, Result(new
        {
            success = false, status = "promptShown", processIds = new[] { 42, 43 }, promptDetected = true, errorCode = "PromptShown",
            windows = new object[]
            {
                new { handle = 1, title = "a.txt - Notepad", status = "closed" },
                new { handle = 2, title = "b.txt - Notepad", status = "promptShown", errorCode = "PromptShown" }
            },
            remainingWindows = new object[] { new { handle = 2, title = "b.txt - Notepad", processId = 43, processName = "notepad" } }
        }), AuditOutcome.Error);

        entry.Items.Should().Equal(
            new AuditItem(1, "a.txt - Notepad", "notepad", 42, null, null, null, Status: "closed"),
            new AuditItem(2, "b.txt - Notepad", "notepad", 43, null, null, "PromptShown", Status: "promptShown"));
        entry.Args!["processName"]!.GetValue<string>().Should().Be("notepad");
        entry.Result!["promptDetected"]!.GetValue<bool>().Should().BeTrue();
    }

    [Fact]
    public void CloseApplicationRefused_WithNoWindows_RecordsItsTarget()
    {
        var windows = new Dictionary<long, AuditTarget> { [100] = new(100, "Untitled - Notepad", "notepad", 42) };
        var entry = Build(Call("close_application", new { handle = 100 }, windows),
            Result(new { success = false, processIds = new[] { 42 }, windows = Array.Empty<object>(), errorCode = "ProtectedWindow" }, isError: true),
            AuditOutcome.Error);

        entry.Items.Should().BeNull();
        entry.Target.Should().Be(new AuditTarget(100, "Untitled - Notepad", "notepad", 42));
    }

    [Fact]
    public void ApplyLayout_RecordsEachPlacement_WithItsWindow_AndWhatItLaunched()
    {
        var call = Call("apply_layout", new { name = "Coding", launchMissing = true });
        var entry = Build(call, Result(new
        {
            success = false, presetName = "Coding", windowsArranged = 1, windowsNotFound = 1, windowsLaunched = 1,
            placements = new object[]
            {
                new
                {
                    order = 0, status = "applied", launched = true, launchCommand = @"C:\Tools\code.exe",
                    window = new { handle = 5, title = "main.cs - Code", processName = "Code" }
                },
                new { order = 1, status = "failed", launchCommand = "https://example.com/reset?t=secret", errorCode = "LaunchFailed" },
                new { order = 2, status = "notFound", errorCode = "WindowNotFound" }
            }
        }), AuditOutcome.Error);

        entry.Items.Should().Equal(
            new AuditItem(5, "main.cs - Code", "Code", null, @"C:\Tools\code.exe", null, null, Status: "applied", Launched: true),
            new AuditItem(null, null, null, null, "https://example.com", null, "LaunchFailed", Status: "failed"),
            new AuditItem(null, null, null, null, null, null, "WindowNotFound", Status: "notFound"));
        entry.Result!["windowsLaunched"]!.GetValue<int>().Should().Be(1);
        AuditRecord.ToJsonLine(entry).Should().NotContain("secret");
    }

    [Fact]
    public void AnItemsStatusThatIsNoIdentifier_IsLeftOut()
    {
        var entry = Build(Call("close_windows_batch", new { windows = new[] { new { handle = 1 } } }),
            Result(new { results = new object[] { new { handle = 1, success = true, status = "closed: hunter2" } } }));

        entry.Items!.Single().Status.Should().BeNull();
    }

    [Fact]
    public void AWindowNotedOutsideAnAuditedCall_GoesNowhere()
    {
        var act = () => AuditScope.NoteWindow(TestDataFactory.CreateWindowInfo(1, "a", "p", 2));

        act.Should().NotThrow();
    }

    [Fact]
    public void AWindowsTitle_IsCut_WhenItIsNoted()
    {
        var call = Call("close_window", new { window = new { processName = "p" } });
        using (AuditScope.Enter(call))
        {
            AuditScope.NoteWindow(TestDataFactory.CreateWindowInfo(1, new string('x', AuditRecord.MaxTitleLength + 9), "p", 2));
        }

        call.ResolvedWindows[1].Title.Should().HaveLength(AuditRecord.MaxTitleLength);
    }

    [Fact]
    public void TheWindowsAndProcessesToReadBeforeTheCall_ComeFromTheArguments()
    {
        AuditRecord.WindowHandles(Arguments(new { windowHandle = 5 })).Should().Equal(5);
        AuditRecord.WindowHandles(Arguments(new { handle = 6, processId = 7 })).Should().Equal(6);
        AuditRecord.WindowHandles(Arguments(new { windows = new object?[] { new { handle = 8 }, null, new { handle = 9 } } })).Should().Equal(8, 9);
        AuditRecord.WindowHandles(Arguments(new { windowHandle = (int?)null, x = 1 })).Should().BeEmpty();
        // A window selector that names a handle (roadmap M2.4), also as a close batch item
        AuditRecord.WindowHandles(Arguments(new { window = new { handle = 10, processId = 7 } })).Should().Equal(10);
        AuditRecord.WindowHandles(Arguments(new { window = new { processName = "notepad" } })).Should().BeEmpty();
        AuditRecord.WindowHandles(Arguments(new { windows = new object[] { new { window = new { handle = 11 } }, new { handle = 12 } } }))
            .Should().Equal(11, 12);
        AuditRecord.WindowHandles(Arguments(new { windows = Enumerable.Range(1, 500).Select(h => new { handle = h }) }))
            .Should().HaveCount(AuditRecord.MaxWindowsRead);
        AuditRecord.ProcessIds(Arguments(new { processId = 7 })).Should().Equal(7);
        AuditRecord.ProcessIds(Arguments(new { processId = "seven" })).Should().BeEmpty();
        AuditRecord.ProcessIds(Arguments(new { processId = 7.5 })).Should().BeEmpty();
        AuditRecord.ProcessIds(Arguments(new { processId = 3_000_000_000 })).Should().BeEmpty("no process ID is that large");
    }

    [Fact]
    public void AHandleOrProcessIdGivenAsAString_IsReadAsTheNumberTheToolBinds()
    {
        // The tools bind a number from a JSON string (the SDK's AllowReadingFromString): close_window {"handle": "123"}
        // closes window 123, so the line names it
        AuditRecord.WindowHandles(Arguments(new { handle = "123" })).Should().Equal(123);
        AuditRecord.WindowHandles(Arguments(new { windowHandle = "0124" })).Should().Equal(124);
        AuditRecord.WindowHandles(Arguments(new { window = new { handle = "125" } })).Should().Equal(125);
        AuditRecord.WindowHandles(Arguments(new { windows = new object[] { new { handle = "126" }, new { window = new { handle = "127" } } } }))
            .Should().Equal(126, 127);
        AuditRecord.ProcessIds(Arguments(new { processId = "7" })).Should().Equal(7);

        // A string the tool would not bind as a number names nothing
        AuditRecord.WindowHandles(Arguments(new { handle = "notepad", windowHandle = "12.5" })).Should().BeEmpty();
    }

    [Fact]
    public void ASelectorsHandle_IsRead_InAnyLetterCase_AsTheSelectorBindsIt()
    {
        // A selector's members bind case-insensitively; the last of two that differ only in case is the one bound
        AuditRecord.WindowHandles(Arguments(new { window = new { Handle = 10 } })).Should().Equal(10);
        AuditRecord.WindowHandles(Arguments(new Dictionary<string, object> { ["window"] = new Dictionary<string, object> { ["HANDLE"] = "11" } }))
            .Should().Equal(11);
        AuditRecord.WindowHandles(Arguments(new { windows = new object[] { new { Handle = 12 }, new { Window = new { handle = 13 } } } }))
            .Should().Equal(12, 13);
        AuditRecord.WindowHandles(Arguments(new Dictionary<string, object>
        {
            ["window"] = new Dictionary<string, object> { ["handle"] = 14, ["Handle"] = 15 }
        })).Should().Equal(15);
    }

    [Fact]
    public void AnIntegerGivenAsAString_IsSummarizedAsTheNumber_WhereTheToolDeclaresAnInteger()
    {
        var schema = new
        {
            type = "object",
            properties = new
            {
                handle = new { type = new[] { "integer", "null" } },
                delayMs = new { type = "integer" },
                text = new { type = "string" },
                monitor = new { type = new[] { "integer", "string", "null" } }
            }
        };

        var entry = Build(Call("close_window", new { handle = "123", delayMs = "12.5", text = "482913", monitor = "1" }, schema: schema), null);

        entry.Args!["handle"]!.GetValue<long>().Should().Be(123);
        entry.Args.ContainsKey("handleLength").Should().BeFalse();
        entry.Args["delayMsLength"]!.GetValue<int>().Should().Be(4, "a string the tool would not bind as an integer stays a length");
        entry.Args["textLength"]!.GetValue<int>().Should().Be(6, "a string parameter's digits are content");
        entry.Args["monitorLength"]!.GetValue<int>().Should().Be(1, "a parameter that takes a string keeps its strings as lengths");
        AuditRecord.ToJsonLine(entry).Should().NotContain("482913");
    }

    [Fact]
    public void ALaunchPlacementsMembers_AreRead_InAnyLetterCase_AndItsBoundsAsTheNumbersTheyBind()
    {
        var call = Call("launch_application", new
        {
            executable = "notepad",
            placement = new { Snap = "left_half", Bounds = (object?)null, Focus = true }
        });
        var byBounds = Call("launch_application", new
        {
            executable = "notepad",
            placement = new { bounds = new { X = "10", y = 20, Width = 800, height = "600" } }
        });

        var snapped = Build(call, Result(new { success = true, processId = 42 })).Target!.Placement!;
        var placed = Build(byBounds, Result(new { success = true, processId = 42 })).Target!.Placement!;

        snapped.Snap.Should().Be("left_half");
        snapped.Focus.Should().BeTrue();
        placed.Bounds.Should().Be(new BoundsDto(10, 20, 800, 600));
    }

    [Fact]
    public void ALaunchAsAdministrator_IsRecordedWithRunAsAdmin()
    {
        // Roadmap M6.5: an elevated start is the launch the audit log most needs to show
        var entry = Build(Call("launch_application", new { executable = @"C:\Tools\setup-helper.exe", runAsAdmin = true }), null);

        entry.Args!["runAsAdmin"]!.GetValue<bool>().Should().BeTrue();
        entry.Target!.Executable.Should().Be(@"C:\Tools\setup-helper.exe");
    }

    [Theory]
    [InlineData("confirmed")]
    [InlineData("uacPrompt")]
    public void ALaunchAsAdministrator_RecordsHowTheElevationWasApproved(string elevation)
    {
        // Review M6.5 #8: the user's yes through the client, or Windows' own UAC prompt
        var entry = Build(Call("launch_application", new { executable = @"C:\Tools\setup-helper.exe", runAsAdmin = true }),
            Result(new { success = true, processId = 4321, launchStatus = "started", elevation }));

        entry.Args!["runAsAdmin"]!.GetValue<bool>().Should().BeTrue();
        entry.Result!["elevation"]!.GetValue<string>().Should().Be(elevation);
    }

    [Fact]
    public void ABatchItemLaunchedAsAdministrator_RecordsItsElevation()
    {
        var call = Call("launch_applications_batch", new { applications = new[] { new { executable = @"C:\Tools\a.exe", runAsAdmin = true } } });

        var entry = Build(call, Result(new
        {
            totalRequested = 1, succeeded = 1, failed = 0,
            results = new[] { new { executable = @"C:\Tools\a.exe", success = true, processId = 7, elevation = "uacPrompt" } }
        }));

        entry.Items!.Single().Elevation.Should().Be("uacPrompt");
    }
}
