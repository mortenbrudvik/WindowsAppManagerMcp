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
    /// <summary>At most this many chords or characters are named in one warning; the result's lists have them all.</summary>
    private const int NamedInWarning = 5;

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
    /// read once, and only for keys that could press one (a modifier, or the grave accent key); a read that fails warns of nothing.
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

        var withoutKeys = pressKeys ? spec.SentAsCharacters ?? [] : CharactersWithoutKeys(spec.Events);
        if (withoutKeys.Count > 0)
        {
            warnings.Add(SentAsCharacterWarning(withoutKeys, layout, pressKeys));
        }

        foreach (var match in SystemHotkeysPressed(spec.Events, layout))
        {
            var name = pressKeys ? PressKeysParser.NameOf(match.Chord) : SendKeysParser.Spec(match.Chord);
            var asked = pressKeys && spec.ResolvedChords?.FirstOrDefault(r => r.Value == name).Key is { } chord ? $" (pressed for '{chord}')" : "";
            warnings.Add(KeyWarnings.Format(KeyWarnings.SystemHotkey, match.Toggle
                ? $"{name}{asked} is {match.What}, so it may switch that instead of reaching the application, and later keys then type " +
                  "on another layout. Press the modifiers with a key, or ask the user whether to change the hotkey."
                : $"{name}{asked} is {match.What}, so Windows takes it and the application does not get it. Press another chord (name " +
                  "the key by its place, such as oem_plus, if a character's Shift made it this one), or ask the user to change the " +
                  "hotkey (Settings > Time & language > Typing > Advanced keyboard settings > Input language hot keys)."));
        }

        return warnings.Count == 0 && spec.ResolvedChords == null
            ? KeyReport.None
            : new KeyReport(pressKeys ? spec.ResolvedChords : null, withoutKeys.Count == 0 ? null : withoutKeys.Select(k => k.Keys).ToList(),
                warnings.Count == 0 ? null : warnings);
    }

    /// <summary>
    /// The SentAsCharacter warning: which chords or characters went out alone, why (the layout was not read, an accent waits on
    /// it, or no key of it types them), and what that means.
    /// </summary>
    private static string SentAsCharacterWarning(IReadOnlyList<CharacterWithoutKey> withoutKeys, KeyboardLayout layout, bool pressKeys)
    {
        var named = withoutKeys.Take(NamedInWarning)
            .Select(k => pressKeys ? $"'{k.Keys}' ('{k.Character}')" : $"'{k.Character}'")
            .ToList();
        var list = string.Join(", ", named) + (withoutKeys.Count > NamedInWarning ? $" and {withoutKeys.Count - NamedInWarning} more" : "");
        var one = withoutKeys.Count == 1;
        var why = layout.ReadFailed ? "the window's keyboard layout could not be read"
            : layout.DeadKeyPending ? "an accent typed on a dead key was waiting on the keyboard layout, so its keys did not type them alone"
            : $"no key of the window's keyboard layout types {(one ? "it" : "them")}, or only a dead key does";
        var advice = pressKeys
            ? "To press a key whatever it types, name it by its place: oem_plus, oem_minus, oem_comma, oem_period, oem_1 to oem_8, oem_102."
            : "To press a key, name it ({OEM_PLUS}, {OEM_1}, ...) or use press_keys.";
        var how = pressKeys ? one ? "with its character" : "with their characters" : one ? "as a character" : "as characters";
        return KeyWarnings.Format(KeyWarnings.SentAsCharacter,
            $"{list} went out {how} alone, with no key ({why}). A text field gets " +
            $"{(one ? "it" : "them")}, but apps that match the physical key (shortcuts, KeyboardEvent.code, scan codes) see no key " +
            $"press. {advice}");
    }

    /// <summary>send_keys' characters that went out as Unicode, each once, in order; a surrogate pair is one.</summary>
    private static List<CharacterWithoutKey> CharactersWithoutKeys(IReadOnlyList<InputEvent> events)
    {
        var characters = new List<CharacterWithoutKey>();
        var downs = events.Where(e => e.Kind == InputEventKind.Unicode && (e.Flags & NativeEnums.KEYEVENTF_KEYUP) == 0)
            .Select(e => (char)e.Code).ToList();
        for (var i = 0; i < downs.Count; i++)
        {
            var text = char.IsHighSurrogate(downs[i]) && i + 1 < downs.Count && char.IsLowSurrogate(downs[i + 1])
                ? new string([downs[i], downs[++i]])
                : downs[i].ToString();
            if (!characters.Exists(c => c.Character == text))
            {
                characters.Add(new CharacterWithoutKey(text, text));
            }
        }

        return characters;
    }

    /// <summary>
    /// The chords of <paramref name="events"/> that Windows takes for itself (<see cref="SystemKeyboardHotkeys.Match"/>). The
    /// registry is read only when the keys hold a modifier or press the grave accent key, since no hotkey or toggle is
    /// pressed otherwise; nothing when it cannot be read or the server has no layout queries (tests).
    /// </summary>
    private IReadOnlyList<SystemHotkeyMatch> SystemHotkeysPressed(IReadOnlyList<InputEvent> events, KeyboardLayout layout)
    {
        if (_keyboardLayout == null || !events.Any(e => e.Kind == InputEventKind.VirtualKey &&
                                                        (KeyTable.IsModifier(e.Code) || e.Scan == GraveAccentScan || e.Code == VkOem3)))
        {
            return [];
        }

        return _keyboardLayout.ReadSystemHotkeys()?.Match(events, layout.Handle) ?? [];
    }

    private const ushort GraveAccentScan = 0x29, VkOem3 = 0xC0;
}
