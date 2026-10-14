using System.Text.Json;

namespace WindowsAppManagerMcp.Tests.Unit.Services;

/// <summary>
/// type_text's methods (roadmap M4.4): paste through a lent clipboard with the paste rule checked right before Ctrl+V,
/// auto's choice between pasting and typing, keys typed with the window's layout, and the length caps. The input is
/// <see cref="FakeInputWrapper"/>, the clipboard <see cref="FakeClipboardWrapper"/> behind the real paste rule and origin,
/// the layouts <see cref="FakeKeyboardLayoutWrapper"/> and the guard a mock, so nothing reaches the desktop or the user's
/// clipboard.
/// </summary>
public class InputSimulationServiceTextMethodTests
{
    private const uint Extended = 0x0001, KeyUp = 0x0002;
    private const ushort VkShift = 0x10, VkControl = 0x11, VkLControl = 0xA2, VkRMenu = 0xA5, VkReturn = 0x0D, VkCapital = 0x14, VkV = 0x56;
    private static readonly nint Window = 0x5150;
    private static readonly WindowSummary Notepad = new(Window, "Untitled - Notepad", "notepad", 5150);

    private readonly FakeInputWrapper _input = new();
    private readonly FakeKeyboardLayoutWrapper _layouts = new();
    private readonly FakeClipboardWrapper _clipboard = new();
    private readonly ClipboardOrigin _origin;
    private readonly Mock<INativeWindowWrapper> _windows = new();
    private readonly Mock<IInputTargetGuard> _guard = TestDataFactory.CreateAllowingInputGuard(Notepad);
    private readonly InputSession _session;

    public InputSimulationServiceTextMethodTests()
    {
        _session = new InputSession(_input);
        _origin = new ClipboardOrigin(_clipboard, new ManualClock(), Timeout.InfiniteTimeSpan);
        WindowIs("Notepad", "notepad");
    }

    private InputSimulationService CreateSut(bool withPasteClipboard = true) =>
        new(TestDataFactory.CreateMockMonitorService().Object, _input, _guard.Object, _session,
            clipboardOrigin: _origin,
            pasteGuard: new ClipboardPasteGuard(_clipboard, _origin, ClipboardOptions.Default, _windows.Object),
            keyboardLayout: _layouts,
            pasteClipboard: withPasteClipboard ? new PasteClipboard(_clipboard, _origin, _windows.Object) : null,
            pasteSettleTime: TimeSpan.Zero);

    private Task<TypeTextResult> Type(string text, TextInputMethod method, TextControlChars? controlChars = null, int delayMs = 0,
        InputTarget target = default) =>
        CreateSut().TypeTextAsync(text, delayMs, target, releaseHeldModifiers: false, controlChars, method, CancellationToken.None);

    private void WindowIs(string className, string processName)
    {
        _windows.Setup(w => w.GetClassName(Window)).Returns(className);
        _windows.Setup(w => w.GetWindowThreadProcessId(Window)).Returns(77u);
        _windows.Setup(w => w.GetProcessName(77u)).Returns(processName);
    }

    private static InputEvent[] Tap(ushort vk, ushort scan, bool extended = false) =>
    [
        InputEvent.Key(vk, extended ? Extended : 0, scan), InputEvent.Key(vk, (extended ? Extended : 0) | KeyUp, scan)
    ];

    private static InputEvent[] Unicode(char c) => [InputEvent.Unicode(c, 0), InputEvent.Unicode(c, KeyUp)];

    /// <summary>Ctrl+V as the paste sends it on the US layout: Ctrl's and V's scan codes.</summary>
    private static readonly InputEvent[] CtrlV =
    [
        InputEvent.Key(VkControl, 0, 0x1D), InputEvent.Key(VkV, 0, 0x2F), InputEvent.Key(VkV, KeyUp, 0x2F), InputEvent.Key(VkControl, KeyUp, 0x1D)
    ];

    private void VerifyNothingIsDown()
    {
        _session.ServerHeld.Should().BeEmpty();
        foreach (var key in new ushort[] { VkShift, VkControl, VkRMenu, VkLControl, VkV })
        {
            _input.IsKeyDown(key).Should().BeFalse($"key 0x{key:X2} must not stay down");
        }
    }

    #region method paste

    [Fact]
    public async Task Paste_PutsTheTextOnTheClipboard_PressesCtrlV_AndPutsTheClipboardBack()
    {
        // Arrange - what the clipboard holds while Ctrl+V goes out
        _clipboard.UserCopies("the user's text");
        string? onTheClipboard = null;
        _input.BeforeSend = _ => onTheClipboard = _clipboard.Text;

        // Act
        var result = await Type("line one\nline two", TextInputMethod.Paste);

        // Assert - a Windows line break on the clipboard, Ctrl+V with scan codes, and the user's text back
        result.Success.Should().BeTrue(result.Error);
        result.Method.Should().Be("paste");
        result.Length.Should().Be(17);
        result.CharactersTyped.Should().Be(17);
        result.ClipboardRestored.Should().BeTrue();
        result.Warning.Should().BeNull();
        result.ReceivingWindow.Should().Be(Notepad);
        onTheClipboard.Should().Be("line one\r\nline two");
        _input.Batches.Should().ContainSingle().Which.Should().Equal(CtrlV);
        _clipboard.Text.Should().Be("the user's text");
        VerifyNothingIsDown();
    }

    [Fact]
    public async Task Paste_TheResultNeverEchoesTheText()
    {
        var result = await Type("correct horse battery staple", TextInputMethod.Paste);

        result.Success.Should().BeTrue(result.Error);
        JsonSerializer.Serialize(result).Should().NotContain("horse");
    }

    [Fact]
    public async Task Paste_TakesNoControlChars_LineBreaksAndTabsArePastedAsTheyAre()
    {
        var result = await Type("a\tb\r\nc", TextInputMethod.Paste);

        result.Success.Should().BeTrue(result.Error);
        result.Length.Should().Be(5);
    }

    [Theory]
    [InlineData("image")]
    [InlineData("private")]
    public async Task Paste_WhenTheClipboardCannotBePutBack_IsClipboardUnavailable_AndNothingIsSentOrReplaced(string what)
    {
        // Arrange
        _clipboard.UserCopies("caption", what == "private" ? ClipboardPrivacyMarkers.ExcludeFromMonitorProcessing : ClipboardPrivacyMarkers.None);
        _clipboard.OtherFormats = what == "image";

        // Act
        var result = await Type("hello", TextInputMethod.Paste);

        // Assert
        result.ErrorCode.Should().Be(InputErrorCode.ClipboardUnavailable);
        result.Error.Should().StartWith("Nothing was pasted").And.Contain("The clipboard is as it was").And.Contain("method 'unicode'");
        result.Method.Should().Be("paste");
        result.ClipboardRestored.Should().BeNull("the clipboard was not used");
        _input.Batches.Should().BeEmpty();
        _clipboard.Replacements.Should().BeEmpty();
    }

    [Fact]
    public async Task Paste_IntoATerminal_IsPasteNotAllowed_AndTheClipboardIsNotTouched()
    {
        WindowIs("CASCADIA_HOSTING_WINDOW_CLASS", "WindowsTerminal");

        var result = await Type("echo hi", TextInputMethod.Paste);

        result.ErrorCode.Should().Be(InputErrorCode.PasteNotAllowed);
        result.Error.Should().Contain("is a terminal").And.Contain("runs the line as a command");
        _input.Batches.Should().BeEmpty();
        _clipboard.Replacements.Should().BeEmpty();
    }

    [Fact]
    public async Task Paste_IntoAnEditorWithATerminalPanel_PastesWhenAsked()
    {
        WindowIs("Chrome_WidgetWin_1", "Code");

        var result = await Type("line one\nline two", TextInputMethod.Paste);

        result.Success.Should().BeTrue(result.Error);
        result.Method.Should().Be("paste");
    }

    [Fact]
    public async Task Paste_WithDelayMs_IsInvalidArguments_AndNothingIsChecked()
    {
        var result = await Type("hello", TextInputMethod.Paste, delayMs: 20);

        result.ErrorCode.Should().Be(InputErrorCode.InvalidArguments);
        result.Error.Should().Contain("delayMs");
        _guard.Verify(g => g.CheckKeyboard(It.IsAny<InputTarget>(), It.IsAny<KeyboardInputKind>(), It.IsAny<IReadOnlyList<InputEvent>?>()), Times.Never);
    }

    [Fact]
    public async Task Paste_OfMoreThanAMillionCharacters_IsTextTooLong()
    {
        var result = await Type(new string('x', InputSimulationService.MaxPasteLength + 1), TextInputMethod.Paste);

        result.ErrorCode.Should().Be(InputErrorCode.TextTooLong);
        result.Error.Should().Contain("1,000,000");
        _clipboard.Replacements.Should().BeEmpty();
    }

    [Fact]
    public async Task Paste_WhenTheWindowLosesTheFocusBeforeCtrlV_SendsNothing_AndPutsTheClipboardBack()
    {
        // Arrange
        _clipboard.UserCopies("before");
        _guard.Setup(g => g.RecheckKeyboard(It.IsAny<InputGuardResult>()))
            .Returns(InputGuardResult.Refuse(InputErrorCode.TargetLostFocus, "Another window is in front.", InputTarget.None, Notepad));

        // Act
        var result = await Type("hello", TextInputMethod.Paste);

        // Assert
        result.ErrorCode.Should().Be(InputErrorCode.TargetLostFocus);
        result.Error.Should().StartWith("Nothing was pasted.");
        result.ClipboardRestored.Should().BeTrue();
        _input.Batches.Should().BeEmpty();
        _clipboard.Text.Should().Be("before");
    }

    [Fact]
    public async Task Paste_WhenAnotherAppCopiesRightBeforeCtrlV_ThePasteRuleRefusesIt_AndItsCopyStays()
    {
        // Arrange - the user copies while the paste rule reads the clipboard (the lend's read is the first)
        _clipboard.UserCopies("before");
        var reads = 0;
        _clipboard.OnRead = () =>
        {
            if (++reads == 2)
            {
                _clipboard.UserCopies("copied meanwhile");
            }
        };

        // Act
        var result = await Type("hello", TextInputMethod.Paste);

        // Assert
        result.ErrorCode.Should().Be(InputErrorCode.PasteNotAllowed);
        result.ClipboardRestored.Should().BeFalse();
        result.Warning.Should().Contain("left as it is");
        _input.Batches.Should().BeEmpty();
        _clipboard.Text.Should().Be("copied meanwhile");
    }

    [Fact]
    public async Task Paste_WhenTheUserPausesBeforeCtrlV_IsPausedByUser_AndPutsTheClipboardBack()
    {
        _clipboard.UserCopies("before");
        _guard.Setup(g => g.RecheckKeyboard(It.IsAny<InputGuardResult>()))
            .Returns((InputGuardResult approved) =>
            {
                _session.Pause(InputControlSources.Hotkey);
                return approved;
            });

        var result = await Type("hello", TextInputMethod.Paste);

        result.ErrorCode.Should().Be(InputErrorCode.PausedByUser);
        result.Error.Should().StartWith("Nothing was pasted.");
        _input.Batches.Should().BeEmpty();
        _clipboard.Text.Should().Be("before");
    }

    [Fact]
    public async Task Paste_WhenTheUserPausesWhileTheWindowIsChecked_LeavesTheClipboardAlone()
    {
        // Arrange - the hotkey goes down while the guard brings the window to the front
        _clipboard.UserCopies("before");
        _guard.Setup(g => g.CheckKeyboard(It.IsAny<InputTarget>(), It.IsAny<KeyboardInputKind>(), It.IsAny<IReadOnlyList<InputEvent>?>()))
            .Returns((InputTarget target, KeyboardInputKind _, IReadOnlyList<InputEvent>? _) =>
            {
                _session.Pause(InputControlSources.Hotkey);
                return InputGuardResult.Allow(target, Window, Notepad, Window);
            });

        // Act
        var result = await Type("hello", TextInputMethod.Paste);

        // Assert
        result.ErrorCode.Should().Be(InputErrorCode.PausedByUser);
        result.Method.Should().Be("paste");
        _clipboard.Replacements.Should().BeEmpty();
        _input.Batches.Should().BeEmpty();
    }

    [Fact]
    public async Task Paste_WhenSendInputStopsPartWay_IsSendInputFailed_ReleasesTheKeys_AndStillPutsTheClipboardBack()
    {
        // Arrange - Ctrl and V go down, then SendInput stops
        _clipboard.UserCopies("before");
        _input.Insert = (events, call) => call == 0 ? 2u : (uint)events.Count;

        // Act
        var result = await Type("hello", TextInputMethod.Paste);

        // Assert
        result.ErrorCode.Should().Be(InputErrorCode.SendInputFailed);
        result.Error.Should().Contain("may or may not have been pasted");
        result.ClipboardRestored.Should().BeTrue();
        _clipboard.Text.Should().Be("before");
        VerifyNothingIsDown();
    }

    [Fact]
    public async Task Paste_WhenTheClipboardCannotBePutBack_Succeeds_WithAWarning()
    {
        // Arrange - another application holds the clipboard open once Ctrl+V has gone out
        _clipboard.UserCopies("before");
        _input.BeforeSend = _ => _clipboard.CanOpen = false;

        // Act
        var result = await Type("hello", TextInputMethod.Paste);

        // Assert
        result.Success.Should().BeTrue();
        result.ClipboardRestored.Should().BeFalse();
        result.Warning.Should().Contain("Tell the user");
    }

    [Fact]
    public async Task Paste_SendsCtrlVWithTheScanCodesOfTheTargetsLayout()
    {
        // V is the same key on a German keyboard; Ctrl+V is the key Windows calls V on every layout
        _layouts.WindowLayouts[Window] = FakeKeyboardLayoutWrapper.German;

        var result = await Type("hello", TextInputMethod.Paste, target: InputTarget.Window(Window));

        result.Success.Should().BeTrue(result.Error);
        _input.Events.Should().Equal(CtrlV);
        _layouts.LayoutQueries.Should().Contain(Window);
    }

    #endregion

    #region method auto

    [Fact]
    public async Task Auto_TypesShortText_WithoutTouchingTheClipboard()
    {
        var result = await Type("hello", TextInputMethod.Auto);

        result.Success.Should().BeTrue(result.Error);
        result.Method.Should().Be("unicode");
        result.ClipboardRestored.Should().BeNull();
        _input.Events.Should().Equal([.. Unicode('h'), .. Unicode('e'), .. Unicode('l'), .. Unicode('l'), .. Unicode('o')]);
        _clipboard.Replacements.Should().BeEmpty();
    }

    [Fact]
    public async Task Auto_PastesTextLongerThan200Characters()
    {
        _clipboard.UserCopies("before");

        var result = await Type(new string('x', InputSimulationService.AutoPasteThreshold + 1), TextInputMethod.Auto);

        result.Success.Should().BeTrue(result.Error);
        result.Method.Should().Be("paste");
        result.ClipboardRestored.Should().BeTrue();
        _input.Events.Should().Equal(CtrlV);
        _clipboard.Text.Should().Be("before");
    }

    [Fact]
    public async Task Auto_PastesLongTextWithLineBreaks_WithoutControlChars()
    {
        var text = string.Join('\n', Enumerable.Repeat("a line of code;", 20));

        var result = await Type(text, TextInputMethod.Auto);

        result.Success.Should().BeTrue(result.Error);
        result.Method.Should().Be("paste");
    }

    [Fact]
    public async Task Auto_WhenTheClipboardHoldsAnImage_TypesInstead_AndSaysWhy()
    {
        // Arrange
        _clipboard.UserCopies("caption");
        _clipboard.OtherFormats = true;
        var text = new string('x', 250);

        // Act
        var result = await Type(text, TextInputMethod.Auto);

        // Assert - typed, and the user's clipboard untouched
        result.Success.Should().BeTrue(result.Error);
        result.Method.Should().Be("unicode");
        result.CharactersTyped.Should().Be(250);
        result.Warning.Should().StartWith("Typed instead of pasted:").And.Contain("more than plain text");
        _input.Events.Should().HaveCount(500);
        _clipboard.Replacements.Should().BeEmpty();
    }

    [Fact]
    public async Task Auto_WhenAnotherAppCopiesRightBeforeCtrlV_TypesInstead_AndSaysWhatTheClipboardHolds()
    {
        // Arrange - the user copies while the paste rule reads the clipboard (the lend's read is the first)
        _clipboard.UserCopies("before");
        var reads = 0;
        _clipboard.OnRead = () =>
        {
            if (++reads == 2)
            {
                _clipboard.UserCopies("copied meanwhile");
            }
        };

        // Act
        var result = await Type(new string('x', 250), TextInputMethod.Auto);

        // Assert - typed, no Ctrl+V, and the user's new copy left alone
        result.Success.Should().BeTrue(result.Error);
        result.Method.Should().Be("unicode");
        result.ClipboardRestored.Should().BeFalse();
        result.Warning.Should().StartWith("Typed instead of pasted: the clipboard could not be used right before Ctrl+V").And.Contain("left as it is");
        _input.Events.Should().HaveCount(500);
        _clipboard.Text.Should().Be("copied meanwhile");
    }

    [Fact]
    public async Task Auto_IntoATerminal_Types()
    {
        WindowIs("ConsoleWindowClass", "conhost");

        var result = await Type(new string('x', 250), TextInputMethod.Auto);

        result.Method.Should().Be("unicode");
        result.Warning.Should().Contain("terminal");
        _clipboard.Replacements.Should().BeEmpty();
    }

    [Fact]
    public async Task Auto_TextWithLineBreaksIntoAnEditorWithATerminalPanel_Types_ButOneLinePastes()
    {
        WindowIs("Chrome_WidgetWin_1", "Code");
        var lines = string.Join('\n', Enumerable.Repeat("a line of code;", 20));

        var multiLine = await Type(lines, TextInputMethod.Auto, TextControlChars.Unicode);
        var oneLine = await Type(new string('x', 250), TextInputMethod.Auto);

        multiLine.Method.Should().Be("unicode");
        multiLine.Warning.Should().Contain("terminal panel");
        oneLine.Method.Should().Be("paste");
    }

    [Fact]
    public async Task Auto_LongTextWithLineBreaks_ThatCannotBePasted_StillNeedsControlChars()
    {
        // Arrange - typing it would need to know what the line breaks do
        _clipboard.UserCopies("caption");
        _clipboard.OtherFormats = true;
        var text = string.Join('\n', Enumerable.Repeat("a line of code;", 20));

        // Act
        var result = await Type(text, TextInputMethod.Auto);

        // Assert
        result.ErrorCode.Should().Be(InputErrorCode.InvalidArguments);
        result.Error.Should().StartWith("The text was not pasted:").And.Contain("controlChars");
        _input.Batches.Should().BeEmpty();
    }

    [Theory]
    [InlineData(TextControlChars.Keys)]
    [InlineData(TextControlChars.ShiftEnter)]
    public async Task Auto_WhenLineBreaksPressKeys_Types(TextControlChars controlChars)
    {
        var text = string.Join('\n', Enumerable.Repeat("a line of text", 20));

        var result = await Type(text, TextInputMethod.Auto, controlChars);

        result.Success.Should().BeTrue(result.Error);
        result.Method.Should().Be("unicode");
        result.Warning.Should().BeNull("auto chose to type: nothing was refused");
        _input.Events.Should().Contain(InputEvent.Key(VkReturn, 0, 0x1C));
        _clipboard.Replacements.Should().BeEmpty();
    }

    [Fact]
    public async Task Auto_WithDelayMs_Types()
    {
        var result = await Type(new string('x', 250), TextInputMethod.Auto, delayMs: 1);

        result.Method.Should().Be("unicode");
        _input.Batches.Should().HaveCount(250);
        _clipboard.Replacements.Should().BeEmpty();
    }

    [Fact]
    public async Task Auto_PastesTextOverTheTypingCap()
    {
        var result = await Type(new string('x', InputSimulationService.MaxTypedLength + 1000), TextInputMethod.Auto);

        result.Success.Should().BeTrue(result.Error);
        result.Method.Should().Be("paste");
    }

    [Fact]
    public async Task Auto_TextOverTheTypingCap_ThatCannotBePasted_IsTextTooLong_AndSaysWhy()
    {
        _clipboard.UserCopies("caption");
        _clipboard.OtherFormats = true;

        var result = await Type(new string('x', InputSimulationService.MaxTypedLength + 1), TextInputMethod.Auto);

        result.ErrorCode.Should().Be(InputErrorCode.TextTooLong);
        result.Error.Should().StartWith("The text was not pasted:").And.Contain("5,000");
        _input.Batches.Should().BeEmpty();
    }

    [Fact]
    public async Task Auto_WithoutThePasteClipboard_Types()
    {
        var result = await CreateSut(withPasteClipboard: false).TypeTextAsync(new string('x', 250), 0, default, false, null,
            TextInputMethod.Auto, CancellationToken.None);

        result.Method.Should().Be("unicode");
        result.Success.Should().BeTrue();
    }

    #endregion

    #region The typing cap and the count

    [Theory]
    [InlineData(TextInputMethod.Unicode)]
    [InlineData(TextInputMethod.Keys)]
    public async Task Typing_MoreThan5000Characters_IsTextTooLong_AndNothingIsChecked(TextInputMethod method)
    {
        var result = await Type(new string('x', InputSimulationService.MaxTypedLength + 1), method);

        result.ErrorCode.Should().Be(InputErrorCode.TextTooLong);
        result.Length.Should().Be(5001);
        result.Error.Should().Contain("5,000").And.Contain("method 'paste'");
        _guard.Verify(g => g.CheckKeyboard(It.IsAny<InputTarget>(), It.IsAny<KeyboardInputKind>(), It.IsAny<IReadOnlyList<InputEvent>?>()), Times.Never);
        _input.Batches.Should().BeEmpty();
    }

    [Fact]
    public async Task Typing_5000Characters_IsAllowed()
    {
        var result = await Type(new string('x', InputSimulationService.MaxTypedLength), TextInputMethod.Unicode);

        result.Success.Should().BeTrue(result.Error);
        result.CharactersTyped.Should().Be(5000);
    }

    [Fact]
    public async Task TheCount_IsOfCodePoints_WithAWindowsLineBreakAsOne()
    {
        // a, an emoji (two UTF-16 code units), a Windows line break, b
        var result = await Type("a\U0001F600\r\nb", TextInputMethod.Unicode, TextControlChars.Unicode);

        result.Length.Should().Be(4);
        result.CharactersTyped.Should().Be(4);
    }

    #endregion

    #region method keys

    [Fact]
    public async Task Keys_TypesEveryCharacterWithTheLayoutsKey_AndShift()
    {
        var result = await Type("Hi!", TextInputMethod.Keys);

        result.Success.Should().BeTrue(result.Error);
        result.Method.Should().Be("keys");
        _input.Events.Should().Equal([
            InputEvent.Key(VkShift, 0, 0x2A), .. Tap('H', 0x23), InputEvent.Key(VkShift, KeyUp, 0x2A),
            .. Tap('I', 0x17),
            InputEvent.Key(VkShift, 0, 0x2A), .. Tap('1', 0x02), InputEvent.Key(VkShift, KeyUp, 0x2A)]);
        VerifyNothingIsDown();
    }

    [Fact]
    public async Task Keys_OnANorwegianLayout_TypesAtWithAltGr()
    {
        _layouts.WindowLayouts[Window] = FakeKeyboardLayoutWrapper.Norwegian;

        var result = await Type("@", TextInputMethod.Keys, target: InputTarget.Window(Window));

        result.Success.Should().BeTrue(result.Error);
        _input.Events.Should().Equal(
            InputEvent.Key(VkLControl, 0, 0x1D), InputEvent.Key(VkRMenu, Extended, 0x38),
            InputEvent.Key('2', 0, 0x03), InputEvent.Key('2', KeyUp, 0x03),
            InputEvent.Key(VkRMenu, Extended | KeyUp, 0x38), InputEvent.Key(VkLControl, KeyUp, 0x1D));
        VerifyNothingIsDown();
    }

    [Fact]
    public async Task Keys_ACharacterTheLayoutHasNoKeyFor_GoesAsUnicode()
    {
        var result = await Type("é\U0001F600", TextInputMethod.Keys);

        result.Success.Should().BeTrue(result.Error);
        result.CharactersTyped.Should().Be(2);
        _input.Events.Should().Equal([.. Unicode('é'), .. Unicode('\uD83D'), .. Unicode('\uDE00')]);
    }

    [Fact]
    public async Task Keys_WithCapsLockOn_KeepsTheTextAsWritten()
    {
        _input.Toggled.Add(VkCapital);

        (await Type("Hi", TextInputMethod.Keys)).Success.Should().BeTrue();

        _input.Events.Should().Equal([
            .. Tap('H', 0x23),
            InputEvent.Key(VkShift, 0, 0x2A), .. Tap('I', 0x17), InputEvent.Key(VkShift, KeyUp, 0x2A)]);
    }

    [Fact]
    public async Task Keys_FollowControlChars_ForLineBreaks()
    {
        var result = await Type("a\nb", TextInputMethod.Keys, TextControlChars.Keys);

        result.Success.Should().BeTrue(result.Error);
        _input.Events.Should().Equal([.. Tap('A', 0x1E), .. Tap(VkReturn, 0x1C), .. Tap('B', 0x30)]);
    }

    [Fact]
    public async Task Keys_ShortInsert_CountsOnlyWholeCharacters()
    {
        // 'H' is four events (Shift down, H down and up, Shift up): three go in
        _input.Insert = (events, call) => call == 0 ? 3u : (uint)events.Count;

        var result = await Type("Hi", TextInputMethod.Keys);

        result.ErrorCode.Should().Be(InputErrorCode.SendInputFailed);
        result.CharactersTyped.Should().Be(0);
        VerifyNothingIsDown();
    }

    #endregion
}
