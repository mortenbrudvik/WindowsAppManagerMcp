using System.Globalization;
using System.Text;

namespace WindowsAppManagerMcp.Services;

/// <summary>The parts of the clipboard's "HTML Format" data (CF_HTML) that get_clipboard returns.</summary>
/// <param name="Html">The fragment that was copied; the whole HTML when the data marks no fragment.</param>
/// <param name="SourceUrl">The page the fragment came from, when the copying app gave one.</param>
public sealed record ClipboardHtmlContent(string Html, string? SourceUrl);

/// <summary>
/// Reads the "HTML Format" clipboard data: UTF-8 bytes that start with a header of "Name:value" lines (Version,
/// StartHTML, EndHTML, StartFragment, EndFragment, optionally SourceURL), where the Start/End values are byte offsets
/// into the whole data. Browsers and Office write it when they copy.
/// </summary>
public static class ClipboardHtml
{
    /// <summary>
    /// The fragment (StartFragment to EndFragment) and the source URL. Without valid fragment offsets, the HTML
    /// (StartHTML to EndHTML); without those either, everything after the header. Offsets past the end of the data are
    /// cut to it, so data that was cut short still gives what it holds.
    /// </summary>
    public static ClipboardHtmlContent Parse(byte[] data)
    {
        ArgumentNullException.ThrowIfNull(data);

        var length = Array.IndexOf(data, (byte)0) is var nul and >= 0 ? nul : data.Length;
        var header = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var position = 0;
        var headerEnd = 0;
        while (position < length)
        {
            var lineEnd = position;
            while (lineEnd < length && data[lineEnd] != (byte)'\n' && data[lineEnd] != (byte)'\r')
            {
                lineEnd++;
            }

            var line = Encoding.UTF8.GetString(data, position, lineEnd - position);
            var colon = line.IndexOf(':');
            if (colon <= 0 || line.StartsWith('<'))
            {
                break;
            }

            header[line[..colon].Trim()] = line[(colon + 1)..].Trim();
            while (lineEnd < length && (data[lineEnd] == (byte)'\n' || data[lineEnd] == (byte)'\r'))
            {
                lineEnd++;
            }

            position = lineEnd;
            headerEnd = position;
        }

        var sourceUrl = header.TryGetValue("SourceURL", out var url) && url.Length > 0 ? url : null;
        var html = Slice(data, length, header, "StartFragment", "EndFragment")
                   ?? Slice(data, length, header, "StartHTML", "EndHTML")
                   ?? Encoding.UTF8.GetString(data, headerEnd, length - headerEnd);
        return new ClipboardHtmlContent(html, sourceUrl);
    }

    private static string? Slice(byte[] data, int length, Dictionary<string, string> header, string startName, string endName)
    {
        if (!header.TryGetValue(startName, out var startText) ||
            !int.TryParse(startText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var start) ||
            start < 0 || start >= length)
        {
            return null;
        }

        var end = header.TryGetValue(endName, out var endText) &&
                  int.TryParse(endText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) && parsed >= start
            ? Math.Min(parsed, length)
            : length;
        return Encoding.UTF8.GetString(data, start, end - start);
    }
}
