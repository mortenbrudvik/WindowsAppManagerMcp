using System.Text;
using System.Text.Json;
using WindowsAppManagerMcp.Infrastructure;

namespace WindowsAppManagerMcp.Tests.Unit.Infrastructure;

/// <summary>
/// How the server writes its tool results as JSON (<see cref="ToolJson"/>): text the agent reads stays readable, and
/// <see cref="ToolJson.StringBytes"/> measures it exactly as it is written, so a read can bound its result in bytes.
/// </summary>
public class ToolJsonTests
{
    [Fact]
    public void SerializerOptions_WriteNonAsciiTextQuotesAndAngleBrackets_AsTheyAre()
    {
        // Act - Cyrillic, CJK, an apostrophe, angle brackets and an ampersand: the default encoder writes each as \uXXXX
        var json = JsonSerializer.Serialize(new ElementTextResult(true, "w1e1", 1, "Document", "Привет, 'мир' <b>&</b> 中文",
            ElementTextSource.Text, 0, 20, false, null, 1), ToolJson.SerializerOptions);

        // Assert
        json.Should().Contain("Привет, 'мир' <b>&</b> 中文");
        json.Should().NotContain("\\u");
    }

    [Fact]
    public void SerializerOptions_StillEscapeQuotesAndControlCharacters_SoTheJsonStaysValid()
    {
        // Arrange - a quote, a backslash, a line break, a control character and the line separator U+2028
        var text = "a\"b\\c\nd" + (char)0x01 + "e" + (char)0x2028 + "f";

        // Act
        var json = JsonSerializer.Serialize(text, ToolJson.SerializerOptions);

        // Assert
        json.Should().Be("\"a\\\"b\\\\c\\nd\\u0001e\\u2028f\"");
        JsonSerializer.Deserialize<string>(json).Should().Be(text);
    }

    [Fact]
    public void StringBytes_IsWhatTheSerializerWrites_ForEveryCharacter()
    {
        // Arrange - every character of the Basic Multilingual Plane but the surrogates
        var mismatches = new List<string>();
        for (var code = 0; code <= 0xFFFF; code++)
        {
            if (code is >= 0xD800 and <= 0xDFFF)
            {
                continue;
            }

            var text = ((char)code).ToString();

            // Act
            var written = Encoding.UTF8.GetByteCount(JsonSerializer.Serialize(text, ToolJson.SerializerOptions)) - 2;

            // Assert
            if (ToolJson.StringBytes(text) != written)
            {
                mismatches.Add($"U+{code:X4}: {ToolJson.StringBytes(text)} counted, {written} written");
            }
        }

        mismatches.Should().BeEmpty();
    }

    [Theory]
    [InlineData("\U0001F600")] // an emoji: a surrogate pair, written as two \uXXXX escapes
    [InlineData("a\uD800b")] // a lone surrogate, written as an escaped replacement character
    [InlineData("Привет, мир")]
    [InlineData("The release ships on Monday.\r\n")]
    public void StringBytes_OfAText_IsWhatTheSerializerWrites(string text)
    {
        var written = Encoding.UTF8.GetByteCount(JsonSerializer.Serialize(text, ToolJson.SerializerOptions)) - 2;

        ToolJson.StringBytes(text).Should().Be(written);
    }
}
