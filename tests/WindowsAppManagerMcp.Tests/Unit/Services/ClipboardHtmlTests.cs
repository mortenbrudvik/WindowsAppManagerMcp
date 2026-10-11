using System.Text;

namespace WindowsAppManagerMcp.Tests.Unit.Services;

/// <summary>Reading the clipboard's "HTML Format" data (CF_HTML) as browsers and Office write it.</summary>
public class ClipboardHtmlTests
{
    /// <summary>
    /// CF_HTML data as Chrome writes it: a header whose offsets are byte positions into the UTF-8 data, then the HTML with
    /// the fragment between StartFragment and EndFragment comments.
    /// </summary>
    public static byte[] CfHtml(string fragment, string? sourceUrl = null, string head = "")
    {
        const string headerTemplate = "Version:0.9\r\nStartHTML:{0:D10}\r\nEndHTML:{1:D10}\r\nStartFragment:{2:D10}\r\nEndFragment:{3:D10}\r\n";
        var source = sourceUrl == null ? string.Empty : $"SourceURL:{sourceUrl}\r\n";
        var before = $"<html><head>{head}</head><body>\r\n<!--StartFragment-->";
        var after = "<!--EndFragment-->\r\n</body></html>";
        var headerLength = Encoding.UTF8.GetByteCount(string.Format(headerTemplate, 0, 0, 0, 0) + source);
        var startHtml = headerLength;
        var startFragment = startHtml + Encoding.UTF8.GetByteCount(before);
        var endFragment = startFragment + Encoding.UTF8.GetByteCount(fragment);
        var endHtml = endFragment + Encoding.UTF8.GetByteCount(after);
        var header = string.Format(headerTemplate, startHtml, endHtml, startFragment, endFragment) + source;
        return Encoding.UTF8.GetBytes(header + before + fragment + after + "\0");
    }

    [Fact]
    public void Parse_ReturnsTheFragment_AndTheSourceUrl()
    {
        var content = ClipboardHtml.Parse(CfHtml("<p>Grüße, <b>world</b></p>", "https://example.com/a?b=1"));

        content.Html.Should().Be("<p>Grüße, <b>world</b></p>");
        content.SourceUrl.Should().Be("https://example.com/a?b=1");
    }

    [Fact]
    public void Parse_FindsAFragmentAfterALongHead()
    {
        // Office puts long style sheets before the fragment; the offsets still point at it
        var head = "<style>" + new string('x', 70_000) + "</style>";

        ClipboardHtml.Parse(CfHtml("<td>42</td>", head: head)).Html.Should().Be("<td>42</td>");
    }

    [Fact]
    public void Parse_WithoutValidOffsets_ReturnsEverythingAfterTheHeader()
    {
        // StartHTML -1 means "no context"; there is no fragment either
        var data = Encoding.UTF8.GetBytes("Version:0.9\r\nStartHTML:-1\r\nEndHTML:-1\r\n<html><body>hi</body></html>");

        ClipboardHtml.Parse(data).Html.Should().Be("<html><body>hi</body></html>");
    }

    [Fact]
    public void Parse_DataWithoutAHeader_IsTheHtmlItself()
    {
        ClipboardHtml.Parse(Encoding.UTF8.GetBytes("<b>bare</b>")).Html.Should().Be("<b>bare</b>");
    }

    [Fact]
    public void Parse_OffsetsPastTheEnd_AreCutToTheData()
    {
        var data = CfHtml("<b>whole fragment</b>");
        var cut = data[..(data.Length - 30)];

        ClipboardHtml.Parse(cut).Html.Should().StartWith("<b>whole");
    }
}
