using WindowsAppManagerMcp.Models;
using WindowsAppManagerMcp.Native;

namespace WindowsAppManagerMcp.Services;

/// <summary>
/// What a send_keys or press_keys call tells about the keys that went out (issues #8 to #10): the chords as pressed on the
/// window's layout where that differs from what was asked, what went out as a character with no key, and the warnings
/// (<see cref="KeyWarnings"/>). A call that succeeds can still miss its target: a character sent without its key fires no
/// shortcut, a chord Windows takes for itself never reaches the application, and a dead key may leave an accent waiting, so
/// the result says so instead of looking like any other success. A call that stops part way says the same of the keys that
/// went out before it stopped.
/// </summary>
public partial class InputSimulationService
{
    /// <summary>At most this many chords or characters are named in one warning, for each reason.</summary>
    private const int NamedInWarning = 5;

    /// <summary>
    /// At most this many entries in sentAsCharacters: a send_keys text of up to 5,000 characters in a script the layout has no
    /// keys for (Cyrillic on a US keyboard, Chinese) would otherwise list hundreds, and the result's token cost with them. The
    /// SentAsCharacter warning counts them all.
    /// </summary>
    private const int MaxListedWithoutKeys = 20;

    /// <summary>
    /// What <see cref="ReportKeys"/> read before anything was sent, each item with the index in <paramref name="Events"/> of its
    /// key-down: the chords pressed on keys of another name (press_keys), what went out as characters, the dead keys pressed,
    /// and Windows' hotkeys (null when not read, or the read failed).
    /// </summary>
    private sealed record KeyReport(IReadOnlyList<InputEvent> Events, bool PressKeys, KeyboardLayout Layout,
        IReadOnlyList<ResolvedChord> Resolved, IReadOnlyList<CharacterWithoutKey> WithoutKeys, IReadOnlyList<DeadKeyPress> DeadKeys,
        SystemKeyboardHotkeys? Hotkeys)
    {
        /// <summary>
        /// <paramref name="result"/> with what the first <paramref name="eventsSent"/> events tell: all of them when the call
        /// went through, those before it stopped otherwise (issue #9), so nothing is reported that did not go out, and nothing
        /// at all when none did. <see cref="SendKeysResult.ResolvedChords"/>, sentAsCharacters and warnings are each left out
        /// when there is nothing to say.
        /// </summary>
        public SendKeysResult ApplyTo(SendKeysResult result, int eventsSent)
        {
            if (eventsSent <= 0)
            {
                return result;
            }

            var warnings = new List<string>();
            if (Layout.ReadFailed)
            {
                warnings.Add(KeyWarnings.Format(KeyWarnings.KeyboardLayoutUnknown,
                    "the keyboard layout of the window the keys went to could not be read, so they were sent without scan codes, and " +
                    "every character but a letter, a digit or the space as a character alone, with no key. Apps that read the physical " +
                    "key (shortcuts in browsers and Electron apps, KeyboardEvent.code, scan codes) may not react: check the window, and " +
                    "pass windowHandle so that its own layout is read."));
            }

            var withoutKeys = WithoutKeys.Where(k => k.Event < eventsSent).ToList();
            if (withoutKeys.Count > 0)
            {
                warnings.Add(SentAsCharacterWarning(withoutKeys, PressKeys));
            }

            var resolved = Resolved.Where(r => r.Event < eventsSent).ToList();
            var sent = eventsSent >= Events.Count ? Events : Events.Take(eventsSent).ToList();
            foreach (var match in Hotkeys?.Match(sent, Layout.Handle) ?? [])
            {
                // Matched on the chord, not on its name: 'lctrl+=' and 'shift+ctrl+=' both press what NameOf writes ctrl+shift+0
                var name = PressKeys ? PressKeysParser.NameOf(match.Chord) : SendKeysParser.Spec(match.Chord);
                var asked = resolved.FirstOrDefault(r => r.Chord == match.Chord) is { } chord ? $" (pressed for '{chord.Asked}')" : "";
                warnings.Add(KeyWarnings.Format(KeyWarnings.SystemHotkey, match.Toggle
                    ? $"{name}{asked} is {match.What}, so it may switch that instead of reaching the application, and later keys then type " +
                      "on another layout. Press the modifiers with a key, or ask the user whether to change the hotkey."
                    : $"{name}{asked} is {match.What}, so Windows may take it, and the application may not get it. Press another chord " +
                      "(when a character's Shift made it this one, a key that types without it, such as plus to zoom in on a Norwegian " +
                      "keyboard), or ask the user to change the hotkey (Settings > Time & language > Typing > Advanced keyboard " +
                      "settings > Input language hot keys)."));
            }

            var deadKeys = DeadKeys.Where(d => d.Event < eventsSent).ToList();
            if (deadKeys.Count > 0)
            {
                warnings.Add(DeadKeyWarning(deadKeys));
            }

            Dictionary<string, string>? resolvedChords = null;
            foreach (var chord in resolved)
            {
                (resolvedChords ??= new Dictionary<string, string>(StringComparer.Ordinal)).TryAdd(chord.Asked, chord.Pressed);
            }

            return result with
            {
                ResolvedChords = resolvedChords,
                SentAsCharacters = withoutKeys.Count == 0 ? null : withoutKeys.Take(MaxListedWithoutKeys).Select(k => k.Keys).ToList(),
                Warnings = warnings.Count == 0 ? null : warnings
            };
        }
    }

    /// <summary>
    /// The report of <paramref name="spec"/>'s keys on <paramref name="layout"/>, read before anything is sent, since the keys
    /// change what the layout says (an accent they leave waiting): press_keys' chords (<paramref name="pressKeys"/>) are named
    /// as press_keys writes them, send_keys' as send_keys does. Windows' hotkeys are read once, and only for keys that could
    /// press one (a modifier, or the key left of 1); a read that fails warns of nothing.
    /// </summary>
    private KeyReport ReportKeys(KeySpec spec, KeyboardLayout layout, bool pressKeys) => new(spec.Events, pressKeys, layout,
        pressKeys ? spec.ResolvedChords ?? [] : [],
        pressKeys ? spec.SentAsCharacters ?? [] : CharactersWithoutKeys(spec.Events, layout),
        pressKeys ? spec.DeadKeys ?? [] : DeadKeysPressed(spec.Events, layout),
        HotkeysToMatch(spec.Events));

    /// <summary>
    /// The SentAsCharacter warning: which chords or characters went out alone; why, for each (the layout was not read, an accent
    /// waiting on it would have come out with them on their keys, or no key of it types them), so a reason is given only where
    /// it applies; and what that means.
    /// </summary>
    private static string SentAsCharacterWarning(IReadOnlyList<CharacterWithoutKey> withoutKeys, bool pressKeys)
    {
        var one = withoutKeys.Count == 1;
        var subject = withoutKeys.Count > NamedInWarning
            ? $"{withoutKeys.Count} {(pressKeys ? "chords" : "characters")}"
            : string.Join(", ", withoutKeys.Select(k => pressKeys ? $"'{k.Keys}' ('{k.Character}')" : $"'{k.Character}'"));
        var groups = withoutKeys.GroupBy(k => k.Why).Select(group => group.ToList()).ToList();
        var reasons = groups.Count == 1 && withoutKeys.Count <= NamedInWarning
            ? Why(groups[0][0].Why, one)
            : string.Join("; ", groups.Select(group => $"{Names(group)}: {Why(group[0].Why, group.Count == 1)}"));
        var how = pressKeys ? one ? "with its character" : "with their characters" : one ? "as a character" : "as characters";
        var advice = pressKeys
            ? "To press a key the layout has, name its virtual key: oem_plus, oem_minus, oem_comma, oem_period, oem_1 to oem_8 or " +
              "oem_102 (which key each is depends on the layout)."
            : "To press a key, name it ({OEM_PLUS}, {OEM_1}, ...) or use press_keys.";
        return KeyWarnings.Format(KeyWarnings.SentAsCharacter,
            $"{subject} went out {how} alone, with no key ({reasons}). A text field gets {(one ? "it" : "them")}, but apps that " +
            $"match the physical key (shortcuts, KeyboardEvent.code, scan codes) see no key press. {advice}");

        static string Names(List<CharacterWithoutKey> items) =>
            string.Join(", ", items.Take(NamedInWarning).Select(k => $"'{k.Keys}'")) +
            (items.Count > NamedInWarning ? $" and {items.Count - NamedInWarning} more" : "");

        static string Why(NoKeyReason why, bool one) => why switch
        {
            NoKeyReason.LayoutUnread => "the window's keyboard layout could not be read",
            // What the key would have typed, the accent in front ("`+") or combined ('è'): not that it types the character
            // once the accent is used up, which a dead key's own character would not (KeyboardLayout.WhyNoKey)
            NoKeyReason.AccentWaiting => "an accent typed on a dead key was waiting on the window's keyboard layout, and " +
                                         $"{(one ? "its key would have typed it" : "their keys would have typed them")} with that accent",
            _ => $"no key of the window's keyboard layout types {(one ? "it" : "them")}, or only a dead key does"
        };
    }

    /// <summary>
    /// The DeadKeyPressed warning (issue #8): which chords pressed a dead key, with the accent of each, and that the accent may
    /// be left waiting in the target. "May": whether the target keeps it is not checked, and is not seen on a desktop yet.
    /// </summary>
    private static string DeadKeyWarning(IReadOnlyList<DeadKeyPress> deadKeys)
    {
        var one = deadKeys.Count == 1;
        var names = string.Join(", ", deadKeys.Take(NamedInWarning).Select(k => k.Accent.Length == 0 ? $"'{k.Keys}'" : $"'{k.Keys}' ('{k.Accent}')")) +
                    (deadKeys.Count > NamedInWarning ? $" and {deadKeys.Count - NamedInWarning} more" : "");
        return KeyWarnings.Format(KeyWarnings.DeadKeyPressed,
            $"{names} pressed {(one ? "a dead key" : "dead keys")} of the window's keyboard layout, so {(one ? "its accent" : "their accents")} " +
            $"may be left waiting in the target, and the next key typed there may then come out with {(one ? "it" : "one")}, in front " +
            "of its character or combined with it into one letter. If text follows, check what it gives.");
    }

    /// <summary>
    /// send_keys' characters that went out as Unicode, each once, in order, with why (<see cref="KeyboardLayout.WhyNoKey"/>) and
    /// the index of its first key-down; a surrogate pair is one, which no key types.
    /// </summary>
    private static List<CharacterWithoutKey> CharactersWithoutKeys(IReadOnlyList<InputEvent> events, KeyboardLayout layout)
    {
        var characters = new List<CharacterWithoutKey>();
        var downs = events.Select((e, index) => (Event: e, Index: index))
            .Where(d => d.Event.Kind == InputEventKind.Unicode && (d.Event.Flags & NativeEnums.KEYEVENTF_KEYUP) == 0).ToList();
        for (var i = 0; i < downs.Count; i++)
        {
            var at = downs[i].Index;
            var c = (char)downs[i].Event.Code;
            var pair = char.IsHighSurrogate(c) && i + 1 < downs.Count && char.IsLowSurrogate((char)downs[i + 1].Event.Code);
            var text = pair ? new string([c, (char)downs[++i].Event.Code]) : c.ToString();
            if (!characters.Exists(k => k.Character == text))
            {
                var why = !pair ? layout.WhyNoKey(text[0]) : layout.ReadFailed ? NoKeyReason.LayoutUnread : NoKeyReason.NotOnLayout;
                characters.Add(new CharacterWithoutKey(text, text, why, at));
            }
        }

        return characters;
    }

    /// <summary>
    /// send_keys' keys that pressed a dead key of the layout (<see cref="KeyboardLayout.DeadKeyOf"/>) with the modifiers held,
    /// read as Windows reads them (an AltGr pair is Ctrl and Alt), each chord once, in order, named as send_keys writes it.
    /// </summary>
    private static List<DeadKeyPress> DeadKeysPressed(IReadOnlyList<InputEvent> events, KeyboardLayout layout)
    {
        var rightAlt = RightAltDown(events);
        var deadKeys = new List<DeadKeyPress>();
        foreach (var (chord, index) in SendKeysParser.IndexedChords(events, altGrIsCtrlAlt: true))
        {
            var name = SendKeysParser.Spec(chord);
            if (events[index].Kind == InputEventKind.VirtualKey && !deadKeys.Exists(d => d.Keys == name) &&
                layout.DeadKeyOf(chord.Code, chord.Modifiers, rightAlt[index]) is { } accent)
            {
                deadKeys.Add(new DeadKeyPress(name, accent, index));
            }
        }

        return deadKeys;
    }

    /// <summary>
    /// For each event, whether a right Alt pressed as a key ({RALT down}) is down there: AltGr on a layout that has one
    /// (<see cref="KeyboardLayout.DeadKeyOf"/>). The right Alt of a parser's AltGr pair (tagged <see cref="InputEvent.AltGr"/>)
    /// is not one: its left Ctrl is down with it, which the chord's modifiers already say.
    /// </summary>
    private static bool[] RightAltDown(IReadOnlyList<InputEvent> events)
    {
        var down = new bool[events.Count];
        var held = false;
        for (var i = 0; i < events.Count; i++)
        {
            down[i] = held;
            var e = events[i];
            if (e.Kind == InputEventKind.VirtualKey && !e.AltGr && e.Code is NativeEnums.VK_RMENU or NativeEnums.VK_MENU)
            {
                var isUp = (e.Flags & NativeEnums.KEYEVENTF_KEYUP) != 0;
                held = isUp ? false : held || e.Code == NativeEnums.VK_RMENU;
            }
        }

        return down;
    }

    /// <summary>
    /// Windows' hotkeys, which <see cref="SystemKeyboardHotkeys.Match"/> finds in the keys that went out. The registry is read
    /// only when <paramref name="events"/> hold a modifier or press the key the grave accent toggle uses
    /// (<see cref="SystemKeyboardHotkeys.IsGraveAccentKey"/>, the test Match applies), since no hotkey or toggle is pressed
    /// otherwise; null when it is not read, cannot be read, or the server has no layout queries (tests).
    /// </summary>
    private SystemKeyboardHotkeys? HotkeysToMatch(IReadOnlyList<InputEvent> events) =>
        _keyboardLayout != null &&
        events.Any(e => (e.Kind == InputEventKind.VirtualKey && KeyTable.IsModifier(e.Code)) || SystemKeyboardHotkeys.IsGraveAccentKey(e))
            ? _keyboardLayout.ReadSystemHotkeys()
            : null;
}
