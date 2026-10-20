using WindowsAppManagerMcp.Native;
using WindowsAppManagerMcp.Services.Interfaces;

namespace WindowsAppManagerMcp.Services;

/// <summary>
/// The key that types a character, and the modifiers it needs: Shift, or Ctrl and Alt together for AltGr (roadmap M4.1).
/// </summary>
public readonly record struct KeyStroke(ushort VirtualKey, KeyModifiers Modifiers)
{
    /// <summary>
    /// True when the character needs AltGr ('@' on a Norwegian or German keyboard). It is sent as the left Ctrl and the
    /// right Alt key, as a keyboard sends AltGr.
    /// </summary>
    public bool IsAltGr => (Modifiers & (KeyModifiers.Control | KeyModifiers.Alt)) == (KeyModifiers.Control | KeyModifiers.Alt);
}

/// <summary>
/// One keyboard layout as send_keys reads it for one call (roadmap M4.1): which key types a character, with which
/// modifiers, and each key's scan code. Built from the layout of the window the keys go to, and the Caps Lock state at
/// the start of the call. Not thread-safe; each call builds its own (the shared <see cref="Invariant"/> keeps nothing).
/// </summary>
public sealed class KeyboardLayout
{
    private const byte ShiftState = 1, CtrlState = 2, AltState = 4;

    private readonly INativeKeyboardLayoutWrapper? _native;
    private readonly nint _layout;
    private readonly Dictionary<(char, bool, bool), KeyStroke?> _strokes = new();
    private readonly Dictionary<ushort, uint> _scanCodes = new();
    private readonly Dictionary<(ushort, byte), string?> _deadKeys = new();
    private string? _pendingAccent;
    private bool _pendingAccentRead;

    private KeyboardLayout(INativeKeyboardLayoutWrapper? native, nint layout, bool capsLock, bool readFailed = false)
    {
        _native = native;
        _layout = layout;
        CapsLock = capsLock;
        ReadFailed = readFailed;
    }

    /// <summary>
    /// A layout that knows only the keys every layout has: the letters A-Z and digits by their virtual keys, and the
    /// space. Every other character is sent as Unicode, and no key has a scan code. Used where no layout can be read
    /// (the destructive-chord settings, and tests without a layout).
    /// </summary>
    public static KeyboardLayout Invariant { get; } = new(null, 0, capsLock: false);

    /// <summary>
    /// The layout of the thread that owns <paramref name="window"/> (the foreground window's when it is 0); the
    /// <see cref="Invariant"/> mapping when it cannot be read.
    /// </summary>
    public static KeyboardLayout Of(INativeKeyboardLayoutWrapper native, nint window, bool capsLock)
    {
        ArgumentNullException.ThrowIfNull(native);
        var layout = native.GetKeyboardLayout(window);
        return new KeyboardLayout(layout == 0 ? null : native, layout, capsLock, readFailed: layout == 0);
    }

    /// <summary>True when Caps Lock was on at the start of the call; the parser follows the call's own {CAPSLOCK} presses.</summary>
    public bool CapsLock { get; }

    /// <summary>
    /// True when <see cref="Of"/> could not read the window's layout and maps with the <see cref="Invariant"/> keys instead:
    /// no key then has a scan code, and every character but a letter, a digit or the space goes as Unicode, so the result
    /// warns (KeyboardLayoutUnknown). False for <see cref="Invariant"/> itself, which is used on purpose.
    /// </summary>
    public bool ReadFailed { get; }

    /// <summary>The input locale (HKL) read for the window, whose low word is its language; 0 when none was read.</summary>
    public nint Handle => _layout;

    /// <summary>
    /// True when an accent typed on a dead key is waiting on the layout for its letter. Windows keeps that accent in the
    /// layout, which every thread shares, so one that the target (or the user) left waiting changes what every key types
    /// until a key uses it up: the space bar then types the accent itself, which is how it is read. False for a layout that
    /// was not read.
    /// </summary>
    public bool DeadKeyPending => _native != null && PendingAccent() != null;

    /// <summary>
    /// The key that types <paramref name="character"/>, or null when no key of the layout types exactly that character
    /// without a dead key (an accent that waits for the next key): it is then sent as Unicode. With
    /// <paramref name="capsLock"/>, the key is the one that types the character while Caps Lock is on: Shift is added or
    /// dropped wherever the layout lets Caps Lock flip the key (letters, and on some layouts the digit row).
    /// </summary>
    /// <param name="character">The character.</param>
    /// <param name="capsLock">Whether Caps Lock is on for this character.</param>
    /// <param name="asKey">
    /// True when the character names a key of a shortcut (a press_keys or send_keys chord with Ctrl, Alt or Win) rather than
    /// text to type: then a key that types the character after an accent left waiting on the layout
    /// (<see cref="DeadKeyPending"/>) is still its key. Issue #8: once Alt+Shift+\ on a Norwegian keyboard had left a grave
    /// accent waiting, the '+' key typed "`+", so 'alt+shift+plus' went out as the character, with no key. As text (and in a
    /// chord without those modifiers, which types) the character stays Unicode then, since the key would type the accent too.
    /// </param>
    public KeyStroke? Map(char character, bool capsLock, bool asKey = false)
    {
        var caps = capsLock;
        if (_native == null)
        {
            // Not cached: the invariant layout is shared by every call
            return InvariantStroke(character, caps);
        }

        if (_strokes.TryGetValue((character, caps, asKey), out var known))
        {
            return known;
        }

        var stroke = NativeStroke(character, caps, asKey);
        _strokes[(character, caps, asKey)] = stroke;
        return stroke;
    }

    /// <summary>
    /// Why <paramref name="character"/>, which <see cref="Map"/> gave no key, went out as Unicode, for the SentAsCharacter
    /// warning (issue #9): the layout was not read; or the accent waiting on the layout changed what the character's key
    /// types, putting itself in front ("`+") or combining with it ('è' for 'e'); or no key types it, or only a dead key does.
    /// Given per character, so a warning blames the accent only for the characters whose key it changed. A dead key's own
    /// character counts as the accent's too then: ToUnicodeEx gives the accent and the character for it just as for a key the
    /// accent does not combine with, so the two cannot be told apart while an accent waits, and the warning says only what the
    /// key would have typed, not that it types the character once the accent is used up.
    /// </summary>
    public NoKeyReason WhyNoKey(char character) =>
        ReadFailed ? NoKeyReason.LayoutUnread
        : DeadKeyPending && LayoutKey(character) is (var virtualKey, var state) &&
          _native!.KeyText(virtualKey, RawScanCode(virtualKey), state, capsLock: false, _layout) != null ? NoKeyReason.AccentWaiting
        : NoKeyReason.NotOnLayout;

    /// <summary>
    /// The accent <paramref name="virtualKey"/> leaves waiting for the next key when it is pressed with <paramref name="held"/>
    /// down and is a dead key then (issue #8: Alt+Shift+\ on a Norwegian keyboard is the dead grave accent), read from the
    /// layout without changing its state; null when it is no dead key then, or the layout was not read. What the key types
    /// is chosen by Shift, Ctrl, and Ctrl with Alt (AltGr), with Caps Lock as it was at the start of the call; Alt without Ctrl
    /// is left out, since Windows types a key's own character with Alt down, and Win is no modifier of a layout. While an
    /// accent already waits, a dead key types it and its own, and leaves nothing waiting.
    /// </summary>
    public string? DeadKeyOf(ushort virtualKey, KeyModifiers held)
    {
        if (_native == null)
        {
            return null;
        }

        var state = (byte)(((held & KeyModifiers.Shift) != 0 ? ShiftState : 0) | ((held & KeyModifiers.Control) != 0 ? CtrlState : 0));
        if ((held & (KeyModifiers.Control | KeyModifiers.Alt)) == (KeyModifiers.Control | KeyModifiers.Alt))
        {
            state |= AltState;
        }

        if (!_deadKeys.TryGetValue((virtualKey, state), out var accent))
        {
            accent = _native.DeadKeyText(virtualKey, RawScanCode(virtualKey), state, CapsLock, _layout);
            _deadKeys[(virtualKey, state)] = accent;
        }

        return accent;
    }

    /// <summary>
    /// The scan code to send with <paramref name="virtualKey"/> (without the 0xE0 prefix), and whether it is an extended
    /// key. The invariant layout has no scan codes.
    /// </summary>
    public (ushort Scan, bool Extended) ScanOf(ushort virtualKey)
    {
        var raw = RawScanCode(virtualKey);
        return (raw >> 8) switch
        {
            0xE0 => ((ushort)(raw & 0xFF), true),
            // Pause's E1 1D 45 sequence has no single scan code
            0xE1 => (0, false),
            _ => ((ushort)(raw & 0xFF), KeyTable.IsExtended(virtualKey))
        };
    }

    private uint RawScanCode(ushort virtualKey)
    {
        if (_native == null)
        {
            return 0;
        }

        if (!_scanCodes.TryGetValue(virtualKey, out var raw))
        {
            raw = _native.ScanCode(virtualKey, _layout);
            _scanCodes[virtualKey] = raw;
        }

        return raw;
    }

    /// <summary>
    /// The key VkKeyScanEx gives for <paramref name="character"/> and the shift state it needs; null when the layout has none,
    /// or the state goes beyond Shift and AltGr (Kana, layout-specific bits), or is Ctrl or Alt alone, which type control
    /// characters, not text.
    /// </summary>
    private (byte VirtualKey, byte State)? LayoutKey(char character)
    {
        var scan = _native!.VkKeyScan(character, _layout);
        var virtualKey = (byte)(scan & 0xFF);
        var state = (byte)((scan >> 8) & 0xFF);
        return scan == -1 || virtualKey == 0xFF || (state & ~(ShiftState | CtrlState | AltState)) != 0 ||
               (state & (CtrlState | AltState)) is CtrlState or AltState
            ? null
            : (virtualKey, state);
    }

    private KeyStroke? NativeStroke(char character, bool caps, bool asKey)
    {
        if (LayoutKey(character) is not (var virtualKey, var state))
        {
            return null;
        }

        // The key must type exactly this character: not a dead key, and with Caps Lock on, possibly with Shift flipped. A key
        // pressed as a key may also type it behind an accent that waits on the layout, which ToUnicodeEx puts in front
        var text = character.ToString();
        var rawScan = RawScanCode(virtualKey);
        var typed = _native!.KeyText(virtualKey, rawScan, state, caps, _layout);
        if (typed == text || (asKey && typed is { Length: > 1 } && PendingAccent() is { } accent && typed == accent + text))
        {
            return new KeyStroke(virtualKey, ModifiersOf(state));
        }

        var flipped = (byte)(state ^ ShiftState);
        return caps && _native.KeyText(virtualKey, rawScan, flipped, caps, _layout) == text
            ? new KeyStroke(virtualKey, ModifiersOf(flipped))
            : null;
    }

    /// <summary>
    /// The accent waiting on the layout (see <see cref="DeadKeyPending"/>), read once: what the space bar types, which is a
    /// space when none waits, and the accent alone when one does (or the accent and a space, when the layout does not
    /// combine the two). Null when none waits, or the space bar types nothing.
    /// </summary>
    private string? PendingAccent()
    {
        if (!_pendingAccentRead)
        {
            _pendingAccentRead = true;
            var space = _native!.KeyText(NativeEnums.VK_SPACE, RawScanCode(NativeEnums.VK_SPACE), 0, capsLock: false, _layout);
            _pendingAccent = space switch
            {
                null or " " => null,
                { Length: 2 } when space[1] == ' ' => space[..1],
                _ => space
            };
        }

        return _pendingAccent;
    }

    private static KeyStroke? InvariantStroke(char character, bool caps)
    {
        if (char.IsAsciiLetterLower(character))
        {
            return new KeyStroke(char.ToUpperInvariant(character), caps ? KeyModifiers.Shift : KeyModifiers.None);
        }

        if (char.IsAsciiLetterUpper(character))
        {
            return new KeyStroke(character, caps ? KeyModifiers.None : KeyModifiers.Shift);
        }

        return char.IsAsciiDigit(character) || character == ' ' ? new KeyStroke(character, KeyModifiers.None) : null;
    }

    private static KeyModifiers ModifiersOf(byte state) =>
        ((state & ShiftState) != 0 ? KeyModifiers.Shift : KeyModifiers.None) |
        ((state & CtrlState) != 0 ? KeyModifiers.Control : KeyModifiers.None) |
        ((state & AltState) != 0 ? KeyModifiers.Alt : KeyModifiers.None);
}
