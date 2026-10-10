---
name: using-windows-app-manager
description: |
  Manage Windows applications, windows, and desktop layouts.
  Use when user asks to:
  - List, find, or show windows
  - Move, resize, snap, minimize, maximize, focus, or close windows
  - Show monitors or move windows between monitors
  - Take screenshots of screens, windows, or regions
  - Get UI elements or element coordinates from windows
  - Click, right-click, double-click, or move the mouse cursor
  - Type text or send keyboard shortcuts
  - Launch applications or open programs
  - List or kill processes
  - Save, apply, restore, or manage window layouts
---

# Using Windows App Manager

Windows App Manager provides 38 MCP tools for managing Windows applications, windows, and desktop layouts.

## Safety Rules (Read First)

The mouse and keyboard tools send real input to the user's desktop.

- **Keyboard input has no target window.** `type_text` and `send_keys` type into whatever window is in the foreground, and that is often your own terminal or chat window. Call `get_foreground_window` right before every `type_text` or `send_keys` call and check that its `handle` is the window you mean to type into. If it is not, call `focus_window` with the right handle and check again. If it still does not match, stop and do not type. Windows can refuse focus changes, so do not rely on `focus_window` alone.
- **Never send destructive key chords blind.** Examples: `%{F4}` (Alt+F4), `^w`, `^q`, `^a{DELETE}`, or `{ENTER}` on a confirmation dialog. In a terminal, typed text followed by `{ENTER}` runs as a shell command.
- **Close windows with `close_window`, not Alt+F4.** `close_window` and `close_windows_batch` send WM_CLOSE to the exact handle you pass. The app may still ask to save.
- **Clicks land on whatever is at the coordinates.** Before clicking, bring the target window to the front with `focus_window` and confirm with `get_foreground_window`. Take coordinates from a fresh `get_ui_elements` or `capture_with_elements` call.
- **`kill_process` ends a process.** Use it only when the user asks for it, and pass the exact `processId` from `list_processes` or `find_windows`.
- **Verify after every action.** Before the next step, check the new state: use what the tool returned (`newBounds`, `newState`, `windowHandle`) or call `get_foreground_window`, `find_windows`, `get_ui_elements` or a capture.

## When to Use

Use this skill when the user asks to:

### Window Discovery
- "List all windows" / "Show open windows"
- "Find Chrome windows" / "Find windows with [title]"
- "What window is focused?"

### Window Control
- "Move [app] to [position]"
- "Resize [app] to [dimensions]"
- "Snap [app] to [left/right/top/bottom] half"
- "Snap [app] to [position] quarter/third"
- "Maximize/minimize/restore [app]"
- "Focus [app]" / "Bring [app] to front"
- "Close [app] window"
- "Move [app] to monitor 2"

### Application Management
- "Open Notepad" / "Launch Chrome"
- "Open VS Code in [directory]"
- "List running processes"
- "Kill process [name/PID]"

### Screenshots & UI Automation
- "Take a screenshot" / "Capture the screen"
- "Take a screenshot of monitor 0"
- "Capture the [app] window"
- "Capture a region at [x, y] with size [width x height]"
- "Capture [app] with UI element overlays"
- "Get all UI elements from the [app] window"
- "Get all buttons and text inputs from [app]"

### Input Simulation / Click & Keyboard Automation
- "Click at position [x, y]"
- "Right-click at [x, y]"
- "Double-click at [x, y]"
- "Move the mouse to [x, y]"
- "Click the [button/menu item]" (requires get_ui_elements first)
- "Type 'Hello World' in the text field"
- "Press Ctrl+C to copy" / "Press Ctrl+V to paste"
- "Press Enter" / "Press Tab"

### Layout Management
- "Save my current window arrangement as [name]"
- "Apply my [name] layout"
- "Show my saved layouts"
- "Delete the [name] layout"

## Available Tools (36)

### Window Discovery (3 tools)

| Tool | Description | Key Parameters |
|------|-------------|----------------|
| `find_windows` | Find windows by criteria | titleContains, processName, processId, handle |
| `get_all_windows` | List all visible windows | includeMinimized |
| `get_foreground_window` | Get focused window | (none) |

### Window Control (8 tools)

| Tool | Description | Key Parameters |
|------|-------------|----------------|
| `move_window` | Move to x,y position | handle, x, y |
| `resize_window` | Change dimensions | handle, width, height |
| `set_window_bounds` | Move + resize combined | handle, x, y, width, height |
| `set_window_state` | Min/max/restore | handle, state |
| `focus_window` | Bring to foreground | handle |
| `close_window` | Close gracefully | handle |
| `snap_window` | Snap to preset position | handle, position, monitorIndex |
| `move_window_to_monitor` | Move to monitor | handle, monitorIndex, maximize |

### Batch Operations (5 tools)

| Tool | Description | Key Parameters |
|------|-------------|----------------|
| `set_windows_bounds_batch` | Move/resize multiple windows | placements |
| `snap_windows_batch` | Snap multiple windows | placements |
| `set_windows_state_batch` | Set state of multiple windows | changes |
| `close_windows_batch` | Close multiple windows | windows |
| `launch_applications_batch` | Launch multiple applications | applications |

### Monitor Info (2 tools)

| Tool | Description | Key Parameters |
|------|-------------|----------------|
| `get_monitors` | List all displays | (none) |
| `get_primary_monitor` | Get primary display | (none) |

### Screenshot (5 tools)

| Tool | Description | Key Parameters |
|------|-------------|----------------|
| `list_screens` | List screens for capture | (none) |
| `take_screenshot` | Capture a monitor | monitorIndex, format, quality |
| `capture_region` | Capture screen region | x, y, width, height, format |
| `capture_window` | Capture a window | handle, includeFrame, format |
| `capture_with_elements` | Capture with UI overlays | handle, controlTypes, maxDepth |

### UI Automation (1 tool)

| Tool | Description | Key Parameters |
|------|-------------|----------------|
| `get_ui_elements` | Get UI element tree | handle, maxDepth, controlTypes, interactableOnly |

### Input Simulation (6 tools)

| Tool | Description | Key Parameters |
|------|-------------|----------------|
| `click` | Left click at coordinates | x, y |
| `right_click` | Right click at coordinates | x, y |
| `double_click` | Double left click | x, y, delayMs |
| `mouse_move` | Move cursor (no click) | x, y |
| `type_text` | Type text into the foreground window | text, delayMs |
| `send_keys` | Send keys to the foreground window | keys (e.g., `^c`, `{ENTER}`) |

### App Launch (3 tools)

| Tool | Description | Key Parameters |
|------|-------------|----------------|
| `launch_application` | Launch app by path/name | path, arguments, workingDirectory |
| `list_processes` | List running processes | nameFilter |
| `kill_process` | Terminate process | pid, confirm |

### Layout Presets (5 tools)

| Tool | Description | Key Parameters |
|------|-------------|----------------|
| `list_layouts` | List saved presets | (none) |
| `get_layout` | Get preset details | name |
| `save_layout` | Save current arrangement | name, description, includeProcesses, excludeProcesses |
| `apply_layout` | Restore saved preset | name, matchBy, launchMissing |
| `delete_layout` | Delete a preset | name |

## Coordinates

- Screen coordinates are physical pixels on the virtual desktop. This covers the `x`/`y` input parameters, window `bounds`, element `bounds` and `screenBounds`, and monitor `bounds`. The server is per-monitor DPI aware, so do not multiply by `scaleFactor`.
- The primary monitor's top-left corner is (0, 0). Monitors to the left of or above it have negative coordinates.
- Monitor indices are 0-based, but index 0 is not always the primary monitor. Use `primaryIndex` from `get_monitors` or `list_screens`.
- Image pixels are not screen pixels once `maxWidth` scales a capture down (`originalWidth` is then set). To click something, use `screenBounds` from `capture_with_elements` or `bounds` from `get_ui_elements`. Do not use positions measured on the image.
- Clicks outside every monitor are rejected with `CoordinatesOutOfBounds`.

## Snap Positions

The `snap_window` tool supports 14 positions:

| Position | Description |
|----------|-------------|
| `left_half` / `right_half` | Left/Right 50% |
| `top_half` / `bottom_half` | Top/Bottom 50% |
| `top_left_quarter` | Top-left 25% |
| `top_right_quarter` | Top-right 25% |
| `bottom_left_quarter` | Bottom-left 25% |
| `bottom_right_quarter` | Bottom-right 25% |
| `left_third` / `center_third` / `right_third` | Thirds |
| `left_two_thirds` / `right_two_thirds` | Two-thirds |
| `fullscreen` | Full screen |

## Common Workflows

### Arrange Windows for Development
1. Use `find_windows` to locate VS Code and Terminal
2. Use `snap_window` to snap VS Code to `left_two_thirds`
3. Use `snap_window` to snap Terminal to `right_third`
4. Check `newBounds` in each result

### Save and Restore Workspace
1. Arrange windows as desired
2. Use `save_layout` with a descriptive name
3. Later, use `apply_layout` to restore

### Multi-Monitor Setup
1. Use `get_monitors` to see available displays
2. Use `move_window_to_monitor` to move windows to specific monitors
3. Combine with `snap_window` for precise positioning

### Launch and Position App
1. Use `launch_application` to start the app
2. Wait briefly for window to appear
3. Use `find_windows` to get the handle
4. Use `snap_window` or `move_window` to position

### Capture and Analyze Window Content
1. Use `find_windows` to locate the target window
2. Use `capture_window` to get a screenshot
3. Analyze the base64-encoded image data
4. Take actions based on visual content

### Get UI Elements for Programmatic Interaction
1. Use `find_windows` to locate the target window
2. Use `get_ui_elements` to get element tree with bounding boxes
3. Filter by controlTypes (Button, Edit, etc.) if needed
4. Use element coordinates to plan click automation

### Visual Element Discovery
1. Use `find_windows` to locate the target window
2. Use `capture_with_elements` to get annotated screenshot
3. Elements are color-coded (green=buttons, blue=inputs, etc.)
4. Use `imageBounds` for visual reference, `screenBounds` for clicks

### Click Automation
1. Use `find_windows` to locate the target window
2. Use `get_ui_elements` to get element bounding boxes
3. Find the target element and calculate its center: x = bounds.x + bounds.width/2, y = bounds.y + bounds.height/2
4. Use `focus_window`, then `get_foreground_window` to confirm the target window is in front
5. Use `click`, `right_click`, or `double_click` with the calculated coordinates
6. Verify the result with `get_ui_elements`, a capture, or `get_foreground_window`

### Keyboard Automation
1. Use `find_windows` to get the target window's handle
2. Use `focus_window` with that handle
3. If needed, `click` the text field (coordinates from `get_ui_elements`)
4. Call `get_foreground_window` and confirm its `handle` is the target. Stop if it is not
5. Use `type_text` to enter text, or `send_keys` for keys and shortcuts:
   - `^c` = Ctrl+C (copy)
   - `^v` = Ctrl+V (paste)
   - `^a` = Ctrl+A (select all)
   - `{ENTER}` = Enter key
   - `{TAB}` = Tab key
   - `{ESC}` = Escape key
   - `+{HOME}` = Shift+Home (select to start)
6. Repeat step 4 before each later `type_text` or `send_keys` call, then verify the result

To close a window, use `close_window`, not a key chord.

## Tips

- Always use `find_windows` first to get window handles before manipulating windows
- Use `processName` filter for reliable matching (titles can change)
- Layout presets can auto-launch missing applications with `launchMissing: true`
- The `close_window` tool sends WM_CLOSE for graceful shutdown (apps can prompt to save)
- Monitor indices are 0-based (primary is typically 0)
- Screenshots return base64-encoded images - use PNG for UI clarity, JPEG for smaller size
- `capture_window` works on occluded windows but not minimized ones
- `capture_with_elements` provides visual overlays - great for debugging UI automation
- `get_ui_elements` is lightweight when only coordinates are needed (no image data)
- Filter elements by controlTypes: Button, Edit, CheckBox, ComboBox, etc.
- For click automation, always use `get_ui_elements` first to get accurate screen coordinates
- Calculate click center: x = bounds.x + bounds.width/2, y = bounds.y + bounds.height/2
- Coordinates are validated against monitor bounds before clicking
- Use `mouse_move` to hover before clicking if needed for hover effects
- `type_text` supports any Unicode characters (any language)
- `send_keys` supports modifiers: `^` = Ctrl, `%` = Alt, `+` = Shift
- Special keys in braces: `{ENTER}`, `{TAB}`, `{F1}`, `{DELETE}`, etc.

## Full Documentation

See [docs/TOOLS.md](https://github.com/mortenbrudvik/WindowsAppManagerMcp/blob/main/docs/TOOLS.md) for complete parameter documentation and examples.
