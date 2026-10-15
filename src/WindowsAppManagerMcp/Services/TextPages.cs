using WindowsAppManagerMcp.Infrastructure;
using WindowsAppManagerMcp.Services.Interfaces;

namespace WindowsAppManagerMcp.Services;

/// <summary>
/// The paging contract read_element_text (roadmap M3.9) and get_browser_content (roadmap M5.6) share: a page holds at most
/// maxLength characters and at most a number of bytes of JSON, it never ends between the two halves of a surrogate pair,
/// and the next page starts just past its text, so paging skips nothing.
/// </summary>
public static class TextPages
{
    /// <summary>
    /// How many times a Text pattern page that does not fit one result is read again over fewer characters before it is cut.
    /// </summary>
    public const int FitTries = 3;

    /// <summary>
    /// How many characters from the start of <paramref name="text"/> fit both limits: at most <paramref name="maxChars"/>
    /// characters that take at most <paramref name="maxBytes"/> bytes in the result's JSON (<see cref="ToolJson.StringBytes"/>),
    /// never ending between the two halves of a surrogate pair (unless that would leave nothing).
    /// </summary>
    public static int FittingLength(ReadOnlySpan<char> text, int maxChars, int maxBytes)
    {
        var end = Math.Min(text.Length, maxChars);
        var bytes = 0;
        for (var i = 0; i < end; i++)
        {
            bytes += ToolJson.CharBytes(text[i]);
            if (bytes > maxBytes)
            {
                end = i;
                break;
            }
        }

        if (end < text.Length && end > 1 && char.IsHighSurrogate(text[end - 1]))
        {
            end--;
        }

        return end;
    }

    /// <summary>
    /// A Text pattern page made to fit one result: at most <paramref name="maxLength"/> characters and
    /// <paramref name="maxBytes"/> bytes of JSON. A page whose text does not fit is read again over fewer of the provider's
    /// characters (<paramref name="readFewer"/>), in proportion to the part that fits, so that its end (Start + Length, the
    /// next page's offset) is still just past its text and nothing is skipped. Only when that does not work (one of the
    /// provider's characters is longer than the page, or the document changes under the read) is the text cut, and the
    /// page says so (<see cref="UiaTextPage.Cut"/>: the rest of its range is skipped).
    /// </summary>
    /// <param name="readFewer">Reads the page again at the same offset over the given number of the provider's characters;
    /// null when it cannot.</param>
    public static UiaTextPage Fit(UiaTextPage page, int maxLength, int maxBytes, Func<int, UiaTextPage?> readFewer)
    {
        for (var tries = 0; tries < FitTries; tries++)
        {
            var fits = FittingLength(page.Text, maxLength, maxBytes);
            if (fits == page.Text.Length || page.Length <= 1)
            {
                break;
            }

            var fewer = (int)Math.Clamp((long)page.Length * fits / page.Text.Length, 1, page.Length - 1);
            if (readFewer(fewer) is not { } shorter)
            {
                break;
            }

            page = shorter;
        }

        var end = FittingLength(page.Text, maxLength, maxBytes);
        return end == page.Text.Length ? page : page with { Text = page.Text[..end], Cut = true };
    }
}
