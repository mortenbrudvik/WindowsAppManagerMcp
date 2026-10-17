namespace WindowsAppManagerMcp.E2ETests.Scenarios;

/// <summary>
/// The window event journal against the fixture (roadmap M7.2), in the full (input) tier because it minimizes and restores
/// the fixture's window: over the real WinEvent hooks, the journal records the fixture's window being minimized and restored,
/// with its title, class and process, and nothing of the server's own process. Only the fixture's window is changed
/// (<see cref="FixtureOnlyCallPolicy"/>).
/// </summary>
public class FixtureWindowEventTests
{
    private static readonly TimeSpan SettleTimeout = TimeSpan.FromSeconds(5);

    private readonly ITestOutputHelper _output;

    public FixtureWindowEventTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [DesktopFact]
    public void MinimizingAndRestoringTheFixture_IsRecorded_WithTheWindowsTitleClassAndProcess()
    {
        // Arrange - the first read starts the recording
        using var e2e = E2ESession.Start(DesktopTier.Input, _output);
        var main = e2e.Fixture.MainWindowHandle;
        var journal = e2e.Server.Get<IWindowEventJournal>();
        var start = journal.Read(null, 50);
        start.StartedNow.Should().BeTrue();

        // Act
        var windows = e2e.Server.Windows;
        windows.SetWindowState(main, WindowStateRequest.Minimize).Success.Should().BeTrue();
        windows.SetWindowState(main, WindowStateRequest.Restore).Success.Should().BeTrue();

        // Assert - the worker records them a moment later
        List<WindowEventEntry> fixtureEvents = [];
        Wait.Until(() =>
        {
            fixtureEvents = journal.Read(start.NextSeq, 200).Events.Where(e => e.Handle == main).ToList();
            return fixtureEvents.Any(e => e.Type == WindowEventType.Minimized) && fixtureEvents.Any(e => e.Type == WindowEventType.Restored);
        }, SettleTimeout).Should().BeTrue("the journal records the fixture's minimize and restore; it saw: {0}",
            string.Join(", ", fixtureEvents.Select(e => $"{e.Seq} {e.Type}")));

        foreach (var entry in fixtureEvents)
        {
            _output.WriteLine($"{entry.Seq} {entry.At:HH:mm:ss.fff} {entry.Type} '{entry.Title}' {entry.ClassName} {entry.ProcessName}");
        }

        var minimized = fixtureEvents.First(e => e.Type == WindowEventType.Minimized);
        minimized.ProcessId.Should().Be(e2e.Fixture.ProcessId);
        minimized.Title.Should().Be(e2e.MainWindow.Title);
        minimized.ClassName.Should().Be(e2e.MainWindow.ClassName);
        minimized.Seq.Should().BeLessThan(fixtureEvents.Last(e => e.Type == WindowEventType.Restored).Seq);
        journal.Read(start.NextSeq, 200).Events.Should().NotContain(e => e.ProcessId == Environment.ProcessId,
            "the hooks skip the server's own process");
    }
}
