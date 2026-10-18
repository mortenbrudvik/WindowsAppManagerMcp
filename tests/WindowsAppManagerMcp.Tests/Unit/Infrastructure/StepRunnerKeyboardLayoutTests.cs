using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol.Protocol;
using WindowsAppManagerMcp.Infrastructure;

namespace WindowsAppManagerMcp.Tests.Unit.Infrastructure;

/// <summary>
/// run_steps' rule that a step closes nothing, on the keyboard layout of the window a step's keys go to (BACKLOG.md RF.F11).
/// A send_keys or press_keys step maps a chord's character on that window's layout, so a character the layout types on a
/// closing key is that chord: ctrl+ц is Ctrl+W on a Russian layout, and ctrl+; Ctrl+Q on a Greek one, which the check
/// before the run, on the invariant layout, sees as a character with no key. Such keys refuse the run before anything
/// runs, and a step whose window's layout changed during the run is not run. A layout that cannot be read fails closed.
/// With <see cref="FakeKeyboardLayoutWrapper"/> and a fake dispatcher: nothing reaches the desktop.
/// </summary>
public class StepRunnerKeyboardLayoutTests
{
    private const long Window = 0x1A0B2C;
    private const long Dialog = 0x1A0B90; // owned by Window, its thread on a layout of its own

    private readonly Mock<IInputSession> _session = new();
    private readonly Mock<IWindowService> _windows = new();
    private readonly Mock<IElementRegistry> _elements = new();
    private readonly FakeKeyboardLayoutWrapper _layouts = new();
    private readonly KeysDispatcher _dispatcher = new();
    private readonly CancellationTokenSource _pause = new();

    public StepRunnerKeyboardLayoutTests()
    {
        _session.Setup(s => s.IsPaused).Returns(false);
        _session.Setup(s => s.PauseToken).Returns(() => _pause.Token);
        _session.Setup(s => s.ReleaseHeldInput(It.IsAny<string>())).Returns(InputReleaseOutcome.Nothing);
        _windows.Setup(w => w.GetWindowInfo(It.IsAny<nint>())).Returns((nint h) => Info(h));
        _windows.Setup(w => w.GetForegroundWindow()).Returns(() => Info((nint)Window));
    }

    private static WindowInfo? Info(nint handle) => (long)handle switch
    {
        Window => new WindowInfo(handle, "Form", "app", 4_100, new WindowRect(0, 0, 800, 600), WindowState.Normal, true, 0),
        Dialog => new WindowInfo(handle, "Open", "dialoghost", 4_200, new WindowRect(0, 0, 400, 300), WindowState.Normal, true, 0)
        {
            OwnerHandle = (nint)Window
        },
        _ => null
    };

    private static nint Layout(string name) => name switch
    {
        "Russian" => FakeKeyboardLayoutWrapper.Russian,
        "Greek" => FakeKeyboardLayoutWrapper.Greek,
        "Us" => FakeKeyboardLayoutWrapper.Us,
        _ => throw new ArgumentOutOfRangeException(nameof(name), name, null)
    };

    private StepRunner Runner() => new(_session.Object, _windows.Object, _elements.Object, keyboardLayout: _layouts);

    private static RunStep Step(StepTool tool, string args) => new(tool, JsonNode.Parse(args)!.AsObject(), null, false, false);

    private static RunStep Typing() => Step(StepTool.TypeText, """{ "text": "a" }""");

    private Task<StepRunOutcome> RunAsync(params RunStep[] steps)
    {
        var runner = Runner();
        return runner.RunAsync((nint)Window, runner.Check(steps, _dispatcher), _dispatcher, TimeSpan.FromSeconds(10), TimeSpan.Zero,
            null, CancellationToken.None);
    }

    [Theory]
    [InlineData(StepTool.PressKeys, """{ "keys": "ctrl+ц" }""", "Russian", "^w")]
    [InlineData(StepTool.PressKeys, """{ "keys": ["ctrl", "ц"] }""", "Russian", "^w")]
    [InlineData(StepTool.SendKeys, """{ "keys": "^ц" }""", "Russian", "^w")]
    [InlineData(StepTool.PressKeys, """{ "keys": "tab ctrl+shift+й" }""", "Russian", "^q")]
    [InlineData(StepTool.SendKeys, """{ "keys": "abc^й" }""", "Russian", "^q")]
    [InlineData(StepTool.PressKeys, """{ "keys": "win+д" }""", "Russian", "#l")]
    [InlineData(StepTool.PressKeys, """{ "keys": "ctrl+ς" }""", "Greek", "^w")]
    // ASCII punctuation as well: refusing only the chords on a non-ASCII character would let this one through
    [InlineData(StepTool.PressKeys, """{ "keys": "ctrl+;" }""", "Greek", "^q")]
    [InlineData(StepTool.SendKeys, """{ "keys": "^;" }""", "Greek", "^q")]
    public async Task ACharacterTheWindowsLayoutTypesOnAClosingKey_RefusesTheRun_BeforeAnyStepRuns(StepTool tool, string args,
        string layout, string chord)
    {
        // Arrange - the check before the run reads the keys on the invariant layout, where the character has no key
        _layouts.WindowLayouts[(nint)Window] = Layout(layout);
        var steps = Runner().Check([Typing(), Step(tool, args)], _dispatcher);

        // Act
        var act = () => Runner().RunAsync((nint)Window, steps, _dispatcher, TimeSpan.FromSeconds(10), TimeSpan.Zero, null,
            CancellationToken.None);

        // Assert
        var error = (await act.Should().ThrowAsync<ToolErrorException>()).Which;
        error.ErrorCode.Should().Be("InvalidArguments");
        error.Message.Should().Contain("Step 2").And.Contain(chord).And.Contain("keyboard layout of window")
            .And.Contain("Nothing was done: the whole run was refused.");
        _dispatcher.Calls.Should().BeEmpty("the whole run is refused before its first step");
    }

    [Theory]
    [InlineData("""{ "keys": "ctrl+ц" }""", "Us")] // no key types ц there: it goes as the character, which closes nothing
    [InlineData("""{ "keys": "ctrl+ы" }""", "Russian")] // Ctrl+S
    [InlineData("""{ "keys": "ctrl+;" }""", "Russian")] // Ctrl+Shift+4: ';' is Shift+4 there
    public async Task TheSameKindOfChord_OnALayoutThatTypesItOnAnotherKey_Runs(string args, string layout)
    {
        _layouts.WindowLayouts[(nint)Window] = Layout(layout);

        var run = await RunAsync(Step(StepTool.PressKeys, args));

        run.Result.Error.Should().BeNull();
        run.Result.Success.Should().BeTrue();
        _dispatcher.Calls.Should().ContainSingle();
    }

    [Fact]
    public async Task ALayoutThatChangedDuringTheRun_RefusesTheStep_BeforeItRuns()
    {
        // Arrange - the window's layout is US when the run starts, and Russian once step 1 has run (a language hotkey, the user)
        _layouts.WindowLayouts[(nint)Window] = FakeKeyboardLayoutWrapper.Us;
        _dispatcher.OnCall = call =>
        {
            if (call.Number == 1)
            {
                _layouts.WindowLayouts[(nint)Window] = FakeKeyboardLayoutWrapper.Russian;
            }
        };

        // Act
        var run = await RunAsync(Typing(), Step(StepTool.PressKeys, """{ "keys": "ctrl+ц" }"""), Typing());

        // Assert
        run.Result.Success.Should().BeFalse();
        run.Result.StoppedBy.Should().Be(StepStopReason.StepFailed);
        run.Result.ErrorCode.Should().Be("InvalidArguments");
        run.Result.Error.Should().Contain("Step 2 (press_keys) was not run").And.Contain("^w");
        run.Result.Steps.Select(s => s.Status).Should().Equal(StepStatus.Ok, StepStatus.Failed, StepStatus.Skipped);
        _dispatcher.Calls.Should().ContainSingle("the step with the closing chord never reaches its tool").Which.Tool.Should().Be("type_text");
    }

    [Fact]
    public async Task AStepToADialog_IsReadOnTheDialogsLayout()
    {
        // Arrange - the dialog's thread has a Russian layout, the run's window a US one
        _layouts.WindowLayouts[(nint)Window] = FakeKeyboardLayoutWrapper.Us;
        _layouts.WindowLayouts[(nint)Dialog] = FakeKeyboardLayoutWrapper.Russian;

        // Act
        var act = () => RunAsync(Step(StepTool.SendKeys, $$"""{ "keys": "^ц", "windowHandle": {{Dialog}} }"""));

        // Assert
        (await act.Should().ThrowAsync<ToolErrorException>()).Which.Message.Should().Contain("^w").And.Contain($"window {Dialog}");
        _dispatcher.Calls.Should().BeEmpty();
    }

    [Fact]
    public async Task AStepToADialogNamedByAReference_IsReadOnTheDialogsLayout_BeforeItRuns()
    {
        // Arrange - the dialog's handle is known only once step 1 has run, so only the check before step 2 reads its layout
        _layouts.WindowLayouts[(nint)Window] = FakeKeyboardLayoutWrapper.Us;
        _layouts.WindowLayouts[(nint)Dialog] = FakeKeyboardLayoutWrapper.Russian;
        _dispatcher.Results["type_text"] = $$"""{ "success": true, "window": { "handle": {{Dialog}} } }""";
        var find = new RunStep(StepTool.TypeText, JsonNode.Parse("""{ "text": "a" }""")!.AsObject(), "find", false, false);

        // Act
        var run = await RunAsync(find, Step(StepTool.PressKeys, """{ "keys": "ctrl+ц", "windowHandle": "$steps.find.window.handle" }"""));

        // Assert
        run.Result.StoppedBy.Should().Be(StepStopReason.StepFailed);
        run.Result.Error.Should().Contain("Step 2 (press_keys) was not run").And.Contain("^w").And.Contain($"window {Dialog}");
        _dispatcher.Calls.Should().ContainSingle();
    }

    [Fact]
    public async Task AStepToADialogNamedByAWindowSelectorsHandle_IsReadOnTheDialogsLayout()
    {
        // Arrange - a literal selector handle names the dialog before the run, as a literal windowHandle does
        _layouts.WindowLayouts[(nint)Window] = FakeKeyboardLayoutWrapper.Us;
        _layouts.WindowLayouts[(nint)Dialog] = FakeKeyboardLayoutWrapper.Russian;

        // Act
        var act = () => RunAsync(Step(StepTool.SendKeys, $$"""{ "keys": "^ц", "window": { "handle": {{Dialog}} } }"""));

        // Assert
        var error = (await act.Should().ThrowAsync<ToolErrorException>()).Which;
        error.ErrorCode.Should().Be("InvalidArguments");
        error.Message.Should().Contain("^w").And.Contain($"window {Dialog}").And.Contain("Nothing was done: the whole run was refused.");
        _dispatcher.Calls.Should().BeEmpty();
    }

    [Fact]
    public async Task AStepToADialogNamedByAWindowSelector_IsReadOnTheDialogsLayout_BeforeItRuns()
    {
        // Arrange - the selector names the dialog only once it is resolved, before the step (it is then passed on as its
        // handle), so only the check before step 2 reads the dialog's layout
        _layouts.WindowLayouts[(nint)Window] = FakeKeyboardLayoutWrapper.Us;
        _layouts.WindowLayouts[(nint)Dialog] = FakeKeyboardLayoutWrapper.Russian;
        _windows.Setup(w => w.ResolveWindow(It.Is<WindowSelector>(s => s.TitleEquals == "Open")))
            .Returns(WindowResolution.Found(Info((nint)Dialog)!));

        // Act
        var run = await RunAsync(Typing(), Step(StepTool.PressKeys, """{ "keys": "ctrl+ц", "window": { "titleEquals": "Open" } }"""));

        // Assert
        run.Result.StoppedBy.Should().Be(StepStopReason.StepFailed);
        run.Result.ErrorCode.Should().Be("InvalidArguments");
        run.Result.Error.Should().Contain("Step 2 (press_keys) was not run").And.Contain("^w").And.Contain($"window {Dialog}");
        _dispatcher.Calls.Should().ContainSingle().Which.Tool.Should().Be("type_text");
    }

    [Theory]
    [InlineData(StepTool.PressKeys, """{ "keys": "ctrl+ц" }""", "^ц")]
    [InlineData(StepTool.SendKeys, """{ "keys": "%ς" }""", "%ς")]
    [InlineData(StepTool.PressKeys, """{ "keys": "ctrl+;" }""", "^;")]
    public async Task AWindowWhoseLayoutCannotBeRead_RefusesAChordOnACharacterWithNoKeyOfItsOwn(StepTool tool, string args, string chord)
    {
        // Arrange - Windows gives no layout for the window's thread; its tool would map the character on whatever it reads then
        _layouts.WindowLayouts[(nint)Window] = 0;

        // Act
        var act = () => RunAsync(Step(tool, args));

        // Assert
        var error = (await act.Should().ThrowAsync<ToolErrorException>()).Which;
        error.ErrorCode.Should().Be("InvalidArguments");
        error.Message.Should().Contain("could not be read").And.Contain(chord).And.Contain("Nothing was done");
        _dispatcher.Calls.Should().BeEmpty();
    }

    [Theory]
    [InlineData(StepTool.PressKeys, """{ "keys": "ctrl+s" }""")] // a letter is its key on any layout
    [InlineData(StepTool.PressKeys, """{ "keys": "ctrl+oem_plus" }""")] // a key by its name
    [InlineData(StepTool.SendKeys, """{ "keys": "ц;" }""")] // text, with no Ctrl, Alt or Win
    [InlineData(StepTool.SendKeys, """{ "keys": "+ц" }""")] // Shift alone types
    public async Task AWindowWhoseLayoutCannotBeRead_StillRunsKeysThatNeedNoLayout(StepTool tool, string args)
    {
        _layouts.WindowLayouts[(nint)Window] = 0;

        var run = await RunAsync(Step(tool, args));

        run.Result.Error.Should().BeNull();
        run.Result.Success.Should().BeTrue();
    }

    [Fact]
    public async Task WithoutLayoutQueries_TheKeysAreReadOnTheInvariantLayout_AsTheirToolsMapThem()
    {
        // A server without the queries (tests) maps every key on the invariant layout, where ц has no key: nothing to refuse
        var runner = new StepRunner(_session.Object, _windows.Object, _elements.Object);

        var run = await runner.RunAsync((nint)Window, runner.Check([Step(StepTool.PressKeys, """{ "keys": "ctrl+ц" }""")], _dispatcher),
            _dispatcher, TimeSpan.FromSeconds(10), TimeSpan.Zero, null, CancellationToken.None);

        run.Result.Success.Should().BeTrue();
        _dispatcher.Calls.Should().ContainSingle();
    }

    /// <summary>Serves type_text, send_keys and press_keys with the parameters their schemas declare, and records the calls.</summary>
    private sealed class KeysDispatcher : IStepDispatcher
    {
        private static readonly Dictionary<string, string[]> Parameters = new(StringComparer.Ordinal)
        {
            ["type_text"] = ["text", "method", "windowHandle", "window", "allowHostTarget"],
            ["send_keys"] = ["keys", "windowHandle", "window", "allowHostTarget"],
            ["press_keys"] = ["keys", "windowHandle", "window", "allowHostTarget"]
        };

        public ConcurrentQueue<Call> Calls { get; } = new();
        public Dictionary<string, string> Results { get; } = new(StringComparer.Ordinal);
        public Action<Call>? OnCall { get; set; }

        public JsonElement? InputSchemaOf(string tool)
        {
            if (!Parameters.TryGetValue(tool, out var names))
            {
                return null;
            }

            var properties = new JsonObject();
            foreach (var name in names)
            {
                properties[name] = new JsonObject { ["type"] = name is "window" ? "object" : name is "windowHandle" ? "integer" : "string" };
            }

            return JsonSerializer.SerializeToElement(new JsonObject { ["type"] = "object", ["properties"] = properties });
        }

        public Task<CallToolResult> InvokeAsync(string tool, JsonObject arguments, int stepNumber, CancellationToken cancellationToken)
        {
            var call = new Call(tool, arguments, stepNumber);
            Calls.Enqueue(call);
            OnCall?.Invoke(call);
            var text = Results.GetValueOrDefault(tool, """{ "success": true }""");
            return Task.FromResult(new CallToolResult { Content = [new TextContentBlock { Text = text }] });
        }
    }

    private sealed record Call(string Tool, JsonObject Arguments, int Number);
}
