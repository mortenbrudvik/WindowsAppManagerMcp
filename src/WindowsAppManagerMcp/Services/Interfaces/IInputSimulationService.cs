using WindowsAppManagerMcp.Models;

namespace WindowsAppManagerMcp.Services.Interfaces;

/// <summary>
/// Service interface for simulating mouse and keyboard input. Every call passes the input guard
/// (<see cref="IInputTargetGuard"/>) before each SendInput batch, with or without a target window, and its
/// result reports the receiving window and the foreground window afterwards (mouse calls also the cursor
/// position).
/// </summary>
/// <remarks>
/// Session safety (roadmap M1.10), for every call: while the user has paused input it returns PausedByUser
/// and sends nothing, and a pause stops a running call between batches; it waits at most
/// <see cref="Services.InputGate.InputWaitTimeout"/> for the input gate (InputBusy) and stops when
/// <c>cancellationToken</c> is cancelled (the MCP request's token: OperationCanceledException); it returns
/// ModifiersHeld when Shift, Ctrl, Alt or Windows is down, unless <c>releaseHeldModifiers</c> releases them
/// first; and no key or button it presses stays down after it returns.
/// </remarks>
public interface IInputSimulationService
{
    /// <summary>
    /// Clicks the primary mouse button (the left one, or the right one when the user swapped the buttons) at the specified
    /// screen coordinates. With a target, the point must be on the target or a popup of it. The move to the point and the
    /// click go in one SendInput batch (roadmap M4.2); a pointer that is not at the point afterwards is CursorClamped, with
    /// the click sent.
    /// </summary>
    ClickResult Click(int x, int y, InputTarget target, bool releaseHeldModifiers, CancellationToken cancellationToken);

    /// <summary>
    /// Clicks the secondary mouse button (the right one, or the left one when the user swapped the buttons) at the specified
    /// screen coordinates, as <see cref="Click"/> does.
    /// </summary>
    ClickResult RightClick(int x, int y, InputTarget target, bool releaseHeldModifiers, CancellationToken cancellationToken);

    /// <summary>
    /// Double-clicks the primary mouse button at the specified screen coordinates, as <see cref="Click"/> does: both clicks
    /// go in the one batch with the move, so Windows counts them as one double click whatever its double-click time.
    /// </summary>
    ClickResult DoubleClick(int x, int y, InputTarget target, bool releaseHeldModifiers, CancellationToken cancellationToken);

    /// <summary>
    /// press_keys (roadmap M4.3): structured chords such as 'ctrl+shift+t', ['ctrl','shift','t'] or 'tab*3 enter'
    /// (<see cref="PressKeysParser"/>), mapped with the target window's keyboard layout and sent exactly as send_keys sends
    /// its keys: the pause chord is refused, the guard checks every chord first (DestructiveChordNeedsTarget,
    /// KeysAfterFocusChange), and each batch is checked again before it is sent. Without holdMs and delayBetweenMs every
    /// press goes in one batch; with holdMs a chord's keys stay down that long, within this call only. KeysSent counts the
    /// presses sent.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">repeat, holdMs or delayBetweenMs is out of range.</exception>
    /// <exception cref="ArgumentException">The presses would take longer than <see cref="InputSimulationService.MaxPressKeysDurationMs"/>.</exception>
    Task<SendKeysResult> PressKeysAsync(KeyList keys, int repeat, int holdMs, int delayBetweenMs, InputTarget target, bool releaseHeldModifiers,
        CancellationToken cancellationToken);

    /// <summary>
    /// mouse_click (roadmap M4.3): <paramref name="clickCount"/> clicks (1 to 3) of any button at a screen point, with
    /// <paramref name="modifiers"/> held around them. The modifier key-downs, the move to the point, the clicks and the
    /// modifier key-ups go in one SendInput batch, after every check <see cref="Click"/> makes; a right or middle click in a
    /// terminal follows the paste rule (PasteNotAllowed). <see cref="Click"/>, <see cref="RightClick"/> and
    /// <see cref="DoubleClick"/>, which only element_action's mouse path calls since 3.0.0 removed the click tools, are this
    /// with the primary or secondary button and no modifiers.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">clickCount outside 1 to 3.</exception>
    ClickResult MouseClick(int x, int y, MouseButton button, int clickCount, IReadOnlyList<MouseModifier> modifiers, InputTarget target,
        bool releaseHeldModifiers, CancellationToken cancellationToken);

    /// <summary>
    /// drag (roadmap M4.3): presses the button at the from point (with the modifiers down first), waits, moves the pointer to
    /// the to point in absolute moves (<see cref="DragPath"/>) and releases the button and the modifiers there. Not one batch:
    /// a pause or cancellation takes effect between the moves. Before the press the guard checks the from point against
    /// <paramref name="target"/> and the to point against <paramref name="dropTarget"/>, both as a click's point; the from
    /// point again after the pointer moved there; and the to point right before the release
    /// (<see cref="IInputTargetGuard.RecheckDrop"/>). A drag that must not drop (the to point refused then, the pointer kept
    /// away from it, a cancelled request) goes back to the from point and releases there. The user's pause releases the
    /// button at once, where the pointer is. Nothing stays held when it returns.
    /// </summary>
    /// <exception cref="ArgumentException">The request is out of range (<see cref="DragRequest.Invalid"/>).</exception>
    Task<DragResult> DragAsync(DragRequest request, InputTarget target, InputTarget dropTarget, bool releaseHeldModifiers,
        CancellationToken cancellationToken);

    /// <summary>
    /// Moves the mouse cursor to the specified screen coordinates without clicking, with an injected absolute move (roadmap
    /// M4.2); a pointer that is not at the point afterwards is CursorClamped.
    /// </summary>
    MouseMoveResult MoveMouse(int x, int y, InputTarget target, bool releaseHeldModifiers, CancellationToken cancellationToken);

    /// <summary>
    /// Gets the current cursor position.
    /// </summary>
    (int X, int Y)? GetCursorPosition();

    /// <summary>
    /// Turns the mouse wheel at a point (roadmap M2.15): moves the pointer there and sends one wheel event per notch, in
    /// one SendInput call. Without a point, at the centre of the target window's client area. The guard checks the point
    /// as for a click, and the wheel must reach the window at the point: when Windows sends the wheel to the focus window
    /// instead, the window at the point must be the foreground window (otherwise WheelFollowsFocus).
    /// </summary>
    /// <param name="x">The point's x, or null (with <paramref name="y"/>) for the target's client centre.</param>
    /// <param name="y">The point's y, or null.</param>
    /// <param name="deltaY">Vertical notches: positive scrolls down, negative up; at most <see cref="InputSimulationService.MaxScrollNotches"/> either way.</param>
    /// <param name="deltaX">Horizontal notches: positive scrolls right, negative left.</param>
    /// <param name="modifiers">
    /// Modifier keys held around the wheel (issue #8: Ctrl+wheel zooms, Shift+wheel scrolls sideways): down before the move
    /// and up after the notches, in the same batch, as mouse_click's; none stays down after the call.
    /// </param>
    /// <param name="target">The target window; required when no point is given.</param>
    /// <param name="releaseHeldModifiers">Release modifier keys that are down first, instead of returning ModifiersHeld.</param>
    /// <param name="cancellationToken">The request's token.</param>
    /// <exception cref="ArgumentException">Only one of x and y, no point and no target, or no notches or too many.</exception>
    ScrollResult Scroll(int? x, int? y, int deltaY, int deltaX, IReadOnlyList<MouseModifier> modifiers, InputTarget target,
        bool releaseHeldModifiers, CancellationToken cancellationToken);

    /// <summary>
    /// Checks if the given screen coordinates are within the bounds of any connected monitor.
    /// </summary>
    bool IsValidScreenCoordinate(int x, int y);

    /// <summary>
    /// Types text by simulating keyboard input using Unicode characters or the keys of the window's layout, or pastes it
    /// through the clipboard (roadmap M4.4). With a target, brings it to the front first; the foreground window is checked
    /// again before every batch of characters and right before a paste, and typing stops (TargetLostFocus, with
    /// CharactersTyped) when another window comes to the front, and (ModifiersHeld) when a modifier key goes down. Typing
    /// takes at most <see cref="InputSimulationService.MaxTypedLength"/> characters and a paste at most
    /// <see cref="InputSimulationService.MaxPasteLength"/> (TextTooLong). Keys that press the user's pause hotkey chord (an
    /// AltGr character of method keys, or a paste's Ctrl+V) are refused before anything is sent (PauseChordNotAllowed;
    /// auto types instead of pasting).
    /// </summary>
    /// <param name="text">The text to type.</param>
    /// <param name="delayBetweenKeysMs">Delay between keystrokes in milliseconds (0-100).</param>
    /// <param name="target">The target window, or <see cref="InputTarget.None"/>.</param>
    /// <param name="releaseHeldModifiers">Release modifier keys that are down before typing, instead of returning ModifiersHeld.</param>
    /// <param name="controlChars">
    /// What the text's line breaks (a Windows line break and a lone carriage return count as a line feed) and tabs do;
    /// null refuses text that has any (InvalidArguments).
    /// </param>
    /// <param name="method">
    /// unicode, keys or paste; auto pastes text longer than <see cref="InputSimulationService.AutoPasteThreshold"/> characters
    /// when it can, and types the rest as unicode.
    /// </param>
    /// <param name="cancellationToken">The request's token: cancelling it stops typing between batches (not a paste once sent).</param>
    Task<TypeTextResult> TypeTextAsync(string text, int delayBetweenKeysMs, InputTarget target, bool releaseHeldModifiers,
        TextControlChars? controlChars, TextInputMethod method, CancellationToken cancellationToken);

    /// <summary>
    /// Sends keys and key combinations in send_keys' grammar (<see cref="KeySpecParser"/>), every character mapped with the
    /// keyboard layout of the window they go to. Destructive chords need a target (DestructiveChordNeedsTarget); the user's
    /// pause hotkey chord is refused (PauseChordNotAllowed). At most <see cref="InputSimulationService.MaxSendKeysPresses"/>
    /// key presses and <see cref="InputSimulationService.MaxSendKeysEvents"/> events (TextTooLong), and at most
    /// <see cref="InputSimulationService.MaxSendKeysDurationMs"/> of waits (InvalidArguments), refused before anything is sent.
    /// </summary>
    /// <param name="keys">The keys to send.</param>
    /// <param name="target">The target window, or <see cref="InputTarget.None"/>.</param>
    /// <param name="releaseHeldModifiers">Release modifier keys that are down first, instead of returning ModifiersHeld.</param>
    /// <param name="delayMs">
    /// Milliseconds to wait between keys (0 to <see cref="InputSimulationService.MaxSendKeysDelayMs"/>), each key a SendInput
    /// batch of its own; 0 sends them in batches of whole keys of at most <see cref="InputSimulationService.SendKeysBatchSize"/>
    /// key-downs (a key with more, such as a long group, goes whole and alone), each checked again right before it.
    /// </param>
    /// <param name="cancellationToken">The request's token: cancelling it stops the keys between batches.</param>
    Task<SendKeysResult> SendKeysAsync(string keys, InputTarget target, bool releaseHeldModifiers, int delayMs, CancellationToken cancellationToken);
}
