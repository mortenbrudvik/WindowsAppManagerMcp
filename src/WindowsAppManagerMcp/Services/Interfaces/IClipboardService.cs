using WindowsAppManagerMcp.Models;

namespace WindowsAppManagerMcp.Services.Interfaces;

/// <summary>The user's yes to reading the clipboard as it was when they were asked.</summary>
/// <param name="SequenceNumber">The clipboard's sequence number when the user was asked; other content is refused.</param>
public sealed record ClipboardConsent(uint SequenceNumber);

/// <summary>
/// get_clipboard and set_clipboard (roadmap M2.16): the clipboard with a privacy guard. A read returns only content the
/// agent put there (set_clipboard, or its own copy: see <see cref="IClipboardOrigin"/>), unless the user allowed more
/// (<see cref="ClipboardOptions.AllowUserClipboard"/>, or a <see cref="ClipboardConsent"/>), and never content marked
/// private, which password managers mark. Nothing here asks the user; the tool does.
/// </summary>
public interface IClipboardService
{
    /// <summary>True when the user's setting lets get_clipboard return the user's clipboard without asking.</summary>
    bool AllowsUserClipboard { get; }

    /// <summary>The clipboard's sequence number now (0 when it cannot be read), to pin what the user is asked about.</summary>
    uint SequenceNumber();

    /// <summary>
    /// Reads <paramref name="format"/>, cut to <paramref name="maxLength"/> characters (files: the paths' characters).
    /// When the agent's copy keys were sent moments ago and the clipboard has not changed yet, it waits up to a second
    /// for the application to copy. Refuses (no content) with NothingCopied, ClipboardNotFromAgent, ClipboardPrivate,
    /// FormatNotAvailable, ClipboardBusy or ClipboardFailed.
    /// </summary>
    /// <param name="format">text, html or files.</param>
    /// <param name="maxLength">The most characters returned, at least 1.</param>
    /// <param name="consent">The user's yes to reading their clipboard as it was when asked; null when not asked.</param>
    /// <param name="cancellationToken">Ends the wait for a copy.</param>
    Task<GetClipboardResult> ReadAsync(ClipboardFormat format, int maxLength, ClipboardConsent? consent, CancellationToken cancellationToken);

    /// <summary>
    /// Puts <paramref name="text"/> on the clipboard, kept out of clipboard history and cloud sync, and remembers it as
    /// the agent's. Fails with ClipboardBusy or ClipboardFailed.
    /// </summary>
    SetClipboardResult SetText(string text);

    /// <summary>
    /// The user did not allow reading the clipboard's content with <paramref name="sequenceNumber"/>: get_clipboard does
    /// not ask about that content again (a new copy is new content, and may be asked about).
    /// </summary>
    void UserDeclined(uint sequenceNumber);

    /// <summary>True when the user did not allow reading the content with <paramref name="sequenceNumber"/>.</summary>
    bool WasDeclined(uint sequenceNumber);
}
