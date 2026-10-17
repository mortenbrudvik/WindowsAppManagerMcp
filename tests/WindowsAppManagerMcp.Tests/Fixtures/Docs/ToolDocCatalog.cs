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
            new("find_windows", "Find windows by criteria", "titleContains", "titleEquals", "titleRegex", "processName", "processId", "className", "sort", "limit"),
            new("get_all_windows", "Deprecated: use find_windows (toolset deprecated, removed in 3.0.0)", "includeMinimized"),
            new("get_foreground_window", "Get focused window"),
            new("get_owned_windows", "Dialogs and popups a window owns; which one blocks it", "handle", "includeHidden"),
            new("get_window_info", "One window, without listing all; full adds foreground, z-order, topmost, enabled, responding, DPI", "window (or handle)", "detail"),
            new("wait_for_window", "Wait until a window exists, is gone, in front, visible, retitled or input-idle", "window", "condition", "excludeHandles", "timeoutMs"),
        ],
        SkillNote: "Every tool that takes a window's `handle` (or `windowHandle`) also takes `window`, a selector with the filters of " +
                   "`find_windows` (`titleContains`, `titleEquals`, `titleRegex`, `processName`, `processId`, `className`, `foreground`, " +
                   "`index`, ...). It must name exactly one window: several matches fail with `AmbiguousWindow` and up to 5 `candidates`, " +
                   "none with `WindowNotFound`. `window.handle` with `processId` or a title refuses a reused handle (`HandleMismatch`)."),
        new("Window Control",
        [
            new("move_window", "Deprecated: use set_window_bounds with x and y (toolset deprecated, removed in 3.0.0)", "window (or handle)", "x", "y"),
            new("resize_window", "Deprecated: use set_window_bounds with width and height (toolset deprecated, removed in 3.0.0)", "window (or handle)", "width", "height"),
            new("set_window_bounds", "Move, resize, or both (x and y, width and height, or all four)", "window (or handle)", "x", "y", "width", "height"),
            new("set_window_state", "Min/max/restore", "window (or handle)", "state"),
            new("focus_window", "Bring to foreground, verified; reports what is in front", "window (or handle)"),
            new("close_window", "Close gracefully and verify: closed, hidden, stillOpen or promptShown", "window (or handle)", "waitMs", "force"),
            new("snap_window", "Snap to preset position", "window (or handle)", "position", "monitorIndex"),
            new("move_window_to_monitor", "Move to monitor", "window (or handle)", "monitorIndex", "positioning"),
        ]),
        new("Batch Operations",
        [
            new("set_windows_bounds_batch", "Move/resize multiple windows (toolset windows-batch)", "placements"),
            new("snap_windows_batch", "Snap multiple windows (toolset windows-batch)", "placements"),
            new("set_windows_state_batch", "Set state of multiple windows (toolset windows-batch)", "changes"),
            new("close_windows_batch", "Close multiple windows and verify each", "windows", "waitMs", "confirm (more than 3 windows)", "force"),
            new("launch_applications_batch", "Launch multiple applications", "applications", "waitForWindows"),
        ]),
        new("Monitor Info",
        [
            new("get_monitors", "List all displays, and the virtual screen's bounds"),
            new("get_primary_monitor", "Deprecated: use get_monitors, the monitor with isPrimary (toolset deprecated, removed in 3.0.0)"),
        ]),
        new("Screenshot",
        [
            new("list_screens", "Deprecated: use get_monitors (toolset deprecated, removed in 3.0.0)"),
            new("take_screenshot", "Capture a monitor", "monitorIndex", "maxDimension", "output"),
            new("capture_region", "Capture screen region", "x", "y", "width", "height", "maxDimension", "output"),
            new("capture_window", "Capture a window, or crop it to one element", "handle", "ref", "padding", "includeFrame", "maxDimension", "output"),
            new("capture_with_elements", "Capture with numbered marks (element refs) and a legend", "handle", "maxElements", "controlTypes", "detail", "maxDimension"),
        ],
        SkillNote: "All capture tools also accept `format` (`auto`, `png`, `jpeg`), `quality` (JPEG), `outputPath` and `overwrite` " +
                   "(with output `file` or `both`), and the legacy `saveToFile` and `maxWidth`."),
        new("UI Automation",
        [
            new("get_desktop_state", "One call to get oriented: window in front, windows, focus, popups, the window's elements with refs; optional capture", "window (or handle)", "include", "maxElements", "scope"),
            new("get_ui_elements", "Get UI element tree", "handle", "maxDepth", "controlTypes", "timeoutMs"),
            new("find_ui_elements", "Find elements by name, automationId, controlType or className; refs, click points", "window (or handle)", "name", "nameMatch", "automationId", "controlType", "withinRef", "scope", "detail"),
            new("element_action", "Act on an element through UI Automation patterns (also a menu path); state before and after", "action", "ref (or window + criteria)", "value", "mode", "direction", "fallback", "observe"),
            new("respond_to_dialog", "Read a dialog's text and buttons and press one, by standard name (yes, no, ok...) in any language", "button", "window (or handle)", "timeoutMs"),
            new("file_dialog", "Complete an Open or Save As dialog with a full path; answers the replace prompt by overwrite", "path", "action", "overwrite", "window (or handle)"),
            new("get_focused_element", "Element with keyboard focus, its window and process", "timeoutMs"),
            new("get_element_at_point", "Element at a screen point, its ancestors and window", "x", "y", "ancestors"),
            new("get_active_popups", "Open menus, dropdowns, flyouts and dialogs of an app, with refs", "window (or handle)", "timeoutMs"),
            new("read_element_text", "An element's text a page at a time (document, value or name); never a password", "ref", "window (with criteria)", "offset", "maxLength"),
            new("read_table", "A table's or list's headers and cells a page of rows at a time", "ref", "startRow", "maxRows", "maxColumns"),
            new("wait_for_element", "Wait until an element exists, is gone, enabled, visible, focused, or has a name, value or toggle state", "window", "ref", "name", "automationId", "controlType", "scope", "condition", "expected", "timeoutMs"),
        ]),
        new("Browser Reading",
        [
            new("get_browser_url", "URL of a browser window (`window: {\"foreground\": true}` for the one in front)", "handle", "window", "cdpPort", "requireCdp"),
            new("get_active_browser_url", "Deprecated: use get_browser_url with `window: {\"foreground\": true}` (toolset deprecated, removed in 3.0.0)", "cdpPort", "requireCdp"),
            new("get_browser_content", "Text of the page (or HTML through CDP), a page at a time", "handle", "maxLength", "offset", "textOnly", "cdpPort", "requireCdp"),
            new("launch_browser_with_debug", "Start Chrome, Edge or Brave with a DevTools port", "browser", "url", "port", "userDataDir", "profile", "reuseIfRunning"),
            new("close_debug_browser", "Close a debug browser this server started, and delete its temporary profile", "processId", "cdpPort", "deleteProfile", "confirm"),
        ]),
        new("Browser Tabs and Navigation",
        [
            new("list_tabs", "A debug browser's tabs (ids, titles, URLs), the window's tabs and the one it shows", "window (or handle)", "cdpPort"),
            new("activate_tab", "Show a tab in its window (toolset browser-actions)", "targetId", "window (or handle)", "cdpPort"),
            new("browser_navigate", "Open an http(s) URL in a tab or a new tab and wait for its load (toolset browser-actions)", "url", "window (or handle)", "targetId", "newTab", "waitUntil", "timeoutMs", "dialog"),
            new("wait_for_browser", "Wait until a page's URL, title or text contains a string, or a CSS selector matches", "window (or handle)", "targetId", "urlContains", "textContains", "selector", "timeoutMs"),
        ],
        SkillNote: "CDP only: a browser started with launch_browser_with_debug (or with its own debug port). activate_tab and " +
                   "browser_navigate are in the `browser-actions` toolset, off by default (`WAM_TOOLSETS=+browser-actions`)."),
        new("Browser Page (DOM Bridge)",
        [
            new("browser_element_rect", "Find a page element; its rect and click point in screen pixels (toolset browser-actions)", "selector", "text", "role", "name", "index", "window (or handle)"),
            new("browser_click", "Click a page element with CDP mouse events, no real cursor (toolset browser-actions)", "selector", "text", "role", "name", "index", "dialog", "window (or handle)", "targetId"),
            new("browser_type", "Type into a page's focused field or a selector's (toolset browser-actions)", "text", "selector", "replace", "pressEnter", "dialog", "window (or handle)"),
            new("browser_handle_dialog", "Read, accept or dismiss a Chrome or Edge window's JavaScript dialog, through UI Automation (toolset browser-actions)", "accept", "promptText", "window (or handle)"),
            new("browser_screenshot", "Capture a tab's page as an image, without the browser frame (toolset browser-actions)", "window (or handle)", "targetId", "maxDimension"),
            new("browser_evaluate", "Run a JavaScript expression in a page; its value or what it threw (toolset browser-script)", "expression", "awaitPromise", "timeoutMs", "window (or handle)"),
        ],
        SkillNote: "CDP only, on a debug browser, no real mouse or keyboard: work on a window behind others without taking the " +
                   "foreground (browser_screenshot may bring a tab that is not shown, and its window, to the front). A JavaScript " +
                   "dialog: pass `dialog` (accept or dismiss) to the action that opens it; one already open is read and answered " +
                   "with browser_handle_dialog, through UI Automation (DevTools cannot answer it). Off by default: " +
                   "`WAM_TOOLSETS=+browser-actions`, and `+browser-script` for browser_evaluate (a script runs with the page's " +
                   "rights, so clicks and typing do not turn it on). For anything richer, attach Playwright MCP to the same browser: `--cdp-endpoint` set to launch_browser_with_debug's " +
                   "`browserWsEndpoint` (or `cdpEndpoint`)."),
        new("Input Simulation",
        [
            new("click", "Deprecated: use mouse_click (toolset deprecated, removed in 3.0.0)", "x", "y", "windowHandle", "captureId (x, y on its image)"),
            new("right_click", "Deprecated: use mouse_click with button `secondary` (toolset deprecated, removed in 3.0.0)", "x", "y", "windowHandle", "captureId"),
            new("double_click", "Deprecated: use mouse_click with clickCount 2 (toolset deprecated, removed in 3.0.0)", "x", "y", "delayMs", "windowHandle", "captureId"),
            new("mouse_click", "Any button, 1-3 clicks, with modifier keys held (ctrl+click, middle, triple, x1/x2)", "x", "y", "button", "clickCount", "modifiers", "windowHandle", "captureId"),
            new("drag", "Press, move and release: drag and drop, sliders, selections; nothing stays held", "fromX", "fromY", "toX", "toY", "button", "modifiers", "windowHandle", "toWindowHandle"),
            new("mouse_move", "Move cursor (no click)", "x", "y", "windowHandle", "captureId"),
            new("scroll", "Turn the mouse wheel over the named window", "windowHandle", "deltaY", "deltaX", "x", "y"),
            new("type_text", "Type text into the named window", "text", "windowHandle", "delayMs"),
            new("send_keys", "Send keys to the named window", "keys (e.g., `^c`, `{ENTER}`)", "windowHandle"),
            new("press_keys", "Press key chords by name (ctrl+shift+t) or sequences (tab*3 enter); nothing stays held", "keys", "repeat", "holdMs", "delayBetweenMs", "windowHandle"),
        ],
        SkillNote: "Click with mouse_click: click, right_click and double_click are deprecated, served only with " +
                   "`WAM_TOOLSETS=+deprecated` until 3.0.0 removes them. " +
                   "All ten also take `allowHostTarget` (default false): only for input to your own host window, named as " +
                   "`windowHandle`, when the user asked for it; and `releaseHeldModifiers` (default false): only for a stuck " +
                   "modifier key (see `ModifiersHeld`)."),
        new("Input Control",
        [
            new("get_control_status", "Is input paused by the user, is the pause hotkey registered, which keys are down"),
            new("resume_input", "Ask the user to resume input after a pause; only their confirmation resumes it"),
            new("release_all_input", "Release stuck keys and mouse buttons", "releaseHeldModifiers"),
        ]),
        new("Step Sequences",
        [
            new("run_steps", "Several steps on one window in one call, under one input gate hold; stops at the first failure",
                "window", "steps (tool, args, id, optional, return)", "maxDurationMs"),
        ],
        SkillNote: "A step is a tool of the list with that tool's own `args`. A step that names no window gets the run's; " +
                   "`\"$steps.<id>.<path>\"` takes an earlier step's ref, handle, captureId or clickPoint. No paste, file output, " +
                   "window_close, scope desktop, allowHostTarget or closing chord (ctrl+w, ctrl+q) in a step. Off with `WAM_TOOLSETS=-steps`."),
        new("App Launch",
        [
            new("launch_application", "Launch app by path/name/URI", "executable", "arguments", "workingDirectory", "waitForWindow", "placement", "reuseExisting"),
            new("list_processes", "List running processes", "nameFilter", "includeWindowless"),
            new("kill_process", "Close or terminate a process", "processId", "confirm", "forceKill", "expectedProcessName"),
            new("close_application", "Close an app's Alt+Tab windows gracefully; reports prompts and what remains", "processName", "processId", "window (or handle)", "timeoutMs"),
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
