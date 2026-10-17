using System.Text;
using System.Text.Json;
using ModelContextProtocol.Protocol;
using WindowsAppManagerMcp.Infrastructure;

namespace WindowsAppManagerMcp.Tests.Contract;

/// <summary>
/// Contract: the result of each tool's typical call (EveryToolContractTests, on the <see cref="TypicalDesktop"/>)
/// stays within a token budget, so a result that grows (a new field on every window, an echoed argument, image
/// data in the metadata) is noticed before it costs every agent context on every call.
/// </summary>
/// <remarks>
/// Text is estimated at <see cref="ToolSurface.BytesPerToken"/> UTF-8 bytes per token, a rough figure for JSON. Images are
/// estimated the way Anthropic documents it for Claude: width x height / <see cref="PixelsPerImageToken"/>, from
/// the capture's metadata. The budgets are generous on purpose: about one and a half times the measured size,
/// rounded up, so only a real change in size fails. When a change makes a result legitimately larger, measure
/// it (the failure message gives the size) and raise the budget in the same change; when one shrinks a lot,
/// lower it. TESTING.md lists the measured sizes.
/// </remarks>
public class ToolPayloadBudgetTests
{
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
        ["set_window_zorder"] = 100, // 16: success, handle, isTopmost and zOrder read back (roadmap M6.7)

        // Batch Operations: 2 windows or applications
        ["set_windows_bounds_batch"] = 160, // 104
        ["snap_windows_batch"] = 160, // 104
        ["set_windows_state_batch"] = 160, // 104
        ["close_windows_batch"] = 100, // 41: each window with its status
        ["launch_applications_batch"] = 370, // 242 (with each window found)

        // Monitor Info: 2 monitors
        ["get_monitors"] = 220, // 146: each monitor with id, friendlyName, dpi and relation (98 before roadmap M6.7)
        ["get_primary_monitor"] = 100, // 44

        // Screenshot: metadata only; the image is checked against ImageTokenBudget
        ["list_screens"] = 200, // 116
        ["take_screenshot"] = 150, // 131
        ["capture_region"] = 150, // 118
        ["capture_window"] = 150, // 126
        ["wait_for_visual"] = 150, // 103: two changed regions and the last frame's captureId, no image (roadmap M6.4)
        ["capture_with_elements"] = 2_150, // 1,416: the legend of the typical tree's 120 elements, one line each (roadmap M3.11; was 6,544 with the elements list, now detail full), and markPrefix (roadmap M3.F38)
        ["read_screen_text"] = 1_900, // 1,276: the 40 lines OCR reads in the typical window, each with its bounds (roadmap M6.3)
        ["find_text_on_screen"] = 190, // 125: one match with its line, bounds, clickPoint and imagePoint (roadmap M6.3)

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
        ["launch_browser_with_debug"] = 150, // 96: with launchStatus and the readiness fields (M5.1)
        ["close_debug_browser"] = 100, // 65: closed with Browser.close, its temporary profile deleted (M5.4)

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

        // Browser tabs (roadmap M5.5): three tabs of a debug Chrome
        ["list_tabs"] = 220, // 144
        ["activate_tab"] = 100, // 36
        ["browser_navigate"] = 100, // 47
        ["wait_for_browser"] = 100, // 47

        // The DOM bridge (roadmap M5.8): the same debug Chrome, its Save button and search field
        ["browser_evaluate"] = 100, // 45
        ["browser_handle_dialog"] = 100, // 48
        ["browser_element_rect"] = 130, // 81
        ["browser_click"] = 100, // 42
        ["browser_type"] = 100, // 34
        ["browser_screenshot"] = 100, // 56 (the image is budgeted separately)

        // Input Control
        ["get_control_status"] = 150, // 65
        ["resume_input"] = 100, // 14
        ["release_all_input"] = 100, // 21

        // Step Sequences: a five-step form fill with each step's status and the last step's own result (roadmap M6.2)
        ["run_steps"] = 300, // 181

        // App Launch
        ["launch_application"] = 160, // 103 (with the window it found)
        ["list_processes"] = 4_500, // 2,976: 40 processes, each with its parent, start time, integrity and windows (1,328 before M6.6)
        ["list_installed_apps"] = 1_150, // 761: the first 25 of 140 Start menu apps, packaged first, desktop apps with their paths (roadmap M6.5)
        ["kill_process"] = 100, // 28
        ["close_application"] = 100, // 49: one Notepad window

        // Layout Presets: 5 presets of 6 placements
        ["list_layouts"] = 400, // 239
        ["get_layout"] = 800, // 529: captured placements carry the window identity (M2.8)
        ["save_layout"] = 100, // 19
        ["apply_layout"] = 420, // 277: one result per placement (M2.8)
        ["delete_layout"] = 100, // 4

        // Server Info: the default deny list, five host processes, two monitors
        ["get_server_info"] = 850, // 702 (634 before M6.9 listed the resources and prompts)
    };

    /// <summary>
    /// The token budget of the whole tools/list result (every tool's name, title, description, schemas and annotations), as
    /// clients that load every tool up front pay it for the whole session. A ratchet, not a margin: it sits just above the
    /// measured size (58,758 tokens, 235,029 bytes for 68 tools, October 2026, after M3.F32's screenshotMarks and observe on six action tools, M3.F8's launch arguments description, M3.F38's markPrefix, M4.2's CursorClamped and mouse buttons, and M4.1's key grammar, send_keys' delayMs and keysSent, and type_text's controlChars, M4.3's mouse_click, drag and press_keys, M4.4's type_text method and paste, and M5.2's targetId on get_browser_url and get_browser_content, +110 after the three cdpPort descriptions were shortened, less after get_active_browser_url's requireCdp stopped naming targetId, M5.7's get_browser_url description, M5.1's launch_browser_with_debug executablePath, port 0 and launch results, and M5.1's readiness sentence (cdpReady, cdpEndpoint, browserVersion, the 10-second wait) in its description, M5.3's useCdp on the three reading tools and their found debug port, and M5.6's paged get_browser_content (offset, selector, the page fields in its description, and offset's 'a uia page continues with useCdp=false'), and M5.4's close_debug_browser and launch_browser_with_debug's profile, reuseIfRunning and placement (launch_application's placement schema, about 420 tokens of the 791), with its cleanup advice shortened to close_debug_browser; then 60,504 tokens, 242,016 bytes for 72 tools after M5.5's list_tabs, activate_tab, browser_navigate and wait_for_browser, each with handle, the window selector and cdpPort; then 63,474 tokens, 253,893 bytes for 78 tools after M5.8's DOM bridge, browser_element_rect, browser_click, browser_type, browser_handle_dialog, browser_screenshot and browser_evaluate, each with handle, the window selector (156 tokens each), cdpPort and targetId; then 63,673 tokens, 254,692 bytes after M5.8's dialog option (dialog and promptText on browser_click, browser_type and browser_navigate) and browser_handle_dialog through UI Automation (window only, accept optional); then 64,078 tokens, 256,309 bytes after M6.1a's virtualScreen on get_monitors (+28), set_window_bounds' optional x/y and width/height pairs and nullable bringToFront (+87), get_browser_url's bare foreground selector (+41) and mouse_click's own click checks instead of 'as for click' (+249); then 61,957 tokens, 247,825 bytes after M6.1b cut the
    /// nine deprecated tools' descriptions to one line that names the replacement, sent take_screenshot, the
    /// handle parameters and drag to get_monitors, find_windows and mouse_click instead of the deprecated tools, and added
    /// get_server_info's moved field to its description (-2,121 in all); then 62,348 tokens, 249,390 bytes after M6.6's
    /// list_processes parameters (processId, pathContains, sortBy, limit, includeCommandLine) and result fields in its
    /// description (+361) and get_window_info's integrity fields (+31); then 63,402 tokens, 253,608 bytes for 79 tools after M6.2's run_steps, its description and the step item schema with the tool list; then 64,474 tokens, 257,896 bytes for 80 tools after M6.4's wait_for_visual in the opt-in visual toolset; then 66,562 tokens, 266,245 bytes for 82 tools after M6.3's read_screen_text and find_text_on_screen in the opt-in ocr toolset (+2,088, sharing their target and result descriptions); then 68,076 tokens, 272,301 bytes for 83 tools after M6.7's set_window_zorder (+522), the `monitor` parameter beside the deprecated monitorIndex on snap_window, move_window_to_monitor, snap_windows_batch, take_screenshot, the two OCR tools, wait_for_visual and the launch placement, set_window_bounds' unit, relativeTo and monitor (+233), get_monitors' new fields and get_server_info's pinnedWindows (+1,514 in all); then 68,433 tokens, 273,729 bytes for 84 tools after M6.5's list_installed_apps in the default-on launch toolset (about 200), launch_application's runAsAdmin and installed-app wording (about 115) and the batch item's runAsAdmin (about 40), +357 on top of M6.7; DefaultToolListTokenBudget measures the tools served by default), so every addition to the surface is a decision. When a change
    /// adds to the surface on purpose, raise it in the same change by what the failure message measures; when a change
    /// trims descriptions, lower it.
    /// </summary>
    public const int ToolListTokenBudget = 68_469;

    /// <summary>
    /// The token budget of the default tools/list (roadmap M6.1): the tools a server started without WAM_TOOLSETS,
    /// WAM_EXCLUDE_TOOLS and WAM_READ_ONLY serves, which is what nearly every user pays for in every session. A ratchet like
    /// <see cref="ToolListTokenBudget"/>, just above the measured size: 59,627 tokens (238,508 bytes, 70 tools) before M6.1,
    /// then 60,032 tokens (240,125 bytes) after M6.1a's virtualScreen on get_monitors, set_window_bounds' optional pairs (the
    /// nullable x, y, width and height are most of its 87 tokens), get_browser_url's foreground sentence and mouse_click's own
    /// click checks (+249); then 51,005 tokens (204,020 bytes, 58 tools) after M6.1b moved the nine deprecated tools (click,
    /// right_click, double_click, move_window, resize_window, get_all_windows, get_primary_monitor, list_screens,
    /// get_active_browser_url) to the opt-in deprecated toolset and the three geometry batches to the opt-in windows-batch
    /// toolset (-9,027); then 51,397 tokens (205,585 bytes) after M6.6's list_processes parameters and fields (+361) and
    /// get_window_info's integrity fields (+31), October 2026; then 52,451 tokens (209,803 bytes, 59 tools) after M6.2's run_steps in the steps
    /// toolset, on by default; then 53,638 tokens (214,551 bytes, 60 tools) after M6.7's set_window_zorder in windows (+522),
    /// set_window_bounds' percent placement (+233), the `monitor` parameters and get_monitors' and get_server_info's fields
    /// (+1,187 in all); then 53,995 tokens (215,979 bytes, 61 tools) after M6.5's list_installed_apps in the launch toolset, on by
    /// default, and runAsAdmin on launch_application and the batch item (+357). A tool in a toolset that is off by default never moves it; a change to the default
    /// surface raises it by what the failure message measures, in the same change, or lowers it.
    /// </summary>
    public const int DefaultToolListTokenBudget = 54_031;

    /// <summary>
    /// The most tokens one tool object (name, title, description, input schema, annotations) may cost in tools/list (roadmap
    /// M6.1). Not a ratchet but a cap: a tool that needs more is doing too much, or repeats what another tool or the server
    /// instructions say. It sits about 100 tokens above the largest tool, so an error code or a sentence still fits: the
    /// plan's 2,400 came from compact estimates (2,337 and 2,279), and measured as the SDK writes them the largest were
    /// capture_with_elements (2,396, 104 of room) and element_action (2,386, 114 of room), October 2026. Do not raise it
    /// when a tool reaches it; shorten the tool or split what it does.
    /// </summary>
    public const int ToolSchemaTokenCap = 2_500;

    /// <summary>
    /// The token budget of each toolset's tools in tools/list (roadmap M6.1): what turning the toolset on adds, and what turning
    /// it off saves. Ratchets, just above the measured sizes (the comments, October 2026); docs/TOOLS.md publishes the measured
    /// sizes in its toolsets table. A change that grows a toolset raises its budget by what the failure message measures.
    /// </summary>
    private static readonly IReadOnlyDictionary<string, int> ToolsetTokenBudgets = new Dictionary<string, int>(StringComparer.Ordinal)
    {
        ["server"] = 1_092, // 1,056 (1,038 before M6.7's pinnedWindows in get_server_info)
        ["windows"] = 10_920, // 10,884: 13 tools, 11 of them with the window selector (13,724 before M6.1b moved six out; 9,921 before M6.6's get_window_info integrity fields; 9,952 before M6.7's set_window_zorder, monitor and percent placement)
        ["monitors"] = 203, // 167: get_monitors alone since M6.1b (314 with get_primary_monitor and list_screens; 118 before M6.7's identity fields)
        ["capture"] = 5_950, // 5,914 (5,856 before M6.7's monitor on take_screenshot)
        ["elements"] = 8_300, // 8,262
        ["input"] = 14_800, // 14,758: 10 tools (19,415 with the click trio before M6.1b)
        ["clipboard"] = 750, // 734
        ["launch"] = 3_372, // 3,336 (2,890 before M6.7's placement monitor, 2,979 before M6.5's list_installed_apps and runAsAdmin)
        ["process"] = 2_318, // 2,282 (1,921 before M6.6's list_processes parameters and fields)
        ["layouts"] = 1_200, // 1,181
        ["steps"] = 1_091, // 1,055: run_steps alone (roadmap M6.2), its description and the steps' item schema with the tool list
        ["browser"] = 4_392, // 4,356 (4,674 with get_active_browser_url before M6.1b; 4,315 before M6.7's placement monitor)
        ["browser-actions"] = 3_600, // 3,577 (off by default)
        ["browser-script"] = 500, // 468 (off by default)
        ["visual"] = 1_181, // 1,145 (off by default): wait_for_visual with handle, the window selector, a region, monitor (and the deprecated monitorIndex) and since (roadmap M6.4; 1,072 before M6.7)
        ["windows-batch"] = 2_102, // 2,066 (off by default since M6.1b; 1,978 before M6.7's monitor on snap_windows_batch's items)
        ["deprecated"] = 4_950, // 4,925 (off by default since M6.1b): nine one-line descriptions, but their full parameters
        ["ocr"] = 2_290, // 2,254 (off by default, roadmap M6.3): read_screen_text and find_text_on_screen (2,087 before M6.7's monitor)
    };

    /// <summary>
    /// The token budget of resources/list, resources/templates/list and prompts/list together (roadmap M6.9), outside tools/list:
    /// clients list them when they connect, but Claude Code shows them to the user (as @-mentions and slash commands) rather
    /// than loading them into the model's context. A ratchet like <see cref="ToolListTokenBudget"/>, just above the measured
    /// size: 646 tokens for two resources, one template and four prompts (October 2026). A change that adds a resource or
    /// prompt, or lengthens their descriptions, raises it by what the failure message measures.
    /// </summary>
    public const int ResourcesAndPromptsListTokenBudget = 655;

    public static TheoryData<string> ServedTools() => EveryToolContractTests.ServedTools();

    public static TheoryData<string> ToolsetNames()
    {
        var data = new TheoryData<string>();
        foreach (var toolset in Toolsets.Server.Toolsets)
        {
            data.Add(toolset.Name);
        }

        return data;
    }

    [Fact]
    public async Task ToolList_StaysWithinItsBudget()
    {
        // Act - the tools as a client receives them, serialized as the SDK writes them
        var tools = await ToolSurface.ListToolsAsync();
        var bytes = ToolSurface.WireBytes(tools);
        var tokens = ToolSurface.EstimatedTokens(bytes);

        // Assert
        tokens.Should().BeLessThanOrEqualTo(ToolListTokenBudget,
            "tools/list is about {0} tokens ({1} bytes, {2} tools; budget {3}); if the surface grew on purpose, raise the budget",
            tokens, bytes, tools.Count, ToolListTokenBudget);
    }

    [Fact]
    public async Task ResourcesAndPromptsLists_StayWithinTheirBudget()
    {
        // Act - the three lists as a client receives them, serialized as the SDK writes them
        var lists = await ResourceSurface.ListAsync();
        var tokens = ResourceSurface.Tokens(lists);

        // Assert
        tokens.Should().BeLessThanOrEqualTo(ResourcesAndPromptsListTokenBudget,
            "resources/list, resources/templates/list and prompts/list are about {0} tokens (budget {1}); if they grew on purpose, " +
            "raise the budget", tokens, ResourcesAndPromptsListTokenBudget);
    }

    [Fact]
    public async Task DefaultToolList_StaysWithinItsBudget()
    {
        // Act - the tools of a server started with no toolset variables
        var tools = await ToolSurface.ListDefaultToolsAsync();
        var bytes = ToolSurface.WireBytes(tools);
        var tokens = ToolSurface.EstimatedTokens(bytes);

        // Assert
        tokens.Should().BeLessThanOrEqualTo(DefaultToolListTokenBudget,
            "the default tools/list is about {0} tokens ({1} bytes, {2} tools; budget {3}); if the default surface grew on purpose, " +
            "raise the budget, and if a tool should not cost every user this, put it in a toolset that is off by default",
            tokens, bytes, tools.Count, DefaultToolListTokenBudget);
    }

    [Fact]
    public async Task DefaultToolList_IsTheToolsOfTheToolsetsOnByDefault()
    {
        // The default ratchet measures what users get: every tool of a default toolset, and nothing of an opt-in one
        var served = (await ToolSurface.ListDefaultToolsAsync()).Select(t => (string)t!["name"]!).ToList();
        var expected = Toolsets.Server.Toolsets.Where(t => t.OnByDefault).SelectMany(t => t.Tools);

        served.Should().BeEquivalentTo(expected);
    }

    [Fact]
    public void EveryToolset_HasATokenBudget()
    {
        ToolsetTokenBudgets.Keys.Should().BeEquivalentTo(Toolsets.Server.Toolsets.Select(t => t.Name),
            "every toolset's share of tools/list is measured; a new toolset needs a budget here (see TESTING.md)");
    }

    [Theory]
    [MemberData(nameof(ToolsetNames))]
    public async Task EveryToolset_StaysWithinItsBudget(string toolset)
    {
        // Arrange
        var budget = ToolsetTokenBudgets.GetValueOrDefault(toolset);

        // Act
        var tokens = ToolSurface.ToolsetTokens(await ToolSurface.ListToolsAsync(), Toolsets.Server.Find(toolset)!);

        // Assert
        tokens.Should().BeLessThanOrEqualTo(budget,
            "the tools of toolset '{0}' are about {1} tokens of tools/list (budget {2}); if it grew on purpose, raise its budget",
            toolset, tokens, budget);
    }

    [Fact]
    public async Task EveryTool_SchemaWithinCap()
    {
        // Act
        var tools = await ToolSurface.ListToolsAsync();
        var over = tools
            .Select(tool => (Name: (string)tool!["name"]!, Tokens: ToolSurface.EstimatedTokens(ToolSurface.WireBytes(tool))))
            .Where(tool => tool.Tokens > ToolSchemaTokenCap)
            .Select(tool => $"{tool.Name}: about {tool.Tokens} tokens")
            .ToList();

        // Assert
        over.Should().BeEmpty(
            "no tool object may cost more than {0} tokens of tools/list: shorten its description or parameters, or split what it does",
            ToolSchemaTokenCap);
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
        var textTokens = ToolSurface.EstimatedTokens(text.Sum(t => Encoding.UTF8.GetByteCount(t)));

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
