namespace WindowsAppManagerMcp.Services.Interfaces;

/// <summary>
/// The keyboard layout queries send_keys maps characters with (roadmap M4.1): which key, with which shift state, types a
/// character on the layout of the window the keys go to, and the key's scan code; and Windows' own keyboard hotkeys, which
/// take a chord before any application sees it (issue #10). Every member only reads. Tests replace it with a fake that
/// models a few layouts.
/// </summary>
public interface INativeKeyboardLayoutWrapper
{
    /// <summary>
    /// The input locale (HKL) of the thread that owns <paramref name="window"/>, or of the foreground window's thread when
    /// <paramref name="window"/> is 0. 0 when it cannot be read.
    /// </summary>
    nint GetKeyboardLayout(nint window);

    /// <summary>
    /// VkKeyScanExW: the low byte is the virtual key that types <paramref name="character"/>, the high byte the shift
    /// state it needs (1 Shift, 2 Ctrl, 4 Alt; Ctrl and Alt together are AltGr); -1 when no key of the layout types it.
    /// </summary>
    short VkKeyScan(char character, nint layout);

    /// <summary>
    /// MapVirtualKeyExW(MAPVK_VK_TO_VSC_EX): the scan code of <paramref name="virtualKey"/>, with 0xE0 (or 0xE1, Pause) in
    /// the high byte for an extended key; 0 when the layout has none.
    /// </summary>
    uint ScanCode(ushort virtualKey, nint layout);

    /// <summary>
    /// What the key types with <paramref name="shiftState"/> held (the high byte of <see cref="VkKeyScan"/>) and Caps Lock
    /// on or off, read with ToUnicodeEx without changing any keyboard state; null when it is a dead key (an accent that
    /// waits for the next key) or types nothing.
    /// </summary>
    string? KeyText(ushort virtualKey, uint scanCode, byte shiftState, bool capsLock, nint layout);

    /// <summary>
    /// The accent the key leaves waiting for the next key when it is a dead key with <paramref name="shiftState"/> held and
    /// Caps Lock on or off: ToUnicodeEx's dead-key answer, read the same way as <see cref="KeyText"/>, without changing any
    /// keyboard state, as the spacing accent it gives (empty when it gives none). Null when the key types something or nothing
    /// in that state, as it does while an accent already waits: then it types that accent and its own.
    /// </summary>
    string? DeadKeyText(ushort virtualKey, uint scanCode, byte shiftState, bool capsLock, nint layout);

    /// <summary>
    /// The user's input-method hotkeys (HKCU\Control Panel\Input Method\Hot Keys) and language-switch toggles (HKCU\Keyboard
    /// Layout\Toggle), read from the registry now; null when they cannot be read. press_keys and send_keys read them once per
    /// call that holds a modifier, to warn about a chord Windows takes (SystemHotkey); a failed read gives no warning.
    /// </summary>
    SystemKeyboardHotkeys? ReadSystemHotkeys();
}
