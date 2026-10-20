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
    public const int TypeTextBatchSize = 64;

    /// <summary>
    /// type_text types at most this many characters (roadmap M4.4), so a runaway call cannot type for minutes; longer text
    /// is pasted (method paste or auto).
    /// </summary>
    public const int MaxTypedLength = 5000;

    /// <summary>type_text pastes at most this many characters, as set_clipboard takes at most.</summary>
    public const int MaxPasteLength = 1_000_000;

    /// <summary>type_text's method auto pastes text longer than this many characters, and types the rest.</summary>
    public const int AutoPasteThreshold = 200;

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
    private readonly IPasteClipboard? _pasteClipboard;
    private readonly TimeSpan _pasteSettleTime;
    private readonly ITakeoverMonitor? _takeover;

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
    /// <param name="pasteClipboard">
    /// The clipboard type_text's paste lends (roadmap M4.4). The server always passes it; without it method auto always
    /// types, and method paste cannot be used.
    /// </param>
    /// <param name="pasteSettleTime">How long a paste keeps the text on the clipboard after Ctrl+V; default <see cref="PasteSettleTime"/>.</param>
    /// <param name="takeover">
    /// Takeover detection (roadmap M7.4): type_text's typing and drag stop (mode stop) or warn (mode warn) when the user uses
    /// the keyboard or mouse meanwhile. The server always passes it; without it nothing is watched.
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
        INativeKeyboardLayoutWrapper? keyboardLayout = null,
        IPasteClipboard? pasteClipboard = null,
        TimeSpan? pasteSettleTime = null,
        ITakeoverMonitor? takeover = null)
    {
        _takeover = takeover;
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
        _pasteClipboard = pasteClipboard;
        _pasteSettleTime = pasteSettleTime ?? PasteSettleTime;
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
                        "prompt or a disconnected session), or the session may be locked. Try again; if it keeps failing, ask the user.",
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

    /// <summary>scroll without modifier keys.</summary>
    public ScrollResult Scroll(int? x, int? y, int deltaY, int deltaX, InputTarget target = default, bool releaseHeldModifiers = false,
        CancellationToken cancellationToken = default) =>
        Scroll(x, y, deltaY, deltaX, [], target, releaseHeldModifiers, cancellationToken);

    public ScrollResult Scroll(int? x, int? y, int deltaY, int deltaX, IReadOnlyList<MouseModifier> modifiers, InputTarget target = default,
        bool releaseHeldModifiers = false, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(modifiers);
        var held = modifiers.Distinct().ToList();
        return ScrollAtPointOrCentre(x, y, deltaY, deltaX, held, target, releaseHeldModifiers, cancellationToken) with
        {
            Modifiers = ModifierNames(held)
        };
    }

    private ScrollResult ScrollAtPointOrCentre(int? x, int? y, int deltaY, int deltaX, IReadOnlyList<MouseModifier> modifiers,
        InputTarget target, bool releaseHeldModifiers, CancellationToken cancellationToken)
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

        return ScrollAt(pointX, pointY, deltaY, deltaX, modifiers, target, releaseHeldModifiers, cancellationToken);
    }

    /// <summary>
    /// scroll at a checked point, as a click: the guard checks the window at the point before the pointer moves and
    /// again right before the wheel, together with where Windows sends the wheel. The wheel notches go in one SendInput
    /// call with a move to the checked point (<see cref="InputBatchBuilder"/>), so no other input can come between them; a
    /// pointer that is not at the point afterwards is CursorClamped. <paramref name="modifiers"/> go down before the move and
    /// up after the notches in the same batch (issue #8: Ctrl+wheel zooms, Shift+wheel scrolls sideways), as mouse_click's go
    /// around its clicks, so none stays down whatever happens to the call; a key the user holds is refused (ModifiersHeld) as
    /// for every call, before and right before the batch.
    /// </summary>
    private ScrollResult ScrollAt(int x, int y, int deltaY, int deltaX, IReadOnlyList<MouseModifier> modifiers, InputTarget target,
        bool releaseHeldModifiers, CancellationToken cancellationToken)
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

                // The modifiers take the scan codes of the target's layout, as mouse_click's do; a plain scroll reads no layout
                var batch = NewBatch(modifiers.Count > 0 ? LayoutFor(target) : null)
                    .ModifiersDown(modifiers).MoveTo(x, y).Wheel(deltaY, deltaX).ModifiersUp(modifiers).Build();
                var sent = Send(batch, approved);
                if (sent.PausedByUser)
                {
                    return held.Apply(Finish(new ScrollResult(false, x, y, deltaY, deltaX, PausedMessage(NothingScrolled), InputErrorCode.PausedByUser), approved));
                }

                if (!sent.Complete)
                {
                    return held.Apply(Finish(new ScrollResult(false, x, y, deltaY, deltaX, ScrollBatchCutShort(sent, deltaY, deltaX, modifiers.Count),
                        InputErrorCode.SendInputFailed), approved));
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
    /// The SendInputFailed message of a scroll batch SendInput inserted only part of: how many of its notches went in. The
    /// session has already released the modifier keys that went down.
    /// </summary>
    private static string ScrollBatchCutShort(InputSendOutcome sent, int deltaY, int deltaX, int modifiers)
    {
        var notches = Math.Abs(deltaY) + Math.Abs(deltaX);
        var scrolled = Math.Clamp(sent.Inserted - modifiers - 1, 0, notches);
        var events = modifiers == 0
            ? "the move to the point, then one per notch"
            : "the modifier keys, the move to the point, one per notch, then the release of the modifier keys, which was sent at once";
        return $"SendInput inserted {sent.Inserted} of {sent.Requested} events ({events}), so {scrolled} of {notches} notches were " +
               "scrolled. Check the window before scrolling again.";
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
        bool releaseHeldModifiers = false, TextControlChars? controlChars = null, TextInputMethod method = TextInputMethod.Unicode,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(text))
        {
            return new TypeTextResult(
                Success: false,
                Length: 0,
                CharactersTyped: 0,
                Error: "Text cannot be empty",
                ErrorCode: InputErrorCode.EmptyText);
        }

        // Every count is of this text, in code points: a Windows line break is one character, as it is one key press, and an
        // emoji is one too
        var typed = NormalizeLineBreaks(text);
        var length = CountCharacters(typed);
        delayBetweenKeysMs = Math.Clamp(delayBetweenKeysMs, 0, 100);
        var viaLayout = method == TextInputMethod.Keys;
        var typingMethod = ToolEnum<TextInputMethod>.Name(viaLayout ? TextInputMethod.Keys : TextInputMethod.Unicode);
        var pasteMethod = ToolEnum<TextInputMethod>.Name(TextInputMethod.Paste);

        // Ctrl+V can be the user's pause hotkey, which the agent must never press: method paste is refused, auto types
        var pasteIsPauseChord = _session.FindPauseChord(PasteKeys(KeyboardLayout.Invariant));

        // auto pastes long text where a paste puts in what typing would: not with delayMs, and not when line breaks or tabs
        // are to press keys
        var pastes = method == TextInputMethod.Paste ||
                     (method == TextInputMethod.Auto && _pasteClipboard != null && length > AutoPasteThreshold && delayBetweenKeysMs == 0 &&
                      (controlChars is null or TextControlChars.Unicode || typed.AsSpan().IndexOfAny('\n', '\t') < 0) &&
                      pasteIsPauseChord == null);
        var planned = pastes ? pasteMethod : typingMethod;
        if (pastes && PasteRefusalBeforeInput(method == TextInputMethod.Paste, length, delayBetweenKeysMs, pasteIsPauseChord) is { } pasteRefused)
        {
            return new TypeTextResult(false, length, 0, pasteRefused.Error, pasteRefused.Code) { Method = pasteMethod };
        }

        // Text that is typed is checked whole before anything is sent; auto checks it when it types instead of pasting
        List<InputEvent[]>? characters = null;
        if (!pastes)
        {
            var prepared = PrepareTyping(typed, length, controlChars, viaLayout, target, pasteIsPauseChord);
            if (prepared.Characters == null)
            {
                return new TypeTextResult(false, length, 0, prepared.Error, prepared.Code) { Method = typingMethod };
            }

            characters = prepared.Characters;
        }

        using var call = new InputCall(_session, cancellationToken);
        if (call.IsPaused)
        {
            return new TypeTextResult(false, length, 0, PausedMessage("Nothing was typed."), InputErrorCode.PausedByUser) { Method = planned };
        }

        IDisposable? gate;
        try
        {
            gate = await InputGate.TryEnterAsync(_gateTimeout, call.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (call.IsPaused)
        {
            return new TypeTextResult(false, length, 0, PausedMessage("Nothing was typed."), InputErrorCode.PausedByUser) { Method = planned };
        }

        if (gate == null)
        {
            return new TypeTextResult(false, length, 0, BusyMessage("typed"), InputErrorCode.InputBusy) { Method = planned };
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
                    return held.Apply(new TypeTextResult(false, length, 0, held.Error, held.Code) { Method = planned });
                }

                approved = _guard.CheckKeyboard(target, KeyboardInputKind.Text, keys: null);
                if (!approved.Allowed)
                {
                    return call.IsPaused
                        ? held.Apply(Finish(new TypeTextResult(false, length, 0, PausedMessage("Nothing was typed."), InputErrorCode.PausedByUser) { Method = planned }, approved))
                        : held.Apply(Finish(new TypeTextResult(false, length, 0, approved.Error, approved.ErrorCode) { Method = planned }, approved));
                }

                string? notPasted = null;
                ClipboardReturn? back = null;
                if (pastes)
                {
                    // Not even the clipboard is touched once the user paused input or the request was cancelled (the pause
                    // reaches call.Token a moment later, so it is read itself)
                    if (call.IsPaused)
                    {
                        return held.Apply(Finish(new TypeTextResult(false, length, 0, PausedMessage("Nothing was typed."),
                            InputErrorCode.PausedByUser) { Method = pasteMethod }, approved));
                    }

                    call.Token.ThrowIfCancellationRequested();
                    var attempt = await PasteAsync(typed, length, method == TextInputMethod.Paste, target, approved).ConfigureAwait(false);
                    if (attempt.Result is { } pasted)
                    {
                        return held.Apply(pasted with { Method = pasteMethod });
                    }

                    // auto types instead, when the text may be typed
                    approved = attempt.Approved;
                    notPasted = attempt.NotPasted;
                    back = attempt.Back;
                    var prepared = PrepareTyping(typed, length, controlChars, viaLayout, target, pasteIsPauseChord);
                    if (prepared.Characters == null)
                    {
                        var typingRefused = prepared.Code == InputErrorCode.TextTooLong
                            ? $"Typing it was refused too: it has {Count(length)} characters, more than the {Count(MaxTypedLength)} type_text " +
                              "types. Nothing was typed. Ask the user to empty the clipboard (or to copy plain text), or send the text in parts."
                            : prepared.Error;
                        return held.Apply(Finish(new TypeTextResult(false, length, 0,
                            $"The text was not pasted: {notPasted}. {typingRefused}", prepared.Code) { Method = typingMethod }, approved));
                    }

                    characters = prepared.Characters;
                }

                var result = await TypeCharactersAsync(characters!, delayBetweenKeysMs, approved, call).ConfigureAwait(false);
                // A clipboard lent before auto typed instead is reported as after a paste
                return held.Apply(result with
                {
                    Method = typingMethod,
                    ClipboardRestored = back?.Restored,
                    Warning = TakeoverMessages.Combine(
                        notPasted == null ? null : $"Typed instead of pasted: {notPasted}.{(back?.Warning is { } lost ? " " + lost : string.Empty)}",
                        result.Warning)
                });
            }
            catch (OperationCanceledException) when (call.IsPaused)
            {
                // Typing reports how far it got itself, so this is a pause before anything was typed
                var paused = new TypeTextResult(false, length, 0, PausedMessage("Nothing was typed."), InputErrorCode.PausedByUser) { Method = planned };
                return held.Apply(approved == null ? paused : Finish(paused, approved));
            }
            finally
            {
                _session.ReleaseHeldInput("the end of the input call");
            }
        }
    }

    /// <summary>
    /// Types <paramref name="characters"/> (one array of events per character) inside the input gate, after the guard
    /// approved the window: in batches of <see cref="TypeTextBatchSize"/> characters, or one character per batch with
    /// <paramref name="delayBetweenKeysMs"/>, with the window, the modifiers and the paste rule checked again right before
    /// every batch. Reports how many characters went in when it stops part way, at the user's pause too.
    /// </summary>
    private async Task<TypeTextResult> TypeCharactersAsync(List<InputEvent[]> characters, int delayBetweenKeysMs,
        InputGuardResult approved, InputCall call)
    {
        // Takeover detection (roadmap M7.4) watches the typing itself: from the first batch on
        call.WatchForTakeover(_takeover);
        var result = await TypeCharactersWatchedAsync(characters, delayBetweenKeysMs, approved, call).ConfigureAwait(false);
        return call.TakeoverWarning("the text was typed") is { } takeover
            ? result with { Warning = TakeoverMessages.Combine(result.Warning, takeover) }
            : result;
    }

    private async Task<TypeTextResult> TypeCharactersWatchedAsync(List<InputEvent[]> characters, int delayBetweenKeysMs,
        InputGuardResult approved, InputCall call)
    {
        var length = characters.Count;
        var charactersTyped = 0;
        try
        {
            foreach (var batch in characters.Chunk(delayBetweenKeysMs == 0 ? TypeTextBatchSize : 1))
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
                    return Finish(new TypeTextResult(false, length, charactersTyped,
                        $"{TypedSoFar(length, charactersTyped, approved)} {recheck.Error}", recheck.ErrorCode), recheck);
                }

                approved = recheck;

                // A modifier going down now is the user taking the keyboard (or starting the pause chord)
                if (ModifiersDownNow("typed") is { } downNow)
                {
                    return Finish(new TypeTextResult(false, length, charactersTyped,
                        $"{TypedSoFar(length, charactersTyped, approved)} {downNow.Error}", InputErrorCode.ModifiersHeld)
                    {
                        HeldModifiers = downNow.Names
                    }, approved);
                }

                var inputs = batch.SelectMany(character => character).ToList();

                // The SYN character pastes in an edit control, as Ctrl+V does
                if (PasteRefusal(inputs, approved) is { } pasteRefused)
                {
                    return Finish(new TypeTextResult(false, length, charactersTyped,
                        $"{TypedSoFar(length, charactersTyped, approved)} {pasteRefused}", InputErrorCode.PasteNotAllowed), approved);
                }

                var sent = _session.Send(inputs);
                if (sent.PausedByUser)
                {
                    return Finish(new TypeTextResult(false, length, charactersTyped,
                        PausedMessage(TypedSoFar(length, charactersTyped, approved)), InputErrorCode.PausedByUser), approved);
                }

                if (!sent.Complete)
                {
                    // A character counts once all its events were inserted; the session has already sent the key-ups
                    // of whatever went down alone
                    charactersTyped += CharactersWithin(batch, sent.Inserted);
                    return Finish(new TypeTextResult(false, length, charactersTyped,
                        $"SendInput failed at character {charactersTyped}", InputErrorCode.SendInputFailed), approved);
                }

                charactersTyped += batch.Length;
            }

            return Finish(new TypeTextResult(Success: true, Length: length, CharactersTyped: charactersTyped), approved);
        }
        catch (OperationCanceledException) when (call.IsPaused)
        {
            return Finish(new TypeTextResult(false, length, charactersTyped,
                PausedMessage(TypedSoFar(length, charactersTyped, approved)), InputErrorCode.PausedByUser), approved);
        }
        catch (OperationCanceledException) when (call.Interrupted)
        {
            return Finish(new TypeTextResult(false, length, charactersTyped,
                TakeoverMessages.Interrupted(TypedSoFar(length, charactersTyped, approved)), InputErrorCode.UserInterrupted), approved);
        }
    }

    /// <summary>
    /// The events that type <paramref name="typed"/>, one array per character; or why it must not be typed: more than
    /// <see cref="MaxTypedLength"/> characters (TextTooLong), a line break or tab without controlChars (InvalidArguments),
    /// keys that press the user's pause hotkey (PauseChordNotAllowed), or a copy followed by a paste among its control
    /// characters (PasteNotAllowed). Nothing is sent. <paramref name="pasteIsPauseChord"/> is the user's pause hotkey when
    /// it is Ctrl+V: no method pastes then, so the advice for text that is too long is to type it in parts.
    /// </summary>
    private (List<InputEvent[]>? Characters, InputErrorCode Code, string? Error) PrepareTyping(string typed, int length,
        TextControlChars? controlChars, bool viaLayout, InputTarget target, string? pasteIsPauseChord)
    {
        if (length > MaxTypedLength)
        {
            return (null, InputErrorCode.TextTooLong,
                $"The text has {Count(length)} characters, more than the {Count(MaxTypedLength)} type_text types, so that a runaway call " +
                "cannot type for minutes. Nothing was typed. " +
                (pasteIsPauseChord == null
                    ? "Paste it with method 'paste' (or 'auto'), or type it in parts."
                    : $"Type it in parts: it cannot be pasted, because a paste presses Ctrl+V and {pasteIsPauseChord} is the " +
                      "user's pause hotkey."));
        }

        if (controlChars == null && typed.AsSpan().IndexOfAny('\n', '\t') >= 0)
        {
            return (null, InputErrorCode.InvalidArguments, ControlCharsNeededMessage);
        }

        var mode = controlChars ?? TextControlChars.Unicode;
        var layout = mode == TextControlChars.Unicode && !viaLayout ? KeyboardLayout.Invariant : LayoutFor(target);
        var characters = CharacterInputs(typed, mode, layout, viaLayout);

        var events = characters.SelectMany(c => c).ToList();

        // The agent must never pause or resume itself, as send_keys and press_keys refuse: the hotkey reads the keys, not the
        // character, so a character method keys types with AltGr (the left Ctrl and the right Alt) presses a Ctrl+Alt chord
        if (_session.FindPauseChord(events) is { } pauseChord)
        {
            return (null, InputErrorCode.PauseChordNotAllowed,
                $"Typing the text presses {pauseChord}, the user's pause hotkey, which pauses and resumes this server's input (a " +
                "character typed with AltGr is Ctrl+Alt with its key to the hotkey). Only the user may press it. Nothing was " +
                "typed. Type the text with method 'unicode', which presses no keys for its characters.");
        }

        // The text goes out in several batches, each checked on its own: a copy (ETX, CAN) in one and the paste (SYN) in a
        // later one would paste what the copy took unchecked, so the order is checked on the whole text first
        if (_pasteGuard != null && ClipboardPasteGuard.CopyThenPasteRefusal(events, receivingClass: null, "Nothing was typed.") is { } copyThenPaste)
        {
            return (null, InputErrorCode.PasteNotAllowed, copyThenPaste);
        }

        return (characters, InputErrorCode.None, null);
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
        var layout = LayoutFor(target);
        try
        {
            spec = KeySpecParser.Parse(keys, layout);
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

        // Bounded as type_text is (roadmap M4.4), before anything is checked or sent: characters type themselves, so a
        // runaway call could otherwise type for minutes, and with delayMs hold the input gate as long
        var presses = SendKeysParser.KeyPresses(inputs).Count;
        if (presses > MaxSendKeysPresses)
        {
            // type_text refuses to paste when Ctrl+V is the user's pause hotkey, so the advice must not send the agent there
            var pasteIsPauseChord = _session.FindPauseChord(PasteKeys(KeyboardLayout.Invariant));
            return new SendKeysResult(false, keys,
                $"The keys press {Count(presses)} keys, more than the {Count(MaxSendKeysPresses)} send_keys sends in one call, so that a " +
                "runaway call cannot type for minutes. Nothing was sent. " +
                (pasteIsPauseChord == null
                    ? "Send them in parts, or put long text in with type_text (method 'paste')."
                    : $"Send them in parts: long text cannot be pasted with type_text, because a paste presses Ctrl+V and " +
                      $"{pasteIsPauseChord} is the user's pause hotkey."), InputErrorCode.TextTooLong);
        }

        // Every event counts too: a modifier going down and up without a key between (crossed: {SHIFT down}{CTRL down}
        // {SHIFT up}{CTRL up}) is no key press
        if (inputs.Count > MaxSendKeysEvents)
        {
            return new SendKeysResult(false, keys,
                $"The keys make {Count(inputs.Count)} key events (each key's down and up, the modifiers' included), more than the " +
                $"{Count(MaxSendKeysEvents)} send_keys sends in one call, so that a runaway call cannot type for minutes. Nothing " +
                "was sent. Send them in parts.", InputErrorCode.TextTooLong);
        }

        delayMs = Math.Clamp(delayMs, 0, MaxSendKeysDelayMs);
        var waits = SendKeysWaitMs(spec.Steps.Count, delayMs);
        if (waits > MaxSendKeysDurationMs)
        {
            return new SendKeysResult(false, keys,
                $"With delayMs {delayMs} between its {Count(spec.Steps.Count)} keys the call would wait {Count(waits)} ms in all; one " +
                $"call waits at most {Count(MaxSendKeysDurationMs)} ms, because every other input and window call waits for it. " +
                "Nothing was sent. Lower delayMs, or send the keys in several calls.", InputErrorCode.InvalidArguments);
        }

        return await SendStepsAsync(keys, spec, target, releaseHeldModifiers, delayMs, holdMs: 0, SendKeysBatchSize,
            ReportKeys(spec, layout, pressKeys: false), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Sends the steps of send_keys or press_keys (roadmap M4.1, M4.3), after the pause chord, the held input and the guard
    /// (destructive chords, keys after a focus change) have checked all of them. Without <paramref name="delayMs"/> and
    /// <paramref name="holdMs"/> the steps go in batches of whole steps of at most <paramref name="batchSize"/> key-downs
    /// (a step with more goes alone), so no other input can come between the keys of a batch; otherwise each step is a batch
    /// of its own, with <paramref name="delayMs"/> before every step but the first. Every batch is checked again right before
    /// it (the window, the modifiers, the paste rule, then the user's pause), as type_text's are; the paste rule and the
    /// clipboard origin read a batch after the first with the modifiers an earlier batch holds down. With
    /// <paramref name="holdMs"/> a step's leading key-downs go first, and its key-ups follow that much later without a check
    /// of their own (they only release what went down; the session releases them anyway if the call stops meanwhile). A
    /// call that goes through carries <paramref name="report"/>: how the keys went out on the layout, and its warnings; a call
    /// that stops part way carries it for the events that went out before it stopped (issue #9), whole batches and the
    /// events SendInput inserted of a batch it cut short.
    /// </summary>
    private async Task<SendKeysResult> SendStepsAsync(string keys, KeySpec spec, InputTarget target, bool releaseHeldModifiers,
        int delayMs, int holdMs, int batchSize, KeyReport report, CancellationToken cancellationToken)
    {
        var inputs = spec.Events;

        // The agent must never pause or resume itself: the chord would reach the hotkey like a real key press
        if (_session.FindPauseChord(inputs) is { } pauseChord)
        {
            return new SendKeysResult(false, keys,
                $"The keys press {pauseChord}, the user's pause hotkey, which pauses and resumes this server's input. Only the user " +
                "may press it. Nothing was sent.", InputErrorCode.PauseChordNotAllowed);
        }

        var batches = KeyBatches(spec, delayMs, holdMs, batchSize);
        var total = spec.Steps.Count;
        var keysSent = 0;

        // The events of the batches sent whole, so that a short SendInput count is told in steps of the whole call
        var eventsSent = 0;

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
                    var (batch, wait, release, steps) = batches[b];
                    if (wait > 0)
                    {
                        await Task.Delay(wait, call.Token).ConfigureAwait(false);
                    }

                    call.Token.ThrowIfCancellationRequested();

                    // From the second batch on, the keys of the batch are pressed with the modifiers an earlier batch holds
                    // down ({CTRL down}): the chord checks read them in front of the batch, or a Ctrl+V or Ctrl+C split
                    // across batches would go unseen
                    var pressedAs = b == 0 ? batch : WithServerHeldModifiers(batch);

                    // A release batch only lets go of what the step before it pressed
                    if (!release)
                    {
                        // Right before every batch: the window in front may have changed while the guard read it, or since the
                        // last batch
                        var recheck = _guard.RecheckKeyboard(approved);
                        if (!recheck.Allowed)
                        {
                            return held.Apply(Finish(report.ApplyTo(new SendKeysResult(false, keys,
                                $"{KeysSentSoFar(keysSent, total, approved)} {recheck.Error}", recheck.ErrorCode) { KeysSent = keysSent }, eventsSent),
                                recheck));
                        }

                        approved = recheck;

                        // Again right before the keys: a modifier the user pressed while the guard activated the target would
                        // turn them into shortcuts, and the keys' own modifier key-ups would release it. A modifier an earlier
                        // batch of these keys holds down ({SHIFT down}) is the server's own.
                        if (ModifiersDownNow("sent", ignoreServerHeld: b > 0) is { } downNow)
                        {
                            return held.Apply(Finish(report.ApplyTo(new SendKeysResult(false, keys,
                                b == 0 ? downNow.Error : $"{KeysSentSoFar(keysSent, total, approved)} {downNow.Error}", InputErrorCode.ModifiersHeld)
                            {
                                HeldModifiers = downNow.Names,
                                KeysSent = keysSent
                            }, eventsSent), approved));
                        }

                        // Right before the keys: a paste must not put the user's clipboard where the agent can read it. The first
                        // check reads the whole call, so a copy in one batch and its paste in a later one are refused together;
                        // a later one reads its batch with the modifiers held for it, so a paste whose Ctrl went down earlier
                        // is checked against the clipboard as it is now
                        if (PasteRefusal(b == 0 ? inputs : pressedAs, approved) is { } pasteRefused)
                        {
                            return held.Apply(Finish(report.ApplyTo(new SendKeysResult(false, keys,
                                b == 0 ? pasteRefused : $"{KeysSentSoFar(keysSent, total, approved)} {pasteRefused}", InputErrorCode.PasteNotAllowed)
                            {
                                KeysSent = keysSent
                            }, eventsSent), approved));
                        }
                    }

                    var sent = Send(batch, pressedAs, approved);
                    if (sent.PausedByUser)
                    {
                        return held.Apply(Finish(report.ApplyTo(new SendKeysResult(false, keys, PausedMessage(KeysSentSoFar(keysSent, total, approved)),
                            InputErrorCode.PausedByUser) { KeysSent = keysSent }, eventsSent), approved));
                    }

                    if (!sent.Complete)
                    {
                        keysSent = StepsWithin(spec, eventsSent + sent.Inserted);
                        return held.Apply(Finish(report.ApplyTo(new SendKeysResult(false, keys,
                            $"SendInput inserted only {sent.Inserted} of {sent.Requested} events, so {keysSent} of {total} keys were sent " +
                            "completely; the keys it had pressed were released again. Check the window before sending the rest.",
                            InputErrorCode.SendInputFailed) { KeysSent = keysSent }, eventsSent + sent.Inserted), approved));
                    }

                    eventsSent += batch.Count;
                    keysSent += steps;
                }

                return held.Apply(Finish(report.ApplyTo(new SendKeysResult(Success: true, Keys: keys) { KeysSent = keysSent }, eventsSent), approved));
            }
            catch (OperationCanceledException) when (call.IsPaused)
            {
                var paused = new SendKeysResult(false, keys, PausedMessage(KeysSentSoFar(keysSent, total, approved)), InputErrorCode.PausedByUser);
                return held.Apply(approved == null ? paused : Finish(report.ApplyTo(paused with { KeysSent = keysSent }, eventsSent), approved));
            }
            finally
            {
                _session.ReleaseHeldInput("the end of the input call");
            }
        }
    }

    /// <summary>send_keys waits at most this long between keys (delayMs).</summary>
    public const int MaxSendKeysDelayMs = 500;

    /// <summary>
    /// send_keys presses at most this many keys in one call (TextTooLong), as type_text types at most
    /// <see cref="MaxTypedLength"/> characters, so that a runaway call cannot type for minutes: every character, named key,
    /// press of <c>{KEY n}</c> and modifier pressed on its own (<c>{ALT}</c>) counts, the modifiers held for a key do not
    /// (<see cref="SendKeysParser.KeyPresses"/>); their events count in <see cref="MaxSendKeysEvents"/>.
    /// </summary>
    public const int MaxSendKeysPresses = MaxTypedLength;

    /// <summary>
    /// send_keys sends at most this many events in one call (TextTooLong), each key-down and key-up counting, the modifiers'
    /// included: <see cref="MaxSendKeysPresses"/> counts no modifier that goes down and up without a key between (crossed:
    /// <c>{SHIFT down}{CTRL down}{SHIFT up}{CTRL up}</c>), so a call of such events alone would be unbounded. 12 is the most
    /// events one key press takes (Ctrl, Alt, Shift and Win held for an AltGr character, which adds the right Alt: five
    /// modifier downs and ups around the key's own), so no call within <see cref="MaxSendKeysPresses"/> is refused by its
    /// presses' events alone.
    /// </summary>
    public const int MaxSendKeysEvents = 12 * MaxSendKeysPresses;

    /// <summary>
    /// How long send_keys waits between <paramref name="keys"/> keys with <paramref name="delayMs"/>: (keys - 1) x delayMs,
    /// in long, so that very many keys cannot wrap past int's range below <see cref="MaxSendKeysDurationMs"/> and hold the
    /// input gate for days.
    /// </summary>
    public static long SendKeysWaitMs(int keys, int delayMs) => (long)Math.Max(0, keys - 1) * delayMs;

    /// <summary>
    /// send_keys without delayMs sends its keys in batches of whole keys of at most this many key-downs, every key going down
    /// counting, a modifier's included (a key with more, such as a long group, goes whole and alone), checked again before
    /// each as type_text's batches of <see cref="TypeTextBatchSize"/> characters are, so a focus change or the user's pause
    /// stops a long call within a batch: once inserted, a batch cannot be stopped.
    /// </summary>
    public const int SendKeysBatchSize = TypeTextBatchSize;

    /// <summary>
    /// The longest send_keys may wait between its keys in all: (keys - 1) x delayMs. The input gate is held throughout, so
    /// every other input and window call waits for it, as for press_keys (<see cref="MaxPressKeysDurationMs"/>).
    /// </summary>
    public const int MaxSendKeysDurationMs = MaxPressKeysDurationMs;

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
        var layout = LayoutFor(target);
        try
        {
            spec = PressKeysParser.Parse(keys, repeat, layout);
        }
        catch (KeySyntaxException ex)
        {
            return new SendKeysResult(false, keys.Text, $"{ex.Message} Nothing was sent.", InputErrorCode.InvalidKeySpecification);
        }
        catch (ArgumentException ex) when (ex is not ArgumentOutOfRangeException)
        {
            return new SendKeysResult(false, keys.Text,
                $"{ex.Message}. Key names: ctrl, shift, alt, win, enter, tab, esc, space, backspace, delete, up, down, left, right, " +
                "home, end, pgup, pgdn, f1-f24, plus, comma, star, a punctuation key by its virtual key (oem_plus, oem_minus, oem_comma, " +
                "oem_period, oem_1 to oem_8, oem_102; which key each is depends on the layout), or a single character. Nothing was sent.",
                InputErrorCode.UnknownKey);
        }

        var duration = (long)spec.Steps.Count * (holdMs + delayBetweenMs);
        if (duration > MaxPressKeysDurationMs)
        {
            throw new ArgumentException(
                $"{spec.Steps.Count} presses x (holdMs {holdMs} + delayBetweenMs {delayBetweenMs}) is {duration} ms; one call takes at " +
                $"most {MaxPressKeysDurationMs} ms, because every other input call waits for it.", nameof(holdMs));
        }

        // At most MaxPresses presses: without waits they all go in one batch
        return await SendStepsAsync(keys.Text, spec, target, releaseHeldModifiers, delayBetweenMs, holdMs, batchSize: int.MaxValue,
            ReportKeys(spec, layout, pressKeys: true), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// The SendInput batches of <paramref name="spec"/>: without waits, its steps in order, as many whole steps in a batch as
    /// hold at most <paramref name="batchSize"/> key-downs (every key going down, a modifier's included; a step with more is a
    /// batch of its own); otherwise each step, after <paramref name="delayMs"/> (not before the first), split with
    /// <paramref name="holdMs"/> into its leading key-downs and, that much later, the rest (a release batch, which
    /// <see cref="SendStepsAsync"/> does not check again). Each batch says how many steps it completes.
    /// </summary>
    private static List<(IReadOnlyList<InputEvent> Events, int Wait, bool Release, int Steps)> KeyBatches(KeySpec spec, int delayMs,
        int holdMs, int batchSize)
    {
        var batches = new List<(IReadOnlyList<InputEvent> Events, int Wait, bool Release, int Steps)>();
        if (delayMs == 0 && holdMs == 0)
        {
            var events = new List<InputEvent>();
            var steps = 0;
            var downs = 0;
            foreach (var step in spec.Steps)
            {
                // Every key going down, a modifier's included: a modifier down without a key press ({SHIFT down}, or a tap
                // split across two steps) still counts, so no run of them goes out in one unchecked batch
                var stepDowns = step.Count(e => e.Kind != InputEventKind.Mouse && (e.Flags & NativeEnums.KEYEVENTF_KEYUP) == 0);
                if (steps > 0 && downs + stepDowns > batchSize)
                {
                    batches.Add((events, 0, false, steps));
                    events = new List<InputEvent>();
                    steps = 0;
                    downs = 0;
                }

                events.AddRange(step);
                steps++;
                downs += stepDowns;
            }

            if (steps > 0)
            {
                batches.Add((events, 0, false, steps));
            }

            return batches;
        }

        for (var i = 0; i < spec.Steps.Count; i++)
        {
            var step = spec.Steps[i];
            var wait = i == 0 ? 0 : delayMs;
            var downs = holdMs == 0 ? step.Count : step.TakeWhile(e => (e.Flags & NativeEnums.KEYEVENTF_KEYUP) == 0).Count();
            if (downs == step.Count || downs == 0)
            {
                batches.Add((step, wait, false, 1));
                continue;
            }

            batches.Add((step.Take(downs).ToList(), wait, false, 0));
            batches.Add((step.Skip(downs).ToList(), holdMs, true, 1));
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

                // The modifiers take the scan codes of the target's layout, as send_keys' keys do; a plain click reads no layout
                var batch = NewBatch(modifiers.Count > 0 ? LayoutFor(target) : null)
                    .ModifiersDown(modifiers).MoveTo(x, y).Click(button, clicks).ModifiersUp(modifiers).Build();

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
    /// <param name="layout">The layout a batch's modifier keys take their scan codes from (mouse_click and drag); null for none.</param>
    private InputBatchBuilder NewBatch(KeyboardLayout? layout = null) =>
        new(_systemInfo?.GetVirtualScreen() is { Width: > 0, Height: > 0 } screen ? screen : InputBatchBuilder.VirtualScreenOf(_monitorService.GetAllMonitors()),
            _systemInfo?.AreMouseButtonsSwapped() ?? false, layout);

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
    /// <paramref name="batch"/> with a key-down in front of it for every modifier the server holds down now: what the
    /// batch's keys are pressed with when an earlier batch of the call left a modifier down (<c>{CTRL down}</c>). The chord
    /// checks read a batch alone, so without these a Ctrl+V or Ctrl+C split across batches would be no chord: the paste
    /// rule would not check the clipboard again before the V (and paste what the user copied in between), and the clipboard
    /// origin would take the agent's copy for the user's. Read only, never sent.
    /// </summary>
    private IReadOnlyList<InputEvent> WithServerHeldModifiers(IReadOnlyList<InputEvent> batch)
    {
        var held = _session.ServerHeld.Where(h => h.IsModifier).ToList();
        return held.Count == 0
            ? batch
            : [.. held.Select(h => InputEvent.Key(h.Code, h.Flags | NativeEnums.KEYEVENTF_KEYDOWN, h.Scan)), .. batch];
    }

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

    /// <summary>
    /// Sends <paramref name="batch"/>; the clipboard origin is told about it as <paramref name="pressedAs"/> presses it: the
    /// mouse buttons as the user knows them, or the keys with the modifiers an earlier batch of the call holds down
    /// (<see cref="WithServerHeldModifiers"/>), so a Ctrl+C whose Ctrl went down in an earlier batch counts as the agent's copy.
    /// </summary>
    private InputSendOutcome Send(IReadOnlyList<InputEvent> batch, IReadOnlyList<InputEvent> pressedAs, InputGuardResult approved)
    {
        var copy = _clipboardOrigin?.BeforeInput(pressedAs, approved.ReceivingWindow?.ProcessId ?? 0, approved.Receiving, approved.Anchor);
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
        "reachable (a UAC prompt or a disconnected session), or the session may be locked. Try again; if it keeps failing, ask the user.";

    private string PausedMessage(string done)
    {
        var pausedBy = _session.PauseState?.PausedBy;
        return $"{done} Input is paused: {InputControlSources.PauseCause(pausedBy, _session.PauseChord)}. Stop and ask the user " +
               "how to continue. Do not try to resume input yourself: " +
               $"{InputControlSources.WhoResumes(pausedBy, _session.IsSharedPauseUnreadable)}.";
    }

    private string BusyMessage(string action) =>
        $"Another input or window call held the input gate for {_gateTimeout.TotalSeconds:0.#} s, so nothing was {action}. Try again " +
        "when it has finished.";

    private static string TypedSoFar(int length, int charactersTyped, InputGuardResult? approved) => charactersTyped == 0
        ? "Nothing was typed."
        : $"Typing stopped after {charactersTyped} of {length} characters, which went to {Describe(approved?.ReceivingWindow)}.";

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
    /// How many characters <paramref name="text"/> has, as type_text counts them: code points, so an emoji (a surrogate
    /// pair) is one, and a lone surrogate is one too.
    /// </summary>
    private static int CountCharacters(string text)
    {
        var count = 0;
        for (var i = 0; i < text.Length; i++)
        {
            if (char.IsHighSurrogate(text[i]) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]))
            {
                i++;
            }

            count++;
        }

        return count;
    }

    /// <summary>The text with every Windows line break (\r\n) and every lone \r as \n.</summary>
    private static string NormalizeLineBreaks(string text) => text.Replace("\r\n", "\n").Replace('\r', '\n');

    /// <summary>
    /// The events that type <paramref name="text"/>, one array per character (<see cref="CountCharacters"/>), so a batch
    /// that is not sent never leaves half a character typed: a key-down and key-up as Unicode for every UTF-16 code unit,
    /// except a line break or tab that <paramref name="mode"/> presses as a key; with <paramref name="viaLayout"/>
    /// (method keys, roadmap M4.4) every other character as send_keys types it, with the key of
    /// <paramref name="layout"/> and the Shift or AltGr it needs, or as Unicode when the layout has no key for it.
    /// </summary>
    private static List<InputEvent[]> CharacterInputs(string text, TextControlChars mode, KeyboardLayout layout, bool viaLayout)
    {
        var characters = new List<InputEvent[]>(text.Length);
        for (var i = 0; i < text.Length; i++)
        {
            var character = text[i];
            if (char.IsHighSurrogate(character) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]))
            {
                characters.Add([.. Unicode(character), .. Unicode(text[++i])]);
                continue;
            }

            characters.Add(character switch
            {
                '\n' when mode == TextControlChars.Keys => Press(NativeEnums.VK_RETURN),
                '\n' when mode == TextControlChars.ShiftEnter =>
                [
                    KeyEvent(NativeEnums.VK_SHIFT, NativeEnums.KEYEVENTF_KEYDOWN, layout), .. Press(NativeEnums.VK_RETURN),
                    KeyEvent(NativeEnums.VK_SHIFT, NativeEnums.KEYEVENTF_KEYUP, layout)
                ],
                '\t' when mode == TextControlChars.Keys => Press(NativeEnums.VK_TAB),
                '\n' or '\t' => Unicode(character),
                // A character in braces is that character to send_keys (roadmap M4.1), mapped as it maps characters
                _ when viaLayout && !char.IsControl(character) && !char.IsSurrogate(character) =>
                    [.. KeySpecParser.Parse("{" + character + "}", layout).Events],
                _ => Unicode(character)
            });
        }

        return characters;

        InputEvent[] Press(ushort virtualKey) =>
            [KeyEvent(virtualKey, NativeEnums.KEYEVENTF_KEYDOWN, layout), KeyEvent(virtualKey, NativeEnums.KEYEVENTF_KEYUP, layout)];

        static InputEvent[] Unicode(char c) =>
            [InputEvent.Unicode(c, NativeEnums.KEYEVENTF_KEYDOWN), InputEvent.Unicode(c, NativeEnums.KEYEVENTF_KEYUP)];
    }

    private static InputEvent KeyEvent(ushort virtualKey, uint flags, KeyboardLayout layout)
    {
        var (scan, extended) = layout.ScanOf(virtualKey);
        return InputEvent.Key(virtualKey, flags | (extended ? NativeEnums.KEYEVENTF_EXTENDEDKEY : 0), scan);
    }

    /// <summary>How many characters of <paramref name="batch"/> have all their events within the first <paramref name="inserted"/>.</summary>
    private static int CharactersWithin(InputEvent[][] batch, int inserted)
    {
        var characters = 0;
        foreach (var character in batch)
        {
            inserted -= character.Length;
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
        private readonly CancellationToken _requestToken;
        private readonly CancellationToken _pauseToken;
        private CancellationTokenSource _linked;
        private CancellationTokenSource? _beforeWatch;
        private TakeoverWatch _takeover = TakeoverWatch.None;

        public InputCall(IInputSession session, CancellationToken requestToken)
        {
            _session = session;
            _requestToken = requestToken;
            _pauseToken = session.PauseToken;
            _linked = CancellationTokenSource.CreateLinkedTokenSource(requestToken, _pauseToken);
        }

        /// <summary>
        /// Cancelled when the request is cancelled or the user pauses input, and once <see cref="WatchForTakeover"/> ran with
        /// mode stop, when the user uses the keyboard or mouse.
        /// </summary>
        public CancellationToken Token => _linked.Token;

        /// <summary>True when the user has paused input (since before or during this call).</summary>
        public bool IsPaused => _pauseToken.IsCancellationRequested || _session.IsPaused;

        /// <summary>True when takeover detection stopped the call (mode stop), and the user did not pause input.</summary>
        public bool Interrupted => _takeover.Interrupted && !IsPaused;

        /// <summary>
        /// The warning the result carries about takeover detection (roadmap M7.4), or null: with mode warn, that the user used
        /// the keyboard or mouse during the watched part; with warn or stop, that detection did not work for some of it, so the
        /// user's input was not watched then (<see cref="TakeoverMessages.For"/>, from the watch itself).
        /// <paramref name="what"/> names the operation, such as "the text was typed".
        /// </summary>
        public string? TakeoverWarning(string what) => TakeoverMessages.For(_takeover, what);

        /// <summary>
        /// Watches for the user's input from now on (roadmap M7.4): with mode stop, <see cref="Token"/> is cancelled too when
        /// the user acts. Only the long operations call it (type_text's typing, drag's press to drop), right before they send.
        /// </summary>
        public void WatchForTakeover(ITakeoverMonitor? monitor)
        {
            if (monitor == null || monitor.Mode == TakeoverMode.Off || _beforeWatch != null)
            {
                return;
            }

            _takeover = monitor.Watch();
            _beforeWatch = _linked;
            _linked = CancellationTokenSource.CreateLinkedTokenSource(_requestToken, _pauseToken, _takeover.Token);
        }

        public void Dispose()
        {
            _takeover.Dispose();
            _linked.Dispose();
            _beforeWatch?.Dispose();
        }
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
