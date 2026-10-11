# Windows App Manager MCP Server

[![Build](https://github.com/mortenbrudvik/WindowsAppManagerMcp/actions/workflows/build.yml/badge.svg)](https://github.com/mortenbrudvik/WindowsAppManagerMcp/actions/workflows/build.yml)
[![Test](https://github.com/mortenbrudvik/WindowsAppManagerMcp/actions/workflows/test.yml/badge.svg)](https://github.com/mortenbrudvik/WindowsAppManagerMcp/actions/workflows/test.yml)
![Coverage](https://img.shields.io/badge/coverage-75%25-green)

An MCP (Model Context Protocol) server that enables AI assistants like Claude to manage Windows applications, windows, and desktop layouts.

## Features

- **Window Discovery** - Find and list app windows by title, process name, class name, or handle, without hidden cloaked, tool or shell windows; UWP apps are reported under their own process
- **Window Selectors** - Every tool that acts on a window can name it with a selector (`{"processName": "notepad"}`) instead of a handle; a selector that matches no window or several does nothing and says so, with the candidates
- **Window Control** - Move, resize, minimize, maximize, snap, and close windows, placing the visible frame and reporting the window as it is afterwards; closes are verified and report a "Save changes?" prompt instead of answering it
- **Multi-Monitor Support** - Move windows between monitors, get display information
- **Screenshot Capture** - Capture screens, regions, or windows with optional UI element overlays; captures come back as images the model sees, sized for vision, with an image-to-screen transform for clicking
- **UI Automation** - Retrieve UI element trees with bounding boxes, and check which element has keyboard focus or sits at a screen point
- **Input Simulation** - Click, right-click, double-click, move the mouse cursor, scroll, type text and send key combinations, aimed at a named window and kept off the agent's own terminal, elevated windows and denied apps
- **Clipboard** - Read what the agent copied and put text on the clipboard to paste it; the user's own clipboard is read, and pasted, only with the user's consent, and content password managers mark as private never
- **Browser Reading** - Read the URL and page text of browser windows through UI Automation or the Chrome DevTools Protocol
- **Application Launch** - Start applications with arguments, open URLs, Settings pages and Store apps, and get back the window the launch opened, whichever process owns it
- **Process Management** - List running processes, close a whole application gracefully, terminate processes safely
- **Layout Presets** - Save and restore window arrangements
- **Server Info and Instructions** - `get_server_info` reports the build, the DPI awareness, the desktop state, the agent's host windows, the pause and the toolsets in force, with warnings; every MCP client gets usage instructions in the handshake, with or without the plugin skill
- **Toolsets** - Serve only the tools you want (`WAM_TOOLSETS`, `WAM_EXCLUDE_TOOLS`), or only tools that read (`WAM_READ_ONLY=1`); see [Toolsets](docs/TOOLS.md#toolsets)
- **Audit Log** - Every input, close, kill, launch, layout-write (apply, save, delete) and clipboard call is recorded, without typed text or clipboard content, so you can see afterwards what was sent where; see [Audit log](docs/TOOLS.md#audit-log)

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
.NET 8 Desktop Runtime too. That exe is rebuilt at each release and can lag behind `main` in between:
the tools and safety behaviour described here are those of the source on `main`, and the plugin gets
them with the next release (its version is `serverInfo.version` in the MCP handshake).

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
is pushed; the first one is v1.12.0 (no release was published for earlier tags).

The release zip is built by CI from the tagged sources. It is not a copy of the plugin's committed
`bin/WindowsAppManagerMcp.exe`: both have the same version and sources, but they are separate builds,
so `serverInfo.version` shows a different commit SHA (`X.Y.Z+<tag commit>` for the release,
`X.Y.Z+<commit before the release commit>` for the plugin exe).

<!-- tools:begin tool-table (generated from tools/list: run the tests with UPDATE_SNAPSHOTS=1, see TESTING.md) -->
## Available Tools (57)

| Category | Tools |
|----------|-------|
| Window Discovery | `find_windows`, `get_all_windows`, `get_foreground_window`, `get_owned_windows`, `get_window_info`, `wait_for_window` |
| Window Control | `move_window`, `resize_window`, `set_window_bounds`, `set_window_state`, `focus_window`, `close_window`, `snap_window`, `move_window_to_monitor` |
| Batch Operations | `set_windows_bounds_batch`, `snap_windows_batch`, `set_windows_state_batch`, `close_windows_batch`, `launch_applications_batch` |
| Monitor Info | `get_monitors`, `get_primary_monitor` |
| Screenshot | `list_screens`, `take_screenshot`, `capture_region`, `capture_window`, `capture_with_elements` |
| UI Automation | `get_ui_elements`, `find_ui_elements`, `get_focused_element`, `get_element_at_point`, `get_active_popups` |
| Browser Reading | `get_browser_url`, `get_active_browser_url`, `get_browser_content`, `launch_browser_with_debug` |
| Input Simulation | `click`, `right_click`, `double_click`, `mouse_move`, `scroll`, `type_text`, `send_keys` |
| Input Control | `get_control_status`, `resume_input`, `release_all_input` |
| App Launch | `launch_application`, `list_processes`, `kill_process`, `close_application` |
| Clipboard | `get_clipboard`, `set_clipboard` |
| Layout Presets | `list_layouts`, `get_layout`, `save_layout`, `apply_layout`, `delete_layout` |
| Server Info | `get_server_info` |
<!-- tools:end tool-table -->

See [docs/TOOLS.md](docs/TOOLS.md) for complete documentation with examples.

## Configuration

The server reads these environment variables when it starts (for example in the `env` block of the MCP
server configuration). No tool argument can change them.

| Variable | Default | Purpose |
|----------|---------|---------|
| `WAM_PAUSE_HOTKEY` | `Ctrl+Alt+Shift+Pause` | The [pause hotkey](docs/TOOLS.md#pause-hotkey) chord, such as `Ctrl+Alt+F12`; `none` turns it off |
| `WAM_DENY_PROCESSES` | common password managers, `CredentialUIBroker`, `consent` | Apps the agent may not act on: no input, window changes, closing, killing or window captures (`TargetDeniedByPolicy`). A value replaces the default list; `default` puts it back in, `none` alone turns it off. See [Target policy](docs/TOOLS.md#target-policy) |
| `WAM_ALLOW_PROCESSES` | not set | When set, the only apps the agent may act on; the deny list still wins |
| `WAM_DESTRUCTIVE_CHORDS` | `%{F4} ^w ^q ^{F4} ^+{ESC} #l` | `send_keys` chords that need `windowHandle` (`DestructiveChordNeedsTarget`) |
| `WAM_LAYOUTS_PATH` | `%LOCALAPPDATA%\WindowsAppManagerMcp\layouts` | Folder of the [layout presets](docs/TOOLS.md#layout-presets) |
| `WAM_AUDIT_PATH` | `%LOCALAPPDATA%\WindowsAppManagerMcp\audit` | Folder of the [audit log](docs/TOOLS.md#audit-log): one JSON line per input, close, kill, launch or layout-write (apply, save, delete) call and per clipboard read or write, one file per day, kept 7 days |
| `WAM_TOOLSETS` | the default toolsets (today: all) | The [toolsets](docs/TOOLS.md#toolsets) to serve, such as `windows,monitors,layouts`; `all`, `default`, `+name` and `-name` (`-browser`) also work. The `server` toolset is always served. A name that is not a toolset never serves more than you asked for: an unknown `-name`, or unknown plain names with no valid one, serve only the `server` toolset (an unknown `+name` is just ignored) |
| `WAM_EXCLUDE_TOOLS` | not set | Tools to leave out by name, such as `kill_process`, or every tool of a toolset by the toolset's name (`process`). `get_server_info` is always served. A name that is neither a tool nor a toolset serves only the `server` toolset, so a typo never leaves on a tool you meant to turn off |
| `WAM_READ_ONLY` | not set | `1` serves only the tools annotated read-only (an observer profile: no input, window changes, captures, layout writes, clipboard writes or process control) |
| `WAM_ALLOWED_PROTOCOLS` | not set | More URI schemes `launch_application` may open, such as `vscode,ms-teams,spotify` (built in: `http`, `https`, `mailto`, `tel`, `ms-settings` and `shell:AppsFolder\<AppUserModelID>`). Listing a scheme trusts its app with whatever the agent puts in the URI. `javascript`, `vbscript`, `data`, `mshta`, the shells, `shell:`, `ms-appinstaller`, `ms-officecmd` and the help schemes stay blocked. See [launch_application](docs/TOOLS.md#launch_application) |
| `WAM_ALLOW_USER_CLIPBOARD` | not set (off) | `1` lets `get_clipboard` return what you copied, without asking you each time. Off, it returns only what the agent copied or set, and asks you (when the client can) before reading your clipboard; content password managers mark as private is never returned. See [Clipboard](docs/TOOLS.md#clipboard) |
| `WAM_UIA_ENGINE` | `uia3` | The UI Automation engine of the element tools: `uia3`, the COM client with per-call timeouts, or `uia2`, the managed client of earlier versions (1.14.0 and before), as a fallback if an app reads wrongly with `uia3`. See [UI Automation](docs/TOOLS.md#ui-automation) |

The server logs the deny list, the allow list, the chords, the toolsets, the clipboard setting and the URI schemes in
force to stderr when it starts, with a warning for every entry it ignored; `get_server_info` reports them too. The test opt-ins `WAM_ALLOW_DESKTOP_TESTS` and
`WAM_ALLOW_DESKTOP_NOINPUT_TESTS` are described under [Testing](#testing).

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
- "Scroll the Settings list down until Bluetooth is visible"
- "Copy the error lines in the terminal and read them"

> **Input is guarded.** Every input tool takes an optional `windowHandle`: keys are sent only after the server has brought that window to the front and only while it (or its dialog) is in front, and clicks only when the point is on that window or a popup of it. Without `windowHandle`, input goes to the foreground window or the window at the point, but never to the agent's own terminal, editor or chat app (`TargetIsAgentHost`), to windows of apps running as administrator (`TargetElevated`) or to apps the user denies through `WAM_DENY_PROCESSES` (`TargetDeniedByPolicy`, password managers by default). Chords that close, quit or lock, such as Alt+F4, need `windowHandle`, and a chord that switches windows, such as Alt+Tab, must be the last key of a `send_keys` call. A paste (Ctrl+V, Shift+Insert, a right click in a terminal) is sent only when the clipboard holds what the agent put there or copied, or what you let it read (`PasteNotAllowed`). A click is pressed exactly at the point the guard checked. Every result reports `receivingWindow` and `foregroundAfter`. Close windows with `close_window`, never with Alt+F4. See [Input guard](docs/TOOLS.md#input-guard) and the safety rules in [SKILL.md](skills/using-windows-app-manager/SKILL.md).

> **Pause hotkey.** Press **Ctrl+Alt+Shift+Pause** at any time to pause the input of every Windows App Manager server on your desktop (each MCP client session runs its own server, and they share the pause): running input stops, keys the servers hold are released, and every input tool and every tool that changes windows or starts or stops processes returns `PausedByUser` until you press it again (or confirm an agent's `resume_input` request, which resumes them all). A second press within 2 seconds of the pause is ignored. The agent cannot resume itself. Set another chord with the `WAM_PAUSE_HOTKEY` environment variable (for example `Ctrl+Alt+F12`, or `none` to turn it off). See [Pause hotkey](docs/TOOLS.md#pause-hotkey).

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

- **About 4,700 tests**: about 4,500 unit, contract and integration tests and about 180 end-to-end harness tests, about 75% line coverage
- **Contract tests** call every tool over MCP through an in-process server, hold each typical result to a
  token budget, and compare the tools/list the server sends with a checked-in snapshot
  (`tests/WindowsAppManagerMcp.Tests/Contract/tools.snapshot.json`). The tool tables in this README,
  docs/TOOLS.md and the plugin skill are generated from it. After a deliberate change to the tools, run the
  tests once with `UPDATE_SNAPSHOTS=1` to regenerate both, and review the diff
  (see [TESTING.md](TESTING.md#tool-surface-snapshot))
- By default a test run sends no keyboard or mouse input, launches or kills no
  processes, and does not move, focus or close your windows (some tests only read
  system state, such as the window, monitor and process lists)
- **Desktop tests** (`[DesktopFact]`/`[DesktopTheory]`, trait `Category=Desktop`) are for
  tests with real side effects, such as starting processes or sending input. They are skipped
  unless you opt in; CI runs them without the opt-in and fails unless every one is reported as
  skipped. A narrower tier, `[DesktopNoInputFact]` (also `Category=DesktopNoInput`), only starts
  the suite's own fixture app and reads or captures it; `WAM_ALLOW_DESKTOP_NOINPUT_TESTS=1` opts in
  to that tier alone.
- **End-to-end tests** (`tests/WindowsAppManagerMcp.E2ETests`) drive the real server services
  against `tests/FixtureApp`, a small WPF app the suite owns, never against your own windows. They
  are desktop tests of both tiers; on GitHub they run only on demand (`.github/workflows/e2e.yml`).
  To opt in:

```powershell
# Only the no-input tier: start the fixture app, read and capture it
$env:WAM_ALLOW_DESKTOP_NOINPUT_TESTS = "1"
dotnet test --filter "Category=DesktopNoInput"
Remove-Item Env:WAM_ALLOW_DESKTOP_NOINPUT_TESTS

# Both tiers: also bring the fixture app to the front and type into it
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
├── tests/               # Unit and integration tests, plus end-to-end tests against tests/FixtureApp
├── config/layouts/      # Example layout presets
└── docs/                # Documentation
```

## Security

- **Launch Validation** - `launch_application` rejects path traversal, resolves bare program names in system folders, `PATH` and App Paths before the working directory can shadow them, and refuses arguments that `cmd.exe` would run as commands. It protects against data being turned into commands, not against an agent that runs `cmd` or PowerShell on purpose
- **Untrusted Content** - Web page text, window titles and UI element names come from other apps and websites and can contain prompt-injection text. The skill and [docs/TOOLS.md](docs/TOOLS.md#untrusted-content) tell agents to treat them as data, never as instructions
- **Input Guard** - Every keyboard and mouse batch is checked first. Input never reaches the agent's own terminal, editor or chat app (unless the call names it as `windowHandle` with `allowHostTarget`, which agents are told to pass only when the user asked), windows of elevated or denied apps, a locked session or a UAC prompt. With `windowHandle`, keys go only to that window (or its dialog) and stop when another window comes to the front, and clicks go only to a point on that window. See [Input guard](docs/TOOLS.md#input-guard)
- **Target Policy** - The user denies apps to the agent with `WAM_DENY_PROCESSES` (password managers and credential prompts by default) or allows only some with `WAM_ALLOW_PROCESSES`; no tool argument overrides it (see [Configuration](#configuration))
- **Protected Processes** - System-critical processes and the agent's own host (its terminal or editor and that terminal's console processes) cannot be terminated; a forced kill checks the whole process tree first
- **Confirmation Required** - Process termination requires explicit confirmation: when your MCP client supports elicitation, the server asks you directly (process name, PID, windows, and for a forced kill the child processes) and does nothing unless you confirm; otherwise the agent must pass `confirm: true`. A process that took the PID while you read the question is never touched
- **Graceful Shutdown** - Windows are closed gracefully (WM_CLOSE)
- **Pause Hotkey** - Ctrl+Alt+Shift+Pause (`WAM_PAUSE_HOTKEY`) pauses and resumes the input and window changes of every server on the desktop; only the user can resume, and no key or mouse button the server presses stays down after a call, an interrupted batch or a shutdown
- **Protected Windows** - `close_window`, `close_windows_batch` and `close_application` refuse the desktop shell (Program Manager, the taskbars) and the terminal, editor or chat app the agent runs in (the window tools unless `force` is true, which the server confirms with you first when the client can ask, and which otherwise needs `confirm` too; `close_application` always), and closing more than 3 windows at once requires your confirmation (asked through the client when it supports elicitation, otherwise `confirm`). Closes are verified: the result says whether the window closed or an app is waiting behind a "Save changes?" prompt, which the agent never answers on its own
- **Clipboard Privacy** - `get_clipboard` returns only what the agent put on the clipboard or copied itself; your own clipboard only after you allow it (asked through the client, or `WAM_ALLOW_USER_CLIPBOARD`), and content password managers mark as private never. Input that pastes follows the same rule (`PasteNotAllowed`), so a paste cannot put your clipboard where the agent can read it. Menu paste, drag and drop and custom paste shortcuts are not covered (see [Clipboard](docs/TOOLS.md#clipboard))
- **Audit Log** - Every input, close, kill, launch, layout-write (apply, save, delete) and clipboard-write call, and every clipboard read, is appended to `%LOCALAPPDATA%\WindowsAppManagerMcp\audit\<date>.jsonl` (`WAM_AUDIT_PATH`) with its target window, the window the input went to, its outcome and error code. Typed text, keys, clipboard text, command-line arguments and the paths, queries and credentials of URLs are never logged, only their lengths; a launched URL keeps only its scheme and host. Files are kept 7 days, and the log cannot be turned off (see [Audit log](docs/TOOLS.md#audit-log))
- **Hung Applications** - The window tools give up on an application that does not answer within 5 seconds (`NotResponding`), so a hung app cannot block the other tools
- **Read-Only Mode** - `WAM_READ_ONLY=1` serves only the tools annotated read-only, and `WAM_TOOLSETS`/`WAM_EXCLUDE_TOOLS` leave out whole groups or single tools; a tool that is not served cannot be called (see [Toolsets](docs/TOOLS.md#toolsets))
- **Browser Debug Port** - `launch_browser_with_debug` opens a Chrome DevTools port on localhost with no authentication. Any local program can control that browser until it is closed. The browser uses a new, empty temporary profile unless `userDataDir` is given; never point it at your real profile (a folder inside a browser's `User Data` folder is refused, and so is a `url` that would be read as a command-line switch).

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
