namespace WindowsAppManagerMcp.E2ETests.Scenarios;

/// <summary>
/// element_action (roadmap M3.6) against the fixture: the input tier, because pattern actions change the application. The
/// harness's guard lets the server act only on the fixture's elements (UiaAction calls on elements of the fixture process,
/// see <see cref="FixtureOnlyCallPolicy"/>), and the fixture's event log is the independent record of what it received.
/// Nothing is typed and the mouse is not used: every action here goes through a pattern.
/// </summary>
public class FixtureElementActionTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    private readonly ITestOutputHelper _output;

    public FixtureElementActionTests(ITestOutputHelper output)
    {
        _output = output;
    }

    private static async Task<string> RefOfAsync(E2ESession e2e, nint window, string automationId)
    {
        var found = await e2e.Server.Get<IElementSearchService>().FindAsync(new ElementQuery(
            window, AutomationId: automationId, VisibleOnly: false, Timeout: Timeout));
        found.Success.Should().BeTrue(found.Error);
        return found.Elements.Should().ContainSingle().Which.Ref!;
    }

    private Task<ElementActionResult> ActAsync(E2ESession e2e, string elementRef, ElementActionKind action, string? value = null)
    {
        _output.WriteLine($"element_action {action} on {elementRef}");
        return e2e.Server.Get<IElementActionService>().ActAsync(new ElementActionRequest(elementRef, action, value, Timeout: Timeout));
    }

    private void Log(ElementActionResult result) =>
        _output.WriteLine($"  success={result.Success} status={result.Status} method={result.Method} verified={result.OutcomeVerified} " +
                          $"before={result.Before} after={result.After} popup={result.OpenedPopup} error={result.ErrorCode}: {result.Error}");

    [DesktopFact]
    public async Task Toggle_TheCheckBoxToAState_ThroughItsPattern_IsVerified_AndTheFixtureSawIt()
    {
        // Arrange
        using var e2e = E2ESession.Start(DesktopTier.Input, _output);
        var checkBox = await RefOfAsync(e2e, e2e.Fixture.MainWindowHandle, FixtureContract.Ids.CheckBox);

        // Act
        var result = await ActAsync(e2e, checkBox, ElementActionKind.Toggle, "on");
        Log(result);

        // Assert
        result.Success.Should().BeTrue(result.Error);
        result.After!.ToggleState.Should().Be(UiaToggleState.On);
        result.OutcomeVerified.Should().BeTrue();
        if (result.Method != ElementActionMethod.None)
        {
            e2e.Fixture.WaitForEvent(FixtureContract.Events.CheckedChanged, e => e.GetBoolean("isChecked") == true);
        }
    }

    [DesktopFact]
    public async Task SetValue_OnTheEditBox_ThroughTheValuePattern_ReadsItBack()
    {
        // Arrange
        using var e2e = E2ESession.Start(DesktopTier.Input, _output);
        var edit = await RefOfAsync(e2e, e2e.Fixture.MainWindowHandle, FixtureContract.Ids.EditBox);

        // Act
        var result = await ActAsync(e2e, edit, ElementActionKind.SetValue, "set through the Value pattern");
        Log(result);

        // Assert
        result.Success.Should().BeTrue(result.Error);
        result.Method.Should().Be(ElementActionMethod.Value);
        result.OutcomeVerified.Should().BeTrue();
        e2e.Fixture.WaitForEvent(FixtureContract.Events.TextChanged,
            e => e.GetString("id") == FixtureContract.Ids.EditBox && e.GetString("text") == "set through the Value pattern");
    }

    [DesktopFact]
    public async Task Invoke_TheButtonThatOpensTheModal_ReportsTheDialog_ThenCancelClosesIt()
    {
        // Arrange
        using var e2e = E2ESession.Start(DesktopTier.Input, _output);
        var open = await RefOfAsync(e2e, e2e.Fixture.MainWindowHandle, FixtureContract.Ids.OpenModalButton);

        // Act - WPF invokes asynchronously, so the call returns (Done) and the dialog shows up meanwhile; a Win32 button
        // would block until the dialog closed and return InvokedPendingModal
        var opened = await ActAsync(e2e, open, ElementActionKind.Invoke);
        Log(opened);
        var modalOpened = e2e.Fixture.WaitForEvent(FixtureContract.Events.ModalOpened);

        // Assert
        opened.Success.Should().BeTrue(opened.Error);
        opened.Method.Should().Be(ElementActionMethod.Invoke);
        e2e.Fixture.WaitForEvent(FixtureContract.Events.ButtonClicked, e => e.GetString("id") == FixtureContract.Ids.OpenModalButton);
        var dialog = modalOpened.GetHandle("hwnd");
        _output.WriteLine($"The modal: 0x{dialog:X} (openedPopup {opened.OpenedPopup})");
        if (opened.OpenedPopup is { } popup)
        {
            popup.Handle.Should().Be(dialog, "the only new window of the fixture is its modal dialog");
        }

        // The main window is disabled now: a pattern action on it would act behind the dialog's back
        var refused = await ActAsync(e2e, open, ElementActionKind.Invoke);
        Log(refused);
        refused.ErrorCode.Should().Be("BlockedByModal");

        // Cancel through the dialog's own button
        var cancel = await RefOfAsync(e2e, dialog, FixtureContract.Ids.ModalCancelButton);
        var closed = await ActAsync(e2e, cancel, ElementActionKind.Invoke);
        Log(closed);
        closed.Success.Should().BeTrue(closed.Error);
        e2e.Fixture.WaitForEvent(FixtureContract.Events.ModalClosed, e => e.GetString("result") == "cancel");
    }
}
