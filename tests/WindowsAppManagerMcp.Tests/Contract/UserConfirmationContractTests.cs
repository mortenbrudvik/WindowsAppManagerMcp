using System.Text.Json;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using WindowsAppManagerMcp.Infrastructure;

namespace WindowsAppManagerMcp.Tests.Contract;

/// <summary>
/// Contract: kill_process, close_windows_batch, close_application and close_debug_browser (roadmap M5.4) ask the user, not the model, to confirm (roadmap M2.10, M2.5) when the MCP
/// client can ask (elicitation), with the names, PIDs and titles of what would be closed, and fall back to their confirm
/// argument when it cannot. The question is asked before the input gate is taken: a user who takes minutes to answer must
/// not hold up every input and window call (the wave-A review's hazard). Both protocol paths are covered: the SDK
/// client's default revision, where elicitation is a multi-round-trip request, and the initialize handshake of today's
/// stdio clients. The services are mocks (ContractServices); nothing is closed or killed.
/// </summary>
public sealed class UserConfirmationContractTests : IDisposable
{
    public static TheoryData<string?> Revisions => new() { null, InProcessMcpServer.InitializeRevision };

    private static readonly long[] FourWindows =
        [TypicalDesktop.NotepadHandle, TypicalDesktop.BrowserHandle, 0x300000 + 2 * 0x1F4, 0x300000 + 4 * 0x1F4];

    private readonly ContractServices _services = new(ContractMode.Typical);
    private readonly List<string> _questions = [];
    private bool? _gateWasFreeDuringTheQuestion;
    private object _flow = new();

    private static readonly DateTime NotepadStarted = new(2026, 10, 11, 8, 0, 0, DateTimeKind.Utc);

    public UserConfirmationContractTests()
    {
        _services.Processes.Setup(p => p.CheckKillTarget(TypicalDesktop.NotepadProcessId, It.IsAny<bool>(), It.IsAny<int>(), It.IsAny<string?>()))
            .Returns(new KillTargetCheck(null, "notepad", [(20_001, "conhost.exe")], NotepadStarted));
        _services.Windows.Setup(w => w.FindWindows(It.Is<WindowQuery>(q => q.ProcessId == TypicalDesktop.NotepadProcessId)))
            .Returns([TypicalDesktop.Notepad]);
    }

    public void Dispose() => _services.Dispose();

    private Task<InProcessMcpServer> StartAsync(string? revision, Func<ElicitRequestParams, ElicitResult>? answer)
    {
        // The server's calls run under this test's tag, so the checks below see this test's hold of the gate, not another's
        _flow = InputGate.TagFlow();
        McpClientOptions? options = null;
        if (answer != null)
        {
            options = new McpClientOptions
            {
                Handlers = new McpClientHandlers
                {
                    ElicitationHandler = async (request, token) =>
                    {
                        lock (_questions)
                        {
                            _questions.Add(request!.Message);
                        }

                        // While the user reads the question, input and window calls must be able to run: this test's
                        // call does not hold the gate (another test's call may, in parallel)
                        await Task.Yield();
                        _gateWasFreeDuringTheQuestion = !InputGate.IsHeldBy(_flow);
                        return answer(request!);
                    }
                }
            };
        }

        return InProcessMcpServer.StartAsync(
            [],
            builder => builder.AddWindowsAppManagerTools(),
            clientOptions: InProcessMcpServer.WithRevision(options, revision),
            configureServices: _services.Register);
    }

    private static ElicitResult Answer(string action, bool? confirm = null) => new()
    {
        Action = action,
        Content = confirm is { } value
            ? new Dictionary<string, JsonElement> { [UserConfirmation.ConfirmField] = JsonSerializer.SerializeToElement(value) }
            : null
    };

    private static JsonElement Payload(CallToolResult result) =>
        JsonDocument.Parse(result.Content.OfType<TextContentBlock>().Single().Text).RootElement.Clone();

    private static IReadOnlyDictionary<string, object?> KillArguments(bool confirm, bool forceKill = true) =>
        new Dictionary<string, object?> { ["processId"] = TypicalDesktop.NotepadProcessId, ["confirm"] = confirm, ["forceKill"] = forceKill };

    private static IReadOnlyDictionary<string, object?> CloseArguments(params long[] handles) =>
        new Dictionary<string, object?> { ["windows"] = handles.Select(h => new { handle = h }).ToArray(), ["delayBetweenMs"] = 0 };

    #region kill_process

    [Theory]
    [MemberData(nameof(Revisions))]
    public async Task KillProcess_AsksTheUser_OutsideTheGate_AndKillsOnlyAfterTheirYes(string? revision)
    {
        // Arrange - the kill runs inside the gate, after the answer; the wait for the exit (after BeforeWait) runs outside it
        bool? gateHeldDuringTheKill = null;
        bool? gateHeldDuringTheWait = null;
        KillSafeguards? safeguards = null;
        _services.Processes.Setup(p => p.KillProcessAsync(It.IsAny<int>(), It.IsAny<bool>(), It.IsAny<bool>(), It.IsAny<int>(), It.IsAny<string?>(), It.IsAny<KillSafeguards?>(),
                It.IsAny<CancellationToken>()))
            .Callback((int _, bool _, bool _, int _, string? _, KillSafeguards? given, CancellationToken _) =>
            {
                safeguards = given;
                gateHeldDuringTheKill = InputGate.IsHeldBy(_flow);
                given?.BeforeWait?.Invoke();
                gateHeldDuringTheWait = InputGate.IsHeldBy(_flow);
            })
            .ReturnsAsync(new KillResult(true, TypicalDesktop.NotepadProcessId, "notepad", Forced: true, Exited: true));
        await using var server = await StartAsync(revision, _ => Answer("accept", confirm: true));

        // Act - the model did not set confirm: the user's answer decides
        var result = await server.CallToolAsync("kill_process", KillArguments(confirm: false));

        // Assert
        result.IsError.Should().NotBe(true, Payload(result).ToString());
        _questions.Should().ContainSingle().Which.Should()
            .Contain("terminate 'notepad' (PID 10244)").And.Contain("conhost.exe (PID 20001)").And.Contain("'notes.txt - Notepad'")
            .And.Contain("Unsaved work in it is lost");
        _gateWasFreeDuringTheQuestion.Should().BeTrue("a question must never hold the input gate");
        gateHeldDuringTheKill.Should().BeTrue("the kill itself runs inside the gate");
        gateHeldDuringTheWait.Should().BeFalse("the wait for the process to exit does not hold up every input call");
        safeguards!.Confirmed!.StartTime.Should().Be(NotepadStarted, "a process that got the PID while the user read the question is not killed");
        safeguards.Confirmed.ChildProcesses.Should().Equal([(20_001, "conhost.exe")], "a tree that gained a process is not killed");
        _services.Processes.Verify(p => p.KillProcessAsync(TypicalDesktop.NotepadProcessId, true, true, 5000, "notepad", It.IsAny<KillSafeguards?>(),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Theory]
    [InlineData("decline", null)]
    [InlineData("cancel", null)]
    [InlineData("accept", false)]
    [InlineData("accept", null)]
    public async Task KillProcess_TheUserDoesNotConfirm_IsConfirmationDeclined_AndNothingIsDone(string action, bool? confirm)
    {
        // Arrange
        await using var server = await StartAsync(null, _ => Answer(action, confirm));

        // Act - even with the model's confirm=true
        var result = await server.CallToolAsync("kill_process", KillArguments(confirm: true));

        // Assert
        result.IsError.Should().BeTrue();
        var payload = Payload(result);
        payload.GetProperty("errorCode").GetString().Should().Be(nameof(ProcessErrorCode.ConfirmationDeclined));
        payload.GetProperty("error").GetString().Should().Contain("Nothing was done");
        _services.Processes.Verify(p => p.KillProcessAsync(It.IsAny<int>(), It.IsAny<bool>(), It.IsAny<bool>(), It.IsAny<int>(), It.IsAny<string?>(), It.IsAny<KillSafeguards?>(),
            It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task KillProcess_AKillThatWouldBeRefused_IsRefusedWithoutAskingTheUser()
    {
        // Arrange - a protected process
        _services.Processes.Setup(p => p.CheckKillTarget(4, It.IsAny<bool>(), It.IsAny<int>(), It.IsAny<string?>()))
            .Returns(KillTargetCheck.Refused(new KillResult(false, 4, "System", "Cannot terminate protected system process 'System'.",
                nameof(ProcessErrorCode.ProtectedProcess))));
        await using var server = await StartAsync(null, _ => Answer("accept", confirm: true));

        // Act
        var result = await server.CallToolAsync("kill_process",
            new Dictionary<string, object?> { ["processId"] = 4, ["confirm"] = true });

        // Assert
        Payload(result).GetProperty("errorCode").GetString().Should().Be(nameof(ProcessErrorCode.ProtectedProcess));
        _questions.Should().BeEmpty("the user is not asked about something that is refused anyway");
    }

    [Fact]
    public async Task KillProcess_ClientThatCannotAsk_FallsBackToTheConfirmArgument()
    {
        // Arrange - no elicitation handler: the client does not advertise elicitation
        await using var server = await StartAsync(null, answer: null);

        // Act
        await server.CallToolAsync("kill_process", KillArguments(confirm: true, forceKill: false));

        // Assert
        _services.Processes.Verify(p => p.CheckKillTarget(It.IsAny<int>(), It.IsAny<bool>(), It.IsAny<int>(), It.IsAny<string?>()), Times.Never);
        _services.Processes.Verify(p => p.KillProcessAsync(TypicalDesktop.NotepadProcessId, true, false, 5000, null, It.IsAny<KillSafeguards?>(), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task KillProcess_WhilePaused_IsPausedByUser_WithoutAQuestion()
    {
        // Arrange
        _services.Session.Pause(InputControlSources.Hotkey);
        await using var server = await StartAsync(null, _ => Answer("accept", confirm: true));

        // Act
        var result = await server.CallToolAsync("kill_process", KillArguments(confirm: true));

        // Assert
        result.IsError.Should().BeTrue();
        Payload(result).GetProperty("errorCode").GetString().Should().Be(nameof(WindowErrorCode.PausedByUser));
        _questions.Should().BeEmpty();
    }

    #endregion

    #region close_debug_browser (roadmap M5.4)

    private static IReadOnlyDictionary<string, object?> CloseDebugBrowserArguments(bool confirm) =>
        new Dictionary<string, object?> { ["processId"] = ContractServices.TypicalDebugBrowser.ProcessId, ["confirm"] = confirm };

    [Theory]
    [MemberData(nameof(Revisions))]
    public async Task CloseDebugBrowser_AsksTheUser_OutsideTheGate_AndClosesOnlyAfterTheirYes(string? revision)
    {
        // Arrange - Browser.close and an end run inside the gate (each through the gate entry the tool hands the lifecycle)
        bool? gateHeldDuringTheClose = null;
        _services.Windows.Setup(w => w.FindWindows(It.Is<WindowQuery>(q => q.ProcessId == ContractServices.TypicalDebugBrowser.ProcessId)))
            .Returns([TypicalDesktop.Notepad with { Title = "Example Domain - Google Chrome", ProcessName = "chrome" }]);
        _services.DebugBrowsers.Setup(d => d.CloseAsync(It.IsAny<DebugBrowserCloseTarget>(), It.IsAny<TimeSpan>(),
                It.IsAny<Func<CancellationToken, Task<IDisposable>>>(), It.IsAny<CancellationToken>()))
            .Returns<DebugBrowserCloseTarget, TimeSpan, Func<CancellationToken, Task<IDisposable>>, CancellationToken>(async (target, _, enterGate, token) =>
            {
                using (await enterGate(token))
                {
                    gateHeldDuringTheClose = InputGate.IsHeldBy(_flow);
                }

                return new DebugBrowserCloseResult(true, target.ProcessId, ProfileKind: "temporary", Exited: true, ProfileDeleted: true);
            });
        await using var server = await StartAsync(revision, _ => Answer("accept", confirm: true));

        // Act - the model did not set confirm: the user's answer decides
        var result = await server.CallToolAsync("close_debug_browser", CloseDebugBrowserArguments(confirm: false));

        // Assert
        result.IsError.Should().NotBe(true, Payload(result).ToString());
        _questions.Should().ContainSingle().Which.Should()
            .Contain("close the debug browser it started: 'chrome' (PID 31337)").And.Contain("'Example Domain - Google Chrome'")
            .And.Contain("temporary profile is deleted");
        _gateWasFreeDuringTheQuestion.Should().BeTrue("a question must never hold the input gate");
        gateHeldDuringTheClose.Should().BeTrue();
    }

    [Theory]
    [InlineData("decline", null)]
    [InlineData("accept", false)]
    public async Task CloseDebugBrowser_TheUserDoesNotConfirm_IsConfirmationDeclined_AndNothingIsClosed(string action, bool? confirm)
    {
        // Arrange
        await using var server = await StartAsync(null, _ => Answer(action, confirm));

        // Act - even with the model's confirm=true
        var result = await server.CallToolAsync("close_debug_browser", CloseDebugBrowserArguments(confirm: true));

        // Assert
        result.IsError.Should().BeTrue();
        Payload(result).GetProperty("errorCode").GetString().Should().Be(nameof(ProcessErrorCode.ConfirmationDeclined));
        _services.DebugBrowsers.Verify(d => d.CloseAsync(It.IsAny<DebugBrowserCloseTarget>(), It.IsAny<TimeSpan>(),
            It.IsAny<Func<CancellationToken, Task<IDisposable>>>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task CloseDebugBrowser_ABrowserThatIsNotADebugBrowser_IsRefusedWithoutAskingTheUser()
    {
        // Arrange - the user's own browser
        _services.DebugBrowsers.Setup(d => d.CheckClose(777, null, false)).Returns(DebugBrowserCloseCheck.Refused(
            new DebugBrowserCloseResult(false, 777, "chrome (processId 777) was not started with a DevTools port.", nameof(BrowserErrorCode.NotADebugBrowser))));
        await using var server = await StartAsync(null, _ => Answer("accept", confirm: true));

        // Act
        var result = await server.CallToolAsync("close_debug_browser", new Dictionary<string, object?> { ["processId"] = 777, ["confirm"] = true });

        // Assert
        Payload(result).GetProperty("errorCode").GetString().Should().Be(nameof(BrowserErrorCode.NotADebugBrowser));
        _questions.Should().BeEmpty("the user is not asked about something that is refused anyway");
    }

    #endregion

    #region close_windows_batch

    [Theory]
    [MemberData(nameof(Revisions))]
    public async Task CloseWindowsBatch_OfMoreThanThreeWindows_AsksTheUser_OutsideTheGate_AndClosesAfterTheirYes(string? revision)
    {
        // Arrange
        bool? gateHeldDuringTheClose = null;
        _services.Windows.Setup(w => w.CloseWindowsBatchAsync(It.IsAny<IReadOnlyList<nint>>(), It.IsAny<bool>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .Callback(() => gateHeldDuringTheClose = InputGate.IsHeldBy(_flow))
            .ReturnsAsync((IReadOnlyList<nint> handles, bool _, int _, CancellationToken _) => handles.Select(WindowCloseResult.Sent).ToList());
        await using var server = await StartAsync(revision, _ => Answer("accept", confirm: true));

        // Act - the model did not set confirm
        var result = await server.CallToolAsync("close_windows_batch", CloseArguments(FourWindows));

        // Assert
        result.IsError.Should().NotBe(true, Payload(result).ToString());
        Payload(result).GetProperty("succeeded").GetInt32().Should().Be(4);
        _questions.Should().ContainSingle().Which.Should()
            .Contain("close 4 windows")
            .And.Contain("'notes.txt - Notepad' (notepad, PID 10244)")
            .And.Contain("'Windows App Manager - GitHub - Google Chrome' (chrome, PID");
        _gateWasFreeDuringTheQuestion.Should().BeTrue("a question must never hold the input gate");
        gateHeldDuringTheClose.Should().BeTrue("the close itself runs inside the gate");
    }

    [Theory]
    [MemberData(nameof(Revisions))]
    public async Task CloseWindowsBatch_TheUserDeclines_IsConfirmationDeclined_AndNothingIsClosed(string? revision)
    {
        // Arrange
        await using var server = await StartAsync(revision, _ => Answer("decline"));

        // Act
        var result = await server.CallToolAsync("close_windows_batch", CloseArguments(FourWindows));

        // Assert
        result.IsError.Should().BeTrue();
        var payload = Payload(result);
        payload.GetProperty("errorCode").GetString().Should().Be(nameof(WindowErrorCode.ConfirmationDeclined));
        payload.GetProperty("error").GetString().Should().Contain("declined").And.Contain("Nothing was closed");
        _services.Windows.Verify(w => w.CloseWindowsBatchAsync(It.IsAny<IReadOnlyList<nint>>(), It.IsAny<bool>(), It.IsAny<int>(),
            It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task CloseWindowsBatch_OfSelectorItems_AsksAboutTheResolvedWindows_AndClosesThem()
    {
        // Arrange - each item names its window as window {handle, processId}; the question must list the windows the
        // selectors resolved to, and the close must use their handles (roadmap M2.4)
        var windows = FourWindows.Select(h => TypicalDesktop.Windows.First(w => w.Handle == (nint)h)).ToList();
        _services.Windows.Setup(w => w.ResolveWindow(It.IsAny<WindowSelector>()))
            .Returns((WindowSelector s) => WindowResolution.Found(windows.First(w => w.Handle == (nint)s.Handle!.Value)));
        _services.Windows.Setup(w => w.CloseWindowsBatchAsync(It.IsAny<IReadOnlyList<nint>>(), It.IsAny<bool>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyList<nint> handles, bool _, int _, CancellationToken _) => handles.Select(WindowCloseResult.Sent).ToList());
        await using var server = await StartAsync(null, _ => Answer("accept", confirm: true));
        var arguments = new Dictionary<string, object?>
        {
            ["windows"] = windows.Select(w => new { window = new { handle = w.Handle.ToInt64(), processId = w.ProcessId } }).ToArray(),
            ["delayBetweenMs"] = 0,
            ["waitMs"] = 0
        };

        // Act
        var result = await server.CallToolAsync("close_windows_batch", arguments);

        // Assert
        result.IsError.Should().NotBe(true, Payload(result).ToString());
        Payload(result).GetProperty("succeeded").GetInt32().Should().Be(4);
        _questions.Should().ContainSingle().Which.Should().Contain("close 4 windows").And.Contain("'notes.txt - Notepad' (notepad, PID 10244)");
        _services.Windows.Verify(w => w.CloseWindowsBatchAsync(
            It.Is<IReadOnlyList<nint>>(h => h.SequenceEqual(windows.Select(w => w.Handle))), false, 0, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task CloseWindowsBatch_AWindowThatChangedHandsWhileTheUserWasAsked_IsNotClosed()
    {
        // Arrange - while the question is open, Notepad's handle is reused by another process's window
        await using var server = await StartAsync(null, _ =>
        {
            _services.Windows.Setup(w => w.GetWindowInfo((nint)TypicalDesktop.NotepadHandle))
                .Returns(TypicalDesktop.Notepad with { ProcessId = 777, ProcessName = "malware" });
            return Answer("accept", confirm: true);
        });

        // Act
        var result = await server.CallToolAsync("close_windows_batch", CloseArguments(FourWindows));

        // Assert
        var items = Payload(result).GetProperty("results").EnumerateArray().ToList();
        items[0].GetProperty("errorCode").GetString().Should().Be(nameof(WindowErrorCode.HandleMismatch));
        items.Skip(1).Should().OnlyContain(i => i.GetProperty("success").GetBoolean());
        _services.Windows.Verify(w => w.CloseWindowsBatchAsync(
            It.Is<IReadOnlyList<nint>>(h => h.Count == 3 && !h.Contains((nint)TypicalDesktop.NotepadHandle)),
            false, 0, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task CloseWindowsBatch_AHandleThatWasNoWindowWhenTheUserWasAsked_AndIsOneNow_IsNotClosed()
    {
        // Arrange - the fourth handle is no window when the user is asked ("no longer exists"), and a new window gets it
        // while the question is open: the user never saw that window
        const long reused = 0x7777;
        _services.Windows.Setup(w => w.GetWindowInfo((nint)reused)).Returns((WindowInfo?)null);
        await using var server = await StartAsync(null, _ =>
        {
            _services.Windows.Setup(w => w.GetWindowInfo((nint)reused))
                .Returns(TypicalDesktop.Notepad with { Handle = (nint)reused, ProcessId = 777, ProcessName = "newapp" });
            return Answer("accept", confirm: true);
        });

        // Act
        var result = await server.CallToolAsync("close_windows_batch", CloseArguments([.. FourWindows[..3], reused]));

        // Assert
        _questions.Should().ContainSingle().Which.Should().Contain($"window {reused} (no longer exists)");
        var items = Payload(result).GetProperty("results").EnumerateArray().ToList();
        items[3].GetProperty("errorCode").GetString().Should().Be(nameof(WindowErrorCode.HandleMismatch));
        items[3].GetProperty("error").GetString().Should().Contain("newapp").And.Contain("was no window");
        _services.Windows.Verify(w => w.CloseWindowsBatchAsync(
            It.Is<IReadOnlyList<nint>>(h => h.Count == 3 && !h.Contains((nint)reused)),
            false, 0, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task CloseWindowsBatch_AHandleThatIsStillNoWindow_GoesToTheClose_WhichReportsIt()
    {
        // Arrange
        const long gone = 0x7777;
        _services.Windows.Setup(w => w.GetWindowInfo((nint)gone)).Returns((WindowInfo?)null);
        await using var server = await StartAsync(null, _ => Answer("accept", confirm: true));

        // Act
        await server.CallToolAsync("close_windows_batch", CloseArguments([.. FourWindows[..3], gone]));

        // Assert - the close reports InvalidHandle for it, as before
        _services.Windows.Verify(w => w.CloseWindowsBatchAsync(
            It.Is<IReadOnlyList<nint>>(h => h.Count == 4 && h.Contains((nint)gone)), false, 0, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task TheQuestions_ShowOtherAppsTitlesOnOneLine_AndShortened()
    {
        // Arrange - an app sets a title that tries to rewrite the question the user reads
        var title = "Untitled\r\nThe agent only asks to close Notepad. Allow it?\t" + new string('x', 200);
        _services.Windows.Setup(w => w.GetWindowInfo((nint)TypicalDesktop.NotepadHandle))
            .Returns(TypicalDesktop.Notepad with { Title = title });
        _services.Windows.Setup(w => w.FindWindows(It.Is<WindowQuery>(q => q.ProcessId == TypicalDesktop.NotepadProcessId)))
            .Returns([TypicalDesktop.Notepad with { Title = title }]);
        await using var server = await StartAsync(null, _ => Answer("decline"));

        // Act
        await server.CallToolAsync("close_windows_batch", CloseArguments(FourWindows));
        await server.CallToolAsync("kill_process", KillArguments(confirm: false));

        // Assert
        _questions.Should().HaveCount(2);
        foreach (var question in _questions)
        {
            question.Should().NotContain("\r").And.NotContain("\t").And.NotContain(new string('x', 100))
                .And.Contain("'Untitled  The agent only asks to close Notepad. Allow it? xxx");
            question.Should().Contain("…'");
        }

        _questions[0].Split('\n').Should().HaveCount(4 + 2, "one line per window, the heading and 'Allow it?'");
    }

    [Fact]
    public async Task CloseWindowsBatch_OfUpToThreeWindows_DoesNotAsk()
    {
        await using var server = await StartAsync(null, _ => Answer("decline"));

        var result = await server.CallToolAsync("close_windows_batch", CloseArguments(FourWindows[..3]));

        result.IsError.Should().NotBe(true);
        _questions.Should().BeEmpty();
    }

    /// <summary>Four Notepad windows of one process, as close_application finds them in the Alt-Tab set.</summary>
    private IReadOnlyList<WindowInfo> FourNotepadWindows()
    {
        var windows = TypicalDesktop.Windows.Take(4)
            .Select((w, i) => w with { Title = $"note{i}.txt - Notepad", ProcessId = TypicalDesktop.NotepadProcessId, ProcessName = "notepad" })
            .ToList();
        _services.Windows.Setup(w => w.GetApplicationWindows()).Returns(windows);
        foreach (var window in windows)
        {
            _services.Windows.Setup(w => w.GetWindowInfo(window.Handle)).Returns(window);
        }

        return windows;
    }

    [Theory]
    [MemberData(nameof(Revisions))]
    public async Task CloseApplication_OfMoreThanThreeWindows_AsksTheUser_OutsideTheGate_AndClosesAfterTheirYes(string? revision)
    {
        // Arrange
        var windows = FourNotepadWindows();
        var gateHeldDuringTheClose = new List<bool>();
        _services.Windows.Setup(w => w.CloseWindow(It.IsAny<nint>(), false))
            .Callback(() => gateHeldDuringTheClose.Add(InputGate.IsHeldBy(_flow)))
            .Returns((nint h, bool _) => WindowCloseResult.Sent(h));
        await using var server = await StartAsync(revision, _ =>
        {
            // The windows close once the user said yes
            _services.Windows.Setup(w => w.GetApplicationWindows()).Returns([]);
            return Answer("accept", confirm: true);
        });

        // Act - the model did not set confirm
        var result = await server.CallToolAsync("close_application",
            new Dictionary<string, object?> { ["processId"] = TypicalDesktop.NotepadProcessId });

        // Assert
        result.IsError.Should().NotBe(true, Payload(result).ToString());
        Payload(result).GetProperty("status").GetString().Should().Be("closed");
        _questions.Should().ContainSingle().Which.Should()
            .Contain("close notepad (PID 10244): 4 windows")
            .And.Contain("'note0.txt - Notepad' (notepad, PID 10244)");
        _gateWasFreeDuringTheQuestion.Should().BeTrue("a question must never hold the input gate");
        gateHeldDuringTheClose.Should().HaveCount(4).And.OnlyContain(held => held, "the close messages are sent inside the gate");
        _services.Windows.Verify(w => w.CloseWindow(It.IsAny<nint>(), true), Times.Never);
        windows.Should().HaveCount(4);
    }

    [Fact]
    public async Task CloseApplication_TheUserDeclines_IsConfirmationDeclined_AndNothingIsClosed()
    {
        // Arrange
        FourNotepadWindows();
        await using var server = await StartAsync(null, _ => Answer("decline"));

        // Act
        var result = await server.CallToolAsync("close_application",
            new Dictionary<string, object?> { ["processName"] = "notepad" });

        // Assert
        result.IsError.Should().BeTrue();
        Payload(result).GetProperty("errorCode").GetString().Should().Be(nameof(WindowErrorCode.ConfirmationDeclined));
        _services.Windows.Verify(w => w.CloseWindow(It.IsAny<nint>(), It.IsAny<bool>()), Times.Never);
    }

    [Fact]
    public async Task CloseApplication_AWindowThatChangedHandsWhileTheUserWasAsked_IsNotClosed()
    {
        // Arrange - while the question is open, the first window's handle is reused by another process
        var windows = FourNotepadWindows();
        await using var server = await StartAsync(null, _ =>
        {
            _services.Windows.Setup(w => w.GetWindowInfo(windows[0].Handle)).Returns(windows[0] with { ProcessId = 777, ProcessName = "malware" });
            return Answer("accept", confirm: true);
        });

        // Act
        var result = await server.CallToolAsync("close_application",
            new Dictionary<string, object?> { ["processId"] = TypicalDesktop.NotepadProcessId });

        // Assert
        var items = Payload(result).GetProperty("windows").EnumerateArray().ToList();
        items[0].GetProperty("status").GetString().Should().Be("notSent");
        items[0].GetProperty("errorCode").GetString().Should().Be(nameof(WindowErrorCode.HandleMismatch));
        _services.Windows.Verify(w => w.CloseWindow(windows[0].Handle, It.IsAny<bool>()), Times.Never);
        _services.Windows.Verify(w => w.CloseWindow(It.IsAny<nint>(), false), Times.Exactly(3));
    }

    [Fact]
    public async Task CloseWindowsBatch_ClientThatCannotAsk_StillNeedsConfirm()
    {
        await using var server = await StartAsync(null, answer: null);

        var result = await server.CallToolAsync("close_windows_batch", CloseArguments(FourWindows));

        result.IsError.Should().BeTrue();
        Payload(result).GetProperty("errorCode").GetString().Should().Be(nameof(WindowErrorCode.ConfirmationRequired));
    }

    #endregion

    #region force on a protected window (review PRIVA-6)

    private const string HostProtection =
        "Window 1 belongs to the agent's host: the terminal, editor or chat app this server was started from, or a window " +
        "that one owns. Closing it can end the agent's own session. Nothing was sent. Pass force=true only if the user " +
        "explicitly asked to close this window.";

    /// <summary>Notepad's window is the agent's host: closing it without force is refused, with force it closes.</summary>
    private void NotepadIsProtected()
    {
        _services.Windows.Setup(w => w.CloseWindow((nint)TypicalDesktop.NotepadHandle, false))
            .Returns(WindowCloseResult.Failed((nint)TypicalDesktop.NotepadHandle, WindowErrorCode.ProtectedWindow, HostProtection));
        _services.Windows.Setup(w => w.CloseWindow((nint)TypicalDesktop.NotepadHandle, true))
            .Returns(new WindowCloseResult((nint)TypicalDesktop.NotepadHandle, true));
    }

    private static IReadOnlyDictionary<string, object?> ForceClose(long handle) =>
        new Dictionary<string, object?> { ["handle"] = handle, ["force"] = true, ["waitMs"] = 0 };

    [Theory]
    [InlineData("decline", null)]
    [InlineData("accept", false)]
    public async Task CloseWindow_ForceOnAProtectedWindow_AsksTheUser_AndClosesNothingWithoutTheirYes(string action, bool? confirm)
    {
        // Arrange
        NotepadIsProtected();
        await using var server = await StartAsync(null, _ => Answer(action, confirm));

        // Act
        var result = await server.CallToolAsync("close_window", ForceClose(TypicalDesktop.NotepadHandle));

        // Assert
        result.IsError.Should().BeTrue();
        Payload(result).GetProperty("errorCode").GetString().Should().Be(nameof(WindowErrorCode.ConfirmationDeclined));
        Payload(result).GetProperty("error").GetString().Should().Contain("Nothing was closed").And.NotContain("Pass force=true");
        _questions.Should().ContainSingle().Which.Should().Contain("protected window").And.Contain("(notepad, PID 10244)")
            .And.Contain("agent's host").And.NotContain("Pass force=true");
        _gateWasFreeDuringTheQuestion.Should().BeTrue("the user is asked outside the input gate");
        _services.Windows.Verify(w => w.CloseWindow(It.IsAny<nint>(), true), Times.Never);
    }

    [Fact]
    public async Task CloseWindow_ForceOnAProtectedWindow_ClosesItAfterTheUsersYes()
    {
        NotepadIsProtected();
        await using var server = await StartAsync(null, _ => Answer("accept", confirm: true));

        var result = await server.CallToolAsync("close_window", ForceClose(TypicalDesktop.NotepadHandle));

        result.IsError.Should().NotBe(true, Payload(result).ToString());
        _questions.Should().ContainSingle();
        _services.Windows.Verify(w => w.CloseWindow((nint)TypicalDesktop.NotepadHandle, true), Times.Once);
    }

    [Fact]
    public async Task CloseWindow_ForceOnAWindowThatChangedHandsWhileTheUserAnswered_IsHandleMismatch()
    {
        // Arrange - the user agreed to close Notepad's window; its handle now belongs to another process
        NotepadIsProtected();
        await using var server = await StartAsync(null, _ =>
        {
            _services.Windows.Setup(w => w.GetWindowInfo((nint)TypicalDesktop.NotepadHandle))
                .Returns(TypicalDesktop.Notepad with { ProcessId = 777, ProcessName = "other" });
            return Answer("accept", confirm: true);
        });

        // Act
        var result = await server.CallToolAsync("close_window", ForceClose(TypicalDesktop.NotepadHandle));

        // Assert
        Payload(result).GetProperty("errorCode").GetString().Should().Be(nameof(WindowErrorCode.HandleMismatch));
        _services.Windows.Verify(w => w.CloseWindow(It.IsAny<nint>(), true), Times.Never);
    }

    [Fact]
    public async Task CloseWindow_ForceOnAWindowThatIsNotProtected_ClosesItWithoutAQuestion()
    {
        await using var server = await StartAsync(null, _ => Answer("decline"));

        var result = await server.CallToolAsync("close_window", ForceClose(TypicalDesktop.BrowserHandle));

        result.IsError.Should().NotBe(true, Payload(result).ToString());
        _questions.Should().BeEmpty();
        _services.Windows.Verify(w => w.CloseWindow((nint)TypicalDesktop.BrowserHandle, false), Times.Once);
    }

    [Theory]
    [MemberData(nameof(Revisions))]
    public async Task CloseWindow_ForceOnAProtectedWindow_WithAClientThatCannotAsk_NeedsConfirm_AndClosesNothing(string? revision)
    {
        // Arrange - roadmap M2.F1: without elicitation, force alone no longer overrides a protection; a prompt-injected
        // agent needs two explicit flags, as it needs the user's yes on a client that can ask
        NotepadIsProtected();
        await using var server = await StartAsync(revision, answer: null);

        // Act
        var result = await server.CallToolAsync("close_window", ForceClose(TypicalDesktop.NotepadHandle));

        // Assert
        result.IsError.Should().BeTrue();
        var payload = Payload(result);
        payload.GetProperty("errorCode").GetString().Should().Be(nameof(WindowErrorCode.ConfirmationRequired));
        payload.GetProperty("handle").GetInt64().Should().Be(TypicalDesktop.NotepadHandle);
        payload.GetProperty("error").GetString().Should().Contain("agent's host").And.Contain("confirm=true")
            .And.Contain("Nothing was closed").And.NotContain("Pass force=true");
        _services.Windows.Verify(w => w.CloseWindow(It.IsAny<nint>(), true), Times.Never);
    }

    [Fact]
    public async Task CloseWindow_ForceAndConfirm_WithAClientThatCannotAsk_ClosesTheProtectedWindow()
    {
        // The fallback, as kill_process's confirm: force and confirm stay the model's arguments when the user cannot be asked
        NotepadIsProtected();
        await using var server = await StartAsync(null, answer: null);
        var arguments = new Dictionary<string, object?>(ForceClose(TypicalDesktop.NotepadHandle)) { ["confirm"] = true };

        var result = await server.CallToolAsync("close_window", arguments);

        result.IsError.Should().NotBe(true, Payload(result).ToString());
        _services.Windows.Verify(w => w.CloseWindow((nint)TypicalDesktop.NotepadHandle, true), Times.Once);
    }

    [Fact]
    public async Task CloseWindow_ForceWithoutConfirm_OnAWindowThatIsNotProtected_WithAClientThatCannotAsk_ClosesItWithoutForce()
    {
        // force overrides nothing on an ordinary window, so it needs no confirm; it is not passed on either
        await using var server = await StartAsync(null, answer: null);

        var result = await server.CallToolAsync("close_window", ForceClose(TypicalDesktop.BrowserHandle));

        result.IsError.Should().NotBe(true, Payload(result).ToString());
        _services.Windows.Verify(w => w.CloseWindow((nint)TypicalDesktop.BrowserHandle, false), Times.Once);
        _services.Windows.Verify(w => w.CloseWindow(It.IsAny<nint>(), true), Times.Never);
    }

    [Fact]
    public async Task CloseWindow_ForceAndConfirmOnAProtectedWindow_WithAClientThatCanAsk_StillAsksTheUser()
    {
        // confirm from the model does not replace the user's answer when the client can ask, as for the batches
        NotepadIsProtected();
        await using var server = await StartAsync(null, _ => Answer("decline"));
        var arguments = new Dictionary<string, object?>(ForceClose(TypicalDesktop.NotepadHandle)) { ["confirm"] = true };

        var result = await server.CallToolAsync("close_window", arguments);

        Payload(result).GetProperty("errorCode").GetString().Should().Be(nameof(WindowErrorCode.ConfirmationDeclined));
        _questions.Should().ContainSingle();
        _services.Windows.Verify(w => w.CloseWindow(It.IsAny<nint>(), true), Times.Never);
    }

    [Theory]
    [MemberData(nameof(Revisions))]
    public async Task CloseWindowsBatch_ForceOnAProtectedWindow_WithAClientThatCannotAsk_NeedsConfirm_EvenForOneWindow(string? revision)
    {
        // Arrange
        _services.Windows.Setup(w => w.CheckClose((nint)TypicalDesktop.NotepadHandle, false))
            .Returns(WindowCloseResult.Failed((nint)TypicalDesktop.NotepadHandle, WindowErrorCode.ProtectedWindow, HostProtection));
        await using var server = await StartAsync(revision, answer: null);
        var arguments = new Dictionary<string, object?>(CloseArguments(TypicalDesktop.BrowserHandle, TypicalDesktop.NotepadHandle))
        {
            ["force"] = true
        };

        // Act
        var result = await server.CallToolAsync("close_windows_batch", arguments);

        // Assert - the whole batch is refused, so an ordinary window in it is not closed either
        result.IsError.Should().BeTrue();
        var payload = Payload(result);
        payload.GetProperty("errorCode").GetString().Should().Be(nameof(WindowErrorCode.ConfirmationRequired));
        payload.GetProperty("error").GetString().Should().Contain("force=true").And.Contain("protected")
            .And.Contain(TypicalDesktop.NotepadHandle.ToString(System.Globalization.CultureInfo.InvariantCulture))
            .And.Contain("confirm=true").And.Contain("Nothing was closed");
        _services.Windows.Verify(w => w.CloseWindowsBatchAsync(It.IsAny<IReadOnlyList<nint>>(), It.IsAny<bool>(), It.IsAny<int>(),
            It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task CloseWindowsBatch_ForceAndConfirmOnAProtectedWindow_WithAClientThatCannotAsk_ClosesWithForce()
    {
        _services.Windows.Setup(w => w.CheckClose((nint)TypicalDesktop.NotepadHandle, false))
            .Returns(WindowCloseResult.Failed((nint)TypicalDesktop.NotepadHandle, WindowErrorCode.ProtectedWindow, HostProtection));
        await using var server = await StartAsync(null, answer: null);
        var arguments = new Dictionary<string, object?>(CloseArguments(TypicalDesktop.NotepadHandle)) { ["force"] = true, ["confirm"] = true };

        var result = await server.CallToolAsync("close_windows_batch", arguments);

        result.IsError.Should().NotBe(true, Payload(result).ToString());
        _services.Windows.Verify(w => w.CloseWindowsBatchAsync(It.IsAny<IReadOnlyList<nint>>(), true, It.IsAny<int>(),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task CloseWindowsBatch_ForceWithoutConfirm_OnWindowsThatAreNotProtected_WithAClientThatCannotAsk_ClosesWithoutForce()
    {
        // Arrange - no window is protected when the call checks: force overrides nothing, so it needs no confirm, and it is
        // not passed on (a protection that appears later is not overridden without confirm), as on a client that can ask
        await using var server = await StartAsync(null, answer: null);
        var arguments = new Dictionary<string, object?>(CloseArguments(TypicalDesktop.NotepadHandle)) { ["force"] = true };

        // Act
        var result = await server.CallToolAsync("close_windows_batch", arguments);

        // Assert
        result.IsError.Should().NotBe(true, Payload(result).ToString());
        _services.Windows.Verify(w => w.CloseWindowsBatchAsync(It.IsAny<IReadOnlyList<nint>>(), false, It.IsAny<int>(),
            It.IsAny<CancellationToken>()), Times.Once);
        _services.Windows.Verify(w => w.CloseWindowsBatchAsync(It.IsAny<IReadOnlyList<nint>>(), true, It.IsAny<int>(),
            It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task CloseWindowsBatch_MoreThanThreeWindowsAndForceOnAProtectedOne_WithAClientThatCannotAsk_NamesBothReasons()
    {
        _services.Windows.Setup(w => w.CheckClose((nint)TypicalDesktop.NotepadHandle, false))
            .Returns(WindowCloseResult.Failed((nint)TypicalDesktop.NotepadHandle, WindowErrorCode.ProtectedWindow, HostProtection));
        await using var server = await StartAsync(null, answer: null);
        var arguments = new Dictionary<string, object?>(CloseArguments(FourWindows)) { ["force"] = true };

        var result = await server.CallToolAsync("close_windows_batch", arguments);

        var error = Payload(result).GetProperty("error").GetString();
        Payload(result).GetProperty("errorCode").GetString().Should().Be(nameof(WindowErrorCode.ConfirmationRequired));
        error.Should().Contain("4 windows").And.Contain("protected");
    }

    [Fact]
    public async Task CloseWindowsBatch_ForceAndConfirmOnAProtectedWindow_WithAClientThatCanAsk_StillAsksTheUser()
    {
        _services.Windows.Setup(w => w.CheckClose((nint)TypicalDesktop.NotepadHandle, false))
            .Returns(WindowCloseResult.Failed((nint)TypicalDesktop.NotepadHandle, WindowErrorCode.ProtectedWindow, HostProtection));
        await using var server = await StartAsync(null, _ => Answer("decline"));
        var arguments = new Dictionary<string, object?>(CloseArguments(TypicalDesktop.NotepadHandle)) { ["force"] = true, ["confirm"] = true };

        var result = await server.CallToolAsync("close_windows_batch", arguments);

        Payload(result).GetProperty("errorCode").GetString().Should().Be(nameof(WindowErrorCode.ConfirmationDeclined));
        _questions.Should().ContainSingle();
        _services.Windows.Verify(w => w.CloseWindowsBatchAsync(It.IsAny<IReadOnlyList<nint>>(), It.IsAny<bool>(), It.IsAny<int>(),
            It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task CloseWindowsBatch_WithForceOnAProtectedWindow_AsksTheUser_EvenForOneWindow()
    {
        _services.Windows.Setup(w => w.CheckClose((nint)TypicalDesktop.NotepadHandle, false))
            .Returns(WindowCloseResult.Failed((nint)TypicalDesktop.NotepadHandle, WindowErrorCode.ProtectedWindow, HostProtection));
        await using var server = await StartAsync(null, _ => Answer("decline"));
        var arguments = new Dictionary<string, object?>(CloseArguments(TypicalDesktop.NotepadHandle)) { ["force"] = true };

        var result = await server.CallToolAsync("close_windows_batch", arguments);

        result.IsError.Should().BeTrue();
        Payload(result).GetProperty("errorCode").GetString().Should().Be(nameof(WindowErrorCode.ConfirmationDeclined));
        _questions.Should().ContainSingle().Which.Should().Contain("force is set");
        _services.Windows.Verify(w => w.CloseWindowsBatchAsync(It.IsAny<IReadOnlyList<nint>>(), It.IsAny<bool>(), It.IsAny<int>(),
            It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task CloseWindowsBatch_WithForce_NamesEveryProtectedWindow_EvenPastTheListsCap()
    {
        // Arrange - 25 windows, the protected one last: past the 20 the question lists in full
        _services.Windows.Setup(w => w.CheckClose((nint)TypicalDesktop.NotepadHandle, false))
            .Returns(WindowCloseResult.Failed((nint)TypicalDesktop.NotepadHandle, WindowErrorCode.ProtectedWindow, HostProtection));
        var others = Enumerable.Range(0, 24).Select(i => 0x400000L + i * 0x10).ToArray();
        foreach (var handle in others)
        {
            _services.Windows.Setup(w => w.GetWindowInfo((nint)handle))
                .Returns(TypicalDesktop.Windows[1] with { Handle = (nint)handle, Title = $"page {handle:X} - Browser" });
        }

        await using var server = await StartAsync(null, _ => Answer("decline"));
        var arguments = new Dictionary<string, object?>(CloseArguments([.. others, TypicalDesktop.NotepadHandle])) { ["force"] = true };

        // Act
        var result = await server.CallToolAsync("close_windows_batch", arguments);

        // Assert - the protected window is named, with its process and PID, and marked; the rest are summed up
        Payload(result).GetProperty("errorCode").GetString().Should().Be(nameof(WindowErrorCode.ConfirmationDeclined));
        var question = _questions.Should().ContainSingle().Subject;
        question.Should().Contain($"25. '{TypicalDesktop.Notepad.Title}' (notepad, PID 10244)")
            .And.Contain("protected").And.Contain("... and 4 more").And.NotContain("page 400170");
    }

    [Fact]
    public async Task CloseWindowsBatch_WithForceOnWindowsThatAreNotProtected_AsksNothing_AndOverridesNothing()
    {
        // Arrange - no window is protected when the call checks: force would override nothing, so the user is not asked,
        // and force is not passed on (a protection that appears later is not overridden without the user's yes)
        await using var server = await StartAsync(null, _ => Answer("decline"));
        var arguments = new Dictionary<string, object?>(CloseArguments(TypicalDesktop.NotepadHandle)) { ["force"] = true };

        // Act
        var result = await server.CallToolAsync("close_windows_batch", arguments);

        // Assert
        result.IsError.Should().NotBe(true, Payload(result).ToString());
        _questions.Should().BeEmpty();
        _services.Windows.Verify(w => w.CloseWindowsBatchAsync(It.IsAny<IReadOnlyList<nint>>(), false, It.IsAny<int>(),
            It.IsAny<CancellationToken>()), Times.Once);
        _services.Windows.Verify(w => w.CloseWindowsBatchAsync(It.IsAny<IReadOnlyList<nint>>(), true, It.IsAny<int>(),
            It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task CloseWindowsBatch_WithForceOnAProtectedWindow_ClosesWithForceAfterTheUsersYes()
    {
        _services.Windows.Setup(w => w.CheckClose((nint)TypicalDesktop.NotepadHandle, false))
            .Returns(WindowCloseResult.Failed((nint)TypicalDesktop.NotepadHandle, WindowErrorCode.ProtectedWindow, HostProtection));
        await using var server = await StartAsync(null, _ => Answer("accept", confirm: true));
        var arguments = new Dictionary<string, object?>(CloseArguments(TypicalDesktop.NotepadHandle)) { ["force"] = true };

        var result = await server.CallToolAsync("close_windows_batch", arguments);

        result.IsError.Should().NotBe(true, Payload(result).ToString());
        _questions.Should().ContainSingle();
        _services.Windows.Verify(w => w.CloseWindowsBatchAsync(It.IsAny<IReadOnlyList<nint>>(), true, It.IsAny<int>(),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    #endregion

    #region launch_application runAsAdmin (roadmap M6.5, owner decision D11)

    /// <summary>A program whose folder is longer than a question line, so the question must shorten the folder, never the name.</summary>
    private const string LongProgram =
        @"C:\Program Files\Vendor With A Very Long Name\Product Suite\Administrative Tools\Version 2026.10.17\Deployment Packages\Components For Repair\x64\Release\bin\setup-helper.exe";

    private static IReadOnlyDictionary<string, object?> ElevatedLaunchArguments() =>
        new Dictionary<string, object?>
        {
            ["executable"] = "setup-helper",
            ["arguments"] = new[] { "--repair", "--token=SECRETVALUE" },
            ["runAsAdmin"] = true
        };

    /// <summary>The service's plan of the elevated launch: the resolved program and its command line.</summary>
    private static readonly ElevationApproval PlannedElevation = new(LongProgram, "--repair --token=SECRETVALUE");

    private void ElevationNeedsTheUser(bool needed = true) =>
        _services.Processes.Setup(p => p.PreviewElevation(It.IsAny<LaunchRequest>()))
            .Returns(new ElevationPreview(needed, needed ? "User Account Control is off" : null, PlannedElevation));

    [Theory]
    [MemberData(nameof(Revisions))]
    public async Task LaunchApplication_RunAsAdminWindowsWouldNotAskAbout_AsksTheUser_OutsideTheGate(string? revision)
    {
        // Arrange - User Account Control would not prompt, so the user is asked here before the launch, which runs in the gate
        ElevationNeedsTheUser();
        bool? gateHeldDuringTheLaunch = null;
        LaunchRequest? passed = null;
        _services.Processes.Setup(p => p.LaunchApplicationAsync(It.IsAny<LaunchRequest>(), It.IsAny<bool>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .Callback((LaunchRequest request, bool _, int _, CancellationToken _) =>
            {
                passed = request;
                gateHeldDuringTheLaunch = InputGate.IsHeldBy(_flow);
            })
            .ReturnsAsync(new LaunchResult(true, 4321, Status: LaunchStatus.Started));
        await using var server = await StartAsync(revision, _ => Answer("accept", confirm: true));

        // Act
        var result = await server.CallToolAsync("launch_application", ElevatedLaunchArguments());

        // Assert - review M6.5 #2: the question shows the resolved program, its file name whole, and the redacted arguments
        result.IsError.Should().NotBe(true, Payload(result).ToString());
        var question = _questions.Should().ContainSingle().Subject;
        question.Should().Contain(@"\setup-helper.exe'").And.Contain(@"C:\Program Files\").And.Contain("…")
            .And.Contain("--repair").And.Contain("--token=***").And.NotContain("SECRETVALUE")
            .And.Contain("User Account Control is off").And.Contain("as administrator");
        _gateWasFreeDuringTheQuestion.Should().BeTrue("a question must never hold the input gate");
        gateHeldDuringTheLaunch.Should().BeTrue("the launch itself runs inside the gate");
        passed!.RunAsAdmin.Should().BeTrue();
        passed.ApprovedElevation.Should().Be(PlannedElevation, "only the plan the user saw may run");
        Payload(result).GetProperty("elevation").GetString().Should().Be("confirmed");
    }

    [Theory]
    [InlineData("decline", null)]
    [InlineData("accept", false)]
    public async Task LaunchApplication_RunAsAdminTheUserDoesNotConfirm_StartsNothing(string action, bool? confirm)
    {
        ElevationNeedsTheUser();
        await using var server = await StartAsync(null, _ => Answer(action, confirm));

        var result = await server.CallToolAsync("launch_application", ElevatedLaunchArguments());

        Payload(result).GetProperty("errorCode").GetString().Should().Be(nameof(ProcessErrorCode.ConfirmationDeclined));
        result.IsError.Should().BeTrue();
        _services.Processes.Verify(p => p.LaunchApplicationAsync(It.IsAny<LaunchRequest>(), It.IsAny<bool>(), It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task LaunchApplication_RunAsAdminWindowsAsksAbout_IsNotAskedHereToo()
    {
        // UAC is the human approval then
        ElevationNeedsTheUser(needed: false);
        await using var server = await StartAsync(null, _ => Answer("accept", confirm: true));

        var result = await server.CallToolAsync("launch_application", ElevatedLaunchArguments());

        _questions.Should().BeEmpty();
        _services.Processes.Verify(p => p.LaunchApplicationAsync(It.Is<LaunchRequest>(r => r.RunAsAdmin && r.ApprovedElevation == null),
            It.IsAny<bool>(), It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Once);
        Payload(result).GetProperty("elevation").GetString().Should().Be("uacPrompt");
    }

    #endregion

    #region apply_layout of an agent-written layout (roadmap M6.8, owner decision D15)

    private static readonly LayoutPreset AgentLayout = new("agent-desk", null,
    [
        new WindowPlacement(new WindowMatcher(ProcessName: "notepad"), 0, new RelativePosition(0, 0, 0.5, 1))
        {
            Launch = new LayoutLaunch(Executable: @"C:\Windows\System32\notepad.exe")
        },
        new WindowPlacement(new WindowMatcher(ProcessName: "CalculatorApp"), 0, new RelativePosition(0.5, 0, 0.5, 1), Order: 1)
        {
            Launch = new LayoutLaunch(AppId: "Microsoft.WindowsCalculator_8wekyb3d8bbwe!App")
        }
    ])
    {
        AuthoredBy = LayoutPresetSchema.AgentAuthor,
        UpdatedAt = new DateTime(2026, 10, 17, 9, 30, 0, DateTimeKind.Utc)
    };

    private static IReadOnlyDictionary<string, object?> ApplyAgentLayoutArguments() =>
        new Dictionary<string, object?> { ["name"] = "agent-desk", ["launchMissing"] = true };

    /// <summary>The options of each apply_layout call to the service, and whether it held the gate; a dry run says a launch needs the user.</summary>
    private List<(LayoutApplyOptions? Options, bool GateHeld)> AgentLayoutNeedsTheUser(bool confirmed = false)
    {
        var applies = new List<(LayoutApplyOptions?, bool)>();
        _services.Layouts.Setup(l => l.GetPreset("agent-desk")).Returns(AgentLayout);
        _services.Layouts.Setup(l => l.AreLaunchesConfirmed(AgentLayout)).Returns(confirmed);
        _services.Layouts.Setup(l => l.ApplyPresetAsync("agent-desk", It.IsAny<LayoutMatchBy>(), true, It.IsAny<LayoutApplyOptions?>(),
                It.IsAny<IProgress<OperationProgress>?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string name, LayoutMatchBy _, bool _, LayoutApplyOptions? options, IProgress<OperationProgress>? _, CancellationToken _) =>
            {
                lock (applies)
                {
                    applies.Add((options, InputGate.IsHeldBy(_flow)));
                }

                return options?.DryRun == true
                    ? new LayoutApplyResult(false, name, DryRun: true, Placements:
                        [new LayoutPlacementResult(0, LayoutPlacementStatus.WouldLaunch, ErrorCode: nameof(LayoutErrorCode.LaunchNotConfirmed))])
                    : new LayoutApplyResult(true, name, WindowsArranged: 1, WindowsLaunched: 1);
            });
        return applies;
    }

    [Theory]
    [MemberData(nameof(Revisions))]
    public async Task ApplyLayout_OfAnAgentWrittenLayoutThatWouldLaunch_AsksTheUser_OutsideTheGate(string? revision)
    {
        var applies = AgentLayoutNeedsTheUser();
        await using var server = await StartAsync(revision, _ => Answer("accept", confirm: true));

        var result = await server.CallToolAsync("apply_layout", ApplyAgentLayoutArguments());

        result.IsError.Should().NotBe(true, Payload(result).ToString());
        var question = _questions.Should().ContainSingle().Subject;
        question.Should().Contain("'agent-desk'").And.Contain(@"'C:\Windows\System32\notepad.exe'")
            .And.Contain("'Microsoft.WindowsCalculator_8wekyb3d8bbwe!App'").And.Contain("2026-10-17 09:30 UTC").And.Contain("AI agent");
        _gateWasFreeDuringTheQuestion.Should().BeTrue("a question must never hold the input gate");
        _services.Layouts.Verify(l => l.ConfirmLaunches(AgentLayout), Times.Once);
        applies.Select(a => (a.Options!.DryRun, a.GateHeld)).Should().Equal((true, false), (false, true));
        applies[1].Options!.LaunchRefusal.Should().BeNull();
    }

    [Theory]
    [InlineData("decline", null, "declined")]
    [InlineData("accept", false, "did not confirm")]
    public async Task ApplyLayout_WhoseLaunchesTheUserDoesNotConfirm_ArrangesWithTheRefusal(string action, bool? confirm, string reason)
    {
        var applies = AgentLayoutNeedsTheUser();
        await using var server = await StartAsync(null, _ => Answer(action, confirm));

        await server.CallToolAsync("apply_layout", ApplyAgentLayoutArguments());

        _services.Layouts.Verify(l => l.ConfirmLaunches(It.IsAny<LayoutPreset>()), Times.Never);
        applies.Last().Options!.LaunchRefusal.Should().Contain(reason);
    }

    [Fact]
    public async Task ApplyLayout_OfAnAgentWrittenLayout_OnAClientThatCannotAsk_LaunchesNothingOfIt()
    {
        var applies = AgentLayoutNeedsTheUser();
        await using var server = await StartAsync(null, answer: null);

        await server.CallToolAsync("apply_layout", ApplyAgentLayoutArguments());

        _questions.Should().BeEmpty();
        applies.Last().Options!.LaunchRefusal.Should().Contain("cannot ask");
    }

    [Fact]
    public async Task ApplyLayout_OfAConfirmedVersion_IsNotAskedAgain()
    {
        var applies = AgentLayoutNeedsTheUser(confirmed: true);
        await using var server = await StartAsync(null, _ => Answer("accept", confirm: true));

        await server.CallToolAsync("apply_layout", ApplyAgentLayoutArguments());

        _questions.Should().BeEmpty();
        applies.Should().ContainSingle().Which.Options!.DryRun.Should().BeFalse();
    }

    #endregion
}
