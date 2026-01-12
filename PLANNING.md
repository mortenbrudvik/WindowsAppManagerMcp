# Windows App Manager MCP Server - Implementation Plan

## Project Overview

Create a Claude Code MCP (Model Context Protocol) server that enables Claude to:
1. **Launch applications** on Windows
2. **Discover windows** by title, process name, PID, or window handle
3. **Control windows** - move, resize, snap, focus, minimize/maximize/restore
4. **Manage displays** - get monitor information, move windows between monitors
5. **Layout presets** - save and apply named window arrangements

## Technology Stack

| Component | Choice | Rationale |
|-----------|--------|-----------|
| **Runtime** | C#/.NET 8+ | Native Windows interop via P/Invoke, excellent async handling |
| **MCP SDK** | ModelContextProtocol NuGet | Official C# SDK for MCP servers |
| **Windows APIs** | P/Invoke (user32.dll, kernel32.dll) | Direct access to all window management APIs |
| **Transport** | STDIO | Standard for local MCP servers |
| **Configuration** | JSON files | Layout presets stored as JSON |

---

## Architecture Overview

```
┌─────────────────────────────────────────────────────────────────┐
│                     MCP CLIENT (Claude)                          │
└─────────────────────────────────────────────────────────────────┘
                              │ JSON-RPC 2.0 / STDIO
                              ▼
┌─────────────────────────────────────────────────────────────────┐
│                     MCP SERVER LAYER                             │
│   McpServer + StdioServerTransport + Tool Registration          │
└─────────────────────────────────────────────────────────────────┘
                              │
                              ▼
┌─────────────────────────────────────────────────────────────────┐
│                        TOOL LAYER                                │
│  ┌─────────────┐ ┌─────────────┐ ┌─────────────┐ ┌───────────┐  │
│  │ AppLauncher │ │ WindowFind  │ │ WindowCtrl  │ │ MonitorInfo│ │
│  │ Tools       │ │ Tools       │ │ Tools       │ │ Tools      │ │
│  └─────────────┘ └─────────────┘ └─────────────┘ └───────────┘  │
│                    ┌─────────────┐                               │
│                    │ LayoutPreset│                               │
│                    │ Tools       │                               │
│                    └─────────────┘                               │
└─────────────────────────────────────────────────────────────────┘
                              │
                              ▼
┌─────────────────────────────────────────────────────────────────┐
│                      SERVICE LAYER                               │
│  ProcessService | WindowService | MonitorService | LayoutService │
└─────────────────────────────────────────────────────────────────┘
                              │
                              ▼
┌─────────────────────────────────────────────────────────────────┐
│                   NATIVE INTEROP LAYER                           │
│        P/Invoke: user32.dll | kernel32.dll | shcore.dll         │
└─────────────────────────────────────────────────────────────────┘
```

---

## MCP Tools (18 Total)

### Application Management (2 tools)
| Tool | Description |
|------|-------------|
| `launch_application` | Launch app by path/name with optional args and working directory |
| `list_processes` | List running processes with optional name filter |

### Window Discovery (3 tools)
| Tool | Description |
|------|-------------|
| `find_windows` | Find windows by title (partial), process name, PID, or handle |
| `get_all_windows` | List all visible top-level windows |
| `get_foreground_window` | Get the currently focused window |

### Window Control (7 tools)
| Tool | Description |
|------|-------------|
| `move_window` | Move window to x,y position |
| `resize_window` | Resize window to width,height |
| `set_window_bounds` | Move and resize in one operation |
| `set_window_state` | Minimize, maximize, or restore |
| `focus_window` | Bring window to foreground |
| `snap_window` | Snap to positions (left_half, right_half, quarters, thirds, etc.) |
| `move_window_to_monitor` | Move window to specific monitor |

### Monitor Information (2 tools)
| Tool | Description |
|------|-------------|
| `get_monitors` | Get all connected monitors with bounds and work area |
| `get_primary_monitor` | Get primary monitor details |

### Layout Management (5 tools)
| Tool | Description |
|------|-------------|
| `save_layout` | Save current window arrangement as named preset |
| `apply_layout` | Apply a saved layout preset |
| `list_layouts` | List all saved layout presets |
| `get_layout` | Get details of a specific preset |
| `delete_layout` | Delete a layout preset |

---

## File Structure

```
WindowsAppManagerMcp/
├── WindowsAppManagerMcp.sln
├── README.md
├── PLANNING.md
├── .gitignore
│
├── src/
│   └── WindowsAppManagerMcp/
│       ├── WindowsAppManagerMcp.csproj
│       ├── Program.cs                       # Entry point, DI, MCP server setup
│       │
│       ├── Tools/                           # MCP Tool definitions
│       │   ├── AppLauncherTools.cs
│       │   ├── WindowFinderTools.cs
│       │   ├── WindowManagerTools.cs
│       │   ├── MonitorInfoTools.cs
│       │   └── LayoutPresetTools.cs
│       │
│       ├── Services/                        # Business logic
│       │   ├── Interfaces/
│       │   │   ├── IProcessService.cs
│       │   │   ├── IWindowService.cs
│       │   │   ├── IMonitorService.cs
│       │   │   └── ILayoutService.cs
│       │   ├── ProcessService.cs
│       │   ├── WindowService.cs
│       │   ├── MonitorService.cs
│       │   └── LayoutService.cs
│       │
│       ├── Models/                          # Data models
│       │   ├── WindowInfo.cs
│       │   ├── MonitorInfo.cs
│       │   ├── LayoutPreset.cs
│       │   ├── WindowPlacement.cs
│       │   └── Results/
│       │       ├── LaunchResult.cs
│       │       └── LayoutApplyResult.cs
│       │
│       ├── Native/                          # P/Invoke
│       │   ├── NativeMethods.User32.cs
│       │   ├── NativeMethods.Kernel32.cs
│       │   ├── NativeStructs.cs
│       │   └── NativeEnums.cs
│       │
│       └── Configuration/
│           └── AppSettings.cs
│
├── tests/
│   └── WindowsAppManagerMcp.Tests/
│       └── Services/
│           ├── WindowServiceTests.cs
│           └── LayoutServiceTests.cs
│
└── config/
    ├── appsettings.json
    └── layouts/                             # Preset storage
        └── example-layouts.json
```

---

## Key Windows APIs (P/Invoke)

### user32.dll - Window Management
- `EnumWindows` - Enumerate all top-level windows
- `GetWindowText` / `GetWindowTextLength` - Get window title
- `GetWindowThreadProcessId` - Get process ID from window
- `IsWindowVisible` / `IsIconic` / `IsZoomed` - Window state checks
- `GetWindowRect` - Get window position/size
- `SetWindowPos` - Move/resize windows (core API)
- `ShowWindow` - Minimize/maximize/restore
- `SetForegroundWindow` - Focus window
- `EnumDisplayMonitors` / `GetMonitorInfo` - Monitor enumeration
- `MonitorFromWindow` - Get monitor containing window

### kernel32.dll - Process Management
- `OpenProcess` / `CloseHandle` - Process handle management
- `QueryFullProcessImageName` - Get executable path

---

## Data Models

### WindowInfo
```csharp
public record WindowInfo(
    nint Handle,
    string Title,
    string ProcessName,
    int ProcessId,
    WindowRect Bounds,
    WindowState State,      // Normal, Minimized, Maximized
    bool IsVisible,
    int MonitorIndex
);
```

### MonitorInfo
```csharp
public record MonitorInfo(
    int Index,
    string DeviceName,
    bool IsPrimary,
    MonitorRect Bounds,     // Full monitor area
    MonitorRect WorkArea,   // Excluding taskbar
    double ScaleFactor
);
```

### LayoutPreset
```csharp
public record LayoutPreset(
    string Name,
    string? Description,
    List<WindowPlacement> Placements
);

public record WindowPlacement(
    WindowMatcher Matcher,      // How to find the window
    int MonitorIndex,
    RelativePosition Position,  // X, Y, Width, Height as 0.0-1.0
    string? LaunchCommand,      // Optional: launch if not found
    int Order
);
```

---

## Snap Positions

The `snap_window` tool supports these predefined positions:

| Position | Description |
|----------|-------------|
| `left_half` | Left 50% of monitor |
| `right_half` | Right 50% of monitor |
| `top_half` | Top 50% of monitor |
| `bottom_half` | Bottom 50% of monitor |
| `top_left_quarter` | Top-left 25% |
| `top_right_quarter` | Top-right 25% |
| `bottom_left_quarter` | Bottom-left 25% |
| `bottom_right_quarter` | Bottom-right 25% |
| `left_third` | Left 33% |
| `center_third` | Center 33% |
| `right_third` | Right 33% |
| `fullscreen` | Full work area |

---

## Implementation Phases

### Phase 1: Foundation (Core Infrastructure)
1. Create solution and project structure
2. Set up MCP server with STDIO transport
3. Implement P/Invoke native methods (user32.dll basics)
4. Create base data models (WindowInfo, MonitorInfo)

### Phase 2: Window Discovery
5. Implement WindowService (EnumWindows, GetWindowText, etc.)
6. Implement MonitorService (EnumDisplayMonitors)
7. Create discovery tools: `find_windows`, `get_all_windows`, `get_foreground_window`
8. Create monitor tools: `get_monitors`, `get_primary_monitor`

### Phase 3: Window Control
9. Implement window manipulation (SetWindowPos, ShowWindow)
10. Create control tools: `move_window`, `resize_window`, `set_window_bounds`
11. Add state management: `set_window_state`, `focus_window`
12. Implement snap positions: `snap_window`, `move_window_to_monitor`

### Phase 4: Application Launch
13. Implement ProcessService (Process.Start, enumeration)
14. Create app tools: `launch_application`, `list_processes`

### Phase 5: Layout Presets
15. Implement LayoutService (JSON persistence)
16. Create layout tools: `save_layout`, `apply_layout`, `list_layouts`, `get_layout`, `delete_layout`

### Phase 6: Polish & Testing
17. Error handling improvements
18. DPI awareness handling
19. Unit tests for services
20. Integration testing with Claude

---

## Configuration

### appsettings.json
```json
{
  "WindowsAppManager": {
    "LayoutPresetsPath": "./layouts",
    "DefaultWaitTimeout": 5000,
    "DpiAware": true
  }
}
```

### Example Layout Preset (layouts/work-setup.json)
```json
{
  "name": "work-setup",
  "description": "IDE left, browser right, terminal bottom-right",
  "placements": [
    {
      "matcher": { "processName": "Code" },
      "monitorIndex": 0,
      "position": { "x": 0, "y": 0, "width": 0.5, "height": 1.0 },
      "launchCommand": "code",
      "order": 1
    },
    {
      "matcher": { "processName": "chrome" },
      "monitorIndex": 0,
      "position": { "x": 0.5, "y": 0, "width": 0.5, "height": 0.6 },
      "order": 2
    },
    {
      "matcher": { "processName": "WindowsTerminal" },
      "monitorIndex": 0,
      "position": { "x": 0.5, "y": 0.6, "width": 0.5, "height": 0.4 },
      "launchCommand": "wt",
      "order": 3
    }
  ]
}
```

---

## Claude Desktop Integration

Add to Claude Desktop config (`claude_desktop_config.json`):

```json
{
  "mcpServers": {
    "windows-app-manager": {
      "command": "dotnet",
      "args": ["run", "--project", "C:\\path\\to\\WindowsAppManagerMcp"]
    }
  }
}
```

Or for published executable:
```json
{
  "mcpServers": {
    "windows-app-manager": {
      "command": "C:\\path\\to\\WindowsAppManagerMcp.exe"
    }
  }
}
```

---

## Verification Plan

### Manual Testing
1. Launch the MCP server and connect via Claude Desktop
2. Test each tool category:
   - Launch notepad, verify PID returned
   - Find windows by title partial match
   - Move a window to specific coordinates
   - Snap window to left half of screen
   - Move window to second monitor (if available)
   - Save current layout as "test-layout"
   - Close windows, apply "test-layout", verify restoration

### Automated Testing
1. Unit tests for WindowService (mock native methods)
2. Unit tests for LayoutService (JSON serialization)
3. Integration tests with real windows (optional, requires display)

---

## Critical Files to Implement First

1. **`src/WindowsAppManagerMcp/Program.cs`** - Entry point with MCP server setup
2. **`src/WindowsAppManagerMcp/Native/NativeMethods.User32.cs`** - Core P/Invoke declarations
3. **`src/WindowsAppManagerMcp/Services/WindowService.cs`** - Window enumeration and manipulation
4. **`src/WindowsAppManagerMcp/Tools/WindowFinderTools.cs`** - Window discovery tools
5. **`src/WindowsAppManagerMcp/Tools/WindowManagerTools.cs`** - Window control tools

---

## Risk Considerations

| Risk | Mitigation |
|------|------------|
| Window handles become invalid | Validate handles before operations, return clear errors |
| DPI scaling issues | Set DPI awareness at startup, use per-monitor DPI |
| SetForegroundWindow restrictions | Use BringWindowToTop as fallback |
| Process elevation requirements | Document elevation needs for certain operations |
| Multi-monitor edge cases | Test thoroughly with various monitor configurations |

---

## MCP Tool Detailed Specifications

### launch_application

**Input Parameters:**
| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| `executable` | string | Yes | Path to executable or app name (e.g., "notepad.exe", "C:\Program Files\App\app.exe") |
| `arguments` | string[] | No | Command-line arguments |
| `workingDirectory` | string | No | Working directory for the app |
| `waitForWindow` | bool | No | Wait for window to appear (default: false) |
| `timeoutMs` | int | No | Timeout in ms when waiting (default: 5000) |

**Output:**
```json
{
  "success": true,
  "processId": 12345,
  "windowHandle": 67890,
  "error": null
}
```

---

### find_windows

**Input Parameters:**
| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| `title` | string | No | Window title partial match (case-insensitive) |
| `processName` | string | No | Process name (e.g., "chrome.exe") |
| `processId` | int | No | Process ID |
| `handle` | long | No | Window handle (HWND) |
| `visibleOnly` | bool | No | Only visible windows (default: true) |

**Output:**
```json
{
  "windows": [
    {
      "handle": 12345678,
      "title": "Document.txt - Notepad",
      "processName": "notepad.exe",
      "processId": 5678,
      "bounds": { "x": 100, "y": 100, "width": 800, "height": 600 },
      "state": "normal",
      "isVisible": true,
      "monitorIndex": 0
    }
  ],
  "totalFound": 1
}
```

---

### snap_window

**Input Parameters:**
| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| `handle` | long | Yes | Window handle |
| `position` | string | Yes | Snap position (see Snap Positions table) |
| `monitorIndex` | int | No | Target monitor (default: current) |

**Output:**
```json
{
  "success": true,
  "newBounds": { "x": 0, "y": 0, "width": 960, "height": 1040 },
  "error": null
}
```

---

### save_layout

**Input Parameters:**
| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| `name` | string | Yes | Name for the layout preset |
| `description` | string | No | Description of the layout |
| `includeProcesses` | string[] | No | Only include these processes (empty = all) |
| `excludeProcesses` | string[] | No | Exclude these processes |

**Output:**
```json
{
  "success": true,
  "layoutId": "work-setup",
  "windowCount": 4,
  "error": null
}
```

---

### apply_layout

**Input Parameters:**
| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| `name` | string | Yes | Name of the layout preset |
| `matchBy` | string | No | How to match windows: "process_and_title", "process_only", "title_only" (default: "process_and_title") |
| `launchMissing` | bool | No | Launch apps for missing windows (default: false) |

**Output:**
```json
{
  "success": true,
  "windowsRestored": 3,
  "windowsNotFound": [
    { "processName": "Slack.exe", "title": "Slack" }
  ],
  "windowsLaunched": 0,
  "error": null
}
```
