using System.Text.Json.Nodes;

namespace WindowsAppManagerMcp.Tests.Unit.Infrastructure;

/// <summary>
/// The generator of the docs' tool tables (<see cref="ToolDocGenerator"/>) and its marker regions
/// (<see cref="MarkdownRegions"/>): what a regeneration takes from tools/list and what hand-written text it keeps.
/// </summary>
public class ToolDocGeneratorTests
{
    private static JsonObject Tool(string schemaJson) => new()
    {
        ["name"] = "a_tool",
        ["inputSchema"] = JsonNode.Parse(schemaJson)
    };

    #region Parameter tables

    [Fact]
    public void ParameterTables_TakeNamesTypesRequiredAndDefaultsFromTheSchema_AndKeepTheWrittenDescriptions()
    {
        // Arrange - the written table has stale types and a parameter the tool no longer takes
        var tool = Tool("""
            {
              "type": "object",
              "properties": {
                "handle": { "description": "Schema text", "type": "integer" },
                "state": { "description": "Schema state", "type": "string", "default": "restore" },
                "visible": { "type": "boolean", "default": true }
              },
              "required": ["handle"]
            }
            """);
        var current = """
            **Parameters:**
            | Parameter | Type | Required | Default | Description |
            |-----------|------|----------|---------|-------------|
            | handle | long | No | 5 | Window handle from find_windows |
            | gone | int | No | - | Removed parameter |
            | visible | bool | No | false | Only visible windows |
            """.ReplaceLineEndings("\n");

        // Act
        var table = ToolDocGenerator.ParameterTables(tool, current);

        // Assert
        table.Should().Be("""
            **Parameters:**
            | Parameter | Type | Required | Default | Description |
            |-----------|------|----------|---------|-------------|
            | handle | integer | Yes | - | Window handle from find_windows |
            | state | string | No | "restore" | Schema state |
            | visible | boolean | No | true | Only visible windows |
            """.ReplaceLineEndings("\n"));
    }

    [Fact]
    public void ParameterTables_KeepWordsForANullDefault_ButNeverAStaleLiteral()
    {
        var tool = Tool("""
            {
              "type": "object",
              "properties": {
                "monitorIndex": { "type": ["integer", "null"], "default": null },
                "maxLength": { "type": ["integer", "null"], "default": null }
              }
            }
            """);
        var current = """
            **Parameters:**
            | Parameter | Type | Required | Default | Description |
            |-----------|------|----------|---------|-------------|
            | monitorIndex | int | No | primary | Monitor to capture |
            | maxLength | int | No | 5000 | Longest text |
            """.ReplaceLineEndings("\n");

        var table = ToolDocGenerator.ParameterTables(tool, current);

        table.Should().Contain("| monitorIndex | integer | No | primary | Monitor to capture |")
            .And.Contain("| maxLength | integer | No | - | Longest text |", "5000 looks like the default value, but the default is null");
    }

    [Fact]
    public void ParameterTables_ForANewParameter_UseTheSchemaDescription_WithPipesEscaped()
    {
        var tool = Tool("""
            {
              "type": "object",
              "properties": { "args": { "description": "Refuses & | < >\nin batch files", "type": "array", "items": { "type": "string" } } }
            }
            """);

        var table = ToolDocGenerator.ParameterTables(tool, current: string.Empty);

        table.Should().EndWith("| args | string[] | No | - | Refuses & \\| < > in batch files |");
    }

    [Fact]
    public void ParameterTables_ListTheFieldsOfAnArrayOfObjects_KeepingTheirWrittenCells()
    {
        // Arrange - the hand-written field table names no parameter; it belongs to the only array of objects
        var tool = Tool("""
            {
              "type": "object",
              "properties": {
                "placements": {
                  "description": "Placements",
                  "type": "array",
                  "items": {
                    "type": "object",
                    "properties": {
                      "handle": { "type": "integer" },
                      "monitorIndex": { "type": ["integer", "null"], "default": null },
                      "bringToFront": { "type": "boolean", "default": true }
                    },
                    "required": ["handle"]
                  }
                }
              },
              "required": ["placements"]
            }
            """);
        var current = """
            **Parameters:**
            | Parameter | Type | Required | Default | Description |
            |-----------|------|----------|---------|-------------|
            | placements | array | Yes | - | Array of placements |

            Each placement object contains:
            | Field | Type | Required | Description |
            |-------|------|----------|-------------|
            | handle | long | Yes | Window handle |
            | monitorIndex | int | No | Target monitor |
            """.ReplaceLineEndings("\n");

        // Act
        var table = ToolDocGenerator.ParameterTables(tool, current);

        // Assert
        table.Should().Be("""
            **Parameters:**
            | Parameter | Type | Required | Default | Description |
            |-----------|------|----------|---------|-------------|
            | placements | object[] | Yes | - | Array of placements |

            Each item of `placements` contains:
            | Field | Type | Required | Default | Description |
            |-------|------|----------|---------|-------------|
            | handle | integer | Yes | - | Window handle |
            | monitorIndex | integer | No | - | Target monitor |
            | bringToFront | boolean | No | true | - |
            """.ReplaceLineEndings("\n"));

        // A second run reads its own format and changes nothing
        ToolDocGenerator.ParameterTables(tool, table).Should().Be(table);
    }

    [Fact]
    public void ParameterTables_WithoutParameters_SayNone()
    {
        ToolDocGenerator.ParameterTables(Tool("""{ "type": "object", "properties": {} }"""), "anything")
            .Should().Be("**Parameters:** None");
    }

    [Theory]
    [InlineData("""{ "type": "integer" }""", "integer")]
    [InlineData("""{ "type": ["integer", "null"] }""", "integer")]
    [InlineData("""{ "type": "array", "items": { "type": "string" } }""", "string[]")]
    [InlineData("""{ "type": ["array", "null"], "items": { "type": "object" } }""", "object[]")]
    [InlineData("""{ "type": "string", "enum": ["left", "right"] }""", "string (one of: `\"left\"`, `\"right\"`)")]
    [InlineData("""{ "description": "no type" }""", "any")]
    public void TypeName_IsTheJsonType(string schema, string expected)
    {
        ToolDocGenerator.TypeName(JsonNode.Parse(schema)).Should().Be(expected);
    }

    [Fact]
    public void SplitRow_KeepsAnEscapedPipeInItsCell()
    {
        ToolDocGenerator.SplitRow("| a | b \\| c | d |").Should().Equal("a", "b \\| c", "d");
    }

    #endregion

    #region Regions

    [Fact]
    public void Rewrite_ReplacesOnlyTheRegionContent()
    {
        var text = "intro\n<!-- tools:begin one (a note) -->\nold\nlines\n<!-- tools:end one -->\nmiddle\n<!-- tools:begin two -->\n<!-- tools:end two -->\nend\n";

        var rewritten = MarkdownRegions.Rewrite(text, (id, current) => id == "one" ? $"new ({current.Replace('\n', '+')})" : "filled\nin");

        rewritten.Should().Be(
            "intro\n<!-- tools:begin one (a note) -->\nnew (old+lines)\n<!-- tools:end one -->\nmiddle\n<!-- tools:begin two -->\nfilled\nin\n<!-- tools:end two -->\nend\n");
        MarkdownRegions.Find(rewritten).Select(r => r.Id).Should().Equal("one", "two");
    }

    [Theory]
    [InlineData("<!-- tools:begin a -->\ntext\n", "has no end marker")]
    [InlineData("text\n<!-- tools:end a -->\n", "ends without a begin marker")]
    [InlineData("<!-- tools:begin a -->\n<!-- tools:begin b -->\n<!-- tools:end b -->\n<!-- tools:end a -->\n", "begins inside region 'a'")]
    [InlineData("<!-- tools:begin a -->\n<!-- tools:end a -->\n<!-- tools:begin a -->\n<!-- tools:end a -->\n", "more than once")]
    public void Find_RejectsBrokenMarkers(string text, string message)
    {
        var act = () => MarkdownRegions.Find(text);

        act.Should().Throw<FormatException>().WithMessage($"*{message}*");
    }

    [Fact]
    public void Generate_RejectsARegionNothingGenerates()
    {
        var act = () => ToolDocGenerator.Generate(ToolDocGenerator.Readme, "no-such-region", string.Empty, []);

        act.Should().Throw<ArgumentException>().WithMessage("*unknown generated region 'no-such-region'*");
    }

    #endregion
}
