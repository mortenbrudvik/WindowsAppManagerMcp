using System.Text.Json.Serialization;

namespace WindowsAppManagerMcp.Models;

/// <summary>
/// What type_text does with the line breaks (\n, after \r\n and a lone \r became \n) and tabs of the text (roadmap M4.1).
/// There is no default: text that contains either needs one, because pressing Enter would submit a chat message or a form,
/// and Tab would move to the next field or Excel cell.
/// </summary>
[ToolEnum(WindowsAppManagerMcp.Infrastructure.ToolErrorReporting.InvalidArgumentsErrorCode, "control character mode")]
public enum TextControlChars
{
    /// <summary>
    /// Typed as the Unicode characters U+000A and U+0009, like every other character: no Enter or Tab key is pressed, so
    /// most editors insert them, while dialogs and chat boxes do not act on them as Enter or Tab (some apps ignore them).
    /// </summary>
    [JsonStringEnumMemberName("unicode")]
    Unicode,

    /// <summary>A line break presses Enter and a tab presses Tab, as a person typing would.</summary>
    [JsonStringEnumMemberName("keys")]
    Keys,

    /// <summary>
    /// A line break presses Shift+Enter, a new line without sending in chat apps (Teams, Slack, Discord, the browser's
    /// text areas); a tab is the Unicode character, as with <see cref="Unicode"/>.
    /// </summary>
    [JsonStringEnumMemberName("shiftEnter")]
    ShiftEnter
}
