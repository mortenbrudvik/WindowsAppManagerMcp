---
name: using-windows-app-manager
description: |
  Manage Windows applications, windows, and desktop layouts.
  Use when user asks to:
  - List, find, or show windows
  - Move, resize, snap, minimize, maximize, focus, or close windows
  - Show monitors or move windows between monitors
  - Launch applications or open programs
  - List or kill processes
  - Save, apply, restore, or manage window layouts
---

# Using Windows App Manager

Windows App Manager provides 21 MCP tools for managing Windows applications, windows, and desktop layouts.

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

### Layout Management
- "Save my current window arrangement as [name]"
- "Apply my [name] layout"
- "Show my saved layouts"
- "Delete the [name] layout"

## Available Tools (21)

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

### Monitor Info (2 tools)

| Tool | Description | Key Parameters |
|------|-------------|----------------|
| `get_monitors` | List all displays | (none) |
| `get_primary_monitor` | Get primary display | (none) |

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

## Tips

- Always use `find_windows` first to get window handles before manipulating windows
- Use `processName` filter for reliable matching (titles can change)
- Layout presets can auto-launch missing applications with `launchMissing: true`
- The `close_window` tool sends WM_CLOSE for graceful shutdown (apps can prompt to save)
- Monitor indices are 0-based (primary is typically 0)

## Full Documentation

See [docs/TOOLS.md](https://github.com/mortenbrudvik/WindowsAppManagerMcp/blob/main/docs/TOOLS.md) for complete parameter documentation and examples.
