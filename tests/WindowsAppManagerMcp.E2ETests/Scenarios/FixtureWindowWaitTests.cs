namespace WindowsAppManagerMcp.E2ETests.Scenarios;

/// <summary>
/// wait_for_window's waits (roadmap M3.7) against the fixture, compared with what the fixture reports about itself.
/// No-input tier: the fixture opens its document window by itself, and the server only reads windows.
/// </summary>
public class FixtureWindowWaitTests
{
    private readonly ITestOutputHelper _output;

    public FixtureWindowWaitTests(ITestOutputHelper output)
    {
        _output = output;
    }

    private static WindowConditionRequest Request(WindowSelector window, WindowWaitCondition condition = WindowWaitCondition.Exists,
        IReadOnlyCollection<nint>? excludeHandles = null) => new()
    {
        Window = window,
        Condition = condition,
        ExcludeHandles = excludeHandles ?? [],
        TimeoutMs = (int)E2ESession.FindWindowTimeout.TotalMilliseconds
    };

    [DesktopNoInputFact]
    public async Task Exists_ExcludingTheMainWindow_FindsTheDocumentWindowTheFixtureOpens()
    {
        // Arrange - the main window was there before; the document window is the new one
        using var e2e = E2ESession.Start(DesktopTier.NoInput, _output, new FixtureLaunchOptions { OpenDocument = true });
        var waits = e2e.Server.Get<IWindowWaitService>();

        // Act
        var result = await waits.WaitForConditionAsync(Request(
            new WindowSelector { ProcessId = e2e.Fixture.ProcessId }, excludeHandles: [e2e.Fixture.MainWindowHandle]));

        // Assert
        var document = e2e.Fixture.WaitForEvent(FixtureContract.Events.DocumentOpened).GetHandle("hwnd");
        result.Satisfied.Should().BeTrue(result.Error);
        result.Window!.Handle.Should().Be(document);
        result.Window.Title.Should().Be(FixtureContract.DocumentTitle(e2e.Fixture.Instance, dirty: true));
    }

    [DesktopNoInputFact]
    public async Task Visible_TheFixtureMainWindow_IsOnScreen()
    {
        // Arrange
        using var e2e = E2ESession.Start(DesktopTier.NoInput, _output);

        // Act
        var result = await e2e.Server.Get<IWindowWaitService>().WaitForConditionAsync(Request(
            new WindowSelector { Handle = e2e.Fixture.MainWindowHandle, ProcessId = e2e.Fixture.ProcessId }, WindowWaitCondition.Visible));

        // Assert
        result.Satisfied.Should().BeTrue(result.Error);
        result.Window!.Handle.Should().Be(e2e.Fixture.MainWindowHandle);
    }

    [DesktopNoInputFact]
    public async Task Gone_AWindowThatDoesNotExist_IsGone_WithAWarning()
    {
        // Arrange
        using var e2e = E2ESession.Start(DesktopTier.NoInput, _output);

        // Act
        var result = await e2e.Server.Get<IWindowWaitService>().WaitForConditionAsync(Request(
            new WindowSelector { TitleEquals = FixtureContract.MainTitle(e2e.Fixture.Instance) + " (no such window)" }, WindowWaitCondition.Gone));

        // Assert
        result.Satisfied.Should().BeTrue();
        result.Warning.Should().Contain("No window matched");
    }
}
