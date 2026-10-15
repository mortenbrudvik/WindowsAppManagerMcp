using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Server;

namespace WindowsAppManagerMcp.Infrastructure;

/// <summary>A named group of tools that the user turns on or off with WAM_TOOLSETS (roadmap M2.14).</summary>
/// <param name="Name">The name WAM_TOOLSETS uses, lower case.</param>
/// <param name="OnByDefault">True when the toolset is served without WAM_TOOLSETS (it is part of "default").</param>
/// <param name="Description">What the toolset's tools do, for the docs and the log.</param>
/// <param name="Tools">The tools, by name.</param>
public sealed record Toolset(string Name, bool OnByDefault, string Description, IReadOnlyList<string> Tools);

/// <summary>
/// The toolsets of a server: every tool belongs to at most one, and one toolset is always on (it reports the server's
/// state and the user's control over it, which an agent needs whatever else is turned off). One tool of it can be
/// always served: the tool that tells the agent which tools are off and why, which WAM_EXCLUDE_TOOLS cannot drop.
/// </summary>
public sealed class ToolsetCatalog
{
    private readonly Dictionary<string, Toolset> _byTool = new(StringComparer.OrdinalIgnoreCase);

    public ToolsetCatalog(string alwaysOn, IReadOnlyList<Toolset> toolsets, string? alwaysServedTool = null)
    {
        ArgumentNullException.ThrowIfNull(toolsets);
        if (toolsets.Select(t => t.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count() != toolsets.Count)
        {
            throw new ArgumentException("Toolset names must be unique.", nameof(toolsets));
        }

        foreach (var toolset in toolsets)
        {
            foreach (var tool in toolset.Tools)
            {
                if (!_byTool.TryAdd(tool, toolset))
                {
                    throw new ArgumentException($"The tool '{tool}' is in two toolsets: {_byTool[tool].Name} and {toolset.Name}.", nameof(toolsets));
                }
            }
        }

        Toolsets = toolsets;
        AlwaysOn = Find(alwaysOn)?.Name ?? throw new ArgumentException($"There is no toolset '{alwaysOn}'.", nameof(alwaysOn));
        if (!Find(alwaysOn)!.OnByDefault)
        {
            throw new ArgumentException($"The always-on toolset '{alwaysOn}' must be on by default.", nameof(alwaysOn));
        }

        if (alwaysServedTool != null && ToolsetOf(alwaysServedTool)?.Name != AlwaysOn)
        {
            throw new ArgumentException(
                $"The always-served tool '{alwaysServedTool}' must be a tool of the always-on toolset '{AlwaysOn}'.", nameof(alwaysServedTool));
        }

        AlwaysServedTool = alwaysServedTool == null ? null : _byTool.Keys.First(t => string.Equals(t, alwaysServedTool, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>The toolsets, in the order the docs and get_server_info list them.</summary>
    public IReadOnlyList<Toolset> Toolsets { get; }

    /// <summary>The toolset that is always served; WAM_TOOLSETS cannot turn it off (WAM_EXCLUDE_TOOLS can drop its tools).</summary>
    public string AlwaysOn { get; }

    /// <summary>
    /// The tool that is served whatever the variables say (WAM_EXCLUDE_TOOLS ignores it with a warning), or null. It must
    /// be a read-only tool of the always-on toolset.
    /// </summary>
    public string? AlwaysServedTool { get; }

    /// <summary>Every tool of every toolset.</summary>
    public IEnumerable<string> ToolNames => _byTool.Keys;

    /// <summary>The toolset with this name (any case), or null.</summary>
    public Toolset? Find(string name) =>
        Toolsets.FirstOrDefault(t => string.Equals(t.Name, name, StringComparison.OrdinalIgnoreCase));

    /// <summary>The toolset of a tool, or null when no toolset names it.</summary>
    public Toolset? ToolsetOf(string toolName) => _byTool.GetValueOrDefault(toolName);
}

/// <summary>
/// The server's toolsets (roadmap M2.14). Each tool the server serves is in exactly one of them (ToolsetCatalogTests),
/// and docs/TOOLS.md lists them in a generated region. Every toolset is on by default except browser-actions (roadmap
/// M5.5, M5.8); the tools that M3-M6 add for OCR, visual diffs, layouts v2 and workspaces come in toolsets that are off by default too.
/// </summary>
public static class Toolsets
{
    /// <summary>The always-on toolset.</summary>
    public const string ServerToolset = "server";

    public static ToolsetCatalog Server { get; } = new(
        ServerToolset,
        [
            new(ServerToolset, OnByDefault: true,
                "The server's build, environment and toolsets, and the user's control over its input: whether input is " +
                "paused, resuming it with the user's consent, releasing stuck keys.",
                ["get_server_info", "get_control_status", "resume_input", "release_all_input"]),
            new("windows", OnByDefault: true,
                "Find, wait for, focus, move, resize, snap, minimize, maximize and close windows, one at a time or in batches.",
                [
                    "find_windows", "get_all_windows", "get_foreground_window", "focus_window", "move_window", "resize_window",
                    "set_window_bounds", "set_window_state", "snap_window", "move_window_to_monitor", "close_window",
                    "set_windows_bounds_batch", "snap_windows_batch", "set_windows_state_batch", "close_windows_batch",
                    "get_owned_windows", "get_window_info", "wait_for_window"
                ]),
            new("monitors", OnByDefault: true,
                "The monitors: bounds, work areas, scale factors.",
                ["get_monitors", "get_primary_monitor", "list_screens"]),
            new("capture", OnByDefault: true,
                "Screenshots of monitors, regions and windows, as image content.",
                ["take_screenshot", "capture_region", "capture_window", "capture_with_elements"]),
            new("elements", OnByDefault: true,
                "UI Automation: one-call desktop observations, element trees, searches that return element refs (also in menus, " +
                "dropdowns and dialogs), waits for elements, the focused element, the element at a point, an app's open popups, " +
                "an element's text and a table's cells.",
                ["get_desktop_state", "get_ui_elements", "find_ui_elements", "get_focused_element", "get_element_at_point",
                    "get_active_popups", "read_element_text", "read_table", "wait_for_element"]),
            new("input", OnByDefault: true,
                "Guarded mouse and keyboard input to a named window (clicks of any button with modifiers, drags, key chords), and actions on UI " +
                "elements through UI Automation patterns: menu paths, answering dialogs, completing Open and Save As dialogs.",
                ["click", "right_click", "double_click", "mouse_click", "drag", "mouse_move", "scroll", "type_text", "send_keys",
                    "press_keys", "element_action", "respond_to_dialog", "file_dialog"]),
            new("clipboard", OnByDefault: true,
                "Read what the agent copied, put text on the clipboard to paste it; the user's own clipboard only with their consent.",
                ["get_clipboard", "set_clipboard"]),
            new("launch", OnByDefault: true,
                "Start applications, optionally waiting for their window.",
                ["launch_application", "launch_applications_batch"]),
            new("process", OnByDefault: true,
                "List processes and close or end one, or close an application's windows gracefully.",
                ["list_processes", "kill_process", "close_application"]),
            new("layouts", OnByDefault: true,
                "Save, apply and manage window layout presets.",
                ["list_layouts", "get_layout", "save_layout", "apply_layout", "delete_layout"]),
            new("browser", OnByDefault: true,
                "Read the URL and text of browser pages, list a debug browser's tabs and wait for a page; start and close a browser with a DevTools port.",
                ["get_browser_url", "get_active_browser_url", "get_browser_content", "launch_browser_with_debug", "close_debug_browser", "list_tabs", "wait_for_browser"]),
            // Off by default (roadmap M2.14, M5.5, M5.8): they change what a browser shows, through its DevTools port. A
            // script runs with the page's rights, so browser_evaluate is Destructive, refused while paused and audited with
            // lengths only, like the clicks and the typing
            new("browser-actions", OnByDefault: false,
                "Act on a debug browser's pages through CDP: show and navigate tabs (with a load wait), click and type in a page " +
                "without the real mouse or keyboard, answer its JavaScript dialogs, place its elements on the screen, capture it, " +
                "run a script in it.",
                ["activate_tab", "browser_navigate", "browser_element_rect", "browser_click", "browser_type", "browser_handle_dialog",
                    "browser_screenshot", "browser_evaluate"]),
        ],
        // It reports the toolsets in force and why a tool is missing, and the server instructions send the agent to it
        alwaysServedTool: "get_server_info");

    /// <summary>
    /// Serves only the tools <paramref name="selection"/> chooses: registers the selection (get_server_info reports it)
    /// and removes every other tool from the server's tool collection once the registrations are complete, so tools/list
    /// leaves them out and a call to one is an unknown tool. When the user turned off tools the default serves, the
    /// server instructions get one more line that says which (<see cref="ToolsetSelection.InstructionsNote"/>). Program.cs reads the selection from the environment before
    /// the tool registrations (<see cref="ServiceRegistration.AddWindowsAppManagerTools"/>).
    /// </summary>
    public static IMcpServerBuilder WithToolsets(this IMcpServerBuilder builder, ToolsetSelection selection)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(selection);

        builder.Services.AddSingleton(selection);
        // PostConfigure runs after the SDK's own options setup has collected every registered tool
        builder.Services.AddOptions<McpServerOptions>().PostConfigure(options => RemoveUnselected(options, selection));
        return builder;
    }

    private static void RemoveUnselected(McpServerOptions options, ToolsetSelection selection)
    {
        // The instructions name tools of the loop (focus_window, click, ...); say which the user turned off
        if (selection.InstructionsNote is { } note)
        {
            options.ServerInstructions = string.IsNullOrEmpty(options.ServerInstructions)
                ? note
                : options.ServerInstructions + "\n" + note;
        }

        if (options.ToolCollection is not { } tools)
        {
            return;
        }

        foreach (var tool in tools.ToArray())
        {
            if (!selection.Serves(tool.ProtocolTool.Name, tool.ProtocolTool.Annotations?.ReadOnlyHint == true))
            {
                tools.Remove(tool);
            }
        }
    }
}
