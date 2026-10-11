using WindowsAppManagerMcp.Models;
using WindowsAppManagerMcp.Native;

namespace WindowsAppManagerMcp.Services;

/// <summary>Modifier keys held down while a key is pressed.</summary>
[Flags]
public enum KeyModifiers
{
    None = 0,
    Control = 1,
    Alt = 2,
    Shift = 4,
    Windows = 8
}

/// <summary>
/// One key press and the modifiers held down while it happens, for example Alt+F4. <see cref="Kind"/> and
/// <see cref="Code"/> are those of the key's <see cref="InputEvent"/>: a virtual-key code, or a character
/// sent as Unicode. A character that has a key of its own (a space, a letter or digit, Tab, Enter,
/// Backspace, Esc) counts as that virtual key, so every spelling of a chord matches it: send_keys sends a
/// literal space as a character, and Alt with that character opens the window menu as Alt+{SPACE} does.
/// </summary>
public readonly record struct KeyChord(KeyModifiers Modifiers, InputEventKind Kind, ushort Code)
{
    /// <summary>
    /// True when <paramref name="pressed"/> is this chord or the same key with more modifiers held down:
    /// Ctrl+Shift+W counts as Ctrl+W, because applications act on both (Chrome closes the window on it).
    /// </summary>
    public bool IsPartOf(KeyChord pressed) =>
        pressed.Kind == Kind && pressed.Code == Code && (pressed.Modifiers & Modifiers) == Modifiers;
}

/// <summary>
/// Parses send_keys' SendKeys-style syntax into input events: <c>^</c> Ctrl, <c>%</c> Alt and <c>+</c>
/// Shift apply to the next key; <c>{NAME}</c> is a special key; any other character is a key.
/// </summary>
public static class SendKeysParser
{
    /// <summary>
    /// The events for <paramref name="keys"/>: modifiers down, the key down and up, modifiers up.
    /// <paramref name="windowsModifier"/> also reads <c>#</c> as the Windows-key modifier; send_keys does
    /// not (there <c>#</c> is the character), only the destructive-chord settings do.
    /// </summary>
    /// <exception cref="KeySyntaxException">A brace is not closed (InvalidKeySpecification).</exception>
    /// <exception cref="ArgumentException">A key name is unknown (UnknownKey).</exception>
    public static List<InputEvent> Parse(string keys, bool windowsModifier = false)
    {
        ArgumentNullException.ThrowIfNull(keys);

        var inputs = new List<InputEvent>();
        var modifiers = new List<ushort>();
        var i = 0;

        while (i < keys.Length)
        {
            var c = keys[i];

            // Handle modifier prefixes
            if (c == '^' || c == '%' || c == '+' || (windowsModifier && c == '#'))
            {
                var modifierVk = c switch
                {
                    '^' => NativeEnums.VK_CONTROL,
                    '%' => NativeEnums.VK_MENU,
                    '+' => NativeEnums.VK_SHIFT,
                    '#' => NativeEnums.VK_LWIN,
                    _ => (ushort)0
                };

                if (modifierVk != 0 && !modifiers.Contains(modifierVk))
                {
                    modifiers.Add(modifierVk);
                    var flags = IsExtendedKey(modifierVk) ? NativeEnums.KEYEVENTF_EXTENDEDKEY : NativeEnums.KEYEVENTF_KEYDOWN;
                    inputs.Add(InputEvent.Key(modifierVk, flags));
                }
                i++;
                continue;
            }

            // Handle special keys in braces
            if (c == '{')
            {
                var endBrace = keys.IndexOf('}', i + 1);
                if (endBrace == -1)
                {
                    throw new KeySyntaxException($"Unclosed brace at position {i}");
                }

                var keyName = keys.Substring(i + 1, endBrace - i - 1).ToUpperInvariant();
                var vk = GetVirtualKeyCode(keyName);
                var flags = IsExtendedKey(vk) ? NativeEnums.KEYEVENTF_EXTENDEDKEY : NativeEnums.KEYEVENTF_KEYDOWN;

                inputs.Add(InputEvent.Key(vk, flags));
                inputs.Add(InputEvent.Key(vk, flags | NativeEnums.KEYEVENTF_KEYUP));

                // Release modifiers after the key
                ReleaseModifiers(inputs, modifiers);
                modifiers.Clear();

                i = endBrace + 1;
                continue;
            }

            // Handle regular characters
            var charVk = GetVirtualKeyForChar(c);
            if (charVk != 0)
            {
                inputs.Add(InputEvent.Key(charVk, NativeEnums.KEYEVENTF_KEYDOWN));
                inputs.Add(InputEvent.Key(charVk, NativeEnums.KEYEVENTF_KEYUP));
            }
            else
            {
                // Send as Unicode
                inputs.Add(InputEvent.Unicode(c, NativeEnums.KEYEVENTF_KEYDOWN));
                inputs.Add(InputEvent.Unicode(c, NativeEnums.KEYEVENTF_KEYUP));
            }

            // Release modifiers after the key
            ReleaseModifiers(inputs, modifiers);
            modifiers.Clear();

            i++;
        }

        // Release any remaining modifiers
        ReleaseModifiers(inputs, modifiers);

        return inputs;
    }

    /// <summary>
    /// Every key press in <paramref name="events"/> with the modifiers held down at that moment. Presses of
    /// the modifier keys themselves are not chords. Reads the events as they would arrive, so a modifier
    /// sent as its own key (<c>{ALT}</c>) is released again before the next key and holds nothing.
    /// </summary>
    public static IReadOnlyList<KeyChord> Chords(IReadOnlyList<InputEvent> events) =>
        IndexedChords(events).Select(c => c.Chord).ToList();

    /// <summary>
    /// The chords of <see cref="Chords"/>, each with the index in <paramref name="events"/> of its key-down, so their
    /// order against other events (a mouse button) can be told.
    /// </summary>
    public static IReadOnlyList<(KeyChord Chord, int Index)> IndexedChords(IReadOnlyList<InputEvent> events)
    {
        ArgumentNullException.ThrowIfNull(events);

        var chords = new List<(KeyChord, int)>();
        var held = KeyModifiers.None;
        for (var i = 0; i < events.Count; i++)
        {
            var e = events[i];
            var isUp = (e.Flags & NativeEnums.KEYEVENTF_KEYUP) != 0;
            if (e.Kind == InputEventKind.VirtualKey && ModifierOf(e.Code) is var modifier && modifier != KeyModifiers.None)
            {
                held = isUp ? held & ~modifier : held | modifier;
                continue;
            }

            if (e.Kind != InputEventKind.Mouse && !isUp)
            {
                chords.Add((Press(held, e), i));
            }
        }

        return chords;
    }

    /// <summary>
    /// Every key press in <paramref name="events"/>, as <see cref="Chords"/> reads them, plus a modifier key
    /// that is tapped: pressed and released with no other key pressed in between, such as <c>{WIN}</c>
    /// (which opens the Start menu) or <c>{ALT}</c>. The tap counts as a press of that key with nothing held.
    /// </summary>
    public static IReadOnlyList<KeyChord> KeyPresses(IReadOnlyList<InputEvent> events)
    {
        ArgumentNullException.ThrowIfNull(events);

        var presses = new List<KeyChord>();
        var held = KeyModifiers.None;
        var tapCandidate = (ushort)0;
        foreach (var e in events)
        {
            var isUp = (e.Flags & NativeEnums.KEYEVENTF_KEYUP) != 0;
            if (e.Kind == InputEventKind.VirtualKey && ModifierOf(e.Code) is var modifier && modifier != KeyModifiers.None)
            {
                if (!isUp)
                {
                    held |= modifier;
                    tapCandidate = e.Code;
                    continue;
                }

                held &= ~modifier;
                if (tapCandidate == e.Code)
                {
                    presses.Add(new KeyChord(held, InputEventKind.VirtualKey, e.Code));
                }

                tapCandidate = 0;
                continue;
            }

            if (e.Kind != InputEventKind.Mouse && !isUp)
            {
                presses.Add(Press(held, e));
                tapCandidate = 0;
            }
        }

        return presses;
    }

    /// <summary>
    /// The press of <paramref name="key"/> with <paramref name="held"/> down. A character sent as Unicode that
    /// has a key of its own counts as that virtual key (see <see cref="KeyChord"/>).
    /// </summary>
    private static KeyChord Press(KeyModifiers held, InputEvent key) =>
        key.Kind == InputEventKind.Unicode && VirtualKeyOfCharacter((char)key.Code) is var vk && vk != 0
            ? new KeyChord(held, InputEventKind.VirtualKey, vk)
            : new KeyChord(held, key.Kind, key.Code);

    /// <summary>The virtual key that types <paramref name="c"/> by itself, or 0 when it has none.</summary>
    private static ushort VirtualKeyOfCharacter(char c) => c switch
    {
        ' ' => NativeEnums.VK_SPACE,
        '\t' => NativeEnums.VK_TAB,
        '\r' or '\n' => NativeEnums.VK_RETURN,
        '\b' => NativeEnums.VK_BACK,
        '\u001b' => NativeEnums.VK_ESCAPE,
        _ => GetVirtualKeyForChar(c)
    };

    private static KeyModifiers ModifierOf(ushort virtualKey) => virtualKey switch
    {
        NativeEnums.VK_CONTROL or NativeEnums.VK_LCONTROL or NativeEnums.VK_RCONTROL => KeyModifiers.Control,
        NativeEnums.VK_MENU or NativeEnums.VK_LMENU or NativeEnums.VK_RMENU => KeyModifiers.Alt,
        NativeEnums.VK_SHIFT or NativeEnums.VK_LSHIFT or NativeEnums.VK_RSHIFT => KeyModifiers.Shift,
        NativeEnums.VK_LWIN or NativeEnums.VK_RWIN => KeyModifiers.Windows,
        _ => KeyModifiers.None
    };

    private static void ReleaseModifiers(List<InputEvent> inputs, List<ushort> modifiers)
    {
        // Release in reverse order
        for (var i = modifiers.Count - 1; i >= 0; i--)
        {
            var flags = IsExtendedKey(modifiers[i]) ? NativeEnums.KEYEVENTF_EXTENDEDKEY : NativeEnums.KEYEVENTF_KEYDOWN;
            inputs.Add(InputEvent.Key(modifiers[i], flags | NativeEnums.KEYEVENTF_KEYUP));
        }
    }

    private static ushort GetVirtualKeyCode(string keyName)
    {
        return keyName switch
        {
            "ENTER" or "RETURN" => NativeEnums.VK_RETURN,
            "TAB" => NativeEnums.VK_TAB,
            "ESC" or "ESCAPE" => NativeEnums.VK_ESCAPE,
            "BACKSPACE" or "BS" or "BKSP" => NativeEnums.VK_BACK,
            "DELETE" or "DEL" => NativeEnums.VK_DELETE,
            "INSERT" or "INS" => NativeEnums.VK_INSERT,
            "UP" => NativeEnums.VK_UP,
            "DOWN" => NativeEnums.VK_DOWN,
            "LEFT" => NativeEnums.VK_LEFT,
            "RIGHT" => NativeEnums.VK_RIGHT,
            "HOME" => NativeEnums.VK_HOME,
            "END" => NativeEnums.VK_END,
            "PGUP" or "PAGEUP" => NativeEnums.VK_PRIOR,
            "PGDN" or "PAGEDOWN" => NativeEnums.VK_NEXT,
            "SPACE" => NativeEnums.VK_SPACE,
            "F1" => NativeEnums.VK_F1,
            "F2" => NativeEnums.VK_F2,
            "F3" => NativeEnums.VK_F3,
            "F4" => NativeEnums.VK_F4,
            "F5" => NativeEnums.VK_F5,
            "F6" => NativeEnums.VK_F6,
            "F7" => NativeEnums.VK_F7,
            "F8" => NativeEnums.VK_F8,
            "F9" => NativeEnums.VK_F9,
            "F10" => NativeEnums.VK_F10,
            "F11" => NativeEnums.VK_F11,
            "F12" => NativeEnums.VK_F12,
            "CAPSLOCK" or "CAPS" => NativeEnums.VK_CAPITAL,
            "NUMLOCK" => NativeEnums.VK_NUMLOCK,
            "SCROLLLOCK" or "SCROLL" => NativeEnums.VK_SCROLL,
            "PRTSC" or "PRINTSCREEN" => NativeEnums.VK_SNAPSHOT,
            "PAUSE" or "BREAK" => NativeEnums.VK_PAUSE,
            "WIN" or "LWIN" => NativeEnums.VK_LWIN,
            "RWIN" => NativeEnums.VK_RWIN,
            "APPS" or "CONTEXTMENU" => NativeEnums.VK_APPS,
            "CTRL" or "CONTROL" => NativeEnums.VK_CONTROL,
            "ALT" or "MENU" => NativeEnums.VK_MENU,
            "SHIFT" => NativeEnums.VK_SHIFT,
            _ => throw new ArgumentException($"Unknown key: {keyName}")
        };
    }

    private static bool IsExtendedKey(ushort vk)
    {
        // Extended keys include arrow keys, Insert, Delete, Home, End, Page Up, Page Down,
        // Num Lock, Print Screen, and the right-hand Alt and Ctrl keys
        return vk == NativeEnums.VK_UP || vk == NativeEnums.VK_DOWN ||
               vk == NativeEnums.VK_LEFT || vk == NativeEnums.VK_RIGHT ||
               vk == NativeEnums.VK_INSERT || vk == NativeEnums.VK_DELETE ||
               vk == NativeEnums.VK_HOME || vk == NativeEnums.VK_END ||
               vk == NativeEnums.VK_PRIOR || vk == NativeEnums.VK_NEXT ||
               vk == NativeEnums.VK_NUMLOCK || vk == NativeEnums.VK_SNAPSHOT ||
               vk == NativeEnums.VK_RCONTROL || vk == NativeEnums.VK_RMENU ||
               vk == NativeEnums.VK_LWIN || vk == NativeEnums.VK_RWIN ||
               vk == NativeEnums.VK_APPS;
    }

    private static ushort GetVirtualKeyForChar(char c)
    {
        // For alphanumeric characters, return the VK code
        if (c >= 'a' && c <= 'z')
        {
            return (ushort)('A' + (c - 'a'));
        }
        if (c >= 'A' && c <= 'Z')
        {
            return (ushort)c;
        }
        if (c >= '0' && c <= '9')
        {
            return (ushort)c;
        }

        // For other characters, return 0 to indicate Unicode should be used
        return 0;
    }
}

/// <summary>
/// The keys string is not valid SendKeys syntax, such as a brace that is not closed: a mistake the agent fixes in
/// the string, reported as InvalidKeySpecification, unlike an unknown key name (UnknownKey).
/// </summary>
public sealed class KeySyntaxException(string message) : ArgumentException(message);
