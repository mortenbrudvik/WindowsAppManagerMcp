using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using WindowsAppManagerMcp.Infrastructure;
using WindowsAppManagerMcp.Tools;

namespace WindowsAppManagerMcp.Tests.Unit.Services;

/// <summary>
/// What the agent is told when the server paused itself because it could not read the pause it shares with the other
/// servers (pausedBy shared_pause_unreadable, review F4): the same PausedByUser and the same "stop and ask the user", but
/// never that the user pressed the pause hotkey, which they did not, so the agent does not tell the user something false.
/// The shared pause is <see cref="FakeDesktopPauseFlag"/>, the input layer <see cref="FakeInputWrapper"/>: nothing reaches
/// the desktop.
/// </summary>
public class SharedPauseUnreadableMessageTests : IDisposable
{
    private readonly FakeInputWrapper _input = new();
    private readonly InputSession _session;

    public SharedPauseUnreadableMessageTests()
    {
        _session = new InputSession(_input, PauseHotkeyOptions.Default);
        _session.ShareDesktopPause(new FakeDesktopPauseFlag { ReadFails = new IOException("the handle broke") });
    }

    public void Dispose() => _session.Dispose();

    [Fact]
    public void TheSessionPausedItself()
    {
        _session.PauseState.PausedBy.Should().Be(InputControlSources.SharedPauseUnreadable);
    }

    [Fact]
    public async Task AnInputCall_SaysTheServerPausedItself_NotThatTheUserPressedTheHotkey()
    {
        // Arrange
        var monitors = TestDataFactory.CreateMockMonitorService(
            [TestDataFactory.CreateMonitorInfo(boundsX: 0, boundsY: 0, boundsWidth: 1920, boundsHeight: 1080)]);
        var sut = new InputSimulationService(monitors.Object, _input, TestDataFactory.CreateAllowingInputGuard().Object, _session);

        // Act
        var result = await sut.TypeTextAsync("hello");

        // Assert
        result.ErrorCode.Should().Be(InputErrorCode.PausedByUser);
        result.Error.Should().Contain("could not read the pause it shares").And.Contain("paused itself")
            .And.Contain("Stop and ask the user").And.Contain("Do not try to resume").And.NotContain("pressed the pause hotkey");
        _input.Batches.Should().BeEmpty();
    }

    [Fact]
    public async Task AWindowChangingTool_SaysTheServerPausedItself_NotThatTheUserPressedTheHotkey()
    {
        // Arrange
        var ran = false;
        var handler = InputGateToolFilter.Filter((_, _) =>
        {
            ran = true;
            return ValueTask.FromResult(new CallToolResult());
        });
        var request = new RequestContext<CallToolRequestParams>(new Mock<McpServer>().Object,
            new JsonRpcRequest { Method = RequestMethods.ToolsCall }, new CallToolRequestParams { Name = "set_clipboard" })
        {
            Services = new ServiceCollection().AddSingleton<IInputSession>(_session).BuildServiceProvider()
        };

        // Act
        var result = await handler(request, CancellationToken.None);

        // Assert
        ran.Should().BeFalse();
        var payload = System.Text.Json.JsonDocument.Parse(result.Content.OfType<TextContentBlock>().Single().Text).RootElement;
        payload.GetProperty("errorCode").GetString().Should().Be("PausedByUser");
        payload.GetProperty("error").GetString().Should().Contain("could not read the pause it shares").And.Contain("Do not try to resume")
            .And.NotContain("pressed the pause hotkey");
    }

    [Fact]
    public void GetControlStatus_SaysTheServerPausedItself_NotTheUser()
    {
        var hotkey = new Mock<IPauseHotkey>();
        hotkey.Setup(h => h.Status).Returns(new PauseHotkeyStatus("Ctrl+Alt+Shift+Pause", PauseHotkeyState.Registered, "default"));

        var status = new InputControlTools(_session, hotkey.Object).GetControlStatus();

        status.Paused.Should().BeTrue();
        status.PausedBy.Should().Be(InputControlSources.SharedPauseUnreadable);
        status.Message.Should().Contain("by this server").And.Contain("could not read the pause it shares").And.NotContain("by the user")
            .And.Contain("Stop and ask the user");

        // The hotkey's first press only makes this pause the user's own (InputSession.TogglePauseByHotkey): "again" is wrong
        status.Message.Should().Contain("Ctrl+Alt+Shift+Pause twice").And.Contain("the first press makes the pause theirs")
            .And.NotContain("Ctrl+Alt+Shift+Pause again");
    }

    [Fact]
    public void GetControlStatus_AfterTheUsersPause_SaysTheHotkeyAgain_NotTwice()
    {
        using var session = new InputSession(_input, PauseHotkeyOptions.Default);
        session.Pause(InputControlSources.Hotkey);
        var hotkey = new Mock<IPauseHotkey>();
        hotkey.Setup(h => h.Status).Returns(new PauseHotkeyStatus("Ctrl+Alt+Shift+Pause", PauseHotkeyState.Registered, "default"));

        var status = new InputControlTools(session, hotkey.Object).GetControlStatus();

        status.Message.Should().Contain("Only the user can resume: Ctrl+Alt+Shift+Pause again, or by confirming resume_input")
            .And.NotContain("twice");
    }

    [Fact]
    public void ReleaseAllInput_WithReleaseHeldModifiers_SaysTheServerPausedItself_NotTheUser()
    {
        // A stuck Windows key: not one of the pause hotkey's modifiers, so only the pause leaves it down
        _input.Press(0x5B);

        var result = new InputControlTools(_session, new Mock<IPauseHotkey>().Object).ReleaseAllInput(releaseHeldModifiers: true);

        result.ErrorCode.Should().Be(InputErrorCode.PausedByUser);
        result.Error.Should().Contain("Input is paused").And.Contain("could not read the pause it shares")
            .And.NotContain("by the user").And.NotContain("pressed the pause hotkey").And.Contain("left alone");
        _input.IsKeyDown(0x5B).Should().BeTrue();
    }

    [Fact]
    public void APauseTheUserMade_StillSaysTheyPressedTheHotkey()
    {
        InputControlSources.PauseCause(InputControlSources.Hotkey, "Ctrl+Alt+Shift+Pause")
            .Should().Be("the user pressed the pause hotkey (Ctrl+Alt+Shift+Pause)");
        InputControlSources.PauseCause(InputControlSources.Hotkey, null).Should().Be("the user pressed the pause hotkey");
    }
}
