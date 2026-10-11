namespace WindowsAppManagerMcp.Tests.Fixtures;

/// <summary>A tool in the generated docs: its one-line summary and the parameters SKILL.md names.</summary>
/// <param name="Name">The tool name, as tools/list sends it.</param>
/// <param name="Summary">The short description in SKILL.md's tool tables.</param>
/// <param name="KeyParameters">
/// The parameters SKILL.md lists, each a real parameter name, optionally followed by a note in parentheses
/// (for example "confirm (more than 3 windows)"). Empty for "(none)".
/// </param>
public sealed record ToolDoc(string Name, string Summary, params string[] KeyParameters);

/// <summary>A category of tools: a row of the README and TOOLS.md tables and a table of SKILL.md.</summary>
/// <param name="Name">The category name, also the '## ' heading of its section in docs/TOOLS.md.</param>
/// <param name="Tools">The tools, in the order of their '### ' sections in docs/TOOLS.md.</param>
/// <param name="SkillNote">Markdown printed under the category's table in SKILL.md, or null.</param>
public sealed record ToolDocCategory(string Name, IReadOnlyList<ToolDoc> Tools, string? SkillNote = null)
{
    /// <summary>The GitHub anchor of the category's '## ' heading.</summary>
    public string Anchor => Name.ToLowerInvariant().Replace(' ', '-');
}

/// <summary>
/// What the generated tool tables need beyond tools/list: each tool's category, its one-line summary and the
/// key parameters SKILL.md names. Everything else in the tables (tool names, counts, parameter names, types,
/// required flags and defaults) comes from tools/list. GeneratedToolDocsTests fails when a served tool is missing
/// here or a key parameter is not a real parameter; a new tool needs an entry before the docs can be generated.
/// </summary>
public static class ToolDocCatalog
{
    public static IReadOnlyList<ToolDocCategory> Categories { get; } =
    [
        new("Window Discovery",
        [
            new("find_windows", "Find windows by criteria", "titleContains", "processName", "processId", "handle", "className"),
            new("get_all_windows", "List user-facing app windows", "includeMinimized"),
            new("get_foreground_window", "Get focused window"),
            new("get_owned_windows", "Dialogs and popups a window owns; which one blocks it", "handle", "includeHidden"),
        ]),
        new("Window Control",
        [
            new("move_window", "Move to x,y position", "handle", "x", "y"),
            new("resize_window", "Change dimensions", "handle", "width", "height"),
            new("set_window_bounds", "Move + resize combined", "handle", "x", "y", "width", "height"),
            new("set_window_state", "Min/max/restore", "handle", "state"),
            new("focus_window", "Bring to foreground, verified; reports what is in front", "handle"),
            new("close_window", "Close gracefully and verify: closed, hidden, stillOpen or promptShown", "handle", "waitMs", "force"),
            new("snap_window", "Snap to preset position", "handle", "position", "monitorIndex"),
            new("move_window_to_monitor", "Move to monitor", "handle", "monitorIndex", "positioning"),
        ]),
        new("Batch Operations",
        [
            new("set_windows_bounds_batch", "Move/resize multiple windows", "placements"),
            new("snap_windows_batch", "Snap multiple windows", "placements"),
            new("set_windows_state_batch", "Set state of multiple windows", "changes"),
            new("close_windows_batch", "Close multiple windows and verify each", "windows", "waitMs", "confirm (more than 3 windows)", "force"),
            new("launch_applications_batch", "Launch multiple applications", "applications", "waitForWindows"),
        ]),
        new("Monitor Info",
        [
            new("get_monitors", "List all displays"),
            new("get_primary_monitor", "Get primary display"),
        ]),
        new("Screenshot",
        [
            new("list_screens", "List screens for capture"),
            new("take_screenshot", "Capture a monitor", "monitorIndex", "maxDimension", "output"),
            new("capture_region", "Capture screen region", "x", "y", "width", "height", "maxDimension", "output"),
            new("capture_window", "Capture a window", "handle", "includeFrame", "maxDimension", "output"),
            new("capture_with_elements", "Capture with UI overlays", "handle", "maxDepth", "maxDimension", "output"),
        ],
        SkillNote: "All capture tools also accept `format` (`auto`, `png`, `jpeg`), `quality` (JPEG), `outputPath` and `overwrite` " +
                   "(with output `file` or `both`), and the legacy `saveToFile` and `maxWidth`."),
        new("UI Automation",
        [
            new("get_ui_elements", "Get UI element tree", "handle", "maxDepth", "controlTypes", "timeoutMs"),
            new("get_focused_element", "Element with keyboard focus, its window and process", "timeoutMs"),
            new("get_element_at_point", "Element at a screen point, its ancestors and window", "x", "y", "ancestors"),
        ]),
        new("Browser Reading",
        [
            new("get_browser_url", "URL of a browser window", "handle", "cdpPort", "requireCdp"),
            new("get_active_browser_url", "URL of the foreground browser window", "cdpPort", "requireCdp"),
            new("get_browser_content", "Text of the page (or HTML through CDP)", "handle", "maxLength", "textOnly", "cdpPort", "requireCdp"),
            new("launch_browser_with_debug", "Start Chrome, Edge or Brave with a DevTools port", "browser", "url", "port", "userDataDir"),
        ]),
        new("Input Simulation",
        [
            new("click", "Left click at coordinates, on the named window", "x", "y", "windowHandle"),
            new("right_click", "Right click at coordinates", "x", "y", "windowHandle"),
            new("double_click", "Double left click", "x", "y", "delayMs", "windowHandle"),
            new("mouse_move", "Move cursor (no click)", "x", "y", "windowHandle"),
            new("scroll", "Turn the mouse wheel over the named window", "windowHandle", "deltaY", "deltaX", "x", "y"),
            new("type_text", "Type text into the named window", "text", "windowHandle", "delayMs"),
            new("send_keys", "Send keys to the named window", "keys (e.g., `^c`, `{ENTER}`)", "windowHandle"),
        ],
        SkillNote: "All seven also take `allowHostTarget` (default false): only for input to your own host window, named as " +
                   "`windowHandle`, when the user asked for it; and `releaseHeldModifiers` (default false): only for a stuck " +
                   "modifier key (see `ModifiersHeld`)."),
        new("Input Control",
        [
            new("get_control_status", "Is input paused by the user, is the pause hotkey registered, which keys are down"),
            new("resume_input", "Ask the user to resume input after a pause; only their confirmation resumes it"),
            new("release_all_input", "Release stuck keys and mouse buttons", "releaseHeldModifiers"),
        ]),
        new("App Launch",
        [
            new("launch_application", "Launch app by path/name/URI", "executable", "arguments", "workingDirectory", "waitForWindow"),
            new("list_processes", "List running processes", "nameFilter", "includeWindowless"),
            new("kill_process", "Close or terminate a process", "processId", "confirm", "forceKill", "expectedProcessName"),
            new("close_application", "Close an app's Alt+Tab windows gracefully; reports prompts and what remains", "processName", "processId", "handle", "timeoutMs"),
        ]),
        new("Clipboard",
        [
            new("get_clipboard", "Read what the agent copied (text, html, files); the user's clipboard only with consent", "format", "maxLength"),
            new("set_clipboard", "Put text on the clipboard, kept out of clipboard history", "text"),
        ]),
        new("Layout Presets",
        [
            new("list_layouts", "List saved presets"),
            new("get_layout", "Get preset details", "name"),
            new("save_layout", "Save current arrangement", "name", "description", "includeProcesses", "excludeProcesses"),
            new("apply_layout", "Restore saved preset", "name", "matchBy", "launchMissing"),
            new("delete_layout", "Delete a preset", "name"),
        ]),
        new("Server Info",
        [
            new("get_server_info", "Build, DPI awareness, desktop state, host, pause, policy and toolsets; warnings first"),
        ]),
    ];

    /// <summary>Every tool of the catalog, in category order.</summary>
    public static IEnumerable<ToolDoc> AllTools => Categories.SelectMany(c => c.Tools);

    /// <summary>The parameter name of a key parameter entry: the text before a note in parentheses.</summary>
    public static string ParameterName(string keyParameter)
    {
        var note = keyParameter.IndexOf(" (", StringComparison.Ordinal);
        return note < 0 ? keyParameter : keyParameter[..note];
    }
}
