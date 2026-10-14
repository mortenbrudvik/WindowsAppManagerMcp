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
/// the start of the call. Not thread-safe; each call builds its own.
/// </summary>
public sealed class KeyboardLayout
{
    private const byte ShiftState = 1, CtrlState = 2, AltState = 4;

    private readonly INativeKeyboardLayoutWrapper? _native;
    private readonly nint _layout;
    private readonly Dictionary<(char, bool), KeyStroke?> _strokes = new();
    private readonly Dictionary<ushort, uint> _scanCodes = new();

    private KeyboardLayout(INativeKeyboardLayoutWrapper? native, nint layout, bool capsLock)
    {
        _native = native;
        _layout = layout;
        CapsLock = capsLock;
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
        return new KeyboardLayout(layout == 0 ? null : native, layout, capsLock);
    }

    /// <summary>True when Caps Lock was on at the start of the call.</summary>
    public bool CapsLock { get; }

    /// <summary>
    /// The key that types <paramref name="character"/>, or null when no key of the layout types exactly that character
    /// without a dead key (an accent that waits for the next key): it is then sent as Unicode. With
    /// <paramref name="capsLockApplies"/> and <see cref="CapsLock"/> on, the key is the one that types the character while
    /// Caps Lock is on: Shift is added or dropped wherever the layout lets Caps Lock flip the key (letters, and on some
    /// layouts the digit row).
    /// </summary>
    public KeyStroke? Map(char character, bool capsLockApplies)
    {
        var caps = capsLockApplies && CapsLock;
        if (_strokes.TryGetValue((character, caps), out var known))
        {
            return known;
        }

        var stroke = _native == null ? InvariantStroke(character, caps) : NativeStroke(character, caps);
        _strokes[(character, caps)] = stroke;
        return stroke;
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

    private KeyStroke? NativeStroke(char character, bool caps)
    {
        var scan = _native!.VkKeyScan(character, _layout);
        var virtualKey = (byte)(scan & 0xFF);
        var state = (byte)((scan >> 8) & 0xFF);

        // Not on the layout; or a state beyond Shift and AltGr (Kana, layout-specific bits); or Ctrl or Alt alone, which
        // type control characters, not text
        if (scan == -1 || virtualKey == 0xFF || (state & ~(ShiftState | CtrlState | AltState)) != 0 ||
            (state & (CtrlState | AltState)) is CtrlState or AltState)
        {
            return null;
        }

        // The key must type exactly this character: not a dead key, and with Caps Lock on, possibly with Shift flipped
        var text = character.ToString();
        var rawScan = RawScanCode(virtualKey);
        if (_native.KeyText(virtualKey, rawScan, state, caps, _layout) == text)
        {
            return new KeyStroke(virtualKey, ModifiersOf(state));
        }

        var flipped = (byte)(state ^ ShiftState);
        return caps && _native.KeyText(virtualKey, rawScan, flipped, caps, _layout) == text
            ? new KeyStroke(virtualKey, ModifiersOf(flipped))
            : null;
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
