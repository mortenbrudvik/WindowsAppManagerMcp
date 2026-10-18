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
    /// <param name="events">The events.</param>
    /// <param name="altGrIsCtrlAlt">
    /// True to read the AltGr pair a parser sent for a character (<see cref="InputEvent.AltGr"/>) as Ctrl and Alt, as a
    /// hotkey registered with RegisterHotKey reads it; false (the default) to read it as holding nothing (see <see cref="Held"/>).
    /// </param>
    public static IReadOnlyList<KeyChord> Chords(IReadOnlyList<InputEvent> events, bool altGrIsCtrlAlt = false) =>
        IndexedChords(events, altGrIsCtrlAlt).Select(c => c.Chord).ToList();

    /// <summary>
    /// The chords of <see cref="Chords"/>, each with the index in <paramref name="events"/> of its key-down, so their
    /// order against other events (a mouse button) can be told.
    /// </summary>
    public static IReadOnlyList<(KeyChord Chord, int Index)> IndexedChords(IReadOnlyList<InputEvent> events, bool altGrIsCtrlAlt = false)
    {
        ArgumentNullException.ThrowIfNull(events);

        var chords = new List<(KeyChord, int)>();
        var down = new List<DownModifier>();
        for (var i = 0; i < events.Count; i++)
        {
            var e = events[i];
            var isUp = (e.Flags & NativeEnums.KEYEVENTF_KEYUP) != 0;
            if (e.Kind == InputEventKind.VirtualKey && ModifierOf(e.Code) != KeyModifiers.None)
            {
                Track(down, e, isUp);
                continue;
            }

            if (e.Kind != InputEventKind.Mouse && !isUp)
            {
                chords.Add((Press(Held(down, altGrIsCtrlAlt), e), i));
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
        var down = new List<DownModifier>();
        var tapCandidate = (ushort)0;
        foreach (var e in events)
        {
            var isUp = (e.Flags & NativeEnums.KEYEVENTF_KEYUP) != 0;
            if (e.Kind == InputEventKind.VirtualKey && ModifierOf(e.Code) != KeyModifiers.None)
            {
                Track(down, e, isUp);
                if (!isUp)
                {
                    tapCandidate = e.Code;
                    continue;
                }

                if (tapCandidate == e.Code)
                {
                    presses.Add(new KeyChord(Held(down, altGrIsCtrlAlt: false), InputEventKind.VirtualKey, e.Code));
                }

                tapCandidate = 0;
                continue;
            }

            if (e.Kind != InputEventKind.Mouse && !isUp)
            {
                presses.Add(Press(Held(down, altGrIsCtrlAlt: false), e));
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

    /// <summary>A modifier key that is down, and whether a parser pressed it as AltGr for a character (<see cref="InputEvent.AltGr"/>).</summary>
    private readonly record struct DownModifier(ushort VirtualKey, bool AltGr);

    private static void Track(List<DownModifier> down, InputEvent e, bool isUp)
    {
        var virtualKey = e.Code;
        if (isUp)
        {
            down.RemoveAll(key => ModifierOf(key.VirtualKey) == ModifierOf(virtualKey) &&
                                  (key.VirtualKey == virtualKey || !IsSided(virtualKey) || !IsSided(key.VirtualKey)));
        }
        else if (!down.Exists(key => key.VirtualKey == virtualKey))
        {
            down.Add(new DownModifier(virtualKey, e.AltGr));
        }
    }

    /// <summary>
    /// The modifiers <paramref name="down"/> holds. The left Ctrl together with the right Alt that a parser pressed as AltGr
    /// (both tagged <see cref="InputEvent.AltGr"/>), the way send_keys and press_keys type a character such as '@' on a German
    /// or Norwegian keyboard, holds nothing unless <paramref name="altGrIsCtrlAlt"/>: typing an address is not a Ctrl+Alt
    /// chord (Ctrl+Alt+Q would otherwise count as Ctrl+Q). The same keys pressed as keys (<c>{LCTRL down}{RALT down}</c>,
    /// <c>lctrl+ralt</c>), or only one of them tagged (an explicit <c>{LCTRL down}</c> before an AltGr character), are Ctrl and
    /// Alt, as Windows reads them: Ctrl+Alt+Tab opens the task switcher, and the pause hotkey fires. An explicit ^ or % is the
    /// generic Ctrl or the left Alt, which count too.
    /// </summary>
    private static KeyModifiers Held(List<DownModifier> down, bool altGrIsCtrlAlt)
    {
        var altGr = !altGrIsCtrlAlt &&
                    down.Contains(new DownModifier(NativeEnums.VK_LCONTROL, AltGr: true)) &&
                    down.Contains(new DownModifier(NativeEnums.VK_RMENU, AltGr: true));
        var held = KeyModifiers.None;
        foreach (var key in down)
        {
            if (!(altGr && key.AltGr && key.VirtualKey is NativeEnums.VK_LCONTROL or NativeEnums.VK_RMENU))
            {
                held |= ModifierOf(key.VirtualKey);
            }
        }

        return held;
    }

    private static bool IsSided(ushort virtualKey) => virtualKey is >= NativeEnums.VK_LSHIFT and <= NativeEnums.VK_RMENU;

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
