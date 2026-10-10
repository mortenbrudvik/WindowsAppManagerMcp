namespace WindowsAppManagerMcp.E2ETests.Scenarios;

/// <summary>
/// Verified activation (EnsureForeground, roadmap M1.3) against the fixture: the full (input) tier. The
/// fixture starts without activation, so another window, usually the terminal that runs the tests, is in
/// front, and the server has to get past the foreground lock. The harness's guard lets the server act only
/// on the fixture: window calls on fixture windows, keys (the Esc that closes a menu) only while the
/// fixture is in front, and for the lock workaround only an attach of the server's own thread and a
/// zero-distance mouse move (see <see cref="FixtureOnlyCallPolicy"/>). Nothing is typed.
/// </summary>
public class FixtureForegroundTests
{
    private readonly ITestOutputHelper _output;

    public FixtureForegroundTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [DesktopFact]
    public void EnsureForeground_BringsTheFixtureToTheFront_AndReportsWhatIsInFront()
    {
        // Arrange
        using var e2e = E2ESession.Start(DesktopTier.Input, _output);
        var main = e2e.Fixture.MainWindowHandle;
        _output.WriteLine("In front before: " + UiaReads.DescribeWindow(E2ENative.GetForegroundWindow()));

        // Act
        var outcome = e2e.Server.Foreground.EnsureForeground(main);
        _output.WriteLine(
            $"EnsureForeground: isForeground={outcome.IsForeground}, method={outcome.Method}, " +
            $"foreground=0x{outcome.ForegroundHandle:X} '{outcome.ForegroundTitle}' ({outcome.ForegroundProcess}), " +
            $"errorCode={outcome.ErrorCode}, error={outcome.Error}");

        // Assert - the server's report
        outcome.IsForeground.Should().BeTrue(outcome.Error);
        outcome.ErrorCode.Should().Be(WindowErrorCode.None);
        outcome.Method.Should().BeOneOf(
            FocusMethod.Already, FocusMethod.SetForeground, FocusMethod.AttachThreadInput, FocusMethod.InputUnlock);
        outcome.ForegroundHandle.Should().Be(main);
        outcome.ForegroundProcessId.Should().Be(e2e.Fixture.ProcessId);
        outcome.BlockingPopup.Should().BeNull("the fixture has no dialog open");
        outcome.MenuClosed.Should().BeFalse("nothing put the fixture in menu mode");

        // ... agrees with the desktop and with the fixture's own record: its edit box got keyboard focus
        E2ENative.GetAncestor(E2ENative.GetForegroundWindow(), E2ENative.GA_ROOTOWNER).Should().Be(main);
        e2e.Fixture.WaitForEvent(FixtureContract.Events.KeyboardFocus, e => e.GetString("id") == FixtureContract.Ids.EditBox);

        // A second call finds the fixture in front and changes nothing
        var again = e2e.Server.Foreground.EnsureForeground(main);
        again.IsForeground.Should().BeTrue(again.Error);
        again.Method.Should().Be(FocusMethod.Already);
    }
}
