using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using WindowsAppManagerMcp.Infrastructure;
using WindowsAppManagerMcp.Tools;

namespace WindowsAppManagerMcp.Tests.Contract;

/// <summary>
/// Contract: the input session through a real MCP client and server (roadmap M1.10): get_control_status,
/// resume_input (which resumes only after the user confirms through elicitation, and never without a client
/// that can ask), release_all_input, the request's cancellation and the user's pause stopping type_text, the
/// pause shared by two servers, window-changing tools refused while paused, and the release of held input when
/// the client closes the server's stdin. The input layer, the hotkey and the shared pause are fakes: nothing is
/// sent, registered or opened for real. The elicitation, cancellation and stdin EOF tests run on both protocol paths
/// (<see cref="Revisions"/>): the SDK client's default revision, where elicitation is a multi-round-trip request, and the
/// initialize handshake of today's stdio clients (2025-06-18 and 2025-11-25), where it is a request from the server
/// while the call is open.
/// </summary>
public sealed class InputControlToolContractTests : IDisposable
{
    public static TheoryData<string?> Revisions => InProcessMcpServer.Revisions();

    private const uint KeyDown = 0x0000, KeyUp = 0x0002;
    private const ushort VkControl = 0x11, VkLControl = 0xA2, VkLMenu = 0xA4, VkLShift = 0xA0, VkLWin = 0x5B;
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    private readonly FakeInputWrapper _input = new();
    private readonly ManualClock _clock = new();
    private readonly InputSession _session;
    private readonly StubHotkey _hotkey = new();
    private int _questions;
    private ElicitRequestParams? _question;

    public InputControlToolContractTests()
    {
        _session = new InputSession(_input, PauseHotkeyOptions.Default, _clock);
    }

    public void Dispose() => _session.Dispose();

    private Task<InProcessMcpServer> StartAsync(
        Func<ElicitRequestParams, CancellationToken, ValueTask<ElicitResult>>? answer = null,
        TimeSpan? answerTimeout = null,
        TaskCompletionSource<RequestId>? requestId = null,
        TaskCompletionSource? callEnded = null,
        InputSession? session = null,
        FakeInputWrapper? input = null,
        string? revision = null)
    {
        session ??= _session;
        input ??= _input;
        McpClientOptions? options = null;
        if (answer != null)
        {
            options = new McpClientOptions
            {
                Handlers = new McpClientHandlers
                {
                    ElicitationHandler = (request, token) =>
                    {
                        Interlocked.Increment(ref _questions);
                        _question = request;
                        return answer(request!, token);
                    }
                }
            };
        }

        var monitors = TestDataFactory.CreateMockMonitorService(
            [TestDataFactory.CreateMonitorInfo(boundsX: 0, boundsY: 0, boundsWidth: 1920, boundsHeight: 1080)]);
        var service = new InputSimulationService(monitors.Object, input, TestDataFactory.CreateAllowingInputGuard().Object, session);

        // A window-changing tool that only records that it ran
        var windowTool = McpServerTool.Create(
            () => { Interlocked.Increment(ref _windowToolRuns); return "activated"; },
            new McpServerToolCreateOptions { Name = "launch_applications_batch" });
        return InProcessMcpServer.StartAsync(
            [windowTool],
            builder =>
            {
                builder
                    .WithRequestFilters(filters => filters.AddCallToolFilter(next => async (request, token) =>
                    {
                        // The JSON-RPC id of the call, which a client names when it cancels the call
                        requestId?.TrySetResult(request.JsonRpcRequest.Id);
                        try
                        {
                            return await next(request, token);
                        }
                        finally
                        {
                            callEnded?.TrySetResult();
                        }
                    }))
                    .AddToolErrorReporting()
                    .AddInputGateSerialization()
                    .WithServerTools(new InputSimulationTools(service));

                // The input control tools are registered as the server registers them: the SDK builds a new
                // instance from the services for every call, so no state of theirs outlives a call. Only a test
                // that needs a shorter answer timeout registers one instance instead.
                _ = answerTimeout is { } timeout
                    ? builder.WithServerTools(new InputControlTools(session, _hotkey, timeout))
                    : builder.WithServerTools<InputControlTools>();
            },
            clientOptions: InProcessMcpServer.WithRevision(options, revision),
            configureServices: services => services
                .AddSingleton<IInputSession>(session)
                .AddSingleton<IPauseHotkey>(_hotkey));
    }

    private int _windowToolRuns;

    private static JsonElement Payload(CallToolResult result)
    {
        var payload = JsonDocument.Parse(result.Content.OfType<TextContentBlock>().Single().Text).RootElement;
        if (payload.TryGetProperty("success", out var success))
        {
            (result.IsError == true).Should().Be(!success.GetBoolean(), "a result goes out with isError = !success (roadmap M2.1)");
        }

        return payload;
    }

    private static ValueTask<ElicitResult> Answer(string action, bool? resume = null) =>
        ValueTask.FromResult(new ElicitResult
        {
            Action = action,
            Content = resume is { } value
                ? new Dictionary<string, JsonElement> { [InputControlTools.ResumeField] = JsonSerializer.SerializeToElement(value) }
                : null
        });

    #region get_control_status

    [Fact]
    public async Task GetControlStatus_ReportsThePauseTheHotkeyAndHeldInput()
    {
        // Arrange
        _session.Pause(InputControlSources.Hotkey);
        _input.Press(VkLControl);
        await using var server = await StartAsync();

        // Act
        var status = Payload(await server.CallToolAsync("get_control_status"));

        // Assert
        status.GetProperty("paused").GetBoolean().Should().BeTrue();
        status.GetProperty("pausedBy").GetString().Should().Be("hotkey");
        status.TryGetProperty("pausedAt", out _).Should().BeTrue();
        var hotkey = status.GetProperty("hotkey");
        hotkey.GetProperty("chord").GetString().Should().Be("Ctrl+Alt+Shift+Pause");
        hotkey.GetProperty("state").GetString().Should().Be("Registered");
        status.GetProperty("heldInput").GetProperty("modifiersDown").EnumerateArray().Select(e => e.GetString())
            .Should().Equal("LeftCtrl");
        status.GetProperty("message").GetString().Should().Contain("Stop and ask the user");
    }

    [Fact]
    public async Task GetControlStatus_ReportsAChordAnotherServerHolds_AndTheSharedPause()
    {
        _session.ShareDesktopPause(new FakeDesktopPauseFlag());
        _hotkey.Status = new PauseHotkeyStatus("Ctrl+Alt+Shift+Pause", PauseHotkeyState.OtherServer, "default", "Another server has it");
        await using var server = await StartAsync();

        var status = Payload(await server.CallToolAsync("get_control_status"));

        status.GetProperty("sharedPause").GetBoolean().Should().BeTrue();
        status.GetProperty("hotkey").GetProperty("state").GetString().Should().Be("OtherServer");
        status.GetProperty("message").GetString().Should().Contain("pauses every server");
    }

    [Fact]
    public async Task GetControlStatus_ReportsAChordAnotherServerHolds_WhenThePauseIsNotShared()
    {
        _hotkey.Status = new PauseHotkeyStatus("Ctrl+Alt+Shift+Pause", PauseHotkeyState.OtherServer, "default", "Another server has it");
        await using var server = await StartAsync();

        var status = Payload(await server.CallToolAsync("get_control_status"));

        status.GetProperty("sharedPause").GetBoolean().Should().BeFalse();
        status.GetProperty("message").GetString().Should().Contain("does not pause this server").And.Contain("Tell the user");
    }

    [Fact]
    public async Task GetControlStatus_ReportsAHotkeyThatCouldNotBeRegistered()
    {
        _hotkey.Status = new PauseHotkeyStatus("Ctrl+Alt+Shift+Pause", PauseHotkeyState.Failed, "default", "another program already uses this chord");
        await using var server = await StartAsync();

        var status = Payload(await server.CallToolAsync("get_control_status"));

        status.GetProperty("paused").GetBoolean().Should().BeFalse();
        status.GetProperty("hotkey").GetProperty("state").GetString().Should().Be("Failed");
        status.GetProperty("message").GetString().Should().Contain("not available").And.Contain("another program");
    }

    #endregion

    #region resume_input

    [Fact]
    public async Task ResumeInput_TakesNoArguments_TheServerIsBoundByTheSdk()
    {
        await using var server = await StartAsync();

        var tool = (await server.Client.ListToolsAsync()).Single(t => t.Name == "resume_input");

        tool.JsonSchema.GetProperty("properties").EnumerateObject().Should().BeEmpty();
    }

    [Fact]
    public async Task ResumeInput_WhenNotPaused_AsksNobody()
    {
        await using var server = await StartAsync(answer: (_, _) => Answer("accept", true));

        var result = Payload(await server.CallToolAsync("resume_input"));

        result.GetProperty("success").GetBoolean().Should().BeTrue();
        result.GetProperty("wasPaused").GetBoolean().Should().BeFalse();
        _questions.Should().Be(0);
    }

    [Theory]
    [MemberData(nameof(Revisions))]
    public async Task ResumeInput_ClientWithoutElicitation_IsResumeNeedsUser_AndStaysPaused(string? revision)
    {
        // The agent cannot resume itself: with no way to ask the user, only the hotkey resumes
        _session.Pause(InputControlSources.Hotkey);
        await using var server = await StartAsync(revision: revision);

        var result = Payload(await server.CallToolAsync("resume_input"));

        result.GetProperty("success").GetBoolean().Should().BeFalse();
        result.GetProperty("errorCode").GetString().Should().Be(nameof(InputErrorCode.ResumeNeedsUser));
        result.GetProperty("error").GetString().Should().Contain("Ctrl+Alt+Shift+Pause");
        _session.IsPaused.Should().BeTrue();
    }

    [Theory]
    [MemberData(nameof(Revisions))]
    public async Task ResumeInput_TheUserConfirms_Resumes(string? revision)
    {
        // Arrange
        _session.Pause(InputControlSources.Hotkey);
        await using var server = await StartAsync(answer: (_, _) => Answer("accept", true), revision: revision);

        // Act
        var result = Payload(await server.CallToolAsync("resume_input"));

        // Assert - the user saw what they are agreeing to, as a yes/no field that defaults to no
        result.GetProperty("success").GetBoolean().Should().BeTrue();
        result.GetProperty("resumedBy").GetString().Should().Be(InputControlSources.ResumeInput);
        _session.IsPaused.Should().BeFalse();
        _session.PauseState.ResumedBy.Should().Be(InputControlSources.ResumeInput);
        _question!.Message.Should().Contain("resume keyboard and mouse input").And.Contain("Ctrl+Alt+Shift+Pause");
        var field = _question.RequestedSchema!.Properties[InputControlTools.ResumeField]
            .Should().BeOfType<ElicitRequestParams.BooleanSchema>().Subject;
        field.Default.Should().BeFalse();
        _question.RequestedSchema.Required.Should().Equal(InputControlTools.ResumeField);
    }

    [Theory]
    [InlineData("accept", false)] // the form was submitted, but the box was not ticked
    [InlineData("accept", null)]
    [InlineData("decline", null)]
    [InlineData("cancel", null)]
    public async Task ResumeInput_TheUserDoesNotConfirm_StaysPaused(string action, bool? resume)
    {
        _session.Pause(InputControlSources.Hotkey);
        await using var server = await StartAsync(answer: (_, _) => Answer(action, resume));

        var result = Payload(await server.CallToolAsync("resume_input"));

        result.GetProperty("success").GetBoolean().Should().BeFalse();
        result.GetProperty("errorCode").GetString().Should().Be(nameof(InputErrorCode.ResumeDeclined));
        _session.IsPaused.Should().BeTrue();
        _questions.Should().Be(1);
    }

    public static TheoryData<string> InitializeRevisions => new() { InProcessMcpServer.InitializeRevision, InProcessMcpServer.NewestInitializeRevision };

    [Theory]
    [MemberData(nameof(InitializeRevisions))]
    public async Task ResumeInput_TheUserDoesNotAnswer_StaysPaused_AndTheCallEndsAtTheAnswerTimeout(string revision)
    {
        // Arrange - the user never answers within the test: the question stays open until the client gives up
        _session.Pause(InputControlSources.Hotkey);
        var userAnswered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var server = await StartAsync(
            answer: async (_, token) =>
            {
                try
                {
                    await Task.Delay(Timeout, token);
                }
                finally
                {
                    userAnswered.TrySetResult();
                }

                return new ElicitResult { Action = "accept" };
            },
            answerTimeout: TimeSpan.FromMilliseconds(100),
            revision: revision);

        // Act
        var result = Payload(await server.CallToolAsync("resume_input"));

        // Assert - the call ended while the question was still open: the answer timeout, not the user, ended it
        userAnswered.Task.IsCompleted.Should().BeFalse("the server's answer timeout ends the call on the initialize handshake");
        result.GetProperty("errorCode").GetString().Should().Be(nameof(InputErrorCode.ResumeDeclined));
        result.GetProperty("error").GetString().Should().Contain("did not answer");
        _session.IsPaused.Should().BeTrue();
    }

    [Fact]
    public async Task ResumeInput_OnTheMultiRoundTripRevision_ALateAnswer_StillLeavesInputPaused()
    {
        // Arrange - on the SDK's default revision the question is a request the server cannot take back: the call stays
        // open until the client's handler returns (pinned here, so a change in either direction is seen), and an answer
        // that comes after the server's answer timeout is not taken as a yes
        _session.Pause(InputControlSources.Hotkey);
        var userAnswered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var server = await StartAsync(
            answer: async (_, token) =>
            {
                await Task.Delay(TimeSpan.FromMilliseconds(300), token);
                userAnswered.TrySetResult();
                return await Answer("accept", resume: true);
            },
            answerTimeout: TimeSpan.FromMilliseconds(100));

        // Act
        var result = Payload(await server.CallToolAsync("resume_input"));

        // Assert
        userAnswered.Task.IsCompleted.Should().BeTrue("the call waits for the client's handler on the multi-round-trip revision");
        result.GetProperty("errorCode").GetString().Should().Be(nameof(InputErrorCode.ResumeDeclined));
        result.GetProperty("error").GetString().Should().Contain("did not answer");
        _session.IsPaused.Should().BeTrue("a yes that came after the answer timeout does not resume input");
    }

    [Theory]
    [MemberData(nameof(Revisions))]
    public async Task ResumeInput_ASecondCallWhileTheFirstAsks_IsDeclined_WithoutAskingAgain(string? revision)
    {
        // Arrange - the first question stays open until the test answers it
        _session.Pause(InputControlSources.Hotkey);
        var asked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var answer = new TaskCompletionSource<ElicitResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var server = await StartAsync(
            answer: (_, _) =>
            {
                asked.TrySetResult();
                return new ValueTask<ElicitResult>(answer.Task);
            },
            revision: revision);
        var first = server.CallToolAsync("resume_input");
        await asked.Task.WaitAsync(Timeout);

        // Act
        var second = Payload(await server.CallToolAsync("resume_input"));
        answer.SetResult(new ElicitResult
        {
            Action = "accept",
            Content = new Dictionary<string, JsonElement> { [InputControlTools.ResumeField] = JsonSerializer.SerializeToElement(true) }
        });
        var firstResult = Payload(await first);

        // Assert - one question only, although every call gets its own InputControlTools; the first call's answer counts
        second.GetProperty("errorCode").GetString().Should().Be(nameof(InputErrorCode.ResumeDeclined));
        second.GetProperty("error").GetString().Should().Contain("already waiting");
        _questions.Should().Be(1);
        firstResult.GetProperty("success").GetBoolean().Should().BeTrue();
        _session.IsPaused.Should().BeFalse();
    }

    [Fact]
    public async Task ResumeInput_TheUserPausesAgainWhileTheQuestionIsOpen_TheOldAnswerDoesNotResume()
    {
        // The user resumes with the hotkey and pauses again (the agent misbehaved), then answers the old question
        _session.Pause(InputControlSources.Hotkey);
        await using var server = await StartAsync(answer: (_, _) =>
        {
            _session.Resume(InputControlSources.Hotkey);
            _session.Pause(InputControlSources.Hotkey);
            return Answer("accept", true);
        });

        var result = Payload(await server.CallToolAsync("resume_input"));

        result.GetProperty("errorCode").GetString().Should().Be(nameof(InputErrorCode.ResumeDeclined));
        result.GetProperty("error").GetString().Should().Contain("paused input again");
        _session.IsPaused.Should().BeTrue();
    }

    [Fact]
    public async Task ResumeInput_WithASharedPause_TellsTheUserThatEveryServerResumes()
    {
        _session.ShareDesktopPause(new FakeDesktopPauseFlag());
        _session.Pause(InputControlSources.Hotkey);
        await using var server = await StartAsync(answer: (_, _) => Answer("decline"));

        await server.CallToolAsync("resume_input");

        _question!.Message.Should().Contain("every Windows App Manager server on this desktop");
    }

    [Fact]
    public async Task ResumeInput_TheUserPressesTheHotkeyInstead_ReportsThatInputRuns()
    {
        _session.Pause(InputControlSources.Hotkey);
        await using var server = await StartAsync(answer: (_, _) =>
        {
            _clock.Advance(InputSession.HotkeyResumeDelay);
            _session.TogglePauseByHotkey();
            return Answer("cancel");
        });

        var result = Payload(await server.CallToolAsync("resume_input"));

        result.GetProperty("success").GetBoolean().Should().BeTrue();
        result.GetProperty("resumedBy").GetString().Should().Be(InputControlSources.Hotkey);
    }

    #endregion

    #region release_all_input

    [Fact]
    public async Task ReleaseAllInput_ReleasesWhatTheServerHolds_ButNotTheUsersKeys()
    {
        // Arrange - a key the server pressed is still down, and the user holds Ctrl
        _session.Send([InputEvent.Key(0x41, KeyDown)]);
        _input.Press(VkLControl);
        await using var server = await StartAsync();

        // Act
        var result = Payload(await server.CallToolAsync("release_all_input"));

        // Assert
        result.GetProperty("success").GetBoolean().Should().BeTrue();
        result.GetProperty("released").EnumerateArray().Select(e => e.GetString()).Should().Equal("A");
        result.GetProperty("modifiersDown").EnumerateArray().Select(e => e.GetString()).Should().Equal("LeftCtrl");
        _input.IsKeyDown(VkLControl).Should().BeTrue();
    }

    [Fact]
    public async Task ReleaseAllInput_WithReleaseHeldModifiers_ReleasesStuckModifiers()
    {
        // A stuck Windows key: not one of the pause hotkey's modifiers (Ctrl+Alt+Shift+Pause)
        _input.Press(VkLWin);
        await using var server = await StartAsync();

        var result = Payload(await server.CallToolAsync("release_all_input",
            new Dictionary<string, object?> { ["releaseHeldModifiers"] = true }));

        result.GetProperty("success").GetBoolean().Should().BeTrue();
        result.GetProperty("released").EnumerateArray().Select(e => e.GetString()).Should().Equal("LeftWin");
        result.GetProperty("modifiersDown").GetArrayLength().Should().Be(0);
        _input.IsKeyDown(VkLWin).Should().BeFalse();
    }

    [Fact]
    public async Task ReleaseAllInput_OneOfThePauseChordsModifiers_IsLeftDown()
    {
        // The user has pressed Ctrl, the first key of Ctrl+Alt+Shift+Pause; releasing it would make the chord miss
        _input.Press(VkLControl);
        await using var server = await StartAsync();

        var result = Payload(await server.CallToolAsync("release_all_input",
            new Dictionary<string, object?> { ["releaseHeldModifiers"] = true }));

        result.GetProperty("errorCode").GetString().Should().Be(nameof(InputErrorCode.ModifiersHeld));
        result.GetProperty("error").GetString().Should().Contain("LeftCtrl is down")
            .And.Contain("the pause hotkey (Ctrl+Alt+Shift+Pause) uses it");
        _input.Batches.Should().BeEmpty();
        _input.IsKeyDown(VkLControl).Should().BeTrue();
    }

    [Fact]
    public void ReleaseAllInput_AStuckKeyBesideOneOfThePauseChordsModifiers_IsLeftDownToo()
    {
        // One modifier of the chord is enough to release nothing; the Windows key, which is not part of the chord, is
        // left down too, so the error must not call every key down a modifier of the hotkey
        _input.Press(VkLControl);
        _input.Press(VkLWin);
        var tools = new InputControlTools(_session, _hotkey);

        var result = tools.ReleaseAllInput(releaseHeldModifiers: true);

        result.ErrorCode.Should().Be(InputErrorCode.ModifiersHeld);
        result.Error.Should().Contain("LeftCtrl, LeftWin are down").And.Contain("uses at least one of them")
            .And.NotContain("the modifiers of the pause hotkey");
        _input.Batches.Should().BeEmpty();
        new[] { VkLControl, VkLWin }.Should().OnlyContain(key => _input.IsKeyDown(key));
    }

    [Fact]
    public async Task ReleaseAllInput_ThePauseChordsModifiers_AreLeftDown()
    {
        // The user holds Ctrl+Alt+Shift to press the pause hotkey; releasing them would make the chord miss
        _input.Press(VkLControl);
        _input.Press(VkLMenu);
        _input.Press(VkLShift);
        await using var server = await StartAsync();

        var result = Payload(await server.CallToolAsync("release_all_input",
            new Dictionary<string, object?> { ["releaseHeldModifiers"] = true }));

        result.GetProperty("errorCode").GetString().Should().Be(nameof(InputErrorCode.ModifiersHeld));
        result.GetProperty("error").GetString().Should().Contain("pause hotkey");
        _input.Batches.Should().BeEmpty();
        new[] { VkLControl, VkLMenu, VkLShift }.Should().OnlyContain(key => _input.IsKeyDown(key));
    }

    [Fact]
    public async Task ReleaseAllInput_WhilePaused_ReleasesTheServersKeysButNotTheUsers()
    {
        _session.Send([InputEvent.Key(0x41, KeyDown)]);
        _session.Pause(InputControlSources.Hotkey); // releases the server's A itself
        _session.Send([InputEvent.Key(0x42, KeyDown)]); // refused: paused
        _input.Press(VkLWin);
        await using var server = await StartAsync();

        var result = Payload(await server.CallToolAsync("release_all_input",
            new Dictionary<string, object?> { ["releaseHeldModifiers"] = true }));

        result.GetProperty("errorCode").GetString().Should().Be(nameof(InputErrorCode.PausedByUser));
        _input.IsKeyDown(VkLWin).Should().BeTrue();
        _input.IsKeyDown(0x41).Should().BeFalse();
    }

    #endregion

    #region Cancellation, pause and stdin EOF through MCP

    [Theory]
    [MemberData(nameof(Revisions))]
    public async Task TypeText_CancelledByTheClient_StopsTypingOnTheServer(string? revision)
    {
        // Arrange - fifty characters 100 ms apart, five seconds of typing
        const string text = "abcdefghijklmnopqrstuvwxyzabcdefghijklmnopqrstuvwx";
        var requestId = new TaskCompletionSource<RequestId>(TaskCreationOptions.RunContinuationsAsynchronously);
        var callEnded = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var server = await StartAsync(requestId: requestId, callEnded: callEnded, revision: revision);
        using var clientGivesUp = new CancellationTokenSource();
        var call = server.CallToolAsync("type_text",
            new Dictionary<string, object?> { ["text"] = text, ["delayMs"] = 100 }, cancellationToken: clientGivesUp.Token);
        var id = await requestId.Task.WaitAsync(Timeout);
        SpinWait.SpinUntil(() => _input.Batches.Count >= 2, Timeout).Should().BeTrue();

        // Act - the client cancels the call: MCP clients send notifications/cancelled with the call's id. The test sends
        // it itself, as any client does, so it does not depend on when the SDK client sends it for its own token.
        await server.Client.SendNotificationAsync(NotificationMethods.CancelledNotification,
            new CancelledNotificationParams { RequestId = id, Reason = "the user cancelled" });

        // Assert - the server's request token ended the call between characters, long before the end of the text,
        // and nothing stays down
        await callEnded.Task.WaitAsync(Timeout);
        _input.Batches.Count.Should().BeLessThan(text.Length, "typing stopped before the end of the text");
        _session.ServerHeld.Should().BeEmpty();

        await server.AssertNoResponseSentAsync(id);
        clientGivesUp.Cancel();
        await FluentActions.Awaiting(() => call).Should().ThrowAsync<Exception>("the cancelled call gets no result");
    }

    [Fact]
    public async Task TypeText_PausedByTheUserMidway_ReturnsPausedByUser()
    {
        // Arrange - the user presses the hotkey (handled by another server on the desktop) while the third character
        // is being sent. The pause is in force before the next batch, whether or not the watcher has run yet.
        var flag = new FakeDesktopPauseFlag();
        _session.ShareDesktopPause(flag);
        await using var server = await StartAsync();
        _input.BeforeSend = _ =>
        {
            if (_input.Batches.Count == 2)
            {
                flag.SetByAnotherServer();
            }
        };

        // Act
        var result = Payload(await server.CallToolAsync("type_text",
            new Dictionary<string, object?> { ["text"] = "abcdefghij", ["delayMs"] = 100 }));

        // Assert
        result.GetProperty("success").GetBoolean().Should().BeFalse();
        result.GetProperty("errorCode").GetString().Should().Be(nameof(InputErrorCode.PausedByUser));
        result.GetProperty("charactersTyped").GetInt32().Should().Be(3);
        _input.Batches.Should().HaveCount(3);
    }

    [Fact]
    public async Task TwoServers_TheHotkeyOfOne_StopsTheOthersTyping()
    {
        // Arrange - two servers (two MCP client sessions) on one desktop; the first holds the pause hotkey
        var flag = new FakeDesktopPauseFlag();
        var firstInput = new FakeInputWrapper();
        using var first = new InputSession(firstInput);
        first.ShareDesktopPause(flag);
        _session.ShareDesktopPause(flag);
        await using var firstServer = await StartAsync(session: first, input: firstInput);
        await using var secondServer = await StartAsync();
        _input.BeforeSend = _ =>
        {
            if (_input.Batches.Count == 2)
            {
                // The user presses the chord; Windows sends it to the first server
                Task.Run(first.TogglePauseByHotkey);
            }
        };

        // Act - the second server types
        var result = Payload(await secondServer.CallToolAsync("type_text",
            new Dictionary<string, object?> { ["text"] = "abcdefghijklmnopqrst", ["delayMs"] = 100 }));
        var refused = Payload(await secondServer.CallToolAsync("send_keys", new Dictionary<string, object?> { ["keys"] = "x" }));

        // Assert - it stops, and both servers report the pause
        result.GetProperty("errorCode").GetString().Should().Be(nameof(InputErrorCode.PausedByUser));
        result.GetProperty("charactersTyped").GetInt32().Should().BeLessThan(20);
        refused.GetProperty("errorCode").GetString().Should().Be(nameof(InputErrorCode.PausedByUser));
        Payload(await firstServer.CallToolAsync("get_control_status")).GetProperty("paused").GetBoolean().Should().BeTrue();
        Payload(await secondServer.CallToolAsync("get_control_status")).GetProperty("paused").GetBoolean().Should().BeTrue();
    }

    [Fact]
    public async Task WhilePaused_AWindowChangingTool_ReturnsPausedByUser_ButReadsWork()
    {
        _session.Pause(InputControlSources.Hotkey);
        await using var server = await StartAsync();

        var focus = await server.CallToolAsync("launch_applications_batch");
        var status = Payload(await server.CallToolAsync("get_control_status"));

        focus.IsError.Should().BeTrue();
        JsonDocument.Parse(focus.Content.OfType<TextContentBlock>().Single().Text).RootElement
            .GetProperty("errorCode").GetString().Should().Be("PausedByUser");
        _windowToolRuns.Should().Be(0);
        status.GetProperty("paused").GetBoolean().Should().BeTrue();
    }

    [Fact]
    public async Task SendKeys_ThePauseChord_IsRefusedThroughMcp()
    {
        await using var server = await StartAsync();

        var result = Payload(await server.CallToolAsync("send_keys", new Dictionary<string, object?> { ["keys"] = "^%+{PAUSE}" }));

        result.GetProperty("errorCode").GetString().Should().Be(nameof(InputErrorCode.PauseChordNotAllowed));
        _input.Batches.Should().BeEmpty();
    }

    [Theory]
    [MemberData(nameof(Revisions))]
    public async Task ClosingTheServersStdin_ReleasesHeldInput(string? revision)
    {
        // Arrange - the server as Program.cs runs it, with the session start-up service and a fake hotkey; a key
        // the server pressed is still down
        var native = new FakeHotkeyWrapper();
        await using var server = await InProcessMcpServer.StartAsync(
            [],
            clientOptions: InProcessMcpServer.WithRevision(null, revision),
            configureServices: services => services
                .AddSingleton<IInputSession>(_session)
                .AddSingleton<IPauseHotkey>(new PauseHotkeyListener(native, PauseHotkeyOptions.Default))
                .AddHostedService<InputSessionStartup>());
        _session.Send([InputEvent.Key(VkControl, KeyDown)]);
        var stopping = server.Services.GetRequiredService<IHostApplicationLifetime>().ApplicationStopping;

        // Act - the client closes the server's input stream (stdin EOF)
        await server.CloseServerInputAsync();

        // Assert - the SDK ends the session and stops the application, which releases the key. The release runs in
        // a callback of ApplicationStopping, after the token reports the cancellation, so wait for the release itself.
        SpinWait.SpinUntil(() => _session.LastRelease != null, Timeout).Should().BeTrue();
        stopping.IsCancellationRequested.Should().BeTrue();
        _input.Batches[^1].Should().Equal(InputEvent.Key(VkControl, KeyUp));
        _input.IsKeyDown(VkControl).Should().BeFalse();
        _session.LastRelease!.Reason.Should().Be("the server is stopping");
    }

    #endregion

    /// <summary>A pause hotkey that only reports a status.</summary>
    private sealed class StubHotkey : IPauseHotkey
    {
        public PauseHotkeyStatus Status { get; set; } =
            new("Ctrl+Alt+Shift+Pause", PauseHotkeyState.Registered, PauseHotkeyOptions.DefaultSource);

        public void Start(Action pressed)
        {
        }

        public void Stop()
        {
        }
    }
}
