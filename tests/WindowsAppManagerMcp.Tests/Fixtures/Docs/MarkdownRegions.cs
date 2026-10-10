using System.Text.RegularExpressions;

namespace WindowsAppManagerMcp.Tests.Fixtures;

/// <summary>A generated region of a Markdown file: the lines between its begin and end markers.</summary>
/// <param name="Id">The region's id, for example "tool-table" or "params:find_windows".</param>
/// <param name="BeginLine">The 0-based line of the begin marker.</param>
/// <param name="EndLine">The 0-based line of the end marker.</param>
public sealed record MarkdownRegion(string Id, int BeginLine, int EndLine);

/// <summary>
/// Finds and rewrites the generated regions of a Markdown file. A region is the lines between
/// <c>&lt;!-- tools:begin ID --&gt;</c> (optionally followed by a note before the closing <c>--&gt;</c>) and
/// <c>&lt;!-- tools:end ID --&gt;</c>, each on a line of its own. The markers are HTML comments, so they do not
/// show when the Markdown is rendered. Regions do not nest, and an id appears once per file.
/// </summary>
public static partial class MarkdownRegions
{
    [GeneratedRegex(@"^<!-- tools:begin (?<id>\S+)(?: [^\r\n]*)? -->$")]
    private static partial Regex BeginMarker();

    [GeneratedRegex(@"^<!-- tools:end (?<id>\S+) -->$")]
    private static partial Regex EndMarker();

    /// <summary>The regions of <paramref name="text"/> (LF line ends), in file order.</summary>
    /// <exception cref="FormatException">A marker is unmatched, nested or repeated.</exception>
    public static IReadOnlyList<MarkdownRegion> Find(string text)
    {
        var lines = text.Split('\n');
        var regions = new List<MarkdownRegion>();
        string? openId = null;
        var openLine = -1;

        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i].TrimEnd('\r');
            if (BeginMarker().Match(line) is { Success: true } begin)
            {
                if (openId != null)
                {
                    throw new FormatException($"Line {i + 1}: region '{begin.Groups["id"].Value}' begins inside region '{openId}' (line {openLine + 1}).");
                }

                openId = begin.Groups["id"].Value;
                openLine = i;
            }
            else if (EndMarker().Match(line) is { Success: true } end)
            {
                var id = end.Groups["id"].Value;
                if (openId != id)
                {
                    throw new FormatException(openId == null
                        ? $"Line {i + 1}: region '{id}' ends without a begin marker."
                        : $"Line {i + 1}: region '{id}' ends inside region '{openId}' (line {openLine + 1}).");
                }

                if (regions.Any(r => r.Id == id))
                {
                    throw new FormatException($"Line {openLine + 1}: region '{id}' appears more than once.");
                }

                regions.Add(new MarkdownRegion(id, openLine, i));
                openId = null;
            }
        }

        if (openId != null)
        {
            throw new FormatException($"Line {openLine + 1}: region '{openId}' has no end marker.");
        }

        return regions;
    }

    /// <summary>The content of a region (without its markers), LF line ends, no trailing line end.</summary>
    public static string Content(string text, MarkdownRegion region)
    {
        var lines = text.Split('\n');
        return string.Join('\n', lines[(region.BeginLine + 1)..region.EndLine]);
    }

    /// <summary>
    /// <paramref name="text"/> with the content of every region replaced by <paramref name="generate"/>(id, current
    /// content). The markers and everything outside the regions stay as they are. Content is passed and returned
    /// with LF line ends and no trailing line end; an empty string leaves the region empty.
    /// </summary>
    public static string Rewrite(string text, Func<string, string, string> generate)
    {
        var lines = text.Split('\n');
        var output = new List<string>(lines.Length);
        var next = 0;
        foreach (var region in Find(text))
        {
            output.AddRange(lines[next..(region.BeginLine + 1)]);
            var current = string.Join('\n', lines[(region.BeginLine + 1)..region.EndLine]);
            var generated = generate(region.Id, current);
            if (generated.Length > 0)
            {
                output.AddRange(generated.Split('\n'));
            }

            next = region.EndLine;
        }

        output.AddRange(lines[next..]);
        return string.Join('\n', output);
    }
}
