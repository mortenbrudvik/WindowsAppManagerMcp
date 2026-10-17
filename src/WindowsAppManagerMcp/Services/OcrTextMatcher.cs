using System.Text;
using System.Text.RegularExpressions;
using WindowsAppManagerMcp.Models;

namespace WindowsAppManagerMcp.Services;

/// <summary>
/// find_text_on_screen's comparison of its text with a line OCR read (roadmap M6.3). contains and exact ignore case (unless
/// case-sensitive) and treat every run of whitespace as one space, since OCR spaces words its own way; between two characters
/// of a script written without spaces (Chinese, Japanese, Thai) they ignore it, since Windows OCR joins those words with
/// spaces too. exact also needs the match to start at a word's start and end at a word's end. A regex runs on the line as
/// OCR read it, ignoring case unless case-sensitive, culture-invariant, with the window titles' 100 ms timeout per match and
/// <see cref="RegexBudget"/> for the whole search.
/// </summary>
public sealed class OcrTextMatcher
{
    private readonly string _text;
    private readonly OcrTextMatch _match;
    private readonly StringComparison _comparison;
    private readonly Regex? _regex;
    private readonly TimeSpan _regexBudget;
    private TimeSpan _regexSpent;

    /// <summary>The most time a regex may take over all the lines of one search, so a slow pattern cannot hold a call.</summary>
    public static readonly TimeSpan RegexBudget = TimeSpan.FromSeconds(2);

    private OcrTextMatcher(string text, OcrTextMatch match, bool caseSensitive, Regex? regex, TimeSpan regexBudget)
    {
        _regexBudget = regexBudget;
        _text = text;
        _match = match;
        _comparison = caseSensitive ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
        _regex = regex;
    }

    /// <summary>
    /// A matcher for <paramref name="text"/>, or null with the reason when there is nothing to match (empty, or only whitespace
    /// for contains and exact) or the regex does not parse. <paramref name="regexBudget"/> replaces <see cref="RegexBudget"/>.
    /// </summary>
    public static OcrTextMatcher? TryCreate(string text, OcrTextMatch match, bool caseSensitive, out string? error,
        TimeSpan? regexBudget = null)
    {
        ArgumentNullException.ThrowIfNull(text);
        error = null;
        if (match == OcrTextMatch.Regex)
        {
            if (text.Length == 0)
            {
                error = "text must not be empty.";
                return null;
            }

            try
            {
                var options = RegexOptions.CultureInvariant | (caseSensitive ? RegexOptions.None : RegexOptions.IgnoreCase);
                return new OcrTextMatcher(text, match, caseSensitive, new Regex(text, options, WindowTitles.RegexTimeout),
                    regexBudget ?? RegexBudget);
            }
            catch (ArgumentException ex)
            {
                error = $"text '{text}' is not a valid .NET regular expression (match regex): {ex.Message}";
                return null;
            }
        }

        var normalized = Normalize(text).Text;
        if (normalized.Length == 0)
        {
            error = "text must contain something besides whitespace.";
            return null;
        }

        return new OcrTextMatcher(normalized, match, caseSensitive, null, TimeSpan.Zero);
    }

    /// <summary>
    /// Where the text matches <paramref name="line"/>, as [start, end) character ranges of <paramref name="line"/>, in order and
    /// not overlapping. <paramref name="words"/> are the [start, end) ranges of the line's words, for exact.
    /// </summary>
    /// <exception cref="RegexMatchTimeoutException">
    /// The regex ran longer than its timeout on the line, or this matcher's regex used up its budget for the whole search.
    /// </exception>
    public IReadOnlyList<(int Start, int End)> Find(string line, IReadOnlyList<(int Start, int End)> words)
    {
        ArgumentNullException.ThrowIfNull(line);
        ArgumentNullException.ThrowIfNull(words);
        if (_regex != null)
        {
            if (_regexSpent >= _regexBudget)
            {
                throw new RegexMatchTimeoutException(line, _regex.ToString(), _regexBudget);
            }

            var started = System.Diagnostics.Stopwatch.GetTimestamp();
            try
            {
                return _regex.Matches(line).Where(m => m.Length > 0).Select(m => (m.Index, m.Index + m.Length)).ToArray();
            }
            finally
            {
                _regexSpent += System.Diagnostics.Stopwatch.GetElapsedTime(started);
            }
        }

        var (normalized, map) = Normalize(line);
        var matches = new List<(int Start, int End)>();
        var from = 0;
        while (from <= normalized.Length - _text.Length && normalized.IndexOf(_text, from, _comparison) is var index and >= 0)
        {
            var start = map[index];
            var end = map[index + _text.Length - 1] + 1;
            if (_match == OcrTextMatch.Contains || (words.Any(w => w.Start == start) && words.Any(w => w.End == end)))
            {
                matches.Add((start, end));
                from = index + _text.Length;
            }
            else
            {
                from = index + 1;
            }
        }

        return matches;
    }

    /// <summary>
    /// <paramref name="text"/> with every run of whitespace as one space and none at the ends (and none between two characters
    /// of a script written without spaces), and for each of its characters the index of the character of
    /// <paramref name="text"/> it came from.
    /// </summary>
    private static (string Text, int[] Map) Normalize(string text)
    {
        var builder = new StringBuilder(text.Length);
        var map = new List<int>(text.Length);
        var pendingSpace = -1;
        for (var i = 0; i < text.Length; i++)
        {
            if (char.IsWhiteSpace(text[i]))
            {
                if (builder.Length > 0 && pendingSpace < 0)
                {
                    pendingSpace = i;
                }

                continue;
            }

            if (pendingSpace >= 0)
            {
                if (!(IsWrittenWithoutSpaces(builder[^1]) && IsWrittenWithoutSpaces(text[i])))
                {
                    builder.Append(' ');
                    map.Add(pendingSpace);
                }

                pendingSpace = -1;
            }

            builder.Append(text[i]);
            map.Add(i);
        }

        return (builder.ToString(), map.ToArray());
    }

    /// <summary>
    /// A character of a script written without spaces between words: CJK ideographs and their punctuation, hiragana,
    /// katakana (also half-width) and Thai. Korean is written with spaces, so Hangul is not one.
    /// </summary>
    private static bool IsWrittenWithoutSpaces(char c) =>
        c is (>= '\u3000' and <= '\u30FF') or (>= '\u3400' and <= '\u4DBF') or (>= '\u4E00' and <= '\u9FFF') or
            (>= '\uF900' and <= '\uFAFF') or (>= '\uFF66' and <= '\uFF9F') or (>= '\u0E00' and <= '\u0E7F');
}
