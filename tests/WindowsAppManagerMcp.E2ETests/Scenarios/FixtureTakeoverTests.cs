using WindowsAppManagerMcp.Services;

namespace WindowsAppManagerMcp.E2ETests.Scenarios;

/// <summary>
/// Takeover detection against the fixture (roadmap M7.4), in the full (input) tier, with WAM_TAKEOVER_DETECTION=stop: Windows
/// installs the real low-level hooks while type_text runs, and the server's own typing, which Windows marks as injected,
/// never counts as the user's, so the text goes in whole. Run it while nobody uses the keyboard or mouse: the user's input
/// would stop the typing, which is the feature, and fail the test. Input goes only to the fixture
/// (<see cref="FixtureOnlyCallPolicy"/>).
/// </summary>
public class FixtureTakeoverTests
{
    private const string Text = "the quick brown fox jumps over the lazy dog";

    private static readonly TimeSpan SettleTimeout = TimeSpan.FromSeconds(3);

    private readonly ITestOutputHelper _output;

    public FixtureTakeoverTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [DesktopFact]
    public async Task ModeStop_TheServersOwnTyping_IsNotTheUsers_AndGoesInWhole()
    {
        // Arrange - the server reads the mode when it is built, as it does when it starts; the E2E tests run one at a time
        var before = Environment.GetEnvironmentVariable(TakeoverOptions.EnvironmentVariable);
        Environment.SetEnvironmentVariable(TakeoverOptions.EnvironmentVariable, "stop");
        try
        {
            using var e2e = E2ESession.Start(DesktopTier.Input, _output);
            var main = e2e.Fixture.MainWindowHandle;
            var processId = e2e.Fixture.ProcessId;
            var monitor = e2e.Server.Get<ITakeoverMonitor>();
            monitor.Mode.Should().Be(TakeoverMode.Stop);
            monitor.Status.State.Should().Be(TakeoverState.Ready, "no hook is installed before an operation runs");

            var focus = e2e.Server.Foreground.EnsureForeground(main);
            focus.IsForeground.Should().BeTrue(focus.Error);
            Wait.Until(() => UiaReads.FocusedAutomationId(processId) == FixtureContract.Ids.EditBox, SettleTimeout)
                .Should().BeTrue("keyboard focus should be on the fixture's edit box; nothing was typed");

            // Act - one character every 20 ms, so the hooks see each of the server's key presses
            var typing = e2e.Server.Input.TypeTextAsync(Text, 20, InputTarget.Window(main), releaseHeldModifiers: false, null,
                TextInputMethod.Unicode, CancellationToken.None);
            var watched = Wait.Until(() => monitor.Status.State == TakeoverState.Watching, SettleTimeout);
            var result = await typing.WaitAsync(TimeSpan.FromSeconds(30));
            _output.WriteLine($"Takeover status while typing: {(watched ? "Watching" : "not seen")}; after: {monitor.Status}.");

            // Assert - the hooks went in, the server's own input did not stop the typing, and the hooks went out again
            result.ErrorCode.Should().NotBe(InputErrorCode.UserInterrupted,
                "the server's own SendInput is injected and must never count as the user's (or someone used the keyboard or " +
                "mouse during the test: run it while the desktop is idle)");
            result.Success.Should().BeTrue(result.Error);
            result.CharactersTyped.Should().Be(Text.Length);
            watched.Should().BeTrue("Windows should install the low-level hooks while type_text runs");
            e2e.Fixture.WaitForEvent(
                FixtureContract.Events.TextChanged,
                e => e.GetString("id") == FixtureContract.Ids.EditBox && e.GetString("text") == Text);
            Wait.Until(() => monitor.Status.State == TakeoverState.Ready, SettleTimeout)
                .Should().BeTrue("the hooks are removed when the last watched operation ends");
        }
        finally
        {
            Environment.SetEnvironmentVariable(TakeoverOptions.EnvironmentVariable, before);
        }
    }
}
