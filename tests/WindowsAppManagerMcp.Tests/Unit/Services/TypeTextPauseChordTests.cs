namespace WindowsAppManagerMcp.Tests.Unit.Services;

/// <summary>
/// type_text never presses the user's pause hotkey, as send_keys and press_keys never do (PauseChordNotAllowed): the
/// agent must not pause itself. The hotkey reads the keys, not the character, so with a pause chord of Ctrl+Alt+Q,
/// method keys typing '@' on a German keyboard (AltGr+Q, the left Ctrl and the right Alt) would press it; and with a pause
/// chord of Ctrl+V, a paste would. The pause chord comes from <see cref="PauseHotkeyOptions.FromEnvironment"/> with a
/// test's own reader (no environment variable is read), the input is <see cref="FakeInputWrapper"/>, the layouts
/// <see cref="FakeKeyboardLayoutWrapper"/> and the clipboard <see cref="FakeClipboardWrapper"/>, so nothing reaches the
/// desktop, the real hotkey or the user's clipboard.
/// </summary>
public class TypeTextPauseChordTests
{
    private static readonly nint Window = 0x5150;
    private static readonly WindowSummary Notepad = new(Window, "Untitled - Notepad", "notepad", 5150);

    private readonly FakeInputWrapper _input = new();
    private readonly FakeKeyboardLayoutWrapper _layouts = new() { Layout = FakeKeyboardLayoutWrapper.German };
    private readonly FakeClipboardWrapper _clipboard = new();
    private readonly ClipboardOrigin _origin;
    private readonly Mock<INativeWindowWrapper> _windows = new();
    private readonly Mock<IInputTargetGuard> _guard = TestDataFactory.CreateAllowingInputGuard(Notepad);

    public TypeTextPauseChordTests()
    {
        _origin = new ClipboardOrigin(_clipboard, new ManualClock(), Timeout.InfiniteTimeSpan);
        _windows.Setup(w => w.GetClassName(Window)).Returns("Notepad");
        _windows.Setup(w => w.GetWindowThreadProcessId(Window)).Returns(77u);
        _windows.Setup(w => w.GetProcessName(77u)).Returns("notepad");
    }

    private InputSimulationService CreateSut(string pauseChord) =>
        new(TestDataFactory.CreateMockMonitorService().Object, _input, _guard.Object,
            new InputSession(_input, PauseHotkeyOptions.FromEnvironment(_ => pauseChord)),
            clipboardOrigin: _origin,
            pasteGuard: new ClipboardPasteGuard(_clipboard, _origin, ClipboardOptions.Default, _windows.Object),
            keyboardLayout: _layouts,
            pasteClipboard: new PasteClipboard(_clipboard, _origin, _windows.Object),
            pasteSettleTime: TimeSpan.Zero);

    private Task<TypeTextResult> Type(string pauseChord, string text, TextInputMethod method, int delayMs = 0) =>
        CreateSut(pauseChord).TypeTextAsync(text, delayMs, InputTarget.None, releaseHeldModifiers: false, controlChars: null, method,
            CancellationToken.None);

    private void VerifyRefusedBeforeAnythingWasSent(TypeTextResult result, string chord)
    {
        result.Success.Should().BeFalse();
        result.ErrorCode.Should().Be(InputErrorCode.PauseChordNotAllowed);
        result.Error.Should().Contain(chord).And.Contain("pause hotkey").And.Contain("Nothing was typed");
        result.CharactersTyped.Should().Be(0);
        _input.Batches.Should().BeEmpty();
        _clipboard.Writes.Should().Be(0, "the clipboard is not even lent");
        _guard.Verify(g => g.CheckKeyboard(It.IsAny<InputTarget>(), It.IsAny<KeyboardInputKind>(), It.IsAny<IReadOnlyList<InputEvent>?>()),
            Times.Never);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)] // one character per batch
    public async Task Keys_AnAltGrCharacterOnThePauseChordsKey_IsRefused_AndNothingIsTyped(int delayMs)
    {
        // '@' is AltGr+Q on a German keyboard: Ctrl+Alt+Q to the hotkey
        var result = await Type("Ctrl+Alt+Q", "user@example.de", TextInputMethod.Keys, delayMs);

        VerifyRefusedBeforeAnythingWasSent(result, "Ctrl+Alt+Q");
        result.Method.Should().Be("keys");
        result.Error.Should().Contain("method 'unicode'");
    }

    [Fact]
    public async Task Keys_TextThatDoesNotPressThePauseChord_IsTyped()
    {
        // AltGr+E types '€' on a German keyboard, which the text does not have
        var result = await Type("Ctrl+Alt+E", "user@example.de", TextInputMethod.Keys);

        result.Success.Should().BeTrue(result.Error);
        result.CharactersTyped.Should().Be(15);
    }

    [Fact]
    public async Task Unicode_TheSameText_IsTyped()
    {
        // A Unicode character presses no key the hotkey reads
        var result = await Type("Ctrl+Alt+Q", "user@example.de", TextInputMethod.Unicode);

        result.Success.Should().BeTrue(result.Error);
        result.CharactersTyped.Should().Be(15);
    }

    [Fact]
    public async Task Paste_WhenCtrlVIsThePauseChord_IsRefused_AndTheClipboardIsLeftAlone()
    {
        _clipboard.UserCopies("the user's text");

        var result = await Type("Ctrl+V", "some text", TextInputMethod.Paste);

        VerifyRefusedBeforeAnythingWasSent(result, "Ctrl+V");
        result.Method.Should().Be("paste");
        _clipboard.Text.Should().Be("the user's text");
    }

    [Fact]
    public async Task Auto_WhenCtrlVIsThePauseChord_TypesLongTextInsteadOfPasting()
    {
        var text = new string('x', InputSimulationService.AutoPasteThreshold + 1);

        var result = await Type("Ctrl+V", text, TextInputMethod.Auto);

        result.Success.Should().BeTrue(result.Error);
        result.Method.Should().Be("unicode");
        _clipboard.Writes.Should().Be(0);
        _input.Events.Should().NotContain(e => e.Kind == InputEventKind.VirtualKey);
    }
}
