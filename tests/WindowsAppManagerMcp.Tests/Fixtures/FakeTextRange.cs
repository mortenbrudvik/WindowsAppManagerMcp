namespace WindowsAppManagerMcp.Tests.Fixtures;

/// <summary>
/// A provider's text range for <see cref="UiaTextPageReader"/>, over a document of the provider's characters (each one or
/// more UTF-16 characters). It starts as the whole document, as DocumentRange does.
/// <see cref="RereadsLineBreakBeforeStart"/> models Chromium (Edge 155 and Chrome 155, read on 2026-10-15 in a 1000-line
/// &lt;pre&gt;): a range whose start sits just after a line break reads that line break again, in front of its own text,
/// though moving its endpoints counts every character once (the range from character 100, just after the first line's
/// "\n", to 200 reads "\n" and then the 100 characters of the second line).
/// </summary>
public sealed class FakeTextRange(IReadOnlyList<string> characters) : UiaTextPageReader
{
    private int _start;
    private int _end = characters.Count;

    /// <summary>A range that starts just after a "\n" character reads it again first, as Chromium's ranges do.</summary>
    public bool RereadsLineBreakBeforeStart { get; init; }

    /// <summary>The maxLength of every GetText call.</summary>
    public List<int> TextLimits { get; } = [];

    /// <summary>One provider character per UTF-16 character of <paramref name="text"/>.</summary>
    public static List<string> OneEach(string text) => text.Select(c => c.ToString()).ToList();

    protected override int MoveStart(int count)
    {
        var moved = Math.Min(count, characters.Count - _start);
        _start += moved;
        _end = Math.Max(_end, _start);
        return moved;
    }

    protected override void Collapse() => _end = _start;

    protected override int MoveEnd(int count)
    {
        var moved = Math.Min(count, characters.Count - _end);
        _end += moved;
        return moved;
    }

    protected override string GetText(int maxLength)
    {
        TextLimits.Add(maxLength);
        var text = string.Concat(characters.Skip(_start).Take(_end - _start));
        if (RereadsLineBreakBeforeStart && _start > 0 && _end > _start && characters[_start - 1] == "\n")
        {
            text = "\n" + text;
        }

        return maxLength >= 0 && text.Length > maxLength ? text[..maxLength] : text;
    }

    protected override bool HasCharacterAfterEnd() => _end < characters.Count;
}
