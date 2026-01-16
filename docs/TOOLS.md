# Windows App Manager - MCP Tools Reference

A comprehensive guide to all 26 MCP tools for managing Windows applications, windows, and layouts.

## Quick Reference

| Category | Tools | Count |
|----------|-------|-------|
| [Window Discovery](#window-discovery) | find_windows, get_all_windows, get_foreground_window | 3 |
| [Window Control](#window-control) | move_window, resize_window, set_window_bounds, set_window_state, focus_window, close_window, snap_window, move_window_to_monitor | 8 |
| [Batch Operations](#batch-operations) | set_windows_bounds_batch, snap_windows_batch, set_windows_state_batch, close_windows_batch, launch_applications_batch | 5 |
| [Monitor Info](#monitor-info) | get_monitors, get_primary_monitor | 2 |
| [App Launch](#app-launch) | launch_application, list_processes, kill_process | 3 |
| [Layout Presets](#layout-presets) | list_layouts, get_layout, save_layout, apply_layout, delete_layout | 5 |

---

## Window Discovery

Tools for finding and listing windows on the system.

### find_windows

Find windows matching specific criteria.

**Parameters:**
| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| titleContains | string | No | - | Partial text to match in window title (case-insensitive) |
| processName | string | No | - | Process name to match (e.g., 'notepad', 'chrome') |
| processId | int | No | - | Process ID to match |
| handle | long | No | - | Specific window handle to find |
| visibleOnly | bool | No | true | Only return visible windows |

**Example:**
> "Find all Chrome windows"

```json
{
  "name": "find_windows",
  "arguments": {
    "processName": "chrome"
  }
}
```

**Response:**
```json
[
  {
    "handle": 2884384,
    "title": "Google - Google Chrome",
    "processName": "chrome",
    "processId": 12456,
    "bounds": { "x": 0, "y": 0, "width": 1920, "height": 1040 },
    "state": "Normal",
    "isVisible": true,
    "monitorIndex": 0
  },
  {
    "handle": 1445556,
    "title": "GitHub - Google Chrome",
    "processName": "chrome",
    "processId": 12456,
    "bounds": { "x": 100, "y": 100, "width": 1200, "height": 800 },
    "state": "Normal",
    "isVisible": true,
    "monitorIndex": 1
  }
]
```

---

### get_all_windows

List all visible top-level windows on the system.

**Parameters:**
| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| includeMinimized | bool | No | true | Include minimized windows in results |

**Example:**
> "List all open windows"

```json
{
  "name": "get_all_windows",
  "arguments": {}
}
```

**Response:**
```json
[
  {
    "handle": 330390,
    "title": "Visual Studio Code",
    "processName": "Code",
    "processId": 8432,
    "bounds": { "x": 0, "y": 0, "width": 1920, "height": 1040 },
    "state": "Maximized",
    "isVisible": true,
    "monitorIndex": 0
  },
  {
    "handle": 2884384,
    "title": "Windows Terminal",
    "processName": "WindowsTerminal",
    "processId": 15672,
    "bounds": { "x": 960, "y": 0, "width": 960, "height": 1040 },
    "state": "Normal",
    "isVisible": true,
    "monitorIndex": 1
  }
]
```

---

### get_foreground_window

Get information about the currently focused window.

**Parameters:** None

**Example:**
> "What window is currently focused?"

```json
{
  "name": "get_foreground_window",
  "arguments": {}
}
```

**Response:**
```json
{
  "handle": 330390,
  "title": "TOOLS.md - Visual Studio Code",
  "processName": "Code",
  "processId": 8432,
  "bounds": { "x": 0, "y": 0, "width": 1920, "height": 1040 },
  "state": "Maximized",
  "isVisible": true,
  "monitorIndex": 0
}
```

---

## Window Control

Tools for manipulating window position, size, and state.

### move_window

Move a window to a specific screen position.

**Parameters:**
| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| handle | long | Yes | - | Window handle from find_windows |
| x | int | Yes | - | X coordinate (left edge) |
| y | int | Yes | - | Y coordinate (top edge) |
| bringToFront | bool | No | true | Bring window to front of z-order |

**Example:**
> "Move Notepad to position 100, 200"

```json
{
  "name": "move_window",
  "arguments": {
    "handle": 12345,
    "x": 100,
    "y": 200
  }
}
```

**Response:**
```json
{
  "success": true,
  "newBounds": { "x": 100, "y": 200, "width": 800, "height": 600 }
}
```

---

### resize_window

Resize a window to specific dimensions.

**Parameters:**
| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| handle | long | Yes | - | Window handle |
| width | int | Yes | - | New width in pixels |
| height | int | Yes | - | New height in pixels |

**Example:**
> "Make the window 1200 pixels wide and 800 pixels tall"

```json
{
  "name": "resize_window",
  "arguments": {
    "handle": 12345,
    "width": 1200,
    "height": 800
  }
}
```

**Response:**
```json
{
  "success": true,
  "newBounds": { "x": 100, "y": 100, "width": 1200, "height": 800 }
}
```

---

### set_window_bounds

Move and resize a window in a single operation.

**Parameters:**
| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| handle | long | Yes | - | Window handle |
| x | int | Yes | - | X coordinate (left edge) |
| y | int | Yes | - | Y coordinate (top edge) |
| width | int | Yes | - | New width in pixels |
| height | int | Yes | - | New height in pixels |
| bringToFront | bool | No | true | Bring window to front of z-order |

**Example:**
> "Position the window at 50,50 with size 1000x700"

```json
{
  "name": "set_window_bounds",
  "arguments": {
    "handle": 12345,
    "x": 50,
    "y": 50,
    "width": 1000,
    "height": 700
  }
}
```

**Response:**
```json
{
  "success": true,
  "newBounds": { "x": 50, "y": 50, "width": 1000, "height": 700 }
}
```

---

### set_window_state

Minimize, maximize, or restore a window.

**Parameters:**
| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| handle | long | Yes | - | Window handle |
| state | string | Yes | - | Desired state: 'minimize', 'maximize', or 'restore' |

**Example:**
> "Maximize the VS Code window"

```json
{
  "name": "set_window_state",
  "arguments": {
    "handle": 330390,
    "state": "maximize"
  }
}
```

**Response:**
```json
{
  "success": true,
  "newState": "Maximized"
}
```

---

### focus_window

Bring a window to the foreground and give it focus.

**Parameters:**
| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| handle | long | Yes | - | Window handle |

**Example:**
> "Bring Chrome to the front"

```json
{
  "name": "focus_window",
  "arguments": {
    "handle": 2884384
  }
}
```

**Response:**
```json
{
  "success": true
}
```

---

### close_window

Close a window gracefully (sends WM_CLOSE message).

**Parameters:**
| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| handle | long | Yes | - | Window handle |

**Example:**
> "Close the Notepad window"

```json
{
  "name": "close_window",
  "arguments": {
    "handle": 12345
  }
}
```

**Response:**
```json
{
  "success": true
}
```

> **Note:** The application may prompt to save unsaved work before closing.

---

### snap_window

Snap a window to predefined screen positions (like Windows Snap Assist).

**Parameters:**
| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| handle | long | Yes | - | Window handle |
| position | string | Yes | - | Snap position (see table below) |
| monitorIndex | int | No | current | Target monitor (0-based index) |
| bringToFront | bool | No | true | Bring window to front of z-order |

**Snap Positions:**

| Position | Description |
|----------|-------------|
| `left_half` | Left 50% of screen |
| `right_half` | Right 50% of screen |
| `top_half` | Top 50% of screen |
| `bottom_half` | Bottom 50% of screen |
| `top_left_quarter` | Top-left 25% |
| `top_right_quarter` | Top-right 25% |
| `bottom_left_quarter` | Bottom-left 25% |
| `bottom_right_quarter` | Bottom-right 25% |
| `left_third` | Left 33% |
| `center_third` | Center 33% |
| `right_third` | Right 33% |
| `left_two_thirds` | Left 66% |
| `right_two_thirds` | Right 66% |
| `fullscreen` | Full screen (maximized work area) |

**Example:**
> "Snap VS Code to the left half of the screen"

```json
{
  "name": "snap_window",
  "arguments": {
    "handle": 330390,
    "position": "left_half"
  }
}
```

**Response:**
```json
{
  "success": true,
  "newBounds": { "x": 0, "y": 0, "width": 960, "height": 1040 }
}
```

**Example with monitor:**
> "Snap Terminal to the right third of monitor 2"

```json
{
  "name": "snap_window",
  "arguments": {
    "handle": 2884384,
    "position": "right_third",
    "monitorIndex": 1
  }
}
```

---

### move_window_to_monitor

Move a window to a specific monitor.

**Parameters:**
| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| handle | long | Yes | - | Window handle |
| monitorIndex | int | Yes | - | Target monitor (0-based index) |
| positioning | string | No | "center" | Position on monitor (see below) |
| bringToFront | bool | No | true | Bring window to front of z-order |

**Positioning Options:**
- `center` - Center of monitor
- `topleft` - Top-left corner
- `topright` - Top-right corner
- `bottomleft` - Bottom-left corner
- `bottomright` - Bottom-right corner
- `maximize` - Maximize on target monitor
- `restore` - Keep current size, move to monitor

**Example:**
> "Move Chrome to the second monitor and center it"

```json
{
  "name": "move_window_to_monitor",
  "arguments": {
    "handle": 2884384,
    "monitorIndex": 1,
    "positioning": "center"
  }
}
```

**Response:**
```json
{
  "success": true,
  "newBounds": { "x": 2240, "y": 320, "width": 1200, "height": 800 }
}
```

---

## Batch Operations

Tools for performing multiple window operations efficiently in a single call.

### set_windows_bounds_batch

Move and resize multiple windows in a single batch operation.

**Parameters:**
| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| placements | array | Yes | - | Array of window placements |
| delayBetweenMs | int | No | 50 | Delay between operations in milliseconds |

Each placement object contains:
| Field | Type | Required | Default | Description |
|-------|------|----------|---------|-------------|
| handle | long | Yes | - | Window handle |
| x | int | Yes | - | X coordinate |
| y | int | Yes | - | Y coordinate |
| width | int | Yes | - | Width in pixels |
| height | int | Yes | - | Height in pixels |
| bringToFront | bool | No | true | Bring window to front |

**Example:**
> "Position Notepad and Calculator side by side"

```json
{
  "name": "set_windows_bounds_batch",
  "arguments": {
    "placements": [
      { "handle": 12345, "x": 0, "y": 0, "width": 960, "height": 1040 },
      { "handle": 67890, "x": 960, "y": 0, "width": 960, "height": 1040 }
    ]
  }
}
```

**Response:**
```json
{
  "totalRequested": 2,
  "succeeded": 2,
  "failed": 0,
  "results": [
    { "handle": 12345, "success": true, "newBounds": { "x": 0, "y": 0, "width": 960, "height": 1040 } },
    { "handle": 67890, "success": true, "newBounds": { "x": 960, "y": 0, "width": 960, "height": 1040 } }
  ]
}
```

---

### snap_windows_batch

Snap multiple windows to predefined positions in a single batch operation.

**Parameters:**
| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| placements | array | Yes | - | Array of snap placements |
| delayBetweenMs | int | No | 50 | Delay between operations in milliseconds |

Each placement object contains:
| Field | Type | Required | Default | Description |
|-------|------|----------|---------|-------------|
| handle | long | Yes | - | Window handle |
| position | string | Yes | - | Snap position (see snap_window) |
| monitorIndex | int | No | current | Target monitor |
| bringToFront | bool | No | true | Bring window to front |

**Example:**
> "Snap VS Code to left half and Terminal to right half"

```json
{
  "name": "snap_windows_batch",
  "arguments": {
    "placements": [
      { "handle": 12345, "position": "left_half" },
      { "handle": 67890, "position": "right_half" }
    ]
  }
}
```

**Response:**
```json
{
  "totalRequested": 2,
  "succeeded": 2,
  "failed": 0,
  "results": [
    { "handle": 12345, "success": true, "newBounds": { "x": 0, "y": 0, "width": 960, "height": 1040 } },
    { "handle": 67890, "success": true, "newBounds": { "x": 960, "y": 0, "width": 960, "height": 1040 } }
  ]
}
```

---

### set_windows_state_batch

Set the state (minimize, maximize, restore) of multiple windows in a single batch operation.

**Parameters:**
| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| changes | array | Yes | - | Array of state changes |
| delayBetweenMs | int | No | 30 | Delay between operations in milliseconds |

Each change object contains:
| Field | Type | Required | Default | Description |
|-------|------|----------|---------|-------------|
| handle | long | Yes | - | Window handle |
| state | string | Yes | - | State: 'minimize', 'maximize', or 'restore' |

**Example:**
> "Minimize all browser windows"

```json
{
  "name": "set_windows_state_batch",
  "arguments": {
    "changes": [
      { "handle": 12345, "state": "minimize" },
      { "handle": 67890, "state": "minimize" }
    ]
  }
}
```

**Response:**
```json
{
  "totalRequested": 2,
  "succeeded": 2,
  "failed": 0,
  "results": [
    { "handle": 12345, "success": true, "newState": "minimized" },
    { "handle": 67890, "success": true, "newState": "minimized" }
  ]
}
```

---

### close_windows_batch

Close multiple windows gracefully in a single batch operation.

**Parameters:**
| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| windows | array | Yes | - | Array of window handles to close |
| delayBetweenMs | int | No | 50 | Delay between operations in milliseconds |

Each window object contains:
| Field | Type | Required | Description |
|-------|------|----------|-------------|
| handle | long | Yes | Window handle |

**Example:**
> "Close all Notepad windows"

```json
{
  "name": "close_windows_batch",
  "arguments": {
    "windows": [
      { "handle": 12345 },
      { "handle": 67890 }
    ]
  }
}
```

**Response:**
```json
{
  "totalRequested": 2,
  "succeeded": 2,
  "failed": 0,
  "results": [
    { "handle": 12345, "success": true },
    { "handle": 67890, "success": true }
  ]
}
```

> **Note:** Applications may prompt to save unsaved work before closing.

---

### launch_applications_batch

Launch multiple applications in a single batch operation.

**Parameters:**
| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| applications | array | Yes | - | Array of applications to launch |
| waitForWindows | bool | No | false | Wait for windows to appear |
| waitTimeoutMs | int | No | 5000 | Timeout for waiting (ms) |

Each application object contains:
| Field | Type | Required | Default | Description |
|-------|------|----------|---------|-------------|
| executable | string | Yes | - | Path, app name, or protocol URI |
| arguments | string[] | No | - | Command line arguments |
| workingDirectory | string | No | - | Working directory |

**Example:**
> "Launch Notepad and Calculator"

```json
{
  "name": "launch_applications_batch",
  "arguments": {
    "applications": [
      { "executable": "notepad" },
      { "executable": "calc" }
    ],
    "waitForWindows": true
  }
}
```

**Response:**
```json
{
  "totalRequested": 2,
  "succeeded": 2,
  "failed": 0,
  "results": [
    { "executable": "notepad", "success": true, "processId": 15432, "windowHandle": 12345 },
    { "executable": "calc", "success": true, "processId": 15433, "windowHandle": 67890 }
  ]
}
```

---

## Monitor Info

Tools for getting information about connected displays.

### get_monitors

Get information about all connected monitors.

**Parameters:** None

**Example:**
> "Show me all connected monitors"

```json
{
  "name": "get_monitors",
  "arguments": {}
}
```

**Response:**
```json
{
  "monitors": [
    {
      "index": 0,
      "name": "\\\\.\\DISPLAY1",
      "isPrimary": false,
      "bounds": { "x": -2560, "y": 365, "width": 2560, "height": 1440 },
      "workArea": { "x": -2560, "y": 365, "width": 2560, "height": 1392 },
      "scaleFactor": 1.0
    },
    {
      "index": 1,
      "name": "\\\\.\\DISPLAY2",
      "isPrimary": true,
      "bounds": { "x": 0, "y": 0, "width": 3840, "height": 2160 },
      "workArea": { "x": 0, "y": 0, "width": 3840, "height": 2088 },
      "scaleFactor": 1.5
    }
  ],
  "primaryIndex": 1
}
```

---

### get_primary_monitor

Get information about the primary display.

**Parameters:** None

**Example:**
> "What's my primary monitor resolution?"

```json
{
  "name": "get_primary_monitor",
  "arguments": {}
}
```

**Response:**
```json
{
  "index": 1,
  "name": "\\\\.\\DISPLAY2",
  "isPrimary": true,
  "bounds": { "x": 0, "y": 0, "width": 3840, "height": 2160 },
  "workArea": { "x": 0, "y": 0, "width": 3840, "height": 2088 },
  "scaleFactor": 1.5
}
```

---

## App Launch

Tools for launching applications and managing processes.

### launch_application

Launch an application by path, name, or protocol URI.

**Parameters:**
| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| executable | string | Yes | - | Path, app name, or protocol URI |
| arguments | string[] | No | - | Command line arguments |
| workingDirectory | string | No | - | Working directory for the app |
| waitForWindow | bool | No | false | Wait for window to appear |
| waitTimeoutMs | int | No | 5000 | Timeout for waiting (ms) |

**Example 1:** Launch by name
> "Open Notepad"

```json
{
  "name": "launch_application",
  "arguments": {
    "executable": "notepad"
  }
}
```

**Example 2:** Launch with arguments
> "Open the README file in Notepad"

```json
{
  "name": "launch_application",
  "arguments": {
    "executable": "notepad",
    "arguments": ["C:\\Projects\\README.md"]
  }
}
```

**Example 3:** Launch and wait for window
> "Open VS Code in the project folder and wait for it"

```json
{
  "name": "launch_application",
  "arguments": {
    "executable": "code",
    "arguments": ["C:\\Projects\\MyApp"],
    "waitForWindow": true,
    "waitTimeoutMs": 10000
  }
}
```

**Example 4:** Open URL
> "Open GitHub in the browser"

```json
{
  "name": "launch_application",
  "arguments": {
    "executable": "https://github.com"
  }
}
```

**Response:**
```json
{
  "success": true,
  "processId": 15432,
  "windowHandle": 2884384
}
```

---

### list_processes

List running processes with optional filtering.

**Parameters:**
| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| nameFilter | string | No | - | Filter by process name (partial match) |
| includeWindowless | bool | No | false | Include processes without windows |

**Example:**
> "Show all Chrome processes"

```json
{
  "name": "list_processes",
  "arguments": {
    "nameFilter": "chrome"
  }
}
```

**Response:**
```json
[
  {
    "processId": 12456,
    "name": "chrome",
    "executablePath": "C:\\Program Files\\Google\\Chrome\\Application\\chrome.exe",
    "windowCount": 5,
    "memoryUsageMB": 512.8
  },
  {
    "processId": 12460,
    "name": "chrome",
    "executablePath": "C:\\Program Files\\Google\\Chrome\\Application\\chrome.exe",
    "windowCount": 0,
    "memoryUsageMB": 128.4
  }
]
```

---

### kill_process

Terminate a process by ID (requires confirmation).

**Parameters:**
| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| processId | int | Yes | - | Process ID to terminate |
| confirm | bool | Yes | - | Must be true to confirm termination |
| forceKill | bool | No | false | Force immediate termination |

**Example:**
> "Terminate process 12456"

```json
{
  "name": "kill_process",
  "arguments": {
    "processId": 12456,
    "confirm": true
  }
}
```

**Response:**
```json
{
  "success": true,
  "processId": 12456,
  "processName": "chrome"
}
```

**Protected Processes:**
The following system processes cannot be terminated:
- System, smss, csrss, wininit, services, lsass, lsm
- svchost, winlogon, dwm, explorer, Registry, Memory Compression

---

## Layout Presets

Tools for saving and restoring window arrangements.

### list_layouts

List all saved layout presets.

**Parameters:** None

**Example:**
> "Show my saved layouts"

```json
{
  "name": "list_layouts",
  "arguments": {}
}
```

**Response:**
```json
[
  {
    "name": "work-setup",
    "description": "My coding workspace with VS Code, Terminal, and Browser",
    "windowCount": 3,
    "createdAt": "2024-01-15T10:30:00Z",
    "updatedAt": "2024-01-20T14:22:00Z"
  },
  {
    "name": "focus-mode",
    "description": "Single maximized window for deep work",
    "windowCount": 1,
    "createdAt": "2024-01-16T09:00:00Z",
    "updatedAt": "2024-01-16T09:00:00Z"
  }
]
```

---

### get_layout

Get full details of a layout preset.

**Parameters:**
| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| name | string | Yes | - | Name of the layout preset |

**Example:**
> "Show the details of work-setup layout"

```json
{
  "name": "get_layout",
  "arguments": {
    "name": "work-setup"
  }
}
```

**Response:**
```json
{
  "name": "work-setup",
  "description": "My coding workspace",
  "placements": [
    {
      "processName": "Code",
      "titleContains": null,
      "monitorIndex": 0,
      "position": { "x": 0, "y": 0, "width": 0.5, "height": 1.0 },
      "launchCommand": "code",
      "focus": true,
      "order": 1
    },
    {
      "processName": "WindowsTerminal",
      "titleContains": null,
      "monitorIndex": 0,
      "position": { "x": 0.5, "y": 0, "width": 0.5, "height": 0.5 },
      "launchCommand": "wt",
      "focus": false,
      "order": 2
    },
    {
      "processName": "chrome",
      "titleContains": null,
      "monitorIndex": 1,
      "position": { "x": 0, "y": 0, "width": 1.0, "height": 1.0 },
      "launchCommand": "chrome",
      "focus": false,
      "order": 3
    }
  ],
  "createdAt": "2024-01-15T10:30:00Z",
  "updatedAt": "2024-01-20T14:22:00Z"
}
```

---

### save_layout

Save the current window arrangement as a preset.

**Parameters:**
| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| name | string | Yes | - | Name for the preset |
| description | string | No | - | Description of the layout |
| includeProcesses | string | No | - | Only save these processes (comma-separated) |
| excludeProcesses | string | No | - | Exclude these processes (comma-separated) |

**Example 1:** Save all windows
> "Save my current layout as 'morning-routine'"

```json
{
  "name": "save_layout",
  "arguments": {
    "name": "morning-routine",
    "description": "Email, calendar, and news sites"
  }
}
```

**Example 2:** Save specific apps
> "Save only VS Code and Terminal as 'coding-setup'"

```json
{
  "name": "save_layout",
  "arguments": {
    "name": "coding-setup",
    "description": "Just the essentials for coding",
    "includeProcesses": "Code,WindowsTerminal"
  }
}
```

**Response:**
```json
{
  "success": true,
  "layoutName": "morning-routine",
  "windowCount": 4
}
```

---

### apply_layout

Apply a saved layout preset.

**Parameters:**
| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| name | string | Yes | - | Name of the preset |
| matchBy | string | No | "process_and_title" | Match method (see below) |
| launchMissing | bool | No | false | Launch apps that aren't running |

**Match Methods:**
- `process_and_title` - Match by process name AND window title
- `process_only` - Match by process name only
- `title_only` - Match by window title only

**Example 1:** Basic apply
> "Apply my work-setup layout"

```json
{
  "name": "apply_layout",
  "arguments": {
    "name": "work-setup"
  }
}
```

**Example 2:** Launch missing apps
> "Restore work-setup and launch any missing apps"

```json
{
  "name": "apply_layout",
  "arguments": {
    "name": "work-setup",
    "launchMissing": true
  }
}
```

**Response:**
```json
{
  "success": true,
  "presetName": "work-setup",
  "windowsArranged": 3,
  "windowsNotFound": 0,
  "windowsLaunched": 1
}
```

---

### delete_layout

Delete a layout preset.

**Parameters:**
| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| name | string | Yes | - | Name of the preset to delete |

**Example:**
> "Delete the old-setup layout"

```json
{
  "name": "delete_layout",
  "arguments": {
    "name": "old-setup"
  }
}
```

**Response:**
```json
{
  "success": true
}
```

---

## Common Workflows

### Create a Development Workspace

1. Launch your apps:
```
"Launch VS Code, then launch Windows Terminal"
```

2. Arrange windows:
```
"Snap VS Code to the left half and Terminal to the right half"
```

3. Save for later:
```
"Save this layout as 'dev-setup'"
```

### Multi-Monitor Setup

1. Check monitors:
```
"Show all monitors"
```

2. Distribute windows:
```
"Move VS Code to monitor 0 and maximize it"
"Move Chrome to monitor 1 and snap it to the left half"
```

3. Save the arrangement:
```
"Save this as 'dual-monitor-coding'"
```

### Quick Window Management

- Find a window: `"Find the Slack window"`
- Bring to front: `"Focus the Teams window"`
- Quick resize: `"Snap Chrome to the right third"`
- Close gracefully: `"Close Notepad"`

---

## Error Codes

| Code | Description |
|------|-------------|
| `WindowNotFound` | Window handle is invalid or window closed |
| `InvalidHandle` | Handle parameter is missing or invalid |
| `OperationFailed` | Window operation failed (permission denied, etc.) |
| `PathTraversal` | Security: path traversal attempt detected |
| `InvalidExecutable` | Invalid or blocked executable path |
| `ProtectedProcess` | Cannot terminate protected system process |
| `ConfirmationRequired` | Kill operation requires confirm=true |
| `PresetNotFound` | Layout preset doesn't exist |
| `InvalidPresetName` | Preset name is empty or invalid |
