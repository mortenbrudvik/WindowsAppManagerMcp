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
        ["find_windows"] = 1_850, // 1,213: each window with visibleBounds
        ["get_all_windows"] = 1_850, // 1,213
        ["get_foreground_window"] = 110, // 72
        ["get_owned_windows"] = 150, // 76: one Save As dialog
        ["get_window_info"] = 150, // 100: detail full, by a window selector (roadmap M2.4)
        ["wait_for_window"] = 150, // 95: the Save As dialog that appeared (roadmap M3.7)

        // Window Control
        ["move_window"] = 100, // 43: handle (roadmap M2.4), bounds, outerBounds, state, monitorIndex (roadmap M2.3)
        ["resize_window"] = 100, // 43
        ["set_window_bounds"] = 100, // 43
        ["set_window_state"] = 100, // 43
        ["focus_window"] = 100, // 44
        ["close_window"] = 100, // 13: the status and handle
        ["snap_window"] = 100, // 43
        ["move_window_to_monitor"] = 100, // 43

        // Batch Operations: 2 windows or applications
        ["set_windows_bounds_batch"] = 160, // 104
        ["snap_windows_batch"] = 160, // 104
        ["set_windows_state_batch"] = 160, // 104
        ["close_windows_batch"] = 100, // 41: each window with its status
        ["launch_applications_batch"] = 370, // 242 (with each window found)

        // Monitor Info: 2 monitors
        ["get_monitors"] = 150, // 98
        ["get_primary_monitor"] = 100, // 44

        // Screenshot: metadata only; the image is checked against ImageTokenBudget
        ["list_screens"] = 200, // 116
        ["take_screenshot"] = 150, // 131
        ["capture_region"] = 150, // 118
        ["capture_window"] = 150, // 126
        ["capture_with_elements"] = 2_150, // 1,416: the legend of the typical tree's 120 elements, one line each (roadmap M3.11; was 6,544 with the elements list, now detail full), and markPrefix (roadmap M3.F38)

        // UI Automation
        ["get_desktop_state"] = 3_200, // 2,129: 15 window rows, the focus, 2 popups and 80 element lines with refs (roadmap M3.12)
        ["get_ui_elements"] = 8_500, // 6,000: 120 elements at the default depth, each with its ref (roadmap M3.4; M6 aims for at most 4,000)
        ["find_ui_elements"] = 250, // 170: two Save buttons with refs and click points (roadmap M3.4)
        ["read_element_text"] = 1_200, // 802: the first page of a 3,000-character e-mail in the reading pane (roadmap M3.9)
        ["read_table"] = 1_000, // 643: the first 50 of Task Manager's processes, 5 columns with headers (roadmap M3.9)
        ["element_action"] = 150, // 99: a set_value with the state before and after (roadmap M3.6)
        ["respond_to_dialog"] = 220, // 141: a save prompt's text and three buttons with refs, the one pressed (roadmap M3.5)
        ["file_dialog"] = 100, // 49: completed, the path typed (roadmap M3.14)
        ["get_focused_element"] = 250, // 140
        ["get_element_at_point"] = 400, // 245: the element and 3 ancestors
        ["get_active_popups"] = 300, // 152: a context menu and a Save As dialog with refs (roadmap M3.5)
        ["wait_for_element"] = 140, // 92: the Save button that became enabled, with its ref (roadmap M3.8)

        // Browser Reading
        ["get_browser_url"] = 100, // 56
        ["get_active_browser_url"] = 100, // 56
        ["get_browser_content"] = 6_000, // 3,815: a 15,000-character article
        ["launch_browser_with_debug"] = 100, // 41

        // Input Simulation
        ["click"] = 150, // 79
        ["right_click"] = 150, // 80
        ["double_click"] = 150, // 80
        ["mouse_click"] = 150, // 90
        ["drag"] = 180, // 116
        ["mouse_move"] = 150, // 75
        ["type_text"] = 150, // 77: the length and method, never the text (M4.4)
        ["send_keys"] = 150, // 67
        ["press_keys"] = 150, // 72
        ["scroll"] = 150, // 81

        // Clipboard: a copied paragraph of 300 characters
        ["get_clipboard"] = 200, // 104
        ["set_clipboard"] = 100, // 19

        // Input Control
        ["get_control_status"] = 150, // 65
        ["resume_input"] = 100, // 14
        ["release_all_input"] = 100, // 21

        // App Launch
        ["launch_application"] = 160, // 103 (with the window it found)
        ["list_processes"] = 2_000, // 1,328: 40 processes
        ["kill_process"] = 100, // 28
        ["close_application"] = 100, // 49: one Notepad window

        // Layout Presets: 5 presets of 6 placements
        ["list_layouts"] = 400, // 239
        ["get_layout"] = 800, // 529: captured placements carry the window identity (M2.8)
        ["save_layout"] = 100, // 19
        ["apply_layout"] = 420, // 277: one result per placement (M2.8)
        ["delete_layout"] = 100, // 4

        // Server Info: the default deny list, five host processes, two monitors
        ["get_server_info"] = 850, // 634
    };

    /// <summary>
    /// The token budget of the whole tools/list result (every tool's name, title, description, schemas and annotations), as
    /// clients that load every tool up front pay it for the whole session. A ratchet, not a margin: it sits just above the
    /// measured size (57,538 tokens, 230,149 bytes for 67 tools, October 2026, after M3.F32's screenshotMarks and observe on six action tools, M3.F8's launch arguments description, M3.F38's markPrefix, M4.2's CursorClamped and mouse buttons, and M4.1's key grammar, send_keys' delayMs and keysSent, and type_text's controlChars, M4.3's mouse_click, drag and press_keys, M4.4's type_text method and paste, and M5.2's targetId on get_browser_url and get_browser_content, +110 after the three cdpPort descriptions were shortened, less after get_active_browser_url's requireCdp stopped naming targetId, and M5.7's get_browser_url description), so every addition to the surface is a decision. When a change
    /// adds to the surface on purpose, raise it in the same change by what the failure message measures; when a change
    /// trims descriptions, lower it.
    /// </summary>
    public const int ToolListTokenBudget = 57_574;

    public static TheoryData<string> ServedTools() => EveryToolContractTests.ServedTools();

    [Fact]
    public async Task ToolList_StaysWithinItsBudget()
    {
        // Act - the tools as a client receives them, serialized as the SDK writes them
        var tools = await ToolSurface.ListToolsAsync();
        var bytes = Encoding.UTF8.GetByteCount(tools.ToJsonString(ModelContextProtocol.McpJsonUtilities.DefaultOptions));
        var tokens = (bytes + BytesPerToken - 1) / BytesPerToken;

        // Assert
        tokens.Should().BeLessThanOrEqualTo(ToolListTokenBudget,
            "tools/list is about {0} tokens ({1} bytes, {2} tools; budget {3}); if the surface grew on purpose, raise the budget",
            tokens, bytes, tools.Count, ToolListTokenBudget);
    }

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
            image.DecodedData.ToArray().Should().NotBeEmpty();
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
