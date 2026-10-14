using WindowsAppManagerMcp.Models;
using WindowsAppManagerMcp.Native;
using WindowsAppManagerMcp.Services.Interfaces;

namespace WindowsAppManagerMcp.Services;

/// <summary>
/// Service for simulating mouse and keyboard input using Windows SendInput API. Every call runs the input
/// guard (<see cref="IInputTargetGuard"/>) inside the input gate before each SendInput batch, and reports
/// where the input went. Every batch goes through the <see cref="IInputSession"/>, which refuses it while
/// the user has paused input and releases what an interrupted batch left down.
/// </summary>
public partial class InputSimulationService : IInputSimulationService
{
    /// <summary>
    /// type_text sends at most this many characters in one SendInput batch, and the guard checks the
    /// foreground window again before each batch, so typing stops within this many characters of a focus
    /// change. With a delay between keystrokes every character is its own batch.
    /// </summary>
    public const int TypeTextBatchSize = 32;

    /// <summary>The most wheel notches scroll sends in one direction in one call.</summary>
    public const int MaxScrollNotches = 100;

    /// <summary>
    /// The ModifiersHeld message's advice for a stuck key. element_action, which has no releaseHeldModifiers, puts its own
    /// advice in its place.
    /// </summary>
    public const string ReleaseHeldModifiersAdvice =
        "pass releaseHeldModifiers: true (or call release_all_input with releaseHeldModifiers: true)";

    private readonly IMonitorService _monitorService;
    private readonly INativeInputWrapper _inputWrapper;
    private readonly IInputTargetGuard _guard;
    private readonly IInputSession _session;
    private readonly TimeSpan _gateTimeout;
    private readonly INativeHitTestWrapper? _hitTest;
    private readonly INativeSystemInfoWrapper? _systemInfo;
    private readonly IClipboardOrigin? _clipboardOrigin;
    private readonly IClipboardPasteGuard? _pasteGuard;
    private readonly INativeKeyboardLayoutWrapper? _keyboardLayout;

    /// <param name="monitorService">The monitors, for coordinate checks.</param>
    /// <param name="inputWrapper">The native input layer, for cursor reads; input itself goes through <paramref name="session"/>.</param>
    /// <param name="guard">The input guard.</param>
    /// <param name="session">The input session: the user's pause and the held-input bookkeeping.</param>
    /// <param name="gateTimeout">How long to wait for the input gate; default <see cref="InputGate.InputWaitTimeout"/>.</param>
    /// <param name="hitTest">scroll: the client area of the target window, where it scrolls when no point is given.</param>
    /// <param name="systemInfo">
    /// The virtual screen a batch's absolute move spans and whether the mouse buttons are swapped (roadmap M4.2), and for
    /// scroll where Windows sends the wheel. Without it the monitors' bounding rectangle is the virtual screen, the buttons
    /// count as not swapped, and the wheel routing is unknown, so scroll goes only to the foreground window, as with focus
    /// routing. The server always passes it.
    /// </param>
    /// <param name="clipboardOrigin">Told about the batches that can copy, so get_clipboard knows the agent's copies.</param>
    /// <param name="pasteGuard">
    /// Checks the batches that paste, right before they are sent, so the agent pastes only what get_clipboard would return
    /// (PasteNotAllowed). The server always passes it.
    /// </param>
    /// <param name="keyboardLayout">
    /// The keyboard layout queries send_keys maps characters with (roadmap M4.1). The server always passes it; without it
    /// keys are mapped with <see cref="KeyboardLayout.Invariant"/>.
    /// </param>
    public InputSimulationService(
        IMonitorService monitorService,
        INativeInputWrapper inputWrapper,
        IInputTargetGuard guard,
        IInputSession session,
        TimeSpan? gateTimeout = null,
        INativeHitTestWrapper? hitTest = null,
        INativeSystemInfoWrapper? systemInfo = null,
        IClipboardOrigin? clipboardOrigin = null,
        IClipboardPasteGuard? pasteGuard = null,
        INativeKeyboardLayoutWrapper? keyboardLayout = null)
    {
        _monitorService = monitorService;
        _inputWrapper = inputWrapper;
        _guard = guard ?? throw new ArgumentNullException(nameof(guard));
        _session = session ?? throw new ArgumentNullException(nameof(session));
        _gateTimeout = gateTimeout ?? InputGate.InputWaitTimeout;
        _hitTest = hitTest;
        _systemInfo = systemInfo;
        _clipboardOrigin = clipboardOrigin;
        _pasteGuard = pasteGuard;
        _keyboardLayout = keyboardLayout;
    }

    /// <summary>
    /// How long a mouse call waits, at most, for the pointer to get to the point after its batch: Windows can move the pointer
    /// after SendInput has returned, so a pointer elsewhere is read again until then before the call reports CursorClamped.
    /// </summary>
    public static readonly TimeSpan PointerSettleTime = TimeSpan.FromMilliseconds(50);

    public ClickResult Click(int x, int y, InputTarget target = default, bool releaseHeldModifiers = false,
        CancellationToken cancellationToken = default) =>
        ClickAt(x, y, target, "left", MouseButton.Primary, clicks: 1, [], releaseHeldModifiers, cancellationToken);

    public ClickResult RightClick(int x, int y, InputTarget target = default, bool releaseHeldModifiers = false,
        CancellationToken cancellationToken = default) =>
        ClickAt(x, y, target, "right", MouseButton.Secondary, clicks: 1, [], releaseHeldModifiers, cancellationToken);

    public ClickResult DoubleClick(int x, int y, InputTarget target = default, bool releaseHeldModifiers = false,
        CancellationToken cancellationToken = default) =>
        ClickAt(x, y, target, "double", MouseButton.Primary, clicks: 2, [], releaseHeldModifiers, cancellationToken);

    public ClickResult MouseClick(int x, int y, MouseButton button, int clickCount, IReadOnlyList<MouseModifier> modifiers,
        InputTarget target = default, bool releaseHeldModifiers = false, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(clickCount, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(clickCount, InputBatchBuilder.MaxClicks);
        ArgumentNullException.ThrowIfNull(modifiers);
        var held = modifiers.Distinct().ToList();
        return ClickAt(x, y, target, ToolEnum<MouseButton>.Name(button), button, clickCount, held, releaseHeldModifiers, cancellationToken) with
        {
            ClickCount = clickCount,
            Modifiers = ModifierNames(held)
        };
    }

    /// <summary>The names of <paramref name="modifiers"/> for a result ("ctrl", "shift", ...); null without any.</summary>
    private static IReadOnlyList<string>? ModifierNames(IReadOnlyList<MouseModifier> modifiers) =>
        modifiers.Count == 0 ? null : modifiers.Select(ToolEnum<MouseModifier>.Name).ToList();

    public MouseMoveResult MoveMouse(int x, int y, InputTarget target = default, bool releaseHeldModifiers = false,
        CancellationToken cancellationToken = default)
    {
        if (!IsValidScreenCoordinate(x, y))
        {
            return new MouseMoveResult(
                Success: false,
                X: x,
                Y: y,
                Error: "Coordinates are outside screen bounds",
                ErrorCode: InputErrorCode.CoordinatesOutOfBounds);
        }

        using var call = new InputCall(_session, cancellationToken);
        if (call.IsPaused)
        {
            return new MouseMoveResult(false, x, y, PausedMessage("The pointer was not moved."), InputErrorCode.PausedByUser);
        }

        IDisposable? gate;
        try
        {
            gate = InputGate.TryEnter(_gateTimeout, call.Token);
        }
        catch (OperationCanceledException) when (call.IsPaused)
        {
            return new MouseMoveResult(false, x, y, PausedMessage("The pointer was not moved."), InputErrorCode.PausedByUser);
        }

        if (gate == null)
        {
            return new MouseMoveResult(false, x, y, BusyMessage("moved"), InputErrorCode.InputBusy);
        }

        using (gate)
        {
            InputGuardResult? approved = null;
            HeldCheck held = default;
            try
            {
                held = CheckHeldInput(releaseHeldModifiers, "moved");
                if (!held.Ok)
                {
                    return held.Apply(new MouseMoveResult(false, x, y, held.Error, held.Code));
                }

                approved = _guard.CheckMouse(x, y, target);
                if (!approved.Allowed)
                {
                    return call.IsPaused
                        ? held.Apply(Finish(new MouseMoveResult(false, x, y, PausedMessage("The pointer was not moved."), InputErrorCode.PausedByUser), approved))
                        : held.Apply(Finish(new MouseMoveResult(false, x, y, approved.Error, approved.ErrorCode), approved));
                }

                // A button the user holds: moving the pointer would move the user's drag
                if (MouseButtonsDownNow("moved") is { } buttons)
                {
                    return held.Apply(Finish(new MouseMoveResult(false, x, y, buttons, InputErrorCode.MouseInUse), approved));
                }

                // An injected move, as a click's (roadmap M4.2): applications that watch raw input or a low-level hook see it,
                // which they do not for SetCursorPos
                var sent = Send(NewBatch().MoveTo(x, y).Build(), approved);
                if (sent.PausedByUser)
                {
                    return held.Apply(Finish(new MouseMoveResult(false, x, y, PausedMessage("The pointer was not moved."), InputErrorCode.PausedByUser), approved));
                }

                if (!sent.Complete)
                {
                    return held.Apply(Finish(new MouseMoveResult(false, x, y,
                        "SendInput did not insert the move, so the pointer was not moved. The input desktop may not be reachable (a UAC " +
                        "prompt, the lock screen or a disconnected session). Try again; if it keeps failing, ask the user.",
                        InputErrorCode.SendInputFailed), approved));
                }

                if (PointerAwayAfterBatch(x, y) is { } at)
                {
                    return held.Apply(Finish(new MouseMoveResult(false, x, y, PointerClamped("The move", x, y, at), InputErrorCode.CursorClamped),
                        approved));
                }

                return held.Apply(Finish(new MouseMoveResult(Success: true, X: x, Y: y), approved));
            }
            catch (OperationCanceledException) when (call.IsPaused)
            {
                var paused = new MouseMoveResult(false, x, y, PausedMessage("The pointer was not moved."), InputErrorCode.PausedByUser);
                return held.Apply(approved == null ? paused : Finish(paused, approved));
            }
            finally
            {
                _session.ReleaseHeldInput("the end of the input call");
            }
        }
    }

    public ScrollResult Scroll(int? x, int? y, int deltaY, int deltaX, InputTarget target = default, bool releaseHeldModifiers = false,
        CancellationToken cancellationToken = default)
    {
        if (x.HasValue != y.HasValue || (!x.HasValue && !target.HasWindow))
        {
            throw new ArgumentException("scroll needs both x and y, or neither and a target window.");
        }

        if ((deltaY == 0 && deltaX == 0) || deltaY is < -MaxScrollNotches or > MaxScrollNotches ||
            deltaX is < -MaxScrollNotches or > MaxScrollNotches)
        {
            throw new ArgumentOutOfRangeException(nameof(deltaY), $"scroll needs 1 to {MaxScrollNotches} notches in at least one direction.");
        }

        // No point: the centre of the target's client area
        if (x is not { } pointX || y is not { } pointY)
        {
            var handle = target.WindowHandle!.Value;
            var client = _hitTest?.GetClientAreaOnScreen(handle);
            if (client == null)
            {
                return new ScrollResult(false, 0, 0, deltaY, deltaX,
                    $"Window {(long)handle} is not a window, or its client area cannot be read, so there is no point to scroll at. " +
                    "Get a current handle from find_windows, or pass x and y.", InputErrorCode.InvalidHandle);
            }

            if (client.Width <= 0 || client.Height <= 0)
            {
                return new ScrollResult(false, client.X, client.Y, deltaY, deltaX,
                    $"Window {(long)handle} has no client area on screen (it is minimized or has no size), so there is nothing to " +
                    "scroll. Restore it with set_window_state first.", InputErrorCode.CoordinatesOutOfBounds);
            }

            pointX = client.X + client.Width / 2;
            pointY = client.Y + client.Height / 2;
        }

        if (!IsValidScreenCoordinate(pointX, pointY))
        {
            return new ScrollResult(false, pointX, pointY, deltaY, deltaX, "Coordinates are outside screen bounds",
                InputErrorCode.CoordinatesOutOfBounds);
        }

        return ScrollAt(pointX, pointY, deltaY, deltaX, target, releaseHeldModifiers, cancellationToken);
    }

    /// <summary>
    /// scroll at a checked point, as a click: the guard checks the window at the point before the pointer moves and
    /// again right before the wheel, together with where Windows sends the wheel. The wheel notches go in one SendInput
    /// call with a move to the checked point (<see cref="InputBatchBuilder"/>), so no other input can come between them; a
    /// pointer that is not at the point afterwards is CursorClamped.
    /// </summary>
    private ScrollResult ScrollAt(int x, int y, int deltaY, int deltaX, InputTarget target, bool releaseHeldModifiers,
        CancellationToken cancellationToken)
    {
        const string NothingScrolled = "Nothing was scrolled.";

        using var call = new InputCall(_session, cancellationToken);
        if (call.IsPaused)
        {
            return new ScrollResult(false, x, y, deltaY, deltaX, PausedMessage(NothingScrolled), InputErrorCode.PausedByUser);
        }

        IDisposable? gate;
        try
        {
            gate = InputGate.TryEnter(_gateTimeout, call.Token);
        }
        catch (OperationCanceledException) when (call.IsPaused)
        {
            return new ScrollResult(false, x, y, deltaY, deltaX, PausedMessage(NothingScrolled), InputErrorCode.PausedByUser);
        }

        if (gate == null)
        {
            return new ScrollResult(false, x, y, deltaY, deltaX, BusyMessage("scrolled"), InputErrorCode.InputBusy);
        }

        using (gate)
        {
            InputGuardResult? approved = null;
            HeldCheck held = default;
            try
            {
                held = CheckHeldInput(releaseHeldModifiers, "scrolled");
                if (!held.Ok)
                {
                    return held.Apply(new ScrollResult(false, x, y, deltaY, deltaX, held.Error, held.Code));
                }

                approved = _guard.CheckMouse(x, y, target);
                if (!approved.Allowed)
                {
                    return call.IsPaused
                        ? held.Apply(Finish(new ScrollResult(false, x, y, deltaY, deltaX, PausedMessage(NothingScrolled), InputErrorCode.PausedByUser), approved))
                        : held.Apply(Finish(new ScrollResult(false, x, y, deltaY, deltaX, approved.Error, approved.ErrorCode), approved));
                }

                // Before the pointer moves: a wheel that follows the focus would scroll another window
                if (WheelGoesElsewhere(approved) is { } elsewhere)
                {
                    return held.Apply(Finish(new ScrollResult(false, x, y, deltaY, deltaX, elsewhere, InputErrorCode.WheelFollowsFocus), approved));
                }

                if (MouseButtonsDownNow("scrolled") is { } buttons)
                {
                    return held.Apply(Finish(new ScrollResult(false, x, y, deltaY, deltaX, buttons, InputErrorCode.MouseInUse), approved));
                }

                switch (_session.SetCursorPos(x, y))
                {
                    case null:
                        return held.Apply(Finish(new ScrollResult(false, x, y, deltaY, deltaX, PausedMessage(NothingScrolled), InputErrorCode.PausedByUser), approved));
                    case false:
                        return held.Apply(Finish(new ScrollResult(false, x, y, deltaY, deltaX,
                            CursorPositionFailedMessage(NothingScrolled), InputErrorCode.CursorPositionFailed), approved));
                }

                // As for a click: a popup the pointer opens there shows up in the re-check
                Wait(10, call);

                var recheck = _guard.RecheckMouse(x, y, approved);
                if (!recheck.Allowed)
                {
                    return held.Apply(Finish(new ScrollResult(false, x, y, deltaY, deltaX, $"{NothingScrolled} {recheck.Error}", recheck.ErrorCode), recheck));
                }

                approved = recheck;
                switch (PointerIsAt(x, y, out var actual))
                {
                    case PointerCheck.Paused:
                        return held.Apply(Finish(new ScrollResult(false, x, y, deltaY, deltaX,
                            PausedMessage("The pointer moved away from the point and was not placed there again; nothing was scrolled."),
                            InputErrorCode.PausedByUser), approved));
                    case PointerCheck.Away:
                        return held.Apply(Finish(new ScrollResult(false, x, y, deltaY, deltaX, $"{NothingScrolled} {PointerMovedAway(x, y, actual)}",
                            InputErrorCode.CursorNotAtPoint), approved));
                }

                // Right before the wheel: Ctrl turns it into zoom, Shift into horizontal scrolling
                if (ModifiersDownNow("scrolled") is { } downNow)
                {
                    return held.Apply(Finish(new ScrollResult(false, x, y, deltaY, deltaX, downNow.Error, InputErrorCode.ModifiersHeld)
                    {
                        HeldModifiers = downNow.Names
                    }, approved));
                }

                if (MouseButtonsDownNow("scrolled") is { } buttonsNow)
                {
                    return held.Apply(Finish(new ScrollResult(false, x, y, deltaY, deltaX, buttonsNow, InputErrorCode.MouseInUse), approved));
                }

                // The foreground may have changed while the pointer moved
                if (WheelGoesElsewhere(approved) is { } elsewhereNow)
                {
                    return held.Apply(Finish(new ScrollResult(false, x, y, deltaY, deltaX, elsewhereNow, InputErrorCode.WheelFollowsFocus), approved));
                }

                var sent = Send(NewBatch().MoveTo(x, y).Wheel(deltaY, deltaX).Build(), approved);
                if (sent.PausedByUser)
                {
                    return held.Apply(Finish(new ScrollResult(false, x, y, deltaY, deltaX, PausedMessage(NothingScrolled), InputErrorCode.PausedByUser), approved));
                }

                if (!sent.Complete)
                {
                    return held.Apply(Finish(new ScrollResult(false, x, y, deltaY, deltaX,
                        $"SendInput inserted {sent.Inserted} of {sent.Requested} events (the move to the point, then one per notch), so " +
                        $"{Math.Max(0, sent.Inserted - 1)} of {Math.Abs(deltaY) + Math.Abs(deltaX)} notches were scrolled. Check the window " +
                        "before scrolling again.", InputErrorCode.SendInputFailed), approved));
                }

                if (PointerAwayAfterBatch(x, y) is { } at)
                {
                    return held.Apply(Finish(new ScrollResult(false, x, y, deltaY, deltaX, PointerClamped("The scroll", x, y, at),
                        InputErrorCode.CursorClamped), approved));
                }

                return held.Apply(Finish(new ScrollResult(true, x, y, deltaY, deltaX), approved));
            }
            catch (OperationCanceledException) when (call.IsPaused)
            {
                var paused = new ScrollResult(false, x, y, deltaY, deltaX, PausedMessage(NothingScrolled), InputErrorCode.PausedByUser);
                return held.Apply(approved == null ? paused : Finish(paused, approved));
            }
            finally
            {
                _session.ReleaseHeldInput("the end of the input call");
            }
        }
    }

    /// <summary>
    /// Null when the wheel goes to the window at the point. Windows sends it there when "Scroll inactive windows when I
    /// hover over them" is on (the default); otherwise, or when the setting cannot be read, it goes to the window with the
    /// keyboard focus, so the window at the point must be the foreground window. Otherwise the WheelFollowsFocus message.
    /// </summary>
    private string? WheelGoesElsewhere(InputGuardResult approved)
    {
        var routing = _systemInfo?.GetMouseWheelRouting();
        if (routing == MouseWheelRouting.MousePosition)
        {
            return null;
        }

        var foreground = _guard.DescribeForeground();
        if (foreground != null && foreground.Handle == (long)approved.Receiving)
        {
            return null;
        }

        var setting = routing == null
            ? "Windows' wheel setting cannot be read, so the wheel may go to the window with the keyboard focus"
            : "\"Scroll inactive windows when I hover over them\" is off in Windows' mouse settings, so the wheel goes to the " +
              "window with the keyboard focus";
        return $"Nothing was scrolled: {setting} ({Describe(foreground)}), not to the window at the point " +
               $"({Describe(approved.ReceivingWindow)}). Bring that window to the front with focus_window first, then scroll again.";
    }

    public (int X, int Y)? GetCursorPosition()
    {
        return _inputWrapper.GetCursorPos();
    }

    public bool IsValidScreenCoordinate(int x, int y)
    {
        var monitors = _monitorService.GetAllMonitors();
        return monitors.Any(m =>
            x >= m.Bounds.X && x < m.Bounds.X + m.Bounds.Width &&
            y >= m.Bounds.Y && y < m.Bounds.Y + m.Bounds.Height);
    }

    public async Task<TypeTextResult> TypeTextAsync(string text, int delayBetweenKeysMs = 0, InputTarget target = default,
        bool releaseHeldModifiers = false, TextControlChars? controlChars = null, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(text))
        {
            return new TypeTextResult(
                Success: false,
                Text: text ?? "",
                CharactersTyped: 0,
                Error: "Text cannot be empty",
                ErrorCode: InputErrorCode.EmptyText);
        }

        // charactersTyped counts this text: a Windows line break is one character, as it is one key press
        var typed = NormalizeLineBreaks(text);
        if (controlChars == null && typed.AsSpan().IndexOfAny('\n', '\t') >= 0)
        {
            return new TypeTextResult(false, text, 0, ControlCharsNeededMessage, InputErrorCode.InvalidArguments);
        }

        var mode = controlChars ?? TextControlChars.Unicode;
        var layout = mode == TextControlChars.Unicode ? KeyboardLayout.Invariant : LayoutFor(target);

        // The text goes out in several batches, each checked on its own: a copy (ETX, CAN) in one and the paste (SYN) in a
        // later one would paste what the copy took unchecked, so the order is checked on the whole text first
        if (_pasteGuard != null && ClipboardPasteGuard.CopyThenPasteRefusal(TextInputs(typed, mode, layout), receivingClass: null,
                "Nothing was typed.") is { } copyThenPaste)
        {
            return new TypeTextResult(false, text, 0, copyThenPaste, InputErrorCode.PasteNotAllowed);
        }

        delayBetweenKeysMs = Math.Clamp(delayBetweenKeysMs, 0, 100);
        var charactersTyped = 0;

        using var call = new InputCall(_session, cancellationToken);
        if (call.IsPaused)
        {
            return new TypeTextResult(false, text, 0, PausedMessage("Nothing was typed."), InputErrorCode.PausedByUser);
        }

        IDisposable? gate;
        try
        {
            gate = await InputGate.TryEnterAsync(_gateTimeout, call.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (call.IsPaused)
        {
            return new TypeTextResult(false, text, 0, PausedMessage("Nothing was typed."), InputErrorCode.PausedByUser);
        }

        if (gate == null)
        {
            return new TypeTextResult(false, text, 0, BusyMessage("typed"), InputErrorCode.InputBusy);
        }

        using (gate)
        {
            InputGuardResult? approved = null;
            HeldCheck held = default;
            try
            {
                held = CheckHeldInput(releaseHeldModifiers, "typed");
                if (!held.Ok)
                {
                    return held.Apply(new TypeTextResult(false, text, 0, held.Error, held.Code));
                }

                approved = _guard.CheckKeyboard(target, KeyboardInputKind.Text, keys: null);
                if (!approved.Allowed)
                {
                    return call.IsPaused
                        ? held.Apply(Finish(new TypeTextResult(false, text, 0, PausedMessage("Nothing was typed."), InputErrorCode.PausedByUser), approved))
                        : held.Apply(Finish(new TypeTextResult(false, text, 0, approved.Error, approved.ErrorCode), approved));
                }

                foreach (var batch in Batches(typed, delayBetweenKeysMs == 0 ? TypeTextBatchSize : 1))
                {
                    if (charactersTyped > 0 && delayBetweenKeysMs > 0)
                    {
                        await Task.Delay(delayBetweenKeysMs, call.Token).ConfigureAwait(false);
                    }

                    call.Token.ThrowIfCancellationRequested();

                    // Right before every batch, the first one too: still the same window, or a dialog of the target?
                    var recheck = _guard.RecheckKeyboard(approved);
                    if (!recheck.Allowed)
                    {
                        return held.Apply(Finish(new TypeTextResult(false, text, charactersTyped,
                            $"{TypedSoFar(typed, charactersTyped, approved)} {recheck.Error}", recheck.ErrorCode), recheck));
                    }

                    approved = recheck;

                    // A modifier going down now is the user taking the keyboard (or starting the pause chord)
                    if (ModifiersDownNow("typed") is { } downNow)
                    {
                        return held.Apply(Finish(new TypeTextResult(false, text, charactersTyped,
                            $"{TypedSoFar(typed, charactersTyped, approved)} {downNow.Error}", InputErrorCode.ModifiersHeld)
                        {
                            HeldModifiers = downNow.Names
                        }, approved));
                    }

                    var inputs = TextInputs(batch, mode, layout);

                    // The SYN character pastes in an edit control, as Ctrl+V does
                    if (PasteRefusal(inputs, approved) is { } pasteRefused)
                    {
                        return held.Apply(Finish(new TypeTextResult(false, text, charactersTyped,
                            $"{TypedSoFar(typed, charactersTyped, approved)} {pasteRefused}", InputErrorCode.PasteNotAllowed), approved));
                    }

                    var sent = _session.Send(inputs);
                    if (sent.PausedByUser)
                    {
                        return held.Apply(Finish(new TypeTextResult(false, text, charactersTyped,
                            PausedMessage(TypedSoFar(typed, charactersTyped, approved)), InputErrorCode.PausedByUser), approved));
                    }

                    if (!sent.Complete)
                    {
                        // A character counts once all its events were inserted; the session has already sent the key-ups
                        // of whatever went down alone
                        charactersTyped += CharactersWithin(batch, mode, sent.Inserted);
                        return held.Apply(Finish(new TypeTextResult(false, text, charactersTyped,
                            $"SendInput failed at character {charactersTyped}", InputErrorCode.SendInputFailed), approved));
                    }

                    charactersTyped += batch.Length;
                }

                return held.Apply(Finish(new TypeTextResult(Success: true, Text: text, CharactersTyped: charactersTyped), approved));
            }
            catch (OperationCanceledException) when (call.IsPaused)
            {
                var paused = new TypeTextResult(false, text, charactersTyped,
                    PausedMessage(TypedSoFar(typed, charactersTyped, approved)), InputErrorCode.PausedByUser);
                return held.Apply(approved == null ? paused : Finish(paused, approved));
            }
            finally
            {
                _session.ReleaseHeldInput("the end of the input call");
            }
        }
    }

    /// <summary>type_text's refusal of text with a line break or a tab and no controlChars (InvalidArguments).</summary>
    public const string ControlCharsNeededMessage =
        "The text contains a line break or a tab: pass controlChars to say what they do. 'unicode' types them as characters " +
        "(most editors insert them; no Enter or Tab key is pressed), 'keys' presses Enter and Tab (which can send a chat " +
        "message, submit a form or move to the next field or cell), 'shiftEnter' presses Shift+Enter for a line break (a new " +
        "line in chat apps) and types tabs as characters. Nothing was typed.";

    public async Task<SendKeysResult> SendKeysAsync(string keys, InputTarget target = default, bool releaseHeldModifiers = false,
        int delayMs = 0, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(keys))
        {
            return new SendKeysResult(
                Success: false,
                Keys: keys ?? "",
                Error: "Keys cannot be empty",
                ErrorCode: InputErrorCode.EmptyText);
        }

        KeySpec spec;
        try
        {
            spec = KeySpecParser.Parse(keys, LayoutFor(target));
        }
        catch (KeySyntaxException ex)
        {
            return new SendKeysResult(
                Success: false,
                Keys: keys,
                Error: ex.Message,
                ErrorCode: InputErrorCode.InvalidKeySpecification);
        }
        catch (ArgumentException ex)
        {
            return new SendKeysResult(
                Success: false,
                Keys: keys,
                Error: ex.Message,
                ErrorCode: InputErrorCode.UnknownKey);
        }

        var inputs = spec.Events;
        if (inputs.Count == 0)
        {
            return new SendKeysResult(
                Success: false,
                Keys: keys,
                Error: "No valid keys parsed from input",
                ErrorCode: InputErrorCode.InvalidKeySpecification);
        }

        return await SendStepsAsync(keys, spec, target, releaseHeldModifiers, Math.Clamp(delayMs, 0, MaxSendKeysDelayMs), holdMs: 0,
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Sends the steps of send_keys or press_keys (roadmap M4.1, M4.3), after the pause chord, the held input and the guard
    /// (destructive chords, keys after a focus change) have checked all of them. Without <paramref name="delayMs"/> and
    /// <paramref name="holdMs"/> every step goes in one SendInput call, so no other input can come between them; otherwise
    /// each step is a batch of its own, checked again right before it, with <paramref name="delayMs"/> before every step but
    /// the first. With <paramref name="holdMs"/> a step's leading key-downs go first, and its key-ups follow that much later
    /// without a check of their own (they only release what went down; the session releases them anyway if the call stops
    /// meanwhile).
    /// </summary>
    private async Task<SendKeysResult> SendStepsAsync(string keys, KeySpec spec, InputTarget target, bool releaseHeldModifiers,
        int delayMs, int holdMs, CancellationToken cancellationToken)
    {
        var inputs = spec.Events;

        // The agent must never pause or resume itself: the chord would reach the hotkey like a real key press
        if (_session.FindPauseChord(inputs) is { } pauseChord)
        {
            return new SendKeysResult(false, keys,
                $"The keys press {pauseChord}, the user's pause hotkey, which pauses and resumes this server's input. Only the user " +
                "may press it. Nothing was sent.", InputErrorCode.PauseChordNotAllowed);
        }

        var batches = KeyBatches(spec, delayMs, holdMs);
        var oneBatch = delayMs == 0 && holdMs == 0;
        var total = spec.Steps.Count;
        var keysSent = 0;

        using var call = new InputCall(_session, cancellationToken);
        if (call.IsPaused)
        {
            return new SendKeysResult(false, keys, PausedMessage("Nothing was sent."), InputErrorCode.PausedByUser);
        }

        IDisposable? gate;
        try
        {
            gate = await InputGate.TryEnterAsync(_gateTimeout, call.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (call.IsPaused)
        {
            return new SendKeysResult(false, keys, PausedMessage("Nothing was sent."), InputErrorCode.PausedByUser);
        }

        if (gate == null)
        {
            return new SendKeysResult(false, keys, BusyMessage("sent"), InputErrorCode.InputBusy);
        }

        using (gate)
        {
            InputGuardResult? approved = null;
            HeldCheck held = default;
            try
            {
                held = CheckHeldInput(releaseHeldModifiers, "sent");
                if (!held.Ok)
                {
                    return held.Apply(new SendKeysResult(false, keys, held.Error, held.Code));
                }

                approved = _guard.CheckKeyboard(target, KeyboardInputKind.Keys, inputs);
                if (!approved.Allowed)
                {
                    return call.IsPaused
                        ? held.Apply(Finish(new SendKeysResult(false, keys, PausedMessage("Nothing was sent."), InputErrorCode.PausedByUser), approved))
                        : held.Apply(Finish(new SendKeysResult(false, keys, approved.Error, approved.ErrorCode), approved));
                }

                for (var b = 0; b < batches.Count; b++)
                {
                    var (batch, wait, release, endsStep) = batches[b];
                    if (wait > 0)
                    {
                        await Task.Delay(wait, call.Token).ConfigureAwait(false);
                    }

                    call.Token.ThrowIfCancellationRequested();

                    // A release batch only lets go of what the step before it pressed
                    if (!release)
                    {
                        // Right before every batch: the window in front may have changed while the guard read it, or since the
                        // last batch
                        var recheck = _guard.RecheckKeyboard(approved);
                        if (!recheck.Allowed)
                        {
                            return held.Apply(Finish(new SendKeysResult(false, keys, $"{KeysSentSoFar(keysSent, total, approved)} {recheck.Error}",
                                recheck.ErrorCode) { KeysSent = keysSent }, recheck));
                        }

                        approved = recheck;

                        // Again right before the keys: a modifier the user pressed while the guard activated the target would
                        // turn them into shortcuts, and the keys' own modifier key-ups would release it. A modifier an earlier
                        // batch of these keys holds down ({SHIFT down}) is the server's own.
                        if (ModifiersDownNow("sent", ignoreServerHeld: b > 0) is { } downNow)
                        {
                            return held.Apply(Finish(new SendKeysResult(false, keys,
                                b == 0 ? downNow.Error : $"{KeysSentSoFar(keysSent, total, approved)} {downNow.Error}", InputErrorCode.ModifiersHeld)
                            {
                                HeldModifiers = downNow.Names,
                                KeysSent = keysSent
                            }, approved));
                        }

                        // Right before the keys: a paste must not put the user's clipboard where the agent can read it. The first
                        // check reads the whole call, so a copy in one batch and its paste in a later one are refused together
                        if (PasteRefusal(b == 0 ? inputs : batch, approved) is { } pasteRefused)
                        {
                            return held.Apply(Finish(new SendKeysResult(false, keys,
                                b == 0 ? pasteRefused : $"{KeysSentSoFar(keysSent, total, approved)} {pasteRefused}", InputErrorCode.PasteNotAllowed)
                            {
                                KeysSent = keysSent
                            }, approved));
                        }
                    }

                    var sent = Send(batch, approved);
                    if (sent.PausedByUser)
                    {
                        return held.Apply(Finish(new SendKeysResult(false, keys, PausedMessage(KeysSentSoFar(keysSent, total, approved)),
                            InputErrorCode.PausedByUser) { KeysSent = keysSent }, approved));
                    }

                    if (!sent.Complete)
                    {
                        if (oneBatch)
                        {
                            keysSent = StepsWithin(spec, sent.Inserted);
                        }

                        return held.Apply(Finish(new SendKeysResult(false, keys,
                            $"SendInput inserted only {sent.Inserted} of {sent.Requested} events, so {keysSent} of {total} keys were sent " +
                            "completely; the keys it had pressed were released again. Check the window before sending the rest.",
                            InputErrorCode.SendInputFailed) { KeysSent = keysSent }, approved));
                    }

                    if (endsStep)
                    {
                        keysSent = oneBatch ? total : keysSent + 1;
                    }
                }

                return held.Apply(Finish(new SendKeysResult(Success: true, Keys: keys) { KeysSent = keysSent }, approved));
            }
            catch (OperationCanceledException) when (call.IsPaused)
            {
                var paused = new SendKeysResult(false, keys, PausedMessage(KeysSentSoFar(keysSent, total, approved)), InputErrorCode.PausedByUser);
                return held.Apply(approved == null ? paused : Finish(paused with { KeysSent = keysSent }, approved));
            }
            finally
            {
                _session.ReleaseHeldInput("the end of the input call");
            }
        }
    }

    /// <summary>send_keys waits at most this long between keys (delayMs).</summary>
    public const int MaxSendKeysDelayMs = 500;

    /// <summary>press_keys holds a chord's keys down at most this long (holdMs).</summary>
    public const int MaxPressKeysHoldMs = 5000;

    /// <summary>press_keys waits at most this long between chords (delayBetweenMs).</summary>
    public const int MaxPressKeysDelayMs = 1000;

    /// <summary>
    /// The longest a press_keys call may take by its waits: presses x (holdMs + delayBetweenMs). The input gate is held
    /// throughout, so every other input and window call waits for it.
    /// </summary>
    public const int MaxPressKeysDurationMs = 10_000;

    public async Task<SendKeysResult> PressKeysAsync(KeyList keys, int repeat, int holdMs, int delayBetweenMs, InputTarget target = default,
        bool releaseHeldModifiers = false, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(keys);
        ArgumentOutOfRangeException.ThrowIfLessThan(repeat, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(repeat, PressKeysParser.MaxRepeat);
        ArgumentOutOfRangeException.ThrowIfLessThan(holdMs, 0);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(holdMs, MaxPressKeysHoldMs);
        ArgumentOutOfRangeException.ThrowIfLessThan(delayBetweenMs, 0);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(delayBetweenMs, MaxPressKeysDelayMs);

        KeySpec spec;
        try
        {
            spec = PressKeysParser.Parse(keys, repeat, LayoutFor(target));
        }
        catch (KeySyntaxException ex)
        {
            return new SendKeysResult(false, keys.Text, $"{ex.Message} Nothing was sent.", InputErrorCode.InvalidKeySpecification);
        }
        catch (ArgumentException ex) when (ex is not ArgumentOutOfRangeException)
        {
            return new SendKeysResult(false, keys.Text,
                $"{ex.Message}. Key names: ctrl, shift, alt, win, enter, tab, esc, space, backspace, delete, up, down, left, right, " +
                "home, end, pgup, pgdn, f1-f24, plus, comma, star, or a single character. Nothing was sent.", InputErrorCode.UnknownKey);
        }

        var duration = (long)spec.Steps.Count * (holdMs + delayBetweenMs);
        if (duration > MaxPressKeysDurationMs)
        {
            throw new ArgumentException(
                $"{spec.Steps.Count} presses x (holdMs {holdMs} + delayBetweenMs {delayBetweenMs}) is {duration} ms; one call takes at " +
                $"most {MaxPressKeysDurationMs} ms, because every other input call waits for it.", nameof(holdMs));
        }

        return await SendStepsAsync(keys.Text, spec, target, releaseHeldModifiers, delayBetweenMs, holdMs, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// The SendInput batches of <paramref name="spec"/>: all its steps in one without waits; otherwise each step, after
    /// <paramref name="delayMs"/> (not before the first), split with <paramref name="holdMs"/> into its leading key-downs and,
    /// that much later, the rest (a release batch, which <see cref="SendStepsAsync"/> does not check again).
    /// </summary>
    private static List<(IReadOnlyList<InputEvent> Events, int Wait, bool Release, bool EndsStep)> KeyBatches(KeySpec spec, int delayMs,
        int holdMs)
    {
        var batches = new List<(IReadOnlyList<InputEvent> Events, int Wait, bool Release, bool EndsStep)>();
        if (delayMs == 0 && holdMs == 0)
        {
            batches.Add((spec.Events, 0, false, true));
            return batches;
        }

        for (var i = 0; i < spec.Steps.Count; i++)
        {
            var step = spec.Steps[i];
            var wait = i == 0 ? 0 : delayMs;
            var downs = holdMs == 0 ? step.Count : step.TakeWhile(e => (e.Flags & NativeEnums.KEYEVENTF_KEYUP) == 0).Count();
            if (downs == step.Count || downs == 0)
            {
                batches.Add((step, wait, false, true));
                continue;
            }

            batches.Add((step.Take(downs).ToList(), wait, false, false));
            batches.Add((step.Skip(downs).ToList(), holdMs, true, true));
        }

        return batches;
    }

    /// <summary>
    /// The layout keys are mapped with: that of the target window's thread, or of the foreground window's when there is no
    /// target, with Caps Lock as it is now; the invariant layout when the server has no layout queries (tests).
    /// </summary>
    private KeyboardLayout LayoutFor(InputTarget target) => _keyboardLayout == null
        ? KeyboardLayout.Invariant
        : KeyboardLayout.Of(_keyboardLayout, target.WindowHandle ?? 0, _inputWrapper.IsKeyToggled(NativeEnums.VK_CAPITAL) == true);

    /// <summary>How many steps of <paramref name="spec"/> lie wholly within its first <paramref name="inserted"/> events.</summary>
    private static int StepsWithin(KeySpec spec, int inserted)
    {
        var steps = 0;
        foreach (var step in spec.Steps)
        {
            inserted -= step.Count;
            if (inserted < 0)
            {
                break;
            }

            steps++;
        }

        return steps;
    }

    private static string KeysSentSoFar(int keysSent, int total, InputGuardResult? approved) => keysSent == 0
        ? "Nothing was sent."
        : $"Sending stopped after {keysSent} of {total} keys, which went to {Describe(approved?.ReceivingWindow)}.";

    /// <summary>
    /// One, two or three clicks at a point: the guard checks the window at the point before the cursor moves, and again
    /// right before the press, since moving the cursor can open a popup there. The clicks are sent in one SendInput call
    /// together with a move to the checked point (<see cref="InputBatchBuilder"/>), so a user moving the mouse cannot make
    /// them land elsewhere (SendInput does not interleave other input with the events of one call), and a double click is
    /// one double click whatever the double-click time. <paramref name="modifiers"/> go down before the move and up after
    /// the clicks, in the same batch (mouse_click, roadmap M4.3), so none stays down whatever happens to the call. A pointer
    /// that is not at the point afterwards is CursorClamped.
    /// </summary>
    private ClickResult ClickAt(int x, int y, InputTarget target, string clickType, MouseButton button, int clicks,
        IReadOnlyList<MouseModifier> modifiers, bool releaseHeldModifiers, CancellationToken cancellationToken)
    {
        const string NothingClicked = "Nothing was clicked: ";

        if (!IsValidScreenCoordinate(x, y))
        {
            return new ClickResult(
                Success: false,
                X: x,
                Y: y,
                ClickType: clickType,
                Error: "Coordinates are outside screen bounds",
                ErrorCode: InputErrorCode.CoordinatesOutOfBounds);
        }

        using var call = new InputCall(_session, cancellationToken);
        if (call.IsPaused)
        {
            return new ClickResult(false, x, y, clickType, PausedMessage("Nothing was clicked."), InputErrorCode.PausedByUser);
        }

        IDisposable? gate;
        try
        {
            gate = InputGate.TryEnter(_gateTimeout, call.Token);
        }
        catch (OperationCanceledException) when (call.IsPaused)
        {
            return new ClickResult(false, x, y, clickType, PausedMessage("Nothing was clicked."), InputErrorCode.PausedByUser);
        }

        if (gate == null)
        {
            return new ClickResult(false, x, y, clickType, BusyMessage("clicked"), InputErrorCode.InputBusy);
        }

        using (gate)
        {
            InputGuardResult? approved = null;
            HeldCheck held = default;
            try
            {
                held = CheckHeldInput(releaseHeldModifiers, "clicked");
                if (!held.Ok)
                {
                    return held.Apply(new ClickResult(false, x, y, clickType, held.Error, held.Code));
                }

                approved = _guard.CheckMouse(x, y, target);
                if (!approved.Allowed)
                {
                    return call.IsPaused
                        ? held.Apply(Finish(new ClickResult(false, x, y, clickType, PausedMessage("Nothing was clicked."), InputErrorCode.PausedByUser), approved))
                        : held.Apply(Finish(new ClickResult(false, x, y, clickType, approved.Error, approved.ErrorCode), approved));
                }

                if (MouseButtonsDownNow("clicked") is { } buttons)
                {
                    return held.Apply(Finish(new ClickResult(false, x, y, clickType, buttons, InputErrorCode.MouseInUse), approved));
                }

                switch (_session.SetCursorPos(x, y))
                {
                    case null:
                        return held.Apply(Finish(new ClickResult(false, x, y, clickType, PausedMessage("Nothing was clicked."), InputErrorCode.PausedByUser), approved));
                    case false:
                        return held.Apply(Finish(new ClickResult(false, x, y, clickType,
                            CursorPositionFailedMessage("Nothing was clicked."), InputErrorCode.CursorPositionFailed), approved));
                }

                // Small delay so that a popup the pointer opens there (a hover menu or tooltip) shows up in the re-check
                Wait(10, call);

                var recheck = _guard.RecheckMouse(x, y, approved);
                if (!recheck.Allowed)
                {
                    return held.Apply(Finish(new ClickResult(false, x, y, clickType, NothingClicked + recheck.Error, recheck.ErrorCode), recheck));
                }

                approved = recheck;
                switch (PointerIsAt(x, y, out var actual))
                {
                    case PointerCheck.Paused:
                        return held.Apply(Finish(new ClickResult(false, x, y, clickType, PausedMessage("Nothing was clicked."),
                            InputErrorCode.PausedByUser), approved));
                    case PointerCheck.Away:
                        return held.Apply(Finish(new ClickResult(false, x, y, clickType, NothingClicked + PointerMovedAway(x, y, actual),
                            InputErrorCode.CursorNotAtPoint), approved));
                }

                // Right before the press: a modifier the user pressed meanwhile would make it a Ctrl- or Shift-click
                if (ModifiersDownNow("clicked") is { } downNow)
                {
                    return held.Apply(Finish(new ClickResult(false, x, y, clickType, NothingClicked + downNow.Error, InputErrorCode.ModifiersHeld)
                    {
                        HeldModifiers = downNow.Names
                    }, approved));
                }

                // And a button the user pressed meanwhile would send the press to the window it went down on
                if (MouseButtonsDownNow("clicked") is { } buttonsNow)
                {
                    return held.Apply(Finish(new ClickResult(false, x, y, clickType, NothingClicked + buttonsNow, InputErrorCode.MouseInUse), approved));
                }

                var batch = NewBatch().ModifiersDown(modifiers).MoveTo(x, y).Click(button, clicks).ModifiersUp(modifiers).Build();

                // A right or middle click in a terminal pastes
                if (button is MouseButton.Secondary or MouseButton.Middle && PasteRefusal(batch.AsUnswapped, approved) is { } pasteRefused)
                {
                    return held.Apply(Finish(new ClickResult(false, x, y, clickType, NothingClicked + pasteRefused, InputErrorCode.PasteNotAllowed), approved));
                }

                var sent = Send(batch, approved);
                if (sent.PausedByUser)
                {
                    return held.Apply(Finish(new ClickResult(false, x, y, clickType, PausedMessage("Nothing was clicked."),
                        InputErrorCode.PausedByUser), approved));
                }

                if (!sent.Complete)
                {
                    return held.Apply(Finish(new ClickResult(false, x, y, clickType, ClickBatchCutShort(sent, clicks, modifiers.Count),
                        InputErrorCode.SendInputFailed), approved));
                }

                if (PointerAwayAfterBatch(x, y) is { } at)
                {
                    var what = clicks switch { 1 => "The click", 2 => "The double click", _ => "The triple click" };
                    return held.Apply(Finish(new ClickResult(false, x, y, clickType, PointerClamped(what, x, y, at), InputErrorCode.CursorClamped),
                        approved));
                }

                return held.Apply(Finish(new ClickResult(Success: true, X: x, Y: y, ClickType: clickType), approved));
            }
            catch (OperationCanceledException) when (call.IsPaused)
            {
                // Only the waits before the batch are cancelled, so nothing was clicked
                var paused = new ClickResult(false, x, y, clickType, PausedMessage("Nothing was clicked."), InputErrorCode.PausedByUser);
                return held.Apply(approved == null ? paused : Finish(paused, approved));
            }
            finally
            {
                _session.ReleaseHeldInput("the end of the input call");
            }
        }
    }

    /// <summary>
    /// The SendInputFailed message of a click batch SendInput inserted only part of: how many of its clicks went in. The
    /// session has already released a button whose press went in without its release, and the modifier keys that went down.
    /// </summary>
    private static string ClickBatchCutShort(InputSendOutcome sent, int clicks, int modifiers)
    {
        var buttonEvents = Math.Clamp(sent.Inserted - modifiers - 1, 0, 2 * clicks);
        var done = buttonEvents / 2;
        var clicked = clicks == 1
            ? done == 0 ? "the click was not sent" : "the click went in"
            : $"{done} of its {clicks} clicks went in";
        var halfway = buttonEvents % 2 == 1 ? ", and the press after them went in without its release, which was sent at once" : string.Empty;
        var events = modifiers == 0
            ? "the move to the point, then a press and a release for each click"
            : "the modifier keys, the move to the point, a press and a release for each click, then the modifier keys' release";
        return $"SendInput inserted {sent.Inserted} of {sent.Requested} events ({events}), so {clicked}{halfway}. Check the window " +
               "before clicking again.";
    }

    /// <summary>
    /// A new batch for the desktop as it is now: Windows' virtual screen and whether the user swapped the mouse buttons, read
    /// for every batch, since the user can change either at any time. Without the system reads (tests), the monitors'
    /// bounding rectangle and unswapped buttons.
    /// </summary>
    private InputBatchBuilder NewBatch() =>
        new(_systemInfo?.GetVirtualScreen() is { Width: > 0, Height: > 0 } screen ? screen : InputBatchBuilder.VirtualScreenOf(_monitorService.GetAllMonitors()),
            _systemInfo?.AreMouseButtonsSwapped() ?? false);

    /// <summary>
    /// Where the pointer is after a batch that moved it to (x, y), when that is not the point; null when it is there, or its
    /// position cannot be read. Windows can move the pointer after SendInput has returned, so a pointer elsewhere is read
    /// again until <see cref="PointerSettleTime"/> has passed. Nothing here throws on a pause or cancellation: the batch has
    /// been sent, and the result must say so.
    /// </summary>
    private (int X, int Y)? PointerAwayAfterBatch(int x, int y)
    {
        var deadline = Environment.TickCount64 + (long)PointerSettleTime.TotalMilliseconds;
        while (true)
        {
            if (_inputWrapper.GetCursorPos() is not { } at || at == (x, y))
            {
                return null;
            }

            if (Environment.TickCount64 >= deadline)
            {
                return at;
            }

            Thread.Sleep(5);
        }
    }

    /// <summary>The CursorClamped message: <paramref name="what"/> was sent, but the pointer is at <paramref name="at"/>.</summary>
    private static string PointerClamped(string what, int x, int y, (int X, int Y) at) =>
        $"{what} was sent, but afterwards the mouse pointer is at ({at.X}, {at.Y}), not at ({x}, {y}): Windows kept it off the point " +
        "(it is confined to a window, as games and remote sessions do, or the point is on no monitor), or the user or the " +
        $"application moved it at once. The input may have landed at ({at.X}, {at.Y}) instead. Look at the window (a capture or " +
        "get_ui_elements) before you send it again; do not simply retry.";

    /// <summary>
    /// Why <paramref name="batch"/> must not be sent because it pastes content get_clipboard would not return (see
    /// <see cref="IClipboardPasteGuard"/>); null when it may be sent.
    /// </summary>
    private string? PasteRefusal(IReadOnlyList<InputEvent> batch, InputGuardResult approved) =>
        _pasteGuard?.Check(batch, approved.Receiving);

    /// <summary>
    /// Sends a batch through the session. A batch that can copy (copy keys, a left-button press) is reported to the
    /// clipboard origin with the process of the window it goes to, so get_clipboard can tell the copy that application
    /// makes from one the user makes.
    /// </summary>
    private InputSendOutcome Send(IReadOnlyList<InputEvent> batch, InputGuardResult approved) => Send(batch, batch, approved);

    /// <summary>
    /// Sends a mouse batch as <see cref="Send(IReadOnlyList{InputEvent}, InputGuardResult)"/> does; the clipboard origin is
    /// told about its buttons as the user knows them (a primary press copies whichever physical button it is).
    /// </summary>
    private InputSendOutcome Send(MouseBatch batch, InputGuardResult approved) => Send(batch.Events, batch.AsUnswapped, approved);

    private InputSendOutcome Send(IReadOnlyList<InputEvent> batch, IReadOnlyList<InputEvent> asUnswapped, InputGuardResult approved)
    {
        var copy = _clipboardOrigin?.BeforeInput(asUnswapped, approved.ReceivingWindow?.ProcessId ?? 0, approved.Receiving, approved.Anchor);
        var sent = _session.Send(batch);
        if (copy != null && sent.Inserted > 0)
        {
            _clipboardOrigin!.InputSent(copy);
        }

        return sent;
    }

    /// <summary>
    /// At when the pointer is at (x, y), or its position cannot be read. Otherwise it places the pointer there
    /// once more and looks again at once: a pointer the user moved is then back, but one confined to a window
    /// (ClipCursor, as games and remote sessions do) is not (Away), and a click would land where the guard did
    /// not look. Paused when the user paused input meanwhile, so the pointer was not placed again.
    /// </summary>
    private PointerCheck PointerIsAt(int x, int y, out (int X, int Y)? actual)
    {
        actual = _inputWrapper.GetCursorPos();
        if (actual is not { } first || first == (x, y))
        {
            return PointerCheck.At;
        }

        if (_session.SetCursorPos(x, y) == null)
        {
            return PointerCheck.Paused;
        }

        actual = _inputWrapper.GetCursorPos();
        return actual is not { } second || second == (x, y) ? PointerCheck.At : PointerCheck.Away;
    }

    private enum PointerCheck
    {
        At,
        Away,
        Paused
    }

    /// <summary>
    /// The modifier keys that are down right before a batch, with the ModifiersHeld message; null when none is. With
    /// <paramref name="ignoreServerHeld"/>, modifiers of the kinds the server itself holds down are left out.
    /// </summary>
    private (IReadOnlyList<string> Names, string Error)? ModifiersDownNow(string action, bool ignoreServerHeld = false)
    {
        var down = _session.ReadModifiersDown();
        if (ignoreServerHeld)
        {
            // A modifier of the kind the server holds down is taken for the server's own
            var own = _session.ServerHeld.Where(h => h.IsModifier).Select(h => SendKeysParser.ModifierOf(h.Code)).ToList();
            down = down.Where(key => !own.Contains(SendKeysParser.ModifierOf(key.Code))).ToList();
        }

        if (down.Count == 0)
        {
            return null;
        }

        var names = down.Select(k => k.Name).ToList();
        return (names, ModifiersDownMessage(names, action, _session.IsPauseChordHeld(down)));
    }

    /// <summary>The mouse buttons, by their key-state codes (VK_LBUTTON, VK_RBUTTON, VK_MBUTTON, VK_XBUTTON1, VK_XBUTTON2).</summary>
    private static readonly (ushort VirtualKey, string Name)[] MouseButtons =
    [
        (0x01, "left mouse button"), (0x02, "right mouse button"), (0x04, "middle mouse button"),
        (0x05, "X1 mouse button"), (0x06, "X2 mouse button")
    ];

    /// <summary>
    /// The MouseInUse message when Windows reports a mouse button down right before the pointer is placed or a
    /// button is pressed, or null when none is. The server releases its own buttons at the end of every call, so
    /// a button that is down is the user's (dragging or selecting) or stuck: the mouse input would go to the window
    /// the button went down on, not to the window the guard checked, and moving the pointer would move the drag.
    /// </summary>
    private string? MouseButtonsDownNow(string action)
    {
        var down = MouseButtons.Where(button => _inputWrapper.IsKeyDown(button.VirtualKey)).Select(button => button.Name).ToList();
        return down.Count == 0
            ? null
            : $"The {string.Join(" and the ", down)} {(down.Count == 1 ? "is" : "are")} held down: the user is dragging or selecting, " +
              $"so mouse input would go to the window under that press, not to the window the guard checked. Nothing (more) was {action}. " +
              "Wait until the user has finished, then try again.";
    }

    private static string PointerMovedAway(int x, int y, (int X, int Y)? actual) =>
        $"The mouse pointer does not stay at ({x}, {y}){(actual is { } at ? $"; it is at ({at.X}, {at.Y})" : string.Empty)}. " +
        "It may be confined to a window (a game or a remote session that captures the mouse, or a drag in progress), or the " +
        "user is moving the mouse, so the input would land where the guard did not check. Try again, or ask the user.";

    /// <summary>
    /// Before any input: releases keys the server itself left down earlier (refusing when that fails), then
    /// checks for modifier keys that are down. Those are the user's or stuck, so the call is refused
    /// (ModifiersHeld) unless the agent asked to release them; the user's keys are never released silently.
    /// </summary>
    private HeldCheck CheckHeldInput(bool releaseHeldModifiers, string action)
    {
        var leftover = _session.ReleaseHeldInput("before the next input call");
        if (!leftover.Complete)
        {
            return HeldCheck.Refuse(InputErrorCode.ModifiersHeld,
                $"Keys the server pressed earlier are still down and could not be released ({string.Join(", ", leftover.StillHeld)}), so " +
                $"nothing was {action}. Call release_all_input, or ask the user to press and release them.", leftover.StillHeld);
        }

        var down = _session.ReadModifiersDown();
        if (down.Count == 0)
        {
            return HeldCheck.None;
        }

        var names = down.Select(k => k.Name).ToList();
        var pauseChord = _session.IsPauseChordHeld(down);
        if (!releaseHeldModifiers || pauseChord)
        {
            // While any modifier of the pause hotkey is down, nothing is released for the agent: the user may be
            // pressing it, one modifier after another
            return HeldCheck.Refuse(InputErrorCode.ModifiersHeld, ModifiersDownMessage(names, action, pauseChord), names);
        }

        var release = _session.ReleaseModifiers(down, "releaseHeldModifiers");
        if (release.PausedByUser)
        {
            return HeldCheck.Refuse(InputErrorCode.PausedByUser, PausedMessage($"Nothing was {action}."), names);
        }

        return release.Complete
            ? new HeldCheck(InputErrorCode.None, null, null, release.Released)
            : HeldCheck.Refuse(InputErrorCode.ModifiersHeld,
                $"{string.Join(", ", release.StillHeld)} could not be released, so nothing was {action}. Ask the user to press and release the key.",
                release.StillHeld);
    }

    private string ModifiersDownMessage(IReadOnlyList<string> names, string action, bool pauseChord)
    {
        var one = names.Count == 1;
        var them = one ? "it" : "them";
        var held = $"{string.Join(", ", names)} {(one ? "is" : "are")} held down";
        return pauseChord
            ? $"{held}, and the user's pause hotkey ({_session.PauseChord}) uses {(one ? "it" : "at least one of them")}, so nothing " +
              $"(more) was {action}. The user may be about to pause input, so {(one ? "it is" : "they are")} not released, " +
              $"not even with releaseHeldModifiers. Wait, and ask the user if {(one ? "it stays" : "they stay")} down."
            : $"{held}, by the user or stuck, so nothing (more) was {action}: with a modifier down, keys become shortcuts and clicks " +
              $"become Ctrl- or Shift-clicks. If the user is using the keyboard, wait and try again. If nobody is pressing {them}, " +
              $"{ReleaseHeldModifiersAdvice} to release {them}.";
    }

    private static string CursorPositionFailedMessage(string done) =>
        $"Windows did not move the mouse pointer to the point (SetCursorPos failed). {done} The input desktop may not be " +
        "reachable (a UAC prompt, the lock screen or a disconnected session). Try again; if it keeps failing, ask the user.";

    private string PausedMessage(string done) =>
        $"{done} Input is paused: the user pressed the pause hotkey{(_session.PauseChord is { } chord ? $" ({chord})" : string.Empty)}. " +
        "Stop and ask the user how to continue. Do not try to resume input yourself: only the user can, with the hotkey or by " +
        "confirming resume_input.";

    private string BusyMessage(string action) =>
        $"Another input or window call held the input gate for {_gateTimeout.TotalSeconds:0.#} s, so nothing was {action}. Try again " +
        "when it has finished.";

    private static string TypedSoFar(string text, int charactersTyped, InputGuardResult? approved) => charactersTyped == 0
        ? "Nothing was typed."
        : $"Typing stopped after {charactersTyped} of {text.Length} characters, which went to {Describe(approved?.ReceivingWindow)}.";

    /// <summary>Waits <paramref name="milliseconds"/>, or less when the call is cancelled or paused (then throws).</summary>
    private static void Wait(int milliseconds, InputCall call)
    {
        if (call.Token.WaitHandle.WaitOne(milliseconds))
        {
            call.Token.ThrowIfCancellationRequested();
        }
    }

    /// <summary>Adds where the input went, what is in front now and where the cursor is.</summary>
    private ClickResult Finish(ClickResult result, InputGuardResult guard)
    {
        var cursor = _inputWrapper.GetCursorPos();
        return result with
        {
            ReceivingWindow = guard.ReceivingWindow,
            BlockingWindow = guard.BlockingWindow,
            ForegroundAfter = _guard.DescribeForeground(),
            ActualX = cursor?.X,
            ActualY = cursor?.Y
        };
    }

    private MouseMoveResult Finish(MouseMoveResult result, InputGuardResult guard)
    {
        var cursor = _inputWrapper.GetCursorPos();
        return result with
        {
            ReceivingWindow = guard.ReceivingWindow,
            BlockingWindow = guard.BlockingWindow,
            ForegroundAfter = _guard.DescribeForeground(),
            ActualX = cursor?.X,
            ActualY = cursor?.Y
        };
    }

    private ScrollResult Finish(ScrollResult result, InputGuardResult guard)
    {
        var cursor = _inputWrapper.GetCursorPos();
        return result with
        {
            ReceivingWindow = guard.ReceivingWindow,
            BlockingWindow = guard.BlockingWindow,
            ForegroundAfter = _guard.DescribeForeground(),
            ActualX = cursor?.X,
            ActualY = cursor?.Y
        };
    }

    private TypeTextResult Finish(TypeTextResult result, InputGuardResult guard) => result with
    {
        ReceivingWindow = guard.ReceivingWindow,
        BlockingWindow = guard.BlockingWindow,
        ForegroundAfter = _guard.DescribeForeground()
    };

    private SendKeysResult Finish(SendKeysResult result, InputGuardResult guard) => result with
    {
        ReceivingWindow = guard.ReceivingWindow,
        BlockingWindow = guard.BlockingWindow,
        ForegroundAfter = _guard.DescribeForeground()
    };

    /// <summary>
    /// Splits <paramref name="text"/> into batches of at most <paramref name="size"/> UTF-16 code units,
    /// never between the two halves of a surrogate pair (an emoji, for example), so a batch that is not
    /// sent never leaves half a character typed.
    /// </summary>
    private static IEnumerable<string> Batches(string text, int size)
    {
        var start = 0;
        while (start < text.Length)
        {
            var length = Math.Min(size, text.Length - start);
            if (start + length < text.Length && char.IsHighSurrogate(text[start + length - 1]))
            {
                length++;
            }

            yield return text.Substring(start, length);
            start += length;
        }
    }

    /// <summary>The text with every Windows line break (\r\n) and every lone \r as \n.</summary>
    private static string NormalizeLineBreaks(string text) => text.Replace("\r\n", "\n").Replace('\r', '\n');

    /// <summary>
    /// The events that type <paramref name="text"/>: a key-down and key-up as Unicode for every UTF-16 code unit, except a
    /// line break or tab that <paramref name="mode"/> presses as a key.
    /// </summary>
    private static List<InputEvent> TextInputs(string text, TextControlChars mode, KeyboardLayout layout)
    {
        var inputs = new List<InputEvent>(text.Length * 2);
        foreach (var character in text)
        {
            switch (character)
            {
                case '\n' when mode == TextControlChars.Keys:
                    AddPress(NativeEnums.VK_RETURN);
                    break;
                case '\n' when mode == TextControlChars.ShiftEnter:
                    inputs.Add(KeyEvent(NativeEnums.VK_SHIFT, NativeEnums.KEYEVENTF_KEYDOWN, layout));
                    AddPress(NativeEnums.VK_RETURN);
                    inputs.Add(KeyEvent(NativeEnums.VK_SHIFT, NativeEnums.KEYEVENTF_KEYUP, layout));
                    break;
                case '\t' when mode == TextControlChars.Keys:
                    AddPress(NativeEnums.VK_TAB);
                    break;
                default:
                    inputs.Add(InputEvent.Unicode(character, NativeEnums.KEYEVENTF_KEYDOWN));
                    inputs.Add(InputEvent.Unicode(character, NativeEnums.KEYEVENTF_KEYUP));
                    break;
            }
        }

        return inputs;

        void AddPress(ushort virtualKey)
        {
            inputs.Add(KeyEvent(virtualKey, NativeEnums.KEYEVENTF_KEYDOWN, layout));
            inputs.Add(KeyEvent(virtualKey, NativeEnums.KEYEVENTF_KEYUP, layout));
        }
    }

    private static InputEvent KeyEvent(ushort virtualKey, uint flags, KeyboardLayout layout)
    {
        var (scan, extended) = layout.ScanOf(virtualKey);
        return InputEvent.Key(virtualKey, flags | (extended ? NativeEnums.KEYEVENTF_EXTENDEDKEY : 0), scan);
    }

    /// <summary>How many characters of <paramref name="batch"/> have all their events within the first <paramref name="inserted"/>.</summary>
    private static int CharactersWithin(string batch, TextControlChars mode, int inserted)
    {
        var characters = 0;
        foreach (var character in batch)
        {
            inserted -= character == '\n' && mode == TextControlChars.ShiftEnter ? 4 : 2;
            if (inserted < 0)
            {
                break;
            }

            characters++;
        }

        return characters;
    }

    private static string Describe(WindowSummary? window) => window switch
    {
        null => "no window",
        { Title.Length: > 0 } => $"'{window.Title}' ({window.ProcessName})",
        _ => $"an untitled window of {window.ProcessName}"
    };

    /// <summary>
    /// One input call's cancellation: the MCP request's token linked with the session's pause token, so either
    /// the client or the user's pause stops it between batches.
    /// </summary>
    private sealed class InputCall : IDisposable
    {
        private readonly IInputSession _session;
        private readonly CancellationToken _pauseToken;
        private readonly CancellationTokenSource _linked;

        public InputCall(IInputSession session, CancellationToken requestToken)
        {
            _session = session;
            _pauseToken = session.PauseToken;
            _linked = CancellationTokenSource.CreateLinkedTokenSource(requestToken, _pauseToken);
        }

        /// <summary>Cancelled when the request is cancelled or the user pauses input.</summary>
        public CancellationToken Token => _linked.Token;

        /// <summary>True when the user has paused input (since before or during this call).</summary>
        public bool IsPaused => _pauseToken.IsCancellationRequested || _session.IsPaused;

        public void Dispose() => _linked.Dispose();
    }

    /// <summary>The held-input check before a call's input, and the modifiers it reported or released.</summary>
    private readonly record struct HeldCheck(
        InputErrorCode Code,
        string? Error,
        IReadOnlyList<string>? Held,
        IReadOnlyList<string>? Released)
    {
        public static HeldCheck None => default;

        public bool Ok => Code == InputErrorCode.None;

        public static HeldCheck Refuse(InputErrorCode code, string error, IReadOnlyList<string> held) => new(code, error, held, null);

        // A result that found modifiers down later (right before a batch) keeps those
        public ClickResult Apply(ClickResult result) => result with { HeldModifiers = result.HeldModifiers ?? Held, ReleasedModifiers = Released };

        public MouseMoveResult Apply(MouseMoveResult result) => result with { HeldModifiers = result.HeldModifiers ?? Held, ReleasedModifiers = Released };

        public ScrollResult Apply(ScrollResult result) => result with { HeldModifiers = result.HeldModifiers ?? Held, ReleasedModifiers = Released };

        public TypeTextResult Apply(TypeTextResult result) => result with { HeldModifiers = result.HeldModifiers ?? Held, ReleasedModifiers = Released };

        public SendKeysResult Apply(SendKeysResult result) => result with { HeldModifiers = result.HeldModifiers ?? Held, ReleasedModifiers = Released };

        public DragResult Apply(DragResult result) => result with { HeldModifiers = result.HeldModifiers ?? Held, ReleasedModifiers = Released };
    }
}
