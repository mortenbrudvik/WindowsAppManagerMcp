namespace WindowsAppManagerMcp.E2ETests.Scenarios;

/// <summary>
/// The user's pause against the fixture (roadmap M1.10), in the full (input) tier: a pause stops real typing
/// between characters, charactersTyped matches what the fixture received, no key stays down, and typing works
/// again after the resume. The pause is made through the input session, as the hotkey's handler does; no real
/// hotkey is registered or pressed. Input goes only to the fixture (<see cref="FixtureOnlyCallPolicy"/>).
/// </summary>
public class FixtureSessionTests
{
    private const string Text = "abcdefghijklmnopqrstuvwxyz";
    private const ushort VkLShift = 0xA0, VkLControl = 0xA2, VkLMenu = 0xA4;

    private static readonly TimeSpan SettleTimeout = TimeSpan.FromSeconds(3);

    private readonly ITestOutputHelper _output;

    public FixtureSessionTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [DesktopFact]
    public async Task TypeText_PausedMidway_StopsWithWhatWasTyped_AndResumes()
    {
        // Arrange - the fixture's edit box has keyboard focus
        using var e2e = E2ESession.Start(DesktopTier.Input, _output);
        var main = e2e.Fixture.MainWindowHandle;
        var processId = e2e.Fixture.ProcessId;
        var session = e2e.Server.Get<IInputSession>();
        var focus = e2e.Server.Foreground.EnsureForeground(main);
        focus.IsForeground.Should().BeTrue(focus.Error);
        Wait.Until(() => UiaReads.FocusedAutomationId(processId) == FixtureContract.Ids.EditBox, SettleTimeout)
            .Should().BeTrue("keyboard focus should be on the fixture's edit box; nothing was typed");

        // Act - one character every 50 ms; the user pauses once a few have arrived
        var typing = e2e.Server.Input.TypeTextAsync(Text, 50, InputTarget.Window(main), releaseHeldModifiers: false, CancellationToken.None);
        Wait.Until(() => (UiaReads.ReadValue(main, FixtureContract.Ids.EditBox) ?? string.Empty).Length >= 5, SettleTimeout)
            .Should().BeTrue("typing should have started");
        session.Pause(InputControlSources.Hotkey);
        var paused = await typing.WaitAsync(SettleTimeout);

        // Assert - the call stopped with what it typed, and the fixture got exactly that much
        paused.ErrorCode.Should().Be(InputErrorCode.PausedByUser, paused.Error);
        paused.CharactersTyped.Should().BeInRange(5, Text.Length - 1);
        var expected = Text[..paused.CharactersTyped];
        e2e.Fixture.WaitForEvent(
            FixtureContract.Events.TextChanged,
            e => e.GetString("id") == FixtureContract.Ids.EditBox && e.GetString("text") == expected);
        UiaReads.ReadValue(main, FixtureContract.Ids.EditBox).Should().Be(expected);
        session.ServerHeld.Should().BeEmpty();
        var input = e2e.Server.Get<INativeInputWrapper>();
        new[] { VkLShift, VkLControl, VkLMenu }.Where(input.IsKeyDown).Should().BeEmpty("no modifier may be left down");

        // While paused nothing is sent
        var refused = await e2e.Server.Input.SendKeysAsync("x", InputTarget.Window(main), releaseHeldModifiers: false, CancellationToken.None);
        refused.ErrorCode.Should().Be(InputErrorCode.PausedByUser);

        // After the resume, typing works again
        session.Resume(InputControlSources.Hotkey);
        var rest = await e2e.Server.Input.TypeTextAsync(Text[paused.CharactersTyped..], 0, InputTarget.Window(main),
            releaseHeldModifiers: false, CancellationToken.None);
        rest.Success.Should().BeTrue(rest.Error);
        e2e.Fixture.WaitForEvent(
            FixtureContract.Events.TextChanged,
            e => e.GetString("id") == FixtureContract.Ids.EditBox && e.GetString("text") == Text);
    }
}
