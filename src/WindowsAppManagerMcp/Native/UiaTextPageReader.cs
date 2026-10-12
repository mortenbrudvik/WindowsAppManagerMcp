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
/// (<see cref="UiaTextPage.Cut"/>). Moving the endpoints of the client's own range only reads: neither the selection nor the
/// document changes.
/// </remarks>
public abstract class UiaTextPageReader
{
    /// <summary>How many times a page whose text is longer than maxLength is made to span fewer characters.</summary>
    public const int MaxShrinks = 16;

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
        var start = offset > 0 ? MoveStart(offset) : 0;
        Collapse();
        var length = MoveEnd(maxLength);
        var text = length > 0 ? GetText(textLimit) : string.Empty;

        var shrunk = false;
        for (var tries = 0; text.Length > maxLength && length > 1 && tries < MaxShrinks; tries++)
        {
            // The provider's characters are longer than one UTF-16 character here: span fewer of them
            var fewer = text.Length < textLimit ? (int)((long)length * maxLength / text.Length) : length / 2;
            Collapse();
            length = MoveEnd(Math.Clamp(fewer, 1, length - 1));
            text = length > 0 ? GetText(textLimit) : string.Empty;
            shrunk = true;
        }

        var cut = false;
        if (text.Length > maxLength)
        {
            // One character is longer than the page: return its start, and the next page starts after it
            var end = maxLength > 1 && char.IsHighSurrogate(text[maxLength - 1]) ? maxLength - 1 : maxLength;
            text = text[..end];
            cut = true;
        }

        // A full page: one more character after it tells whether text follows. A page made shorter is followed by what it
        // no longer spans
        var hasMore = shrunk || (length >= maxLength && HasCharacterAfterEnd());
        return new UiaTextPage(text, start, length, hasMore, cut);
    }
}
