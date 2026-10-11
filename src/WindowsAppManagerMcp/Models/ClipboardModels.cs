using System.Text.Json.Serialization;

namespace WindowsAppManagerMcp.Models;

/// <summary>What get_clipboard reads (roadmap M2.16).</summary>
[ToolEnum(nameof(ClipboardErrorCode.InvalidFormat), "clipboard format", NullMeans = "text")]
public enum ClipboardFormat
{
    /// <summary>Plain text (CF_UNICODETEXT).</summary>
    [JsonStringEnumMemberName("text")]
    Text,

    /// <summary>The HTML fragment a browser or Office app copied ("HTML Format"), with the page it came from.</summary>
    [JsonStringEnumMemberName("html")]
    Html,

    /// <summary>The paths of copied files (CF_HDROP), as File Explorer copies them.</summary>
    [JsonStringEnumMemberName("files")]
    Files
}

/// <summary>Who put the clipboard's content there, as far as the server can tell.</summary>
public enum ClipboardContentOrigin
{
    /// <summary>set_clipboard of this server, and nothing has changed the clipboard since.</summary>
    SetClipboard,

    /// <summary>
    /// A copy the agent's own input started: the clipboard changed within a few seconds after send_keys sent a copy
    /// chord (Ctrl+C, Ctrl+X, Ctrl+Insert, Shift+Delete) or a click pressed the left button (a Copy menu item).
    /// </summary>
    AgentInput,

    /// <summary>
    /// The user or another app: read only because the user allowed it (WAM_ALLOW_USER_CLIPBOARD, or a yes to the
    /// question).
    /// </summary>
    User
}

/// <summary>
/// The private-content markers Windows clipboard formats define, which password managers and other apps set to keep
/// what they copy out of clipboard history and clipboard viewers.
/// </summary>
[Flags]
public enum ClipboardPrivacyMarkers
{
    None = 0,

    /// <summary>"ExcludeClipboardContentFromMonitorProcessing" is on the clipboard.</summary>
    ExcludeFromMonitorProcessing = 1,

    /// <summary>"CanIncludeInClipboardHistory" is on the clipboard with the value 0 (or cannot be read).</summary>
    NotForClipboardHistory = 2,

    /// <summary>"Clipboard Viewer Ignore" is on the clipboard.</summary>
    ClipboardViewerIgnore = 4
}

/// <summary>Result of get_clipboard.</summary>
/// <param name="Success">True when content was returned.</param>
/// <param name="Format">The format asked for: text, html or files.</param>
/// <param name="Text">text: the text; html: the HTML fragment. Cut to maxLength characters.</param>
/// <param name="Files">files: the paths, until their characters reach maxLength.</param>
/// <param name="Length">The full length in characters (text, html) or number of paths (files), before any cut.</param>
/// <param name="Truncated">True when maxLength cut the content.</param>
/// <param name="Origin">Who put the content there.</param>
/// <param name="SourceUrl">html: the page the fragment was copied from, when the copying app gave one.</param>
/// <param name="AvailableFormats">With FormatNotAvailable: which of text, html and files the clipboard holds.</param>
/// <param name="Error">Why nothing was returned, and what to do; null on success.</param>
/// <param name="ErrorCode">None on success.</param>
public sealed record GetClipboardResult(
    bool Success,
    ClipboardFormat Format,
    string? Text = null,
    IReadOnlyList<string>? Files = null,
    int? Length = null,
    bool? Truncated = null,
    ClipboardContentOrigin? Origin = null,
    string? SourceUrl = null,
    IReadOnlyList<ClipboardFormat>? AvailableFormats = null,
    string? Error = null,
    ClipboardErrorCode ErrorCode = ClipboardErrorCode.None);

/// <summary>Result of set_clipboard.</summary>
/// <param name="Success">True when the text is on the clipboard.</param>
/// <param name="Length">The number of characters put on the clipboard.</param>
/// <param name="ExcludedFromHistory">
/// True when the text carries the formats that keep it out of Win+V clipboard history and cloud clipboard sync.
/// </param>
/// <param name="Error">Why the text is not on the clipboard; null on success.</param>
/// <param name="ErrorCode">None on success.</param>
public sealed record SetClipboardResult(
    bool Success,
    int Length,
    bool ExcludedFromHistory = false,
    string? Error = null,
    ClipboardErrorCode ErrorCode = ClipboardErrorCode.None);
