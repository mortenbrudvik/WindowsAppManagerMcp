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
/// A tool that moved out of a toolset into another one (roadmap M6.1: into the opt-in windows-batch and deprecated
/// toolsets). It is served only while the toolset it came from is on too, so turning its new toolset on never serves a
/// configuration more than it served before the move, and excluding the toolset it came from by name excludes it too.
/// </summary>
/// <param name="Tool">The tool, by name.</param>
/// <param name="FormerToolset">The toolset it was in.</param>
/// <param name="Replacement">What to call instead, as get_server_info names it ("mouse_click with button 'secondary'"),
/// starting with the replacing tool's name; null when nothing replaces it (it only moved).</param>
public sealed record MovedTool(string Tool, string FormerToolset, string? Replacement = null);

/// <summary>
/// The toolsets of a server: every tool belongs to at most one, and one toolset is always on (it reports the server's
/// state and the user's control over it, which an agent needs whatever else is turned off). One tool of it can be
/// always served: the tool that tells the agent which tools are off and why, which WAM_EXCLUDE_TOOLS cannot drop.
/// </summary>
public sealed class ToolsetCatalog
{
    private readonly Dictionary<string, Toolset> _byTool = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, MovedTool> _moves = new(StringComparer.OrdinalIgnoreCase);

    public ToolsetCatalog(string alwaysOn, IReadOnlyList<Toolset> toolsets, string? alwaysServedTool = null, IReadOnlyList<MovedTool>? moved = null)
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

        var movedTools = new List<MovedTool>();
        foreach (var move in moved ?? [])
        {
            if (ToolsetOf(move.Tool) is not { } toolset)
            {
                throw new ArgumentException($"The moved tool '{move.Tool}' is in no toolset.", nameof(moved));
            }

            if (Find(move.FormerToolset) is not { } former || former.Name == toolset.Name)
            {
                throw new ArgumentException(
                    $"The moved tool '{move.Tool}' must come from another toolset of the catalog, not '{move.FormerToolset}'.", nameof(moved));
            }

            var name = _byTool.Keys.First(t => string.Equals(t, move.Tool, StringComparison.OrdinalIgnoreCase));
            var normalized = move with { Tool = name, FormerToolset = former.Name };
            if (!_moves.TryAdd(name, normalized))
            {
                throw new ArgumentException($"The tool '{move.Tool}' moved twice.", nameof(moved));
            }

            movedTools.Add(normalized);
        }

        MovedTools = movedTools;
    }

    /// <summary>The tools that moved out of a toolset, in the order the catalog lists the moves.</summary>
    public IReadOnlyList<MovedTool> MovedTools { get; }

    /// <summary>The move of a tool that moved out of a toolset, or null.</summary>
    public MovedTool? MoveOf(string toolName) => _moves.GetValueOrDefault(toolName);

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
/// M5.5, M5.8), browser-script (roadmap M5.8), windows-batch and deprecated (roadmap M6.1). Of the tools M6 adds, the niche
/// or heavy ones come in toolsets that are off by default (ocr, visual, windows-batch), and the small everyday steps
/// (list_installed_apps, set_window_zorder, run_steps) join toolsets that are on (owner decision D2, October 2026):
/// run_steps in a toolset of its own, steps (roadmap M6.2), so WAM_TOOLSETS=-steps drops it alone.
/// </summary>
public static class Toolsets
{
    /// <summary>The always-on toolset.</summary>
    public const string ServerToolset = "server";

    /// <summary>The opt-in toolset of the tools roadmap M6.1 replaced, until 3.0.0 removes them.</summary>
    public const string DeprecatedToolset = "deprecated";

    public static ToolsetCatalog Server { get; } = new(
        ServerToolset,
        [
            new(ServerToolset, OnByDefault: true,
                "The server's build, environment and toolsets, and the user's control over its input: whether input is " +
                "paused, resuming it with the user's consent, releasing stuck keys.",
                ["get_server_info", "get_control_status", "resume_input", "release_all_input"]),
            new("windows", OnByDefault: true,
                "Find, wait for, focus, move, resize, snap, minimize, maximize and close windows, and close several at once.",
                [
                    "find_windows", "get_foreground_window", "focus_window", "set_window_bounds", "set_window_state", "snap_window",
                    "move_window_to_monitor", "close_window", "close_windows_batch", "get_owned_windows", "get_window_info",
                    "wait_for_window"
                ]),
            new("monitors", OnByDefault: true,
                "The monitors: bounds, work areas, scale factors, and the virtual screen they make up.",
                ["get_monitors"]),
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
                ["mouse_click", "drag", "mouse_move", "scroll", "type_text", "send_keys", "press_keys", "element_action",
                    "respond_to_dialog", "file_dialog"]),
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
            // Roadmap M6.2: a run's steps call the tools of the toolsets above, and only those the server serves
            new("steps", OnByDefault: true,
                "Run several steps (element actions, keys, clicks, waits) on one window in one call, under one input gate hold, " +
                "stopping at the first failure.",
                ["run_steps"]),
            new("browser", OnByDefault: true,
                "Read the URL and text of browser pages, list a debug browser's tabs and wait for a page; start and close a browser with a DevTools port.",
                ["get_browser_url", "get_browser_content", "launch_browser_with_debug", "close_debug_browser", "list_tabs", "wait_for_browser"]),
            // Off by default (roadmap M2.14, M5.5, M5.8): they change what a browser shows, through its DevTools port
            new("browser-actions", OnByDefault: false,
                "Act on a debug browser's pages through CDP: show and navigate tabs (with a load wait), click and type in a page " +
                "without the real mouse or keyboard, answer its JavaScript dialogs, place its elements on the screen, capture it.",
                ["activate_tab", "browser_navigate", "browser_element_rect", "browser_click", "browser_type", "browser_handle_dialog",
                    "browser_screenshot"]),
            // Off by default, apart from browser-actions (roadmap M5.8): a script runs with the page's rights (cookies scripts
            // can read, storage, requests with the user's sign-in) and leaves no trace on the screen, so turning on clicks and
            // typing does not turn it on. browser_evaluate is also Destructive, refused while paused and audited with lengths only
            new("browser-script", OnByDefault: false,
                "Run JavaScript in a debug browser's page through CDP, with the page's rights.",
                ["browser_evaluate"]),
            // Off by default (roadmap M6.1, owner decision D4): each item takes its own placement or state, so they stay tools
            // of their own, but most sessions place one window at a time. Closing and launching several stay in the default
            new("windows-batch", OnByDefault: false,
                "Place, snap, or minimize, maximize and restore several windows in one call, each with its own placement or state.",
                ["set_windows_bounds_batch", "snap_windows_batch", "set_windows_state_batch"]),
            // Off by default (roadmap M6.1, owner decision D1): the tools another tool replaced in 2.2.0, served unchanged until
            // 3.0.0 removes them. Their descriptions are one line that names the replacement; a call is never forwarded to it
            new(DeprecatedToolset, OnByDefault: false,
                "Tools replaced in 2.2.0 and removed in 3.0.0, unchanged until then and each served only with the toolset it came " +
                "from: use mouse_click, set_window_bounds, find_windows, get_monitors and get_browser_url instead.",
                ["click", "right_click", "double_click", "move_window", "resize_window", "get_all_windows", "get_primary_monitor",
                    "list_screens", "get_active_browser_url"]),
        ],
        // It reports the toolsets in force and why a tool is missing, and the server instructions send the agent to it
        alwaysServedTool: "get_server_info",
        // Roadmap M6.1: each is served only while the toolset it came from is on too, so 'all' and '+deprecated' never serve a
        // configuration a tool it did not serve before 2.2.0 (all,-input serves no click), and WAM_EXCLUDE_TOOLS=input
        // still excludes click; get_server_info says where each went while it is off
        moved:
        [
            new("set_windows_bounds_batch", "windows"),
            new("snap_windows_batch", "windows"),
            new("set_windows_state_batch", "windows"),
            new("click", "input", "mouse_click"),
            new("right_click", "input", "mouse_click with button 'secondary'"),
            new("double_click", "input", "mouse_click with clickCount 2"),
            new("move_window", "windows", "set_window_bounds with x and y"),
            new("resize_window", "windows", "set_window_bounds with width and height"),
            new("get_all_windows", "windows", "find_windows"),
            new("get_primary_monitor", "monitors", "get_monitors"),
            new("list_screens", "monitors", "get_monitors"),
            new("get_active_browser_url", "browser", "get_browser_url with window {\"foreground\": true}"),
        ]);

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
        // The instructions name tools of the loop (focus_window, mouse_click, ...); say which the user turned off
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
