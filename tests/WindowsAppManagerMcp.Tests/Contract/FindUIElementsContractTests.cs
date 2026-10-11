using System.Text.Json;
using ModelContextProtocol.Protocol;
using WindowsAppManagerMcp.Infrastructure;

namespace WindowsAppManagerMcp.Tests.Contract;

/// <summary>
/// Contract: find_ui_elements (roadmap M3.4) through the production tool and filter registrations, with a mocked search
/// service: the result's JSON shape, the strict binding of nameMatch and detail, and failures as isError.
/// </summary>
public class FindUIElementsContractTests
{
    private static async Task<CallToolResult> CallAsync(Dictionary<string, object?> arguments, Action<ContractServices>? arrange = null)
    {
        using var services = new ContractServices(ContractMode.Typical);
        arrange?.Invoke(services);
        await using var server = await InProcessMcpServer.StartAsync(
            [],
            builder => builder.AddWindowsAppManagerTools(),
            configureServices: services.Register);
        return await server.CallToolAsync("find_ui_elements", arguments);
    }

    private static JsonElement Json(CallToolResult result) =>
        JsonDocument.Parse(result.Content.OfType<TextContentBlock>().Single().Text).RootElement.Clone();

    [Fact]
    public async Task AStandardResult_PutsTheRefFirst_NamesItsStates_AndLeavesOutWhatDoesNotApply()
    {
        // Arrange
        var wrap = new FoundElement("w4120035e40", "Word wrap", "CheckBox", "wrapBox", "CheckBox", new BoundsDto(10, 20, 100, 20),
            new ScreenPoint(18, 30), true, false, ["Toggle"], ToggleState: UiaToggleState.On, HasKeyboardFocus: false, IsKeyboardFocusable: true);

        // Act
        var result = await CallAsync(
            new() { ["handle"] = 1706796L, ["controlType"] = "CheckBox", ["detail"] = "standard" },
            services => services.ElementSearch
                .Setup(s => s.FindAsync(It.Is<ElementQuery>(q => q.Detail == ElementDetail.Standard), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new FindElementsResult(true, 1706796, "Editor", 1, false, [wrap], 4.5)));

        // Assert
        result.IsError.Should().NotBe(true);
        var element = Json(result).GetProperty("elements")[0];
        element.EnumerateObject().First().Name.Should().Be("ref");
        element.GetProperty("toggleState").GetString().Should().Be("On");
        element.GetProperty("clickPoint").GetProperty("x").GetInt32().Should().Be(18);
        element.TryGetProperty("value", out _).Should().BeFalse();
        element.TryGetProperty("frameworkId", out _).Should().BeFalse();
    }

    [Theory]
    [InlineData("nameMatch", "fuzzy")]
    [InlineData("detail", "everything")]
    public async Task UnknownFixedValues_AreRefused_ListingTheAllowedOnes(string parameter, string value)
    {
        var result = await CallAsync(new() { ["handle"] = 1706796L, ["name"] = "Save", [parameter] = value });

        result.IsError.Should().BeTrue();
        var json = Json(result);
        json.GetProperty("errorCode").GetString().Should().Be(ToolErrorReporting.InvalidArgumentsErrorCode);
        json.GetProperty("error").GetString().Should().Contain(parameter == "detail" ? "minimal" : "contains");
    }

    [Fact]
    public async Task ACallWithoutCriteria_IsAToolError()
    {
        var result = await CallAsync(new() { ["handle"] = 1706796L });

        result.IsError.Should().BeTrue();
        Json(result).GetProperty("errorCode").GetString().Should().Be(ToolErrorReporting.InvalidArgumentsErrorCode);
    }

    [Fact]
    public async Task AStaleWithinRef_IsAnErrorWithItsCode_AndNothingIsSearched()
    {
        // Arrange
        ContractServices? arranged = null;

        // Act
        var result = await CallAsync(
            new() { ["name"] = "Save", ["withinRef"] = "w4120035e9" },
            services =>
            {
                arranged = services;
                services.ElementRegistry.Setup(r => r.Lookup("w4120035e9")).Returns(new ElementLookup(
                    "w4120035e9", 0, null, 0, UIElementErrorCode.ElementStale, "Element ref 'w4120035e9' is stale: its window closed."));
            });

        // Assert
        result.IsError.Should().BeTrue();
        Json(result).GetProperty("errorCode").GetString().Should().Be("ElementStale");
        arranged!.ElementSearch.Verify(s => s.FindAsync(It.IsAny<ElementQuery>(), It.IsAny<CancellationToken>()), Times.Never);
    }
}
