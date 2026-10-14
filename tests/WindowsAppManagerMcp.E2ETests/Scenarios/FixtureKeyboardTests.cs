namespace WindowsAppManagerMcp.E2ETests.Scenarios;

/// <summary>
/// The keyboard engine against the fixture (roadmap M4.1): send_keys types every character with the key of the keyboard
/// layout of the fixture's thread, with Shift or AltGr and the key's scan code, and type_text presses Enter for a line
/// break only when asked. The full (input) tier: input goes only to the fixture (see <see cref="FixtureOnlyCallPolicy"/>),
/// and each test types nothing unless the fixture's text box has keyboard focus. The tests pass on any keyboard layout
/// and with Caps Lock on or off: each expects the text the keys stand for, whatever keys type it.
/// </summary>
public class FixtureKeyboardTests
{
    private static readonly TimeSpan SettleTimeout = TimeSpan.FromSeconds(3);

    private readonly ITestOutputHelper _output;

    public FixtureKeyboardTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [DesktopFact]
    public async Task SendKeys_TypesCapitalsAndPunctuation_AsWritten()
    {
        // Before M4.1 capitals came out small and punctuation went as Unicode characters
        using var e2e = StartOnTheEditBox(out var main);

        await SendAndExpect(e2e, main, ["Hello, World! a-b/c;d'e[f]g=h_i?j\"k<l>m"], "Hello, World! a-b/c;d'e[f]g=h_i?j\"k<l>m");
    }

    [DesktopFact]
    public async Task SendKeys_TypesAltGrCharacters_AndTheEscapedCharacters()
    {
        // On a Norwegian layout @ { } [ ] $ € need AltGr; ~ ^ ` ´ ¨ are dead keys there and go as Unicode, so the 'a' after
        // them stays an 'a'
        using var e2e = StartOnTheEditBox(out var main);

        await SendAndExpect(e2e, main, ["@{{}{}}[]\\$€|{~}{^}`´¨a{+}{%}{#}{(}{)}"], "@{}[]\\$€|~^`´¨a+%#()");
    }

    [DesktopFact]
    public async Task SendKeys_RepeatsMoveTheCaret_AndAHeldShiftSelects()
    {
        using var e2e = StartOnTheEditBox(out var main);

        await SendAndExpect(e2e, main, ["abcdef{LEFT 5}X"], "aXbcdef");
        await SendAndExpect(e2e, main, ["{HOME}{SHIFT down}{RIGHT 2}{SHIFT up}Z"], "Zbcdef");
    }

    [DesktopFact]
    public async Task SendKeys_ACtrlChord_AndPacedKeys_Arrive()
    {
        // Ctrl+A selects the old text, and the paced keys replace it one by one
        using var e2e = StartOnTheEditBox(out var main);

        await SendAndExpect(e2e, main, ["old", "^a"], "old");
        await SendAndExpect(e2e, main, ["new"], "new", delayMs: 40);
    }

    [DesktopFact]
    public async Task PressKeys_ChordsRepeatsAndAHeldChord_Arrive()
    {
        // roadmap M4.3: named keys only, so the test passes on any layout and with Caps Lock either way
        using var e2e = StartOnTheEditBox(out var main);
        await SendAndExpect(e2e, main, ["abcdef"], "abcdef");

        await PressAndExpect(e2e, main, KeyList.Of("home shift+right*2 delete"), "cdef");
        await PressAndExpect(e2e, main, KeyList.Of(["ctrl", "a"]), "cdef", holdMs: 150);
        await PressAndExpect(e2e, main, KeyList.Of("backspace"), "", delayBetweenMs: 30);
    }

    [DesktopFact]
    public async Task TypeText_WithControlCharsKeys_PressesEnter_InAMultiLineTextBox()
    {
        // Arrange - the document window's text box accepts Enter
        using var e2e = E2ESession.Start(DesktopTier.Input, _output, new FixtureLaunchOptions { OpenDocument = true });
        var document = e2e.Fixture.WaitForEvent(FixtureContract.Events.DocumentOpened).GetHandle("hwnd");
        var processId = e2e.Fixture.ProcessId;
        var toDocument = e2e.Server.Foreground.EnsureForeground(document);
        toDocument.IsForeground.Should().BeTrue(toDocument.Error);
        Wait.Until(() => UiaReads.FocusedAutomationId(processId) == FixtureContract.Ids.DocumentTextBox, SettleTimeout).Should().BeTrue(
            "keyboard focus should be on the document's text box, but it is on '" + UiaReads.FocusedAutomationId(processId) + "'. Nothing was typed");
        var before = UiaReads.ReadValue(document, FixtureContract.Ids.DocumentTextBox);

        // Act - to the end of the text, then a line break as Enter
        var end = await e2e.Server.Input.SendKeysAsync("^{END}", InputTarget.Window(document), releaseHeldModifiers: false, 0, CancellationToken.None);
        var typed = await e2e.Server.Input.TypeTextAsync("\nline two", 0, InputTarget.Window(document), releaseHeldModifiers: false,
            TextControlChars.Keys, TextInputMethod.Unicode, CancellationToken.None);
        _output.WriteLine($"send_keys: {end.ErrorCode} {end.Error}; type_text: {typed.ErrorCode} {typed.Error}");

        // Assert
        end.Success.Should().BeTrue(end.Error);
        typed.Success.Should().BeTrue(typed.Error);
        typed.CharactersTyped.Should().Be(9);
        var expected = before + "\r\nline two";
        e2e.Fixture.WaitForEvent(
            FixtureContract.Events.TextChanged,
            e => e.GetString("id") == FixtureContract.Ids.DocumentTextBox && e.GetString("text") == expected);
        UiaReads.ReadValue(document, FixtureContract.Ids.DocumentTextBox).Should().Be(expected);
    }

    [DesktopFact]
    public async Task TypeText_WithALineBreak_AndNoControlChars_TypesNothing()
    {
        using var e2e = StartOnTheEditBox(out var main);

        var typed = await e2e.Server.Input.TypeTextAsync("a\nb", 0, InputTarget.Window(main), releaseHeldModifiers: false, null, TextInputMethod.Unicode, CancellationToken.None);

        typed.ErrorCode.Should().Be(InputErrorCode.InvalidArguments, typed.Error);
        UiaReads.ReadValue(main, FixtureContract.Ids.EditBox).Should().BeEmpty();
    }

    [DesktopFact]
    public async Task TypeText_KeysMethod_TypesCapitalsPunctuationAndAltGrCharacters_WithTheLayoutsKeys()
    {
        // Roadmap M4.4: method keys types as send_keys does, for remote desktops that drop Unicode keystrokes; a character the
        // layout has no key for (the emoji) still goes as Unicode
        using var e2e = StartOnTheEditBox(out var main);
        const string Text = "Hello, World! a-b/c;d'e[f]g=h_i?j\"k<l>m@{}$|~^`+%#()\U0001F600";

        var typed = await e2e.Server.Input.TypeTextAsync(Text, 0, InputTarget.Window(main), releaseHeldModifiers: false, null,
            TextInputMethod.Keys, CancellationToken.None);
        _output.WriteLine($"type_text keys: {typed.ErrorCode} {typed.Error}; {typed.CharactersTyped} of {typed.Length}");

        typed.Success.Should().BeTrue(typed.Error);
        typed.Method.Should().Be("keys");
        typed.CharactersTyped.Should().Be(Text.Length - 1, "the emoji is one character");
        e2e.Fixture.WaitForEvent(
            FixtureContract.Events.TextChanged,
            e => e.GetString("id") == FixtureContract.Ids.EditBox && e.GetString("text") == Text);
        UiaReads.ReadValue(main, FixtureContract.Ids.EditBox).Should().Be(Text);
    }

    [DesktopFact]
    public async Task TypeText_TextLongerThanOneBatch_ArrivesWhole()
    {
        // 64 characters go in a batch, with the window checked again before each (roadmap M4.4)
        using var e2e = StartOnTheEditBox(out var main);
        var text = string.Concat(Enumerable.Range(0, 30).Select(i => $"word{i} "));

        var typed = await e2e.Server.Input.TypeTextAsync(text, 0, InputTarget.Window(main), releaseHeldModifiers: false, null,
            TextInputMethod.Unicode, CancellationToken.None);

        typed.Success.Should().BeTrue(typed.Error);
        typed.CharactersTyped.Should().Be(text.Length).And.BeGreaterThan(WindowsAppManagerMcp.Services.InputSimulationService.TypeTextBatchSize * 2);
        e2e.Fixture.WaitForEvent(
            FixtureContract.Events.TextChanged,
            e => e.GetString("id") == FixtureContract.Ids.EditBox && e.GetString("text") == text);
    }

    /// <summary>
    /// Sends each of <paramref name="calls"/> with send_keys to the fixture's main window, then checks that its edit box
    /// holds <paramref name="expected"/>, as the fixture recorded it and as UI Automation reads it.
    /// </summary>
    private async Task SendAndExpect(E2ESession e2e, nint main, string[] calls, string expected, int delayMs = 0)
    {
        foreach (var keys in calls)
        {
            var sent = await e2e.Server.Input.SendKeysAsync(keys, InputTarget.Window(main), releaseHeldModifiers: false, delayMs,
                CancellationToken.None);
            _output.WriteLine($"send_keys {keys}: {sent.ErrorCode} {sent.Error}; keysSent {sent.KeysSent}");
            sent.Success.Should().BeTrue(sent.Error);
        }

        e2e.Fixture.WaitForEvent(
            FixtureContract.Events.TextChanged,
            e => e.GetString("id") == FixtureContract.Ids.EditBox && e.GetString("text") == expected);
        UiaReads.ReadValue(main, FixtureContract.Ids.EditBox).Should().Be(expected);
    }

    /// <summary>Presses <paramref name="keys"/> with press_keys in the fixture's main window, then checks its edit box.</summary>
    private async Task PressAndExpect(E2ESession e2e, nint main, KeyList keys, string expected, int holdMs = 0, int delayBetweenMs = 0)
    {
        var pressed = await e2e.Server.Input.PressKeysAsync(keys, 1, holdMs, delayBetweenMs, InputTarget.Window(main), releaseHeldModifiers: false,
            CancellationToken.None);
        _output.WriteLine($"press_keys {keys.Text}: {pressed.ErrorCode} {pressed.Error}; keysSent {pressed.KeysSent}");
        pressed.Success.Should().BeTrue(pressed.Error);

        Wait.Until(() => UiaReads.ReadValue(main, FixtureContract.Ids.EditBox) == expected, SettleTimeout).Should().BeTrue(
            $"the edit box should hold '{expected}', but it holds '{UiaReads.ReadValue(main, FixtureContract.Ids.EditBox)}'");
    }

    /// <summary>Starts the fixture and brings it to the front with the caret in its (empty) edit box.</summary>
    private E2ESession StartOnTheEditBox(out nint main)
    {
        var e2e = E2ESession.Start(DesktopTier.Input, _output);
        try
        {
            main = e2e.Fixture.MainWindowHandle;
            var focus = e2e.Server.Foreground.EnsureForeground(main);
            focus.IsForeground.Should().BeTrue(focus.Error);
            var processId = e2e.Fixture.ProcessId;
            Wait.Until(() => UiaReads.FocusedAutomationId(processId) == FixtureContract.Ids.EditBox, SettleTimeout).Should().BeTrue(
                "keyboard focus should be on the fixture's edit box, but it is on '" + UiaReads.FocusedAutomationId(processId) +
                "'. Nothing was typed");
            return e2e;
        }
        catch
        {
            e2e.Dispose();
            throw;
        }
    }
}
