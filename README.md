# Windows App Manager MCP Server

[![Build](https://github.com/mortenbrudvik/WindowsAppManagerMcp/actions/workflows/build.yml/badge.svg)](https://github.com/mortenbrudvik/WindowsAppManagerMcp/actions/workflows/build.yml)
[![Test](https://github.com/mortenbrudvik/WindowsAppManagerMcp/actions/workflows/test.yml/badge.svg)](https://github.com/mortenbrudvik/WindowsAppManagerMcp/actions/workflows/test.yml)
![Coverage](https://img.shields.io/badge/coverage-60%25-yellowgreen)

An MCP (Model Context Protocol) server that enables AI assistants like Claude to manage Windows applications, windows, and desktop layouts.

## Features

- **Window Discovery** - Find and list app windows by title, process name, class name, or handle, without hidden cloaked, tool or shell windows; UWP apps are reported under their own process
- **Window Control** - Move, resize, minimize, maximize, snap, and close windows
- **Multi-Monitor Support** - Move windows between monitors, get display information
- **Screenshot Capture** - Capture screens, regions, or windows with optional UI element overlays
- **UI Automation** - Retrieve UI element trees with bounding boxes, and check which element has keyboard focus or sits at a screen point
- **Input Simulation** - Click, right-click, double-click, move the mouse cursor, type text and send key combinations
- **Browser Reading** - Read the URL and page text of browser windows through UI Automation or the Chrome DevTools Protocol
- **Application Launch** - Start applications with arguments, open URLs
- **Process Management** - List running processes, terminate processes safely
- **Layout Presets** - Save and restore window arrangements

## Installation

### Prerequisites

- Windows 10/11 (x64)
- [.NET 8 Desktop Runtime (x64)](https://dotnet.microsoft.com/download/dotnet/8.0) (the "Windows Desktop Runtime" download).
  The server exe is framework-dependent and uses WPF UI Automation, so the base .NET Runtime alone is not enough:
  without the Desktop Runtime the server exits at startup and no windows-app-manager tools appear.
  Check with `dotnet --list-runtimes`, which should list `Microsoft.WindowsDesktop.App 8.x`.

### Option 1: Claude Code Plugin (Recommended)

Install as a Claude Code plugin:

```bash
# Add the marketplace
/plugin marketplace add mortenbrudvik/WindowsAppManagerMcp

# Install the plugin
/plugin install windows-app-manager@windows-app-manager-marketplace
```

Then restart Claude Code. The plugin includes a skill with usage guidance.

The plugin runs the pre-built `bin/WindowsAppManagerMcp.exe` from this repository, so it needs the
.NET 8 Desktop Runtime too. That exe is rebuilt at each release and can lag behind `main` in between.

### Option 2: Claude Desktop (Manual)

Get `WindowsAppManagerMcp.exe` from a release (Option 4) or build it (Option 3), then add it to your
`claude_desktop_config.json` (located at `%APPDATA%\Claude\`):

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
dotnet publish src/WindowsAppManagerMcp -c Release -o ./publish
```

This produces `publish/WindowsAppManagerMcp.exe`, a framework-dependent single-file exe that needs the
.NET 8 Desktop Runtime. To bundle the runtime instead, add `--self-contained`; the output is much larger
(about 160 MB) and includes native WPF DLLs that must stay next to the exe. Do not publish into `./bin`:
that folder holds the committed plugin exe (see [Building](#building)).

### Option 4: Download Release

Download `WindowsAppManagerMcp-vX.Y.Z-win-x64.zip` from the [Releases](https://github.com/mortenbrudvik/WindowsAppManagerMcp/releases)
page, extract it, and point your MCP client at `WindowsAppManagerMcp.exe` (see Option 2). It is a
framework-dependent single-file exe and needs the .NET 8 Desktop Runtime. Releases are created by CI when a `v*` tag
is pushed; no release was published for the tags up to v1.11.0, so until the next tag use Option 3.

The release zip is built by CI from the tagged sources. It is not a copy of the plugin's committed
`bin/WindowsAppManagerMcp.exe`: both have the same version and sources, but they are separate builds,
so `serverInfo.version` shows a different commit SHA (`X.Y.Z+<tag commit>` for the release,
`X.Y.Z+<commit before the release commit>` for the plugin exe).

## Available Tools (44)

| Category | Tools |
|----------|-------|
| Window Discovery | `find_windows`, `get_all_windows`, `get_foreground_window` |
| Window Control | `move_window`, `resize_window`, `set_window_bounds`, `set_window_state`, `focus_window`, `close_window`, `snap_window`, `move_window_to_monitor` |
| Batch Operations | `set_windows_bounds_batch`, `snap_windows_batch`, `set_windows_state_batch`, `close_windows_batch`, `launch_applications_batch` |
| Monitor Info | `get_monitors`, `get_primary_monitor` |
| Screenshot | `list_screens`, `take_screenshot`, `capture_region`, `capture_window`, `capture_with_elements` |
| UI Automation | `get_ui_elements`, `get_focused_element`, `get_element_at_point` |
| Browser Reading | `get_browser_url`, `get_browser_content`, `get_active_browser_url`, `launch_browser_with_debug` |
| Input Simulation | `click`, `right_click`, `double_click`, `mouse_move`, `type_text`, `send_keys` |
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

### Screenshots & UI Automation
- "Take a screenshot of the primary monitor"
- "Capture the VS Code window"
- "Take a screenshot of monitor 0"
- "Capture a 800x600 region at position 100, 200"
- "Capture the Notepad window with UI element overlays"
- "Get all buttons and text inputs from the Settings window"
- "Which control has keyboard focus?" / "What is at position 640, 480?"

### Click & Keyboard Automation
- "Click at position 500, 300"
- "Right-click at 600, 400"
- "Double-click the file at position 200, 150"
- "Move the mouse to 800, 600"
- "Type 'Hello World' into the text field"
- "Press Ctrl+C to copy"
- "Press Enter to confirm"

> **Keyboard input goes to the foreground window.** `type_text` and `send_keys` have no target window. They type into whatever window has focus, and that can be the terminal running the agent. Agents should call `get_foreground_window` right before typing to confirm the target, and should close windows with `close_window`, never with Alt+F4. See the safety rules in [SKILL.md](skills/using-windows-app-manager/SKILL.md).

### Browser Reading
- "What page is open in Chrome?"
- "Read the text of the page open in Edge"
- "Launch Chrome with a debug port and read the full text of this JavaScript-heavy page"

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

The `config/layouts/` folder contains example presets. They are built into the server and added to your layouts folder the first time a layout tool runs:

- `work-setup.json` - Typical development workspace
- `dual-monitor.json` - Multi-monitor configuration
- `focus-mode.json` - Single centered window

Presets are stored in `%LOCALAPPDATA%\WindowsAppManagerMcp\layouts`, outside the plugin folder, so they survive plugin updates. To use another folder, set the `WAM_LAYOUTS_PATH` environment variable to an absolute path. On first run, presets saved by older versions (in a `layouts` folder next to the executable) are copied there and the originals are left in place. Existing presets are never overwritten, and a sample you delete is not added back.

## Building

```bash
# Build
dotnet build

# Run tests
dotnet test

# Publish the exe (framework-dependent single file, needs the .NET 8 Desktop Runtime)
dotnet publish src/WindowsAppManagerMcp -c Release -o ./publish
```

### Versioning and the plugin binary

- The version is defined once, as `<Version>` in `Directory.Build.props`. Change it with
  `./scripts/Set-Version.ps1 -Version X.Y.Z`, which also updates `.claude-plugin/plugin.json` and
  `.claude-plugin/marketplace.json` (tests fail if they drift). The server reports it, with the
  commit SHA, as `serverInfo.version` in the MCP handshake.
- The plugin runs the committed `bin/WindowsAppManagerMcp.exe`; it is the only binary in git.
  Refresh it only with `./scripts/Publish-Plugin.ps1` (the `/commit` release flow does this). The
  script refuses uncommitted changes to the build inputs and verifies the exe reports
  `<version>+<HEAD sha>`. The build inputs are `src/`, `config/` (the sample layout presets are
  embedded in the exe) and the MSBuild, NuGet and SDK files at the repository root; they are listed
  once, in `scripts/ReleaseCommon.ps1`. Ignored files count too, because MSBuild reads them (for
  example a `*.csproj.user`); only each project's `bin/` and `obj/` build output is left out.
  `-AllowDirty` builds anyway, but only into another folder (`-OutputDirectory`), never into `bin/`.
- `./scripts/Test-PluginBinary.ps1` checks that the committed exe has the current version and that
  no build input changed since the commit it was built from. The Build workflow runs it as an error
  on `v*` tags, so a release cannot ship a stale exe, and on pushes and pull requests that change
  `bin/WindowsAppManagerMcp.exe`, so a stale or hand-built exe fails when it is committed. On other
  pushes and pull requests it is only a warning. The tag's GitHub release gets
  `WindowsAppManagerMcp-vX.Y.Z-win-x64.zip`, which CI rebuilds from the tagged sources (a separate
  build from the committed exe, see [Option 4](#option-4-download-release)) and publishes with the
  GitHub CLI.
- `bin/` is gitignored, but `bin/WindowsAppManagerMcp.exe` is tracked, so staging it needs
  `git add -f -- bin/WindowsAppManagerMcp.exe`. The `/commit` command
  (`.claude/commands/commit.md`) does this.

## Testing

The project includes comprehensive tests:

```bash
# Run all tests (safe on your desktop: desktop tests are skipped by default)
dotnet test

# Run what CI runs: leave desktop tests out of the run entirely
dotnet test --filter "Category!=Desktop"

# Run with coverage
dotnet test --filter "Category!=Desktop" --collect:"XPlat Code Coverage"
```

- **1,300+ tests** covering unit and integration scenarios, about 60% line coverage
- By default a test run sends no keyboard or mouse input, launches or kills no
  processes, and does not move, focus or close your windows (some tests only read
  system state, such as the window, monitor and process lists)
- **Desktop tests** (`[DesktopFact]`/`[DesktopTheory]`, trait `Category=Desktop`) are for
  tests with real side effects, such as starting processes or sending input. They are skipped
  unless you opt in; CI runs them without the opt-in and fails unless every one is reported as
  skipped. Today the only ones are the gate's own canaries, which have no side effects. To opt in:

```powershell
$env:WAM_ALLOW_DESKTOP_TESTS = "1"
dotnet test --filter "Category=Desktop"
Remove-Item Env:WAM_ALLOW_DESKTOP_TESTS
```

See [TESTING.md](TESTING.md#desktop-tests) for details.

## Project Structure

```
WindowsAppManagerMcp/
├── .claude/commands/    # /commit (release) and /update-plugin (local install) commands
├── .claude-plugin/      # Claude Code plugin configuration
│   ├── plugin.json      # Plugin metadata and MCP server config
│   └── marketplace.json # Marketplace distribution manifest
├── skills/              # Claude Code skills
│   └── using-windows-app-manager/
│       └── SKILL.md     # Usage guidance and trigger patterns
├── bin/                 # Pre-built plugin executable (refreshed only by scripts/Publish-Plugin.ps1)
├── scripts/             # Release scripts: Set-Version, Publish-Plugin, Test-PluginBinary
├── Directory.Build.props # Product version (single source of truth)
├── src/WindowsAppManagerMcp/
│   ├── Infrastructure/  # MCP pipeline extensions (tool error reporting)
│   ├── Models/          # Data models and DTOs
│   ├── Native/          # P/Invoke declarations and native wrappers
│   ├── Services/        # Business logic
│   └── Tools/           # MCP tool definitions
├── tests/               # Unit and integration tests
├── config/layouts/      # Example layout presets
└── docs/                # Documentation
```

## Security

- **Launch Validation** - `launch_application` rejects path traversal, resolves bare program names in system folders, `PATH` and App Paths before the working directory can shadow them, and refuses arguments that `cmd.exe` would run as commands. It protects against data being turned into commands, not against an agent that runs `cmd` or PowerShell on purpose
- **Untrusted Content** - Web page text, window titles and UI element names come from other apps and websites and can contain prompt-injection text. The skill and [docs/TOOLS.md](docs/TOOLS.md#untrusted-content) tell agents to treat them as data, never as instructions
- **Protected Processes** - System-critical processes cannot be terminated
- **Confirmation Required** - Process termination requires explicit confirmation
- **Graceful Shutdown** - Windows are closed gracefully (WM_CLOSE)
- **Browser Debug Port** - `launch_browser_with_debug` opens a Chrome DevTools port on localhost with no authentication. Any local program can control that browser until it is closed. The browser uses a new, empty temporary profile unless `userDataDir` is given; never point it at your real profile.

## Requirements

- Windows 10 version 1607+ or Windows 11
- .NET 8 Desktop Runtime (x64), not just the base .NET Runtime (see [Prerequisites](#prerequisites))
- DPI awareness enabled for accurate positioning on high-DPI displays

## License

MIT License - see [LICENSE](LICENSE) for details.

## Contributing

Contributions are welcome! Please feel free to submit issues and pull requests.

## Acknowledgments

- Built with [Model Context Protocol SDK](https://github.com/modelcontextprotocol/csharp-sdk)
- Inspired by PowerToys FancyZones and Windows Snap Assist
