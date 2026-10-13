namespace WindowsAppManagerMcp.E2ETests.Scenarios;

/// <summary>
/// The input guard (roadmap M1.1, M1.5) against the fixture and the real desktop. The refusal scenarios run
/// in the no-input tier where they can: the guard refuses before anything is sent, and if it ever did not,
/// the harness's read-only policy would refuse the input instead of letting it reach the desktop. The
/// scenarios that must send input run in the input tier, where the harness lets input reach only the
/// fixture (see <see cref="FixtureOnlyCallPolicy"/>).
/// </summary>
public class FixtureInputGuardTests
{
    private const string Text = "wam guard 7";

    private static readonly TimeSpan SettleTimeout = TimeSpan.FromSeconds(3);

    private readonly ITestOutputHelper _output;

    public FixtureInputGuardTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [DesktopNoInputFact]
    public async Task ClickAndTypeText_TargetDisabledByTheModal_AreBlockedByModal_WithTheDialog()
    {
        // Arrange - ShowDialog disables the main window; the WPF dialog is owned through GW_OWNER only
        using var e2e = E2ESession.Start(DesktopTier.NoInput, _output, new FixtureLaunchOptions { OpenModal = true });
        var modal = e2e.Fixture.WaitForEvent(FixtureContract.Events.ModalOpened).GetHandle("hwnd");
        var main = e2e.Fixture.MainWindowHandle;
        var editBox = e2e.Fixture.Ready.Element(FixtureContract.Ids.EditBox);

        // Act
        var click = e2e.Server.Input.Click(editBox.CenterX, editBox.CenterY, InputTarget.Window(main), releaseHeldModifiers: false, CancellationToken.None);
        var typed = await e2e.Server.Input.TypeTextAsync(Text, 0, InputTarget.Window(main), releaseHeldModifiers: false, CancellationToken.None);
        _output.WriteLine($"click: {click.ErrorCode} {click.Error}");
        _output.WriteLine($"type_text: {typed.ErrorCode} {typed.Error}");

        // Assert - refused before any input or activation, naming the dialog
        click.ErrorCode.Should().Be(InputErrorCode.BlockedByModal, click.Error);
        click.BlockingWindow!.Handle.Should().Be(modal);
        click.ReceivingWindow!.Handle.Should().Be(main);
        typed.ErrorCode.Should().Be(InputErrorCode.BlockedByModal, typed.Error);
        typed.CharactersTyped.Should().Be(0);
        typed.BlockingWindow!.Handle.Should().Be(modal);
    }

    [DesktopNoInputFact]
    public async Task SendKeys_DestructiveChordWithoutWindowHandle_IsRefused_WhateverIsInFront()
    {
        // Arrange - the chord that closed the user's terminal in M0. It holds whatever is in front: the guard refuses the
        // request before it looks at a window, and if it did not, the host check or the harness's read-only policy would
        // fail the call with another result
        using var e2e = E2ESession.Start(DesktopTier.NoInput, _output);

        // Act
        var result = await e2e.Server.Input.SendKeysAsync("%{F4}", InputTarget.None, releaseHeldModifiers: false, CancellationToken.None);

        // Assert
        result.ErrorCode.Should().Be(InputErrorCode.DestructiveChordNeedsTarget, result.Error);
        _output.WriteLine("The chord would have gone to " + UiaReads.DescribeWindow(E2ENative.GetForegroundWindow()));
    }

    [DesktopNoInputFact]
    public async Task TypeText_WithoutWindowHandle_WhileTheAgentHostIsInFront_IsTargetIsAgentHost()
    {
        // Arrange - the terminal or IDE that runs the tests, in front: the host here. Anything else in front fails the test,
        // since the guard would then be checked against another window, or not at all
        using var e2e = E2ESession.Start(DesktopTier.NoInput, _output);
        var foreground = e2e.RequireAgentHostInFront();
        _output.WriteLine("In front: " + UiaReads.DescribeWindow(foreground));

        // Act
        var typed = await e2e.Server.Input.TypeTextAsync("exit", 0, InputTarget.None, releaseHeldModifiers: false, CancellationToken.None);

        // Assert
        typed.ErrorCode.Should().Be(InputErrorCode.TargetIsAgentHost, typed.Error);
        typed.ReceivingWindow!.Handle.Should().Be(foreground);
        typed.CharactersTyped.Should().Be(0);
    }

    [DesktopFact]
    public async Task TypeText_WithWindowHandle_BringsTheFixtureBackToTheFrontAndTypesIntoIt()
    {
        // Arrange - put the caret in the edit box, then bring the document window (another window of the
        // fixture) in front of it
        using var e2e = E2ESession.Start(DesktopTier.Input, _output, new FixtureLaunchOptions { OpenDocument = true });
        var document = e2e.Fixture.WaitForEvent(FixtureContract.Events.DocumentOpened).GetHandle("hwnd");
        var main = e2e.Fixture.MainWindowHandle;
        var processId = e2e.Fixture.ProcessId;

        var toMain = e2e.Server.Foreground.EnsureForeground(main);
        toMain.IsForeground.Should().BeTrue(toMain.Error);
        Wait.Until(() => UiaReads.FocusedAutomationId(processId) == FixtureContract.Ids.EditBox, SettleTimeout).Should().BeTrue(
            "keyboard focus should be on the fixture's edit box, but it is on '" + UiaReads.FocusedAutomationId(processId) + "'");
        var toDocument = e2e.Server.Foreground.EnsureForeground(document);
        toDocument.IsForeground.Should().BeTrue(toDocument.Error);
        var documentText = UiaReads.ReadValue(document, FixtureContract.Ids.DocumentTextBox);

        // Act - type_text names the main window: the guard brings it back to the front before typing
        var typed = await e2e.Server.Input.TypeTextAsync(Text, 0, InputTarget.Window(main), releaseHeldModifiers: false, CancellationToken.None);
        _output.WriteLine($"type_text: {typed.ErrorCode} {typed.Error}; receiving {typed.ReceivingWindow}; after {typed.ForegroundAfter}");

        // Assert - the server's result, the fixture's own record and both text boxes agree
        typed.Success.Should().BeTrue(typed.Error);
        typed.CharactersTyped.Should().Be(Text.Length);
        typed.ReceivingWindow!.Handle.Should().Be(main);
        typed.ForegroundAfter!.Handle.Should().Be(main);
        e2e.Fixture.WaitForEvent(
            FixtureContract.Events.TextChanged,
            e => e.GetString("id") == FixtureContract.Ids.EditBox && e.GetString("text") == Text);
        UiaReads.ReadValue(main, FixtureContract.Ids.EditBox).Should().Be(Text);
        UiaReads.ReadValue(document, FixtureContract.Ids.DocumentTextBox).Should().Be(documentText, "nothing was typed into the document");
    }

    [DesktopFact]
    public void Click_WithWindowHandle_AnotherFixtureWindowOverThePoint_IsTargetOccluded()
    {
        // Arrange - move the document window, another main window of the same process, over the check box
        using var e2e = E2ESession.Start(DesktopTier.Input, _output, new FixtureLaunchOptions { OpenDocument = true });
        var document = e2e.Fixture.WaitForEvent(FixtureContract.Events.DocumentOpened).GetHandle("hwnd");
        var main = e2e.Fixture.MainWindowHandle;
        var checkBox = e2e.Fixture.Ready.Element(FixtureContract.Ids.CheckBox);

        e2e.Server.Windows.SetWindowBounds(document, checkBox.CenterX - 150, checkBox.CenterY - 100, 300, 200, bringToFront: true)
            .Success.Should().BeTrue();
        var toDocument = e2e.Server.Foreground.EnsureForeground(document);
        toDocument.IsForeground.Should().BeTrue(toDocument.Error);
        var point = new E2ENative.POINT { X = checkBox.CenterX, Y = checkBox.CenterY };
        Wait.Until(() => E2ENative.GetAncestor(E2ENative.WindowFromPoint(point), E2ENative.GA_ROOT) == document, SettleTimeout)
            .Should().BeTrue("the document window should cover the check box, but the window there is " +
                             UiaReads.DescribeWindow(E2ENative.WindowFromPoint(point)));
        E2ENative.GetCursorPos(out var before);

        // Act
        var click = e2e.Server.Input.Click(checkBox.CenterX, checkBox.CenterY, InputTarget.Window(main), releaseHeldModifiers: false, CancellationToken.None);
        _output.WriteLine($"click: {click.ErrorCode} {click.Error}");

        // Assert - refused before the cursor moved
        click.ErrorCode.Should().Be(InputErrorCode.TargetOccluded, click.Error);
        click.ReceivingWindow!.Handle.Should().Be(document);
        (click.ActualX, click.ActualY).Should().Be((before.X, before.Y), "the cursor was not moved");
    }
}
