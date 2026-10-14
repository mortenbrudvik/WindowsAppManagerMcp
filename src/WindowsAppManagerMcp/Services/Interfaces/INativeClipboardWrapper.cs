using WindowsAppManagerMcp.Models;

namespace WindowsAppManagerMcp.Services.Interfaces;

/// <summary>
/// What one read of the clipboard found, while the server held it open: the sequence number, the privacy markers, which
/// formats it holds, and the content of the format asked for.
/// </summary>
/// <param name="Opened">False when the clipboard could not be opened (another app kept it open); nothing else is set.</param>
/// <param name="SequenceNumber">GetClipboardSequenceNumber while the clipboard was open, so it belongs to this content.</param>
/// <param name="Markers">The private-content markers on the clipboard.</param>
/// <param name="HasText">The clipboard holds text (CF_UNICODETEXT, or a text format Windows converts to it).</param>
/// <param name="HasHtml">The clipboard holds "HTML Format".</param>
/// <param name="HasFiles">The clipboard holds copied files (CF_HDROP).</param>
/// <param name="Text">text: the text up to its terminating null, at most the asked number of characters.</param>
/// <param name="TextLength">text: the full length of the text in characters.</param>
/// <param name="Html">html: the raw "HTML Format" bytes (UTF-8, with the CF_HTML header), at most <see cref="MaxHtmlBytes"/>.</param>
/// <param name="Files">files: the paths, at most the asked number.</param>
/// <param name="FileCount">files: how many paths there are.</param>
/// <param name="Error">Why the content of a format the clipboard holds could not be read (with the Win32 error); null when it was.</param>
/// <param name="HasOtherFormats">
/// The clipboard holds a format besides plain text: HTML, files, an image, rich text or an application's own data. The
/// text formats Windows makes from text, the privacy markers and OLE's bookkeeping formats do not count. Only a clipboard
/// without it can be put back after type_text's paste (roadmap M4.4).
/// </param>
public sealed record ClipboardSnapshot(
    bool Opened,
    uint SequenceNumber = 0,
    ClipboardPrivacyMarkers Markers = ClipboardPrivacyMarkers.None,
    bool HasText = false,
    bool HasHtml = false,
    bool HasFiles = false,
    string? Text = null,
    int TextLength = 0,
    byte[]? Html = null,
    IReadOnlyList<string>? Files = null,
    int FileCount = 0,
    string? Error = null,
    bool HasOtherFormats = false)
{
    /// <summary>
    /// The most "HTML Format" bytes a read takes. The fragment's offsets point into the whole data, and an Office copy
    /// puts long style sheets before the fragment, so the data is read whole up to this size and cut afterwards.
    /// </summary>
    public const int MaxHtmlBytes = 16 * 1024 * 1024;

    /// <summary>The clipboard could not be opened.</summary>
    public static ClipboardSnapshot NotOpened { get; } = new(false);

    /// <summary>True when any privacy marker is on the clipboard.</summary>
    public bool IsPrivate => Markers != ClipboardPrivacyMarkers.None;
}

/// <summary>What setting the clipboard did.</summary>
/// <param name="Opened">False when the clipboard could not be opened; nothing was changed.</param>
/// <param name="Success">True when the text is on the clipboard.</param>
/// <param name="SequenceNumber">
/// GetClipboardSequenceNumber right after the clipboard was closed again, while the server's window still owned it; 0
/// when another application had already replaced the text, so the number is not the server's.
/// </param>
/// <param name="ExcludedFromHistory">True when the formats that keep the text out of clipboard history are on it too.</param>
/// <param name="Error">Why the text is not on the clipboard (with the Win32 error); null on success.</param>
/// <param name="Changed">
/// <see cref="INativeClipboardWrapper.ReplaceText"/>: the clipboard no longer had the expected sequence number, so nothing
/// was changed.
/// </param>
public sealed record ClipboardWriteOutcome(bool Opened, bool Success, uint SequenceNumber = 0, bool ExcludedFromHistory = false, string? Error = null,
    bool Changed = false);

/// <summary>
/// The Windows clipboard (roadmap M2.16): the one way the server reads or replaces it. Each read or write opens the
/// clipboard on a dedicated STA thread with a message-only window of the server as the owner, retries while another app
/// holds it open, and closes it again before returning. Tests replace it with a fake: the clipboard is the user's, shared
/// with every app, so a test must never read or replace it.
/// </summary>
public interface INativeClipboardWrapper
{
    /// <summary>
    /// GetClipboardSequenceNumber: a number that changes whenever the clipboard's content changes; 0 when it cannot be
    /// read. It does not open the clipboard.
    /// </summary>
    uint GetSequenceNumber();

    /// <summary>
    /// The process of the window that owns the clipboard (GetClipboardOwner): the application that last emptied it to put
    /// its copy there. 0 when the clipboard has no owner (an application emptied it without a window) or it cannot be
    /// read. It does not open the clipboard.
    /// </summary>
    int GetOwnerProcessId();

    /// <summary>
    /// Opens the clipboard and reads its sequence number, its privacy markers and which formats it holds. Then, unless
    /// a privacy marker is on it and its sequence number (read while it is open) is not
    /// <paramref name="privateContentSequence"/>, reads the content of <paramref name="format"/>: at most
    /// <paramref name="maxCharacters"/> characters of text, the "HTML Format" data up to
    /// <see cref="ClipboardSnapshot.MaxHtmlBytes"/>, or at most <paramref name="maxCharacters"/> paths.
    /// </summary>
    /// <param name="format">The format to read.</param>
    /// <param name="maxCharacters">The most characters (text) or paths (files) read.</param>
    /// <param name="privateContentSequence">
    /// The sequence number of the one content with privacy markers that may be read (the text set_clipboard put there);
    /// 0: none.
    /// </param>
    ClipboardSnapshot Read(ClipboardFormat format, int maxCharacters, uint privateContentSequence);

    /// <summary>
    /// Replaces the clipboard's content with <paramref name="text"/> (CF_UNICODETEXT) plus the formats that keep it out of
    /// clipboard history and cloud sync: ExcludeClipboardContentFromMonitorProcessing, CanIncludeInClipboardHistory = 0
    /// and CanUploadToCloudClipboard = 0.
    /// </summary>
    ClipboardWriteOutcome WriteText(string text);

    /// <summary>
    /// As <see cref="WriteText"/>, but only while the clipboard's sequence number, read while the server holds it open, is
    /// still <paramref name="expectedSequence"/>; otherwise nothing is changed (<see cref="ClipboardWriteOutcome.Changed"/>).
    /// With <paramref name="text"/> null it empties the clipboard instead. type_text's paste (roadmap M4.4) lends the
    /// clipboard with it and gives it back, never over content another application put there meanwhile.
    /// </summary>
    ClipboardWriteOutcome ReplaceText(string? text, uint expectedSequence);
}
