using WindowsAppManagerMcp.Models;
using WindowsAppManagerMcp.Native;

namespace WindowsAppManagerMcp.Services;

/// <summary>
/// The user's pause hotkey (roadmap M1.10): the chord that pauses and resumes the server's input. The user
/// sets it with <see cref="EnvironmentVariable"/>; the agent cannot change it.
/// </summary>
public sealed class PauseHotkeyOptions
{
    /// <summary>The environment variable that sets the chord, such as "Ctrl+Alt+F12", or "none" to turn it off.</summary>
    public const string EnvironmentVariable = "WAM_PAUSE_HOTKEY";

    /// <summary>The default chord.</summary>
    public const string DefaultChord = "Ctrl+Alt+Shift+Pause";

    /// <summary>The source of a chord that is not configured.</summary>
    public const string DefaultSource = "default";

    private PauseHotkeyOptions(PauseHotkeyChord? chord, string source, string? warning)
    {
        Chord = chord;
        Source = source;
        Warning = warning;
    }

    /// <summary>The default: <see cref="DefaultChord"/>.</summary>
    public static PauseHotkeyOptions Default { get; } =
        new(PauseHotkeyChord.TryParse(DefaultChord, out var chord, out _) ? chord : null, DefaultSource, null);

    /// <summary>The chord; null when the user turned the hotkey off.</summary>
    public PauseHotkeyChord? Chord { get; }

    /// <summary>Where the chord comes from: <see cref="DefaultSource"/> or <see cref="EnvironmentVariable"/>.</summary>
    public string Source { get; }

    /// <summary>Why the configured value was not used (the default chord is used instead); null when it was.</summary>
    public string? Warning { get; }

    /// <summary>
    /// Reads <see cref="EnvironmentVariable"/>. Not set, empty or "default": the default chord. "none", "off" or
    /// "disabled": no hotkey. Anything else must be a chord such as "Ctrl+Alt+F12"; a value that is not one
    /// keeps the default chord in force (fail closed) and says why in <see cref="Warning"/>.
    /// </summary>
    public static PauseHotkeyOptions FromEnvironment(Func<string, string?>? readVariable = null)
    {
        var value = (readVariable ?? Environment.GetEnvironmentVariable)(EnvironmentVariable)?.Trim();
        if (string.IsNullOrEmpty(value) || value.Equals("default", StringComparison.OrdinalIgnoreCase))
        {
            return Default;
        }

        if (value.Equals("none", StringComparison.OrdinalIgnoreCase) ||
            value.Equals("off", StringComparison.OrdinalIgnoreCase) ||
            value.Equals("disabled", StringComparison.OrdinalIgnoreCase))
        {
            return new PauseHotkeyOptions(null, EnvironmentVariable, null);
        }

        return PauseHotkeyChord.TryParse(value, out var chord, out var error)
            ? new PauseHotkeyOptions(chord, EnvironmentVariable, null)
            : new PauseHotkeyOptions(Default.Chord, DefaultSource,
                $"{EnvironmentVariable} '{value}' is not a chord the pause hotkey can use ({error}), so the default {DefaultChord} is used.");
    }
}

/// <summary>A pause hotkey chord: modifiers and one key, such as Ctrl+Alt+Shift+Pause.</summary>
public sealed record PauseHotkeyChord
{
    /// <summary>RegisterHotKey's MOD_ALT.</summary>
    public const uint ModAlt = 0x0001;

    /// <summary>RegisterHotKey's MOD_CONTROL.</summary>
    public const uint ModControl = 0x0002;

    /// <summary>RegisterHotKey's MOD_SHIFT.</summary>
    public const uint ModShift = 0x0004;

    /// <summary>RegisterHotKey's MOD_WIN.</summary>
    public const uint ModWin = 0x0008;

    /// <summary>RegisterHotKey's MOD_NOREPEAT: holding the chord down fires it once, so it does not toggle back and forth.</summary>
    public const uint ModNoRepeat = 0x4000;

    private PauseHotkeyChord(string text, KeyModifiers modifiers, ushort key, IReadOnlyList<ushort> virtualKeys)
    {
        Text = text;
        Modifiers = modifiers;
        Key = key;
        VirtualKeys = virtualKeys;
    }

    /// <summary>The chord as it is shown, such as "Ctrl+Alt+Shift+Pause".</summary>
    public string Text { get; }

    /// <summary>The modifiers that must be held.</summary>
    public KeyModifiers Modifiers { get; }

    /// <summary>The key's virtual-key code.</summary>
    public ushort Key { get; }

    /// <summary>
    /// The virtual keys the hotkey is registered for. Usually just <see cref="Key"/>; for Pause with Ctrl also
    /// VK_CANCEL, because a keyboard sends Ctrl+Pause as Ctrl+Break (VK_CANCEL), while remote sessions and some
    /// keyboards send VK_PAUSE.
    /// </summary>
    public IReadOnlyList<ushort> VirtualKeys { get; }

    /// <summary>The RegisterHotKey modifier flags, MOD_NOREPEAT included.</summary>
    public uint RegisterModifiers =>
        ((Modifiers & KeyModifiers.Alt) != 0 ? ModAlt : 0) |
        ((Modifiers & KeyModifiers.Control) != 0 ? ModControl : 0) |
        ((Modifiers & KeyModifiers.Shift) != 0 ? ModShift : 0) |
        ((Modifiers & KeyModifiers.Windows) != 0 ? ModWin : 0) |
        ModNoRepeat;

    /// <summary>
    /// True when <paramref name="events"/> press the chord: one of <see cref="VirtualKeys"/> goes down while at
    /// least the chord's modifiers are held. Every left or right Ctrl and Alt counts, the AltGr pair a parser sent for a
    /// character included (<see cref="InputEvent.AltGr"/>): RegisterHotKey reads the modifier keys, not the character, so
    /// typing '@' as AltGr+Q fires a Ctrl+Alt+Q hotkey.
    /// </summary>
    public bool IsPressedBy(IReadOnlyList<InputEvent> events) =>
        SendKeysParser.Chords(events, altGrIsCtrlAlt: true).Any(chord =>
            chord.Kind == InputEventKind.VirtualKey &&
            VirtualKeys.Contains(chord.Code) &&
            (chord.Modifiers & Modifiers) == Modifiers);

    /// <summary>
    /// Parses a chord such as "Ctrl+Alt+Shift+Pause": modifiers (Ctrl or Control, Alt, Shift, Win or Windows)
    /// and one key (a letter, a digit, F1-F24, Pause or Break, Esc, Space, Enter, Tab, Backspace, Insert,
    /// Delete, Home, End, PgUp, PgDn, the arrows, ScrollLock), separated by '+', in any case. A chord needs a
    /// modifier, and Shift alone is not enough for a key that types text; only Pause, ScrollLock and F13-F24
    /// may stand alone.
    /// </summary>
    public static bool TryParse(string text, out PauseHotkeyChord? chord, out string? error)
    {
        ArgumentNullException.ThrowIfNull(text);
        chord = null;

        var parts = text.Split('+', StringSplitOptions.TrimEntries);
        if (parts.Any(p => p.Length == 0))
        {
            error = "an empty part between '+' signs";
            return false;
        }

        var modifiers = KeyModifiers.None;
        foreach (var part in parts[..^1])
        {
            var modifier = part.ToUpperInvariant() switch
            {
                "CTRL" or "CONTROL" => KeyModifiers.Control,
                "ALT" => KeyModifiers.Alt,
                "SHIFT" => KeyModifiers.Shift,
                "WIN" or "WINDOWS" => KeyModifiers.Windows,
                _ => KeyModifiers.None
            };
            if (modifier == KeyModifiers.None)
            {
                error = $"'{part}' is not a modifier (Ctrl, Alt, Shift or Win)";
                return false;
            }

            modifiers |= modifier;
        }

        if (KeyNames.Parse(parts[^1]) is not { } key)
        {
            error = $"'{parts[^1]}' is not a key the pause hotkey can use";
            return false;
        }

        var standsAlone = key is NativeEnums.VK_PAUSE or NativeEnums.VK_SCROLL || key >= NativeEnums.VK_F1 + 12 && key <= NativeEnums.VK_F1 + 23;
        if (modifiers == KeyModifiers.None && !standsAlone)
        {
            error = "it needs at least one modifier (Ctrl, Alt, Shift or Win)";
            return false;
        }

        if (modifiers == KeyModifiers.Shift && TypesText(key))
        {
            error = "Shift alone with that key types text";
            return false;
        }

        var name = key == NativeEnums.VK_PAUSE ? "Pause" : KeyNames.Of(key);
        var shown = string.Join('+', new[]
        {
            (KeyModifiers.Control, "Ctrl"), (KeyModifiers.Alt, "Alt"), (KeyModifiers.Shift, "Shift"), (KeyModifiers.Windows, "Win")
        }.Where(m => (modifiers & m.Item1) != 0).Select(m => m.Item2).Append(name));

        IReadOnlyList<ushort> virtualKeys = key == NativeEnums.VK_PAUSE && (modifiers & KeyModifiers.Control) != 0
            ? [KeyNames.VkCancel, NativeEnums.VK_PAUSE]
            : [key];

        chord = new PauseHotkeyChord(shown, modifiers, key, virtualKeys);
        error = null;
        return true;
    }

    private static bool TypesText(ushort key) =>
        key is >= 'A' and <= 'Z' or >= '0' and <= '9' ||
        key is NativeEnums.VK_SPACE or NativeEnums.VK_RETURN or NativeEnums.VK_TAB or NativeEnums.VK_BACK;
}
