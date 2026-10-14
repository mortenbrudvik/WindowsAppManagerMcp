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
/// send_keys' key grammar without a keyboard layout (see <see cref="KeySpecParser"/>), and the chords a batch of input
/// events presses, which the input guard, the pause hotkey and the clipboard checks read.
/// </summary>
public static class SendKeysParser
{
    /// <summary>
    /// The events for <paramref name="keys"/> on the <see cref="KeyboardLayout.Invariant"/> layout: letters and digits by
    /// their keys (Shift for capitals), every other character as Unicode, no scan codes. For the settings and checks that
    /// read chords, where no window's layout applies; send_keys itself maps with the target's layout.
    /// </summary>
    /// <exception cref="KeySyntaxException">The string breaks the grammar (InvalidKeySpecification).</exception>
    /// <exception cref="ArgumentException">A key name is unknown (UnknownKey).</exception>
    public static List<InputEvent> Parse(string keys) => KeySpecParser.Parse(keys, KeyboardLayout.Invariant).Events.ToList();

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

    /// <summary>
    /// <paramref name="chord"/> written in send_keys syntax, for messages: <c>#r</c>, <c>^+{F5}</c>, <c>%{VK 0xE8}</c> for a
    /// key without a name.
    /// </summary>
    public static string Spec(KeyChord chord)
    {
        var modifiers = ((chord.Modifiers & KeyModifiers.Control) != 0 ? "^" : string.Empty) +
                        ((chord.Modifiers & KeyModifiers.Alt) != 0 ? "%" : string.Empty) +
                        ((chord.Modifiers & KeyModifiers.Shift) != 0 ? "+" : string.Empty) +
                        ((chord.Modifiers & KeyModifiers.Windows) != 0 ? "#" : string.Empty);
        var code = chord.Code;
        var key = chord.Kind == InputEventKind.Unicode ? ((char)code).ToString()
            : code is >= 'A' and <= 'Z' or >= '0' and <= '9' ? char.ToLowerInvariant((char)code).ToString()
            : $"{{{KeyTable.NameOf(code) ?? KeyNames.Of(code)}}}";
        return modifiers + key;
    }

    /// <summary>The modifier <paramref name="virtualKey"/> is, generic or left/right; None for any other key.</summary>
    public static KeyModifiers ModifierOf(ushort virtualKey) => virtualKey switch
    {
        NativeEnums.VK_CONTROL or NativeEnums.VK_LCONTROL or NativeEnums.VK_RCONTROL => KeyModifiers.Control,
        NativeEnums.VK_MENU or NativeEnums.VK_LMENU or NativeEnums.VK_RMENU => KeyModifiers.Alt,
        NativeEnums.VK_SHIFT or NativeEnums.VK_LSHIFT or NativeEnums.VK_RSHIFT => KeyModifiers.Shift,
        NativeEnums.VK_LWIN or NativeEnums.VK_RWIN => KeyModifiers.Windows,
        _ => KeyModifiers.None
    };

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
/// The keys string breaks send_keys' grammar, such as a brace that is not closed: a mistake the agent fixes in
/// the string, reported as InvalidKeySpecification, unlike an unknown key name (UnknownKey).
/// </summary>
public sealed class KeySyntaxException(string message) : ArgumentException(message);
