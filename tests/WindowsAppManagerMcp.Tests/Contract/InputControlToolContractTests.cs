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
        string? revision = null,
        IProcessSecurityProbe? securityProbe = null)
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
            configureServices: services =>
            {
                services
                    .AddSingleton<IInputSession>(session)
                    .AddSingleton<IPauseHotkey>(_hotkey);
                if (securityProbe != null)
                {
                    services.AddSingleton(securityProbe);
                }
            });
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

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task GetControlStatus_WhileTheSessionIsLocked_SaysSo_NotOnlyThatInputIsRunningOrPaused(bool paused)
    {
        // Arrange - issue #18: an agent read 'Input is running' as an unlocked desktop
        if (paused)
        {
            _session.Pause(InputControlSources.Hotkey);
        }

        var probe = new Mock<IProcessSecurityProbe>();
        probe.Setup(p => p.GetSessionLock()).Returns(SessionLockState.Locked);
        await using var server = await StartAsync(securityProbe: probe.Object);

        // Act
        var status = Payload(await server.CallToolAsync("get_control_status"));

        // Assert
        status.GetProperty("message").GetString().Should().Contain(paused ? "Input is paused" : "Input is running")
            .And.Contain("The session is locked").And.Contain("SessionLocked");
    }

    [Theory]
    [InlineData(SessionLockState.Unlocked)]
    [InlineData(SessionLockState.Unknown)]
    public async Task GetControlStatus_WhileTheSessionIsNotKnownToBeLocked_SaysNothingOfALock(SessionLockState lockState)
    {
        var probe = new Mock<IProcessSecurityProbe>();
        probe.Setup(p => p.GetSessionLock()).Returns(lockState);
        await using var server = await StartAsync(securityProbe: probe.Object);

        var status = Payload(await server.CallToolAsync("get_control_status"));

        status.GetProperty("message").GetString().Should().Contain("Input is running").And.NotContain("locked");
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

    [Fact]
    public async Task ResumeInput_ClientWithoutElicitation_WhenTheServerPausedItself_SaysWhy_NotThatTheUserPausedIt()
    {
        // The server pauses itself when it cannot read the shared pause: the agent must not tell the user they paused it
        using var session = new InputSession(_input, PauseHotkeyOptions.Default, _clock);
        session.ShareDesktopPause(new FakeDesktopPauseFlag { ReadFails = new IOException("the handle broke") });
        await using var server = await StartAsync(session: session);

        var result = Payload(await server.CallToolAsync("resume_input"));

        result.GetProperty("errorCode").GetString().Should().Be(nameof(InputErrorCode.ResumeNeedsUser));
        result.GetProperty("error").GetString().Should().Contain("Input is paused").And.Contain("could not read the pause it shares")
            .And.NotContain("paused by the user").And.NotContain("pressed the pause hotkey").And.Contain("Ctrl+Alt+Shift+Pause");
        session.IsPaused.Should().BeTrue();
    }

    [Fact]
    public async Task ResumeInput_ClientWithoutElicitation_AfterTheHotkey_SaysTheUserPausedIt()
    {
        _session.Pause(InputControlSources.Hotkey);
        await using var server = await StartAsync();

        var result = Payload(await server.CallToolAsync("resume_input"));

        result.GetProperty("error").GetString().Should().Contain("Input is paused: the user pressed the pause hotkey (Ctrl+Alt+Shift+Pause)")
            .And.Contain("by pressing Ctrl+Alt+Shift+Pause again").And.NotContain("twice");
    }

    [Fact]
    public async Task ResumeInput_ClientWithoutElicitation_WhenTheServerPausedItself_SaysTheUserPressesTheHotkeyTwice()
    {
        // After a pause the server made itself, the hotkey's first press makes the pause the user's own and only the next one
        // resumes (InputSession.TogglePauseByHotkey): "with the hotkey" would have the user press once and find input still paused
        using var session = new InputSession(_input, PauseHotkeyOptions.Default, _clock);
        session.ShareDesktopPause(new FakeDesktopPauseFlag { ReadFails = new IOException("the handle broke") });
        await using var server = await StartAsync(session: session);

        var result = Payload(await server.CallToolAsync("resume_input"));

        result.GetProperty("errorCode").GetString().Should().Be(nameof(InputErrorCode.ResumeNeedsUser));
        result.GetProperty("error").GetString().Should().Contain("the first press keeps the pause")
            .And.Contain("a second press 2 seconds or more later resumes");
    }

    [Theory]
    [MemberData(nameof(InitializeRevisions))]
    public async Task ResumeInput_TheQuestionFails_SaysWhyInputIsPaused_AndHowTheUserResumes(string revision)
    {
        // On the initialize handshake's revisions the question is a request from the server, so a client that cannot show it
        // fails that request: the error says why input is paused, as the other ResumeNeedsUser error does
        _session.Pause(InputControlSources.Hotkey);
        await using var server = await StartAsync(
            answer: (_, _) => throw new InvalidOperationException("The client could not show the question."), revision: revision);

        var result = Payload(await server.CallToolAsync("resume_input"));

        result.GetProperty("errorCode").GetString().Should().Be(nameof(InputErrorCode.ResumeNeedsUser));
        result.GetProperty("error").GetString().Should().Contain("could not be asked")
            .And.Contain("Input is paused: the user pressed the pause hotkey (Ctrl+Alt+Shift+Pause)")
            .And.Contain("by pressing Ctrl+Alt+Shift+Pause again");
        _session.IsPaused.Should().BeTrue();
    }

    [Theory]
    [MemberData(nameof(InitializeRevisions))]
    public async Task ResumeInput_TheQuestionFails_WhenTheServerPausedItself_SaysWhy_AndThatTheUserPressesTheHotkeyTwice(string revision)
    {
        using var session = new InputSession(_input, PauseHotkeyOptions.Default, _clock);
        session.ShareDesktopPause(new FakeDesktopPauseFlag { ReadFails = new IOException("the handle broke") });
        await using var server = await StartAsync(
            answer: (_, _) => throw new InvalidOperationException("The client could not show the question."), session: session,
            revision: revision);

        var result = Payload(await server.CallToolAsync("resume_input"));

        result.GetProperty("errorCode").GetString().Should().Be(nameof(InputErrorCode.ResumeNeedsUser));
        result.GetProperty("error").GetString().Should().Contain("could not read the pause it shares")
            .And.Contain("the first press keeps the pause").And.NotContain("pressed the pause hotkey");
        session.IsPaused.Should().BeTrue();
    }

    [Fact]
    public async Task ResumeInput_WhenTheServerPausedItself_TheQuestionSaysWhy_NotThatTheUserPausedIt()
    {
        // The user is asked: telling them "You paused it" would be false, and could make them resume a pause they never made
        using var session = new InputSession(_input, PauseHotkeyOptions.Default, _clock);
        session.ShareDesktopPause(new FakeDesktopPauseFlag { ReadFails = new IOException("the handle broke") });
        await using var server = await StartAsync(answer: (_, _) => Answer("decline"), session: session);

        await server.CallToolAsync("resume_input");

        _question!.Message.Should().Contain("resume keyboard and mouse input").And.Contain("could not read the pause it shares")
            .And.NotContain("You paused it");
        session.IsPaused.Should().BeTrue();
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
        result.GetProperty("error").GetString().Should().Contain("paused again while the question was open")
            .And.Contain("the user pressed the pause hotkey (Ctrl+Alt+Shift+Pause)");
        _session.IsPaused.Should().BeTrue();
    }

    [Fact]
    public async Task ResumeInput_TheServerPausesItselfWhileTheQuestionIsOpen_SaysSo_NotThatTheUserPausedAgain()
    {
        // Another server resumes the shared pause while the question is open, and then this server can no longer read it, so it
        // pauses itself: a new pause, which the answer does not apply to, but not one the user made
        using var session = new InputSession(_input, PauseHotkeyOptions.Default, _clock);
        var flag = new FakeDesktopPauseFlag();
        session.ShareDesktopPause(flag);
        session.Pause(InputControlSources.Hotkey);
        await using var server = await StartAsync(session: session, answer: (_, _) =>
        {
            flag.ResetByAnotherServer();
            _ = session.PauseState; // takes the other server's resume before the reads fail
            flag.ReadFails = new IOException("the handle broke");
            _ = session.PauseState;
            return Answer("accept", true);
        });

        var result = Payload(await server.CallToolAsync("resume_input"));

        session.PauseState.PausedBy.Should().Be(InputControlSources.SharedPauseUnreadable);
        result.GetProperty("errorCode").GetString().Should().Be(nameof(InputErrorCode.ResumeDeclined));
        result.GetProperty("error").GetString().Should().Contain("paused again while the question was open")
            .And.Contain("could not read the pause it shares").And.NotContain("The user paused");
        session.IsPaused.Should().BeTrue();
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

    #region resume_input after the server paused itself (shared_pause_unreadable)

    // The server paused itself because it could not read the shared pause: the user did not pause it, a press of the
    // hotkey keeps that pause (as the user's own) and only a second one resumes. When another server holds the chord, its
    // presses reach this one only once the shared pause can be read again, and a shared pause set then (the user paused in
    // that server meanwhile) makes the pause the user's, so one press resumes it

    /// <summary>A session that shares a pause it cannot read, so it paused itself.</summary>
    private InputSession SelfPausedSession()
    {
        var session = new InputSession(_input, PauseHotkeyOptions.Default, _clock);
        session.ShareDesktopPause(new FakeDesktopPauseFlag { ReadFails = new IOException("the handle broke") });
        session.PauseState.PausedBy.Should().Be(InputControlSources.SharedPauseUnreadable);
        return session;
    }

    [Fact]
    public async Task ResumeInput_AfterTheServerPausedItself_TheQuestionSaysSo_NotThatTheUserPausedIt()
    {
        // Arrange
        using var session = SelfPausedSession();
        await using var server = await StartAsync(answer: (_, _) => Answer("decline"), session: session);

        // Act
        await server.CallToolAsync("resume_input");

        // Assert - and the user learns that, after a resume, a pause made in another server does not reach this one
        _question!.Message.Should().NotContain("You paused it").And.Contain("paused it itself")
            .And.Contain("could not read the pause it shares").And.Contain("every Windows App Manager server on this desktop")
            .And.Contain("After a resume, Ctrl+Alt+Shift+Pause still pauses this server").And.Contain("made in another server does not");
        session.IsPaused.Should().BeTrue();
    }

    [Fact]
    public async Task ResumeInput_AfterTheServerPausedItself_WhileAnotherServerHoldsTheHotkey_TheQuestionSaysTheHotkeyWillNotStopIt()
    {
        using var session = SelfPausedSession();
        _hotkey.Status = new PauseHotkeyStatus("Ctrl+Alt+Shift+Pause", PauseHotkeyState.OtherServer, "default", "Another server has it");
        await using var server = await StartAsync(answer: (_, _) => Answer("decline"), session: session);

        await server.CallToolAsync("resume_input");

        _question!.Message.Should().NotContain("You paused it").And.Contain("paused it itself")
            .And.Contain("After a resume, the pause hotkey (Ctrl+Alt+Shift+Pause) does not pause this server");
    }

    [Theory]
    [MemberData(nameof(Revisions))]
    public async Task ResumeInput_ClientWithoutElicitation_AfterTheServerPausedItself_SaysASecondPressResumes(string? revision)
    {
        using var session = SelfPausedSession();
        await using var server = await StartAsync(session: session, revision: revision);

        var result = Payload(await server.CallToolAsync("resume_input"));

        result.GetProperty("errorCode").GetString().Should().Be(nameof(InputErrorCode.ResumeNeedsUser));
        result.GetProperty("error").GetString().Should().NotContain("paused by the user").And.Contain("this server paused itself")
            .And.NotContain("Ctrl+Alt+Shift+Pause again").And.Contain("the first press keeps the pause")
            .And.Contain("a second press 2 seconds or more later resumes");
        session.IsPaused.Should().BeTrue();
    }

    [Fact]
    public async Task ResumeInput_ClientWithoutElicitation_AfterTheServerPausedItself_WhileAnotherServerHoldsTheHotkey_SaysTheHotkeyCannotResume()
    {
        using var session = SelfPausedSession();
        _hotkey.Status = new PauseHotkeyStatus("Ctrl+Alt+Shift+Pause", PauseHotkeyState.OtherServer, "default", "Another server has it");
        await using var server = await StartAsync(session: session);

        var result = Payload(await server.CallToolAsync("resume_input"));

        result.GetProperty("errorCode").GetString().Should().Be(nameof(InputErrorCode.ResumeNeedsUser));
        result.GetProperty("error").GetString().Should().NotContain("paused by the user").And.NotContain("pressing")
            .And.Contain("The pause hotkey cannot resume it").And.Contain("another Windows App Manager server has registered")
            .And.Contain("only restarting this server");
        session.IsPaused.Should().BeTrue();
    }

    [Fact]
    public async Task ResumeInput_TheUserCannotBeAsked_AfterTheServerPausedItself_DoesNotSayOnePressResumes()
    {
        // The client fails the question: the server gets an error instead of an answer
        using var session = SelfPausedSession();
        await using var server = await StartAsync(answer: (_, _) => throw new InvalidOperationException("the form could not be shown"),
            session: session, revision: InProcessMcpServer.InitializeRevision);

        var result = Payload(await server.CallToolAsync("resume_input"));

        result.GetProperty("errorCode").GetString().Should().Be(nameof(InputErrorCode.ResumeNeedsUser));
        result.GetProperty("error").GetString().Should().Contain("could not be asked")
            .And.NotContain("Ctrl+Alt+Shift+Pause again").And.Contain("a second press 2 seconds or more later resumes");
        session.IsPaused.Should().BeTrue();
    }

    [Fact]
    public async Task ResumeInput_TheUserCannotBeAsked_AfterTheServerPausedItself_WhileAnotherServerHoldsTheHotkey_NamesWhatStillResumes()
    {
        // This client can ask, so a later question may work: unlike a client without elicitation, not only a restart resumes
        using var session = SelfPausedSession();
        _hotkey.Status = new PauseHotkeyStatus("Ctrl+Alt+Shift+Pause", PauseHotkeyState.OtherServer, "default", "Another server has it");
        await using var server = await StartAsync(answer: (_, _) => throw new InvalidOperationException("the form could not be shown"),
            session: session, revision: InProcessMcpServer.InitializeRevision);

        var result = Payload(await server.CallToolAsync("resume_input"));

        result.GetProperty("errorCode").GetString().Should().Be(nameof(InputErrorCode.ResumeNeedsUser));
        result.GetProperty("error").GetString().Should().Contain("could not be asked").And.Contain("The pause hotkey cannot resume it")
            .And.Contain("only a resume_input the user confirms, or restarting this server").And.NotContain("with this client");
        session.IsPaused.Should().BeTrue();
    }

    [Fact]
    public async Task GetControlStatus_AfterTheServerPausedItself_OnceTheSharedPauseCanBeReadAgain_SaysASecondPressResumes()
    {
        // The other server's presses reach this one through the shared pause again: the first pauses every server, and this
        // one resumes when the second resumes them all
        var flag = new FakeDesktopPauseFlag { ReadFails = new IOException("the handle broke") };
        using var session = new InputSession(_input, PauseHotkeyOptions.Default, _clock);
        session.ShareDesktopPause(flag);
        flag.ReadFails = null;
        session.IsSharedPauseUnreadable.Should().BeFalse();
        _hotkey.Status = new PauseHotkeyStatus("Ctrl+Alt+Shift+Pause", PauseHotkeyState.OtherServer, "default", "Another server has it");
        await using var server = await StartAsync(session: session);

        var status = Payload(await server.CallToolAsync("get_control_status"));

        status.GetProperty("message").GetString().Should().Contain("by this server").And.NotContain("Ctrl+Alt+Shift+Pause again")
            .And.Contain("does not resume it at once").And.Contain("a second press 2 seconds or more later resumes")
            .And.NotContain("cannot read");
        flag.SetByAnotherServer();
        session.IsPaused.Should().BeTrue();
        // After the first press the pause is the user's own, so the next press resumes, and the message says so
        Payload(await server.CallToolAsync("get_control_status")).GetProperty("message").GetString().Should().Contain("by the user")
            .And.Contain("Ctrl+Alt+Shift+Pause again, or by confirming resume_input").And.NotContain("first press keeps the pause");
        flag.ResetByAnotherServer();
        session.IsPaused.Should().BeFalse();
    }

    /// <summary>
    /// A session that paused itself because it could not read the shared pause, in which the user paused with the hotkey
    /// another server registered while it could not; the shared pause can be read again.
    /// </summary>
    private InputSession UserPausedWhileUnreadableSession(out FakeDesktopPauseFlag flag)
    {
        flag = new FakeDesktopPauseFlag { ReadFails = new IOException("the handle broke") };
        var session = new InputSession(_input, PauseHotkeyOptions.Default, _clock);
        session.ShareDesktopPause(flag);
        session.PauseState.PausedBy.Should().Be(InputControlSources.SharedPauseUnreadable);
        flag.SetByAnotherServer();
        flag.ReadFails = null;
        _hotkey.Status = new PauseHotkeyStatus("Ctrl+Alt+Shift+Pause", PauseHotkeyState.OtherServer, "default", "Another server has it");
        return session;
    }

    [Fact]
    public async Task GetControlStatus_AfterTheServerPausedItself_TheUserPausedInAnotherServerMeanwhile_SaysOnePressResumes()
    {
        // Arrange - once the shared pause can be read, the pause in force is the user's: that server's next press resumes
        // every server, this one too, so "the first press keeps the pause" would be false
        using var session = UserPausedWhileUnreadableSession(out var flag);
        await using var server = await StartAsync(session: session);

        // Act
        var status = Payload(await server.CallToolAsync("get_control_status"));

        // Assert
        status.GetProperty("pausedBy").GetString().Should().Be(InputControlSources.Hotkey);
        status.GetProperty("message").GetString().Should().Contain("by the user")
            .And.Contain("Ctrl+Alt+Shift+Pause again, or by confirming resume_input").And.NotContain("first press keeps the pause");
        flag.ResetByAnotherServer();
        session.IsPaused.Should().BeFalse("one press of the other server's hotkey resumed every server");
    }

    [Fact]
    public async Task ResumeInput_ClientWithoutElicitation_AfterTheServerPausedItself_TheUserPausedInAnotherServerMeanwhile_SaysOnePressResumes()
    {
        using var session = UserPausedWhileUnreadableSession(out _);
        await using var server = await StartAsync(session: session);

        var result = Payload(await server.CallToolAsync("resume_input"));

        result.GetProperty("errorCode").GetString().Should().Be(nameof(InputErrorCode.ResumeNeedsUser));
        result.GetProperty("error").GetString().Should().Contain("Input is paused: the user pressed the pause hotkey (Ctrl+Alt+Shift+Pause)")
            .And.Contain("by pressing Ctrl+Alt+Shift+Pause again").And.NotContain("first press keeps the pause");
        session.IsPaused.Should().BeTrue();
    }

    [Fact]
    public async Task ResumeInput_AfterTheServerPausedItself_TheUserPausesInAnotherServerWhileTheQuestionIsOpen_TheOldAnswerDoesNotResume()
    {
        // Arrange - the question is about the server's own pause; while it is open, the user presses the hotkey another server
        // registered, which pauses every server: a pause of the user's, which that answer must not resume everywhere
        var flag = new FakeDesktopPauseFlag { ReadFails = new IOException("the handle broke") };
        using var session = new InputSession(_input, PauseHotkeyOptions.Default, _clock);
        session.ShareDesktopPause(flag);
        flag.ReadFails = null;
        _hotkey.Status = new PauseHotkeyStatus("Ctrl+Alt+Shift+Pause", PauseHotkeyState.OtherServer, "default", "Another server has it");
        await using var server = await StartAsync(session: session, answer: (_, _) =>
        {
            flag.SetByAnotherServer();
            return Answer("accept", true);
        });

        // Act
        var result = Payload(await server.CallToolAsync("resume_input"));

        // Assert
        result.GetProperty("errorCode").GetString().Should().Be(nameof(InputErrorCode.ResumeDeclined));
        result.GetProperty("error").GetString().Should().Contain("paused again while the question was open")
            .And.Contain("the user pressed the pause hotkey (Ctrl+Alt+Shift+Pause)");
        session.IsPaused.Should().BeTrue();
        flag.IsSet.Should().BeTrue("the other servers stay paused too");
    }

    #endregion

    #region A pause the user made, while the shared pause cannot be read

    // The user paused input with the hotkey another server registered, and then this server could not read the pause they
    // share (pausedBy stays hotkey): that server's presses no longer reach this one, so none resumes it, and after a resume
    // none pauses it. Only this server's own registration of the chord still works

    /// <summary>A session the user paused through the shared pause, which then could not be read.</summary>
    private InputSession UserPausedUnreadableSession(out FakeDesktopPauseFlag flag)
    {
        flag = new FakeDesktopPauseFlag();
        var session = new InputSession(_input, PauseHotkeyOptions.Default, _clock);
        session.ShareDesktopPause(flag);
        flag.SetByAnotherServer();
        session.IsPaused.Should().BeTrue();
        flag.ReadFails = new IOException("the handle broke");
        session.IsSharedPauseUnreadable.Should().BeTrue();
        session.PauseState.PausedBy.Should().Be(InputControlSources.Hotkey);
        return session;
    }

    [Theory]
    [InlineData(PauseHotkeyState.OtherServer, "another Windows App Manager server has registered Ctrl+Alt+Shift+Pause")]
    [InlineData(PauseHotkeyState.Failed, "this server has not registered it (Failed)")]
    public async Task GetControlStatus_AUsersPause_WhileTheSharedPauseCannotBeRead_SaysTheHotkeyCannotResume(PauseHotkeyState state, string why)
    {
        // Arrange
        using var session = UserPausedUnreadableSession(out var flag);
        _hotkey.Status = new PauseHotkeyStatus("Ctrl+Alt+Shift+Pause", state, "default");
        await using var server = await StartAsync(session: session);

        // Act
        var status = Payload(await server.CallToolAsync("get_control_status"));

        // Assert - and the other server's resume does not reach this one
        status.GetProperty("message").GetString().Should().Contain("by the user").And.NotContain("Ctrl+Alt+Shift+Pause again")
            .And.Contain("by confirming resume_input").And.Contain("The pause hotkey does not resume it").And.Contain(why);
        flag.ResetByAnotherServer();
        session.IsPaused.Should().BeTrue();
    }

    [Fact]
    public async Task GetControlStatus_AUsersPause_WhileTheSharedPauseCannotBeRead_ThisServerHoldsTheHotkey_SaysAPressResumes()
    {
        using var session = UserPausedUnreadableSession(out _);
        await using var server = await StartAsync(session: session);

        var status = Payload(await server.CallToolAsync("get_control_status"));

        status.GetProperty("message").GetString().Should().Contain("Ctrl+Alt+Shift+Pause again, or by confirming resume_input");
        _clock.Advance(InputSession.HotkeyResumeDelay);
        session.TogglePauseByHotkey();
        session.IsPaused.Should().BeFalse();
    }

    [Fact]
    public async Task ResumeInput_ClientWithoutElicitation_AUsersPause_WhileTheSharedPauseCannotBeRead_SaysTheHotkeyCannotResume()
    {
        using var session = UserPausedUnreadableSession(out _);
        _hotkey.Status = new PauseHotkeyStatus("Ctrl+Alt+Shift+Pause", PauseHotkeyState.OtherServer, "default", "Another server has it");
        await using var server = await StartAsync(session: session);

        var result = Payload(await server.CallToolAsync("resume_input"));

        result.GetProperty("errorCode").GetString().Should().Be(nameof(InputErrorCode.ResumeNeedsUser));
        result.GetProperty("error").GetString().Should().Contain("Input is paused: the user pressed the pause hotkey").And.NotContain("pressing")
            .And.Contain("The pause hotkey cannot resume it").And.Contain("which this server cannot read")
            .And.Contain("only restarting this server");
        session.IsPaused.Should().BeTrue();
    }

    [Theory]
    [InlineData(PauseHotkeyState.OtherServer, "After a resume, the pause hotkey (Ctrl+Alt+Shift+Pause) does not pause this server")]
    [InlineData(PauseHotkeyState.Registered, "After a resume, Ctrl+Alt+Shift+Pause still pauses this server, but while it cannot read")]
    public async Task ResumeInput_AUsersPause_WhileTheSharedPauseCannotBeRead_TheQuestionSaysWhatThePauseHotkeyStillStops(
        PauseHotkeyState state, string afterResume)
    {
        using var session = UserPausedUnreadableSession(out _);
        _hotkey.Status = new PauseHotkeyStatus("Ctrl+Alt+Shift+Pause", state, "default");
        await using var server = await StartAsync(answer: (_, _) => Answer("decline"), session: session);

        await server.CallToolAsync("resume_input");

        _question!.Message.Should().Contain("You paused it with Ctrl+Alt+Shift+Pause").And.NotContain("paused it itself")
            .And.Contain("This server cannot read the pause it shares with the other servers on this desktop now").And.Contain(afterResume);
        session.IsPaused.Should().BeTrue();
    }

    [Fact]
    public async Task AUsersPause_WhileTheSharedPauseCannotBeRead_InputAndWindowTools_NameResumeInput_NotTheHotkey()
    {
        using var session = UserPausedUnreadableSession(out _);
        await using var server = await StartAsync(session: session);

        var typed = Payload(await server.CallToolAsync("type_text", new Dictionary<string, object?> { ["text"] = "abc" }));
        var launched = await server.CallToolAsync("launch_applications_batch");

        typed.GetProperty("errorCode").GetString().Should().Be(nameof(InputErrorCode.PausedByUser));
        typed.GetProperty("error").GetString().Should().Contain("the user pressed the pause hotkey")
            .And.Contain("by confirming resume_input (get_control_status says").And.NotContain("with the hotkey");
        JsonDocument.Parse(launched.Content.OfType<TextContentBlock>().Single().Text).RootElement.GetProperty("error").GetString()
            .Should().Contain("by confirming resume_input (get_control_status says").And.NotContain("with the hotkey");
        _windowToolRuns.Should().Be(0);
        _input.Batches.Should().BeEmpty();
    }

    [Fact]
    public async Task AUsersPause_OnceTheSharedPauseCanBeReadAgain_NamesTheHotkeyAgain()
    {
        // The read works again: the user's own pause is resumed by the hotkey again, wherever it was registered
        using var session = UserPausedUnreadableSession(out var flag);
        flag.ReadFails = null;
        _hotkey.Status = new PauseHotkeyStatus("Ctrl+Alt+Shift+Pause", PauseHotkeyState.OtherServer, "default", "Another server has it");
        await using var server = await StartAsync(session: session);

        var status = Payload(await server.CallToolAsync("get_control_status"));
        var typed = Payload(await server.CallToolAsync("type_text", new Dictionary<string, object?> { ["text"] = "abc" }));

        status.GetProperty("message").GetString().Should().Contain("Ctrl+Alt+Shift+Pause again, or by confirming resume_input");
        typed.GetProperty("error").GetString().Should().Contain("with the hotkey or by confirming resume_input");
        flag.ResetByAnotherServer();
        session.IsPaused.Should().BeFalse();
    }

    [Theory]
    [InlineData(PauseHotkeyState.OtherServer, "this server cannot read the pause they share, so the hotkey does not pause this server")]
    [InlineData(PauseHotkeyState.Registered, "a pause made in another one does not reach it")]
    public async Task GetControlStatus_Running_WhileTheSharedPauseCannotBeRead_SaysWhatThePauseHotkeyStillStops(PauseHotkeyState state, string what)
    {
        // The user confirmed the resume of the server's own pause: running, but the shared pause still cannot be read
        using var session = SelfPausedSession();
        session.Resume(InputControlSources.ResumeInput).Should().BeTrue();
        _hotkey.Status = new PauseHotkeyStatus("Ctrl+Alt+Shift+Pause", state, "default");
        await using var server = await StartAsync(session: session);

        var status = Payload(await server.CallToolAsync("get_control_status"));

        status.GetProperty("paused").GetBoolean().Should().BeFalse();
        status.GetProperty("message").GetString().Should().Contain(what).And.NotContain("it pauses every server");
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
        result.GetProperty("error").GetString().Should().Contain("Input is paused: the user pressed the pause hotkey (Ctrl+Alt+Shift+Pause)");
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
        // Ctrl went down without a scan code: it goes up with the one it has on every keyboard
        _input.Batches[^1].Should().Equal(InputEvent.Key(VkControl, KeyUp, 0x1D));
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
