using System.Text;
using System.Text.Json;
using ModelContextProtocol.Protocol;

namespace WindowsAppManagerMcp.Tests.Contract;

/// <summary>
/// Contract: the result of each tool's typical call (EveryToolContractTests, on the <see cref="TypicalDesktop"/>)
/// stays within a token budget, so a result that grows (a new field on every window, an echoed argument, image
/// data in the metadata) is noticed before it costs every agent context on every call.
/// </summary>
/// <remarks>
/// Text is estimated at <see cref="BytesPerToken"/> UTF-8 bytes per token, a rough figure for JSON. Images are
/// estimated the way Anthropic documents it for Claude: width x height / <see cref="PixelsPerImageToken"/>, from
/// the capture's metadata. The budgets are generous on purpose: about one and a half times the measured size,
/// rounded up, so only a real change in size fails. When a change makes a result legitimately larger, measure
/// it (the failure message gives the size) and raise the budget in the same change; when one shrinks a lot,
/// lower it. TESTING.md lists the measured sizes.
/// </remarks>
public class ToolPayloadBudgetTests
{
    /// <summary>UTF-8 bytes of JSON text per token, for the estimate.</summary>
    public const int BytesPerToken = 4;

    /// <summary>Pixels per image token: Claude's documented estimate is width x height / 750.</summary>
    public const int PixelsPerImageToken = 750;

    /// <summary>
    /// The largest image at the default maxDimension: 1568 pixels on both edges, about 3,278 tokens. A capture
    /// over this budget was not scaled to the default.
    /// </summary>
    public const int ImageTokenBudget = 3_300;

    /// <summary>
    /// The text (JSON) token budget of each tool's typical result: at least 100, otherwise about one and a half times
    /// the measured size (the comment, October 2026), rounded up. For a capture it covers the metadata block only.
    /// </summary>
    private static readonly IReadOnlyDictionary<string, int> TextTokenBudgets = new Dictionary<string, int>(StringComparer.Ordinal)
    {
        // Window Discovery: 15 windows
        ["find_windows"] = 1_500, // 989
        ["get_all_windows"] = 1_500, // 989
        ["get_foreground_window"] = 100, // 58

        // Window Control
        ["move_window"] = 100, // 18
        ["resize_window"] = 100, // 18
        ["set_window_bounds"] = 100, // 18
        ["set_window_state"] = 100, // 9
        ["focus_window"] = 100, // 44
        ["close_window"] = 100, // 4
        ["snap_window"] = 100, // 18
        ["move_window_to_monitor"] = 100, // 18

        // Batch Operations: 2 windows or applications
        ["set_windows_bounds_batch"] = 100, // 59
        ["snap_windows_batch"] = 100, // 59
        ["set_windows_state_batch"] = 100, // 42
        ["close_windows_batch"] = 100, // 32
        ["launch_applications_batch"] = 100, // 54

        // Monitor Info: 2 monitors
        ["get_monitors"] = 150, // 98
        ["get_primary_monitor"] = 100, // 44

        // Screenshot: metadata only; the image is checked against ImageTokenBudget
        ["list_screens"] = 200, // 116
        ["take_screenshot"] = 150, // 97
        ["capture_region"] = 150, // 79
        ["capture_window"] = 150, // 94
        ["capture_with_elements"] = 9_500, // 6,026: the 120 elements, each with imageBounds and screenBounds

        // UI Automation
        ["get_ui_elements"] = 8_500, // 5,397: 120 elements at the default depth (roadmap M6 aims for at most 4,000)
        ["get_focused_element"] = 250, // 140
        ["get_element_at_point"] = 400, // 245: the element and 3 ancestors

        // Browser Reading
        ["get_browser_url"] = 100, // 56
        ["get_active_browser_url"] = 100, // 56
        ["get_browser_content"] = 6_000, // 3,815: a 15,000-character article
        ["launch_browser_with_debug"] = 100, // 41

        // Input Simulation
        ["click"] = 150, // 79
        ["right_click"] = 150, // 80
        ["double_click"] = 150, // 80
        ["mouse_move"] = 150, // 75
        ["type_text"] = 150, // 77: echoes the typed text
        ["send_keys"] = 150, // 67

        // Input Control
        ["get_control_status"] = 150, // 72
        ["resume_input"] = 100, // 14
        ["release_all_input"] = 100, // 21

        // App Launch
        ["launch_application"] = 100, // 15
        ["list_processes"] = 2_000, // 1,328: 40 processes
        ["kill_process"] = 100, // 28

        // Layout Presets: 5 presets of 6 placements
        ["list_layouts"] = 400, // 239
        ["get_layout"] = 400, // 257
        ["save_layout"] = 100, // 19
        ["apply_layout"] = 150, // 72
        ["delete_layout"] = 100, // 4
    };

    public static TheoryData<string> ServedTools() => EveryToolContractTests.ServedTools();

    [Fact]
    public void EveryServedTool_HasATextBudget()
    {
        TextTokenBudgets.Keys.Should().BeEquivalentTo(ServerToolCatalog.ServedToolNames,
            "every tool's typical result is measured; a new tool needs a budget here (see TESTING.md)");
    }

    [Theory]
    [MemberData(nameof(ServedTools))]
    public async Task TypicalResult_StaysWithinItsTokenBudget(string tool)
    {
        // Arrange
        var budget = TextTokenBudgets.GetValueOrDefault(tool);

        // Act
        var result = await EveryToolContractTests.TypicalCallAsync(tool);
        var text = result.Content.OfType<TextContentBlock>().Select(t => t.Text).ToList();
        var textTokens = (text.Sum(t => Encoding.UTF8.GetByteCount(t)) + BytesPerToken - 1) / BytesPerToken;

        // Assert
        textTokens.Should().BeLessThanOrEqualTo(budget,
            "the typical result of '{0}' is about {1} tokens of JSON (budget {2}); if it grew on purpose, raise its budget",
            tool, textTokens, budget);

        foreach (var image in result.Content.OfType<ImageContentBlock>())
        {
            var (width, height) = ImageSize(text);
            var imageTokens = width * height / PixelsPerImageToken;
            imageTokens.Should().BeLessThanOrEqualTo(ImageTokenBudget,
                "the {0} x {1} image of '{2}' costs about {3} tokens; captures are scaled to 1568 pixels on the long edge by default",
                width, height, tool, imageTokens);
            image.Data.Should().NotBeEmpty();
        }
    }

    [Fact]
    public async Task TheTypicalResults_HaveTheTypicalSizes()
    {
        // The budgets mean something only if the typical call really returns a typical desktop's worth of data
        Json(await EveryToolContractTests.TypicalCallAsync("find_windows")).GetArrayLength().Should().Be(TypicalDesktop.Windows.Count);
        Json(await EveryToolContractTests.TypicalCallAsync("list_processes")).GetArrayLength().Should().Be(TypicalDesktop.Processes.Count);
        Json(await EveryToolContractTests.TypicalCallAsync("get_ui_elements")).GetProperty("totalElementCount").GetInt32()
            .Should().Be(TypicalDesktop.UiTreeElements);
        Json(await EveryToolContractTests.TypicalCallAsync("get_browser_content")).GetProperty("contentLength").GetInt32()
            .Should().Be(TypicalDesktop.ArticleLength);
        Json(await EveryToolContractTests.TypicalCallAsync("list_layouts")).GetArrayLength().Should().Be(TypicalDesktop.Layouts.Count);

        var screenshot = Json(await EveryToolContractTests.TypicalCallAsync("take_screenshot"));
        screenshot.GetProperty("width").GetInt32().Should().Be(1568, "a 2560 x 1440 monitor is scaled to the default long edge");
        screenshot.GetProperty("originalWidth").GetInt32().Should().Be(2560);
    }

    private static (int Width, int Height) ImageSize(IReadOnlyList<string> text)
    {
        using var metadata = JsonDocument.Parse(text.Single());
        return (metadata.RootElement.GetProperty("width").GetInt32(), metadata.RootElement.GetProperty("height").GetInt32());
    }

    private static JsonElement Json(CallToolResult result) =>
        JsonDocument.Parse(result.Content.OfType<TextContentBlock>().Single().Text).RootElement.Clone();
}
