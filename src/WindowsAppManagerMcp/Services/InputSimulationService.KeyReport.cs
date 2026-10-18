using WindowsAppManagerMcp.Models;
using WindowsAppManagerMcp.Native;

namespace WindowsAppManagerMcp.Services;

/// <summary>
/// What a send_keys or press_keys call that went through tells about its keys (issues #9 and #10): the chords as pressed on
/// the window's layout where that differs from what was asked, what went out as a character with no key, and the warnings
/// (<see cref="KeyWarnings"/>). A call that succeeds can still miss its target: a character sent without its key fires no
/// shortcut, and a chord Windows takes for itself never reaches the application, so the result says so instead of looking
/// like any other success.
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

    /// <summary>What the result of a call adds on success: <see cref="SendKeysResult.ResolvedChords"/>, sentAsCharacters and warnings.</summary>
    private sealed record KeyReport(IReadOnlyDictionary<string, string>? ResolvedChords, IReadOnlyList<string>? SentAsCharacters,
        IReadOnlyList<string>? Warnings)
    {
        public static KeyReport None { get; } = new(null, null, null);

        public SendKeysResult ApplyTo(SendKeysResult result) =>
            result with { ResolvedChords = ResolvedChords, SentAsCharacters = SentAsCharacters, Warnings = Warnings };
    }

    /// <summary>
    /// The report of <paramref name="spec"/>'s keys on <paramref name="layout"/>, read before anything is sent: press_keys' chords
    /// (<paramref name="pressKeys"/>) are named as press_keys writes them, send_keys' as send_keys does. Windows' hotkeys are
    /// read once, and only for keys that could press one (a modifier, or the key left of 1); a read that fails warns of nothing.
    /// </summary>
    private KeyReport ReportKeys(KeySpec spec, KeyboardLayout layout, bool pressKeys)
    {
        var warnings = new List<string>();
        if (layout.ReadFailed)
        {
            warnings.Add(KeyWarnings.Format(KeyWarnings.KeyboardLayoutUnknown,
                "the keyboard layout of the window the keys went to could not be read, so they were sent without scan codes, and " +
                "every character but a letter, a digit or the space as a character alone, with no key. Apps that read the physical " +
                "key (shortcuts in browsers and Electron apps, KeyboardEvent.code, scan codes) may not react: check the window, and " +
                "pass windowHandle so that its own layout is read."));
        }

        var withoutKeys = pressKeys ? spec.SentAsCharacters ?? [] : CharactersWithoutKeys(spec.Events, layout);
        if (withoutKeys.Count > 0)
        {
            warnings.Add(SentAsCharacterWarning(withoutKeys, pressKeys));
        }

        var resolved = pressKeys ? spec.ResolvedChords : null;
        foreach (var match in SystemHotkeysPressed(spec.Events, layout))
        {
            // Matched on the chord, not on its name: 'lctrl+=' and 'shift+ctrl+=' both press what NameOf writes ctrl+shift+0
            var name = pressKeys ? PressKeysParser.NameOf(match.Chord) : SendKeysParser.Spec(match.Chord);
            var asked = resolved?.FirstOrDefault(r => r.Chord == match.Chord) is { } chord ? $" (pressed for '{chord.Asked}')" : "";
            warnings.Add(KeyWarnings.Format(KeyWarnings.SystemHotkey, match.Toggle
                ? $"{name}{asked} is {match.What}, so it may switch that instead of reaching the application, and later keys then type " +
                  "on another layout. Press the modifiers with a key, or ask the user whether to change the hotkey."
                : $"{name}{asked} is {match.What}, so Windows takes it and the application does not get it. Press another chord (when " +
                  "a character's Shift made it this one, a key that types without it, such as plus to zoom in on a Norwegian " +
                  "keyboard), or ask the user to change the hotkey (Settings > Time & language > Typing > Advanced keyboard settings > " +
                  "Input language hot keys)."));
        }

        if (warnings.Count == 0 && resolved == null)
        {
            return KeyReport.None;
        }

        Dictionary<string, string>? resolvedChords = null;
        foreach (var chord in resolved ?? [])
        {
            (resolvedChords ??= new Dictionary<string, string>(StringComparer.Ordinal)).TryAdd(chord.Asked, chord.Pressed);
        }

        var listed = withoutKeys.Count == 0 ? null : withoutKeys.Take(MaxListedWithoutKeys).Select(k => k.Keys).ToList();
        return new KeyReport(resolvedChords, listed, warnings.Count == 0 ? null : warnings);
    }

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
    /// send_keys' characters that went out as Unicode, each once, in order, with why (<see cref="KeyboardLayout.WhyNoKey"/>); a
    /// surrogate pair is one, which no key types.
    /// </summary>
    private static List<CharacterWithoutKey> CharactersWithoutKeys(IReadOnlyList<InputEvent> events, KeyboardLayout layout)
    {
        var characters = new List<CharacterWithoutKey>();
        var downs = events.Where(e => e.Kind == InputEventKind.Unicode && (e.Flags & NativeEnums.KEYEVENTF_KEYUP) == 0)
            .Select(e => (char)e.Code).ToList();
        for (var i = 0; i < downs.Count; i++)
        {
            var pair = char.IsHighSurrogate(downs[i]) && i + 1 < downs.Count && char.IsLowSurrogate(downs[i + 1]);
            var text = pair ? new string([downs[i], downs[++i]]) : downs[i].ToString();
            if (!characters.Exists(c => c.Character == text))
            {
                var why = !pair ? layout.WhyNoKey(text[0]) : layout.ReadFailed ? NoKeyReason.LayoutUnread : NoKeyReason.NotOnLayout;
                characters.Add(new CharacterWithoutKey(text, text, why));
            }
        }

        return characters;
    }

    /// <summary>
    /// The chords of <paramref name="events"/> that Windows takes for itself (<see cref="SystemKeyboardHotkeys.Match"/>). The
    /// registry is read only when the keys hold a modifier or press the key the grave accent toggle uses
    /// (<see cref="SystemKeyboardHotkeys.IsGraveAccentKey"/>, the test Match applies), since no hotkey or toggle is pressed
    /// otherwise; nothing when it cannot be read or the server has no layout queries (tests).
    /// </summary>
    private IReadOnlyList<SystemHotkeyMatch> SystemHotkeysPressed(IReadOnlyList<InputEvent> events, KeyboardLayout layout)
    {
        if (_keyboardLayout == null ||
            !events.Any(e => (e.Kind == InputEventKind.VirtualKey && KeyTable.IsModifier(e.Code)) || SystemKeyboardHotkeys.IsGraveAccentKey(e)))
        {
            return [];
        }

        return _keyboardLayout.ReadSystemHotkeys()?.Match(events, layout.Handle) ?? [];
    }
}
