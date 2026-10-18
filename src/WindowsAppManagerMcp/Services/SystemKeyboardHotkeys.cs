using System.Globalization;
using WindowsAppManagerMcp.Models;
using WindowsAppManagerMcp.Native;

namespace WindowsAppManagerMcp.Services;

/// <summary>
/// Windows' own keyboard hotkeys, which take a chord before the application in front sees it (issue #10), as the user's
/// registry holds them: the input-method hotkeys (HKCU\Control Panel\Input Method\Hot Keys: switch straight to an input
/// language or keyboard layout, or turn an Asian IME on or off) and the language-switch toggles (HKCU\Keyboard Layout\Toggle:
/// Left Alt+Shift, Ctrl+Shift or the grave accent key switches the input language or the layout). press_keys and send_keys
/// warn when they press one (<see cref="KeyWarnings.SystemHotkey"/>): on a layout where '=' needs Shift, 'ctrl+=' is
/// Ctrl+Shift+0, which can be such a hotkey, so the call goes through and the application never sees the chord.
/// </summary>
/// <param name="InputMethodHotkeys">The input-method hotkeys that have a key.</param>
/// <param name="Toggles">The language-switch toggles that are on.</param>
public sealed record SystemKeyboardHotkeys(IReadOnlyList<InputMethodHotkey> InputMethodHotkeys, IReadOnlyList<LanguageToggle> Toggles)
{
    /// <summary>No hotkeys: what a test without any reads.</summary>
    public static SystemKeyboardHotkeys None { get; } = new([], []);

    /// <summary>The registry key of the input-method hotkeys, under HKEY_CURRENT_USER: one subkey per hotkey, named by its id.</summary>
    public const string InputMethodHotkeysKey = @"Control Panel\Input Method\Hot Keys";

    /// <summary>The registry key of the language-switch toggles, under HKEY_CURRENT_USER.</summary>
    public const string ToggleKey = @"Keyboard Layout\Toggle";

    /// <summary>
    /// The hotkeys and toggles that <paramref name="events"/> press, each once, in the order pressed: a key-down with exactly a
    /// hotkey's modifiers held (read as Windows reads them: an AltGr pair is Ctrl and Alt), or modifiers pressed and released
    /// with no other key between them that make a toggle's chord (Alt+Shift, Ctrl+Shift), or the grave accent key alone. An
    /// input-method hotkey of an Asian IME counts only when <paramref name="layout"/> is of that language (or unknown, 0).
    /// </summary>
    public IReadOnlyList<SystemHotkeyMatch> Match(IReadOnlyList<InputEvent> events, nint layout)
    {
        ArgumentNullException.ThrowIfNull(events);
        var matches = new List<SystemHotkeyMatch>();
        var language = (int)(layout & 0x3FF);

        foreach (var (chord, index) in SendKeysParser.IndexedChords(events, altGrIsCtrlAlt: true))
        {
            if (chord.Kind != InputEventKind.VirtualKey)
            {
                continue;
            }

            foreach (var hotkey in InputMethodHotkeys)
            {
                if (hotkey.VirtualKey == chord.Code && (hotkey.AnyModifiers || hotkey.Modifiers == chord.Modifiers) &&
                    (layout == 0 || hotkey.AppliesTo(language)))
                {
                    Add(chord, $"a Windows input-method hotkey (HKCU\\{InputMethodHotkeysKey}\\{hotkey.Name}), which {hotkey.Does}", toggle: false);
                }
            }

            var grave = chord.Modifiers == KeyModifiers.None && IsGraveAccentKey(events[index]);
            if (grave && Toggles.FirstOrDefault(t => t.Keys == LanguageToggleKeys.GraveAccent) is { } toggle)
            {
                Add(chord, toggle.Describe(), toggle: true);
            }
        }

        // A toggle fires on modifiers pressed and released alone: KeyPresses reads the last one's tap with the others held
        foreach (var press in SendKeysParser.KeyPresses(events))
        {
            if (press.Kind != InputEventKind.VirtualKey || SendKeysParser.ModifierOf(press.Code) == KeyModifiers.None)
            {
                continue;
            }

            var keys = (press.Modifiers | SendKeysParser.ModifierOf(press.Code)) switch
            {
                KeyModifiers.Alt | KeyModifiers.Shift => LanguageToggleKeys.LeftAltShift,
                KeyModifiers.Control | KeyModifiers.Shift => LanguageToggleKeys.CtrlShift,
                _ => (LanguageToggleKeys?)null
            };
            if (keys != null && Toggles.FirstOrDefault(t => t.Keys == keys) is { } toggle)
            {
                Add(press, toggle.Describe(), toggle: true);
            }
        }

        return matches;

        void Add(KeyChord chord, string what, bool toggle)
        {
            if (!matches.Exists(m => m.Chord == chord))
            {
                matches.Add(new SystemHotkeyMatch(chord, what, toggle));
            }
        }
    }

    /// <summary>
    /// True when <paramref name="e"/> is the key the grave accent toggle (4) switches with: the key left of 1 by its scan code,
    /// whatever the layout calls it, or VK_OEM_3 sent without one (the invariant layout). Not VK_OEM_3 with another scan code:
    /// that is another key, such as 'ø' on a Norwegian keyboard or 'ö' on a German one. The read of Windows' hotkeys uses the
    /// same test, so it reads them only for keys Match can find.
    /// </summary>
    public static bool IsGraveAccentKey(InputEvent e) =>
        e.Kind == InputEventKind.VirtualKey && (e.Scan == GraveScanCode || (e.Scan == 0 && e.Code == VkOem3));

    /// <summary>The scan code of the key left of 1 (the grave accent on a US keyboard), which toggle 4 switches with.</summary>
    private const ushort GraveScanCode = 0x29;

    /// <summary>VK_OEM_3, the grave accent key's virtual key on a US keyboard.</summary>
    private const ushort VkOem3 = 0xC0;

    /// <summary>
    /// The toggles that are on, from the Toggle key's values (REG_SZ "1" to "4"; null when absent): Language Hotkey (Hotkey
    /// on older Windows) switches the input language, Layout Hotkey the keyboard layout within one. A value that is absent
    /// counts as Windows' default (Left Alt+Shift for the language, Ctrl+Shift for the layout), and so does the whole key
    /// when <paramref name="keyExists"/> is false; 3 (and any other value) turns the toggle off.
    /// </summary>
    public static IReadOnlyList<LanguageToggle> TogglesFrom(bool keyExists, object? hotkey, object? languageHotkey, object? layoutHotkey)
    {
        var toggles = new List<LanguageToggle>();
        var language = keyExists ? ToggleValue(languageHotkey) ?? ToggleValue(hotkey) ?? 1 : 1;
        var layout = keyExists ? ToggleValue(layoutHotkey) ?? 2 : 2;
        Add("Language Hotkey", language, "the input language");
        Add("Layout Hotkey", layout, "the keyboard layout");
        return toggles;

        void Add(string value, int setting, string switches)
        {
            if (setting is 1 or 2 or 4 && !toggles.Exists(t => (int)t.Keys == setting))
            {
                toggles.Add(new LanguageToggle(value, (LanguageToggleKeys)setting, switches));
            }
        }
    }

    private static int? ToggleValue(object? value) => value switch
    {
        string text when int.TryParse(text.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var number) => number,
        int number => number,
        _ => null
    };
}

/// <summary>
/// A chord that Windows takes, what takes it (for the SystemHotkey warning), and whether that is a language-switch toggle,
/// which acts on the modifiers' release and may let the keys through, rather than an input-method hotkey, which keeps the
/// chord from the application.
/// </summary>
public sealed record SystemHotkeyMatch(KeyChord Chord, string What, bool Toggle);

/// <summary>
/// One input-method hotkey (imm.h's IME_*HOTKEY_*), as HKCU\Control Panel\Input Method\Hot Keys\&lt;id&gt; holds it: the
/// modifiers (Key Modifiers: MOD_ALT 1, MOD_CONTROL 2, MOD_SHIFT 4; MOD_LEFT 0x8000 and MOD_RIGHT 0x4000 say which side's
/// keys count, both for either, which the match does not tell apart; MOD_IGNORE_ALL_MODIFIER 0x400 matches the key with
/// any) and the key (Virtual Key). Ids 0x100 to 0x11F switch straight to an input language or layout and work whatever
/// layout is active; the others belong to the Chinese, Japanese or Korean IMEs and work only with one of them.
/// </summary>
public sealed record InputMethodHotkey(int Id, KeyModifiers Modifiers, ushort VirtualKey, bool AnyModifiers)
{
    private const uint ModAlt = 0x0001, ModControl = 0x0002, ModShift = 0x0004, ModWin = 0x0008, ModIgnoreAllModifiers = 0x0400;

    /// <summary>The subkey's name: the id as eight hex digits (00000104).</summary>
    public string Name => Id.ToString("X8", CultureInfo.InvariantCulture);

    /// <summary>What the hotkey does, for the warning.</summary>
    public string Does => Id switch
    {
        >= 0x100 and <= 0x11F => "switches to an input language or keyboard layout",
        _ => "turns an input method (IME) mode on or off"
    };

    /// <summary>
    /// The hotkey of the subkey <paramref name="subkeyName"/> from its Key Modifiers and Virtual Key values (REG_BINARY, four
    /// bytes, little-endian; a DWORD is read too); null when the name is no id, or it has no key (absent, 0, or 0xFF and
    /// above, which mark a hotkey without one).
    /// </summary>
    public static InputMethodHotkey? FromRegistry(string subkeyName, object? keyModifiers, object? virtualKey)
    {
        ArgumentNullException.ThrowIfNull(subkeyName);
        if (!int.TryParse(subkeyName, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var id) ||
            Number(virtualKey) is not { } key || key is 0 or >= 0xFF)
        {
            return null;
        }

        var bits = Number(keyModifiers) ?? 0;
        var modifiers = ((bits & ModAlt) != 0 ? KeyModifiers.Alt : KeyModifiers.None) |
                        ((bits & ModControl) != 0 ? KeyModifiers.Control : KeyModifiers.None) |
                        ((bits & ModShift) != 0 ? KeyModifiers.Shift : KeyModifiers.None) |
                        ((bits & ModWin) != 0 ? KeyModifiers.Windows : KeyModifiers.None);
        return new InputMethodHotkey(id, modifiers, (ushort)key, (bits & ModIgnoreAllModifiers) != 0);
    }

    /// <summary>
    /// True when the hotkey works with a layout of the primary language <paramref name="language"/> (the low 10 bits of its
    /// language id): every layout for a direct switch (and an id imm.h does not name, to be safe); Chinese for
    /// IME_CHOTKEY_* (0x10-0x2F) and IME_THOTKEY_* (0x70-0x8F), Japanese for IME_JHOTKEY_* (0x30-0x4F), Korean for
    /// IME_KHOTKEY_* (0x50-0x6F), and any of the three for an IME's private hotkeys (0x200-0x21F).
    /// </summary>
    public bool AppliesTo(int language) => Id switch
    {
        >= 0x10 and <= 0x2F or >= 0x70 and <= 0x8F => language == LangChinese,
        >= 0x30 and <= 0x4F => language == LangJapanese,
        >= 0x50 and <= 0x6F => language == LangKorean,
        >= 0x200 and <= 0x21F => language is LangChinese or LangJapanese or LangKorean,
        _ => true
    };

    private const int LangChinese = 0x04, LangJapanese = 0x11, LangKorean = 0x12;

    private static uint? Number(object? value) => value switch
    {
        byte[] { Length: > 0 } bytes => bytes.Take(4).Select((b, i) => (uint)b << (8 * i)).Aggregate(0u, (a, b) => a | b),
        int number => unchecked((uint)number),
        _ => null
    };
}

/// <summary>The keys of a language-switch toggle (HKCU\Keyboard Layout\Toggle's values; 3 is off).</summary>
public enum LanguageToggleKeys
{
    /// <summary>1: the left Alt and Shift, pressed together and released with no other key.</summary>
    LeftAltShift = 1,

    /// <summary>2: Ctrl and Shift, pressed together and released with no other key.</summary>
    CtrlShift = 2,

    /// <summary>4: the grave accent key alone (the key left of 1), as on Thai keyboards.</summary>
    GraveAccent = 4
}

/// <summary>A language-switch toggle that is on: its Toggle value's name, its keys and what it switches.</summary>
public sealed record LanguageToggle(string Value, LanguageToggleKeys Keys, string Switches)
{
    /// <summary>What the toggle does, for the warning.</summary>
    public string Describe() =>
        $"Windows' switch of {Switches} (HKCU\\{SystemKeyboardHotkeys.ToggleKey}, {Value} {(int)Keys}) when more than one is installed";
}
