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
public class InputSimulationService : IInputSimulationService
{
    /// <summary>
    /// type_text sends at most this many characters in one SendInput batch, and the guard checks the
    /// foreground window again before each batch, so typing stops within this many characters of a focus
    /// change. With a delay between keystrokes every character is its own batch.
    /// </summary>
    public const int TypeTextBatchSize = 32;

    /// <summary>The most wheel notches scroll sends in one direction in one call.</summary>
    public const int MaxScrollNotches = 100;

    private readonly IMonitorService _monitorService;
    private readonly INativeInputWrapper _inputWrapper;
    private readonly IInputTargetGuard _guard;
    private readonly IInputSession _session;
    private readonly TimeSpan _gateTimeout;
    private readonly INativeHitTestWrapper? _hitTest;
    private readonly INativeSystemInfoWrapper? _systemInfo;
    private readonly IClipboardOrigin? _clipboardOrigin;
    private readonly IClipboardPasteGuard? _pasteGuard;

    /// <param name="monitorService">The monitors, for coordinate checks.</param>
    /// <param name="inputWrapper">The native input layer, for cursor reads; input itself goes through <paramref name="session"/>.</param>
    /// <param name="guard">The input guard.</param>
    /// <param name="session">The input session: the user's pause and the held-input bookkeeping.</param>
    /// <param name="gateTimeout">How long to wait for the input gate; default <see cref="InputGate.InputWaitTimeout"/>.</param>
    /// <param name="hitTest">scroll: the client area of the target window, where it scrolls when no point is given.</param>
    /// <param name="systemInfo">
    /// scroll: where Windows sends the wheel. Without it the routing is unknown, and scroll then goes only to the
    /// foreground window, as with focus routing.
    /// </param>
    /// <param name="clipboardOrigin">Told about the batches that can copy, so get_clipboard knows the agent's copies.</param>
    /// <param name="pasteGuard">
    /// Checks the batches that paste, right before they are sent, so the agent pastes only what get_clipboard would return
    /// (PasteNotAllowed). The server always passes it.
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
        IClipboardPasteGuard? pasteGuard = null)
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
    }

    public ClickResult Click(int x, int y, InputTarget target = default, bool releaseHeldModifiers = false,
        CancellationToken cancellationToken = default) =>
        ClickAt(x, y, target, "left", leftButton: true, clicks: 1, delayMs: 0, releaseHeldModifiers, cancellationToken);

    public ClickResult RightClick(int x, int y, InputTarget target = default, bool releaseHeldModifiers = false,
        CancellationToken cancellationToken = default) =>
        ClickAt(x, y, target, "right", leftButton: false, clicks: 1, delayMs: 0, releaseHeldModifiers, cancellationToken);

    public ClickResult DoubleClick(int x, int y, int delayMs = 50, InputTarget target = default, bool releaseHeldModifiers = false,
        CancellationToken cancellationToken = default) =>
        ClickAt(x, y, target, "double", leftButton: true, clicks: 2, delayMs: delayMs, releaseHeldModifiers, cancellationToken);

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

                switch (_session.SetCursorPos(x, y))
                {
                    case null:
                        return held.Apply(Finish(new MouseMoveResult(false, x, y, PausedMessage("The pointer was not moved."), InputErrorCode.PausedByUser), approved));
                    case false:
                        return held.Apply(Finish(new MouseMoveResult(false, x, y, CursorPositionFailedMessage("The pointer was not moved."),
                            InputErrorCode.CursorPositionFailed), approved));
                }

                switch (PointerIsAt(x, y, out var actual))
                {
                    case PointerCheck.Paused:
                        return held.Apply(Finish(new MouseMoveResult(false, x, y, PausedMessage("The pointer moved away from the point and was not placed there again."), InputErrorCode.PausedByUser), approved));
                    case PointerCheck.Away:
                        return held.Apply(Finish(new MouseMoveResult(false, x, y, PointerMovedAway(x, y, actual), InputErrorCode.CursorNotAtPoint),
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
    /// call with a move to the checked point, so no other input can come between them.
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

                var batch = ScrollInputs(ToVirtualDesktopAbsolute(x, y, _monitorService.GetAllMonitors()), deltaY, deltaX);
                var sent = Send(batch, approved);
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
    /// A move to the point, then one wheel event per notch: vertical first, then horizontal. One event per notch
    /// (WHEEL_DELTA each) rather than one large delta, because some applications scroll one step for any delta.
    /// deltaY counts down as positive (as a web page's wheel event does), which is a negative wheel movement.
    /// </summary>
    public static List<InputEvent> ScrollInputs((int X, int Y) absolute, int deltaY, int deltaX)
    {
        var inputs = new List<InputEvent>(1 + Math.Abs(deltaY) + Math.Abs(deltaX))
        {
            InputEvent.MouseMoveAbsolute(absolute.X, absolute.Y)
        };

        for (var i = 0; i < Math.Abs(deltaY); i++)
        {
            inputs.Add(InputEvent.Wheel(-Math.Sign(deltaY) * InputEvent.WheelDelta));
        }

        for (var i = 0; i < Math.Abs(deltaX); i++)
        {
            inputs.Add(InputEvent.HorizontalWheel(Math.Sign(deltaX) * InputEvent.WheelDelta));
        }

        return inputs;
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
        bool releaseHeldModifiers = false, CancellationToken cancellationToken = default)
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

        // The text goes out in several batches, each checked on its own: a copy (ETX, CAN) in one and the paste (SYN) in a
        // later one would paste what the copy took unchecked, so the order is checked on the whole text first
        if (_pasteGuard != null && ClipboardPasteGuard.CopyThenPasteRefusal(UnicodeInputs(text), receivingClass: null,
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

                foreach (var batch in Batches(text, delayBetweenKeysMs == 0 ? TypeTextBatchSize : 1))
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
                            $"{TypedSoFar(text, charactersTyped, approved)} {recheck.Error}", recheck.ErrorCode), recheck));
                    }

                    approved = recheck;

                    // A modifier going down now is the user taking the keyboard (or starting the pause chord)
                    if (ModifiersDownNow("typed") is { } downNow)
                    {
                        return held.Apply(Finish(new TypeTextResult(false, text, charactersTyped,
                            $"{TypedSoFar(text, charactersTyped, approved)} {downNow.Error}", InputErrorCode.ModifiersHeld)
                        {
                            HeldModifiers = downNow.Names
                        }, approved));
                    }

                    var inputs = UnicodeInputs(batch);

                    // The SYN character pastes in an edit control, as Ctrl+V does
                    if (PasteRefusal(inputs, approved) is { } pasteRefused)
                    {
                        return held.Apply(Finish(new TypeTextResult(false, text, charactersTyped,
                            $"{TypedSoFar(text, charactersTyped, approved)} {pasteRefused}", InputErrorCode.PasteNotAllowed), approved));
                    }

                    var sent = _session.Send(inputs);
                    if (sent.PausedByUser)
                    {
                        return held.Apply(Finish(new TypeTextResult(false, text, charactersTyped,
                            PausedMessage(TypedSoFar(text, charactersTyped, approved)), InputErrorCode.PausedByUser), approved));
                    }

                    if (!sent.Complete)
                    {
                        // A character counts once its key-down and key-up were both inserted; the session has
                        // already sent the key-up of a character whose key-down went in alone
                        charactersTyped += sent.Inserted / 2;
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
                    PausedMessage(TypedSoFar(text, charactersTyped, approved)), InputErrorCode.PausedByUser);
                return held.Apply(approved == null ? paused : Finish(paused, approved));
            }
            finally
            {
                _session.ReleaseHeldInput("the end of the input call");
            }
        }
    }

    public async Task<SendKeysResult> SendKeysAsync(string keys, InputTarget target = default, bool releaseHeldModifiers = false,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(keys))
        {
            return new SendKeysResult(
                Success: false,
                Keys: keys ?? "",
                Error: "Keys cannot be empty",
                ErrorCode: InputErrorCode.EmptyText);
        }

        List<InputEvent> inputs;
        try
        {
            inputs = SendKeysParser.Parse(keys);
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

        if (inputs.Count == 0)
        {
            return new SendKeysResult(
                Success: false,
                Keys: keys,
                Error: "No valid keys parsed from input",
                ErrorCode: InputErrorCode.InvalidKeySpecification);
        }

        // The agent must never pause or resume itself: the chord would reach the hotkey like a real key press
        if (_session.FindPauseChord(inputs) is { } pauseChord)
        {
            return new SendKeysResult(false, keys,
                $"The keys press {pauseChord}, the user's pause hotkey, which pauses and resumes this server's input. Only the user " +
                "may press it. Nothing was sent.", InputErrorCode.PauseChordNotAllowed);
        }

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

                call.Token.ThrowIfCancellationRequested();

                // Right before the keys: the window in front may have changed while the guard read it
                var recheck = _guard.RecheckKeyboard(approved);
                if (!recheck.Allowed)
                {
                    return held.Apply(Finish(new SendKeysResult(false, keys, $"Nothing was sent. {recheck.Error}", recheck.ErrorCode), recheck));
                }

                approved = recheck;

                // Again right before the keys: a modifier the user pressed while the guard activated the target would
                // turn them into shortcuts, and the keys' own modifier key-ups would release it
                if (ModifiersDownNow("sent") is { } downNow)
                {
                    return held.Apply(Finish(new SendKeysResult(false, keys, downNow.Error, InputErrorCode.ModifiersHeld)
                    {
                        HeldModifiers = downNow.Names
                    }, approved));
                }

                // Right before the keys: a paste must not put the user's clipboard where the agent can read it
                if (PasteRefusal(inputs, approved) is { } pasteRefused)
                {
                    return held.Apply(Finish(new SendKeysResult(false, keys, pasteRefused, InputErrorCode.PasteNotAllowed), approved));
                }

                var sent = Send(inputs, approved);
                if (sent.PausedByUser)
                {
                    return held.Apply(Finish(new SendKeysResult(false, keys, PausedMessage("Nothing was sent."), InputErrorCode.PausedByUser), approved));
                }

                if (!sent.Complete)
                {
                    return held.Apply(Finish(new SendKeysResult(false, keys,
                        "SendInput failed to send all keystrokes; the keys it had pressed were released again", InputErrorCode.SendInputFailed), approved));
                }

                return held.Apply(Finish(new SendKeysResult(Success: true, Keys: keys), approved));
            }
            catch (OperationCanceledException) when (call.IsPaused)
            {
                var paused = new SendKeysResult(false, keys, PausedMessage("Nothing was sent."), InputErrorCode.PausedByUser);
                return held.Apply(approved == null ? paused : Finish(paused, approved));
            }
            finally
            {
                _session.ReleaseHeldInput("the end of the input call");
            }
        }
    }

    /// <summary>
    /// A click or double click at a point: the guard checks the window at the point before the cursor
    /// moves, and again before every button press, since moving the cursor can open a popup there. Each
    /// press is sent in one SendInput call together with a move to the checked point, so a user moving the
    /// mouse in between cannot make it land elsewhere (SendInput does not interleave other input with the
    /// events of one call).
    /// </summary>
    private ClickResult ClickAt(int x, int y, InputTarget target, string clickType, bool leftButton, int clicks, int delayMs,
        bool releaseHeldModifiers, CancellationToken cancellationToken)
    {
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
            var clicksSent = 0;
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

                var absolute = ToVirtualDesktopAbsolute(x, y, _monitorService.GetAllMonitors());
                for (var click = 1; click <= clicks; click++)
                {
                    if (click > 1)
                    {
                        // Delay between clicks
                        Wait(Math.Clamp(delayMs, 10, 500), call);
                    }

                    var sentSoFar = ClicksSentSoFar(clicksSent);
                    var recheck = _guard.RecheckMouse(x, y, approved);
                    if (!recheck.Allowed)
                    {
                        return held.Apply(Finish(new ClickResult(false, x, y, clickType, sentSoFar + recheck.Error, recheck.ErrorCode), recheck));
                    }

                    approved = recheck;
                    switch (PointerIsAt(x, y, out var actual))
                    {
                        case PointerCheck.Paused:
                            return held.Apply(Finish(new ClickResult(false, x, y, clickType, PausedMessage(sentSoFar.TrimEnd(' ', ':') + "."),
                                InputErrorCode.PausedByUser), approved));
                        case PointerCheck.Away:
                            return held.Apply(Finish(new ClickResult(false, x, y, clickType, sentSoFar + PointerMovedAway(x, y, actual),
                                InputErrorCode.CursorNotAtPoint), approved));
                    }

                    // Right before the press: a modifier the user pressed meanwhile would make it a Ctrl- or Shift-click
                    if (ModifiersDownNow("clicked") is { } downNow)
                    {
                        return held.Apply(Finish(new ClickResult(false, x, y, clickType, sentSoFar + downNow.Error, InputErrorCode.ModifiersHeld)
                        {
                            HeldModifiers = downNow.Names
                        }, approved));
                    }

                    // And a button the user pressed meanwhile would send the press to the window it went down on
                    if (MouseButtonsDownNow("clicked") is { } buttonsNow)
                    {
                        return held.Apply(Finish(new ClickResult(false, x, y, clickType, sentSoFar + buttonsNow, InputErrorCode.MouseInUse), approved));
                    }

                    // A right click in a terminal pastes
                    if (!leftButton && PasteRefusal(ClickInputs(leftButton, absolute), approved) is { } pasteRefused)
                    {
                        return held.Apply(Finish(new ClickResult(false, x, y, clickType, sentSoFar + pasteRefused, InputErrorCode.PasteNotAllowed), approved));
                    }

                    var sent = SendClickInputs(leftButton, absolute, approved);
                    if (sent.PausedByUser)
                    {
                        return held.Apply(Finish(new ClickResult(false, x, y, clickType, PausedMessage(sentSoFar.TrimEnd(' ', ':') + "."),
                            InputErrorCode.PausedByUser), approved));
                    }

                    if (!sent.Complete)
                    {
                        var error = clicks == 1 ? "SendInput failed"
                            : click == 1 ? "First click in double-click failed"
                            : "Second click in double-click failed";
                        return held.Apply(Finish(new ClickResult(false, x, y, clickType, error, InputErrorCode.SendInputFailed), approved));
                    }

                    clicksSent++;
                }

                return held.Apply(Finish(new ClickResult(Success: true, X: x, Y: y, ClickType: clickType), approved));
            }
            catch (OperationCanceledException) when (call.IsPaused)
            {
                var paused = new ClickResult(false, x, y, clickType, PausedMessage(ClicksSentSoFar(clicksSent).TrimEnd(' ', ':') + "."),
                    InputErrorCode.PausedByUser);
                return held.Apply(approved == null ? paused : Finish(paused, approved));
            }
            finally
            {
                _session.ReleaseHeldInput("the end of the input call");
            }
        }
    }

    private static string ClicksSentSoFar(int clicksSent) =>
        clicksSent == 0 ? "Nothing was clicked: " : "The first click was sent, the second was not: ";

    /// <summary>A move to the checked point, the button down and up, in one SendInput call.</summary>
    private InputSendOutcome SendClickInputs(bool leftButton, (int X, int Y) absolute, InputGuardResult approved) =>
        Send(ClickInputs(leftButton, absolute), approved);

    private static InputEvent[] ClickInputs(bool leftButton, (int X, int Y) absolute)
    {
        var downFlag = leftButton ? NativeEnums.MOUSEEVENTF_LEFTDOWN : NativeEnums.MOUSEEVENTF_RIGHTDOWN;
        var upFlag = leftButton ? NativeEnums.MOUSEEVENTF_LEFTUP : NativeEnums.MOUSEEVENTF_RIGHTUP;
        return
        [
            InputEvent.MouseMoveAbsolute(absolute.X, absolute.Y),
            InputEvent.Mouse(downFlag),
            InputEvent.Mouse(upFlag)
        ];
    }

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
    private InputSendOutcome Send(IReadOnlyList<InputEvent> batch, InputGuardResult approved)
    {
        var copy = _clipboardOrigin?.BeforeInput(batch, approved.ReceivingWindow?.ProcessId ?? 0, approved.Receiving, approved.Anchor);
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
    /// The modifier keys that are down right before a batch, with the ModifiersHeld message; null when none is.
    /// </summary>
    private (IReadOnlyList<string> Names, string Error)? ModifiersDownNow(string action)
    {
        var down = _session.ReadModifiersDown();
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
    /// The normalized absolute coordinates (0..65535 across the virtual desktop, the bounding rectangle of all
    /// monitors) of the pixel (x, y), for MOUSEEVENTF_ABSOLUTE | MOUSEEVENTF_VIRTUALDESK. Windows maps a
    /// normalized coordinate n to the pixel left + n * width / 65536, rounded down. This returns the middle of
    /// the pixel's range of n, so the pixel comes back exactly with that mapping, and also with one that
    /// rounds n * (width - 1) / 65535 to the nearest pixel.
    /// </summary>
    public static (int X, int Y) ToVirtualDesktopAbsolute(int x, int y, IReadOnlyCollection<MonitorInfo> monitors)
    {
        ArgumentNullException.ThrowIfNull(monitors);
        if (monitors.Count == 0)
        {
            throw new ArgumentException("At least one monitor is needed.", nameof(monitors));
        }

        var left = monitors.Min(m => m.Bounds.X);
        var top = monitors.Min(m => m.Bounds.Y);
        var right = monitors.Max(m => m.Bounds.X + m.Bounds.Width);
        var bottom = monitors.Max(m => m.Bounds.Y + m.Bounds.Height);
        return (Normalize(x - left, right - left), Normalize(y - top, bottom - top));

        static int Normalize(int offset, int size) =>
            (int)Math.Clamp((2L * offset + 1) * 65536 / (2L * Math.Max(size, 1)), 0, 65535);
    }

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
              $"become Ctrl- or Shift-clicks. If the user is using the keyboard, wait and try again. If nobody is pressing {them}, pass " +
              $"releaseHeldModifiers: true (or call release_all_input with releaseHeldModifiers: true) to release {them}.";
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

    /// <summary>A key-down and key-up as Unicode for every UTF-16 code unit of <paramref name="text"/>.</summary>
    private static List<InputEvent> UnicodeInputs(string text)
    {
        var inputs = new List<InputEvent>(text.Length * 2);
        foreach (var character in text)
        {
            inputs.Add(InputEvent.Unicode(character, NativeEnums.KEYEVENTF_KEYDOWN));
            inputs.Add(InputEvent.Unicode(character, NativeEnums.KEYEVENTF_KEYUP));
        }

        return inputs;
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
    }
}
