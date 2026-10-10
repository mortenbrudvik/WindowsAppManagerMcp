# Windows App Manager - MCP Tools Reference

A comprehensive guide to all 42 MCP tools for managing Windows applications, windows, and layouts.

## Quick Reference

| Category | Tools | Count |
|----------|-------|-------|
| [Window Discovery](#window-discovery) | find_windows, get_all_windows, get_foreground_window | 3 |
| [Window Control](#window-control) | move_window, resize_window, set_window_bounds, set_window_state, focus_window, close_window, snap_window, move_window_to_monitor | 8 |
| [Batch Operations](#batch-operations) | set_windows_bounds_batch, snap_windows_batch, set_windows_state_batch, close_windows_batch, launch_applications_batch | 5 |
| [Monitor Info](#monitor-info) | get_monitors, get_primary_monitor | 2 |
| [Screenshot](#screenshot) | list_screens, take_screenshot, capture_region, capture_window, capture_with_elements | 5 |
| [UI Automation](#ui-automation) | get_ui_elements | 1 |
| [Browser Reading](#browser-reading) | get_browser_url, get_browser_content, get_active_browser_url, launch_browser_with_debug | 4 |
| [Input Simulation](#input-simulation) | click, right_click, double_click, mouse_move, type_text, send_keys | 6 |
| [App Launch](#app-launch) | launch_application, list_processes, kill_process | 3 |
| [Layout Presets](#layout-presets) | list_layouts, get_layout, save_layout, apply_layout, delete_layout | 5 |
| [Browser Reading](#browser-reading) | get_browser_url, get_browser_content, get_active_browser_url | 3 |

---

## Safety and Coordinates

### Input safety

The input tools send real mouse and keyboard input to the user's desktop.

- `type_text` and `send_keys` have no target window. They type into whatever window is in the foreground, and that is often the terminal or chat window running the agent. Call `get_foreground_window` immediately before every `type_text` or `send_keys` call and check that `handle` is the intended window. If it is not, call `focus_window` and check again. Windows can refuse focus changes, so do not trust `focus_window` alone.
- Never send destructive key chords without that check. Examples: Alt+F4 (`%{F4}`), Ctrl+W, Ctrl+Q, Ctrl+A followed by Delete, or Enter on a confirmation dialog. In a terminal, typed text followed by `{ENTER}` runs as a shell command.
- To close a window, use [close_window](#close_window) or [close_windows_batch](#close_windows_batch) with the window's handle. Do not use Alt+F4.
- `click`, `right_click` and `double_click` act on whatever is at the given point. Bring the target window to the front and confirm it with `get_foreground_window` before clicking.
- After each action, check the result before the next step.

### Coordinate space

- All screen coordinates are physical pixels on the virtual desktop. This covers input `x`/`y`, window `bounds`, UI element `bounds` and `screenBounds`, monitor `bounds`/`workArea` and `capture_region` arguments. The server is per-monitor DPI aware, so do not multiply by `scaleFactor`.
- The primary monitor's top-left corner is (0, 0). Monitors to the left of or above it have negative coordinates (see the [get_monitors](#get_monitors) example).
- Monitor indices are 0-based, and index 0 is not necessarily the primary monitor. Use `primaryIndex` or `isPrimary`.
- When `maxWidth` scales a screenshot down, image pixels no longer match screen pixels. Use `screenBounds` (from `capture_with_elements`) or `bounds` (from `get_ui_elements`) for clicks, not positions measured on the image.

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
    "state": "normal",
    "isVisible": true,
    "monitorIndex": 0
  },
  {
    "handle": 1445556,
    "title": "GitHub - Google Chrome",
    "processName": "chrome",
    "processId": 12456,
    "bounds": { "x": 100, "y": 100, "width": 1200, "height": 800 },
    "state": "normal",
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
    "state": "maximized",
    "isVisible": true,
    "monitorIndex": 0
  },
  {
    "handle": 2884384,
    "title": "Windows Terminal",
    "processName": "WindowsTerminal",
    "processId": 15672,
    "bounds": { "x": 960, "y": 0, "width": 960, "height": 1040 },
    "state": "normal",
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
  "state": "maximized",
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
  "newState": "maximized"
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

Each item is validated and resolved with the same rules as [launch_application](#launch_application). A failed item includes `error` and `errorCode` (for example `UnsafeBatchArguments`).

---

## Monitor Info

Tools for getting information about connected displays.

### get_monitors

Get information about all connected monitors. The server reuses monitor information for up to 250 ms, so a display change can take that long to appear.

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

## Screenshot

Tools for capturing screenshots of screens, regions, and windows.

**How images are returned:** every capture tool returns its result as JSON text. By default the image is a base64 string in `imageData`. It is not sent as MCP image content, so a model cannot see the picture by reading that string, and a full-resolution PNG can be several megabytes of text. To keep results usable:

- Pass `saveToFile: true` to write the image to disk instead. `imageData` is then null and `filePath` holds the path: `outputPath` if given, otherwise an auto-named `screenshot_<timestamp>_<id>.png` (or `.jpg`) in the temp directory. Open that file with an image viewer; in Claude Code, the Read tool displays images.
- Pass `maxWidth` to scale wide images down proportionally (narrower images are unchanged), and `format: "jpeg"` for smaller files. `width` and `height` are the output image size. `originalWidth` and `originalHeight` are set only when the image was scaled down. `maxWidth` must be a positive number.
- If you only need element positions, use [get_ui_elements](#get_ui_elements), which returns no image data.

### list_screens

List all available screens/monitors for capture, including virtual screen bounds.

**Parameters:** None

**Example:**
> "What screens are available for capture?"

```json
{
  "name": "list_screens",
  "arguments": {}
}
```

**Response:**
```json
{
  "screens": [
    {
      "index": 0,
      "name": "\\\\.\\DISPLAY1",
      "isPrimary": false,
      "x": -2560,
      "y": 365,
      "width": 2560,
      "height": 1440,
      "workAreaX": -2560,
      "workAreaY": 365,
      "workAreaWidth": 2560,
      "workAreaHeight": 1392,
      "scaleFactor": 1.0
    },
    {
      "index": 1,
      "name": "\\\\.\\DISPLAY2",
      "isPrimary": true,
      "x": 0,
      "y": 0,
      "width": 3840,
      "height": 2160,
      "workAreaX": 0,
      "workAreaY": 0,
      "workAreaWidth": 3840,
      "workAreaHeight": 2088,
      "scaleFactor": 1.5
    }
  ],
  "primaryIndex": 1,
  "virtualScreen": {
    "x": -2560,
    "y": 0,
    "width": 6400,
    "height": 2160
  }
}
```

---

### take_screenshot

Capture a screenshot of a specific monitor.

**Parameters:**
| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| monitorIndex | int | No | primary | Monitor index (0-based). Defaults to primary monitor. |
| format | string | No | "png" | Image format: "png" (lossless) or "jpeg" (smaller) |
| quality | int | No | 85 | JPEG quality (1-100). Only used when format is "jpeg". |
| saveToFile | bool | No | false | Write the image to a file and return `filePath`; `imageData` is null |
| outputPath | string | No | temp file | File path used when saveToFile is true |
| maxWidth | int | No | - | Scale the image down proportionally to at most this width in pixels |

**Example:**
> "Take a screenshot of the primary monitor"

```json
{
  "name": "take_screenshot",
  "arguments": {}
}
```

**Example with options:**
> "Take a screenshot of monitor 0 as JPEG"

```json
{
  "name": "take_screenshot",
  "arguments": {
    "monitorIndex": 0,
    "format": "jpeg",
    "quality": 90
  }
}
```

**Response:**
```json
{
  "success": true,
  "imageData": "iVBORw0KGgoAAAANSUhEUgAAA...",
  "imageFormat": "png",
  "width": 3840,
  "height": 2160,
  "capturedRegion": { "x": 0, "y": 0, "width": 3840, "height": 2160 },
  "monitorIndex": 1,
  "scaleFactor": 1.5
}
```

**Example: small JPEG saved to a file**
> "Take a screenshot I can open, and keep it small"

```json
{
  "name": "take_screenshot",
  "arguments": {
    "format": "jpeg",
    "maxWidth": 1280,
    "saveToFile": true
  }
}
```

**Response:**
```json
{
  "success": true,
  "imageFormat": "jpeg",
  "width": 1280,
  "height": 720,
  "capturedRegion": { "x": 0, "y": 0, "width": 3840, "height": 2160 },
  "monitorIndex": 1,
  "scaleFactor": 1.5,
  "filePath": "C:\\Users\\me\\AppData\\Local\\Temp\\screenshot_20260110_101500_3f2a9c0d4b8e4f6a9d1c2b3a4e5f6a7b.jpg",
  "originalWidth": 3840,
  "originalHeight": 2160
}
```

---

### capture_region

Capture a screenshot of an arbitrary screen region.

**Parameters:**
| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| x | int | Yes | - | X coordinate of the region's left edge |
| y | int | Yes | - | Y coordinate of the region's top edge |
| width | int | Yes | - | Width of the region in pixels |
| height | int | Yes | - | Height of the region in pixels |
| format | string | No | "png" | Image format: "png" or "jpeg" |
| quality | int | No | 85 | JPEG quality (1-100) |
| saveToFile | bool | No | false | Write the image to a file and return `filePath`; `imageData` is null |
| outputPath | string | No | temp file | File path used when saveToFile is true |
| maxWidth | int | No | - | Scale the image down proportionally to at most this width in pixels |

**Example:**
> "Capture a 800x600 region starting at position 100, 200"

```json
{
  "name": "capture_region",
  "arguments": {
    "x": 100,
    "y": 200,
    "width": 800,
    "height": 600
  }
}
```

**Response:**
```json
{
  "success": true,
  "imageData": "iVBORw0KGgoAAAANSUhEUgAAA...",
  "imageFormat": "png",
  "width": 800,
  "height": 600,
  "capturedRegion": { "x": 100, "y": 200, "width": 800, "height": 600 },
  "monitorIndex": 1,
  "scaleFactor": 1.5
}
```

---

### capture_window

Capture a screenshot of a specific window. Works even if the window is partially occluded by other windows.

**Parameters:**
| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| handle | long | Yes | - | Window handle from find_windows or get_all_windows |
| includeFrame | bool | No | true | Include window frame/decoration (title bar, borders) |
| format | string | No | "png" | Image format: "png" or "jpeg" |
| quality | int | No | 85 | JPEG quality (1-100) |
| saveToFile | bool | No | false | Write the image to a file and return `filePath`; `imageData` is null |
| outputPath | string | No | temp file | File path used when saveToFile is true |
| maxWidth | int | No | - | Scale the image down proportionally to at most this width in pixels |

**Example:**
> "Capture the VS Code window"

```json
{
  "name": "capture_window",
  "arguments": {
    "handle": 330390
  }
}
```

**Example without frame:**
> "Capture just the content area of Chrome"

```json
{
  "name": "capture_window",
  "arguments": {
    "handle": 2884384,
    "includeFrame": false
  }
}
```

**Response:**
```json
{
  "success": true,
  "imageData": "iVBORw0KGgoAAAANSUhEUgAAA...",
  "imageFormat": "png",
  "width": 1920,
  "height": 1040,
  "capturedRegion": { "x": 0, "y": 0, "width": 1920, "height": 1040 },
  "monitorIndex": 0,
  "scaleFactor": 1.0
}
```

> **Note:** Minimized windows cannot be captured. The window must be visible (though it can be behind other windows).

---

### capture_with_elements

Capture a window screenshot with UI element bounding box overlays. Returns an annotated image with element coordinates for click targeting.

**Parameters:**
| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| handle | long | Yes | - | Window handle from find_windows or get_all_windows |
| includeFrame | bool | No | true | Include window frame/decoration |
| format | string | No | "png" | Image format: "png" or "jpeg" |
| quality | int | No | 85 | JPEG quality (1-100) |
| maxDepth | int | No | 5 | Max depth to traverse UI tree (1-10) |
| controlTypes | string | No | - | Comma-separated control types to include (e.g., "Button,Edit") |
| interactableOnly | bool | No | false | Only include enabled/interactable elements |
| visibleOnly | bool | No | true | Only include visible elements |
| highlightInteractable | bool | No | true | Use brighter colors for enabled elements |
| timeoutMs | int | No | 5000 | Timeout for UI element retrieval in ms |
| saveToFile | bool | No | false | Write the image to a file and return `filePath`; `imageData` is null |
| outputPath | string | No | temp file | File path used when saveToFile is true |
| maxWidth | int | No | - | Scale the image down proportionally to at most this width in pixels. `imageBounds` are scaled with the image; `screenBounds` stay in screen pixels |

**Color Coding:**
| Control Type | Color |
|--------------|-------|
| Button, SplitButton | Green |
| Edit, Document | Blue |
| CheckBox, RadioButton | Orange |
| ComboBox, List, ListItem | Purple |
| Hyperlink | Cyan |
| MenuItem, Menu, MenuBar | Red |
| Tab, TabItem | Yellow |
| Image | Gray |
| Text | Light Gray |
| Other | Light Blue |

**Example:**
> "Capture Notepad with UI element overlays"

```json
{
  "name": "capture_with_elements",
  "arguments": {
    "handle": 12345
  }
}
```

**Example with filters:**
> "Capture only buttons and text inputs in VS Code"

```json
{
  "name": "capture_with_elements",
  "arguments": {
    "handle": 330390,
    "controlTypes": "Button,Edit",
    "interactableOnly": true,
    "maxDepth": 3
  }
}
```

**Response:**
```json
{
  "success": true,
  "imageData": "iVBORw0KGgoAAAANSUhEUgAAA...",
  "imageFormat": "png",
  "width": 1200,
  "height": 800,
  "windowHandle": 12345,
  "windowTitle": "Untitled - Notepad",
  "elements": [
    {
      "name": "File",
      "controlType": "MenuItem",
      "automationId": null,
      "imageBounds": { "x": 10, "y": 30, "width": 50, "height": 22 },
      "screenBounds": { "x": 110, "y": 130, "width": 50, "height": 22 },
      "isEnabled": true,
      "overlayColor": "red"
    },
    {
      "name": "Text Editor",
      "controlType": "Edit",
      "automationId": "15",
      "imageBounds": { "x": 0, "y": 52, "width": 1200, "height": 748 },
      "screenBounds": { "x": 100, "y": 152, "width": 1200, "height": 748 },
      "isEnabled": true,
      "overlayColor": "blue"
    }
  ],
  "elementCount": 24,
  "captureElapsedMs": 45.2,
  "annotationElapsedMs": 127.8,
  "monitorScaleFactor": 1.5
}
```

> **Note:** The `imageBounds` coordinates are relative to the output image (scaled when `maxWidth` shrinks it), while `screenBounds` are absolute screen coordinates. Use `imageBounds` for visual reference and `screenBounds` for click automation.

> **Note:** `controlTypes` currently has the same limitation as in [get_ui_elements](#get_ui_elements): matching elements inside non-matching containers are left out.

---

## UI Automation

Tools for retrieving UI element information from windows using Windows UI Automation.

### get_ui_elements

Get the UI element tree with bounding boxes for a window. Provides a lightweight alternative to screenshots when only element coordinates are needed for programmatic interaction planning.

**Parameters:**
| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| handle | long | Yes | - | Window handle from find_windows or get_all_windows |
| maxDepth | int | No | 5 | Max depth to traverse UI tree (1-10) |
| controlTypes | string | No | - | Comma-separated control types to include (e.g., "Button,Edit,ComboBox") |
| interactableOnly | bool | No | false | Only include enabled elements |
| visibleOnly | bool | No | true | Only include visible (on-screen) elements |
| minWidth | int | No | - | Minimum element width to include |
| minHeight | int | No | - | Minimum element height to include |
| timeoutMs | int | No | 10000 | Timeout for UI traversal in ms (max 30000) |

**Supported Control Types:**
AppBar, Button, Calendar, CheckBox, ComboBox, Custom, DataGrid, DataItem, Document, Edit, Group, Header, HeaderItem, Hyperlink, Image, List, ListItem, Menu, MenuBar, MenuItem, Pane, ProgressBar, RadioButton, ScrollBar, SemanticZoom, Separator, Slider, Spinner, SplitButton, StatusBar, Tab, TabItem, Table, Text, Thumb, TitleBar, ToolBar, ToolTip, Tree, TreeItem, Window

AppBar and SemanticZoom (used by UWP/WinUI apps) are named from the element's localized control type, so on a non-English Windows they may appear under a localized name. An element with no recognizable type is reported as `Unknown`.

> **Note:** The `controlTypes` filter currently keeps an element only if every container above it (below the window) also matches the filter. Matching elements nested inside non-matching containers, such as a Button inside a Pane or a MenuItem inside a MenuBar, are left out. If a filtered call returns fewer elements than expected, call again without `controlTypes` and filter the result yourself, or narrow it with `interactableOnly`, `minWidth` and `minHeight`.

**Example:**
> "Get all UI elements from the Notepad window"

```json
{
  "name": "get_ui_elements",
  "arguments": {
    "handle": 12345
  }
}
```

**Example with filters:**
> "Get only buttons and text inputs larger than 20x20 pixels"

```json
{
  "name": "get_ui_elements",
  "arguments": {
    "handle": 12345,
    "controlTypes": "Button,Edit",
    "interactableOnly": true,
    "minWidth": 20,
    "minHeight": 20
  }
}
```

**Response:**
```json
{
  "success": true,
  "windowHandle": 12345,
  "windowTitle": "Untitled - Notepad",
  "elements": [
    {
      "name": "File",
      "controlType": "MenuItem",
      "automationId": null,
      "bounds": { "x": 110, "y": 130, "width": 50, "height": 22 },
      "isEnabled": true,
      "isOffscreen": false,
      "children": null
    },
    {
      "name": "Edit",
      "controlType": "MenuItem",
      "automationId": null,
      "bounds": { "x": 160, "y": 130, "width": 45, "height": 22 },
      "isEnabled": true,
      "isOffscreen": false,
      "children": null
    },
    {
      "name": "Text Editor",
      "controlType": "Edit",
      "automationId": "15",
      "bounds": { "x": 100, "y": 152, "width": 1200, "height": 748 },
      "isEnabled": true,
      "isOffscreen": false,
      "children": null
    }
  ],
  "totalElementCount": 45,
  "depth": 5,
  "elapsedMilliseconds": 127.5,
  "skippedElementCount": 0
}
```

> **Note:** If an element disappears or its UI Automation provider fails while the tree is walked, that element is skipped and the walk continues with its siblings. `skippedElementCount` counts these skips. A value above 0 means the tree may be incomplete, so call again if a control you expect is missing. Errors reading the window itself still fail the call.

**Error Codes:**
| Code | Description |
|------|-------------|
| `InvalidHandle` | Window handle is invalid |
| `WindowNotFound` | Window no longer exists |
| `AutomationUnavailable` | UI Automation not available |
| `ElementAccessFailed` | Could not access window elements |
| `OperationTimedOut` | Traversal exceeded timeout |

---

## Browser Reading

Tools for reading the URL and page text of a browser window without screenshots. They work with Chrome, Edge, Firefox, Brave, Opera, Vivaldi and other Chromium- or Firefox-based browsers.

**How the browser is read:**

- **UI Automation (default):** the URL is read from the address bar and the text from the page's accessibility tree. The URL is the text shown in the address bar, so the scheme (`https://`) can be missing.
- **CDP (Chrome DevTools Protocol):** used when you pass `cdpPort` and a browser started with that remote debugging port is listening on localhost, for example one started by [launch_browser_with_debug](#launch_browser_with_debug). The server matches the window to a tab by title and reads the page with `document.body.innerText` (or the full HTML when `textOnly` is false), so JavaScript-rendered content is included. When CDP cannot be used, the tools fall back to UI Automation.

For clicking, form filling and multi-step navigation inside web pages, a dedicated browser automation tool (such as Playwright MCP) is a better fit.

### get_browser_url

Get the current URL from a browser window's address bar.

**Parameters:**
| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| handle | long | Yes | - | Window handle of the browser, from find_windows or get_foreground_window |
| cdpPort | int | No | - | CDP port of a browser started with `--remote-debugging-port` (for example 9222) |

**Example:**
> "What page is open in Chrome?"

```json
{
  "name": "get_browser_url",
  "arguments": {
    "handle": 2884384
  }
}
```

**Response:**
```json
{
  "success": true,
  "url": "github.com/mortenbrudvik/WindowsAppManagerMcp",
  "browserType": "chrome",
  "pageTitle": "mortenbrudvik/WindowsAppManagerMcp - Google Chrome",
  "errorCode": "None"
}
```

---

### get_active_browser_url

Get the URL from the foreground browser window. This combines get_foreground_window and get_browser_url.

**Parameters:**
| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| cdpPort | int | No | - | CDP port of a browser started with `--remote-debugging-port` |

**Example:**
> "What URL am I looking at?"

```json
{
  "name": "get_active_browser_url",
  "arguments": {}
}
```

**Response:** Same shape as [get_browser_url](#get_browser_url).

**Error Response** (the foreground window is not a browser):
```json
{
  "success": false,
  "error": "Foreground window 'WindowsTerminal' is not a browser",
  "errorCode": "NoBrowserInForeground"
}
```

> **Note:** When an agent calls this tool, the foreground window is often the agent's own terminal or chat window. Prefer get_browser_url with a handle from find_windows.

---

### get_browser_content

Extract the text (or, through CDP, the HTML) of the page shown in a browser window.

**Parameters:**
| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| handle | long | Yes | - | Window handle of the browser, from find_windows or get_foreground_window |
| maxLength | int | No | all | Maximum number of characters to return; longer content is cut off |
| textOnly | bool | No | true | Return text only. With CDP, false returns the full HTML. UI Automation always returns text |
| cdpPort | int | No | - | CDP port of a browser started with `--remote-debugging-port`; includes JavaScript-rendered content |

**Example 1:** Read the visible text
> "Read the page open in Edge"

```json
{
  "name": "get_browser_content",
  "arguments": {
    "handle": 2884384,
    "maxLength": 5000
  }
}
```

**Response:**
```json
{
  "success": true,
  "content": "Skip to content\nmortenbrudvik / WindowsAppManagerMcp\n...",
  "url": "github.com/mortenbrudvik/WindowsAppManagerMcp",
  "browserType": "edge",
  "pageTitle": "mortenbrudvik/WindowsAppManagerMcp - Microsoft Edge",
  "contentLength": 5000,
  "errorCode": "None"
}
```

**Example 2:** Read the HTML of a page in a debug browser
> "Get the HTML of the page in the debug browser"

```json
{
  "name": "get_browser_content",
  "arguments": {
    "handle": 3342410,
    "cdpPort": 9222,
    "textOnly": false,
    "maxLength": 20000
  }
}
```

**Error Codes** (get_browser_url, get_active_browser_url and get_browser_content):
| Code | Description |
|------|-------------|
| `InvalidHandle` | Window handle is invalid |
| `NotABrowser` | The window's process is not a recognized browser |
| `UrlBarNotFound` | The address bar could not be found or read |
| `ContentExtractionFailed` | No content could be extracted |
| `AutomationUnavailable` | UI Automation is not available |
| `NoBrowserInForeground` | The foreground window is not a browser (get_active_browser_url) |
| `OperationException` | Unexpected error; see `error` |

---

### launch_browser_with_debug

Launch Chrome, Edge or Brave with a Chrome DevTools Protocol (CDP) debug port, so that get_browser_content and get_browser_url can use `cdpPort`.

> **Security:** This opens a DevTools port on localhost with no authentication. Any program running on this computer can connect to it and take full control of that browser: read pages and cookies, run JavaScript and navigate. The port stays open until that browser is closed. Launch it only when you need CDP, and close it with [close_window](#close_window) when you are done.

By default the browser starts as a separate instance with a new, empty profile in `%TEMP%\browser-debug-<guid>`. It has none of the logins, cookies, history or extensions of your normal profile, and the folder is not deleted afterwards. Do not set `userDataDir` to your real browser profile, because that would expose its signed-in sessions through the open port.

**Parameters:**
| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| browser | string | No | "chrome" | Browser to launch: "chrome", "edge" or "brave" |
| url | string | No | - | URL to open |
| port | int | No | 9222 | CDP debug port. Pass the same value as `cdpPort` to the reading tools |
| userDataDir | string | No | new temp folder | Profile folder for the browser instance |

**Example:**
> "Open example.com in a Chrome I can read with CDP"

```json
{
  "name": "launch_browser_with_debug",
  "arguments": {
    "browser": "chrome",
    "url": "https://example.com"
  }
}
```

**Response:**
```json
{
  "success": true,
  "processId": 18320,
  "windowHandle": 3342410,
  "cdpPort": 9222,
  "userDataDir": "C:\\Users\\me\\AppData\\Local\\Temp\\browser-debug-6f1c2d3e-4a5b-6c7d-8e9f-0a1b2c3d4e5f"
}
```

**Error Codes:**
| Code | Description |
|------|-------------|
| `UnsupportedBrowser` | `browser` is not chrome, edge or brave |
| `BrowserNotFound` | The browser was not found in Program Files or on PATH |
| `PortInUse` | The port is already in use; pick another port |
| `DebugLaunchFailed` | The browser could not be started. A launch_application error code can also appear here |

---

## Input Simulation

Tools for simulating mouse and keyboard input. Use with `get_ui_elements` to find element coordinates, then calculate center points for clicking. Coordinates are physical virtual-desktop pixels and can be negative (see [Coordinate space](#coordinate-space)).

> **Warning:** Keyboard input is sent to whatever window is in the foreground, which is often the terminal or chat window running the agent. Call `get_foreground_window` immediately before `type_text` or `send_keys` and confirm the target. Close windows with [close_window](#close_window), not Alt+F4. See [Input safety](#input-safety).

Input calls never interleave. If several input tools are called in parallel, each one finishes sending its input before the next one starts, so typed text is not mixed and a click is not split by another mouse move. The order in which parallel calls run is not guaranteed, so make steps that depend on each other one call at a time.

### click

Perform a left mouse click at the specified screen coordinates.

**Parameters:**
| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| x | int | Yes | - | X coordinate (screen position) |
| y | int | Yes | - | Y coordinate (screen position) |

**Example:**
> "Click at position 500, 300"

```json
{
  "name": "click",
  "arguments": {
    "x": 500,
    "y": 300
  }
}
```

**Response:**
```json
{
  "success": true,
  "x": 500,
  "y": 300,
  "clickType": "left"
}
```

**Error Response:**
```json
{
  "success": false,
  "x": 5000,
  "y": 5000,
  "clickType": "left",
  "error": "Coordinates are outside screen bounds",
  "errorCode": "CoordinatesOutOfBounds"
}
```

---

### right_click

Perform a right mouse click at the specified screen coordinates.

**Parameters:**
| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| x | int | Yes | - | X coordinate (screen position) |
| y | int | Yes | - | Y coordinate (screen position) |

**Example:**
> "Right-click at position 600, 400"

```json
{
  "name": "right_click",
  "arguments": {
    "x": 600,
    "y": 400
  }
}
```

**Response:**
```json
{
  "success": true,
  "x": 600,
  "y": 400,
  "clickType": "right"
}
```

---

### double_click

Perform a double left click at the specified screen coordinates. Useful for opening files, selecting words, or other double-click actions.

**Parameters:**
| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| x | int | Yes | - | X coordinate (screen position) |
| y | int | Yes | - | Y coordinate (screen position) |
| delayMs | int | No | 50 | Delay between clicks in milliseconds (range: 10-500) |

**Example:**
> "Double-click at position 200, 150"

```json
{
  "name": "double_click",
  "arguments": {
    "x": 200,
    "y": 150
  }
}
```

**Example with custom delay:**
> "Double-click with 100ms between clicks"

```json
{
  "name": "double_click",
  "arguments": {
    "x": 200,
    "y": 150,
    "delayMs": 100
  }
}
```

**Response:**
```json
{
  "success": true,
  "x": 200,
  "y": 150,
  "clickType": "double"
}
```

---

### mouse_move

Move the mouse cursor to the specified screen coordinates without clicking. Useful for hovering over elements to trigger tooltips or hover states.

**Parameters:**
| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| x | int | Yes | - | X coordinate (screen position) |
| y | int | Yes | - | Y coordinate (screen position) |

**Example:**
> "Move the mouse to position 800, 600"

```json
{
  "name": "mouse_move",
  "arguments": {
    "x": 800,
    "y": 600
  }
}
```

**Response:**
```json
{
  "success": true,
  "x": 800,
  "y": 600
}
```

---

### type_text

Type text by simulating keyboard input. Text is sent to the foreground window using Unicode characters, supporting any language. There is no target window parameter, so confirm the foreground window with `get_foreground_window` first.

**Parameters:**
| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| text | string | Yes | - | The text to type |
| delayMs | int | No | 0 | Delay between keystrokes in milliseconds (0-100). Use for visible typing effect. |

**Example:**
> "Type 'Hello World' into the focused text field"

```json
{
  "name": "type_text",
  "arguments": {
    "text": "Hello World"
  }
}
```

**Example with delay:**
> "Type text slowly with 50ms between each keystroke"

```json
{
  "name": "type_text",
  "arguments": {
    "text": "Typing slowly...",
    "delayMs": 50
  }
}
```

**Response:**
```json
{
  "success": true,
  "text": "Hello World",
  "charactersTyped": 11
}
```

**Error Response:**
```json
{
  "success": false,
  "text": "",
  "charactersTyped": 0,
  "error": "Text cannot be empty",
  "errorCode": "EmptyText"
}
```

---

### send_keys

Send key combinations to the foreground window. Supports special keys and modifier combinations using SendKeys-style syntax. There is no target window parameter, so confirm the foreground window with `get_foreground_window` first, and never send chords that close, quit or delete (such as Alt+F4) without that check.

**Parameters:**
| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| keys | string | Yes | - | Keys to send in SendKeys format (see syntax below) |

**SendKeys Syntax:**

| Syntax | Description | Example |
|--------|-------------|---------|
| `{KEY}` | Special key by name | `{ENTER}`, `{TAB}`, `{F1}` |
| `^` | Control modifier | `^c` = Ctrl+C |
| `%` | Alt modifier | `%f` = Alt+F |
| `+` | Shift modifier | `+{HOME}` = Shift+Home |
| Regular characters | Sent directly | `abc` types "abc" |

**Supported Special Keys:**

| Key Name | Aliases | Description |
|----------|---------|-------------|
| `{ENTER}` | `{RETURN}` | Enter key |
| `{TAB}` | - | Tab key |
| `{ESC}` | `{ESCAPE}` | Escape key |
| `{BACKSPACE}` | `{BS}`, `{BKSP}` | Backspace |
| `{DELETE}` | `{DEL}` | Delete key |
| `{INSERT}` | `{INS}` | Insert key |
| `{UP}` | - | Up arrow |
| `{DOWN}` | - | Down arrow |
| `{LEFT}` | - | Left arrow |
| `{RIGHT}` | - | Right arrow |
| `{HOME}` | - | Home key |
| `{END}` | - | End key |
| `{PGUP}` | `{PAGEUP}` | Page Up |
| `{PGDN}` | `{PAGEDOWN}` | Page Down |
| `{SPACE}` | - | Space bar |
| `{F1}` - `{F12}` | - | Function keys |
| `{CAPSLOCK}` | `{CAPS}` | Caps Lock |
| `{NUMLOCK}` | - | Num Lock |
| `{SCROLLLOCK}` | `{SCROLL}` | Scroll Lock |
| `{PRTSC}` | `{PRINTSCREEN}` | Print Screen |
| `{PAUSE}` | `{BREAK}` | Pause/Break |
| `{WIN}` | `{LWIN}` | Windows key |

**Example 1:** Press Enter
> "Press Enter to confirm"

```json
{
  "name": "send_keys",
  "arguments": {
    "keys": "{ENTER}"
  }
}
```

**Example 2:** Copy (Ctrl+C)
> "Copy selected text"

```json
{
  "name": "send_keys",
  "arguments": {
    "keys": "^c"
  }
}
```

**Example 3:** Select all and delete (Ctrl+A, Delete)
> "Clear all text in the field"

This deletes everything in the focused control. Send it only after `get_foreground_window` confirms the target window and the right field has focus.

```json
{
  "name": "send_keys",
  "arguments": {
    "keys": "^a{DELETE}"
  }
}
```

> **Closing a window:** do not send Alt+F4 (`%{F4}`). It closes whatever window is in the foreground, which may be the agent's own terminal. Use [close_window](#close_window) with the window's handle instead.

**Example 4:** Select to beginning (Shift+Home)
> "Select text from cursor to beginning of line"

```json
{
  "name": "send_keys",
  "arguments": {
    "keys": "+{HOME}"
  }
}
```

**Example 5:** Multiple modifiers (Ctrl+Shift+End)
> "Select from cursor to end of document"

```json
{
  "name": "send_keys",
  "arguments": {
    "keys": "^+{END}"
  }
}
```

**Response:**
```json
{
  "success": true,
  "keys": "^c"
}
```

**Error Response:**
```json
{
  "success": false,
  "keys": "{UNKNOWNKEY}",
  "error": "Unknown key: UNKNOWNKEY",
  "errorCode": "UnknownKey"
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

**How the executable is resolved:**
- **Absolute paths** may contain any character that is valid in a Windows path, including `(x86)`, `&`, `'`, `;`, `$` and `{GUID}`. Only `" < > | * ?` and control characters are rejected. Applications are started with ShellExecute, which does not pass the path through a shell.
- **Bare names and relative paths** (`notepad`, `tools\app.exe`) must not contain shell metacharacters (`` & | ; < > $ ` " ' ( ) { } ``).
- **Bare names** are looked up in the Windows directory, System32, the absolute entries of `PATH` and then App Paths (HKCU, HKLM). The resolved absolute path is what gets started. The current and working directories are **never** searched, so a `notepad.exe` or `code.cmd` planted in a repository cannot hijack the launch. If the name exists only in the working directory, the launch is refused with `ExecutableInWorkingDirectory`; pass the full path to run that file on purpose.
- **`file://` URIs** are converted to their local or UNC path and validated like a plain path. `http`, `https`, `mailto` and `tel` URIs are passed to their handler. Other protocols are blocked (`BlockedProtocol`, `UnknownProtocol`).

**How arguments are passed:**
- Each array element is passed as exactly one argument, quoted with the standard Windows rules. Spaces, embedded quotes, trailing backslashes (`C:\My Folder\`) and empty strings are preserved. Do not add your own quotes.
- **Batch files** (`.bat`/`.cmd`, including shims such as VS Code's `code` → `bin\code.cmd` and `npm` → `npm.cmd`) are run by Windows through `cmd.exe`, which would treat parts of an argument as commands or variables. If the target is a batch file, or cannot be verified to be a native `.exe`/`.com` (URIs, documents, shortcuts, scripts, unresolved names), arguments containing `& | < > ^ % ! "` or control characters are refused with `UnsafeBatchArguments`. To pass such an argument, launch the program's `.exe` by its full path, for example `...\Microsoft VS Code\Code.exe` instead of `code`.

**Error codes:**
| Code | Meaning |
|------|---------|
| `EmptyExecutablePath` | `executable` is empty |
| `InvalidCharacters` | Character not valid in a Windows path, or a shell metacharacter in a bare name or relative path |
| `PathTraversal` | Path contains `..` |
| `BlockedProtocol` / `UnknownProtocol` / `InvalidUri` | URI not allowed or malformed |
| `InvalidWorkingDirectory` | Working directory contains invalid characters |
| `InvalidArguments` | An argument is null or contains a NUL character |
| `UnsafeBatchArguments` | Argument would be interpreted by `cmd.exe` (see above) |
| `ExecutableInWorkingDirectory` | Bare name found only in the current or working directory |
| `ProcessStartFailed` / `LaunchException` | Windows failed to start the target |

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

Close or terminate a process by ID (requires confirmation).

**Parameters:**
| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| processId | int | Yes | - | Process ID to terminate |
| confirm | bool | Yes | - | Must be true to confirm termination |
| forceKill | bool | No | false | `false`: graceful close only. `true`: terminate the process **and all of its child processes** immediately |
| gracefulTimeoutMs | int | No | 5000 | Graceful mode: how long to wait for the process to exit after the close request (0-60000) |
| expectedProcessName | string | No | - | PID-reuse guard: expected name as shown by `list_processes` (`notepad` or `notepad.exe`, case-insensitive) |

**Graceful mode (default) never escalates.** It sends `WM_CLOSE` to the process's main window and waits up to `gracefulTimeoutMs`:
- The process exits: `success: true`.
- The process has no main window that can be closed (windowless, notification-area only, or its window is blocked by a modal dialog): `success: false`, `errorCode: "NoClosableWindow"`. The process is left running.
- The process is still running after the timeout, for example because it is showing a "Save changes?" prompt or runs elevated: `success: false`, `errorCode: "GracefulCloseTimedOut"`. The process is left running.

To end the process anyway, call again with `forceKill: true`. This ends the process and all of its child processes immediately, and unsaved work is lost.

**Force mode** returns `success: true` only after the process has actually exited. Otherwise it returns `TerminationNotConfirmed`.

**Example 1:** Close gracefully
> "Close Notepad (PID 12456)"

```json
{
  "name": "kill_process",
  "arguments": {
    "processId": 12456,
    "confirm": true,
    "expectedProcessName": "notepad"
  }
}
```

**Response (closed):**
```json
{
  "success": true,
  "processId": 12456,
  "processName": "notepad",
  "closeRequested": true,
  "forced": false,
  "exited": true
}
```

**Response (still running, e.g. a save prompt is open):**
```json
{
  "success": false,
  "processId": 12456,
  "processName": "notepad",
  "error": "A close request was sent to 'notepad' (PID 12456), but it was still running after 5000 ms. ...",
  "errorCode": "GracefulCloseTimedOut",
  "closeRequested": true,
  "forced": false,
  "exited": false
}
```

**Example 2:** Force terminate
```json
{
  "name": "kill_process",
  "arguments": {
    "processId": 12456,
    "confirm": true,
    "forceKill": true
  }
}
```

**Response fields:**
| Field | Description |
|-------|-------------|
| success | `true` only when the process is confirmed to have exited |
| closeRequested | A close request (WM_CLOSE) was posted to the main window |
| forced | A forced termination of the process tree was issued |
| exited | The process is confirmed to have exited |
| warning | Additional information, e.g. child processes that could not be terminated |

**Error codes:**
| Code | Meaning |
|------|---------|
| `ConfirmationRequired` | `confirm` was not true |
| `InvalidProcessId` / `InvalidTimeout` | Parameter out of range |
| `ProcessNotFound` | No such process (it may have exited) |
| `ProtectedProcess` | Protected system or shell process (see below) |
| `CannotTerminateSelf` | The PID is this server |
| `CannotTerminateHost` | The PID is an ancestor of this server, such as the terminal or editor hosting the agent |
| `ProcessNameMismatch` | The PID belongs to a different process than `expectedProcessName`; nothing was done |
| `NoClosableWindow` | Graceful mode: no closable main window; process left running |
| `GracefulCloseTimedOut` | Graceful mode: still running after the timeout; process left running |
| `AccessDenied` | The process runs elevated, as another user or is protected; the server needs at least the same privileges |
| `TerminationNotConfirmed` | Force mode: the process had not exited when the wait ended |
| `TerminationException` | Unexpected error |

**Protected Processes:**
The following system and shell processes cannot be terminated:
- System, smss, csrss, wininit, services, lsass, lsm
- svchost, winlogon, dwm, explorer, Registry, Memory Compression
- sihost, ctfmon, StartMenuExperienceHost, ShellExperienceHost, SearchHost, TextInputHost, fontdrvhost

---

## Layout Presets

Tools for saving and restoring window arrangements.

**Where presets are stored:** each preset is a JSON file in `%LOCALAPPDATA%\WindowsAppManagerMcp\layouts`. To use another folder, set the `WAM_LAYOUTS_PATH` environment variable to an absolute folder path. The folder is outside the plugin install, so presets survive plugin updates.

The first time a layout tool runs, the server:
- copies presets saved by older versions into this folder. Older versions kept them in a `layouts` folder next to the executable, inside the versioned plugin folder. The original files are left in place.
- adds the sample presets `work-setup`, `dual-monitor` and `focus-mode`.

Neither step overwrites an existing preset, and a preset you delete is not added back on the next start. Preset files you add or edit by hand are picked up on the next layout tool call, without a restart.

If the folder cannot be created or used (for example, `WAM_LAYOUTS_PATH` points to a read-only location), the layout tools fail with `StorageUnavailable` and a message naming the folder. The other tools keep working.

**Layout error codes:**

| Code | Returned by | Meaning |
|------|-------------|---------|
| `PresetNotFound` | get_layout, apply_layout, delete_layout | No preset has that name |
| `InvalidName` | save_layout, delete_layout | The name is empty or longer than 128 characters |
| `NoWindowsCaptured` | save_layout | No visible window matched the filters, so nothing was saved |
| `SaveFailed` | save_layout | The preset file could not be written |
| `DeleteFailed` | delete_layout | The preset file could not be deleted |
| `WindowNotFound` | apply_layout | No open window matched a placement |
| `LaunchFailed` | apply_layout | A missing app could not be launched, or its window did not appear |
| `ArrangeFailed` | apply_layout | A matched window could not be moved, resized or brought to the foreground |
| `StorageUnavailable` | all layout tools | The layouts folder could not be created or used |

`list_layouts` and `get_layout` return plain data, so they report `PresetNotFound` and `StorageUnavailable` as a tool error (`isError: true`) whose message contains the code. The other layout tools return `success: false` with `error` and `errorCode` fields.

### list_layouts

List all layout presets in the layouts folder, sorted by name.

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

Get full details of a layout preset. If no preset has that name, the call returns a tool error whose message contains `PresetNotFound`.

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

Save the current window arrangement as a preset. Saving under an existing name replaces that preset, and the response has `replaced: true`.

If no visible window matches the filters, nothing is saved (an existing preset with that name is kept) and the call fails with `NoWindowsCaptured`.

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
  "windowCount": 4,
  "replaced": false
}
```

**Failure response:**
```json
{
  "success": false,
  "layoutName": "coding-setup",
  "windowCount": 0,
  "error": "No visible windows matched the capture filters, so nothing was saved. Check includeProcesses/excludeProcesses.",
  "errorCode": "NoWindowsCaptured",
  "replaced": false
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

`success` is true only when every placement was arranged. If a window is not found, cannot be launched, or cannot be arranged or focused, `success` is false, `errorCode` names the first problem, and `errors` lists every problem. The counts show what was done.

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

**Partial response** (one app was not running and `launchMissing` was false):
```json
{
  "success": false,
  "presetName": "work-setup",
  "windowsArranged": 2,
  "windowsNotFound": 1,
  "windowsLaunched": 0,
  "errors": [
    "No open window matched placement 3 (process 'WindowsTerminal'); set launchMissing=true to launch 'wt'"
  ],
  "errorCode": "WindowNotFound"
}
```

---

### delete_layout

Delete a layout preset. If no preset has that name, the call fails with `PresetNotFound`. A preset defined in a hand-named file is deleted from that file.

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

**Failure response:**
```json
{
  "success": false,
  "error": "Layout preset 'old-setup' was not found",
  "errorCode": "PresetNotFound"
}
```

---

## Browser Reading

Tools for reading the URL and page content of browser windows. By default they use Windows UI Automation. When you pass `cdpPort` (the `--remote-debugging-port` of a Chromium browser, for example one started with `launch_browser_with_debug`), they read the page through the Chrome DevTools Protocol (CDP) first, which sees JavaScript-rendered content and can return HTML.

**How CDP reading works:**
- **Never hangs.** Every CDP request (target list, WebSocket connect, send and receive) is bounded by `timeoutMs` (default 10000, clamped to 1000-30000), and MCP request cancellation is honored. A page with an open JavaScript dialog (alert, confirm, prompt, beforeunload) never answers CDP, so the call returns `CdpDialogOpen` or `CdpTimeout` instead of blocking.
- **Right browser.** The process listening on `cdpPort` must be the process that owns the window. If it is not (for example the handle is your normal Chrome window but the port belongs to a separate debug Chrome), the result is `CdpPortOwnerMismatch`.
- **Right tab.** The tab is the one whose whole title starts the window title, followed only by the browser's own decorations (`Inbox - Google Chrome`, or Edge's `Inbox and 2 more pages - Personal - Microsoft Edge`). Substrings, empty titles and URL hosts never match. If no tab matches, the result is `CdpTargetNotFound`; if several tabs have the same title, it is `CdpTargetAmbiguous`. The tool never guesses.
- **No silent fallback.** If CDP cannot be used, the tool falls back to UI Automation and explains why in `cdpError` (`"Code: message"`). With `requireCdp: true`, the CDP error is returned instead of falling back.
- **Visible source.** `source` is `"cdp"` or `"uia"` and says which method produced the result; `targetId` is the CDP tab ID when `source` is `"cdp"`.
- **HTML needs CDP.** `textOnly: false` returns HTML only through CDP. Without a usable CDP connection the result is `HtmlRequiresCdp` instead of plain text.

### get_browser_url

Get the current URL from a browser window: from the address bar with UI Automation, or from the matched tab with CDP.

**Parameters:**
| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| handle | long | Yes | - | Window handle of the browser from find_windows or get_foreground_window |
| cdpPort | int | No | - | CDP debug port of the browser that owns the window |
| requireCdp | bool | No | false | Return the CDP error instead of falling back to UI Automation (needs cdpPort) |
| timeoutMs | int | No | 10000 | Time limit for the CDP requests of this call in ms (1000-30000) |

**Example:**
> "What page is the debug Chrome window showing?"

```json
{
  "name": "get_browser_url",
  "arguments": {
    "handle": 12345,
    "cdpPort": 9222
  }
}
```

**Response:**
```json
{
  "success": true,
  "url": "https://github.com/pulls",
  "browserType": "chrome",
  "pageTitle": "Pull requests",
  "error": null,
  "errorCode": "None",
  "source": "cdp",
  "targetId": "8C1F0A6E2B7D4F3A9E5C1B2D3F4A5B6C",
  "cdpError": null
}
```

**Response after a fallback to UI Automation:**
```json
{
  "success": true,
  "url": "https://github.com/pulls",
  "browserType": "chrome",
  "pageTitle": "Pull requests - Google Chrome",
  "error": null,
  "errorCode": "None",
  "source": "uia",
  "targetId": null,
  "cdpError": "CdpPortOwnerMismatch: CDP port 9222 belongs to process 4242 (chrome), but the window belongs to process 1337 (chrome). ..."
}
```

### get_browser_content

Extract the content of the page shown in a browser window. UI Automation returns visible text; CDP returns `document.body.innerText` (text) or `document.documentElement.outerHTML` (HTML).

**Parameters:**
| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| handle | long | Yes | - | Window handle of the browser |
| maxLength | int | No | - | Maximum content length in characters |
| textOnly | bool | No | true | true for text; false for full HTML (requires CDP) |
| cdpPort | int | No | - | CDP debug port of the browser that owns the window |
| requireCdp | bool | No | false | Return the CDP error instead of falling back to UI Automation (needs cdpPort) |
| timeoutMs | int | No | 10000 | Time limit for the CDP requests of this call in ms (1000-30000) |

**Example:**
> "Read the HTML of the page in the debug browser, but fail rather than fall back"

```json
{
  "name": "get_browser_content",
  "arguments": {
    "handle": 12345,
    "textOnly": false,
    "cdpPort": 9222,
    "requireCdp": true
  }
}
```

**Response when the page has an alert open:**
```json
{
  "success": false,
  "content": null,
  "url": null,
  "browserType": "chrome",
  "pageTitle": "Checkout - Google Chrome",
  "contentLength": null,
  "error": "The page has a JavaScript alert dialog open ('Are you sure?'), so it cannot be read via CDP. Dismiss the dialog (for example press Enter or Escape in the browser window) and retry.",
  "errorCode": "CdpDialogOpen",
  "source": "cdp",
  "targetId": "8C1F0A6E2B7D4F3A9E5C1B2D3F4A5B6C",
  "cdpError": "CdpDialogOpen: The page has a JavaScript alert dialog open ('Are you sure?'), ..."
}
```

### get_active_browser_url

Get the URL from the foreground browser window. Same parameters (except `handle`), CDP behavior and response fields as `get_browser_url`.

**Parameters:**
| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| cdpPort | int | No | - | CDP debug port of the browser that owns the window |
| requireCdp | bool | No | false | Return the CDP error instead of falling back to UI Automation (needs cdpPort) |
| timeoutMs | int | No | 10000 | Time limit for the CDP requests of this call in ms (1000-30000) |

**Error Codes (browser reading tools):**
| Code | Description |
|------|-------------|
| `InvalidHandle` | Window handle is invalid |
| `NotABrowser` / `NoBrowserInForeground` | The window is not a recognized browser |
| `UrlBarNotFound` / `ContentExtractionFailed` | UI Automation could not read the address bar or page |
| `AutomationUnavailable` | UI Automation is not available |
| `CdpUnavailable` | Nothing answered on the CDP port, or `requireCdp` was set without `cdpPort` |
| `CdpTimeout` | The browser or page did not answer within `timeoutMs` (often an open JavaScript dialog) |
| `CdpDialogOpen` | The page has a JavaScript dialog open; dismiss it and retry |
| `CdpPortOwnerMismatch` | The CDP port belongs to a different process than the window |
| `CdpTargetNotFound` | No tab's title matches the window title |
| `CdpTargetAmbiguous` | Several tabs have the window's title; the tool will not guess |
| `CdpEvaluationFailed` | The page threw, or the browser rejected the script |
| `CdpProtocolError` | The CDP connection closed early or returned an unreadable response |
| `HtmlRequiresCdp` | `textOnly: false` was requested but CDP could not be used |

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

### Click Automation Workflow

1. Find the window:
```
find_windows { "processName": "notepad" }
```

2. Get UI elements with bounding boxes:
```
get_ui_elements { "handle": 12345, "interactableOnly": true }
```

3. Find the target element in the response and calculate center:
   - Element bounds: `{ "x": 100, "y": 200, "width": 80, "height": 30 }`
   - Center: x = 100 + 80/2 = 140, y = 200 + 30/2 = 215

4. Bring the window to the front and confirm it (check that the returned `handle` is 12345):
```
focus_window { "handle": 12345 }
get_foreground_window {}
```

5. Click the element:
```
click { "x": 140, "y": 215 }
```

6. Verify the result, for example with `get_ui_elements`, a capture or `get_foreground_window`.

### Keyboard Automation Workflow

1. Focus the target window:
```
focus_window { "handle": 12345 }
```

2. Click on a text input field (get coordinates from get_ui_elements):
```
click { "x": 500, "y": 300 }
```

3. Confirm the foreground window before typing. Stop if `handle` is not 12345:
```
get_foreground_window {}
```

4. Type text into the field:
```
type_text { "text": "Hello World" }
```

5. Use keyboard shortcuts (check `get_foreground_window` again if anything else could have taken focus):
```
send_keys { "keys": "^a" }       // Select all
send_keys { "keys": "^c" }       // Copy
send_keys { "keys": "{TAB}" }    // Move to next field
send_keys { "keys": "^v" }       // Paste
send_keys { "keys": "{ENTER}" }  // Submit
```

### Form Automation Example

Complete a form with multiple fields:

```
// 1. Focus the window
focus_window { "handle": 12345 }

// 2. Click first field
click { "x": 500, "y": 100 }

// 3. Confirm the form window is in front (handle 12345) before typing
get_foreground_window {}

// 4. Type name
type_text { "text": "John Doe" }

// 5. Tab to next field
send_keys { "keys": "{TAB}" }

// 6. Type email
type_text { "text": "john@example.com" }

// 7. Tab to next field
send_keys { "keys": "{TAB}" }

// 8. Type message
type_text { "text": "This is my message" }

// 9. Confirm the form window is still in front, then submit
get_foreground_window {}
send_keys { "keys": "{ENTER}" }
```

---

## Error Codes

### Unexpected tool errors

If a tool throws an unexpected exception, or its result cannot be serialized, the call returns
`isError: true` with a JSON body that names the exception, and the server logs the exception with
its stack trace to stderr:

```json
{
  "success": false,
  "error": "Tool 'get_primary_monitor' failed. InvalidOperationException: No monitors found",
  "errorCode": "UnhandledException",
  "exceptionType": "System.InvalidOperationException",
  "tool": "get_primary_monitor"
}
```

An unknown tool name is still reported as a JSON-RPC protocol error, not as a tool result.

### Error code reference

| Code | Description |
|------|-------------|
| `UnhandledException` | The tool threw an unexpected exception (see above) |
| `WindowNotFound` | Window handle is invalid or window closed |
| `InvalidHandle` | Handle parameter is missing or invalid |
| `OperationFailed` | Window operation failed (permission denied, etc.) |
| `PathTraversal` | Security: path traversal attempt detected |
| `InvalidExecutable` | Invalid or blocked executable path |
| `ProtectedProcess` | Cannot terminate protected system process |
| `ConfirmationRequired` | Kill operation requires confirm=true |
| `PresetNotFound` | Layout preset doesn't exist |
| `InvalidName` | Layout preset name is empty or too long |
| `StorageUnavailable` | Layout presets folder cannot be created or used (see [Layout Presets](#layout-presets)) |
| `CoordinatesOutOfBounds` | Click coordinates outside screen bounds |
| `SendInputFailed` | SendInput API call failed |
| `EmptyText` | Text or keys string is empty |
| `InvalidKeySpecification` | Invalid key specification format |
| `UnknownKey` | Unknown special key name in braces |
