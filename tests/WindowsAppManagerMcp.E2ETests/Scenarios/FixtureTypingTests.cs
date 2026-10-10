namespace WindowsAppManagerMcp.E2ETests.Scenarios;

/// <summary>
/// focus_window and type_text against the fixture: the full (input) tier. Input goes only to the
/// fixture: the harness's guard refuses any input batch while a window of another process is in front
/// (see <see cref="FixtureOnlyCallPolicy"/>), and the test types nothing unless the fixture's edit box
/// has keyboard focus. The text has no control characters.
/// </summary>
public class FixtureTypingTests
{
    private const string Text = "wam e2e 42";

    private static readonly TimeSpan SettleTimeout = TimeSpan.FromSeconds(3);

    private readonly ITestOutputHelper _output;

    public FixtureTypingTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [DesktopFact]
    public void FocusWindow_ThenTypeText_TypesIntoTheFixtureEditBox()
    {
        // Arrange - the fixture starts without activation; the server has to bring it to the front
        using var e2e = E2ESession.Start(DesktopTier.Input, _output);
        var main = e2e.Fixture.MainWindowHandle;
        var processId = e2e.Fixture.ProcessId;

        // Act 1 - focus_window (verified activation)
        var focus = e2e.Server.Foreground.EnsureForeground(main);
        var inFront = Wait.Until(() => ForegroundRootOwner() == main, SettleTimeout);

        // Assert 1 - otherwise stop here, before anything is typed
        inFront.Should().BeTrue(
            $"focus_window reported isForeground={focus.IsForeground} (method {focus.Method}, {focus.ErrorCode}: {focus.Error}), " +
            "but the foreground window is " + UiaReads.DescribeWindow(E2ENative.GetForegroundWindow()) + ". Nothing was typed");
        focus.IsForeground.Should().BeTrue(focus.Error);
        var onEditBox = Wait.Until(() => UiaReads.FocusedAutomationId(processId) == FixtureContract.Ids.EditBox, SettleTimeout);
        onEditBox.Should().BeTrue(
            "keyboard focus should be on the fixture's edit box, but it is on '" +
            UiaReads.FocusedAutomationId(processId) + "'. Nothing was typed");

        // Act 2 - type_text without a target; the input guard checks the foreground window before every batch
        var typed = e2e.Server.Input.TypeText(Text, 0, InputTarget.None);

        // Assert 2 - the server's result, the fixture's own record and the edit box all agree
        typed.Success.Should().BeTrue(typed.Error);
        typed.CharactersTyped.Should().Be(Text.Length);
        e2e.Fixture.WaitForEvent(
            FixtureContract.Events.TextChanged,
            e => e.GetString("id") == FixtureContract.Ids.EditBox && e.GetString("text") == Text);
        UiaReads.ReadValue(main, FixtureContract.Ids.EditBox).Should().Be(Text);
    }

    private static nint ForegroundRootOwner() =>
        E2ENative.GetAncestor(E2ENative.GetForegroundWindow(), E2ENative.GA_ROOTOWNER);
}
