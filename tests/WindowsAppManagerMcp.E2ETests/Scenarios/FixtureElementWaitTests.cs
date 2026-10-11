namespace WindowsAppManagerMcp.E2ETests.Scenarios;

/// <summary>
/// wait_for_element's waits (roadmap M3.8) against the fixture: the window that the fixture opens by itself is waited for
/// first and its control found, a control that is there gives the ref find_ui_elements gives, and a control that is not
/// there is gone at once. No-input tier: the fixture opens its document window by itself, and the server only reads.
/// </summary>
public class FixtureElementWaitTests
{
    private readonly ITestOutputHelper _output;

    public FixtureElementWaitTests(ITestOutputHelper output)
    {
        _output = output;
    }

    private static ElementWaitRequest Request(
        WindowSelector window, string automationId, ElementWaitCondition condition = ElementWaitCondition.Exists, string? expected = null) => new()
    {
        Window = window,
        Criteria = new ElementQuery(nint.Zero, AutomationId: automationId),
        Condition = condition,
        Expected = expected,
        TimeoutMs = (int)E2ESession.FindWindowTimeout.TotalMilliseconds
    };

    [DesktopNoInputFact]
    public async Task Exists_InTheDocumentWindowTheFixtureOpens_WaitsForTheWindowFirst()
    {
        // Arrange - the document window opens on its own after the start
        using var e2e = E2ESession.Start(DesktopTier.NoInput, _output, new FixtureLaunchOptions { OpenDocument = true });
        var window = new WindowSelector { ProcessId = e2e.Fixture.ProcessId, TitleContains = FixtureContract.DocumentTitlePrefix };

        // Act
        var result = await e2e.Server.Get<IElementWaitService>().WaitAsync(Request(window, FixtureContract.Ids.DocumentSaveButton));

        // Assert
        _output.WriteLine($"{result.ElapsedMs} ms, {result.Polls} checks: {result.Element?.Ref} {result.Element?.ControlType} '{result.Element?.Name}'");
        result.Satisfied.Should().BeTrue(result.Error);
        result.WindowHandle.Should().Be(e2e.Fixture.WaitForEvent(FixtureContract.Events.DocumentOpened).GetHandle("hwnd"));
        result.Element!.Ref.Should().NotBeNull();
        result.Element.ControlType.Should().Be("Button");
    }

    [DesktopNoInputFact]
    public async Task Exists_AndEnabled_GiveTheRefFindUiElementsGives()
    {
        // Arrange
        using var e2e = E2ESession.Start(DesktopTier.NoInput, _output);
        var window = new WindowSelector { Handle = e2e.Fixture.MainWindowHandle, ProcessId = e2e.Fixture.ProcessId };
        var waits = e2e.Server.Get<IElementWaitService>();

        // Act
        var exists = await waits.WaitAsync(Request(window, FixtureContract.Ids.OpenModalButton));
        var enabled = await waits.WaitAsync(new ElementWaitRequest
        {
            Ref = exists.Element?.Ref, Condition = ElementWaitCondition.Enabled, TimeoutMs = 5000
        });
        var found = await e2e.Server.Get<IElementSearchService>().FindAsync(new ElementQuery(
            e2e.Fixture.MainWindowHandle, AutomationId: FixtureContract.Ids.OpenModalButton, Timeout: TimeSpan.FromSeconds(10)));

        // Assert
        exists.Satisfied.Should().BeTrue(exists.Error);
        exists.Polls.Should().Be(1, "the button is there from the start");
        enabled.Satisfied.Should().BeTrue(enabled.Error);
        found.Elements.Should().ContainSingle().Which.Ref.Should().Be(exists.Element!.Ref);
    }

    [DesktopNoInputFact]
    public async Task Gone_AControlThatIsNotThere_IsGoneAtTheFirstCheck_AndAValueThatDoesNotCome_TimesOut()
    {
        // Arrange
        using var e2e = E2ESession.Start(DesktopTier.NoInput, _output);
        var window = new WindowSelector { Handle = e2e.Fixture.MainWindowHandle, ProcessId = e2e.Fixture.ProcessId };
        var waits = e2e.Server.Get<IElementWaitService>();

        // Act
        var gone = await waits.WaitAsync(Request(window, "noSuchControl", ElementWaitCondition.Gone));
        var value = await waits.WaitAsync(Request(window, FixtureContract.Ids.EditBox, ElementWaitCondition.ValueEquals,
            "a value nobody typed") with { TimeoutMs = 600 });

        // Assert
        gone.Satisfied.Should().BeTrue(gone.Error);
        gone.Polls.Should().Be(1);
        _output.WriteLine($"value: {value.LastObservedState}");
        value.TimedOut.Should().BeTrue();
        value.ErrorCode.Should().Be("WaitTimedOut");
        value.LastObservedState.Should().Contain("not the expected one");
    }
}
