using WindowsAppManagerMcp.Models;
using WindowsAppManagerMcp.Native;

namespace WindowsAppManagerMcp.Services;

/// <summary>
/// The input events of a send_keys string, in steps: each top-level key, chord or group is one step, and so is each
/// repetition of <c>{KEY n}</c>. send_keys' delayMs waits between steps, each a batch of its own; without it the steps go
/// in batches of whole steps (<see cref="InputSimulationService.SendKeysBatchSize"/> key-downs at most, unless one step has
/// more), so a step is never split.
/// </summary>
public sealed class KeySpec
{
    public KeySpec(IReadOnlyList<IReadOnlyList<InputEvent>> steps)
    {
        Steps = steps;
        Events = steps.SelectMany(step => step).ToList();
    }

    /// <summary>The steps, in order. The key-ups of keys left down with <c>{KEY down}</c> end the last step.</summary>
    public IReadOnlyList<IReadOnlyList<InputEvent>> Steps { get; }

    /// <summary>Every event of every step, in order.</summary>
    public IReadOnlyList<InputEvent> Events { get; }

    /// <summary>
    /// press_keys (issue #10): each chord whose character went out on a key of another name than the chord's, each once, in
    /// order (<see cref="ResolvedChord"/>); null when there is none.
    /// </summary>
    public IReadOnlyList<ResolvedChord>? ResolvedChords { get; init; }

    /// <summary>
    /// press_keys (issue #9): the chords whose character went out as Unicode, with no key, each once, in order; null when none
    /// did. send_keys reads its characters from the events instead.
    /// </summary>
    public IReadOnlyList<CharacterWithoutKey>? SentAsCharacters { get; init; }
}

/// <summary>
/// A press_keys chord whose character went out on a key of another name (issue #10), so the agent can tell what the target
/// got: <paramref name="Asked"/> as written; <paramref name="Pressed"/>, the keys that went down in press_keys' names
/// ('ctrl+shift+0' for 'ctrl+=' where '=' is Shift+0, 'ctrl+ralt+2' for 'ctrl+@' where '@' is AltGr+2, 'ctrl+6' for 'ctrl+-'
/// on French AZERTY); and <paramref name="Chord"/>, the chord as Windows reads it (AltGr as Ctrl+Alt), which the SystemHotkey
/// warning matches to name the chord asked whatever its spelling.
/// </summary>
public sealed record ResolvedChord(string Asked, string Pressed, KeyChord Chord);

/// <summary>
/// A chord (press_keys) or a character (send_keys) that went out as its character alone (KEYEVENTF_UNICODE), with no key and
/// no scan code (issue #9): <paramref name="Keys"/> as the call wrote it, the <paramref name="Character"/> sent, and
/// <paramref name="Why"/>, which the SentAsCharacter warning gives for each.
/// </summary>
public sealed record CharacterWithoutKey(string Keys, string Character, NoKeyReason Why);

/// <summary>Why a character went out with no key (<see cref="KeyboardLayout.WhyNoKey"/>).</summary>
public enum NoKeyReason
{
    /// <summary>No key of the window's layout types it, or only a dead key does.</summary>
    NotOnLayout,

    /// <summary>
    /// Its key would have typed an accent waiting on the layout too (<see cref="KeyboardLayout.DeadKeyPending"/>): text, and a
    /// press_keys chord without Ctrl, Alt or Win, stay exact then.
    /// </summary>
    AccentWaiting,

    /// <summary>The window's layout could not be read (<see cref="KeyboardLayout.ReadFailed"/>).</summary>
    LayoutUnread
}

/// <summary>
/// Parses send_keys' key grammar (roadmap M4.1) into input events, mapping every character through a keyboard layout:
/// <list type="bullet">
/// <item>A character types itself: the key the layout types it with, plus the Shift or AltGr it needs, with a scan code;
/// Unicode only when no key of the layout types it (or only a dead key does). Caps Lock is allowed for. With Ctrl, Alt or
/// Win held, a letter or digit the layout has no key for is its A-Z or 0-9 key, so <c>^c</c> copies on any layout.</item>
/// <item><c>^</c> Ctrl, <c>%</c> Alt, <c>+</c> Shift and <c>#</c> Win hold down for the next key or <c>(group)</c>.</item>
/// <item><c>{NAME}</c> is a named key (<see cref="KeyTable"/>); <c>{NAME n}</c> presses it n times (1 to
/// <see cref="MaxRepeat"/>); <c>{NAME down}</c> and <c>{NAME up}</c> press and release it apart, and a key still down at
/// the end is released there. A single character in braces is that character, so <c>{+} {^} {%} {#} {~} {(} {)} {{}
/// {}}</c> type the characters the grammar uses; <c>{a 3}</c> types aaa.</item>
/// <item><c>~</c> is Enter; a tab, a line break, a backspace or an escape character is its key.</item>
/// </list>
/// </summary>
public static class KeySpecParser
{
    /// <summary>The most times <c>{KEY n}</c> presses a key.</summary>
    public const int MaxRepeat = 100;

    /// <summary>The events <paramref name="keys"/> stand for on <paramref name="layout"/>.</summary>
    /// <exception cref="KeySyntaxException">The string breaks the grammar (InvalidKeySpecification).</exception>
    /// <exception cref="ArgumentException">A key name is unknown (UnknownKey).</exception>
    public static KeySpec Parse(string keys, KeyboardLayout layout)
    {
        ArgumentNullException.ThrowIfNull(keys);
        ArgumentNullException.ThrowIfNull(layout);
        return new Parser(keys, layout).Run();
    }

    private sealed class Parser(string keys, KeyboardLayout layout)
    {
        private readonly List<List<InputEvent>> _steps = new();

        /// <summary>The modifier keys held down at this point: by enclosing groups and by <c>{KEY down}</c>.</summary>
        private readonly List<ushort> _held = new();

        /// <summary>The keys <c>{KEY down}</c> pressed and no <c>{KEY up}</c> released yet, in order.</summary>
        private readonly List<ushort> _down = new();

        private List<InputEvent> _current = new();
        private int _i;

        /// <summary>Caps Lock as the keys so far leave it: each press of {CAPSLOCK} toggles it, as on a keyboard.</summary>
        private bool _capsLock = layout.CapsLock;

        public KeySpec Run()
        {
            ParseSequence(topLevel: true);

            // Keys still down are released at the end of the last step
            if (_down.Count > 0)
            {
                if (_steps.Count == 0)
                {
                    BeginStep();
                }

                for (var k = _down.Count - 1; k >= 0; k--)
                {
                    Up(_down[k]);
                }
            }

            return new KeySpec(_steps);
        }

        private void BeginStep()
        {
            _current = new List<InputEvent>();
            _steps.Add(_current);
        }

        private void ParseSequence(bool topLevel)
        {
            while (_i < keys.Length)
            {
                if (keys[_i] == ')')
                {
                    if (topLevel)
                    {
                        throw new KeySyntaxException($"The ')' at position {_i} closes no group. Write the character ) as {{)}}.");
                    }

                    return;
                }

                if (topLevel)
                {
                    BeginStep();
                }

                ParseToken(topLevel);
            }
        }

        private void ParseToken(bool topLevel)
        {
            var start = _i;
            var modifiers = new List<ushort>();
            while (_i < keys.Length && ModifierKey(keys[_i]) is var modifier && modifier != 0)
            {
                if (!modifiers.Contains(modifier))
                {
                    modifiers.Add(modifier);
                }

                _i++;
            }

            if (_i >= keys.Length || keys[_i] == ')')
            {
                throw new KeySyntaxException(
                    $"'{keys[start.._i]}' at position {start} has no key after it to hold down. Write the characters + ^ % # as " +
                    "{+} {^} {%} {#}.");
            }

            switch (keys[_i])
            {
                case '(':
                    ParseGroup(modifiers);
                    break;

                case '{':
                    ParseBrace(modifiers, topLevel);
                    break;

                case '}':
                    throw new KeySyntaxException($"The '}}' at position {_i} closes no brace. Write the character }} as {{}}}}.");

                case '~':
                    _i++;
                    PressKey(new KeyStroke(NativeEnums.VK_RETURN, KeyModifiers.None), modifiers);
                    break;

                default:
                    ParseCharacter(modifiers);
                    break;
            }
        }

        /// <summary>A <c>(group)</c>: its modifiers stay down for every key in it.</summary>
        private void ParseGroup(List<ushort> modifiers)
        {
            var open = _i++;
            var pressed = PressModifiers(modifiers);
            _held.AddRange(pressed);
            var before = _current.Count;

            ParseSequence(topLevel: false);
            if (_i >= keys.Length)
            {
                throw new KeySyntaxException($"The '(' at position {open} is not closed. Write the character ( as {{(}}.");
            }

            if (_current.Count == before)
            {
                throw new KeySyntaxException($"The group at position {open} is empty.");
            }

            _i++;
            foreach (var key in pressed)
            {
                _held.Remove(key);
            }

            ReleaseModifiers(pressed);
        }

        private void ParseBrace(List<ushort> modifiers, bool topLevel)
        {
            var open = _i;
            var close = _i + 2 <= keys.Length ? keys.IndexOf('}', _i + 2) : -1;
            if (close < 0)
            {
                throw new KeySyntaxException($"The brace at position {open} is not closed. Write the character {{ as {{{{}}.");
            }

            var content = keys[(open + 1)..close];
            _i = close + 1;
            var (name, argument) = Split(content);
            if (name.Length > 1 && name[0] == '}')
            {
                throw new KeySyntaxException($"The braces at position {open} are empty. Write the character }} as {{}}}}.");
            }

            // A single character, or an emoji's surrogate pair, is itself
            var character = name.Length == 1 || (name.Length == 2 && char.IsSurrogatePair(name[0], name[1])) ? name : null;
            ushort virtualKey = 0;
            if (character == null)
            {
                virtualKey = KeyTable.Find(name) ?? throw new ArgumentException($"Unknown key: {name.ToUpperInvariant()}");
            }

            if (argument == null)
            {
                Press();
                return;
            }

            if (argument.Equals("down", StringComparison.OrdinalIgnoreCase) || argument.Equals("up", StringComparison.OrdinalIgnoreCase))
            {
                var down = argument.Equals("down", StringComparison.OrdinalIgnoreCase);
                if (character != null)
                {
                    throw new KeySyntaxException($"{{{content}}}: down and up take a key name, such as {{SHIFT down}}.");
                }

                if (modifiers.Count > 0)
                {
                    throw new KeySyntaxException(
                        $"{{{content}}} at position {open} has ^ % + or # before it. Hold modifiers with their own {{CTRL down}}.");
                }

                HoldOrRelease(virtualKey, name, down);
                return;
            }

            if (!int.TryParse(argument, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture,
                    out var count) || count is < 1 or > MaxRepeat)
            {
                throw new KeySyntaxException(
                    $"{{{content}}}: after the key comes a count from 1 to {MaxRepeat}, 'down' or 'up', separated by a space.");
            }

            for (var n = 0; n < count; n++)
            {
                if (topLevel && n > 0)
                {
                    BeginStep();
                }

                Press();
            }

            void Press()
            {
                if (character?.Length == 2)
                {
                    PressUnicode(character.ToCharArray(), modifiers);
                }
                else if (character != null)
                {
                    PressCharacter(character[0], modifiers);
                }
                else
                {
                    PressKey(new KeyStroke(virtualKey, KeyModifiers.None), modifiers);
                }
            }
        }

        /// <summary>
        /// The key name and what follows it: <c>LEFT 5</c>, <c>SHIFT down</c>, <c>+ 3</c>, <c>} 2</c>; a single character
        /// is itself, the space included.
        /// </summary>
        private static (string Name, string? Argument) Split(string content)
        {
            if (content.Length == 1)
            {
                return (content, null);
            }

            if (content[1] == ' ')
            {
                return (content[..1], content[2..].Trim());
            }

            var space = content.IndexOf(' ');
            return space < 0 ? (content, null) : (content[..space], content[(space + 1)..].Trim());
        }

        private void HoldOrRelease(ushort virtualKey, string name, bool down)
        {
            var upper = name.ToUpperInvariant();
            if (down)
            {
                if (_down.Contains(virtualKey))
                {
                    throw new KeySyntaxException($"{{{upper} down}} comes twice without {{{upper} up}} in between.");
                }

                Down(virtualKey);
                _down.Add(virtualKey);
                if (KeyTable.IsModifier(virtualKey))
                {
                    _held.Add(virtualKey);
                }

                return;
            }

            if (!_down.Remove(virtualKey))
            {
                throw new KeySyntaxException($"{{{upper} up}} has no {{{upper} down}} before it in these keys.");
            }

            _held.Remove(virtualKey);
            Up(virtualKey);
        }

        private void ParseCharacter(List<ushort> modifiers)
        {
            var c = keys[_i++];
            if (char.IsHighSurrogate(c) && _i < keys.Length && char.IsLowSurrogate(keys[_i]))
            {
                PressUnicode([c, keys[_i++]], modifiers);
                return;
            }

            // A Windows line break is one Enter
            if (c == '\r' && _i < keys.Length && keys[_i] == '\n')
            {
                _i++;
            }

            PressCharacter(c, modifiers);
        }

        private void PressCharacter(char c, List<ushort> modifiers)
        {
            var controlKey = c switch
            {
                '\t' => NativeEnums.VK_TAB,
                '\r' or '\n' => NativeEnums.VK_RETURN,
                '\b' => NativeEnums.VK_BACK,
                '\u001b' => NativeEnums.VK_ESCAPE,
                _ => (ushort)0
            };
            if (controlKey != 0)
            {
                PressKey(new KeyStroke(controlKey, KeyModifiers.None), modifiers);
                return;
            }

            if (char.IsControl(c) || char.IsSurrogate(c))
            {
                PressUnicode([c], modifiers);
                return;
            }

            // Caps Lock matters only to text: a shortcut is the same key whatever Caps Lock says, and its character names the
            // key even while an accent waits on the layout (issue #8)
            var plain = modifiers.Count == 0 && !_held.Any(KeyTable.IsModifier);
            var shortcut = HoldsShortcutModifier(modifiers);
            var stroke = layout.Map(c, capsLock: plain && _capsLock, asKey: shortcut);
            if (stroke == null && char.IsAsciiLetterOrDigit(c) && shortcut)
            {
                // A shortcut on a layout without Latin letters (Cyrillic, Greek): the key Windows calls A-Z or 0-9
                stroke = new KeyStroke(char.ToUpperInvariant(c), char.IsAsciiLetterUpper(c) ? KeyModifiers.Shift : KeyModifiers.None);
            }

            if (stroke is { } key)
            {
                PressKey(key, modifiers);
            }
            else
            {
                PressUnicode([c], modifiers);
            }
        }

        private bool HoldsShortcutModifier(List<ushort> modifiers) =>
            modifiers.Concat(_held).Any(key => SendKeysParser.ModifierOf(key) is KeyModifiers.Control or KeyModifiers.Alt or KeyModifiers.Windows);

        /// <summary>
        /// The token's modifiers, then the ones the key itself needs (AltGr as the left Ctrl and the right Alt, then Shift),
        /// each unless it is already down; the key; and the modifiers it pressed up again, last first.
        /// </summary>
        private void PressKey(KeyStroke stroke, List<ushort> modifiers)
        {
            var pressed = PressModifiers(modifiers);
            var altGr = new List<ushort>();
            if (stroke.IsAltGr)
            {
                // The left Ctrl and the right Alt, as a keyboard sends AltGr, tagged (InputEvent.AltGr) so that the chord checks
                // read the pair as a character, not as Ctrl+Alt; the same keys spelled out ({LCTRL down}) are not tagged. The right
                // Alt goes down under an explicit % too, so %@ stays Alt with an AltGr character
                if (!_held.Concat(pressed).Any(down => SendKeysParser.ModifierOf(down) == KeyModifiers.Control))
                {
                    altGr.Add(NativeEnums.VK_LCONTROL);
                }

                if (!_held.Concat(pressed).Contains(NativeEnums.VK_RMENU))
                {
                    altGr.Add(NativeEnums.VK_RMENU);
                }

                foreach (var key in altGr)
                {
                    Down(key, altGr: true);
                    pressed.Add(key);
                }
            }

            if ((stroke.Modifiers & KeyModifiers.Shift) != 0)
            {
                PressIfNotDown(NativeEnums.VK_SHIFT, KeyModifiers.Shift, pressed);
            }

            Down(stroke.VirtualKey);
            Up(stroke.VirtualKey);
            ReleaseModifiers(pressed, altGr);
        }

        private void PressUnicode(char[] characters, List<ushort> modifiers)
        {
            var pressed = PressModifiers(modifiers);
            foreach (var c in characters)
            {
                _current.Add(InputEvent.Unicode(c, NativeEnums.KEYEVENTF_KEYDOWN));
                _current.Add(InputEvent.Unicode(c, NativeEnums.KEYEVENTF_KEYUP));
            }

            ReleaseModifiers(pressed);
        }

        /// <summary>Presses the modifiers that are not down yet; returns the ones it pressed.</summary>
        private List<ushort> PressModifiers(List<ushort> modifiers)
        {
            var pressed = new List<ushort>();
            foreach (var key in modifiers)
            {
                PressIfNotDown(key, SendKeysParser.ModifierOf(key), pressed);
            }

            return pressed;
        }

        private void PressIfNotDown(ushort key, KeyModifiers kind, List<ushort> pressed)
        {
            if (_held.Concat(pressed).Any(down => SendKeysParser.ModifierOf(down) == kind))
            {
                return;
            }

            Down(key);
            pressed.Add(key);
        }

        /// <summary>Releases <paramref name="pressed"/>, last first; the keys in <paramref name="altGr"/> go up tagged as AltGr.</summary>
        private void ReleaseModifiers(List<ushort> pressed, List<ushort>? altGr = null)
        {
            for (var k = pressed.Count - 1; k >= 0; k--)
            {
                Up(pressed[k], altGr?.Contains(pressed[k]) == true);
            }
        }

        private void Down(ushort virtualKey, bool altGr = false)
        {
            _current.Add(KeyEvent(virtualKey, NativeEnums.KEYEVENTF_KEYDOWN, altGr));
            if (virtualKey == NativeEnums.VK_CAPITAL)
            {
                _capsLock = !_capsLock;
            }
        }

        private void Up(ushort virtualKey, bool altGr = false) => _current.Add(KeyEvent(virtualKey, NativeEnums.KEYEVENTF_KEYUP, altGr));

        private InputEvent KeyEvent(ushort virtualKey, uint flags, bool altGr)
        {
            var (scan, extended) = layout.ScanOf(virtualKey);
            return InputEvent.Key(virtualKey, flags | (extended ? NativeEnums.KEYEVENTF_EXTENDEDKEY : 0), scan) with { AltGr = altGr };
        }

        private static ushort ModifierKey(char c) => c switch
        {
            '^' => NativeEnums.VK_CONTROL,
            '%' => NativeEnums.VK_MENU,
            '+' => NativeEnums.VK_SHIFT,
            '#' => NativeEnums.VK_LWIN,
            _ => 0
        };
    }
}
