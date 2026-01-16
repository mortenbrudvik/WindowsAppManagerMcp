# Windows App Manager MCP Server

[![Build](https://github.com/mortenbrudvik/WindowsAppManagerMcp/actions/workflows/build.yml/badge.svg)](https://github.com/mortenbrudvik/WindowsAppManagerMcp/actions/workflows/build.yml)
[![Test](https://github.com/mortenbrudvik/WindowsAppManagerMcp/actions/workflows/test.yml/badge.svg)](https://github.com/mortenbrudvik/WindowsAppManagerMcp/actions/workflows/test.yml)
![Coverage](https://img.shields.io/badge/coverage-70%25-green)

An MCP (Model Context Protocol) server that enables AI assistants like Claude to manage Windows applications, windows, and desktop layouts.

## Features

- **Window Discovery** - Find and list windows by title, process name, or handle
- **Window Control** - Move, resize, minimize, maximize, snap, and close windows
- **Multi-Monitor Support** - Move windows between monitors, get display information
- **Screenshot Capture** - Capture screens, regions, or windows for AI visual orientation
- **Application Launch** - Start applications with arguments, open URLs
- **Process Management** - List running processes, terminate processes safely
- **Layout Presets** - Save and restore window arrangements

## Installation

### Prerequisites

- Windows 10/11
- .NET 8.0 Runtime (or use self-contained build)

### Option 1: Claude Code Plugin (Recommended)

Install as a Claude Code plugin:

```bash
# Add the marketplace
/plugin marketplace add mortenbrudvik/WindowsAppManagerMcp

# Install the plugin
/plugin install windows-app-manager@windows-app-manager-marketplace
```

Then restart Claude Code. The plugin includes a skill with usage guidance.

### Option 2: Claude Desktop (Manual)

Add to your `claude_desktop_config.json` (located at `%APPDATA%\Claude\`):

```json
{
  "mcpServers": {
    "windows-app-manager": {
      "command": "C:\\path\\to\\WindowsAppManagerMcp.exe"
    }
  }
}
```

Then restart Claude Desktop.

### Option 3: Build from Source

```bash
git clone https://github.com/mortenbrudvik/WindowsAppManagerMcp.git
cd WindowsAppManagerMcp
dotnet publish src/WindowsAppManagerMcp -c Release -r win-x64 --self-contained -o ./bin
```

### Option 4: Download Release

Download the latest release from the [Releases](https://github.com/mortenbrudvik/WindowsAppManagerMcp/releases) page.

## Available Tools (30)

| Category | Tools |
|----------|-------|
| Window Discovery | `find_windows`, `get_all_windows`, `get_foreground_window` |
| Window Control | `move_window`, `resize_window`, `set_window_bounds`, `set_window_state`, `focus_window`, `close_window`, `snap_window`, `move_window_to_monitor` |
| Batch Operations | `set_windows_bounds_batch`, `snap_windows_batch`, `set_windows_state_batch`, `close_windows_batch`, `launch_applications_batch` |
| Monitor Info | `get_monitors`, `get_primary_monitor` |
| Screenshot | `list_screens`, `take_screenshot`, `capture_region`, `capture_window` |
| App Launch | `launch_application`, `list_processes`, `kill_process` |
| Layout Presets | `list_layouts`, `get_layout`, `save_layout`, `apply_layout`, `delete_layout` |

See [docs/TOOLS.md](docs/TOOLS.md) for complete documentation with examples.

## Usage Examples

Once configured, you can ask Claude to:

### Window Management
- "List all open windows"
- "Find all Chrome windows"
- "Snap VS Code to the left half of the screen"
- "Move Terminal to the second monitor"
- "Maximize the Notepad window"
- "Close all Notepad windows"

### Application Launch
- "Open Notepad"
- "Launch Chrome and go to github.com"
- "Open VS Code in C:\Projects\MyApp"

### Layout Management
- "Save my current window arrangement as 'work-setup'"
- "Show my saved layouts"
- "Apply my 'coding' layout"
- "Restore my dual-monitor layout and launch any missing apps"

### Multi-Monitor
- "Show all connected monitors"
- "Move Chrome to monitor 2 and maximize it"
- "Snap Terminal to the right third of the primary monitor"

### Screenshots
- "Take a screenshot of the primary monitor"
- "Capture the VS Code window"
- "Take a screenshot of monitor 0"
- "Capture a 800x600 region at position 100, 200"

### Batch Operations
- "Launch Notepad and Calculator, then snap them side by side"
- "Minimize all browser windows"
- "Position these three windows in a row"

## Snap Positions

The `snap_window` tool supports 14 predefined positions:

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

## Example Layout Presets

The `config/layouts/` folder contains example presets:

- `work-setup.json` - Typical development workspace
- `dual-monitor.json` - Multi-monitor configuration
- `focus-mode.json` - Single maximized window

## Building

```bash
# Build
dotnet build

# Run tests
dotnet test

# Publish self-contained executable
dotnet publish src/WindowsAppManagerMcp -c Release -r win-x64 --self-contained -o ./publish
```

## Testing

The project includes comprehensive tests:

```bash
# Run all tests
dotnet test

# Run with coverage
dotnet test --collect:"XPlat Code Coverage"
```

- **482 tests** covering unit and integration scenarios
- ~70% code coverage

## Project Structure

```
WindowsAppManagerMcp/
├── .claude-plugin/      # Claude Code plugin configuration
│   ├── plugin.json      # Plugin metadata and MCP server config
│   └── marketplace.json # Marketplace distribution manifest
├── skills/              # Claude Code skills
│   └── using-windows-app-manager/
│       └── SKILL.md     # Usage guidance and trigger patterns
├── bin/                 # Pre-built executable (for plugin)
├── src/WindowsAppManagerMcp/
│   ├── Models/          # Data models and DTOs
│   ├── Native/          # P/Invoke declarations
│   ├── Services/        # Business logic
│   └── Tools/           # MCP tool definitions
├── tests/               # Unit and integration tests
├── config/layouts/      # Example layout presets
└── docs/                # Documentation
```

## Security

- **Input Validation** - Path traversal and shell injection prevention
- **Protected Processes** - System-critical processes cannot be terminated
- **Confirmation Required** - Process termination requires explicit confirmation
- **Graceful Shutdown** - Windows are closed gracefully (WM_CLOSE)

## Requirements

- Windows 10 version 1607+ or Windows 11
- .NET 8.0 Runtime (or self-contained build)
- DPI awareness enabled for accurate positioning on high-DPI displays

## License

MIT License - see [LICENSE](LICENSE) for details.

## Contributing

Contributions are welcome! Please feel free to submit issues and pull requests.

## Acknowledgments

- Built with [Model Context Protocol SDK](https://github.com/modelcontextprotocol/csharp-sdk)
- Inspired by PowerToys FancyZones and Windows Snap Assist
