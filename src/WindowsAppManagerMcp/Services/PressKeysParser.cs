using System.Globalization;
using WindowsAppManagerMcp.Models;
using WindowsAppManagerMcp.Native;

namespace WindowsAppManagerMcp.Services;

/// <summary>
/// press_keys' structured chords (roadmap M4.3), read into the same steps of input events as send_keys' grammar
/// (<see cref="KeySpec"/>), so the input guard reads their chords exactly as it reads send_keys' (destructive chords,
/// keys after a focus change, the pause chord, pastes):
/// <list type="bullet">
/// <item>A chord is key names joined by '+': modifiers (ctrl, shift, alt, win, and their left and right forms) and at
/// most one other key: a name from <see cref="KeyTable"/> (enter, tab, f4, delete, ...) or a single character. The
/// modifiers go down in the order given, then the key goes down and up, then the modifiers come up, last first.</item>
/// <item>A letter or digit is its key (A-Z, 0-9), with no Shift of its own, so 'ctrl+t' and 'ctrl+T' are the same chord
/// and 'ctrl+1' is the shortcut on any layout. Any other character is the key the layout types it with, plus the Shift or
/// AltGr it needs; Unicode only when no key types it. '+', ',' and '*' are written plus, comma and star. A key is also named
/// by its place, whatever it types on the layout: oem_plus, oem_minus, oem_comma, oem_period, oem_1 to oem_8 and oem_102
/// (<see cref="KeyTable"/>), so 'alt+shift+oem_plus' is Alt+Shift and the key right of 0 on any layout (issue #8).</item>
/// <item>'chord*n' presses the chord n times (1 to <see cref="MaxRepeat"/>). Chords separated by spaces or commas, or the
/// items of an array that is not one chord (<see cref="KeyList.IsOneChord"/>), follow each other.</item>
/// </list>
/// Each press is one step; repeat repeats the whole sequence. At most <see cref="MaxPresses"/> presses in one call. The spec
/// also says how the chords went out on the layout (issues #9, #10): the chords whose character needed a Shift or AltGr the
/// chord does not name (<see cref="KeySpec.ResolvedChords"/>), and those sent as their character with no key
/// (<see cref="KeySpec.SentAsCharacters"/>), so the result can tell the agent what the target got.
/// </summary>
public static class PressKeysParser
{
    /// <summary>The most times '*n' presses a chord, and the most times repeat runs the sequence.</summary>
    public const int MaxRepeat = 100;

    /// <summary>The most presses one call sends, repeats included.</summary>
    public const int MaxPresses = 200;

    /// <summary>The names press_keys takes besides the key table's: the characters that are its own syntax, and 'windows'.</summary>
    private static readonly Dictionary<string, char> CharacterNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ["plus"] = '+', ["comma"] = ',', ["star"] = '*', ["asterisk"] = '*'
    };

    /// <summary>True when <paramref name="name"/> names a modifier key (ctrl, shift, alt, win, or a left or right one).</summary>
    public static bool IsModifierName(string name) => Resolve(name) is { Character: null } key && KeyTable.IsModifier(key.VirtualKey);

    /// <summary>The steps <paramref name="keys"/> stand for on <paramref name="layout"/>, run <paramref name="repeat"/> times.</summary>
    /// <exception cref="KeySyntaxException">The keys break the grammar or press too often (InvalidKeySpecification).</exception>
    /// <exception cref="ArgumentException">A key name is unknown (UnknownKey).</exception>
    /// <exception cref="ArgumentOutOfRangeException">repeat is outside 1 to <see cref="MaxRepeat"/>.</exception>
    public static KeySpec Parse(KeyList keys, int repeat, KeyboardLayout layout)
    {
        ArgumentNullException.ThrowIfNull(keys);
        ArgumentNullException.ThrowIfNull(layout);
        ArgumentOutOfRangeException.ThrowIfLessThan(repeat, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(repeat, MaxRepeat);

        var tokens = keys.IsOneChord
            ? [string.Join("+", keys.Items)]
            : keys.Items.SelectMany(item => item.Split([' ', ',', '\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries)).ToList();
        if (tokens.Count == 0)
        {
            throw new KeySyntaxException("There are no keys to press: pass a chord such as 'ctrl+s', or a sequence such as 'tab*3 enter'.");
        }

        var chords = tokens.Select(Chord).ToList();
        var presses = chords.Sum(chord => chord.Count) * repeat;
        if (presses > MaxPresses)
        {
            throw new KeySyntaxException(
                $"The keys press {presses} chords (repeats included); one call presses at most {MaxPresses}. Send the rest in another call.");
        }

        // Each chord once: a repeat presses the same keys
        var pressed = chords.Select(chord => Press(chord, layout)).ToList();
        var steps = new List<IReadOnlyList<InputEvent>>(presses);
        for (var r = 0; r < repeat; r++)
        {
            for (var c = 0; c < chords.Count; c++)
            {
                for (var n = 0; n < chords[c].Count; n++)
                {
                    steps.Add(pressed[c].Events);
                }
            }
        }

        var resolved = new Dictionary<string, string>(StringComparer.Ordinal);
        var asCharacters = new List<CharacterWithoutKey>();
        for (var c = 0; c < chords.Count; c++)
        {
            if (pressed[c].Resolved is { } name)
            {
                resolved.TryAdd(chords[c].Text, name);
            }

            if (pressed[c].Character is { } character && !asCharacters.Exists(sent => sent.Keys == chords[c].Text))
            {
                asCharacters.Add(new CharacterWithoutKey(chords[c].Text, character.ToString()));
            }
        }

        return new KeySpec(steps)
        {
            ResolvedChords = resolved.Count == 0 ? null : resolved,
            SentAsCharacters = asCharacters.Count == 0 ? null : asCharacters
        };
    }

    /// <summary>
    /// The name press_keys takes for <paramref name="virtualKey"/>, for the result: a letter or digit as itself ('t', '0'),
    /// a punctuation key by its place (oem_plus, oem_1, oem_102), another key by its first <see cref="KeyTable"/> name
    /// (enter, pgup, f4); "vk_0xNN" for a key without one.
    /// </summary>
    public static string KeyName(ushort virtualKey)
    {
        if (virtualKey is >= 'A' and <= 'Z' or >= '0' and <= '9')
        {
            return char.ToLowerInvariant((char)virtualKey).ToString();
        }

        return ModifierNames.TryGetValue(virtualKey, out var modifier) ? modifier
            : KeyTable.NameOf(virtualKey) is { } name
                ? name.StartsWith("OEM", StringComparison.Ordinal) ? "oem_" + name[3..].ToLowerInvariant() : name.ToLowerInvariant()
                : $"vk_0x{virtualKey:X2}";
    }

    /// <summary>
    /// <paramref name="chord"/> as press_keys writes it: its modifiers (ctrl, alt, shift, win), then its key
    /// (<see cref="KeyName"/>), or the character it sent as Unicode.
    /// </summary>
    public static string NameOf(KeyChord chord)
    {
        var names = new List<string>();
        foreach (var (kind, name) in new[]
                 {
                     (KeyModifiers.Control, "ctrl"), (KeyModifiers.Alt, "alt"), (KeyModifiers.Shift, "shift"), (KeyModifiers.Windows, "win")
                 })
        {
            if ((chord.Modifiers & kind) != 0)
            {
                names.Add(name);
            }
        }

        names.Add(chord.Kind == InputEventKind.Unicode ? ((char)chord.Code).ToString() : KeyName(chord.Code));
        return string.Join("+", names);
    }

    /// <summary>The modifier keys' names, as a chord takes them.</summary>
    private static readonly Dictionary<ushort, string> ModifierNames = new()
    {
        [NativeEnums.VK_CONTROL] = "ctrl", [NativeEnums.VK_LCONTROL] = "lctrl", [NativeEnums.VK_RCONTROL] = "rctrl",
        [NativeEnums.VK_MENU] = "alt", [NativeEnums.VK_LMENU] = "lalt", [NativeEnums.VK_RMENU] = "ralt",
        [NativeEnums.VK_SHIFT] = "shift", [NativeEnums.VK_LSHIFT] = "lshift", [NativeEnums.VK_RSHIFT] = "rshift",
        [NativeEnums.VK_LWIN] = "win", [NativeEnums.VK_RWIN] = "rwin"
    };

    /// <summary>A key a name stands for: a virtual key, or a character the layout maps.</summary>
    private readonly record struct NamedKey(ushort VirtualKey, char? Character);

    /// <summary>
    /// One chord of the sequence: its modifiers in order, its other key (or none), how often it is pressed, and the chord as
    /// written, without its count.
    /// </summary>
    private sealed record ChordSpec(IReadOnlyList<ushort> Modifiers, NamedKey? Key, int Count, string Text);

    /// <summary>
    /// One press of a chord: its events; the chord as pressed (<see cref="KeyName"/>s) when its character needed a Shift or
    /// AltGr the chord does not name ('ctrl+shift+0' for 'ctrl+=' where '=' is Shift+0), else null; and the character it
    /// sent as Unicode, with no key, else null.
    /// </summary>
    private sealed record Pressed(IReadOnlyList<InputEvent> Events, string? Resolved, char? Character);

    private static ChordSpec Chord(string token)
    {
        var count = 1;
        var star = token.LastIndexOf('*');
        if (star >= 0)
        {
            var countText = token[(star + 1)..];
            if (!int.TryParse(countText, NumberStyles.None, CultureInfo.InvariantCulture, out count) || count is < 1 or > MaxRepeat)
            {
                throw new KeySyntaxException(
                    $"'{token}': after '*' comes how often to press the chord, 1 to {MaxRepeat} (write the * key as 'star').");
            }

            token = token[..star];
        }

        var names = token.Split('+');
        if (names.Any(name => name.Length == 0))
        {
            throw new KeySyntaxException(
                $"'{token}' has an empty key name between or around its '+': write the + key as 'plus' (ctrl+plus).");
        }

        var modifiers = new List<ushort>();
        NamedKey? key = null;
        foreach (var name in names)
        {
            var named = Resolve(name) ?? throw new ArgumentException($"Unknown key: {name.ToUpperInvariant()}");
            if (named.Character == null && KeyTable.IsModifier(named.VirtualKey))
            {
                var kind = SendKeysParser.ModifierOf(named.VirtualKey);
                if (modifiers.Any(held => SendKeysParser.ModifierOf(held) == kind))
                {
                    throw new KeySyntaxException($"'{token}' names the {name.ToLowerInvariant()} modifier twice.");
                }

                modifiers.Add(named.VirtualKey);
                continue;
            }

            if (key != null)
            {
                throw new KeySyntaxException(
                    $"'{token}' has more than one key besides its modifiers; a chord has one key. To press keys one after another, " +
                    "separate them with spaces ('ctrl+a delete').");
            }

            key = named;
        }

        return new ChordSpec(modifiers, key, count, token);
    }

    private static NamedKey? Resolve(string name)
    {
        if (name.Length == 1)
        {
            return new NamedKey(0, name[0]);
        }

        if (CharacterNames.TryGetValue(name, out var character))
        {
            return new NamedKey(0, character);
        }

        if (name.Equals("windows", StringComparison.OrdinalIgnoreCase))
        {
            return new NamedKey(NativeEnums.VK_LWIN, null);
        }

        return KeyTable.Find(name) is { } virtualKey ? new NamedKey(virtualKey, null) : null;
    }

    /// <summary>One press of <paramref name="chord"/>: its events, and how its character went out.</summary>
    private static Pressed Press(ChordSpec chord, KeyboardLayout layout)
    {
        var events = new List<InputEvent>();
        var pressed = new List<ushort>();
        var altGrKeys = new List<ushort>();
        var added = new List<string>();
        string? resolved = null;
        char? asCharacter = null;
        foreach (var modifier in chord.Modifiers)
        {
            Down(modifier);
        }

        switch (chord.Key)
        {
            case { Character: null } named:
                Down(named.VirtualKey);
                Up(named.VirtualKey);
                break;

            case { Character: { } c } when char.IsAsciiLetterOrDigit(c):
                // The key Windows calls A-Z or 0-9, as a shortcut names it, whatever the layout types with it
                Down(char.ToUpperInvariant(c));
                Up(char.ToUpperInvariant(c));
                break;

            case { Character: { } c }:
                // The character names the key, so an accent waiting on the layout does not turn it into Unicode (issue #8)
                if (layout.Map(c, capsLock: false, asKey: true) is { } stroke)
                {
                    if (stroke.IsAltGr)
                    {
                        added.Add("altgr");

                        // As a keyboard sends AltGr, and as send_keys does: tagged, so the chord checks read the pair as a
                        // character; 'lctrl+ralt' named in the chord is not tagged, and counts as Ctrl+Alt
                        if (!Holds(KeyModifiers.Control))
                        {
                            Down(NativeEnums.VK_LCONTROL, altGr: true);
                        }

                        if (!pressed.Contains(NativeEnums.VK_RMENU))
                        {
                            Down(NativeEnums.VK_RMENU, altGr: true);
                        }
                    }

                    if ((stroke.Modifiers & KeyModifiers.Shift) != 0 && !Holds(KeyModifiers.Shift))
                    {
                        Down(NativeEnums.VK_SHIFT);
                        added.Add("shift");
                    }

                    Down(stroke.VirtualKey);
                    Up(stroke.VirtualKey);
                    if (added.Count > 0)
                    {
                        resolved = string.Join("+", chord.Modifiers.Select(KeyName).Concat(added).Append(KeyName(stroke.VirtualKey)));
                    }
                }
                else
                {
                    events.Add(InputEvent.Unicode(c, NativeEnums.KEYEVENTF_KEYDOWN));
                    events.Add(InputEvent.Unicode(c, NativeEnums.KEYEVENTF_KEYUP));
                    asCharacter = c;
                }

                break;
        }

        for (var k = pressed.Count - 1; k >= 0; k--)
        {
            events.Add(KeyEvent(pressed[k], NativeEnums.KEYEVENTF_KEYUP, altGrKeys.Contains(pressed[k])));
        }

        return new Pressed(events, resolved, asCharacter);

        bool Holds(KeyModifiers kind) => pressed.Any(key => SendKeysParser.ModifierOf(key) == kind);

        void Down(ushort virtualKey, bool altGr = false)
        {
            events.Add(KeyEvent(virtualKey, NativeEnums.KEYEVENTF_KEYDOWN, altGr));
            if (KeyTable.IsModifier(virtualKey))
            {
                pressed.Add(virtualKey);
            }

            if (altGr)
            {
                altGrKeys.Add(virtualKey);
            }
        }

        void Up(ushort virtualKey) => events.Add(KeyEvent(virtualKey, NativeEnums.KEYEVENTF_KEYUP, altGr: false));

        InputEvent KeyEvent(ushort virtualKey, uint flags, bool altGr)
        {
            var (scan, extended) = layout.ScanOf(virtualKey);
            return InputEvent.Key(virtualKey, flags | (extended ? NativeEnums.KEYEVENTF_EXTENDEDKEY : 0), scan) with { AltGr = altGr };
        }
    }
}
