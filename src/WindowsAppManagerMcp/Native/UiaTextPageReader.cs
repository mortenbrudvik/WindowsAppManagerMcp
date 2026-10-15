using WindowsAppManagerMcp.Services.Interfaces;

namespace WindowsAppManagerMcp.Native;

/// <summary>
/// Reads a page of a document's text through a text range of its provider (<see cref="IUiaTextPattern.GetTextPage"/>,
/// roadmap M3.9), the same way in both engines, which each give it their own range (a new DocumentRange per page). The
/// range's start is moved to the offset and its end maxLength characters on, in the provider's character unit, and only that
/// range's text is read, never more than twice maxLength UTF-16 characters of it, so a page never costs the document.
/// </summary>
/// <remarks>
/// A provider's character can be longer than one UTF-16 character: a line break counted as one, a grapheme cluster, or the
/// whole document from a provider that has no character unit and falls back to a larger one. When the range's text is longer
/// than maxLength, the range is made to span fewer characters (in proportion when its whole text was read, else half as
/// many) and read again, at most <see cref="MaxShrinks"/> times, so the page's text fits maxLength and its end (Start +
/// Length, where the next page starts) is just past its text: nothing is skipped. Only when one of the provider's characters
/// is longer than maxLength is its text cut, without splitting a surrogate pair, and the page says so
/// (<see cref="UiaTextPage.Cut"/>). A page after the first is read from the character before it, whose own text is then left out (the lead-in,
/// at most <see cref="MaxLeadInLength"/> UTF-16 characters): Chromium's range that starts just after a line break reads
/// that line break again (Edge 155 and Chrome 155, 2026-10-15), and a range that starts before it reads it once. Moving the endpoints of the client's own range only reads: neither the selection nor the
/// document changes.
/// </remarks>
public abstract class UiaTextPageReader
{
    /// <summary>How many times a page whose text is longer than maxLength is made to span fewer characters.</summary>
    public const int MaxShrinks = 16;

    /// <summary>The most UTF-16 characters the character before a page may read as.</summary>
    public const int MaxLeadInLength = 64;

    /// <summary>Moves the range's start forward by up to <paramref name="count"/> of the provider's characters.</summary>
    /// <returns>How many it moved: fewer at the end of the document.</returns>
    protected abstract int MoveStart(int count);

    /// <summary>Moves the range's end back to its start.</summary>
    protected abstract void Collapse();

    /// <summary>Moves the range's end forward by up to <paramref name="count"/> of the provider's characters.</summary>
    /// <returns>How many it moved: fewer at the end of the document.</returns>
    protected abstract int MoveEnd(int count);

    /// <summary>The range's text, at most <paramref name="maxLength"/> UTF-16 characters of it.</summary>
    protected abstract string GetText(int maxLength);

    /// <summary>True when a character follows the range's end (moves the end of a copy of the range, not of the range).</summary>
    protected abstract bool HasCharacterAfterEnd();

    /// <summary>The page at <paramref name="offset"/>; see <see cref="IUiaTextPattern.GetTextPage"/>.</summary>
    public UiaTextPage Read(int offset, int maxLength)
    {
        maxLength = Math.Clamp(maxLength, 1, int.MaxValue / 2);
        var textLimit = 2 * maxLength;

        // A page after the first is read from one character before it, and that character's text (the lead-in, read the same
        // way) is left out: Chromium's range that starts just after a line break reads that line break again, so a page that
        // started at a line's start repeated the last page's line break and, made to fit, ended a character short
        var start = 0;
        string? leadIn = null;
        if (offset > 0)
        {
            start = MoveStart(offset - 1);
            Collapse();
            if (start == offset - 1 && MoveEnd(1) == 1)
            {
                start = offset;
                if (GetText(MaxLeadInLength + 1) is { Length: > 0 and <= MaxLeadInLength } text)
                {
                    leadIn = text;
                }
                else
                {
                    // A character too long to leave out reliably: read from the page's start
                    MoveStart(1);
                }
            }
        }

        var (length, pageText) = Span(maxLength, ref leadIn, textLimit);

        var shrunk = false;
        for (var tries = 0; pageText.Length > maxLength && length > 1 && tries < MaxShrinks; tries++)
        {
            // The provider's characters are longer than one UTF-16 character here: span fewer of them
            var fewer = pageText.Length < textLimit ? (int)((long)length * maxLength / pageText.Length) : length / 2;
            (length, pageText) = Span(Math.Clamp(fewer, 1, length - 1), ref leadIn, textLimit);
            shrunk = true;
        }

        var cut = false;
        if (pageText.Length > maxLength)
        {
            // One character is longer than the page: return its start, and the next page starts after it
            var end = maxLength > 1 && char.IsHighSurrogate(pageText[maxLength - 1]) ? maxLength - 1 : maxLength;
            pageText = pageText[..end];
            cut = true;
        }

        // A full page: one more character after it tells whether text follows. A page made shorter is followed by what it
        // no longer spans
        var hasMore = shrunk || (length >= maxLength && HasCharacterAfterEnd());
        return new UiaTextPage(pageText, start, length, hasMore, cut);
    }

    /// <summary>
    /// Spans <paramref name="count"/> of the provider's characters from the page's start and reads their text, at most
    /// <paramref name="textLimit"/> UTF-16 characters of it. With a lead-in the range starts one character before the page
    /// and the lead-in's text is left out of what it reads; a range whose text does not start with it (a provider that reads
    /// the character differently in a longer range) is read again from the page's start, without the lead-in, which is then
    /// dropped for the rest of the page.
    /// </summary>
    private (int Length, string Text) Span(int count, ref string? leadIn, int textLimit)
    {
        Collapse();
        if (leadIn != null)
        {
            MoveEnd(1);
            var length = MoveEnd(count);
            var text = length > 0 ? GetText(textLimit + leadIn.Length) : string.Empty;
            if (length == 0 || text.StartsWith(leadIn, StringComparison.Ordinal))
            {
                return (length, length > 0 ? text[leadIn.Length..] : string.Empty);
            }

            leadIn = null;
            MoveStart(1);
            Collapse();
        }

        var spanned = MoveEnd(count);
        return (spanned, spanned > 0 ? GetText(textLimit) : string.Empty);
    }
}
