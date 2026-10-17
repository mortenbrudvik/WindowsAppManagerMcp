using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol.Server;
using WindowsAppManagerMcp.Infrastructure;

namespace WindowsAppManagerMcp.Tests.Unit.Models;

/// <summary>
/// The `monitor` parameter (roadmap M6.7, owner decision D13): an index as a JSON number or a string of digits, or a name
/// ('primary', 'left', 'right', 'current', 'all', an id or a device name), read by its converter and described in the
/// input schema as an integer or a string.
/// </summary>
public class MonitorSelectorTests
{
    private static MonitorSelector? Read(string json) => JsonSerializer.Deserialize<MonitorSelector>(json, ToolJson.SerializerOptions);

    [Theory]
    [InlineData("0", 0)]
    [InlineData("2", 2)]
    [InlineData("-1", -1)]
    [InlineData("\"1\"", 1)]
    [InlineData("\" 3 \"", 3)]
    [InlineData("\"-1\"", -1)]
    [InlineData("1.0", 1)]
    [InlineData("1e0", 1)]
    [InlineData("-1.0", -1)]
    public void AnIndex_IsReadFromANumberOrAStringOfDigits(string json, int index)
    {
        // Act
        var selector = Read(json);

        // Assert
        selector!.Index.Should().Be(index);
        selector.Name.Should().BeNull();
    }

    [Theory]
    [InlineData("\"primary\"", "primary")]
    [InlineData("\"  Left \"", "Left")]
    [InlineData("\"m-1a2b3c4d\"", "m-1a2b3c4d")]
    [InlineData("\"\\\\\\\\.\\\\DISPLAY2\"", @"\\.\DISPLAY2")]
    public void AName_IsReadFromAString_Trimmed(string json, string name)
    {
        // Act
        var selector = Read(json);

        // Assert
        selector!.Index.Should().BeNull();
        selector.Name.Should().Be(name);
    }

    [Theory]
    [InlineData("1.5")]
    [InlineData("1e10")]
    [InlineData("\"\"")]
    [InlineData("\"   \"")]
    [InlineData("true")]
    [InlineData("{}")]
    [InlineData("[1]")]
    [InlineData("99999999999")]
    public void AnythingElse_FailsToBind(string json)
    {
        // Act
        var read = () => Read(json);

        // Assert
        read.Should().Throw<JsonException>().WithMessage("*monitor*");
    }

    [Theory]
    [InlineData("1")]
    [InlineData("\"left\"")]
    public void ASelector_IsWrittenAsItWasGiven(string json)
    {
        // Act
        var written = JsonSerializer.Serialize(Read(json), ToolJson.SerializerOptions);

        // Assert
        written.Should().Be(json);
    }

    [Fact]
    public void ASelector_IsDescribedAsAnIntegerOrAString_AndNullWhenOptional()
    {
        // Arrange
        var method = typeof(MonitorSelectorTests).GetMethod(nameof(SampleTool), System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;

        // Act
        var schema = JsonNode.Parse(ToolJson.CreateTool(method, typeof(MonitorSelectorTests), null).ProtocolTool.InputSchema.GetRawText())!;

        // Assert
        var required = schema["properties"]!["required"]!.AsObject();
        required["type"]!.AsArray().Select(t => t!.GetValue<string>()).Should().Equal("integer", "string");
        required.ContainsKey("properties").Should().BeFalse();
        required["description"]!.GetValue<string>().Should().Be("The monitor");

        var optional = schema["properties"]!["optional"]!.AsObject();
        optional["type"]!.AsArray().Select(t => t!.GetValue<string>()).Should().Equal("integer", "string", "null");
    }

    [Fact]
    public void ASelectorProperty_OfAnItem_IsDescribedTheSameWay()
    {
        // Arrange - snap_windows_batch's items and the launch placement have a monitor property
        var method = typeof(MonitorSelectorTests).GetMethod(nameof(SampleItemTool), System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;

        // Act
        var schema = JsonNode.Parse(ToolJson.CreateTool(method, typeof(MonitorSelectorTests), null).ProtocolTool.InputSchema.GetRawText())!;

        // Assert
        var monitor = schema["properties"]!["item"]!["properties"]!["monitor"]!.AsObject();
        monitor["type"]!.AsArray().Select(t => t!.GetValue<string>()).Should().Equal("integer", "string", "null");
        monitor["description"]!.GetValue<string>().Should().Be("Its monitor");
    }

    [McpServerTool(Name = "sample")]
    private static string SampleTool([Description("The monitor")] MonitorSelector required, MonitorSelector? optional = null) =>
        required.ToString() + optional;

    [McpServerTool(Name = "sample_item")]
    private static string SampleItemTool(SampleItem item) => item.ToString();

    internal sealed record SampleItem(string Name, [property: Description("Its monitor")] MonitorSelector? Monitor = null);
}
