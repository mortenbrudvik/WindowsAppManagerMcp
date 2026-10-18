using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol.Protocol;
using WindowsAppManagerMcp.Infrastructure;

namespace WindowsAppManagerMcp.Tests.Unit.Infrastructure;

/// <summary>
/// run_steps' runner (roadmap M6.2, owner decision D7) with a fake dispatcher: what it refuses before anything runs, which
/// arguments it gives each step (the run's window, type_text's method, a wait cut to the time left, references), when it
/// stops (a failed step, a pause, the time, the foreground taken), what it returns, and that it holds the input gate for the
/// whole run while its own steps pass it. The contract tests run it through the real MCP pipeline.
/// </summary>
public class StepRunnerTests
{
    private const long Window = 0x1A0B2C;
    private const long Dialog = 0x1A0B90; // owned by Window, in another process (a shell dialog host)
    private const long SameProcess = 0x1A0C00; // another window of Window's process
    private const long Stranger = 0x2B0000; // another application
    private const long Terminal = 0x3C0000; // the agent's terminal
    private const long Explorer = 0x4D0000; // a File Explorer window, in the shell's process
    private const long Taskbar = 0x4D0100; // the taskbar, another window of the shell's process
    private const int ShellProcessId = 7_000;
    private const int ProcessId = 4_100;

    private static readonly TimeSpan Long = TimeSpan.FromSeconds(30);

    private readonly Mock<IInputSession> _session = new();
    private readonly Mock<IWindowService> _windows = new();
    private readonly Mock<IElementRegistry> _elements = new();
    private readonly FakeDispatcher _dispatcher = new();
    private readonly CancellationTokenSource _pause = new();
    private long _foreground = Window;
    private bool _paused;

    public StepRunnerTests()
    {
        _session.Setup(s => s.IsPaused).Returns(() => _paused);
        _session.Setup(s => s.PauseToken).Returns(() => _pause.Token);
        _session.Setup(s => s.ReleaseHeldInput(It.IsAny<string>())).Returns(InputReleaseOutcome.Nothing);
        _windows.Setup(w => w.GetWindowInfo(It.IsAny<nint>())).Returns((nint h) => Info(h));
        _windows.Setup(w => w.GetForegroundWindow()).Returns(() => Info((nint)_foreground));
        _windows.Setup(w => w.ResolveWindow(It.IsAny<WindowSelector>())).Returns(WindowResolution.Found(Info((nint)Stranger)!));
        _elements.Setup(e => e.Lookup(It.IsAny<string>())).Returns((string r) => new ElementLookup(r, (nint)Window, null, 1));
    }

    private static WindowInfo? Info(nint handle) => (long)handle switch
    {
        Window => new WindowInfo(handle, "Form", "app", ProcessId, new WindowRect(0, 0, 800, 600), WindowState.Normal, true, 0),
        Dialog => new WindowInfo(handle, "Open", "dialoghost", 4_200, new WindowRect(0, 0, 400, 300), WindowState.Normal, true, 0)
        {
            OwnerHandle = (nint)Window
        },
        SameProcess => new WindowInfo(handle, "Palette", "app", ProcessId, new WindowRect(0, 0, 200, 300), WindowState.Normal, true, 0),
        Stranger => new WindowInfo(handle, "Mail", "mail", 5_300, new WindowRect(0, 0, 800, 600), WindowState.Normal, true, 0),
        Terminal => new WindowInfo(handle, "pwsh", "WindowsTerminal", 6_400, new WindowRect(0, 0, 800, 600), WindowState.Normal, true, 0),
        Explorer => new WindowInfo(handle, "Documents", "explorer", ShellProcessId, new WindowRect(0, 0, 800, 600), WindowState.Normal, true, 0),
        Taskbar => new WindowInfo(handle, "", "explorer", ShellProcessId, new WindowRect(0, 1040, 1920, 40), WindowState.Normal, true, 0),
        _ => null
    };

    private StepRunner Runner() => new(_session.Object, _windows.Object, _elements.Object);

    private static RunStep Step(StepTool tool, string? args = null, string? id = null, bool optional = false, bool @return = false) =>
        new(tool, args == null ? null : JsonNode.Parse(args)!.AsObject(), id, optional, @return);

    private IReadOnlyList<CheckedStep> Check(params RunStep[] steps) => Runner().Check(steps, _dispatcher);

    private Task<StepRunOutcome> RunAsync(params RunStep[] steps) => RunAsync(TimeSpan.FromSeconds(10), steps);

    private Task<StepRunOutcome> RunAsync(TimeSpan maxDuration, params RunStep[] steps) =>
        Runner().RunAsync((nint)Window, Runner().Check(steps, _dispatcher), _dispatcher, maxDuration, TimeSpan.Zero, null, CancellationToken.None);

    private static string Refusal(Action check)
    {
        var error = check.Should().Throw<ToolErrorException>().Which;
        error.ErrorCode.Should().Be("InvalidArguments");
        error.Message.Should().Contain("Nothing was done");
        return error.Message;
    }

    // ---- Checks before the run ----

    [Theory]
    [InlineData(0)]
    [InlineData(51)]
    public void Check_RefusesARunOfNoStepsOrMoreThan50(int count)
    {
        var steps = Enumerable.Repeat(Step(StepTool.GetFocusedElement), count).ToArray();

        Refusal(() => Check(steps)).Should().Contain("1-50");
    }

    [Fact]
    public void Check_RefusesAToolTheServerDoesNotServe_BeforeAnythingRuns()
    {
        _dispatcher.NotServed.Add("type_text");

        Refusal(() => Check(Step(StepTool.FocusWindow), Step(StepTool.TypeText, """{ "text": "a" }""")))
            .Should().Contain("Step 2 (type_text)").And.Contain("does not serve type_text");
        _dispatcher.Calls.Should().BeEmpty();
    }

    [Fact]
    public void Check_RefusesAnArgumentTheStepsToolDoesNotDeclare_NamingTheOneItMeant()
    {
        Refusal(() => Check(Step(StepTool.TypeText, """{ "txt": "a" }""")))
            .Should().Contain("Step 1 (type_text)").And.Contain("'txt' (did you mean 'text'?)");
    }

    [Theory]
    [InlineData("bad id")]
    [InlineData("")]
    [InlineData("an-id-that-is-much-longer-than-32-chars")]
    public void Check_RefusesAnIdThatIsNoName(string id)
    {
        Refusal(() => Check(Step(StepTool.GetFocusedElement, id: id))).Should().Contain("id");
    }

    [Fact]
    public void Check_RefusesAStepThatNamesNoTool_InsteadOfRunningFocusWindow()
    {
        // StepTool's first value is focus_window: a step without a tool would otherwise run it, on the run's window
        Refusal(() => Check(Step(StepTool.GetFocusedElement), new RunStep(null, JsonNode.Parse("""{ "handle": 1706796 }""")!.AsObject())))
            .Should().Contain("Step 2 names no tool");
        _dispatcher.Calls.Should().BeEmpty();
    }

    [Fact]
    public void Check_RefusesTwoStepsWithOneId()
    {
        Refusal(() => Check(Step(StepTool.GetFocusedElement, id: "a"), Step(StepTool.GetFocusedElement, id: "a")))
            .Should().Contain("'a' is already the id");
    }

    [Theory]
    [InlineData(StepTool.TypeText, """{ "text": "a", "allowHostTarget": true }""", "allowHostTarget")]
    [InlineData(StepTool.TypeText, """{ "text": "a", "method": "paste" }""", "clipboard")]
    [InlineData(StepTool.TypeText, """{ "text": "a", "method": "Paste" }""", "clipboard")]
    [InlineData(StepTool.TypeText, """{ "text": "a", "method": "auto" }""", "auto pastes")]
    [InlineData(StepTool.ElementAction, """{ "action": "set_value", "value": "a", "mode": "paste" }""", "clipboard")]
    [InlineData(StepTool.ElementAction, """{ "action": "window_close" }""", "closes nothing")]
    [InlineData(StepTool.ElementAction, """{ "action": "windowClose" }""", "closes nothing")]
    [InlineData(StepTool.CaptureWindow, """{ "outputPath": "C:\\temp\\a.png" }""", "writes no file")]
    [InlineData(StepTool.CaptureWindow, """{ "saveToFile": true }""", "writes no file")]
    [InlineData(StepTool.CaptureWindow, """{ "overwrite": true }""", "writes no file")]
    [InlineData(StepTool.CaptureWindow, """{ "output": "file" }""", "writes no file")]
    [InlineData(StepTool.CaptureWindow, """{ "output": "both" }""", "writes no file")]
    [InlineData(StepTool.ElementAction, """{ "action": "invoke", "automationId": "send", "scope": "desktop" }""", "every window")]
    [InlineData(StepTool.FindUiElements, """{ "name": "Send", "scope": "Desktop" }""", "every window")]
    [InlineData(StepTool.WaitForElement, """{ "name": "Send", "scope": "desktop" }""", "every window")]
    [InlineData(StepTool.PressKeys, """{ "keys": "alt+f4" }""", "%{F4}")]
    [InlineData(StepTool.PressKeys, """{ "keys": ["ctrl", "w"] }""", "^w")]
    [InlineData(StepTool.PressKeys, """{ "keys": "tab ctrl+q" }""", "^q")]
    [InlineData(StepTool.PressKeys, """{ "keys": "win+l" }""", "#l")]
    [InlineData(StepTool.SendKeys, """{ "keys": "%{F4}" }""", "%{F4}")]
    [InlineData(StepTool.SendKeys, """{ "keys": "abc^w" }""", "^w")]
    public void Check_RefusesWhatAStepMayNotDo_ThePasteMethodsFileOutputClosingTheHostTargetAndTheDesktopScope(StepTool tool, string args, string reason)
    {
        Refusal(() => Check(Step(StepTool.FocusWindow), Step(tool, args))).Should().Contain("Step 2").And.Contain(reason);
    }

    [Theory]
    [InlineData(StepTool.TypeText, """{ "text": "a", "method": "keys" }""")]
    [InlineData(StepTool.TypeText, """{ "text": "a", "method": "unicode", "allowHostTarget": false }""")]
    [InlineData(StepTool.ElementAction, """{ "action": "set_value", "value": "a", "mode": "type" }""")]
    [InlineData(StepTool.CaptureWindow, """{ "output": "metadata", "outputPath": null }""")]
    [InlineData(StepTool.ElementAction, """{ "action": "invoke", "automationId": "ok", "scope": "windowAndPopups" }""")]
    [InlineData(StepTool.PressKeys, """{ "keys": "ctrl+a delete" }""")]
    [InlineData(StepTool.PressKeys, """{ "keys": "ctrl+s" }""")]
    [InlineData(StepTool.SendKeys, """{ "keys": "^a{DELETE}" }""")]
    [InlineData(StepTool.PressKeys, """{ "keys": "not a key at all" }""")] // its tool refuses it, with the reason
    public void Check_AcceptsTheWaysAStepMayDoIt(StepTool tool, string args)
    {
        Check(Step(tool, args)).Should().ContainSingle();
    }

    [Fact]
    public void Check_RefusesMoreThan20000CharactersOfTextInAllTheSteps()
    {
        var half = new string('a', 10_001);

        Refusal(() => Check(Step(StepTool.TypeText, $$"""{ "text": "{{half}}" }"""), Step(StepTool.TypeText, $$"""{ "text": "{{half}}" }""")))
            .Should().Contain("20002 characters").And.Contain("at most 20000");
    }

    [Theory]
    [InlineData("$steps.later.elements[0].ref", "not an earlier step")]
    [InlineData("$steps.find", "no reference")]
    [InlineData("$steps.find.elements[0].name", "identifier only")]
    [InlineData("$steps.find.elements[0].value", "identifier only")]
    [InlineData("$steps.find.window.title", "identifier only")]
    [InlineData("$steps.find.elements[0].x", "identifier only")]
    [InlineData("$steps.find.elements[0].ref[1]", "identifier only")]
    [InlineData("$steps.find.elements[x].ref", "property names")]
    public void Check_RefusesAReferenceToALaterStep_OrToTextFromAWindow(string reference, string reason)
    {
        Refusal(() => Check(
                Step(StepTool.FindUiElements, """{ "name": "Name" }""", id: "find"),
                Step(StepTool.ElementAction, $$"""{ "action": "invoke", "ref": "{{reference}}" }"""),
                Step(StepTool.GetFocusedElement, id: "later")))
            .Should().Contain("Step 2").And.Contain(reason);
    }

    [Theory]
    [InlineData("$steps.find.elements[0].ref")]
    [InlineData("$steps.find.elements[12].clickPoint.x")]
    [InlineData("$steps.find.element.ref")]
    [InlineData("$steps.find.window.handle")]
    [InlineData("$steps.find.captureId")]
    public void Check_AcceptsAReferenceToAnEarlierStepsIdentifier(string reference)
    {
        Check(Step(StepTool.FindUiElements, """{ "name": "Name" }""", id: "find"),
                Step(StepTool.ElementAction, $$"""{ "action": "invoke", "ref": "{{reference}}" }"""))
            .Should().HaveCount(2);
    }

    // ---- The arguments each step gets ----

    [Fact]
    public async Task AStepThatNamesNoWindow_GetsTheRunsWindow_UnderTheNameItsToolTakes()
    {
        var run = await RunAsync(
            Step(StepTool.FocusWindow),
            Step(StepTool.TypeText, """{ "text": "Ada" }"""),
            Step(StepTool.WaitForElement, """{ "automationId": "ok" }"""),
            Step(StepTool.WaitForWindow, """{ "window": { "titleContains": "Saved" } }"""),
            Step(StepTool.ElementAction, """{ "action": "invoke", "ref": "w1a2" }"""),
            Step(StepTool.GetFocusedElement));

        run.Result.Success.Should().BeTrue();
        var calls = _dispatcher.Calls.ToList();
        calls.Select(c => (c.Tool, c.Number)).Should().Equal(("focus_window", 1), ("type_text", 2), ("wait_for_element", 3),
            ("wait_for_window", 4), ("element_action", 5), ("get_focused_element", 6));
        calls[0].Args["handle"]!.GetValue<long>().Should().Be(Window);
        calls[1].Args["windowHandle"]!.GetValue<long>().Should().Be(Window);
        calls[2].Args["window"]!["handle"]!.GetValue<long>().Should().Be(Window);
        calls[3].Args["window"]!.ToJsonString().Should().Be("""{"titleContains":"Saved"}""", "wait_for_window's window is what it waits for");
        calls[4].Args.ContainsKey("handle").Should().BeFalse("a ref names its window");
        calls[5].Args.ContainsKey("handle").Should().BeFalse("get_focused_element takes no window");
    }

    [Theory]
    [InlineData(StepTool.PressKeys, """{ "keys": "enter", "windowHandle": null }""", "windowHandle")]
    [InlineData(StepTool.PressKeys, """{ "keys": "enter", "window": null }""", "windowHandle")]
    [InlineData(StepTool.PressKeys, """{ "keys": "enter", "window": {} }""", "windowHandle")]
    [InlineData(StepTool.PressKeys, """{ "keys": "enter", "window": { "foreground": true } }""", "windowHandle")]
    [InlineData(StepTool.TypeText, """{ "text": "a", "windowHandle": null, "window": null }""", "windowHandle")]
    [InlineData(StepTool.ElementAction, """{ "action": "invoke", "automationId": "ok", "handle": null, "ref": null }""", "handle")]
    [InlineData(StepTool.WaitForElement, """{ "automationId": "ok", "window": {} }""", "window")]
    public async Task AStepWhoseWindowIsNullOrEmpty_GetsTheRunsWindow_NotTheOneInFront(StepTool tool, string args, string injected)
    {
        // A null or empty target would otherwise count as named, and the input tools send to the window in front
        var run = await RunAsync(Step(tool, args));

        run.Result.Success.Should().BeTrue();
        var call = _dispatcher.Calls.Single();
        (injected == "window" ? call.Args["window"]!["handle"] : call.Args[injected])!.GetValue<long>().Should().Be(Window);
        call.Args.Where(p => p.Value is null).Should().BeEmpty("a null argument is left out, as the tool would read it");
        if (injected != "window")
        {
            call.Args.ContainsKey("window").Should().BeFalse("an empty or bare foreground selector names no window");
        }
    }

    [Fact]
    public async Task AStepsWindowSelector_IsResolvedOnce_AndPassedOnAsTheHandleItFound()
    {
        _windows.Setup(w => w.ResolveWindow(It.Is<WindowSelector>(s => s.ProcessName == "app"))).Returns(WindowResolution.Found(Info((nint)SameProcess)!));

        var run = await RunAsync(Step(StepTool.FocusWindow, """{ "window": { "processName": "app" } }"""));

        run.Result.Success.Should().BeTrue();
        _dispatcher.Calls.Single().Args["window"]!.ToJsonString().Should().Be($$"""{"handle":{{SameProcess}}}""",
            "the tool must act on the window the run checked, not resolve the selector again");
    }

    [Fact]
    public async Task AStepsWindowSelectorThatMatchesNoneOrSeveral_IsNotRun()
    {
        _windows.Setup(w => w.ResolveWindow(It.IsAny<WindowSelector>()))
            .Returns(WindowResolution.Failed("AmbiguousWindow", "The selector matched 2 windows."));

        var run = await RunAsync(Step(StepTool.FocusWindow, """{ "window": { "processName": "app" } }"""));

        run.Result.Steps[0].Status.Should().Be(StepStatus.Failed);
        run.Result.Steps[0].ErrorCode.Should().Be("AmbiguousWindow");
        run.Result.Steps[0].Result!["error"]!.GetValue<string>().Should().Contain("matched 2 windows").And.Contain("handle");
        _dispatcher.Calls.Should().BeEmpty();
    }

    [Fact]
    public async Task ARunOnAShellWindow_DoesNotCountTheShellsOtherWindowsAsItsOwn()
    {
        // explorer.exe (and ApplicationFrameHost) hosts unrelated windows: the taskbar, every folder window, UWP frames
        var steps = Runner().Check([Step(StepTool.TypeText, $$"""{ "text": "a", "windowHandle": {{Taskbar}} }""")], _dispatcher);

        var run = await Runner().RunAsync((nint)Explorer, steps, _dispatcher, Long, TimeSpan.Zero, null, CancellationToken.None);

        run.Result.Steps[0].ErrorCode.Should().Be("InvalidArguments");
        run.Result.Steps[0].Result!["error"]!.GetValue<string>().Should().Contain("not the run's window");
        _dispatcher.Calls.Should().BeEmpty();
    }

    [Theory]
    [InlineData("gone", "InvalidHandle")]
    [InlineData("reused", "InvalidHandle")]
    [InlineData("host", "TargetIsAgentHost")]
    public async Task TheRunsWindow_ReadAgainOnceTheGateIsHeld_RefusesTheRun_WhenItClosedChangedOrIsTheHost(string change, string code)
    {
        // Arrange - the window as it was before the wait for the gate, then as it is once the run holds it
        var reads = 0;
        _windows.Setup(w => w.GetWindowInfo((nint)Window)).Returns(() => reads++ == 0 ? Info((nint)Window) : change switch
        {
            "gone" => null,
            "reused" => Info((nint)Window)! with { ProcessId = 9_999, ProcessName = "other" },
            _ => Info((nint)Window)! with { IsAgentHost = true }
        });

        // Act
        var act = () => RunAsync(Step(StepTool.FocusWindow));

        // Assert - nothing ran, and the gate is free again
        (await act.Should().ThrowAsync<ToolErrorException>()).Which.ErrorCode.Should().Be(code);
        _dispatcher.Calls.Should().BeEmpty();
        using var gate = await InputGate.TryEnterAsync(Long, CancellationToken.None);
        gate.Should().NotBeNull();
    }

    [Fact]
    public async Task TheRunsWindow_CountedAsTheHostOnlyWhileTheAncestorsAreUnknown_IsRefused_SayingWhy()
    {
        // Arrange - once the run holds the gate, the window counts as the host's: the server cannot read its ancestors, and the
        // window's program started before it
        var reads = 0;
        _windows.Setup(w => w.GetWindowInfo((nint)Window)).Returns(() => reads++ == 0
            ? Info((nint)Window)
            : Info((nint)Window)! with { IsAgentHost = true, IsAgentHostOnlyWhileAncestorsUnknown = true });

        // Act
        var act = () => RunAsync(Step(StepTool.FocusWindow));

        // Assert - not called the agent's terminal as fact
        var refusal = (await act.Should().ThrowAsync<ToolErrorException>()).Which;
        refusal.ErrorCode.Should().Be("TargetIsAgentHost");
        refusal.Message.Should().Contain(HostGuard.AncestorsUnknownNote).And.Contain("Nothing was done");
        _dispatcher.Calls.Should().BeEmpty();
    }

    [Fact]
    public async Task TypeText_TypesAsUnicode_WhenTheStepNamesNoMethod_OrANullOne_AndKeepsKeys()
    {
        // A null method would otherwise bind to type_text's default, auto, which pastes a long text
        await RunAsync(Step(StepTool.TypeText, """{ "text": "a" }"""), Step(StepTool.TypeText, """{ "text": "b", "method": null }"""),
            Step(StepTool.TypeText, """{ "text": "c", "method": "keys" }"""));

        _dispatcher.Calls.Select(c => c.Args["method"]!.GetValue<string>()).Should().Equal("unicode", "unicode", "keys");
    }

    [Fact]
    public async Task AWaitStepsTimeout_IsCutToTheTimeTheRunHasLeft()
    {
        await RunAsync(TimeSpan.FromSeconds(5),
            Step(StepTool.WaitForElement, """{ "automationId": "ok", "timeoutMs": 60000 }"""),
            Step(StepTool.WaitForWindow, """{ "window": { "titleContains": "Saved" }, "timeoutMs": 0 }"""),
            Step(StepTool.WaitForElement, """{ "automationId": "ok" }"""));

        var timeouts = _dispatcher.Calls.Select(c => c.Args["timeoutMs"]!.GetValue<long>()).ToList();
        timeouts[0].Should().BeInRange(1, 5_000 - 250);
        timeouts[1].Should().Be(0, "a check once stays a check once");
        timeouts[2].Should().BeInRange(1, 5_000 - 250, "without timeoutMs the tool's default would be 10 s");
    }

    [Fact]
    public async Task AReference_TakesTheValueFromTheEarlierStepsResult_KeepingItsType()
    {
        _dispatcher.Results["find_ui_elements"] = """{ "success": true, "elements": [ { "ref": "w4e17", "name": "Save", "clickPoint": { "x": 410, "y": 220 } } ] }""";

        var run = await RunAsync(
            Step(StepTool.FindUiElements, """{ "name": "Save" }""", id: "find"),
            Step(StepTool.ElementAction, """{ "action": "invoke", "ref": "$steps.find.elements[0].ref" }"""),
            Step(StepTool.MouseClick, """{ "x": "$steps.find.elements[0].clickPoint.x", "y": "$steps.find.elements[0].clickPoint.y" }"""));

        run.Result.Success.Should().BeTrue();
        var calls = _dispatcher.Calls.ToList();
        calls[1].Args["ref"]!.GetValue<string>().Should().Be("w4e17");
        calls[2].Args["x"]!.GetValueKind().Should().Be(JsonValueKind.Number);
        calls[2].Args["x"]!.GetValue<long>().Should().Be(410);
        calls[2].Args["y"]!.GetValue<long>().Should().Be(220);
    }

    [Fact]
    public async Task AReferenceToAnOptionalStepThatFailed_FailsItsStep_WithoutRunningIt()
    {
        _dispatcher.Failures["find_ui_elements"] = "ElementNotFound";

        var run = await RunAsync(
            Step(StepTool.FindUiElements, """{ "name": "Save" }""", id: "find", optional: true),
            Step(StepTool.ElementAction, """{ "action": "invoke", "ref": "$steps.find.elements[0].ref" }"""));

        run.Result.Success.Should().BeFalse();
        run.Result.Steps[1].Status.Should().Be(StepStatus.Failed);
        run.Result.Steps[1].ErrorCode.Should().Be("InvalidArguments");
        run.Result.Steps[1].Result!["error"]!.GetValue<string>().Should().Contain("which failed");
        _dispatcher.Calls.Select(c => c.Tool).Should().Equal("find_ui_elements");
    }

    // ---- The run's window ----

    [Theory]
    [InlineData(Dialog)]
    [InlineData(SameProcess)]
    [InlineData(Window)]
    public async Task AStepMayNameTheRunsWindow_ADialogItOwns_OrAWindowOfItsProcess(long other)
    {
        var run = await RunAsync(Step(StepTool.TypeText, $$"""{ "text": "a", "windowHandle": {{other}} }"""));

        run.Result.Success.Should().BeTrue();
        _dispatcher.Calls.Should().ContainSingle();
    }

    [Theory]
    [InlineData(StepTool.TypeText, """{ "text": "a", "windowHandle": 2818048 }""")]
    [InlineData(StepTool.FocusWindow, """{ "handle": 2818048 }""")]
    [InlineData(StepTool.FocusWindow, """{ "window": { "handle": 2818048 } }""")]
    [InlineData(StepTool.FocusWindow, """{ "window": { "processName": "mail" } }""")]
    [InlineData(StepTool.Drag, """{ "fromX": 1, "fromY": 1, "toX": 2, "toY": 2, "toWindowHandle": 2818048 }""")]
    [InlineData(StepTool.FocusWindow, """{ "handle": 999 }""")]
    public async Task AStepThatNamesAnotherWindow_IsNotRun_AndStopsTheRun(StepTool tool, string args)
    {
        var run = await RunAsync(Step(StepTool.GetFocusedElement), Step(tool, args), Step(StepTool.GetFocusedElement));

        run.Result.Success.Should().BeFalse();
        run.Result.StoppedBy.Should().Be(StepStopReason.StepFailed);
        run.Result.Steps[1].ErrorCode.Should().Be("InvalidArguments");
        run.Result.Steps[1].Result!["error"]!.GetValue<string>().Should().Contain("not the run's window");
        run.Result.Steps[2].Status.Should().Be(StepStatus.Skipped);
        _dispatcher.Calls.Should().ContainSingle();
    }

    [Theory]
    [InlineData(StepTool.TypeText, """{ "text": "a", "windowHandle": "2818048" }""")]
    [InlineData(StepTool.FocusWindow, """{ "handle": "2818048" }""")]
    [InlineData(StepTool.FocusWindow, """{ "handle": "0002818048" }""")]
    [InlineData(StepTool.CaptureWindow, """{ "handle": "2818048" }""")]
    [InlineData(StepTool.FindUiElements, """{ "name": "Send", "handle": "2818048" }""")]
    [InlineData(StepTool.MouseClick, """{ "x": 1, "y": 1, "windowHandle": "2818048" }""")]
    [InlineData(StepTool.Drag, """{ "fromX": 1, "fromY": 1, "toX": 2, "toY": 2, "toWindowHandle": "2818048" }""")]
    [InlineData(StepTool.FocusWindow, """{ "window": { "handle": "2818048" } }""")]
    [InlineData(StepTool.FocusWindow, """{ "window": { "Handle": 2818048 } }""")]
    [InlineData(StepTool.FocusWindow, """{ "window": { "HANDLE": "2818048" } }""")]
    public async Task AStepThatNamesAnotherWindow_AsAStringOrInAnotherLetterCaseThatItsToolBinds_IsNotRun(StepTool tool, string args)
    {
        // The tools bind a number from a JSON string, and a selector's members in any letter case: the check must see the
        // window the tool would act on
        var run = await RunAsync(Step(StepTool.GetFocusedElement), Step(tool, args));

        run.Result.Success.Should().BeFalse();
        run.Result.Steps[1].ErrorCode.Should().Be("InvalidArguments");
        run.Result.Steps[1].Result!["error"]!.GetValue<string>().Should().Contain("not the run's window");
        _dispatcher.Calls.Should().ContainSingle("only the first step ran");
    }

    [Theory]
    [InlineData(StepTool.TypeText, """{ "text": "a", "windowHandle": "1706796" }""", "windowHandle")]
    [InlineData(StepTool.FocusWindow, """{ "handle": "1706796" }""", "handle")]
    public async Task AHandleGivenAsAString_IsPassedOnAsTheNumberTheToolWouldBind(StepTool tool, string args, string name)
    {
        var run = await RunAsync(Step(tool, args));

        run.Result.Success.Should().BeTrue();
        var handle = _dispatcher.Calls.Single().Args[name]!;
        handle.GetValueKind().Should().Be(JsonValueKind.Number);
        handle.GetValue<long>().Should().Be(Window);
    }

    [Theory]
    [InlineData(StepTool.TypeText, """{ "text": "a", "windowHandle": "the editor" }""", "windowHandle")]
    [InlineData(StepTool.TypeText, """{ "text": "a", "windowHandle": "17068.5" }""", "windowHandle")]
    [InlineData(StepTool.TypeText, """{ "text": "a", "windowHandle": " 1706796" }""", "windowHandle")]
    [InlineData(StepTool.FocusWindow, """{ "handle": 1706796.5 }""", "handle")]
    [InlineData(StepTool.FocusWindow, """{ "handle": true }""", "handle")]
    [InlineData(StepTool.FocusWindow, """{ "handle": { "handle": 1706796 } }""", "handle")]
    [InlineData(StepTool.FocusWindow, """{ "handle": [1706796] }""", "handle")]
    [InlineData(StepTool.Drag, """{ "fromX": 1, "fromY": 1, "toX": 2, "toY": 2, "toWindowHandle": "" }""", "toWindowHandle")]
    public void Check_RefusesAHandleThatIsNoWholeNumber_NamingTheStep(StepTool tool, string args, string name)
    {
        Refusal(() => Check(Step(StepTool.FocusWindow), Step(tool, args)))
            .Should().Contain("Step 2").And.Contain(name).And.Contain("whole number");
    }

    [Fact]
    public void Check_RefusesAHandleArgumentInAnotherLetterCase_WhichItsToolWouldNotBind()
    {
        // The SDK binds a tool's own parameters by their exact names: WindowHandle would be left out, and the keys sent to
        // the window in front
        Refusal(() => Check(Step(StepTool.TypeText, """{ "text": "a", "WindowHandle": 2818048 }""")))
            .Should().Contain("Step 1 (type_text)").And.Contain("'WindowHandle' (did you mean 'windowHandle'?)");
    }

    [Fact]
    public async Task AReferenceThatTakesAStringIntoAHandle_IsChecked_AsTheNumberItHolds_OrRefused()
    {
        // A captureId or ref is a string: one of digits binds as that window's handle, any other is no handle
        _dispatcher.Results["find_ui_elements"] = """{ "success": true, "captureId": "2818048", "elements": [ { "ref": "w4e17" } ] }""";

        var digits = await RunAsync(
            Step(StepTool.FindUiElements, """{ "name": "Save" }""", id: "find"),
            Step(StepTool.TypeText, """{ "text": "a", "windowHandle": "$steps.find.captureId" }"""));
        var letters = await RunAsync(
            Step(StepTool.FindUiElements, """{ "name": "Save" }""", id: "find"),
            Step(StepTool.TypeText, """{ "text": "a", "windowHandle": "$steps.find.elements[0].ref" }"""));

        digits.Result.Steps[1].ErrorCode.Should().Be("InvalidArguments");
        digits.Result.Steps[1].Result!["error"]!.GetValue<string>().Should().Contain("not the run's window");
        letters.Result.Steps[1].ErrorCode.Should().Be("InvalidArguments");
        letters.Result.Steps[1].Result!["error"]!.GetValue<string>().Should().Contain("whole number");
        _dispatcher.Calls.Select(c => c.Tool).Should().Equal("find_ui_elements", "find_ui_elements");
    }

    [Fact]
    public async Task AStepWhoseRefBelongsToAnotherWindow_IsNotRun()
    {
        _elements.Setup(e => e.Lookup("w9e1")).Returns(new ElementLookup("w9e1", (nint)Stranger, null, 1));

        var run = await RunAsync(Step(StepTool.ElementAction, """{ "action": "invoke", "ref": "w9e1" }"""));

        run.Result.Steps[0].ErrorCode.Should().Be("InvalidArguments");
        _dispatcher.Calls.Should().BeEmpty();
    }

    // ---- When the run stops ----

    [Fact]
    public async Task AFailedStep_StopsTheRun_AndTheStepsAfterItAreSkipped()
    {
        _dispatcher.Failures["element_action"] = "ElementNotFound";

        var run = await RunAsync(
            Step(StepTool.FocusWindow, id: "focus"),
            Step(StepTool.ElementAction, """{ "action": "invoke", "automationId": "missing" }""", id: "press"),
            Step(StepTool.TypeText, """{ "text": "a" }"""),
            Step(StepTool.PressKeys, """{ "keys": "enter" }"""));

        run.Result.Success.Should().BeFalse();
        run.Result.Ran.Should().Be(2);
        run.Result.Total.Should().Be(4);
        run.Result.StoppedBy.Should().Be(StepStopReason.StepFailed);
        run.Result.ErrorCode.Should().Be("ElementNotFound");
        run.Result.Error.Should().Contain("Step 2 (element_action, 'press') failed with ElementNotFound").And.Contain("Steps 3-4 were skipped");
        run.Result.Steps.Select(s => s.Status).Should().Equal(StepStatus.Ok, StepStatus.Failed, StepStatus.Skipped, StepStatus.Skipped);
        run.Result.Steps[0].Result.Should().BeNull("an ok step that is not the last to run returns its status only");
        run.Result.Steps[1].Result!["errorCode"]!.GetValue<string>().Should().Be("ElementNotFound");
        run.Result.Steps[2].ElapsedMs.Should().BeNull();
        _dispatcher.Calls.Select(c => c.Tool).Should().Equal("focus_window", "element_action");
    }

    [Fact]
    public async Task AnOptionalStepThatFails_DoesNotStopTheRun()
    {
        _dispatcher.Failures["wait_for_window"] = "WaitTimedOut";

        var run = await RunAsync(
            Step(StepTool.WaitForWindow, """{ "window": { "titleContains": "Tip of the day" }, "timeoutMs": 0 }""", optional: true),
            Step(StepTool.TypeText, """{ "text": "a" }"""));

        run.Result.Success.Should().BeTrue();
        run.Result.StoppedBy.Should().BeNull();
        run.Result.Steps.Select(s => s.Status).Should().Equal(StepStatus.Failed, StepStatus.Ok);
        run.Result.Steps[1].Result.Should().NotBeNull("the last step that ran returns its result");
    }

    [Fact]
    public async Task APauseBeforeAStep_StopsTheRun_WithPausedByUser()
    {
        _dispatcher.OnCall = call =>
        {
            if (call.Number == 1)
            {
                _paused = true;
            }
        };

        var run = await RunAsync(Step(StepTool.TypeText, """{ "text": "a" }"""), Step(StepTool.TypeText, """{ "text": "b" }"""));

        run.Result.StoppedBy.Should().Be(StepStopReason.Paused);
        run.Result.ErrorCode.Should().Be("PausedByUser");
        run.Result.Error.Should().Contain("never resume input yourself");
        run.Result.Steps[1].Status.Should().Be(StepStatus.Skipped);
        _dispatcher.Calls.Should().ContainSingle();
    }

    [Theory]
    [InlineData(InputControlSources.Hotkey, "the user pressed the pause hotkey (Ctrl+Alt+Pause)", "could not read the pause it shares")]
    [InlineData(InputControlSources.SharedPauseUnreadable, "this server could not read the pause it shares", "hotkey")]
    public async Task APause_StopsTheRun_SayingWhyInputIsPaused(string pausedBy, string says, string doesNotSay)
    {
        // Arrange - the server pauses itself when it cannot read the shared pause: the user pressed nothing, so the agent
        // must not tell the user they paused it, nor name the hotkey at all
        _session.Setup(s => s.PauseState).Returns(() => _paused ? new InputPauseState(true, pausedBy) : InputPauseState.Running);
        _session.Setup(s => s.PauseChord).Returns("Ctrl+Alt+Pause");
        _dispatcher.OnCall = call =>
        {
            if (call.Number == 1)
            {
                _paused = true;
            }
        };

        // Act
        var run = await RunAsync(Step(StepTool.TypeText, """{ "text": "a" }"""), Step(StepTool.TypeText, """{ "text": "b" }"""));

        // Assert
        run.Result.StoppedBy.Should().Be(StepStopReason.Paused);
        run.Result.ErrorCode.Should().Be("PausedByUser");
        run.Result.Error.Should().Contain("Input is paused").And.Contain(says).And.NotContain(doesNotSay)
            .And.Contain("before step 2 (type_text)").And.NotContain("The user paused input").And.Contain("never resume input yourself");
    }

    [Fact]
    public async Task AStepThePauseStopped_StopsTheRun_EvenWhenItIsOptional()
    {
        _dispatcher.Failures["type_text"] = "PausedByUser";

        var run = await RunAsync(Step(StepTool.TypeText, """{ "text": "a" }""", optional: true), Step(StepTool.PressKeys, """{ "keys": "enter" }"""));

        run.Result.StoppedBy.Should().Be(StepStopReason.Paused);
        run.Result.ErrorCode.Should().Be("PausedByUser");
        _dispatcher.Calls.Should().ContainSingle();
    }

    [Fact]
    public async Task APauseDuringTheWaitBetweenSteps_StopsTheRun()
    {
        _dispatcher.OnCall = _ =>
        {
            _paused = true;
            _pause.Cancel();
        };
        var steps = Runner().Check([Step(StepTool.TypeText, """{ "text": "a" }"""), Step(StepTool.TypeText, """{ "text": "b" }""")], _dispatcher);

        var run = await Runner().RunAsync((nint)Window, steps, _dispatcher, Long, TimeSpan.FromSeconds(20), null, CancellationToken.None);

        run.Result.StoppedBy.Should().Be(StepStopReason.Paused);
        run.Result.Steps[1].Status.Should().Be(StepStatus.Skipped);
    }

    [Fact]
    public async Task InputPausedBeforeTheRun_RefusesItWithPausedByUser_AndRunsNothing()
    {
        _paused = true;

        var act = () => RunAsync(Step(StepTool.TypeText, """{ "text": "a" }"""));

        (await act.Should().ThrowAsync<ToolErrorException>()).Which.ErrorCode.Should().Be("PausedByUser");
        _dispatcher.Calls.Should().BeEmpty();
    }

    [Fact]
    public async Task AStepStillRunningWhenTheTimeRunsOut_IsCancelled_AndTheRunStopsWithOperationTimedOut()
    {
        _dispatcher.Delays["element_action"] = Long;

        var run = await RunAsync(TimeSpan.FromMilliseconds(1_000),
            Step(StepTool.FocusWindow), Step(StepTool.ElementAction, """{ "action": "invoke", "name": "Slow" }"""), Step(StepTool.FocusWindow));

        run.Result.StoppedBy.Should().Be(StepStopReason.TimedOut);
        run.Result.ErrorCode.Should().Be("OperationTimedOut");
        run.Result.Error.Should().Contain("maxDurationMs (1000) ran out during step 2");
        run.Result.Steps.Select(s => s.Status).Should().Equal(StepStatus.Ok, StepStatus.Failed, StepStatus.Skipped);
        run.Result.Steps[1].ErrorCode.Should().Be("OperationTimedOut");
        run.Result.ElapsedMs.Should().BeLessThan(10_000);
    }

    [Fact]
    public async Task AWaitCutToTheTimeLeft_ThatTimesOut_StopsTheRunAsTimedOut()
    {
        _dispatcher.OnCall = call => Thread.Sleep(call.Tool == "wait_for_element" ? (int)call.Args["timeoutMs"]!.GetValue<long>() : 0);
        _dispatcher.Failures["wait_for_element"] = "WaitTimedOut";

        var run = await RunAsync(TimeSpan.FromMilliseconds(1_000),
            Step(StepTool.WaitForElement, """{ "automationId": "never" }"""), Step(StepTool.FocusWindow));

        run.Result.StoppedBy.Should().Be(StepStopReason.TimedOut);
        run.Result.ErrorCode.Should().Be("OperationTimedOut");
        run.Result.Steps[0].ErrorCode.Should().Be("WaitTimedOut");
    }

    [Fact]
    public async Task WhenTheUserTakesTheForeground_TheRunStopsWithTargetNotForeground_InsteadOfTakingItBack()
    {
        _dispatcher.OnCall = call =>
        {
            if (call.Number == 2)
            {
                _foreground = Stranger; // the user clicked another application after step 2
            }
        };

        var run = await RunAsync(
            Step(StepTool.FocusWindow), Step(StepTool.TypeText, """{ "text": "a" }"""), Step(StepTool.PressKeys, """{ "keys": "enter" }"""));

        run.Result.StoppedBy.Should().Be(StepStopReason.ForegroundMoved);
        run.Result.ErrorCode.Should().Be("TargetNotForeground");
        run.Result.Error.Should().Contain("Before step 3 (press_keys)").And.Contain("process mail").And.Contain("ask the user");
        run.Result.Steps.Select(s => s.Status).Should().Equal(StepStatus.Ok, StepStatus.Ok, StepStatus.Skipped);
        _dispatcher.Calls.Should().HaveCount(2);
    }

    [Fact]
    public async Task WhenTheUserTakesTheForeground_RightAfterFocusWindowBroughtTheRunsWindowToTheFront_TheRunStops()
    {
        // The user clicks another application before the runner looks at the foreground after focus_window: the window
        // was in front all the same, since focus_window succeeded
        _dispatcher.OnCall = call =>
        {
            if (call.Number == 1)
            {
                _foreground = Stranger;
            }
        };

        var run = await RunAsync(Step(StepTool.FocusWindow), Step(StepTool.PressKeys, """{ "keys": "enter" }"""));

        run.Result.StoppedBy.Should().Be(StepStopReason.ForegroundMoved);
        run.Result.ErrorCode.Should().Be("TargetNotForeground");
        run.Result.Steps.Select(s => s.Status).Should().Equal(StepStatus.Ok, StepStatus.Skipped);
        _dispatcher.Calls.Should().HaveCount(1);
    }

    [Fact]
    public async Task ADialogOfTheRunsWindowInFront_DoesNotStopTheRun()
    {
        _dispatcher.OnCall = call => _foreground = call.Number == 1 ? Window : Dialog;

        var run = await RunAsync(Step(StepTool.FocusWindow), Step(StepTool.ElementAction, """{ "action": "invoke", "name": "Open" }"""),
            Step(StepTool.TypeText, """{ "text": "a.txt", "windowHandle": 1706896 }"""), Step(StepTool.PressKeys, """{ "keys": "enter" }"""));

        run.Result.Success.Should().BeTrue();
    }

    [Fact]
    public async Task ARunThatNeverHadTheForeground_IsNotStoppedByTheWindowInFront()
    {
        // Pattern actions do not need the foreground: the agent's terminal stays in front for the whole run
        _foreground = Terminal;

        var run = await RunAsync(
            Step(StepTool.ElementAction, """{ "action": "set_value", "automationId": "name", "value": "Ada" }"""),
            Step(StepTool.ElementAction, """{ "action": "toggle", "automationId": "agree" }"""));

        run.Result.Success.Should().BeTrue();
    }

    // ---- The gate, progress, images ----

    [Fact]
    public async Task TheRunHoldsTheGateForAllItsSteps_WhichPassIt_WhileOtherFlowsWait()
    {
        var inStep = new ConcurrentBag<bool>();
        var otherFlow = new ConcurrentBag<bool>();
        _dispatcher.OnCall = _ =>
        {
            using (var gate = InputGate.TryEnter(TimeSpan.FromMilliseconds(100), CancellationToken.None))
            {
                inStep.Add(gate != null);
            }

            bool? got = null;
            Thread other;
            using (ExecutionContext.SuppressFlow())
            {
                other = new Thread(() =>
                {
                    using var gate = InputGate.TryEnter(TimeSpan.FromMilliseconds(100), CancellationToken.None);
                    got = gate != null;
                });
                other.Start();
            }

            other.Join();
            otherFlow.Add(got!.Value);
        };

        await RunAsync(Step(StepTool.FocusWindow), Step(StepTool.TypeText, """{ "text": "a" }"""));

        inStep.Should().Equal(true, true);
        otherFlow.Should().Equal(false, false);
        InputGate.HeldByThisFlow().Should().BeFalse("the lease belonged to the run, not to its caller");
        using var after = await Task.Run(() => InputGate.TryEnter(Long, CancellationToken.None));
        after.Should().NotBeNull("the run released the gate");
        _session.Verify(s => s.ReleaseHeldInput("the end of run_steps"), Times.Once);
    }

    [Fact]
    public async Task ACancelledRun_CancelsItsStep_AndReleasesTheGate()
    {
        _dispatcher.Delays["type_text"] = Long;
        using var cancel = new CancellationTokenSource();
        _dispatcher.OnCall = _ => cancel.CancelAfter(100);
        var steps = Check(Step(StepTool.TypeText, """{ "text": "a" }"""), Step(StepTool.PressKeys, """{ "keys": "enter" }"""));

        var act = () => Runner().RunAsync((nint)Window, steps, _dispatcher, Long, TimeSpan.Zero, null, cancel.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
        _dispatcher.Calls.Should().ContainSingle();
        using var after = await Task.Run(() => InputGate.TryEnter(Long, CancellationToken.None));
        after.Should().NotBeNull();
    }

    [Fact]
    public async Task EachStep_ReportsTheRunsProgress()
    {
        var reports = new List<OperationProgress>();
        var progress = new SynchronousProgress(reports.Add);
        _dispatcher.Failures["press_keys"] = "TargetNotForeground";
        var steps = Check(Step(StepTool.FocusWindow), Step(StepTool.PressKeys, """{ "keys": "enter" }"""), Step(StepTool.FocusWindow));

        await Runner().RunAsync((nint)Window, steps, _dispatcher, Long, TimeSpan.Zero, progress, CancellationToken.None);

        reports.Select(r => $"{r.Completed}/{r.Total} {r.Message}").Should().Equal(
            "1/3 step 1 of 3: focus_window ok", "2/3 step 2 of 3: press_keys failed");
    }

    [Fact]
    public async Task TheImagesOfTheStepsWithReturnAndTheLastStep_AreReturned_AtMostTwo()
    {
        _dispatcher.Images.Add("capture_window");

        var run = await RunAsync(
            Step(StepTool.CaptureWindow, @return: true),
            Step(StepTool.CaptureWindow),
            Step(StepTool.CaptureWindow, @return: true),
            Step(StepTool.CaptureWindow));

        run.Images.Should().HaveCount(2);
        run.Result.Steps.Select(s => s.Result != null).Should().Equal(true, false, true, true);
    }

    [Fact]
    public async Task WhenMoreThanTwoImagesQualify_TheLastStepsImageIsKept()
    {
        _dispatcher.Images.Add("capture_window");

        var run = await RunAsync(
            Step(StepTool.CaptureWindow, @return: true),
            Step(StepTool.CaptureWindow, @return: true),
            Step(StepTool.CaptureWindow));

        run.Images.Should().Equal(_dispatcher.ImagesByStep[1], _dispatcher.ImagesByStep[3]);
    }

    [Fact]
    public async Task TheStepsResults_AreBoundedInAll_KeepingTheLastStepsResult_AndSayingWhichWereLeftOut()
    {
        // Arrange - every find returns about 8,000 characters
        var elements = string.Join(", ", Enumerable.Range(0, 100).Select(i => $$"""{ "ref": "w1e{{i}}", "name": "{{new string('x', 50)}}" }"""));
        _dispatcher.Results["find_ui_elements"] = $$"""{ "success": true, "elements": [ {{elements}} ] }""";
        var steps = Enumerable.Range(0, 6).Select(_ => Step(StepTool.FindUiElements, """{ "name": "x" }""", @return: true)).ToArray();

        // Act
        var run = await RunAsync(steps);

        // Assert
        var returned = run.Result.Steps.Where(s => s.Result != null).ToList();
        returned.Sum(s => s.Result!.ToJsonString().Length).Should().BeLessThanOrEqualTo(StepRunner.MaxResultTextLength);
        run.Result.Steps[^1].Result.Should().NotBeNull("the last step's result comes first");
        run.Result.Steps.Where(s => s.Result == null).Should().NotBeEmpty().And.OnlyContain(s => s.ResultOmitted == true);
        returned.Should().OnlyContain(s => s.ResultOmitted == null);
    }

    // ---- Takeover detection (roadmap M7.4) ----

    private Task<StepRunOutcome> RunWatchedAsync(StubTakeoverMonitor monitor, TimeSpan delayBetweenSteps, params RunStep[] steps)
    {
        var runner = new StepRunner(_session.Object, _windows.Object, _elements.Object, takeover: monitor);
        return runner.RunAsync((nint)Window, runner.Check(steps, _dispatcher), _dispatcher, Long, delayBetweenSteps, null,
            CancellationToken.None);
    }

    [Fact]
    public async Task ModeStop_TheUsersInputDuringAStep_StopsTheRunBeforeTheNext_WithUserInterrupted()
    {
        // Arrange - the user takes the mouse while step 1 (a click: one batch, not watched itself) runs
        var monitor = new StubTakeoverMonitor(TakeoverMode.Stop);
        _dispatcher.OnCall = call =>
        {
            if (call.Number == 1)
            {
                monitor.Last!.Signal();
            }
        };

        // Act
        var run = await RunWatchedAsync(monitor, TimeSpan.Zero,
            Step(StepTool.MouseClick, """{ "x": 10, "y": 10 }"""), Step(StepTool.TypeText, """{ "text": "b" }"""));

        // Assert
        run.Result.StoppedBy.Should().Be(StepStopReason.UserInterrupted);
        run.Result.ErrorCode.Should().Be("UserInterrupted");
        run.Result.Error.Should().Contain("before step 2").And.Contain("WAM_TAKEOVER_DETECTION=stop");
        run.Result.Steps[0].Status.Should().Be(StepStatus.Ok);
        run.Result.Steps[1].Status.Should().Be(StepStatus.Skipped);
        _dispatcher.Calls.Should().ContainSingle();
        monitor.Watches.Should().Be(1);
        monitor.Last!.Disposed.Should().BeTrue("the watch ends with the run");
    }

    [Fact]
    public async Task ModeStop_TheUsersInputDuringTheWaitBetweenSteps_EndsTheWait()
    {
        // Arrange - a 20-second wait between steps would hold the gate long after the user took over
        var monitor = new StubTakeoverMonitor(TakeoverMode.Stop);
        _dispatcher.OnCall = _ => monitor.Last!.Signal();

        // Act
        var run = await RunWatchedAsync(monitor, TimeSpan.FromSeconds(20),
            Step(StepTool.TypeText, """{ "text": "a" }"""), Step(StepTool.TypeText, """{ "text": "b" }"""));

        // Assert
        run.Result.StoppedBy.Should().Be(StepStopReason.UserInterrupted);
        run.Result.ElapsedMs.Should().BeLessThan(10_000);
        run.Result.Steps[1].Status.Should().Be(StepStatus.Skipped);
    }

    [Fact]
    public async Task AStepTheUsersInputStopped_StopsTheRun_EvenWhenItIsOptional()
    {
        // A type_text step watches itself and returns UserInterrupted
        _dispatcher.Failures["type_text"] = "UserInterrupted";

        var run = await RunWatchedAsync(new StubTakeoverMonitor(TakeoverMode.Stop), TimeSpan.Zero,
            Step(StepTool.TypeText, """{ "text": "a" }""", optional: true), Step(StepTool.PressKeys, """{ "keys": "enter" }"""));

        run.Result.StoppedBy.Should().Be(StepStopReason.UserInterrupted);
        run.Result.Error.Should().Contain("during step 1");
        _dispatcher.Calls.Should().ContainSingle();
    }

    [Fact]
    public async Task ModeWarn_TheUsersInput_LetsTheRunFinish_WithAWarning()
    {
        // Arrange
        var monitor = new StubTakeoverMonitor(TakeoverMode.Warn);
        _dispatcher.OnCall = _ => monitor.Last!.Signal();

        // Act
        var run = await RunWatchedAsync(monitor, TimeSpan.Zero,
            Step(StepTool.TypeText, """{ "text": "a" }"""), Step(StepTool.TypeText, """{ "text": "b" }"""));

        // Assert
        run.Result.Success.Should().BeTrue();
        run.Result.StoppedBy.Should().BeNull();
        run.Result.Warning.Should().Contain("WAM_TAKEOVER_DETECTION=warn").And.Contain("the steps ran");
        _dispatcher.Calls.Should().HaveCount(2);
    }

    [Fact]
    public async Task WithoutTheUsersInput_TheWatchedRunHasNoWarning()
    {
        var run = await RunWatchedAsync(new StubTakeoverMonitor(TakeoverMode.Warn), TimeSpan.Zero, Step(StepTool.TypeText, """{ "text": "a" }"""));

        run.Result.Success.Should().BeTrue();
        run.Result.Warning.Should().BeNull();
    }

    private sealed class SynchronousProgress(Action<OperationProgress> report) : IProgress<OperationProgress>
    {
        public void Report(OperationProgress value) => report(value);
    }

    /// <summary>A step call the fake dispatcher saw.</summary>
    private sealed record Call(string Tool, JsonObject Args, int Number);

    /// <summary>
    /// Serves the step tools with the parameters that matter here, as the real schemas declare them, and answers each call
    /// with success (or the failure a test sets) without doing anything.
    /// </summary>
    private sealed class FakeDispatcher : IStepDispatcher
    {
        private static readonly Dictionary<string, string[]> Parameters = new(StringComparer.Ordinal)
        {
            ["focus_window"] = ["handle", "window"],
            ["find_ui_elements"] = ["handle", "window", "name", "automationId", "controlType", "withinRef", "scope"],
            ["element_action"] = ["action", "ref", "handle", "window", "name", "automationId", "value", "mode", "scope", "timeoutMs"],
            ["type_text"] = ["text", "method", "windowHandle", "window", "allowHostTarget"],
            ["send_keys"] = ["keys", "windowHandle", "window", "allowHostTarget"],
            ["press_keys"] = ["keys", "windowHandle", "window", "allowHostTarget"],
            ["mouse_click"] = ["x", "y", "windowHandle", "window", "captureId", "allowHostTarget"],
            ["drag"] = ["fromX", "fromY", "toX", "toY", "windowHandle", "window", "toWindowHandle", "allowHostTarget"],
            ["mouse_move"] = ["x", "y", "windowHandle", "window"],
            ["scroll"] = ["windowHandle", "window", "deltaY"],
            ["wait_for_window"] = ["window", "condition", "timeoutMs"],
            ["wait_for_element"] = ["window", "ref", "name", "automationId", "scope", "condition", "expected", "timeoutMs"],
            ["get_focused_element"] = ["timeoutMs"],
            ["capture_window"] = ["handle", "window", "ref", "output", "outputPath", "overwrite", "saveToFile"],
        };

        public HashSet<string> NotServed { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, string> Failures { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, string> Results { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, TimeSpan> Delays { get; } = new(StringComparer.Ordinal);
        public HashSet<string> Images { get; } = new(StringComparer.Ordinal);
        public ConcurrentDictionary<int, ImageContentBlock> ImagesByStep { get; } = new();
        public ConcurrentQueue<Call> Calls { get; } = new();
        public Action<Call>? OnCall { get; set; }

        public JsonElement? InputSchemaOf(string tool)
        {
            if (NotServed.Contains(tool) || !Parameters.TryGetValue(tool, out var names))
            {
                return null;
            }

            var properties = new JsonObject();
            foreach (var name in names)
            {
                properties[name] = new JsonObject
                {
                    ["type"] = name is "window" ? "object" : name.EndsWith("Handle", StringComparison.Ordinal) || name is "handle" or "x" or "y" ? "integer" : "string"
                };
            }

            return JsonSerializer.SerializeToElement(new JsonObject { ["type"] = "object", ["properties"] = properties });
        }

        public async Task<CallToolResult> InvokeAsync(string tool, JsonObject arguments, int stepNumber, CancellationToken cancellationToken)
        {
            var call = new Call(tool, arguments, stepNumber);
            Calls.Enqueue(call);
            OnCall?.Invoke(call);
            if (Delays.TryGetValue(tool, out var delay))
            {
                await Task.Delay(delay, cancellationToken);
            }

            var text = Failures.TryGetValue(tool, out var code)
                ? $$"""{ "success": false, "errorCode": "{{code}}", "error": "{{tool}} failed." }"""
                : Results.GetValueOrDefault(tool, """{ "success": true }""");
            var content = new List<ContentBlock>();
            if (Images.Contains(tool))
            {
                var image = ImageContentBlock.FromBytes(new byte[] { 0x89, 0x50, 0x4E, 0x47, (byte)stepNumber }, "image/png");
                ImagesByStep[stepNumber] = image;
                content.Add(image);
            }

            content.Add(new TextContentBlock { Text = text });
            return new CallToolResult { Content = content, IsError = Failures.ContainsKey(tool) };
        }
    }
}
