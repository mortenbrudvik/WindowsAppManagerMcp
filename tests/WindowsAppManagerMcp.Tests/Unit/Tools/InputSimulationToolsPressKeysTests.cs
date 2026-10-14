using System.Text.Json;
using WindowsAppManagerMcp.Infrastructure;
using WindowsAppManagerMcp.Tools;

namespace WindowsAppManagerMcp.Tests.Unit.Tools;

/// <summary>
/// press_keys (roadmap M4.3) as a tool: its keys bind from a string or an array of strings, its arguments reach the input
/// service, and its ranges are refused before anything is sent.
/// </summary>
public class InputSimulationToolsPressKeysTests
{
    private readonly Mock<IInputSimulationService> _input = new();

    public InputSimulationToolsPressKeysTests()
    {
        _input.Setup(s => s.PressKeysAsync(It.IsAny<KeyList>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<InputTarget>(),
                It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((KeyList keys, int repeat, int _, int _, InputTarget _, bool _, CancellationToken _) =>
                new SendKeysResult(true, keys.Text) { KeysSent = repeat });
    }

    private InputSimulationTools CreateSut() => new(_input.Object);

    [Fact]
    public async Task PressKeys_PassesItsArgumentsAndTarget()
    {
        var result = await CreateSut().PressKeys(KeyList.Of("tab*3"), repeat: 2, holdMs: 50, delayBetweenMs: 20, windowHandle: 0x5150)
            .Payload<SendKeysResult>();

        result.Success.Should().BeTrue();
        result.Keys.Should().Be("tab*3");
        _input.Verify(s => s.PressKeysAsync(KeyList.Of("tab*3"), 2, 50, 20, InputTarget.Window(0x5150, false), false, It.IsAny<CancellationToken>()));
    }

    [Theory]
    [InlineData(0, 0, 0)]
    [InlineData(101, 0, 0)]
    [InlineData(1, -1, 0)]
    [InlineData(1, 5001, 0)]
    [InlineData(1, 0, 1001)]
    public async Task PressKeys_OutOfRange_IsInvalidArguments_AndSendsNothing(int repeat, int holdMs, int delayBetweenMs)
    {
        var act = () => CreateSut().PressKeys(KeyList.Of("tab"), repeat, holdMs, delayBetweenMs);

        (await act.Should().ThrowAsync<ToolErrorException>()).Which.ErrorCode.Should().Be(ToolErrorReporting.InvalidArgumentsErrorCode);
        _input.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task PressKeys_TooLongInAll_IsInvalidArguments()
    {
        _input.Setup(s => s.PressKeysAsync(It.IsAny<KeyList>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<InputTarget>(),
                It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new ArgumentException("100 presses x (holdMs 100 + delayBetweenMs 1) is 10100 ms"));

        var act = () => CreateSut().PressKeys(KeyList.Of("tab*100"), holdMs: 100, delayBetweenMs: 1);

        (await act.Should().ThrowAsync<ToolErrorException>()).Which.Message.Should().Contain("10100 ms").And.Contain("Nothing was sent.");
    }

    [Theory]
    [InlineData("\"ctrl+shift+t\"", false, new[] { "ctrl+shift+t" })]
    [InlineData("[\"ctrl\",\"shift\",\"t\"]", true, new[] { "ctrl", "shift", "t" })]
    public void Keys_BindFromAStringOrAnArrayOfStrings(string json, bool isArray, string[] items)
    {
        var keys = JsonSerializer.Deserialize<KeyList>(json, ToolJson.SerializerOptions)!;

        keys.IsArray.Should().Be(isArray);
        keys.Items.Should().Equal(items);
        JsonSerializer.Serialize(keys, ToolJson.SerializerOptions).Should().Be(json);
    }

    [Theory]
    [InlineData("5")]
    [InlineData("[\"ctrl\", 5]")]
    [InlineData("{\"keys\": \"t\"}")]
    public void Keys_OfAnyOtherShape_DoNotBind(string json)
    {
        var act = () => JsonSerializer.Deserialize<KeyList>(json, ToolJson.SerializerOptions);

        act.Should().Throw<JsonException>().WithMessage("*'ctrl+shift+t'*");
    }

    [Fact]
    public async Task Keys_AreAStringOrAnArrayOfStrings_InTheInputSchema()
    {
        var keys = ToolSurface.Tool(await ToolSurface.ListToolsAsync(), "press_keys")["inputSchema"]!["properties"]!["keys"]!;

        keys["type"]!.AsArray().Select(t => t!.GetValue<string>()).Should().Equal("string", "array");
        keys["items"]!["type"]!.GetValue<string>().Should().Be("string");
        keys["description"]!.GetValue<string>().Should().Contain("['ctrl','shift','t']");
    }
}
