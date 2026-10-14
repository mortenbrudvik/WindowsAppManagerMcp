using System.Text.Json.Serialization;

namespace WindowsAppManagerMcp.Models;

/// <summary>
/// How type_text puts the text into the window (roadmap M4.4): typed as Unicode characters, typed with the keys of the
/// window's keyboard layout, or pasted through the clipboard; auto pastes long text and types the rest.
/// </summary>
[ToolEnum(WindowsAppManagerMcp.Infrastructure.ToolErrorReporting.InvalidArgumentsErrorCode, "text input method")]
public enum TextInputMethod
{
    /// <summary>
    /// Pastes text longer than <see cref="Services.InputSimulationService.AutoPasteThreshold"/> characters when the
    /// clipboard can be lent and put back and the window is no terminal; otherwise types it as <see cref="Unicode"/>.
    /// </summary>
    [JsonStringEnumMemberName("auto")]
    Auto,

    /// <summary>Every character as a Unicode keystroke (KEYEVENTF_UNICODE): any language, no keyboard layout involved.</summary>
    [JsonStringEnumMemberName("unicode")]
    Unicode,

    /// <summary>
    /// Every character with the key of the window's keyboard layout, with Shift or AltGr and the scan code, as send_keys
    /// types it (roadmap M4.1): for remote desktop and virtual machine windows that drop Unicode keystrokes. A character
    /// the layout has no key for is sent as Unicode.
    /// </summary>
    [JsonStringEnumMemberName("keys")]
    Keys,

    /// <summary>
    /// Puts the text on the clipboard, presses Ctrl+V and puts the clipboard's earlier text back: fast, and free of
    /// autocomplete and auto-indent. Line breaks and tabs are pasted as they are.
    /// </summary>
    [JsonStringEnumMemberName("paste")]
    Paste
}
