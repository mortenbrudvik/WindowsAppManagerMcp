using System.Text.Json.Serialization;

namespace WindowsAppManagerMcp.Models;

/// <summary>What read_screen_text returns for each line (roadmap M6.3).</summary>
[ToolEnum(WindowsAppManagerMcp.Infrastructure.ToolErrorReporting.InvalidArgumentsErrorCode, "detail", NullMeans = "lines")]
public enum OcrDetail
{
    /// <summary>Each line's text and box.</summary>
    [JsonStringEnumMemberName("lines")]
    Lines,

    /// <summary>Each line's text and box, and its words with their boxes.</summary>
    [JsonStringEnumMemberName("words")]
    Words
}

/// <summary>How find_text_on_screen compares the text OCR read with its <c>text</c> argument (roadmap M6.3).</summary>
[ToolEnum(WindowsAppManagerMcp.Infrastructure.ToolErrorReporting.InvalidArgumentsErrorCode, "match", NullMeans = "contains")]
public enum OcrTextMatch
{
    /// <summary>A part of a line, anywhere, even inside a word.</summary>
    [JsonStringEnumMemberName("contains")]
    Contains,

    /// <summary>Whole words: one word of a line, or a run of its words.</summary>
    [JsonStringEnumMemberName("exact")]
    Exact,

    /// <summary>A .NET regular expression that matches somewhere in a line (100 ms per line at most).</summary>
    [JsonStringEnumMemberName("regex")]
    Regex
}

/// <summary>A line OCR read: its text and its box in screen pixels, and with detail 'words' its words.</summary>
public sealed record OcrLineDto(string Text, BoundsDto Bounds, IReadOnlyList<OcrWordDto>? Words = null);

/// <summary>A word OCR read and its box in screen pixels.</summary>
public sealed record OcrWordDto(string Text, BoundsDto Bounds);

/// <summary>
/// A match of find_text_on_screen: the text matched, the line it is in, its box in screen pixels, the screen point at its
/// centre, and the same point in pixels of the registered capture (for mouse_click with the result's captureId).
/// </summary>
public sealed record OcrMatchDto(string Text, string Line, BoundsDto Bounds, ScreenPoint ClickPoint, ScreenPoint ImagePoint);

/// <summary>
/// What read_screen_text read (roadmap M6.3). On failure Success is false with Error and ErrorCode (a capture code, OcrUnavailable
/// or OcrLanguageNotInstalled, with the installed languages).
/// </summary>
/// <param name="Source">Always 'ocr': the text was read from pixels, not from UI Automation.</param>
/// <param name="Language">The OCR language the text was read in.</param>
/// <param name="CaptureId">The capture the boxes belong to, registered 1:1 from <paramref name="CapturedRegion"/>'s corner.</param>
/// <param name="CapturedRegion">The screen rectangle read, in physical virtual-screen pixels.</param>
/// <param name="OcrScale">How much the pixels were enlarged before OCR (2 for small text below 150% scale, else 1).</param>
/// <param name="LineCount">How many lines OCR read, also those left out by maxLines.</param>
/// <param name="Lines">The lines in reading order, at most maxLines.</param>
/// <param name="Truncated">True when maxLines left lines out.</param>
/// <param name="WindowHandle">The window read, for a window target.</param>
/// <param name="TextAngle">The text's angle in degrees when OCR found it skewed.</param>
/// <param name="MaskedWindows">How many windows the user's policy denies were blacked out before OCR; left out when none.</param>
/// <param name="Warnings">The capture's warnings, each starting with a code (ImageBlank, WindowNotResponding, OcrTiled, ...).</param>
/// <param name="InstalledLanguages">The installed OCR languages, with OcrLanguageNotInstalled and OcrUnavailable.</param>
public sealed record ScreenTextResult(
    bool Success,
    string Source,
    string? Language,
    string? CaptureId,
    CapturedRegion? CapturedRegion,
    int? OcrScale,
    int LineCount,
    IReadOnlyList<OcrLineDto>? Lines,
    bool Truncated,
    long? WindowHandle = null,
    double? TextAngle = null,
    int? MaskedWindows = null,
    IReadOnlyList<string>? Warnings = null,
    string? Error = null,
    string? ErrorCode = null,
    IReadOnlyList<string>? InstalledLanguages = null);

/// <summary>
/// What find_text_on_screen found (roadmap M6.3): as <see cref="ScreenTextResult"/>, with the matches instead of the lines.
/// No match is a success with an empty list and a hint.
/// </summary>
/// <param name="MatchCount">How many matches OCR's text has, also those left out by maxResults.</param>
/// <param name="Matches">The matches, top to bottom and then left to right, at most maxResults.</param>
/// <param name="Truncated">True when maxResults left matches out.</param>
/// <param name="Hint">What to try when nothing matched.</param>
public sealed record TextFindResult(
    bool Success,
    string Source,
    string? Language,
    string? CaptureId,
    CapturedRegion? CapturedRegion,
    int? OcrScale,
    int MatchCount,
    IReadOnlyList<OcrMatchDto>? Matches,
    bool Truncated,
    long? WindowHandle = null,
    int? MaskedWindows = null,
    IReadOnlyList<string>? Warnings = null,
    string? Hint = null,
    string? Error = null,
    string? ErrorCode = null,
    IReadOnlyList<string>? InstalledLanguages = null);
