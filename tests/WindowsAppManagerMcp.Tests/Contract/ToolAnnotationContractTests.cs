using System.Text.Json.Nodes;
using WindowsAppManagerMcp.Infrastructure;

namespace WindowsAppManagerMcp.Tests.Contract;

/// <summary>
/// Contract: every tool tells the client what it does (roadmap M2.10). tools/list gives each tool a title and all four
/// hints, set on purpose rather than left to the protocol's defaults (which call every tool destructive and open-world):
/// clients auto-approve or parallelize read-only tools and ask before destructive ones, so a wrong or missing hint
/// either nags the user or lets a click through unasked.
/// </summary>
public class ToolAnnotationContractTests
{
    private static readonly string[] Hints = ["readOnlyHint", "destructiveHint", "idempotentHint", "openWorldHint"];

    public static TheoryData<string> ServedTools()
    {
        var data = new TheoryData<string>();
        foreach (var name in ServerToolCatalog.ServedToolNames.Order(StringComparer.Ordinal))
        {
            data.Add(name);
        }

        return data;
    }

    private static async Task<JsonObject> ToolAsync(string name) => ToolSurface.Tool(await ToolSurface.ListToolsAsync(), name);

    private static bool Hint(JsonObject tool, string hint) => tool["annotations"]![hint]!.GetValue<bool>();

    [Theory]
    [MemberData(nameof(ServedTools))]
    public async Task EveryTool_HasATitle_AndSetsAllFourHints(string name)
    {
        // Act
        var tool = await ToolAsync(name);

        // Assert
        var title = tool["title"]?.GetValue<string>();
        title.Should().NotBeNullOrWhiteSpace("'{0}' needs a Title for the client to show", name);
        tool["annotations"]?["title"]?.GetValue<string>().Should().Be(title);
        foreach (var hint in Hints)
        {
            tool["annotations"]?[hint].Should().NotBeNull("'{0}' must set {1} explicitly on its [McpServerTool]", name, hint);
        }
    }

    [Fact]
    public async Task Titles_AreUnique()
    {
        var tools = await ToolSurface.ListToolsAsync();

        tools.Select(t => t!["title"]!.GetValue<string>()).Should().OnlyHaveUniqueItems();
    }

    [Theory]
    [MemberData(nameof(ServedTools))]
    public async Task AReadOnlyTool_IsNeitherDestructiveNorGated_AndCanBeRepeated(string name)
    {
        // Arrange
        var tool = await ToolAsync(name);
        if (!Hint(tool, "readOnlyHint"))
        {
            return;
        }

        // Assert - a client may run read-only tools in parallel and without asking
        Hint(tool, "destructiveHint").Should().BeFalse();
        Hint(tool, "idempotentHint").Should().BeTrue();
        InputGateToolFilter.ReadOnlyTools.Should().Contain(name, "a read-only tool runs without the input gate");
    }

    [Fact]
    public async Task TheToolsThatCanLoseWorkOrActForTheUser_AreDestructive()
    {
        // close*, kill, the layout tools that remove or replace a preset, the input that can click or type anything (scroll:
        // the wheel over a combo box, slider or number box changes its value), the launches (any program with any arguments,
        // a shell command included; launch_browser_with_debug opens an unauthenticated debug port on a profile directory
        // the agent chooses), the captures (overwrite:true replaces the image file at outputPath) and set_clipboard (it
        // replaces what the user copied)
        string[] destructive =
        [
            "close_window", "close_windows_batch", "close_application", "kill_process", "delete_layout", "save_layout",
            "click", "right_click", "double_click", "scroll", "type_text", "send_keys",
            "launch_application", "launch_applications_batch", "launch_browser_with_debug",
            "take_screenshot", "capture_region", "capture_window", "capture_with_elements",
            "set_clipboard"
        ];

        var tools = await ToolSurface.ListToolsAsync();

        tools.Where(t => t!["annotations"]!["destructiveHint"]!.GetValue<bool>()).Select(t => t!["name"]!.GetValue<string>())
            .Should().BeEquivalentTo(destructive);
    }

    [Fact]
    public async Task TheToolsThatChangeNothing_AreReadOnly_AndTheCapturesAreNot()
    {
        var tools = await ToolSurface.ListToolsAsync();
        var readOnly = tools.Where(t => t!["annotations"]!["readOnlyHint"]!.GetValue<bool>())
            .Select(t => t!["name"]!.GetValue<string>()).ToList();

        // Every find/get/list tool, the browser reads, the control status and the clipboard read (it may ask the user, which
        // changes nothing)
        readOnly.Should().BeEquivalentTo(
        [
            "find_windows", "get_all_windows", "get_foreground_window", "get_monitors", "get_primary_monitor", "list_screens",
            "get_ui_elements", "get_focused_element", "get_element_at_point", "get_browser_url", "get_active_browser_url",
            "get_browser_content", "list_processes", "list_layouts", "get_layout", "get_control_status", "get_server_info",
            "get_owned_windows", "get_clipboard", "get_window_info", "wait_for_window", "find_ui_elements", "wait_for_element"
        ]);

        // The captures can write image files (output 'file' or 'both'), so they are not read-only
        foreach (var capture in new[] { "take_screenshot", "capture_region", "capture_window", "capture_with_elements" })
        {
            readOnly.Should().NotContain(capture);
        }
    }

    [Fact]
    public async Task TheToolsThatStartPrograms_AndThePageContent_AreOpenWorld()
    {
        var tools = await ToolSurface.ListToolsAsync();

        // They start any program or open any URI (apply_layout with launchMissing runs a preset's launch commands);
        // get_browser_content returns what a website wrote
        tools.Where(t => t!["annotations"]!["openWorldHint"]!.GetValue<bool>()).Select(t => t!["name"]!.GetValue<string>())
            .Should().BeEquivalentTo(
                ["launch_application", "launch_applications_batch", "launch_browser_with_debug", "apply_layout", "get_browser_content"]);
    }

    [Theory]
    [InlineData("launch_application")]
    [InlineData("launch_applications_batch")]
    [InlineData("launch_browser_with_debug")]
    [InlineData("apply_layout")]
    public async Task TheToolsThatCanStartPrograms_AreNotIdempotent(string name)
    {
        // A second call can start a second instance: apply_layout with launchMissing launches again when the window it
        // launched the first time is not matched yet (still opening, or titled differently than the preset)
        Hint(await ToolAsync(name), "idempotentHint").Should().BeFalse();
    }
}
