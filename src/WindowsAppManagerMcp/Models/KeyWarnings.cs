namespace WindowsAppManagerMcp.Models;

/// <summary>
/// The warnings a send_keys or press_keys call that went through can carry (issues #9 and #10): the call succeeded, but some
/// of its keys may not have done what they look like. Each starts with a stable code and a colon, as the captures' warnings
/// do (<see cref="CaptureWarnings"/>), so an agent can match on the code; the rest of the sentence says what happened and
/// what to do.
/// </summary>
public static class KeyWarnings
{
    /// <summary>
    /// A chord's character (press_keys) or a character (send_keys) went out as the character alone (KEYEVENTF_UNICODE), with
    /// no key, for the reason the warning gives each: no key of the window's keyboard layout types it (or only a dead key
    /// does), an accent typed on a dead key was waiting on the layout and its key would have typed that accent too, or the
    /// layout could not be read. A text field gets the character, but an application that matches the physical key (a
    /// shortcut, KeyboardEvent.code, a scan code or virtual key) sees no key press. The result's sentAsCharacters lists them.
    /// </summary>
    public const string SentAsCharacter = "SentAsCharacter";

    /// <summary>
    /// The keyboard layout of the window the keys went to could not be read, so the keys went without scan codes, and every
    /// character but a letter, a digit or the space as a character with no key.
    /// </summary>
    public const string KeyboardLayoutUnknown = "KeyboardLayoutUnknown";

    /// <summary>
    /// A chord the call pressed is one Windows takes for itself: an input-method hotkey (switch to an input language or
    /// layout, an IME mode) or a language-switch toggle, so it may never reach the application.
    /// </summary>
    public const string SystemHotkey = "SystemHotkey";

    /// <summary>"Code: text".</summary>
    public static string Format(string code, string text) => $"{code}: {text}";
}
