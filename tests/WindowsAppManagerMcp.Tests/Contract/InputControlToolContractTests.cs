using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using WindowsAppManagerMcp.Infrastructure;
using WindowsAppManagerMcp.Tools;

namespace WindowsAppManagerMcp.Tests.Contract;

/// <summary>
/// Contract: the input session through a real MCP client and server (roadmap M1.10): get_control_status,
/// resume_input (which resumes only after the user confirms through elicitation, and never without a client
/// that can ask), release_all_input, the request's cancellation and the user's pause stopping type_text, and
/// the release of held input when the client closes the server's stdin. The input layer and the hotkey are
/// fakes: nothing is sent or registered for real.
/// </summary>
public class InputControlToolContractTests
{
    private const uint KeyDown = 0x0000, KeyUp = 0x0002;
    private const ushort VkControl = 0x11, VkLControl = 0xA2;
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    private readonly FakeInputWrapper _input = new();
    private readonly InputSession _session;
    private readonly StubHotkey _hotkey = new();
    private int _questions;
    private ElicitRequestParams? _question;

    public InputControlToolContractTests()
    {
        _session = new InputSession(_input);
    }

    private Task<InProcessMcpServer> StartAsync(
        Func<ElicitRequestParams, CancellationToken, ValueTask<ElicitResult>>? answer = null,
        TimeSpan? answerTimeout = null,
        TaskCompletionSource<RequestId>? requestId = null)
    {
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
        var input = new InputSimulationService(monitors.Object, _input, TestDataFactory.CreateAllowingInputGuard().Object, _session);
        return InProcessMcpServer.StartAsync(
            [],
            builder => builder
                .AddCallToolFilter(next => (request, token) =>
                {
                    // The JSON-RPC id of the call, which a client names when it cancels the call
                    requestId?.TrySetResult(request.JsonRpcRequest.Id);
                    return next(request, token);
                })
                .AddToolErrorReporting()
                .WithTools(new InputControlTools(_session, _hotkey, answerTimeout))
                .WithTools(new InputSimulationTools(input)),
            clientOptions: options);
    }

    private static JsonElement Payload(CallToolResult result)
    {
        result.IsError.Should().NotBe(true);
        return JsonDocument.Parse(result.Content.OfType<TextContentBlock>().Single().Text).RootElement;
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

    [Fact]
    public async Task ResumeInput_ClientWithoutElicitation_IsResumeNeedsUser_AndStaysPaused()
    {
        // The agent cannot resume itself: with no way to ask the user, only the hotkey resumes
        _session.Pause(InputControlSources.Hotkey);
        await using var server = await StartAsync();

        var result = Payload(await server.CallToolAsync("resume_input"));

        result.GetProperty("success").GetBoolean().Should().BeFalse();
        result.GetProperty("errorCode").GetString().Should().Be(nameof(InputErrorCode.ResumeNeedsUser));
        result.GetProperty("error").GetString().Should().Contain("Ctrl+Alt+Shift+Pause");
        _session.IsPaused.Should().BeTrue();
    }

    [Fact]
    public async Task ResumeInput_TheUserConfirms_Resumes()
    {
        // Arrange
        _session.Pause(InputControlSources.Hotkey);
        await using var server = await StartAsync(answer: (_, _) => Answer("accept", true));

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

    [Fact]
    public async Task ResumeInput_TheUserDoesNotAnswer_StaysPaused()
    {
        _session.Pause(InputControlSources.Hotkey);
        await using var server = await StartAsync(
            answer: async (_, token) =>
            {
                await Task.Delay(Timeout, token);
                return new ElicitResult { Action = "accept" };
            },
            answerTimeout: TimeSpan.FromMilliseconds(100));

        var result = Payload(await server.CallToolAsync("resume_input"));

        result.GetProperty("errorCode").GetString().Should().Be(nameof(InputErrorCode.ResumeDeclined));
        result.GetProperty("error").GetString().Should().Contain("did not answer");
        _session.IsPaused.Should().BeTrue();
    }

    [Fact]
    public async Task ResumeInput_TheUserPressesTheHotkeyInstead_ReportsThatInputRuns()
    {
        _session.Pause(InputControlSources.Hotkey);
        await using var server = await StartAsync(answer: (_, _) =>
        {
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
        _input.Press(VkLControl);
        await using var server = await StartAsync();

        var result = Payload(await server.CallToolAsync("release_all_input",
            new Dictionary<string, object?> { ["releaseHeldModifiers"] = true }));

        result.GetProperty("success").GetBoolean().Should().BeTrue();
        result.GetProperty("released").EnumerateArray().Select(e => e.GetString()).Should().Equal("LeftCtrl");
        result.GetProperty("modifiersDown").GetArrayLength().Should().Be(0);
        _input.IsKeyDown(VkLControl).Should().BeFalse();
    }

    [Fact]
    public async Task ReleaseAllInput_WhilePaused_ReleasesTheServersKeysButNotTheUsers()
    {
        _session.Send([InputEvent.Key(0x41, KeyDown)]);
        _session.Pause(InputControlSources.Hotkey); // releases the server's A itself
        _session.Send([InputEvent.Key(0x42, KeyDown)]); // refused: paused
        _input.Press(VkLControl);
        await using var server = await StartAsync();

        var result = Payload(await server.CallToolAsync("release_all_input",
            new Dictionary<string, object?> { ["releaseHeldModifiers"] = true }));

        result.GetProperty("errorCode").GetString().Should().Be(nameof(InputErrorCode.PausedByUser));
        _input.IsKeyDown(VkLControl).Should().BeTrue();
        _input.IsKeyDown(0x41).Should().BeFalse();
    }

    #endregion

    #region Cancellation, pause and stdin EOF through MCP

    [Fact]
    public async Task TypeText_CancelledByTheClient_StopsTypingOnTheServer()
    {
        // Arrange - ten characters 100 ms apart, a second of typing
        var requestId = new TaskCompletionSource<RequestId>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var server = await StartAsync(requestId: requestId);
        using var clientGivesUp = new CancellationTokenSource();
        var call = server.CallToolAsync("type_text",
            new Dictionary<string, object?> { ["text"] = "abcdefghij", ["delayMs"] = 100 }, cancellationToken: clientGivesUp.Token);
        var id = await requestId.Task.WaitAsync(Timeout);
        SpinWait.SpinUntil(() => _input.Batches.Count >= 2, Timeout).Should().BeTrue();

        // Act - the client cancels the call. MCP clients send notifications/cancelled with the call's id; the C#
        // SDK client (0.5.0-preview.1) does not send it when its own token is cancelled, so the test sends it.
        await server.Client.SendNotificationAsync(NotificationMethods.CancelledNotification,
            new CancelledNotificationParams { RequestId = id, Reason = "the user cancelled" });

        // Assert - the server's request token stops typing between characters, and nothing stays down
        await Task.Delay(300);
        var afterCancel = _input.Batches.Count;
        await Task.Delay(300);
        _input.Batches.Count.Should().Be(afterCancel, "no character is typed after the cancellation has arrived");
        afterCancel.Should().BeLessThan(8, "typing stopped long before the end of the text");
        _session.ServerHeld.Should().BeEmpty();

        clientGivesUp.Cancel();
        await FluentActions.Awaiting(() => call).Should().ThrowAsync<Exception>("the cancelled call gets no result");
    }

    [Fact]
    public async Task TypeText_PausedByTheUserMidway_ReturnsPausedByUser()
    {
        // Arrange - the hotkey is pressed while the third character is being sent
        await using var server = await StartAsync();
        var pausing = Task.CompletedTask;
        _input.BeforeSend = _ =>
        {
            if (_input.Batches.Count == 2)
            {
                pausing = Task.Run(() => _session.Pause(InputControlSources.Hotkey));
            }
        };

        // Act
        var result = Payload(await server.CallToolAsync("type_text",
            new Dictionary<string, object?> { ["text"] = "abcdefghij", ["delayMs"] = 100 }));
        await pausing.WaitAsync(Timeout);

        // Assert
        result.GetProperty("success").GetBoolean().Should().BeFalse();
        result.GetProperty("errorCode").GetString().Should().Be(nameof(InputErrorCode.PausedByUser));
        result.GetProperty("charactersTyped").GetInt32().Should().Be(3);
    }

    [Fact]
    public async Task SendKeys_ThePauseChord_IsRefusedThroughMcp()
    {
        await using var server = await StartAsync();

        var result = Payload(await server.CallToolAsync("send_keys", new Dictionary<string, object?> { ["keys"] = "^%+{PAUSE}" }));

        result.GetProperty("errorCode").GetString().Should().Be(nameof(InputErrorCode.PauseChordNotAllowed));
        _input.Batches.Should().BeEmpty();
    }

    [Fact]
    public async Task ClosingTheServersStdin_ReleasesHeldInput()
    {
        // Arrange - the server as Program.cs runs it, with the session start-up service and a fake hotkey; a key
        // the server pressed is still down
        var native = new FakeHotkeyWrapper();
        await using var server = await InProcessMcpServer.StartAsync(
            [],
            configureServices: services => services
                .AddSingleton<IInputSession>(_session)
                .AddSingleton<IPauseHotkey>(new PauseHotkeyListener(native, PauseHotkeyOptions.Default))
                .AddHostedService<InputSessionStartup>());
        _session.Send([InputEvent.Key(VkControl, KeyDown)]);
        var stopping = server.Services.GetRequiredService<IHostApplicationLifetime>().ApplicationStopping;

        // Act - the client closes the server's input stream (stdin EOF)
        await server.CloseServerInputAsync();

        // Assert - the SDK ends the session and stops the application, which releases the key
        SpinWait.SpinUntil(() => stopping.IsCancellationRequested, Timeout).Should().BeTrue();
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
